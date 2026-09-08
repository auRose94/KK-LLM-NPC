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

        // Serialize anything we pass to/from the LLM: dictionaries, lists, anonymous
        // types (via reflection on public properties), primitives. NaN/Infinity → null.
        public static string Write(object v)
        {
            var sb = new StringBuilder();
            WriteVal(sb, v);
            return sb.ToString();
        }
        private static void WriteVal(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string s) { WriteStr(sb, s); return; }
            if (v is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (v is float || v is double || v is decimal)
            {
                double d = Convert.ToDouble(v);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
            }
            if (v is byte || v is short || v is int || v is long) { sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return; }
            if (v is IDictionary<string, object> dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict) { if (!first) sb.Append(','); first = false; WriteStr(sb, kv.Key); sb.Append(':'); WriteVal(sb, kv.Value); }
                sb.Append('}'); return;
            }
            if (v is IEnumerable en)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in en) { if (!first) sb.Append(','); first = false; WriteVal(sb, item); }
                sb.Append(']'); return;
            }
            // Anonymous/POCO types: serialize public instance properties as an object.
            var t = v.GetType();
            if (t.IsClass && !t.FullName.StartsWith("System.", StringComparison.Ordinal))
            {
                sb.Append('{');
                bool first = true;
                foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                    object pv;
                    try { pv = prop.GetValue(v, null); } catch (Exception) { continue; }
                    if (!first) sb.Append(','); first = false;
                    WriteStr(sb, prop.Name); sb.Append(':'); WriteVal(sb, pv);
                }
                sb.Append('}'); return;
            }
            WriteStr(sb, v.ToString());
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
                    sb.Append(e == 'n' ? '\n' : e == 'r' ? '\r' : e == 't' ? '\t' : e == '"' ? '"' : e == '\\' ? '\\' : e == '/' ? '/' : e);
                }
                else if (c != '\\') sb.Append(c);
            }
            return sb.ToString();
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
