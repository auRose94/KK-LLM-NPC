// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// ConsolePipe — bash-like pipelines and sequencing for the agents' console, plus
// the stream filters. Models with shell training write real shells: `ps | grep
// k | head -n 5`, `cd kitchen && ls` — the console used to run such lines as one
// unknown command and the model fought the terminal forever. This file holds the
// PURE half: quote-aware stage splitting and the text filters. Splitting DECISIONS
// stay in ConsoleShell (they need the command table); the console turn loop in
// src/Console.cs executes the stages and wires stdin/stdout between them.
//
// Safety rules (why the splitting is picky):
//   * Payload commands (echo/remember/goal/plan/skill/...) keep their legacy
//     single-line behavior — their ';' and '|' are DATA (skill steps, prose facts),
//     never operators.
//   * Quotes are respected: a '|' or ';' inside quotes is text.
//   * Filters operate on whatever text the previous stage printed; they consume no
//     command budget (no game effect).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace KKLLMNPC
{
    internal static class ConsolePipe
    {
        // The stream filters (aliases canonicalized by the caller's table).
        public static bool IsFilter(string cmd)
        {
            if (cmd == null) return false;
            return cmd == "grep" || cmd == "head" || cmd == "tail"
                || cmd == "wc" || cmd == "sort" || cmd == "uniq";
        }
        public static readonly string[] FilterNames = { "grep", "head", "tail", "wc", "sort", "uniq" };

        public sealed class PipeStage
        {
            public string Raw;      // the stage text (trimmed)
            public bool PipeDown;   // this stage's output feeds the next stage
        }

        // Split a raw line into stages on top-level operators: '|' (pipe),
        // '&&' / ";" / "||" (sequence — consoles have no exit codes, both = run all).
        // Quotes keep their contents intact. PipeDown marks a stage whose output
        // feeds the following stage. Returns null when it's a single stage (not a
        // pipeline: fewer than two real stages).
        public static List<PipeStage> StagesOf(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var stages = new List<PipeStage>();
            var cur = new System.Text.StringBuilder();
            bool inQ = false;
            char qch = '"';
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (inQ)
                {
                    if (c == qch) inQ = false;
                    cur.Append(c);
                    continue;
                }
                if (c == '"' || c == '\'') { inQ = true; qch = c; cur.Append(c); continue; }
                bool seq = c == ';' || (c == '&' && i + 1 < raw.Length && raw[i + 1] == '&')
                    || (c == '|' && i + 1 < raw.Length && raw[i + 1] == '|');
                bool pipe = c == '|' && !seq;
                if (pipe || seq)
                {
                    if (seq && c != ';') i++; // consume the second & or |
                    FlushStage(stages, cur, pipe); // a pipe feeds the next stage; a sequence doesn't
                    continue;
                }
                if (c == '\t') cur.Append(' ');
                else cur.Append(c);
            }
            FlushStage(stages, cur, false);
            if (stages.Count < 2) return null;
            int real = 0;
            foreach (var s in stages) if (!string.IsNullOrEmpty(s.Raw)) real++;
            if (real < 2) return null;
            return stages;
        }

        // Trimmed stage → list (empty stages never become lines).
        private static void FlushStage(List<PipeStage> stages, System.Text.StringBuilder cur, bool pipeDown)
        {
            string text = cur.ToString().Trim();
            cur.Clear();
            if (text.Length == 0) return;
            stages.Add(new PipeStage { Raw = text, PipeDown = pipeDown });
        }

        // ---- the filters -------------------------------------------------
        // stdinText: the previous stage's full output text (null = no pipe input).
        // Never throws; unusable shapes answer with a teaching line.
        public static string RunFilter(string cmd, string[] args, string stdinText)
        {
            List<string> argv = new List<string>();
            if (args != null) foreach (var a in args) if (!string.IsNullOrEmpty(a)) argv.Add(a);
            if (stdinText == null)
                return "a filter needs pipe input — put a read command before it: ls | " + (cmd ?? "filter");
            List<string> lines = SplitLines(stdinText);
            try
            {
                switch (cmd)
                {
                    case "grep": return FilterGrep(argv, lines);
                    case "head": return FilterHeadOrTail(argv, lines, true);
                    case "tail": return FilterHeadOrTail(argv, lines, false);
                    case "wc": return FilterWc(argv, lines);
                    case "sort": return FilterSort(argv, lines);
                    case "uniq": return FilterUniq(argv, lines);
                    default: return "unknown filter '" + cmd + "'";
                }
            }
            catch (Exception e)
            {
                return "filter error: " + e.Message;
            }
        }

        private static List<string> SplitLines(string text)
        {
            var outp = new List<string>();
            if (text == null) return outp;
            var parts = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                // drop the trailing newline artifact (after the final line)
                if (i == parts.Length - 1 && parts[i].Length == 0) break;
                outp.Add(parts[i]);
            }
            return outp;
        }

        private static string Flag(List<string> argv, string shortFlag)
        {
            if (argv == null) return null;
            for (int i = 0; i < argv.Count; i++)
                if (argv[i] != null && argv[i].Trim() == shortFlag) return shortFlag;
            return null;
        }

        private static int FlagInt(List<string> argv, string shortFlag, int def, out List<string> rest)
        {
            int n = def;
            rest = new List<string>();
            if (argv == null) return n;
            for (int i = 0; i < argv.Count; i++)
            {
                string a = argv[i];
                if (a == null) continue;
                string t = a.Trim();
                if (t == shortFlag)
                {
                    if (i + 1 < argv.Count)
                    {
                        int v;
                        if (int.TryParse(argv[i + 1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) n = v;
                        i++;
                        continue;
                    }
                    continue;
                }
                // "-n10" / "-n=10" glued forms
                if (t.StartsWith(shortFlag, StringComparison.Ordinal))
                {
                    string num = t.Substring(shortFlag.Length).TrimStart('=');
                    int v;
                    if (num.Length > 0 && int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) n = v;
                    continue;
                }
                rest.Add(a);
            }
            return n;
        }

        private static string FilterGrep(List<string> argv, List<string> lines)
        {
            bool ci = false, invert = false, count = false;
            var rest = new List<string>();
            if (argv != null)
            {
                for (int i = 0; i < argv.Count; i++)
                {
                    string t = argv[i] != null ? argv[i].Trim() : null;
                    if (t != null && t.Length > 1 && t[0] == '-')
                    {
                        bool allFlags = true;
                        for (int k = 1; k < t.Length; k++) if (t[k] != 'i' && t[k] != 'v' && t[k] != 'c') { allFlags = false; break; }
                        if (allFlags)
                        {
                            if (t.IndexOf('i') >= 0) ci = true;
                            if (t.IndexOf('v') >= 0) invert = true;
                            if (t.IndexOf('c') >= 0) count = true;
                            continue;
                        }
                    }
                    rest.Add(argv[i]);
                }
            }
            string pattern = rest.Count > 0 ? string.Join(" ", rest.ToArray()) : null;
            if (string.IsNullOrEmpty(pattern))
                return "grep needs a text to filter for — grep <text>";
            string cmp = ci ? pattern.ToLowerInvariant() : pattern;
            int hits = 0;
            var outp = new List<string>();
            foreach (var l in lines)
            {
                string hay = ci ? l.ToLowerInvariant() : l;
                bool has = hay.IndexOf(cmp, ci ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) >= 0;
                if (has != invert) { hits++; if (!count) outp.Add(l); }
            }
            if (count) return hits.ToString(CultureInfo.InvariantCulture);
            return string.Join("\n", outp.ToArray());
        }

        private static string FilterHeadOrTail(List<string> argv, List<string> lines, bool head)
        {
            List<string> rest;
            int n = FlagInt(argv, "-n", 10, out rest);
            // bare positional number: head 5
            List<string> rest2 = new List<string>();
            foreach (var a in rest)
            {
                int v;
                if (int.TryParse(a.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) n = v;
                else rest2.Add(a);
            }
            if (n < 0) n = 0;
            if (n > lines.Count) n = lines.Count;
            var outp = new List<string>(n);
            if (head) for (int i = 0; i < n; i++) outp.Add(lines[i]);
            else for (int i = lines.Count - n; i < lines.Count; i++) outp.Add(lines[i]);
            return string.Join("\n", outp.ToArray());
        }

        private static string FilterWc(List<string> argv, List<string> lines)
        {
            int words = 0, chars = 0;
            foreach (var l in lines)
            {
                chars += l.Length + 1;
                bool inWord = false;
                for (int i = 0; i < l.Length; i++)
                {
                    bool ws = char.IsWhiteSpace(l[i]);
                    if (!ws && !inWord) { words++; inWord = true; }
                    else if (ws) inWord = false;
                }
            }
            foreach (var a in argv)
            {
                string t = a != null ? a.Trim() : "";
                if (t == "-w") return words.ToString(CultureInfo.InvariantCulture);
                if (t == "-c") return chars.ToString(CultureInfo.InvariantCulture);
                if (t == "-l") return lines.Count.ToString(CultureInfo.InvariantCulture);
            }
            return lines.Count.ToString(CultureInfo.InvariantCulture);
        }

        private static string FilterSort(List<string> argv, List<string> lines)
        {
            bool reverse = Flag(argv, "-r") != null;
            var copy = new List<string>(lines);
            copy.Sort(StringComparer.Ordinal);
            if (reverse)
            {
                var rev = new List<string>(copy.Count);
                for (int i = copy.Count - 1; i >= 0; i--) rev.Add(copy[i]);
                return string.Join("\n", rev.ToArray());
            }
            return string.Join("\n", copy.ToArray());
        }

        private static string FilterUniq(List<string> argv, List<string> lines)
        {
            bool counts = Flag(argv, "-c") != null;
            var outp = new List<string>();
            int run = 0;
            string prev = null;
            foreach (var l in lines)
            {
                if (prev != null && l == prev) { run++; continue; }
                if (prev != null)
                    outp.Add(counts ? run.ToString(CultureInfo.InvariantCulture) + " " + prev : prev);
                prev = l;
                run = 1;
            }
            if (prev != null)
                outp.Add(counts ? run.ToString(CultureInfo.InvariantCulture) + " " + prev : prev);
            return string.Join("\n", outp.ToArray());
        }
    }
}