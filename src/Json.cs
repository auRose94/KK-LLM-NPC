// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Hand-rolled JSON write/parse (no external deps), and JsonObj accessors for LLM arguments.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
// NOTE: this file deliberately has no Unity/BepInEx/Photon usings — it stays
// dependency-free so tests/ can compile it standalone (see tests/README.md).

namespace KKLLMNPC
{

    // (NavMesh / wander suppression is done via KoboldSeeker + KoboldAIPossession.)

    // ----------------------------------------------------------------------
    // minimal JSON (no external deps), enough for the payloads we exchange.
    // ----------------------------------------------------------------------
    internal static class Json
    {
        // Parse-error reporting sink (set by the plugin at startup). Null in standalone tests.
        public static System.Action<string> ErrorLog;

        // Serialize anything we pass to/from the LLM: dictionaries (any key/value
        // types — Dictionary<string,string>, Hashtable, …), lists/arrays, KeyValuePair,
        // anonymous/POCO/struct types (public fields + properties via reflection),
        // primitives. NaN/Infinity → null. Depth-capped so cyclic shapes terminate.
        // This is the ONE choke point every tool result flows through on the way to
        // the model — before the IDictionary/KeyValuePair handling existed, any dict
        // whose value type wasn't object serialized each entry as a KeyValuePair
        // struct via ToString() ("[k, System.Collections.Generic.List`1[...]]"),
        // which is the type-name garbage the agent saw instead of JSON (2026-11-04).
        private const int MaxWriteDepth = 32;
        public static string Write(object v)
        {
            var sb = new StringBuilder();
            WriteVal(sb, v, 0);
            return sb.ToString();
        }
        private static void WriteVal(StringBuilder sb, object v, int depth)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string s) { WriteStr(sb, s); return; }
            if (v is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (v is char c) { WriteStr(sb, c.ToString()); return; }
            if (v is float || v is double || v is decimal)
            {
                double d = Convert.ToDouble(v);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
            }
            if (v is byte || v is sbyte || v is short || v is ushort || v is int || v is uint || v is long || v is ulong)
            { sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return; }
            if (v is Enum) { WriteStr(sb, v.ToString()); return; }
            if (v is DateTime dt) { WriteStr(sb, dt.ToString("o", CultureInfo.InvariantCulture)); return; }
            // Depth cap: cyclic dict/list shapes (or a pathological object graph)
            // must terminate; emit an ellipsis marker rather than a stack overflow.
            if (depth > MaxWriteDepth) { WriteStr(sb, "…"); return; }

            // Maps: any IDictionary. Checked BEFORE IEnumerable because dictionaries
            // are enumerable too — the non-generic interface covers Dictionary<K,V>
            // of every value type (generic variance doesn't apply, so the old
            // IDictionary<string,object> test missed Dictionary<string,string> etc.)
            // plus Hashtable/SortedList. Keys are stringified (JSON object keys are
            // strings); values recurse.
            if (v is IDictionary map)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in map)
                {
                    if (!first) sb.Append(','); first = false;
                    WriteStr(sb, e.Key is string ks ? ks
                        : e.Key == null ? "" : Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteVal(sb, e.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }
            var t = v.GetType();
            // KeyValuePair<K,V> as a standalone value (e.g. inside an array): the
            // pair form {"key":…,"value":…}. Inside an IDictionary loop it never
            // reaches here (entries are split above). Fields, not properties —
            // Mono's KeyValuePair<K,V> stores key/value as private fields.
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                var kf = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (kf.Length == 0) { WriteStr(sb, v.ToString()); return; } // unexpected runtime — string fallback
                sb.Append('{');
                bool first = true;
                foreach (var f in kf)
                {
                    if (f.IsStatic) continue;
                    if (!first) sb.Append(','); first = false;
                    WriteStr(sb, f.Name); sb.Append(':');
                    try { WriteVal(sb, f.GetValue(v), depth + 1); } catch (Exception) { sb.Append("null"); }
                }
                sb.Append('}');
                return;
            }
            if (v is IEnumerable en)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in en) { if (!first) sb.Append(','); first = false; WriteVal(sb, item, depth + 1); }
                sb.Append(']');
                return;
            }
            // Anonymous/POCO/struct types: public instance properties AND fields as
            // an object (fields included so Unity structs like Vector3 carry x/y/z;
            // anonymous types only have properties). System.* types keep the old
            // ToString() stringification — Guid/TimeSpan read better as strings than
            // a reflected property dump.
            if (!t.FullName.StartsWith("System.", StringComparison.Ordinal))
            {
                sb.Append('{');
                bool first = true;
                var seen = new HashSet<string>();
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.IsStatic || !seen.Add(f.Name)) continue;
                    object fv;
                    try { fv = f.GetValue(v); } catch (Exception) { continue; }
                    if (!first) sb.Append(','); first = false;
                    WriteStr(sb, f.Name); sb.Append(':'); WriteVal(sb, fv, depth + 1);
                }
                foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanRead || prop.GetIndexParameters().Length > 0 || !seen.Add(prop.Name)) continue;
                    object pv;
                    try { pv = prop.GetValue(v, null); } catch (Exception) { continue; }
                    if (!first) sb.Append(','); first = false;
                    WriteStr(sb, prop.Name); sb.Append(':'); WriteVal(sb, pv, depth + 1);
                }
                sb.Append('}');
                return;
            }
            WriteStr(sb, v.ToString());
        }
        // Single-value textualisation for LLM/log-visible strings — never a .NET
        // type name. Collections render as compact JSON; everything else falls
        // through to ToString(). For the console result formatter and tool-log
        // briefs where a dict/list VALUE used to hit ToString()
        // ("System.Collections.Generic.List`1[...]") instead of its data.
        public static string Text(object v)
        {
            if (v == null) return "";
            if (v is string s) return s;
            if (v is IDictionary || v is IEnumerable)
            {
                try { return Write(v); } catch (Exception) { }
            }
            return v.ToString();
        }

        private static void WriteStr(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                // Lone surrogates (illegal UTF-16) crash Mono's string-to-native
                // conversion with "Illegal byte sequence in the input"; LLM output can
                // occasionally contain them, so scrub here so no poisoned string ever
                // leaves as JSON. Valid surrogate pairs (emoji etc.) pass through.
                if (char.IsHighSurrogate(c) || char.IsLowSurrogate(c))
                {
                    if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        sb.Append(c).Append(s[i + 1]); i++;
                        continue;
                    }
                    c = '?';
                }
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            }
            sb.Append('"');
        }

        // Minimal recursive-descent parser returning Dictionary/List/primitives.
        // Enhanced with parse position tracking for better error reporting.
        private const int MaxDepth = 64;
        // Recover whatever the model sent. Tolerant of markdown fences, truncation,
        // truncated strings, doubled-encoded JSON inside JSON. Returns null on garbage.
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int i = 0, depth = 0;
            try
            {
                return ParseValue(text, ref i, ref depth);
            }
            catch (Exception e)
            {
                // Report parse position so we know where the model went wrong.
                int line = 1, col = i;
                for (int j = 0; j < i && j < text.Length; j++)
                {
                    if (text[j] == '\n') { line++; col = 1; }
                    else col++;
                }
                // Pluggable error sink (wired to the plugin logger in Main.cs) so this file
                // stays compilable standalone for tests — null outside the plugin.
                if (ErrorLog != null)
                    try { ErrorLog($"Json.Parse error at line {line}, col {col}: {e.Message} (char '{(i < text.Length ? text[i] : 'E')}')"); } catch (Exception) { }
                return null;
            }
        }
        private static void SkipWs(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
        private static object ParseValue(string s, ref int i, ref int depth)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            if (depth > MaxDepth) { throw new InvalidDataException("json too deep"); }
            char c = s[i];
            if (c == '{')
            {
                depth++;
                var d = new Dictionary<string, object>();
                i++; SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; depth--; return d; }
                while (i < s.Length)
                {
                    SkipWs(s, ref i); if (i >= s.Length) break;
                    string key = ParseStr(s, ref i); SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ':') i++;
                    d[key] = ParseValue(s, ref i, ref depth); SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; break; }
                    break;
                }
                depth--;
                return d;
            }
            if (c == '[')
            {
                depth++;
                var l = new List<object>(); i++; SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; depth--; return l; }
                while (i < s.Length)
                {
                    l.Add(ParseValue(s, ref i, ref depth)); SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; break; }
                    break;
                }
                depth--;
                return l;
            }
            if (c == '"') return ParseStr(s, ref i);
            if (c == 't') { i += 4; return true; }
            if (c == 'f') { i += 5; return false; }
            if (c == 'n') { i += 4; return null; }
            return ParseNum(s, ref i);
        }
        private static string ParseStr(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'u': AppendUnicodeEscape(s, ref i, sb); break;
                        default: sb.Append(e); break;
                    }
                }
                else if (c != '\\') sb.Append(c);
            }
            return sb.ToString();
        }

        // \uXXXX — Go-based OpenAI-compatible servers (Ollama, OpenAI) HTML-escape
        // <, >, & as \u003c/\u003e/\u0026 in every response string, and WriteStr
        // itself emits \u00xx for control chars; without this decode those arrived
        // as literal "u003c" text (the field bug: a name reply "Name: <the name>"
        // echoed by the model was registered as the NAME "u003cthe", and inline
        // "<think>" tags never matched StripThinkBlocks). Malformed escapes stay
        // literal — whatever hex was read is echoed back, matching the lenient
        // parse convention of the rest of this parser.
        private static void AppendUnicodeEscape(string s, ref int i, StringBuilder sb)
        {
            int cp = 0, n = 0;
            while (n < 4 && i < s.Length)
            {
                int d = HexDigit(s[i]);
                if (d < 0) break;
                cp = (cp << 4) | d;
                i++; n++;
            }
            if (n < 4)
            {
                sb.Append('u');
                if (n > 0) sb.Append(s.Substring(i - n, n)); // keep consumed hex literal
                return;
            }
            char ch = (char)cp;
            sb.Append(ch);
            // High surrogate followed by a \uDC00-\uDFFF escape: consume the pair
            // so astral characters (emoji) don't strand a lone high surrogate.
            if (ch >= 0xD800 && ch <= 0xDBFF && i + 1 < s.Length && s[i] == '\\' && s[i + 1] == 'u')
            {
                int j = i + 2, lo = 0, m = 0;
                while (m < 4 && j < s.Length)
                {
                    int d = HexDigit(s[j]);
                    if (d < 0) break;
                    lo = (lo << 4) | d;
                    j++; m++;
                }
                if (m == 4 && lo >= 0xDC00 && lo <= 0xDFFF)
                {
                    i = j;
                    sb.Append((char)lo);
                }
            }
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
        private static object ParseNum(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "-+.eE0123456789".IndexOf(s[i]) >= 0) i++;
            if (i == start) { i++; return null; } // not a number: skip to avoid infinite loop
            string num = s.Substring(start, i - start);
            if (num.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0) { double d; if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d; }
            long l; if (long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            return num;
        }
    }

    // ----------------------------------------------------------------------
    // accessor over a JSON object string (the LLM tool arguments).
    // ----------------------------------------------------------------------
    internal class JsonObj
    {
        private readonly Dictionary<string, object> _d;
        public Dictionary<string, object> Dict => _d;
        public JsonObj(string json)
        {
            _d = Json.Parse(json) as Dictionary<string, object> ?? new Dictionary<string, object>();
        }
        public JsonObj(Dictionary<string, object> d)
        {
            _d = d ?? new Dictionary<string, object>();
        }
        public float F(string k, float def = 0f)
        {
            if (!_d.TryGetValue(k, out var v) || v == null) return def;
            if (v is double db) return (float)db;
            if (v is long l) return l;
            if (v is int i) return i;
            float f; return float.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : def;
        }
        public bool B(string k, bool def = false)
        {
            if (!_d.TryGetValue(k, out var v) || v == null) return def;
            if (v is bool b) return b;
            bool r; return bool.TryParse(v.ToString(), out r) ? r : def;
        }
        public string S(string k, string def = "")
        {
            if (!_d.TryGetValue(k, out var v) || v == null) return def;
            return v.ToString();
        }
        public bool Has(string k)
        {
            return _d.ContainsKey(k) && _d[k] != null;
        }
        public double DB(string k, double def = 0)
        {
            if (!_d.TryGetValue(k, out var v) || v == null) return def;
            if (v is double dd) return dd;
            if (v is long l) return l;
            if (v is int i) return i;
            double r; return double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : def;
        }
        public List<JsonObj> A(string k)
        {
            var outp = new List<JsonObj>();
            if (!_d.TryGetValue(k, out var v) || v == null) return outp;
            // Accept a real array, or a single object (models often emit one).
            if (v is List<object> list)
            {
                foreach (var item in list)
                {
                    if (item is Dictionary<string, object> dd) outp.Add(new JsonObj(dd));
                    else if (item is string ss) outp.Add(new JsonObj(ss));
                }
            }
            else if (v is Dictionary<string, object> single) outp.Add(new JsonObj(single));
            else if (v is string sj) { try { outp.Add(new JsonObj(sj)); } catch (Exception) { } }
            return outp;
        }
    }
}
