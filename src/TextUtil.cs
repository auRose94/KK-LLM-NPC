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

        // ------------------------------------------------------------------
        // chat-template expansion
        // ------------------------------------------------------------------
        // Each LLM call mode ships its prompt as an editable template file
        // (system_prompt_<mode>.txt next to the DLL; see Main.cs binds). The
        // dynamic parts are {tokens} replaced wholesale: {gender}/{species}/
        // {traits}/{taken} for the one-shot name prompt, {stereo} for the
        // vision caption clause. Unknown {tokens} pass through untouched so a
        // user-edited template that drops a placeholder degrades gracefully
        // (nothing here validates content — the reply validators do that).
        // Pure + unit-tested (tests/test_textutil.cs).
        public static string ExpandTemplate(string template, System.Collections.Generic.Dictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(template) || vars == null || vars.Count == 0) return template;
            string result = template;
            foreach (var kv in vars)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                result = result.Replace(kv.Key, kv.Value ?? "");
            }
            return result;
        }

        // ------------------------------------------------------------------
        // Fact hygiene (pure + unit-tested): is this "fact" VIEWER-RELATIVE —
        // i.e. useless the moment the speaker or the thing moves? "object to
        // left", "the bed is here", "the wall ahead". Facts are long-term
        // knowledge; they must be name-anchored ("bed is upstairs", "in front
        // of the house" — anchored to a NAMED thing, that's allowed) or a
        // lesson. The remember tool refuses these with a teaching note;
        // NPCInstance.RememberFact silently drops any that slip in elsewhere.
        // ------------------------------------------------------------------
        private static readonly System.Text.RegularExpressions.Regex[] ViewerRelativePatterns =
        {
            // "object to left", "to my right", "on the right" — but NOT
            // "to the left of the stairs" (a named anchor follows: fine).
            new System.Text.RegularExpressions.Regex(
                @"\b(to|on|at|from|toward|towards|past|over)\s+(my|our|your|their|his|her|the)?\s*(left|right)\b(?!\s+of)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            // "left of me", "behind you", "in front of me/us/you".
            new System.Text.RegularExpressions.Regex(
                @"\b(left|right|behind|ahead|front|back|rear)\s+of\s+(me|us|you|i)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            // "in front of me", "next to me", "near me", "beside me" — viewer anchors.
            new System.Text.RegularExpressions.Regex(
                @"\b(in front of|ahead of|next to|beside|near|nearby|close to|opposite|in back of)\s+(me|us|you)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            // "my left side", "their back", "his right" — body-attached frames.
            new System.Text.RegularExpressions.Regex(
                @"\b(my|our|your|their|his|her)\s+(left|right|side|front|back|rear)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            // Bare viewer-frame headings with no named anchor: "the wall ahead",
            // "door behind". "ahead of the house" (named anchor) is allowed.
            new System.Text.RegularExpressions.Regex(
                @"\b(ahead|behind)\b(?!\s+of\s+\w)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            // "the bed is here" — a position that stopped being true the moment
            // the speaker memorized it from somewhere else.
            new System.Text.RegularExpressions.Regex(
                @"\b(is|are|was|were)\s+(right\s+)?here\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant),
        };

        public static bool IsViewerRelativeFact(string fact)
        {
            if (string.IsNullOrWhiteSpace(fact)) return false;
            string s = fact.Trim();
            foreach (var rx in ViewerRelativePatterns)
                try { if (rx.IsMatch(s)) return true; } catch (Exception) { }
            return false;
        }

        // The refusal note the remember tool returns — teaches the restatement shape.
        public static string ViewerRelativeFactNote()
        {
            return "facts are STICKY knowledge — this one is viewer-relative (left/right/ahead/behind me, 'is here') and goes stale the moment anything moves; name the thing and its durable place ('bed is upstairs', 'the blender is in the kitchen'), or drop the idea";
        }

        // ------------------------------------------------------------------
        // Junk-fact classifier round 2 (pure + unit-tested): beyond viewer-
        // relative text, a fact store also fills with (a) GOALS ("goal: lay an
        // egg", "my goal is X" — the goal slot carries the plan; facts keep
        // getting re-read long after the goal cleared → "stale goals in facts"),
        // (b) SELF states/positions ("i am in the kitchen", "i am hungry",
        // "i have 3 eggs" — all queryable LIVE via ls/status/sonar/cat needs),
        // (c) TRANSIENT object states ("the door is open", "the can is empty"
        // — they change; read them live instead of banking them).
        // Returns a machine-readable reason per class (null = keep the fact).
        // ------------------------------------------------------------------
        private static readonly System.Text.RegularExpressions.Regex[] GoalishPatterns =
        {
            new System.Text.RegularExpressions.Regex(
                @"^\s*goal\b|^\s*(\[?goal\]?:)|\bgoal\s*:|^(set|new|current)\s+goal\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            new System.Text.RegularExpressions.Regex(
                @"\b(my|our|their|current|new)\s+goal\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant),
        };

        private static readonly System.Text.RegularExpressions.Regex[] SelfStatePatterns =
        {
            // "i am in the kitchen", "i'm near the blender", "i am standing here".
            new System.Text.RegularExpressions.Regex(
                @"\bi\s*(am|'m|was)\s+(in|at|inside|near|nearby|beside|next to|close to|by|on|behind|front of|standing|sitting|lying|heading|walking)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            // "i am here", "i'm here".
            new System.Text.RegularExpressions.Regex(
                @"\bi\s*(am|'m)\s+here\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            // Body needs/states: "i am hungry", "i feel tired", "i am egg-bound".
            new System.Text.RegularExpressions.Regex(
                @"\bi\s*(am|'m|feel|felt|seem)\s+(hungry|thirsty|tired|sleepy|sleeping|asleep|sick|horny|aroused|stimulated|wet|dry|dirty|filthy|naked|cold|hot|warm|full|stuffed|soaked|egg ?-?bound|pregnant|energetic|exhausted)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            // "i have 3 eggs", "i have enough energy" → cat needs reads it live.
            new System.Text.RegularExpressions.Regex(
                @"\bi\s*(have|had|carry|carrying)\s+(\d|enough|some|a few|full)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant),
        };

        private static readonly System.Text.RegularExpressions.Regex ObjectStatePattern =
            // "the door is open", "the can was empty", "the machine is busy".
            new System.Text.RegularExpressions.Regex(
                @"\b(is|are|was|were)\s+(open|opened|closed|locked|unlocked|busy|occupied|full|empty|dry|wet|soaked|broken|damaged|hungry|sleeping|planted|mature|ripened|grown|watered|ready|ripe|not built|unbuilt|under construction)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        public static string JunkFactReason(string fact)
        {
            if (string.IsNullOrWhiteSpace(fact)) return null;
            string s = fact.Trim();
            foreach (var rx in GoalishPatterns)
                try { if (rx.IsMatch(s)) return "goalish_fact"; } catch (Exception) { }
            foreach (var rx in SelfStatePatterns)
                try { if (rx.IsMatch(s)) return "self_state_fact"; } catch (Exception) { }
            try { if (ObjectStatePattern.IsMatch(s)) return "stale_state_fact"; } catch (Exception) { }
            if (IsViewerRelativeFact(s)) return "stale_relative_fact";
            return null;
        }

        // Class-specific teaching notes (why refused + where the info lives live).
        public static string JunkFactNote(string reason)
        {
            switch (reason)
            {
                case "goalish_fact":
                    return "goals do NOT belong in facts — the goal slot carries the plan: `goal <text>`; facts are sticky world knowledge. This fact is refused (that's why 'stale goals' linger — the goal already cleared).";
                case "self_state_fact":
                    return "your body's position/needs are LIVE — query them any time (ls, status, sonar, cat needs, feel); facts must be STICKY world knowledge, not momentary body state. Refused.";
                case "stale_state_fact":
                    return "that object state changes (open→closed, empty→full, built→not) — read it LIVE (ls/status/cat places) when you need it instead of banking a snapshot that will be wrong soon. Refused.";
                default:
                    return ViewerRelativeFactNote();
            }
        }
    }
}