// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// SpeechClean — the JSON-in-speech hygiene, pure C# (unit-tested, adversarially).
//
// Reasoning models answer chat turns with machinery: act-shaped JSON, a
// {"say": "..."} echo, roleplay quoted AS JSON ({"roleplay": {"line": …}}),
// fenced fragments, schema-narration lines. None of it may reach the
// player's chat. The 2026-11-21 hole (Rose's field report — "quoting
// roleplay as json formatted strings that get sent to the say tool"): the
// old cleaner extracted a voice field and returned it UNCLEANED, so a JSON
// blob nested inside the say parameter sailed straight to chat, and nested
// objects dodged the flat-fragment regex (its [^{}]* cannot cross braces).
//
// The contract this module pins (all adversarially tested):
//   * a voice field's content is cleaned RECURSIVELY — JSON wrapped in JSON
//     unwraps down to the spoken words, however deep the model nests it;
//   * a voice field with no speakable content refuses ("" — the caller feeds
//     the model a format note instead of speaking machinery);
//   * JSON fragments in mid-text prose are stripped by BRACE-MATCHING scan
//     (nested objects included), with a quoted-key+colon look test so RP
//     braces ("{waves}", "{{user}}") survive;
//   * the INVARIANT: the result is never a parseable JSON document — object
//     OR array. If everything strips away, the say is refused, never spoken.
//
// Build: mcs -target:exe -out:/tmp/t_sc.exe tests/test_speechclean.cs src/SpeechClean.cs src/Json.cs && mono /tmp/t_sc.exe
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KKLLMNPC
{
    public static class SpeechClean
    {
        // Voice-field keys the unwrapper looks for, in priority order. "roleplay"
        // and "rp" joined after the field report (models quoting roleplay as JSON
        // tended to name the field exactly that).
        public static readonly string[] VoiceKeys =
            { "say", "text", "message", "msg", "dialogue", "line", "response", "roleplay", "rp", "dialog" };

        private static readonly string[] MachineryKeys = { "\"progress\"", "\"action\":", "\"thought\"", "\"why\":" };

        // Reasoning models narrate their own output format — none of that is speech.
        public static bool IsMachineryLine(string line)
        {
            string l = line.Trim();
            if (l.Length == 0) return true;
            string low = l.ToLowerInvariant();
            foreach (var k in MachineryKeys) if (low.Contains(k)) return true;
            if (low.Contains("json") || low.Contains("schema") || low.Contains("action object")) return true;
            if (low.StartsWith("we need to output") || low.StartsWith("we can combine") || low.StartsWith("so we need")
                || low.StartsWith("let me output") || low.StartsWith("the schema")) return true;
            return false;
        }

        public static string CleanSpokenJson(string text)
        {
            return Clean(text, 0);
        }

        private const int MaxUnwrapDepth = 4;

        private static string Clean(string text, int depth)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string t = text.Trim();
            // Strip one markdown-fence wrapper.
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                int firstNl = t.IndexOf('\n');
                if (firstNl > 0) t = t.Substring(firstNl + 1);
                int lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0) t = t.Substring(0, lastFence);
                t = t.Trim();
            }
            // A QUOTED string ("{\"a\":1}" or "\"*waves* hi\"") — unwrap the
            // quotes and re-run: models quote the whole payload as a string.
            if (t.Length > 1 && t[0] == '"' && t[t.Length - 1] == '"')
            {
                string inner = t.Substring(1, t.Length - 2).Trim();
                if (inner.Length > 0 && depth < MaxUnwrapDepth)
                {
                    string re = Clean(inner, depth + 1);
                    if (re.Length > 0 || inner.StartsWith("{", StringComparison.Ordinal)) return re;
                }
                t = inner; // plain quoted speech falls through to fragment cleaning
            }
            // Whole reply is (probably) one JSON object: speak its voice field —
            // CLEANED (the field may itself be JSON-wrapped roleplay — the field
            // report's exact hole) — recursing until words or refusal. Voice
            // fields that are JSON OBJECTS (not strings) are re-serialized and
            // recursed into the same way. An act object without a voice field
            // says nothing.
            if (t.Length > 1 && t[0] == '{' && t[t.Length - 1] == '}')
            {
                var parsed = Json.Parse(t) as Dictionary<string, object>;
                if (parsed != null)
                {
                    foreach (var key in VoiceKeys)
                    {
                        if (!parsed.TryGetValue(key, out var v) || v == null) continue;
                        string s = v as string;
                        if (s == null)
                        {
                            try { s = Json.Write(v); } // object/array voice field → same treatment
                            catch (Exception) { continue; }
                        }
                        if (s.Trim().Length == 0) continue;
                        if (depth < MaxUnwrapDepth)
                        {
                            string inner = Clean(s, depth + 1);
                            if (inner.Length > 0) return inner;
                            continue; // this field was machinery — try the next voice key
                        }
                        return s.Trim().Trim('"'); // depth cap: speak the leaf as-is
                    }
                    // No voice key matched — but a DOUBLE-ESCAPED payload parses
                    // into GARBAGE KEYS ('' → null) in our lenient parser, which
                    // is the unmistakable signature. Retry unescaped once,
                    // depth-guarded. A cleanly-parsed payload with no voice key
                    // (pure data) refuses here — that refusal is correct.
                    if (parsed.ContainsKey("") )
                    {
                        var re2 = RetryIfDoubleEscaped(t, depth);
                        if (re2 != null) return re2;
                    }
                    return "";
                }
                // Not parseable as JSON — possibly double-escaped; retry once.
                {
                    var re2 = RetryIfDoubleEscaped(t, depth);
                    if (re2 != null) return re2;
                }
                // fall through to fragment stripping
            }
            // Mid-text JSON fragments — brace-MATCHED scan so NESTED objects are
            // caught too (the old flat regex's [^{}]* couldn't cross a brace).
            // A span is machinery when it carries a quoted key followed by a
            // colon; prose braces ("{waves}") and RP placeholders ("{{user}}")
            // never match that look and survive. Runs BEFORE the line filter so
            // prose sharing a line with a JSON fragment survives.
            t = StripJsonSpans(t);
            // Machinery-line filter: dropped per line so a mixed block keeps its
            // speakable lines; a kobold saying "I need to find food" still passes
            // (the patterns require output/combine verbs or quoted JSON keys).
            if (t.IndexOf('\n') >= 0 || IsMachineryLine(t))
            {
                var kept = new List<string>();
                foreach (var ln in t.Split('\n'))
                {
                    string l2 = ln.Trim();
                    if (l2.Length == 0 || IsMachineryLine(l2)) continue;
                    kept.Add(l2);
                }
                t = string.Join(" ", kept.ToArray()).Trim();
                if (t.Length == 0) return "";
            }
            t = Regex.Replace(t, "\\s+", " ").Trim();
            // THE INVARIANT: never speak a parseable JSON document. Anything that
            // still is one gets refused entirely (the caller feeds a format note).
            if (t.Length > 1 && ((t[0] == '{' && t[t.Length - 1] == '}') || (t[0] == '[' && t[t.Length - 1] == ']')))
            {
                if (Json.Parse(t) is Dictionary<string, object>
                    || Json.Parse(t) is List<object>)
                    return "";
            }
            return t.Trim('"').Trim();
        }

        // One conservative unescape-and-retry for DOUBLE-ESCAPED payloads (the
        // model wrote {\"say\": …} as literal text). Returns null when there's
        // nothing to retry or the retry refused — the caller then refuses too.
        private static string RetryIfDoubleEscaped(string t, int depth)
        {
            if (string.IsNullOrEmpty(t) || !t.Contains("\\\"") || depth >= MaxUnwrapDepth) return null;
            string unescaped = t.Replace("\\\"", "\"").Replace("\\\\", "\\");
            string re = Clean(unescaped, depth + 1);
            return re.Length > 0 ? re : null;
        }

        // Replace every brace-matched {...} span that looks like JSON machinery
        // with a space. Depth-counted matching, quote-blind (good enough: a
        // quoted '}' inside machinery is rare and the worst case is leftovers
        // that the final invariant refuses).
        private static string StripJsonSpans(string t)
        {
            if (string.IsNullOrEmpty(t) || t.IndexOf('{') < 0) return t;
            var sb = new System.Text.StringBuilder();
            int i = 0;
            while (i < t.Length)
            {
                if (t[i] != '{')
                {
                    sb.Append(t[i]);
                    i++;
                    continue;
                }
                int depth = 0;
                int j = i;
                for (; j < t.Length; j++)
                {
                    if (t[j] == '{') depth++;
                    else if (t[j] == '}')
                    {
                        depth--;
                        if (depth == 0) break;
                    }
                }
                if (j >= t.Length)
                {
                    sb.Append(t.Substring(i)); // unbalanced — keep the rest verbatim
                    break;
                }
                string span = t.Substring(i, j - i + 1);
                bool machinery = span.Contains("\":") || span.Contains("\": ")
                    || span.StartsWith("{\"") || span.StartsWith("{ \"");
                if (machinery) sb.Append(' ');
                else sb.Append(span); // RP braces / placeholders survive
                i = j + 1;
            }
            return sb.ToString();
        }
    }
}
