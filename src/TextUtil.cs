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
        // RP chat-template placeholders ({{user}}/{{char}}). Models trained on
        // SillyTavern-style roleplay templates write these into their SPEECH
        // (observed live: "nudges against {{user}}'s entrance") — a literal
        // "{{user}}" leaking into the chat window breaks the fiction. The mod
        // resolves {{user}} to whoever is actually closest to this NPC (the
        // live engagement partner, else the nearest person in radius, else the
        // host player) and {{char}} to the NPC itself, at speak time.
        // Pure + unit-tested (tests/test_textutil.cs); case- and
        // whitespace-tolerant ({{ User }}) since templates vary.
        // ------------------------------------------------------------------
        public static string SubstituteRpPlaceholders(string text, string userReplacement, string charReplacement)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("{{", StringComparison.Ordinal) < 0) return text;
            string result = text;
            if (!string.IsNullOrEmpty(charReplacement))
                result = ReplacePlaceholder(result, "char", charReplacement);
            if (!string.IsNullOrEmpty(userReplacement))
            {
                result = ReplacePlaceholder(result, "user", userReplacement);
                // Models also emit {{person}} for the same slot sometimes.
                result = ReplacePlaceholder(result, "person", userReplacement);
            }
            return result;
        }

        // ------------------------------------------------------------------
        // Console thought containment (pure + unit-tested): a console reply that
        // mixes PLANNING with an RP marker ("*considers* I need energy", 'the
        // letter said "fetch the mail" — I should go') used to be broadcast WHOLE
        // by the response-block path — the model's thinking escaped as chat text.
        // SpeakablePartOf splits a reply into VOICE spans (quoted dialogue always;
        // starred actions unless the stars wrap planning) and THINKING spans
        // (plain prose that reads as self-directed planning). The caller speaks
        // only the voice and keeps the thinking as a thought.
        // ------------------------------------------------------------------
        public static string SpeakablePartOf(string text, out string thinking)
        {
            thinking = "";
            if (string.IsNullOrEmpty(text)) return "";
            var voice = new StringBuilder(text.Length);
            var thoughts = new StringBuilder();
            bool voiceMarker = text.IndexOf("{{", StringComparison.Ordinal) >= 0; // a placeholder block IS voice
            int i = 0, n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (c == '"' || c == '\u201C')
                {
                    // Straight quotes pair with themselves; a curly open pairs with
                    // a curly close. A CLOSED quote span is ALWAYS voice — planning
                    // inside quotes is spoken dialogue ("I'll wait" / "I should
                    // probably go...", she said), never contained. An UNCLOSED
                    // quote is degenerate output: its text falls back to the prose
                    // rules (planning inside it stays contained).
                    char closeCh = c == '\u201C' ? '\u201D' : '"';
                    int e = text.IndexOf(closeCh, i + 1);
                    if (e < 0)
                    {
                        string rest = text.Substring(i);
                        if (IsThinkingProse(rest)) thoughts.Append(rest);
                        else { voice.Append(rest); voiceMarker = true; }
                        i = n;
                    }
                    else
                    {
                        voice.Append(text, i, e + 1 - i);
                        voiceMarker = true;
                        i = e + 1;
                    }
                }
                else if (c == '*')
                {
                    int e = text.IndexOf('*', i + 1);
                    if (e < 0)
                    {
                        // Lone asterisk (emphasis/footnote) — prose rules apply.
                        string rest = text.Substring(i);
                        if (IsThinkingProse(rest)) thoughts.Append(rest); else voice.Append(rest);
                        i = n;
                    }
                    else
                    {
                        string inner = text.Substring(i + 1, e - i - 1);
                        // Short single-token stars ("*and*", "*ok*") are emphasis,
                        // not an action — they never count as the voice marker.
                        if (inner.Trim().Length >= 4) voiceMarker = true;
                        if (inner.Trim().Length > 0 && IsThinkingProse(inner)) thoughts.Append(text, i, e + 1 - i);
                        else voice.Append(text, i, e + 1 - i);
                        i = e + 1;
                    }
                }
                else
                {
                    int next = i;
                    while (next < n && text[next] != '"' && text[next] != '\u201C' && text[next] != '*') next++;
                    string prose = text.Substring(i, next - i);
                    if (IsThinkingProse(prose)) thoughts.Append(prose); else voice.Append(prose);
                    i = next;
                }
            }
            thinking = CollapseSpaces(thoughts.ToString()).Trim();
            if (!voiceMarker)
            {
                // No real voice span survived (emphasis stars only / planning with a
                // stray marker) — nothing is speakable; everything stays a thought.
                if (thinking.Length == 0) thinking = CollapseSpaces(text).Trim();
                return "";
            }
            return CollapseSpaces(voice.ToString()).Trim();
        }

        // Does this prose read as SELF-DIRECTED PLANNING (a console thought) rather
        // than narration or speech? Signals: first-person intent ("I should/need/
        // I'll"), let's/let me, goal/plan vocabulary, and the console's own command
        // words (a thought that names cd/ls is terminal reasoning). Applied only to
        // text OUTSIDE quoted dialogue — planning inside quotes is speech.
        private static System.Text.RegularExpressions.Regex _thinkingSignal;
        private static System.Text.RegularExpressions.Regex ThinkingSignalRegex()
        {
            if (_thinkingSignal == null)
            {
                _thinkingSignal = new System.Text.RegularExpressions.Regex(
                    "("
                    + @"\bi\s+(should|need|wanted?|want to|gotta|have to|must|will|would|am going|was going|plan|planned|decided|think|thought)\b"
                    + @"|\bi'?ll\b|\bwe'?ll\b|\bi'?m going to\b|\blet'?s\b|\blet me\b|\bgoing to\b|\btime to\b"
                    + @"|\bi think\b|\bmy goal\b|\bthe goal\b|\bmy plan\b|\bplan\s*:|\bstep\s*\d"
                    + @"|\bmaybe\s+(i|we|let|it|the)\b|\bthen\s+(i|we)\b|\bnext,?\s+(i|we|let)\b|\bfirst,?\s+(i|we|let|then)\b"
                    + @"|\b(cd|ls|sonar|whoami|go_to|report_issue)\b"
                    + ")",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            }
            return _thinkingSignal;
        }

        public static bool IsThinkingProse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (t.Length < 2) return false;
            try { return ThinkingSignalRegex().IsMatch(t); } catch (Exception) { return false; }
        }

        private static string CollapseSpaces(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            try { return System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " "); }
            catch (Exception) { return s; }
        }

        // Is this reply a RESPONSE BLOCK — roleplay the model means as its actual
        // chat line? Detected the objective way: asterisk-wrapped actions (*tail
        // sways*), quoted dialogue ("..."), or an RP placeholder ({{user}}).
        // Plain short thoughts ("check the farm, then bed") have none of those and
        // stay working memory; a block has at least one marker and says it out loud.
        // Pure + unit-tested (tests/test_textutil.cs).
        public static bool IsRpResponseBlock(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.IndexOf("{{", StringComparison.Ordinal) >= 0) return true;
            return HasAsteriskAction(text) || HasQuotedDialogue(text);
        }

        // Do the starred actions in a block narrate things the game could DO
        // physically (walk over, climb in, grab it) instead of sending commands?
        // RP-locked models narrate "*walks to the kitchen*" and wait — the world
        // never moves, because only command lines act. The caller uses this for a
        // one-line reminder after speaking the block. Pure + unit-tested.
        public static bool StarsWantRealActions(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('*') < 0) return false;
            int i = 0;
            while (true)
            {
                int open = text.IndexOf('*', i);
                if (open < 0) break;
                int close = text.IndexOf('*', open + 1);
                if (close < 0) break;
                string seg = text.Substring(open + 1, close - open - 1).Trim();
                if (seg.Length > 0 && ActionVerbRegex().IsMatch(seg)) return true;
                i = close + 1;
            }
            return false;
        }

        private static System.Text.RegularExpressions.Regex _actionVerb;
        private static System.Text.RegularExpressions.Regex ActionVerbRegex()
        {
            if (_actionVerb == null)
            {
                _actionVerb = new System.Text.RegularExpressions.Regex(
                    @"\b(walks?|wanders?|steps?|heads?|runs? over|runs? to|jumps?|hops?|climbs?|enters?|crouches?|kneels?|lies? (down|back)|sits? (down|up)|stands? (up|by)|grabs?|grabs? up|picks? up|reaches?|touches?|rubs?|mounts?|dismounts?|cuddles? up|curls?)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            }
            return _actionVerb;
        }

        // *action* — an asterisk pair with non-space content between. (char IndexOf —
        // no string overloads: some mcs/mcorlib surfaces lack the comparison variants.)
        private static bool HasAsteriskAction(string text)
        {
            int open = text.IndexOf('*');
            while (open >= 0)
            {
                int close = text.IndexOf('*', open + 1);
                if (close < 0) return false; // lone asterisk (censor/footnote) — not a marker
                if (close - open > 2) return true;
                open = text.IndexOf('*', close + 1);
            }
            return false;
        }

        // Quote pairs (straight or curly) with words inside — dialogue.
        private static bool HasQuotedDialogue(string text)
        {
            return QuotePair(text, '"', '"') || QuotePair(text, (char)0x201C, (char)0x201D);
        }

        private static bool QuotePair(string text, char open, char close)
        {
            int o = text.IndexOf(open);
            if (o < 0) return false;
            int c = text.IndexOf(close, o + 1);
            while (o >= 0 && c > o)
            {
                if (c - o > 2) return true; // words inside, not an empty pair
                o = text.IndexOf(open, o + 1);
                c = text.IndexOf(close, o + 1);
            }
            return false;
        }

        // Replace every "{{ <slot> }}" (case/space tolerant) with the value; other
        // placeholders pass through. Bracketless scan (no regex backtracking per char).
        private static string ReplacePlaceholder(string text, string slot, string value)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                int open = text.IndexOf("{{", i, StringComparison.Ordinal);
                if (open < 0) { sb.Append(text.Substring(i)); break; }
                int close = text.IndexOf("}}", open + 2, StringComparison.OrdinalIgnoreCase);
                if (close < 0) { sb.Append(text.Substring(i)); break; }
                string inner = text.Substring(open + 2, close - open - 2).Trim();
                if (string.Equals(inner, slot, StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(text.Substring(i, open - i)).Append(value);
                    i = close + 2;
                }
                else
                {
                    // A different placeholder — copy up to the open brace and continue
                    // inside it (so {{char}}" and "{{user}}" in one line both resolve).
                    sb.Append(text.Substring(i, open + 2 - i));
                    i = open + 2;
                }
            }
            return sb.ToString();
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

        // Type-name scrubber (2026-10-25): C# objects that reached a string via
        // ToString() ("System.Collections.Generic.Dictionary`2[System.String,
        // System.Object]") used to persist in transcripts/memories and REPLICATE —
        // models copy what they see, so one leak echoed through chat for turns.
        // Strips the known spans at every persistence/relay point (say, heard-chat,
        // facts, transcript). Pure.
        public static string ScrubTypeNames(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf("System.", StringComparison.Ordinal) < 0) return text;
            try
            {
                var rx = new System.Text.RegularExpressions.Regex(
                    "System\\.[A-Za-z0-9_.]+`?\\d*(\\[[^\\]]*\\])?(\\[[^\\]]*\\])?");
                text = rx.Replace(text, " ");
                return System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
            }
            catch (Exception) { return text; }
        }
    }
}