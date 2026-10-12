// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// ConsoleShell — the bash-like command vocabulary and line/reply parser for the
// console REPL. Pure C# (no Unity/BepInEx deps) so it is unit-testable:
//   mcs -target:exe -out:/tmp/t_cs.exe tests/test_console_shell.cs src/ConsoleShell.cs && mono /tmp/t_cs.exe
//
// Design goals:
//   * command names a small model already knows from shell data (ls, cd, cat, echo, ps, pwd...)
//   * lenient parsing: prompts, bullets, markdown fences, legacy act-tool names
//     (say -> echo, go_to -> cd, interact -> use, walk -> run, ...) and call-style
//     args (cd(name="bed"), run(duration=2)) are all recovered
//   * one command per line; payload commands (echo/remember/goal) keep the raw
//     rest-of-line so quoted speech survives.
using System;
using System.Collections.Generic;
using System.Text;

namespace KKLLMNPC
{
    // Result of SkillParseAdd; Error != null means the add was refused (with the reason).
    internal class SkillAddParse
    {
        public string Name;
        public List<string> Steps;
        public string Error;
    }

    internal sealed class ConsoleLine
    {
        // Canonical command name (lowercase) — null when the line has no known command.
        public string Cmd;
        // Original trimmed line (for the transcript).
        public string Raw;
        // Quote-aware token split of the arguments (payload commands still have this,
        // joined back with spaces; Payload holds the raw remainder).
        public string[] Args = new string[0];
        // Raw remainder after the command word — used by echo/remember/goal/forget
        // so inner spacing and quotes of the speech/fact text are preserved.
        public string Payload;
        public bool IsUnknown { get { return Cmd == null; } }
    }

    // ------------------------------------------------------------------
    // ConsoleTools — OpenAI-style function definitions for the console commands
    // + recovery of tool-call JSON the model emitted INLINE (as content).
    //
    // Why both: models trained on tool-call chat templates (Qwen, Hermes, GLM…)
    // keep emitting `<tool_call>`-wrapped JSON even when the request carries no tools
    // array — their template priors force the format. When the request DOES
    // carry a tools array, a tool-aware server parses the stream into proper
    // `tool_calls` deltas (deterministic; no markup in content at all) and the
    // inline salvage only has to cover servers without tool support.
    // Pure C# (vocabulary only) — unit-testable.
    // ------------------------------------------------------------------
    internal class ConsoleToolParam { public string Key; public string Type; public string Description; }

    internal static class ConsoleTools
    {
        private static ConsoleToolParam P(string key, string type, string desc)
        { return new ConsoleToolParam { Key = key, Type = type, Description = desc }; }

        // Params per console command. "target" covers names and "id:N" forms;
        // payload commands get "text". Types are loose (string) — the shell's
        // line parser already handles numbers in string form.
        private static readonly Dictionary<string, ConsoleToolParam[]> Params = new Dictionary<string, ConsoleToolParam[]>(StringComparer.Ordinal)
        {
            { "echo",     new[] { P("text", "string", "what you say out loud") } },
            { "emote",    new[] { P("text", "string", "body language you perform") } },
            { "remember", new[] { P("text", "string", "the sticky fact to store") } },
            { "forget",   new[] { P("text", "string", "the fact to drop (prefix match)") } },
            { "goal",     new[] { P("text", "string", "goal text, or 'done', or 'drop [why]'") } },
            { "report",   new[] { P("text", "string", "what is broken/frustrating + what you tried") } },
            { "cd",       new[] { P("target", "string", "place name, a player's name, or id:N") } },
            { "use",      new[] { P("target", "string", "usable name or id:N") } },
            { "get",      new[] { P("target", "string", "optional item hint (default: ~1m ahead)") } },
            { "throw",    new[] { P("target", "string", "optional direction hint (default: view direction)") } },
            { "find",     new[] { P("target", "string", "place name, or degrees to probe") } },
            { "look",     new[] { P("target", "string", "left|right|up|down|around, or degrees") } },
            { "cat",      new[] { P("item", "string", "facts|goal|plan|chat|needs|history|stations|places|map|body|hold|here|player|quest") } },
            { "sonar",    new[] { P("filter", "string", "optional glyphs 'W S V U K P' or 'all'") } },
            { "run",      new[] { P("duration", "string", "seconds (default 2)"), P("direction", "string", "left|right") } },
            { "turn",     new[] { P("deg", "string", "degrees (+right/-left)") } },
            { "crouch",   new[] { P("amount", "string", "0..1 (0 stand, 1 full crouch)") } },
            { "follow",   new[] { P("state", "string", "on|off") } },
            { "sleep",    new[] { P("secs", "string", "seconds to pause that long") } },
            { "plan",     new[] { P("text", "string", "add a step | 'clear' | 'next' (current step done)") } },
        };
        // no-parameter commands (empty properties).
        private static readonly string[] NoParams =
        {
            "ls", "ps", "pwd", "whoami", "status", "stop", "jump", "exit", "drop",
            "hold", "screenshot", "help", "clear",
        };

