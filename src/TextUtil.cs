// TextUtil — small shared string helpers that several perception/movement files
// used to re-implement inline. Keep this file Unity-free where possible so it
// can be reused from test suites.
using System;
using System.Text;

namespace KKLLMNPC
{
    internal static class TextUtil
    {
        // Keep printable ASCII only. Game strings (entity names, owner nicknames,
        // model replies) can carry control characters or non-ASCII junk that
        // corrupts JSON payloads and Mono's string handling. Dropped (not '?') —
        // matches the historical inline pattern at the three original call sites.
        public static string AsciiSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            bool anyBad = false;
            foreach (char c in s)
            {
                if (c < 32 || c >= 127) { anyBad = true; break; }
            }
            if (!anyBad) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (c >= 32 && c < 127) sb.Append(c);
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // degenerate-say guard
        // ------------------------------------------------------------------
        // Some models (Gemma-class quants observed live) fall into sampling loops
        // and reply with a phrase repeated hundreds of times ("I see you's see you's
        // see you's …" — a 40KB bubble). Ship-guard: trim a degenerate consecutive
        // repeat to two occurrences with an ellipsis, and cut past the length cap.
        // Pure + unit-tested (tests/test_textutil.cs).
        public static string TrimDegenerateRepeat(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";

            // Char-level runs first ("!!!!!!", "aaaaaa"): a same-character run that
            // is huge relative to the text is degenerate — cut it.
            string outp = TrimCharRun(s) ?? s;

            // Token-level runs ("you's you's you's ..."): consecutive identical
            // whitespace-split tokens repeated >= 6 times — keep two.
            outp = TrimTokenRun(outp);
            outp = TrimBigramRun(outp);

            if (outp.Length > maxLen) outp = outp.Substring(0, maxLen) + " […]";
            return outp;
        }

        // Cut a single character repeated until it dominates the string.
        private static string TrimCharRun(string s)
        {
            if (s.Length < 12) return s;
            int run = 1, best = 0, bestAt = 0;
            for (int i = 1; i <= s.Length; i++)
            {
                bool same = i < s.Length && s[i] == s[i - 1];
                if (same) { run++; continue; }
                if (run > best) { best = run; bestAt = i - run; }
                run = 1;
            }
            // only when the repeat dominates: >= 24 long AND a quarter of the text
            if (best >= 24 && best * 4 >= s.Length)
            {
                var sb = new StringBuilder(s, 0, Math.Max(1, bestAt + 2), s.Length + 8);
                sb.Append(" […]");
                sb.Append(s.Substring(bestAt + best)); // content after the run survives too
                return sb.ToString();
            }
            return s;
        }

        // One-token consecutive runs (incl. punctuation tokens like "you's").
        private static string TrimTokenRun(string s)
        {
            string[] tokens = SplitTokens(s);
            if (!HasRun(tokens, 1, 6, out int runStart, out int unitTokens, out int runEnd)) return s;
            return Rebuild(tokens, runStart, unitTokens, runEnd);
        }

        // Two-token ("see you's" style) consecutive runs.
        private static string TrimBigramRun(string s)
        {
            string[] tokens = SplitTokens(s);
            if (tokens.Length < 12) return s;
            if (!HasRun(tokens, 2, 6, out int runStart, out int unitTokens, out int runEnd)) return s;
            return Rebuild(tokens, runStart, unitTokens, runEnd);
        }

        private static string[] SplitTokens(string s)
        {
            return s.Split(' ');
        }

        // Longest run of consecutive identical k-token windows, >= 6 repeats.
        // Outputs: runStart (first token of the run), unitTokens (window width),
        // runEnd (exclusive — first token AFTER the whole run).
        private static bool HasRun(string[] tokens, int unitWidth, int minRepeats, out int runStart, out int unitTokens, out int runEnd)
        {
            runStart = 0; unitTokens = 0; runEnd = 0;
            if (tokens.Length < unitWidth * minRepeats) return false;
            for (int i = 0; i + unitWidth * minRepeats <= tokens.Length; i++)
            {
                bool match = true;
                for (int u = 1; u < minRepeats; u++)
                {
                    for (int k = 0; k < unitWidth; k++)
                    {
                        if (!string.Equals(tokens[i + k], tokens[i + u * unitWidth + k], StringComparison.Ordinal))
                        { match = false; break; }
                    }
                    if (!match) break;
                }
                if (!match) continue;
                // Matched min repeats at i — extend to the full repeat length.
                runStart = i;
                unitTokens = unitWidth;
                int j = i + unitWidth * minRepeats;
                while (j + unitWidth <= tokens.Length)
                {
                    bool ok = true;
                    for (int k = 0; k < unitWidth; k++)
                        if (!string.Equals(tokens[j + k], tokens[j - unitWidth + k], StringComparison.Ordinal))
                        { ok = false; break; }
                    if (!ok) break;
                    j += unitWidth;
                }
                runEnd = j;
                return true;
            }
            return false;
        }

        // Keep everything outside the run; inside it, two units + " […]".
        private static string Rebuild(string[] tokens, int runStart, int unitTokens, int runEnd)
        {
            var sb = new StringBuilder();
            for (int t = 0; t < runStart + unitTokens * 2 && t < tokens.Length; t++)
                sb.Append(t == 0 ? "" : " ").Append(tokens[t]);
            sb.Append(" […]");
            for (int t = runEnd; t < tokens.Length; t++) // content AFTER the glitched run survives too
                sb.Append(' ').Append(tokens[t]);
            return sb.ToString();
        }
    }
}