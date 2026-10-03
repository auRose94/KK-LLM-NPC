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
//   * one command per line; payload commands (echo/remember/goal/ask) keep the raw
//     rest-of-line so quoted speech survives.
using System;
using System.Collections.Generic;
using System.Text;

namespace KKLLMNPC
{
    internal sealed class ConsoleLine
    {
        // Canonical command name (lowercase) — null when the line has no known command.
        public string Cmd;
        // Original trimmed line (for the transcript).
        public string Raw;
        // Quote-aware token split of the arguments (payload commands still have this,
        // joined back with spaces; Payload holds the raw remainder).
        public string[] Args = new string[0];
        // Raw remainder after the command word — used by echo/remember/goal/ask/forget
        // so inner spacing and quotes of the speech/fact text are preserved.
        public string Payload;
        public bool IsUnknown { get { return Cmd == null; } }
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
            { "cat",        "read: cat facts | goal | chat | needs | history | stations | map | body | hold | player" },
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
            { "remember",   "store a fact: remember <fact>" },
            { "forget",     "drop a fact: forget <fact>" },
            { "goal",       "set goal: goal <text> | goal done | goal drop [why]" },
            { "ask",        "ask your inner world-model: ask <question>" },

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
            { "ask", "ask" }, { "query", "ask" },
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

        // commands whose argument is a raw payload (speech/fact/goal/question text)
        private static readonly HashSet<string> PayloadCmds = new HashSet<string>(StringComparer.Ordinal)
        {
            "echo", "remember", "forget", "goal", "ask", "report",
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

        public static bool IsPayloadCommand(string canonical)
        {
            return canonical != null && PayloadCmds.Contains(canonical);
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
        //   text/mem/goal/q/say="hi" → payload (echo/remember/goal/ask)
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
                case "remember": case "forget": case "goal": case "ask":
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