        // OpenAI tools array for the console vocabulary (Table + module extras).
        // Returned as plain List/Dictionary shapes — KKLLMNPC Json.Write serializes.
        public static List<object> BuildTools()
        {
            var tools = new List<object>();
            foreach (var name0 in ConsoleShell.CommandNames())
            {
                string name = name0;
                if (name == "clear") continue; // no game effect — not worth a schema entry
                var props = new Dictionary<string, object>();
                ConsoleToolParam[] ps;
                if (Params.TryGetValue(name, out ps))
                {
                    foreach (var p in ps)
                        props[p.Key] = new Dictionary<string, object> { ["type"] = p.Type, ["description"] = p.Description };
                }
                else if (Array.IndexOf(NoParams, name) < 0)
                {
                    // module tools: positional target + amount convention
                    props["target"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "target (name or id:N)" };
                    props["amount"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "optional amount" };
                }
                string desc = ConsoleShell.HelpFor(name);
                if (string.IsNullOrEmpty(desc)) desc = "(module command)";
                tools.Add(new Dictionary<string, object>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["description"] = desc,
                        ["parameters"] = new Dictionary<string, object>
                        {
                            ["type"] = "object",
                            ["properties"] = props,
                            ["required"] = new string[0],
                            ["additionalProperties"] = false,
                        },
                    },
                });
            }
            return tools;
        }

        // ---------------- inline tool-call recovery ----------------
        // The wrappers we recognize (the JSON inside is what matters — found by
        // brace matching, so the wrapper text barely matters). Markers assembled
        // from fragments; keep them that way (never contiguous in source).
        internal static readonly string ToolCallOpen = "<" + "tool_call" + ">";
        internal static readonly string ToolCallClose = "<" + "/" + "tool_call" + ">";
        private static readonly string[] ToolCallOpens =
        {
            ToolCallOpen,
            "<|" + "tool_call" + "|>",
            "[" + "TOOL_CALL" + "]",
        };

        // Extract tool-call JSON blocks from anywhere in a reply and turn each into
        // a console command line; narration between blocks is kept as plain lines.
        // Returns an EMPTY list when the text carries no tool-call JSON at all
        // (so plain replies fall back to the regular parse/salvage).
        public static List<string> ToolCallMarkupLines(string text)
        {
            var outp = new List<string>();
            if (string.IsNullOrEmpty(text)) return outp;
            bool anyCall = false;
            int i = 0, n = text.Length;
            while (i < n)
            {
                int hit = -1, hitLen = 0;
                foreach (var open in ToolCallOpensWithFence)
                {
                    int j = text.IndexOf(open, i, StringComparison.OrdinalIgnoreCase);
                    if (j >= 0 && (hit < 0 || j < hit)) { hit = j; hitLen = open.Length; }
                }
                if (hit < 0) { AddNarration(text.Substring(i), outp); break; }
                AddNarration(text.Substring(i, hit - i), outp);
                int brace = text.IndexOf('{', hit + hitLen);
                if (brace < 0) { i = hit + hitLen; continue; }
                int end = MatchBrace(text, brace);
                if (end < 0) { i = brace; continue; }
                if (AddParsed(text.Substring(brace, end - brace + 1), outp)) anyCall = true;
                i = end + 1;
            }
            return anyCall ? outp : new List<string>();
        }

        // Fences count as markers: models trained on act/tool schemas fence the JSON
        // (`​```json {"name":"cd",...}`​```) — brace matching finds the object; what
        // isn't a call survives as narration lines.
        private static readonly string[] ToolCallOpensWithFence = BuildOpens();
        private static string[] BuildOpens()
        {
            var l = new List<string>(ToolCallOpens);
            l.Add("```");
            return l.ToArray();
        }

        private static void AddNarration(string chunk, List<string> outp)
        {
            if (string.IsNullOrEmpty(chunk)) return;
            foreach (var raw in chunk.Split('\n'))
            {
                string t = raw.Trim();
                // Markup leftovers around a brace-matched call (close markers, fence
                // tails) are junk, not narration.
                if (t.Length == 0 || t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("<", StringComparison.Ordinal)) continue;
                outp.Add(t);
            }
        }

        private static bool AddParsed(string json, List<string> outp)
        {
            object parsed;
            try { parsed = Json.Parse(json); } catch (Exception) { return false; }
            var d = parsed as Dictionary<string, object>;
            if (d == null)
            {
                var list = parsed as List<object>; // a JSON array of call objects
                if (list == null) return false;
                bool any = false;
                foreach (var item in list) any |= AddOne(item as Dictionary<string, object>, outp);
                return any;
            }
            return AddOne(d, outp);
        }

        private static bool AddOne(Dictionary<string, object> d, List<string> outp)
        {
            if (d == null) return false;
            // Tool-call shape: {"name":"cd","arguments":{...}} (or function-nested).
            // Act-schema shape: {"action":"say","say":"hi",...} — command in "action".
            // NOTE "name" resolves contextually: inside real arguments it IS the
            // cd/use target, so "action" wins when present.
            object act;
            d.TryGetValue("action", out act);
            bool actShape = act is string && ((string)act).Length > 0;
            var fn = d.GetValueOrDefault("function") as Dictionary<string, object> ?? d;
            string name = actShape ? (string)act : (fn.GetValueOrDefault("name") ?? d.GetValueOrDefault("name")) as string;
            if (string.IsNullOrEmpty(name))
            {
                // Not a call after all (prose-like object without a tool name):
                // keep the longest string value so a stray payload still lands.
                string best = null;
                foreach (var kv in d)
                {
                    var s = kv.Value as string;
                    if (s != null && (best == null || s.Length > best.Length)) best = s;
                }
                if (!string.IsNullOrWhiteSpace(best)) { outp.Add(best.Trim()); return false; }
                return false;
            }
            object argsObj = fn.GetValueOrDefault("arguments") ?? fn.GetValueOrDefault("parameters")
                ?? d.GetValueOrDefault("arguments") ?? d.GetValueOrDefault("args");
            if (argsObj == null)
            {
                // No explicit arguments container: act-schema replies hold the args in
                // the dict itself; bare tool-shape calls hold them there minus "name".
                if (actShape) argsObj = d;
                else
                {
                    var stripped = new Dictionary<string, object>();
                    foreach (var kv in d)
                        if (kv.Key != "name" && kv.Key != "function") stripped[kv.Key] = kv.Value;
                    argsObj = stripped;
                }
            }
            string argsStr = argsObj as string;
            if (argsStr == null && argsObj != null) argsStr = Json.Write(argsObj);
            string line = ConsoleShell.ToolCallToLine(name, argsStr);
            if (line == null) return false;
            outp.Add(line);
            return true;
        }

        // Quote-aware brace matcher. Returns the index of the matching '}' or -1.
        private static int MatchBrace(string s, int open)
        {
            int depth = 0;
            bool inQ = false;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"')
                {
                    if (inQ && i > 0 && s[i - 1] == '\\') continue;
                    inQ = !inQ;
                }
                if (inQ) continue;
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }
    }

    internal static class ConsoleSalvage
    {
        // The first occurrence of any marker starts an embedded-command segment.
        // NOTE: the think-marker spelling is fragment-assembled — the contiguous
        // markers must never appear in source (see ModelFamilies.cs header).
        private static readonly string ThinkOpen = "<" + "think";
        private static readonly string ThinkClose = "<" + "/think" + ">";
        private static readonly string[] JunkMarkers =
        {
            "<tool_call", "</tool_call", "<|", "|>", "<br/>", "<p/>", "<n/>", "<s/>",
            "<k>", "</k>", "<lsl->", "<goal", "<whomi", "<whoami", "<end", "---end",
            "<end-of-turn", "</s>", "<s>", ThinkOpen, ThinkClose,
        };

        private static int IndexOfJunk(string s)
        {
            int best = -1;
            foreach (var m in JunkMarkers)
            {
                int i = s.IndexOf(m, StringComparison.OrdinalIgnoreCase);
                if (i >= 0 && (best < 0 || i < best)) best = i;
            }
            return best;
        }

        /// <summary>Clean a reply: (1) tool-call JSON streamed as content (`<tool_call>`-wrapped,
        /// `<|tool_call|>`, or plain fenced) is converted to exact command lines, with
        /// narration kept as plain lines; (2) per line, cut at tool-markup junk (clean
        /// head kept, buried commands recovered as lines); or — when there is no
        /// markup — split speech with the whole terminal turn glued onto it ("…goal
        /// explore the house echo Hi everyone~ sleep"). Plain lines pass through.</summary>
        public static string Clean(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return reply;
            // Native tool-call JSON that the server never parsed (no tools support /
            // markup streamed as content). These become exact lines — args included —
            // which the word-scavenger below cannot recover reliably.
            var fromCalls = ConsoleTools.ToolCallMarkupLines(reply);
            if (fromCalls.Count > 0)
                return string.Join("\n", fromCalls.ToArray());
            var outLines = new System.Collections.Generic.List<string>();
            foreach (var rawLine in reply.Split('\n'))
            {
                int cut = IndexOfJunk(rawLine);
                if (cut >= 0)
                {
                    string head = rawLine.Substring(0, cut).Trim().TrimEnd(':', ',', ';');
                    if (head.Length > 0) outLines.Add(head); // clean head (echo text or prose)
                    SalvageCommands(rawLine.Substring(cut), outLines);
                }
                else GlueSplit(rawLine, outLines);
            }
            if (outLines.Count == 0) return "";
            return string.Join("\n", outLines.ToArray());
        }

        // Models that learned the terminal convention still glue the entire turn onto
        // one line after speech ("… my tummy! ps whoami status go find bed echo Zzzzzz~
        // 15m"). REAL glued command queues pack command words DENSELY — at least two
        // inside a 3-token window ("ps whoami status", "go find bed"). Roleplay prose
        // scatters command words one at a time ("let me use you~", "stop! You sick
        // bastard!", "get out") — those are speech and stay speech; the old
        // "2 command tokens anywhere" rule butchered exactly that prose (observed
        // live: long RP paragraphs carved into junk echo/use/stop commands, a goal
        // set from fragments). RP replies also never need the split: they carry no
        // command queue, and the tool-call JSON salvage doesn't fire on them either.
        private static void GlueSplit(string line, List<string> outp)
        {
            if (line == null || line.Trim().Length == 0) return;
            var tokens = line.Split(' ');
            if (tokens.Length < 5)
            {
                outp.Add(line);
                return;
            }
            int clusterStart = -1;
            for (int i = 1; i < tokens.Length; i++)
            {
                if (!DenseCommandish(tokens[i])) continue;
                bool nextIsCmd = i + 1 < tokens.Length && DenseCommandish(tokens[i + 1]);
                bool nextNextIsCmd = i + 2 < tokens.Length && DenseCommandish(tokens[i + 2]);
                if (nextIsCmd || nextNextIsCmd) { clusterStart = i; break; }
            }
            if (clusterStart < 4 || clusterStart >= tokens.Length)
            {
                outp.Add(line);
                return;
            }
            var sb = new System.Text.StringBuilder();
            for (int t = 0; t < clusterStart; t++)
                sb.Append(t == 0 ? "" : " ").Append(tokens[t]);
            outp.Add(sb.ToString().Trim());
            var restTokens = new System.Collections.Generic.List<string>();
            for (int t = clusterStart; t < tokens.Length; t++) restTokens.Add(tokens[t]);
            SalvageCommands(string.Join(" ", restTokens.ToArray()), outp);
        }

        // The glue-split DENSITY helper: only exact commands/aliases (no fuzzy), and
        // never the function-word aliases ("me"/"id"/"self" are whoami-speak, not
        // sentence words — "let me use you" is speech). With this rule a glued queue
        // ("ps whoami status") still trips the cluster while prose never does.
        private static readonly HashSet<string> GlueFunctionWords = new HashSet<string>(StringComparer.Ordinal)
        { "me", "id", "self" };

        private static bool DenseCommandish(string w)
        {
            if (string.IsNullOrEmpty(w)) return false;
            string t = w.Trim().ToLowerInvariant();
            if (t.Length == 0) return false;
            // one glued digit ("sleep1h" → sleep) still counts
            string head = t;
            for (int i = 0; i < t.Length; i++)
                if (char.IsDigit(t[i]))
                { head = t.Substring(0, i); break; }
            if (head.Length == 0 || GlueFunctionWords.Contains(head)) return false;
            string c = ConsoleShell.CanonicalTight(head);
            return c != null && !(c == "whoami" && head.Length > 0 && head != c);
        }

        // Canonical that also forgives glued digits: "sleep1h05m" → "sleep 1h05m".
        internal static string CanonicalGlue(string w)
        {
            if (w == null) return null;
            string c = ConsoleShell.Canonical(w);
            if (c != null) return c;
            for (int i = 1; i < w.Length; i++)
            {
                if (char.IsDigit(w[i]))
                {
                    string head = w.Substring(0, i).ToLowerInvariant();
                    string hc = ConsoleShell.Canonical(head);
                    if (hc != null) return hc + " " + w.Substring(i);
                    break;
                }
            }
            return null;
        }

        private static readonly char[] WordSplit =
        { ' ', '\t', '<', '>', '|', '{', '}', '[', ']', '?', '!', '/', '"', ',', '.', ';', ':', '(', ')', '_', '-' };

        // Extract command lines from a junk segment. Words that canonicalize to known
        // commands become lines; payload-y commands (goal/remember/echo-style) swallow
        // the following non-command words as their payload; targeted commands (cd/use/
        // get/find/look) take the next token as their target.
        private static bool SalvageCommands(string seg, List<string> outp)
        {
            if (string.IsNullOrEmpty(seg)) return false;
            var words = seg.Split(WordSplit, StringSplitOptions.RemoveEmptyEntries);
            bool found = false;
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i].Trim().ToLowerInvariant();
                if (w.Length == 0) continue;
                string canon = CanonicalLoose(w);
                if (canon == null) continue;
                found = true;
                if (ConsoleShell.IsPayloadCommand(canon))
                {
                    var payload = new List<string>();
                    int j = i + 1;
                    while (j < words.Length && payload.Count < 8 && !IsCommandish(words[j]))
                    { payload.Add(words[j]); j++; }
                    outp.Add(canon + (payload.Count > 0 ? " " + string.Join(" ", payload.ToArray()) : ""));
                    i = j - 1;
                }
                else if (canon == "cd" || canon == "use" || canon == "get" || canon == "find" || canon == "look" || canon == "turn")
                {
                    // target = the next NON-command token ("go find bed" → cd bed);
                    // when none follows, the bare command — EXCEPT the action words
                    // that would fire the body at whatever stands near ("use", "get",
                    // "drop", "throw"): a bare one inside a scavenged segment is prose
                    // debris, not an order.
                    int j = i + 1;
                    while (j < words.Length && CanonicalLoose(words[j]) != null) j++;
                    bool bare = !(j < words.Length && words[j].Trim().Length > 0);
                    if (bare && (canon == "use" || canon == "get" || canon == "drop" || canon == "throw")) continue;
                    if (!bare)
                    { outp.Add(canon + " " + words[j].Trim()); i = j; }
                    else outp.Add(canon);
                }
                else outp.Add(canon);
            }
            return found;
        }

        private static string CanonicalLoose(string w)
        {
            string c = CanonicalGlue(w);
            if (c != null)
            {
                // Payload commands get their digits; plain commands return bare.
                return c.Trim();
            }
            return null;
        }

        // Would this next word itself start a command? (payload terminators)
        private static bool IsCommandish(string w)
        {
            if (string.IsNullOrEmpty(w)) return true;
            return ConsoleShell.CanonicalTight(w) != null;
        }
    }

    internal static class ConsoleShell
    {
        // ------------------------------------------------------------------
        // command table: canonical name → one-line help (order = help output)
        // ------------------------------------------------------------------
        private static readonly Dictionary<string, string> Table = new Dictionary<string, string>
        {
            // ---- read (poll the game — small, on demand) ----
            { "ls",         "what's near you (ids, distances, direction)" },
            { "ps",         "who's around (players + kobolds)" },
            { "pwd",        "where you are (pos, scene, facing)" },
            { "whoami",     "you: name, body, needs (energy, horniness, eggs)" },
            { "cat",        "read: cat facts | goal | plan | chat | needs | history | stations | places | map | body | hold | here | player | quest" },
            { "status",     "one-line snapshot (goal, needs, station, follow, map)" },
            { "find",       "locate a place (find bed) or probe a direction (find 90)" },
            { "look",       "look left | right | up | down | around | <deg>" },
            { "sonar",      "north-up ASCII map of your surroundings: sonar [filter: W S V U K P | all]" },

            // ---- act ----
            { "echo",       "SPEAK out loud: echo <your words> (this is how you talk)" },
            { "emote",      "body language, not speech: emote <what you do> — renders as *your words*" },
            { "cd",         "travel: cd bed | cd Yipper | cd id:3" },
            { "use",        "interact: use id:2 | use nest" },
            { "run",        "walk short: run [secs] [left|right]" },
            { "turn",       "turn your body: turn [deg]" },
            { "jump",       "hop a ledge / get off a station" },
            { "crouch",     "crouch: crouch [0..1]" },
            { "exit",       "leave the station you're in" },
            { "get",        "grab a nearby item (whatever is ~1m in front of your face)" },
            { "drop",       "drop what you hold" },
            { "throw",      "use your held thing: hurls it in view direction; sprays the bucket; fires tools" },
            { "hold",       "what's in your hands (details + physics)" },
            { "follow",     "follow [on|off] — stay near / release the player" },
            { "stop",       "stop moving" },
            { "sleep",      "end your turn: sleep [secs] (secs also pauses that long; chat/events wake you)" },

            // ---- memory & goals ----
            { "remember",   "store a fact: remember <fact> — STICKY knowledge only: name the thing and its durable place ('bed is upstairs'); viewer-relative facts ('to my left', 'is here') are refused — they go stale the moment anything moves" },
            { "forget",     "drop a fact: forget <fact>" },
            { "goal",       "set goal: goal <text> | goal done | goal drop [why]" },
            { "skill",      "save a reusable procedure: skill add <name> | <step> ; <step> — run with skill <name>; `cat skills` lists them" },
            { "plan",       "your steps for the current goal: plan <step text> | plan | plan next (step done) | plan clear" },

            // ---- misc ----
            { "screenshot", "show yourself your current view (image)" },
            { "report",     "file a dev bug note: report <what's broken/frustrating> (sonar wrong, cd fails...)" },
            { "help",       "list all commands" },
            { "clear",      "clear the screen (no game effect)" },
        };

        // alias → canonical (legacy act-tool names + common variants, so a model
        // trained on the old prompt or on plain shell habits still lands somewhere)
        private static readonly Dictionary<string, string> AliasMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // speak
            { "say", "echo" }, { "speak", "echo" }, { "print", "echo" }, { "talk", "echo" }, { "whisper", "echo" },
            // travel
            { "go", "cd" }, { "goto", "cd" }, { "go_to", "cd" }, { "move_to", "cd" }, { "travel", "cd" }, { "move", "cd" }, { "walk_to", "cd" }, { "navigate", "cd" },
            // interact
            { "interact", "use" }, { "touch", "use" }, { "try", "use" }, { "operate", "use" },
            // short walk
            { "walk", "run" }, { "step", "run" }, { "sprint", "run" }, { "walk_ray", "run" },
            // look
            { "look_around", "look" }, { "scan", "look" }, { "face", "look" },
            // sonar (the north-up map)
            { "radar", "sonar" }, { "radar_map", "sonar" }, { "minimap", "sonar" },
            // grab
            { "grab", "get" }, { "pickup", "get" }, { "pick", "get" }, { "take", "get" },
            // throw / activate held
            { "toss", "throw" }, { "hurl", "throw" }, { "yeet", "throw" }, { "lob", "throw" },
            { "activate", "throw" }, { "fire", "throw" }, { "throw_item", "throw" },
            // leave station
            { "leave", "exit" }, { "quit", "exit" }, { "exit_station", "exit" }, { "get_out", "exit" }, { "dismount", "exit" },
            // drop
            { "release", "drop" }, { "put", "drop" },
            // follow
            { "tail", "follow" }, { "unfollow", "follow" },
            // stop
            { "halt", "stop" }, { "break", "stop" }, { "kill", "stop" },
            // pause
            { "wait", "sleep" }, { "pause", "sleep" }, { "none", "sleep" }, { "idle", "sleep" }, { "rest", "sleep" },
            // memory
            { "memo", "remember" }, { "note", "remember" }, { "save", "remember" }, { "mem", "remember" },
            { "rm", "forget" }, { "delete", "forget" },
            // goals
            { "set_goal", "goal" }, { "complete_goal", "goal" }, { "drop_goal", "goal" }, { "g", "goal" },
            // misc
            { "stat", "status" }, { "info", "status" }, { "state", "status" }, { "top", "status" },
            { "me", "whoami" }, { "self", "whoami" }, { "id", "whoami" },
            { "where", "pwd" }, { "loc", "pwd" }, { "location", "pwd" },
            // report (AI feedback to the dev) — deliberately NOT aliasing words that
            // prose replies often start with ("problem"/"issue"/"bug"), so thinking
            // out loud never files a report by accident.
            { "report_issue", "report" }, { "bug_report", "report" }, { "report_bug", "report" },
            { "complain", "report" }, { "complaint", "report" },
            { "people", "ps" }, { "players", "ps" }, { "who", "ps" },
            { "nearby", "ls" }, { "dir", "ls" }, { "objects", "ls" }, { "things", "ls" },
            { "head", "cat" }, { "read", "cat" }, { "show", "cat" },
            { "survey", "find" }, { "locate", "find" }, { "seek", "find" }, { "search", "find" },
            { "shot", "screenshot" }, { "img", "screenshot" }, { "image", "screenshot" }, { "view", "screenshot" }, { "see", "screenshot" },
            { "man", "help" }, { "cmds", "help" }, { "commands", "help" }, { "list", "help" }, { "helpme", "help" },
            { "cls", "clear" }, { "reset_screen", "clear" },
            // legacy salvage (small models trained on the act schema)
            { "crouch", "crouch" }, { "jump", "jump" }, { "turn", "turn" }, { "rotate", "turn" }, { "spin", "turn" },
            // common small-model misspellings (from the legacy fuzzy matcher)
            { "goo_to", "cd" }, { "go_too", "cd" }, { "go_t", "cd" },
            { "waalk", "run" }, { "wakl", "run" }, { "wakk", "run" }, { "wal", "run" },
            { "saaay", "echo" }, { "saay", "echo" }, { "sya", "echo" },
            { "intercat", "use" }, { "interac", "use" }, { "intreract", "use" }, { "intract", "use" },
            { "rember", "remember" }, { "remembr", "remember" }, { "remmber", "remember" }, { "reember", "remember" },
            { "exitt_station", "exit" }, { "exit_statoin", "exit" }, { "exit_staton", "exit" }, { "exit_stat", "exit" },
            { "loo_around", "look" }, { "look_arond", "look" }, { "lookarond", "look" },
            { "folow", "follow" }, { "folo", "follow" }, { "follw", "follow" },
            { "sllep", "sleep" }, { "sleeps", "sleep" },
            { "stauts", "status" }, { "statu", "status" },
            { "hepl", "help" }, { "helpp", "help" },
            { "gget", "get" }, { "grop", "get" },
            { "dropp", "drop" }, { "dorp", "drop" },
            { "crouc", "crouch" }, { "crouchn", "crouch" },
        };

        // commands whose argument is a raw payload (speech/fact/goal text)
        private static readonly HashSet<string> PayloadCmds = new HashSet<string>(StringComparer.Ordinal)
        {
            "echo", "remember", "forget", "goal", "plan", "skill", "report",
        };

        // ------------------------------------------------------------------
        // public surface
        // ------------------------------------------------------------------
        // Module-registered tools (thrust, plant, water, ...) are added at runtime by
        // the plugin (ModuleRegistry). Exact-name match only — no fuzzy for unknown
        // module names. Populated before the first turn; tests run with an empty set.
        private static readonly HashSet<string> ExtraNames = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object ExtraLock = new object();

        public static void RegisterExtra(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (ExtraLock) ExtraNames.Add(name.Trim().ToLowerInvariant());
        }

        public static IEnumerable<string> CommandNames()
        {
            lock (ExtraLock)
            {
                foreach (var k in Table.Keys) yield return k;
                foreach (var k in ExtraNames) if (!Table.ContainsKey(k)) yield return k;
            }
        }

        public static bool IsCommandWord(string w)
        {
            if (string.IsNullOrEmpty(w)) return false;
            string n = w.Trim().ToLowerInvariant().TrimEnd('.', ',', ';', '!', '?', ':');
            if (n.Length == 0) return false;
            lock (ExtraLock) return Table.ContainsKey(n) || AliasMap.ContainsKey(n) || ExtraNames.Contains(n);
        }

        // Exact name → alias → fuzzy (Levenshtein ≤2). Returns null when nothing fits.
        public static string Canonical(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string n = name.Trim().ToLowerInvariant().TrimEnd('.', ',', ';', '!', '?', ':');
            if (n.Length == 0) return null;
            if (Table.ContainsKey(n)) return n;
            string a;
            if (AliasMap.TryGetValue(n, out a)) return a;
            lock (ExtraLock) if (ExtraNames.Contains(n)) return n;
            // fuzzy: ONLY one-character typos of 3+ char words. Deliberately tighter
            // than the legacy tool-name matcher, because here the FIRST WORD OF ANY
            // LINE is a command candidate — including prose lines ("I want food").
            // Rule: pure insertions/deletions (one string contains the other) are safe
            // ("lss"->ls, "cdd"->cd, "ech"->echo); single SUBSTITUTIONS are only
            // allowed for 4+ char commands ("let"->"get" must NOT match, "remembe"->
            // "remember" is a deletion so it does). Frequent misspellings are covered
            // by the alias table instead.
            if (n.Length < 3) return null;
            string best = null;
            foreach (var k in Table.Keys)
            {
                if (Math.Abs(n.Length - k.Length) != 1) continue;
                if (Levenshtein(n, k) == 1) { best = k; break; }
            }
            if (best == null && n.Length >= 4)
            {
                foreach (var k in Table.Keys)
                {
                    if (k.Length != n.Length || k.Length < 4) continue;
                    if (Levenshtein(n, k) == 1) { best = k; break; }
                }
            }
            return best;
        }

        // The GLUE-scan canonical: exact table + aliases + module names ONLY — no
        // Levenshtein. Edit-distance matching is for a standalone WORD that's clearly
        // a request; inside glue scans it hits ordinary prose ("burn"→"turn",
        // "text"→"next"-class accidents) and the scanner carves sentences apart.
        public static string CanonicalTight(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string n = name.Trim().ToLowerInvariant().TrimEnd('.', ',', ';', '!', '?', ':');
            if (n.Length == 0) return null;
            if (Table.ContainsKey(n)) return n;
            string a;
            if (AliasMap.TryGetValue(n, out a)) return a;
            lock (ExtraLock) if (ExtraNames.Contains(n)) return n;
            return null;
        }

        // ------------------------------------------------------------------
        // Skills parsing — `skill add <name> | <step> ; <step> ; ...`. Pure +
        // unit-tested: the name/steps split, the step validation (each must parse
        // to a REAL command — junk becomes nothing), and the dedupe/caps live here
        // so both the local add path and the swallow/egg transfer paths share it.
        // ------------------------------------------------------------------
        public static SkillAddParse SkillParseAdd(string payload, int nameMax, int maxSteps, int stepMaxChars)
        {
            var r = new SkillAddParse();
            if (string.IsNullOrWhiteSpace(payload))
            {
                r.Error = "empty — skill add <name> | <step> ; <step>";
                return r;
            }
            string p = payload.Trim();
            string lower = p.ToLowerInvariant();
            if (lower.StartsWith("add ") || lower.StartsWith("add\t")) p = p.Substring(4).Trim();
            int bar = p.IndexOf('|');
            if (bar < 0)
            {
                r.Error = "need the | : skill add <name> | <step 1> ; <step 2>";
                return r;
            }
            string name = p.Substring(0, bar).Trim();
            if (name.Length == 0) { r.Error = "empty skill name"; return r; }
            if (name.Length > nameMax) name = name.Substring(0, nameMax);
            string stepsText = p.Substring(bar + 1).Trim();
            if (stepsText.Length == 0) { r.Error = "no steps after | — what does the skill do?"; return r; }
            var steps = new List<string>();
            foreach (var raw in stepsText.Split(';'))
            {
                string step = raw.Trim();
                if (step.Length == 0) continue;
                var cl = ConsoleShell.Parse(step);
                if (cl == null || cl.IsUnknown) continue; // junk/junk step silently dropped
                if (cl.Raw.Length > stepMaxChars) cl.Raw = cl.Raw.Substring(0, stepMaxChars);
                if (steps.Count == 0 || steps[steps.Count - 1] != cl.Raw) steps.Add(cl.Raw);
            }
            if (steps.Count == 0)
            {
                r.Error = "no valid steps — each ';' part must be a REAL command";
                return r;
            }
            if (steps.Count > maxSteps) steps = steps.GetRange(0, maxSteps);
            r.Name = name;
            r.Steps = steps;
            return r;
        }

        public static bool IsPayloadCommand(string canonical)
        {
            return canonical != null && PayloadCmds.Contains(canonical);
        }

        // ------------------------------------------------------------------
        // ConsoleCompact — mechanical compaction of trimmed transcript spans.
        // Pure C# — unit-testable. The console conversation used to LOSE its oldest
        // turns silently at the window edge; a compact digest keeps the story
        // ("what happened") available without paying tokens for full text.
        // ------------------------------------------------------------------
        public static class ConsoleCompact
        {
            const int MaxItems = 8;
            const int ItemChars = 48;
            const int DigestChars = 420;

            // roleLines: "role|text" entries for the DROPPED messages (oldest first).
            // Returns one line: "[trimmed N earlier messages] a: … | u: …". Spread:
            // the first three (how it started) and the last two (where it left off).
            public static string Digest(List<string> roleLines)
            {
                int n = roleLines == null ? 0 : roleLines.Count;
                if (n == 0) return "(nothing)";
                var sb = new StringBuilder();
                sb.Append("[trimmed ").Append(n).Append(" earlier message").Append(n == 1 ? "" : "s").Append("]");
                var items = new List<string>();
                for (int i = 0; i < n && items.Count < MaxItems; i++)
                {
                    // First 3 in order + last 2 (skip ones already taken).
                    bool keep = i < 3 || i >= n - 2;
                    if (!keep) continue;
                    string entry = roleLines[i];
                    if (items.Contains(entry)) continue;
                    items.Add(entry);
                }
                foreach (var entry in items)
                {
                    string role = "a", text = entry;
                    int bar = entry.IndexOf('|');
                    if (bar >= 0) { role = entry.Substring(0, bar).Trim(); text = entry.Substring(bar + 1); }
                    if (text.Length > ItemChars) text = text.Substring(0, ItemChars) + "…";
                    string one = (role.Length > 0 ? role + ": " : "") + text;
                    if (sb.Length + one.Length > DigestChars) break;
                    sb.Append(' ').Append(one).Append(" |");
                }
                if (sb[sb.Length - 1] == '|') sb.Length -= 1;
                return sb.ToString().Trim();
            }
        }

        // The help line for one command ("travel: cd bed | cd Yipper | cd id:3"), or
        // null when unknown. Used by the tools-array builder for descriptions.
        public static string HelpFor(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return null;
            string v;
            return Table.TryGetValue(cmd.Trim().ToLowerInvariant(), out v) ? v : null;
        }

        // A structured tool call ("go_to" + {"name":"bed"}, or {"action":"say","say":"hi"})
        // → the console command line it expresses ("cd bed" / "echo hi"), or null when
        // the name isn't a console command. Accepts the arguments object as a JSON
        // string (server tool_calls), as a nested object, or double-encoded once.
        public static string ToolCallToLine(string toolName, string argStr)
        {
            string cmd = Canonical(toolName);
            if (cmd == null) return null;
            var d = ArgsAsDict(argStr);
            if (d == null) return cmd; // no usable args → the bare command
            return ExpandArgDict(d, cmd);
        }

        // Parse the arguments payload into a dictionary: handles a plain JSON object,
        // a wrapping {"arguments": ...} form, and one level of double-encoded strings.
        // Null when there is nothing usable.
        private static Dictionary<string, object> ArgsAsDict(string argStr)
        {
            if (string.IsNullOrWhiteSpace(argStr)) return null;
            string a = argStr.Trim();
            for (int unwrap = 0; unwrap < 2 && a.StartsWith("{", StringComparison.Ordinal); unwrap++)
            {
                object parsed;
                try { parsed = Json.Parse(a); } catch (Exception) { return null; }
                var asDict = parsed as Dictionary<string, object>;
                if (asDict == null) return null;
                object nested;
                if (asDict.TryGetValue("arguments", out nested)
                    || asDict.TryGetValue("args", out nested)
                    || asDict.TryGetValue("parameters", out nested))
                {
                    var s2 = nested as string;
                    if (s2 != null) { a = s2.Trim(); continue; } // double-encoded — unwrap
                    var d2 = nested as Dictionary<string, object>;
                    if (d2 != null) return d2;
                    return asDict; // arguments: null / other — treat the wrapper as args
                }
                return asDict;
            }
            return null;
        }

        // Meta keys that never become command args (tool identity / act reasoning).
        // "name" is NOT here: inside real arguments it IS the cd/use target — it is
        // stripped contextually in AddOne for bare tool-shape dicts.
        private static readonly HashSet<string> ArgMetaKeys = new HashSet<string>(StringComparer.Ordinal)
        { "action", "thought", "progress", "why", "wait", "plan", "reasoning", "type", "function", "id_str" };

        // Flatten {"name":"bed"} → "cd bed"; bool state → on/off; strafe → left/right.
        private static string ExpandArgDict(Dictionary<string, object> d, string cmd)
        {
            var sb = new StringBuilder(cmd);
            foreach (var kv in d)
            {
                string k = kv.Key.ToLowerInvariant();
                if (ArgMetaKeys.Contains(k)) continue;
                string v = kv.Value == null ? "" : kv.Value.ToString().Trim('"', '\'');
                if (v.Length == 0) continue;
                if (k == "id") { sb.Append(" id:").Append(v); continue; }
                if (k == "on" || k == "follow" || k == "state")
                { sb.Append(' ').Append(v == "True" || v == "true" ? "on" : "off"); continue; }
                if (k == "strafe" || k == "direction")
                {
                    if (v == "on") { sb.Append(" on"); continue; } // direction:"on" is follow-speak
                    float f;
                    if (float.TryParse(v, out f)) { sb.Append(' ').Append(f < 0 ? "left" : "right"); continue; }
                }
                sb.Append(' ').Append(v);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // reply parsing
        // ------------------------------------------------------------------
        // Split a model reply into console lines. Strips markdown fences, prompt
        // prefixes ($, >, #, *), bullets, and "command:"/"action:" headers.
        // Returns an empty list for empty replies; lines without a known command
        // come back with Cmd=null (the caller decides how to treat prose).
        public static List<ConsoleLine> ParseReply(string reply)
        {
            var outp = new List<ConsoleLine>();
            if (string.IsNullOrWhiteSpace(reply)) return outp;
            string s = reply.Trim();
            // markdown fence block
            if (s.StartsWith("```", StringComparison.Ordinal))
            {
                int nl = s.IndexOf('\n');
                if (nl >= 0) s = s.Substring(nl + 1);
                if (s.EndsWith("```", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 3);
                s = s.Trim();
            }
            foreach (var raw in s.Split('\n'))
            {
                string line = ParseLine(raw);
                if (line == null) continue;
                var cl = Parse(line);
                if (cl != null) outp.Add(cl);
            }
            return outp;
        }

        // Parse one line. Returns null for blank lines.
        public static string ParseLine(string raw)
        {
            string s = (raw ?? "").Trim();
            if (s.Length == 0) return null;
            // Bare prompt markers alone ("$", "$ $", "$ $ $") are transcript-echo
            // artifacts (models that copy the terminal); they are not commands and
            // should vanish silently rather than spam "unknown command". Strip
            // repeatedly (nesting!) and vanish the line when nothing is left.
            string prev;
            while (true)
            {
                prev = s;
                s = s.TrimStart('$', '>', '#').Trim();
                if (s.Length >= prev.Length) break;
            }
            if (s.Length == 0) return null;

            // "command: cd bed" / "action: go_to(name=bed)" headers
            foreach (var hdr in new[] { "command:", "cmd:", "action:", "tool:", "run:" })
            {
                if (s.StartsWith(hdr, StringComparison.OrdinalIgnoreCase))
                {
                    s = s.Substring(hdr.Length).Trim();
                    break;
                }
            }
            // leading prompt / bullet markers (only when followed by the command)
            while (s.Length > 1)
            {
                char c = s[0];
                if (c == '$' || c == '>' || c == '#' || c == '•' || c == '-' || c == '*' || c == '·' || c == '|' || c == '`')
                {
                    if (char.IsWhiteSpace(s[1]) || s[1] == '(') { s = s.Substring(1).Trim(); continue; }
                }
                break;
            }
            if (s.Length == 0) return null;
            return s;
        }

        // Full line → ConsoleLine (command + args).
        public static ConsoleLine ToLine(string line)
        {
            var cl = new ConsoleLine { Raw = line };
            if (line.Length == 0) return cl;

            // first word (may carry call-style args: cd(name="bed")) — when a paren
            // opens in the first word, extend the head to its matching close paren so
            // multi-word quoted args survive (echo("hi there"), cd(name="Top Door")).
            int i = 0;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            string head = i < line.Length ? line.Substring(0, i) : line;
            if (head.IndexOf('(') >= 0)
            {
                int depth = 0, j = line.Length;
                for (int k = 0; k < line.Length; k++)
                {
                    if (line[k] == '(') depth++;
                    else if (line[k] == ')') { depth--; if (depth == 0) { j = k + 1; break; } }
                }
                head = line.Substring(0, j);
                i = j;
            }
            string rest = i < line.Length ? line.Substring(i).Trim() : "";

            string cmdWord = head;
            string callArgs = null;
            int paren = head.IndexOf('(');
            if (paren > 0)
            {
                cmdWord = head.Substring(0, paren);
                callArgs = head.Substring(paren + 1);
                int close = callArgs.LastIndexOf(')');
                if (close >= 0) callArgs = callArgs.Substring(0, close);
            }

            string cmd = Canonical(cmdWord);
            cl.Cmd = cmd;

            var args = new List<string>();
            if (callArgs != null && callArgs.Trim().Length > 0)
                ExpandCallArgs(callArgs, cmd, args, ref cl.Payload);
            if (rest.Length > 0)
            {
                if (cmd != null && IsPayloadCommand(cmd) && cl.Payload == null)
                    cl.Payload = rest;
                args.AddRange(SplitTokens(rest));
            }
            cl.Args = args.ToArray();
            return cl;
        }

        // Convenience used by the executor: parse + resolve in one step.
        public static ConsoleLine Parse(string line)
        {
            string s = ParseLine(line);
            if (s == null) return null;
            return ToLine(s);
        }

        // "a b \"c d\" e" → [a, b, "c d", e]
        public static List<string> SplitTokens(string s)
        {
            var outp = new List<string>();
            var cur = new StringBuilder();
            bool inQ = false;
            char qch = '"';
            foreach (char c in s)
            {
                if (inQ)
                {
                    if (c == qch) { inQ = false; continue; }
                    cur.Append(c);
                }
                else if (c == '"' || c == '\'')
                {
                    inQ = true; qch = c;
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (cur.Length > 0) { outp.Add(cur.ToString()); cur.Clear(); }
                }
                else cur.Append(c);
            }
            if (cur.Length > 0) outp.Add(cur.ToString());
            return outp;
        }

        // Expand legacy call-style args into console tokens:
        //   name="bed"        → bed            (cd/use/find)
        //   id=3              → id:3           (cd/use)
        //   text/mem/goal/say="hi" → payload (echo/remember/goal)
        //   duration=2        → 2              (run)
        //   on=false          → off            (follow)
        //   dyaw_deg/turn_deg/deg=90 → 90      (turn/look)
        //   strafe=-1         → left           (run)
        //   "quoted bare"     → quoted value
        private static void ExpandCallArgs(string callArgs, string cmd, List<string> args, ref string payload)
        {
            string ca = callArgs.Trim();
            // single bare value (no '='): cd("bed"), echo("hi"), run(2)
            if (ca.IndexOf('=') < 0)
            {
                string v = Unquote(ca);
                if (v.Length > 0)
                {
                    // follow(on:false) / follow(on=true) — colon/equals state form
                    if (cmd == "follow")
                    {
                        int colon = v.IndexOf(':');
                        if (colon >= 0) v = v.Substring(colon + 1).Trim();
                        bool b;
                        if (bool.TryParse(v, out b)) { args.Add(b ? "on" : "off"); return; }
                        if (v == "on" || v == "off") { args.Add(v); return; }
                    }
                    if (cmd != null && IsPayloadCommand(cmd)) payload = v;
                    args.Add(v);
                }
                return;
            }
            // k=v pairs (split on commas outside quotes)
            var parts = new List<string>();
            var cur = new StringBuilder();
            bool inQ = false;
            foreach (char c in ca)
            {
                if (c == '"') inQ = !inQ;
                if (c == ',' && !inQ) { parts.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            if (cur.Length > 0) parts.Add(cur.ToString());
            foreach (var pr in parts)
            {
                string p = pr.Trim();
                int eq = p.IndexOf('=');
                if (eq < 1) continue;
                string k = p.Substring(0, eq).Trim().ToLowerInvariant();
                string v = Unquote(p.Substring(eq + 1).Trim());
                if (v.Length == 0) continue;
                switch (k)
                {
                    case "name":
                    case "target":
                        args.Insert(0, v);
                        break;
                    case "id":
                        args.Insert(0, "id:" + v);
                        break;
                    case "text":
                    case "mem":
                    case "message":
                    case "say":
                    case "goal":
                    case "note":
                    case "reason":
                    case "q":
                        if (cmd != null && IsPayloadCommand(cmd) && payload == null) payload = v;
                        args.Add(v);
                        break;
                    case "duration":
                    case "speed":
                    case "at":
                    case "range":
                    case "sweep":
                    case "crouch":
                    case "amount":
                        args.Add(v);
                        break;
                    case "dyaw_deg":
                    case "turn_deg":
                    case "deg":
                    case "heading_deg":
                        args.Add(v);
                        break;
                    case "strafe":
                        float sv;
                        if (float.TryParse(v, out sv)) args.Add(sv < 0 ? "left" : "right");
                        break;
                    case "on":
                    case "follow":
                    case "jump":
                        {
                            string bv = v;
                            int colon = bv.IndexOf(':');
                            if (colon >= 0) bv = bv.Substring(colon + 1).Trim();
                            bool b;
                            if (bool.TryParse(bv, out b)) args.Add(b ? "on" : "off");
                            else if (bv == "on" || bv == "off") args.Add(bv);
                        }
                        break;
                    case "multi":
                        break; // ignored in console form
                    default:
                        args.Add(v);
                        break;
                }
            }
        }

        private static string Unquote(string s)
        {
            if (s.Length >= 2 && ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\'')))
                return s.Substring(1, s.Length - 2);
            return s.Trim('"', '\'');
        }

        // ------------------------------------------------------------------
        // help text (also embedded in the system prompt)
        // ------------------------------------------------------------------
        public static string Help()
        {
            var sb = new StringBuilder();
            string lastGroup = null;
            foreach (var kv in Table)
            {
                string group = GroupFor(kv.Key);
                if (group != lastGroup)
                {
                    if (lastGroup != null) sb.Append('\n');
                    sb.Append(group).Append('\n');
                    lastGroup = group;
                }
                sb.Append("  ").Append(kv.Key.PadRight(11, ' ')).Append(kv.Value).Append('\n');
            }
            string[] extras = null;
            lock (ExtraLock) { extras = new string[ExtraNames.Count]; ExtraNames.CopyTo(extras, 0); Array.Sort(extras, StringComparer.Ordinal); }
            if (extras != null && extras.Length > 0)
            {
                sb.Append('\n').Append("MODULE COMMANDS (args are positional: target, amount)\n");
                foreach (var e in extras) sb.Append("  ").Append(e.PadRight(11, ' ')).Append("(module)").Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        private static string GroupFor(string cmd)
        {
            switch (cmd)
            {
                case "ls": case "ps": case "pwd": case "whoami": case "cat": case "status": case "find": case "look": case "sonar":
                    return "READ (poll the game)";
                case "echo": case "emote": case "cd": case "use": case "run": case "turn": case "jump": case "crouch": case "exit": case "get": case "drop": case "throw": case "follow": case "stop": case "sleep":
                    return "ACT";
                case "remember": case "forget": case "goal": case "plan":
                case "skill":
                    return "MEMORY & GOALS";
                default:
                    if (cmd == "hold") return "READ (poll the game)";
                    return "MISC";
            }
        }

        // ------------------------------------------------------------------
        // Levenshtein (small-model misspellings) — one shared implementation in
        // ChatSimilarity.cs (pure C#, unit-tested there).
        // ------------------------------------------------------------------
        public static int Levenshtein(string a, string b) => ChatSimilarity.LevenshteinDistance(a, b);
    }
}
