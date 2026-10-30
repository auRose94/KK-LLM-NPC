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
                || cmd == "wc" || cmd == "sort" || cmd == "uniq"
                || cmd == "sed" || cmd == "cut" || cmd == "tr"
                || cmd == "nl" || cmd == "tac" || cmd == "pick" || cmd == "tee";
        }
        public static readonly string[] FilterNames =
        {
            "grep", "head", "tail", "wc", "sort", "uniq",
            "sed", "cut", "tr", "nl", "tac", "pick", "tee",
        };

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
        // sink: for `tee` — a side channel that RECEIVES the stream while tee passes
        // it on (the agent's scratch buffer). Never throws; unusable shapes answer
        // with a teaching line.
        public static string RunFilter(string cmd, string[] args, string stdinText)
        {
            return RunFilter(cmd, args, stdinText, null);
        }

        public static string RunFilter(string cmd, string[] args, string stdinText, Action<string> sink)
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
                    case "sed": return FilterSed(argv, lines);
                    case "cut": return FilterCut(argv, lines);
                    case "tr": return FilterTr(argv, lines);
                    case "nl": return FilterNl(lines);
                    case "tac": return FilterTac(lines);
                    case "pick": return FilterPick(argv, lines);
                    case "tee":
                        if (sink != null) sink(stdinText);
                        return stdinText;
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
            // Flags: separate (-r -n) or glued (-rn) — grep-style letter scan.
            bool reverse = false, numeric = false;
            if (argv != null)
                foreach (var a in argv)
                {
                    string t = a != null ? a.Trim() : "";
                    if (t.Length > 1 && t[0] == '-')
                    {
                        bool allFlags = true;
                        for (int k = 1; k < t.Length; k++) if (t[k] != 'r' && t[k] != 'n') { allFlags = false; break; }
                        if (allFlags)
                        {
                            if (t.IndexOf('r') >= 0) reverse = true;
                            if (t.IndexOf('n') >= 0) numeric = true;
                            continue;
                        }
                    }
                }
            var copy = new List<string>(lines);
            if (numeric)
            {
                // sort -n: by the leading number of each line (uniq -c output!),
                // lines without one sort as 0; ties break Ordinal.
                copy.Sort(delegate(string a, string b)
                {
                    double na = LeadingNumber(a), nb = LeadingNumber(b);
                    if (na != nb) return na < nb ? -1 : 1;
                    return string.CompareOrdinal(a, b);
                });
            }
            else copy.Sort(StringComparer.Ordinal);
            if (reverse)
            {
                var rev = new List<string>(copy.Count);
                for (int i = copy.Count - 1; i >= 0; i--) rev.Add(copy[i]);
                return string.Join("\n", rev.ToArray());
            }
            return string.Join("\n", copy.ToArray());
        }

        private static double LeadingNumber(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int i = 0;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            double v;
            if (i == start || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float,
                CultureInfo.InvariantCulture, out v)) return 0;
            return v;
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

        // sed 's/OLD/NEW/[g]' — LITERAL substitution (no regex: predictable for a
        // small model). Delimiter is whatever follows 's' (/ | , ; # _). Trailing
        // g = every occurrence on the line, else the first only.
        private static string FilterSed(List<string> argv, List<string> lines)
        {
            string expr = argv != null && argv.Count > 0 ? string.Join(" ", argv.ToArray()).Trim() : null;
            if (string.IsNullOrEmpty(expr) || expr.Length < 3
                || (expr[0] != 's' && expr[0] != 'S'))
                return "sed needs a substitution — sed 's/old/new/g' (g = every occurrence; quote it when the text has spaces)";
            char delim = expr[1];
            if (char.IsLetterOrDigit(delim) || delim == ' ')
                return "sed: pick a delimiter after 's' — sed 's/old/new/'";
            string body = expr.Substring(2);
            // Split into OLD / NEW / FLAGS on the delimiter (3 splits max).
            var parts = new List<string>();
            int start = 0;
            for (int i = 0; i <= body.Length && parts.Count < 3; i++)
            {
                if (i == body.Length || body[i] == delim)
                { parts.Add(body.Substring(start, i - start)); start = i + 1; }
            }
            if (parts.Count < 2)
                return "sed: use s/OLD/NEW/ — three delimiter-separated parts";
            string oldS = parts[0], newS = parts.Count > 1 ? parts[1] : "";
            string flags = parts.Count > 2 ? parts[2] : "";
            bool global = flags.IndexOf('g', StringComparison.OrdinalIgnoreCase) >= 0;
            if (oldS.Length == 0)
                return "sed: empty OLD — what should be replaced?";
            var outp = new List<string>(lines.Count);
            foreach (var l in lines)
            {
                if (global)
                {
                    var sb = new System.Text.StringBuilder(l.Length);
                    int i = 0;
                    while (i < l.Length)
                    {
                        int h = l.IndexOf(oldS, i, StringComparison.Ordinal);
                        if (h < 0) { sb.Append(l.Substring(i)); break; }
                        sb.Append(l, i, h - i).Append(newS);
                        i = h + oldS.Length;
                    }
                    outp.Add(sb.ToString());
                }
                else
                {
                    int h = l.IndexOf(oldS, StringComparison.Ordinal);
                    outp.Add(h < 0 ? l : l.Substring(0, h) + newS + l.Substring(h + oldS.Length));
                }
            }
            return string.Join("\n", outp.ToArray());
        }

        // cut -fN[-M][,K...] [-d<ch>] — field selection. Default delimiter is
        // whitespace runs (awk-like); -d picks a single char. Out-of-range fields
        // are skipped, like real cut.
        private static string FilterCut(List<string> argv, List<string> lines)
        {
            char delim = '\0';
            string spec = null;
            var rest = new List<string>();
            if (argv != null)
            {
                for (int i = 0; i < argv.Count; i++)
                {
                    string t = argv[i] != null ? argv[i].Trim() : "";
                    if (t.StartsWith("-d", StringComparison.Ordinal) && t.Length == 2 && i + 1 < argv.Count)
                    { delim = argv[i + 1].Length > 0 ? argv[i + 1][0] : ' '; i++; }
                    else if (t.StartsWith("-d", StringComparison.Ordinal) && t.Length > 2) delim = t[2];
                    else if (t.StartsWith("-f", StringComparison.Ordinal) && t.Length > 2) spec = t.Substring(2);
                    else if (t == "-f" && i + 1 < argv.Count) { spec = argv[i + 1].Trim(); i++; }
                    else rest.Add(argv[i]);
                }
            }
            if (string.IsNullOrEmpty(spec))
                return "cut needs fields — cut -f1 · cut -f2-3 · cut -d: -f1 (default delimiter is whitespace)";
            var want = new List<int>();
            foreach (var piece in spec.Split(','))
            {
                string p = piece.Trim();
                if (p.Length == 0) continue;
                int dash = p.IndexOf('-');
                if (dash > 0)
                {
                    int a, b;
                    if (int.TryParse(p.Substring(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture, out a)
                        && int.TryParse(p.Substring(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
                    {
                        // Clamp the range span — a hostile/errant "cut -f1-2147483647"
                        // would otherwise materialize ~2.1B ints before any line is read.
                        if (b < a) { a = 1; b = 1; }
                        if (b - a > 10000) b = a + 10000;
                        for (int n = a; n <= b; n++) want.Add(n);
                    }
                }
                else
                {
                    int a;
                    if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out a)) want.Add(a);
                }
            }
            if (want.Count == 0)
                return "cut: bad field spec '" + spec + "' — try -f1 or -f1,3-4";
            var outp = new List<string>(lines.Count);
            foreach (var l in lines)
            {
                string[] fields = delim == '\0'
                    ? l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                    : l.Split(delim);
                var sb = new System.Text.StringBuilder();
                foreach (var n in want)
                {
                    if (n < 1 || n > fields.Length) continue;
                    if (sb.Length > 0) sb.Append(delim == '\0' ? ' ' : delim);
                    sb.Append(fields[n - 1]);
                }
                outp.Add(sb.ToString());
            }
            return string.Join("\n", outp.ToArray());
        }

        // tr — single-char replace (tr X Y) or char deletion (tr -d <chars>).
        private static string FilterTr(List<string> argv, List<string> lines)
        {
            var rest = new List<string>();
            bool delete = false;
            if (argv != null)
                foreach (var a in argv)
                {
                    string t = a != null ? a.Trim() : "";
                    if (t == "-d") { delete = true; continue; }
                    rest.Add(a);
                }
            if (rest.Count == 0 || string.IsNullOrEmpty(rest[0]))
                return "tr needs chars — tr -d , (delete) · tr a b (replace every a with b)";
            if (delete)
            {
                string kill = string.Join("", rest.ToArray());
                var outp = new List<string>(lines.Count);
                foreach (var l in lines)
                {
                    var sb = new System.Text.StringBuilder(l.Length);
                    foreach (char c in l) if (kill.IndexOf(c) < 0) sb.Append(c);
                    outp.Add(sb.ToString());
                }
                return string.Join("\n", outp.ToArray());
            }
            if (rest[0].Length != 1)
                return "tr replaces ONE char — tr a b (or delete with: tr -d <chars>)";
            char from = rest[0][0];
            char to = rest.Count > 1 && !string.IsNullOrEmpty(rest[1]) && rest[1].Length >= 1 ? rest[1][0] : ' ';
            var outp2 = new List<string>(lines.Count);
            foreach (var l in lines) outp2.Add(l.Replace(from, to));
            return string.Join("\n", outp2.ToArray());
        }

        private static string FilterNl(List<string> lines)
        {
            var outp = new List<string>(lines.Count);
            for (int i = 0; i < lines.Count; i++)
                outp.Add((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(4) + "  " + lines[i]);
            return string.Join("\n", outp.ToArray());
        }

        private static string FilterTac(List<string> lines)
        {
            var outp = new List<string>(lines.Count);
            for (int i = lines.Count - 1; i >= 0; i--) outp.Add(lines[i]);
            return string.Join("\n", outp.ToArray());
        }

        // pick N — the single 1-based line N (the head/tail combo that was missing).
        private static string FilterPick(List<string> argv, List<string> lines)
        {
            int n = 0;
            if (argv != null && argv.Count > 0)
                int.TryParse(argv[argv.Count - 1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
            if (n < 1)
                return "pick needs a line number — pick 3 (1-based; pipe from nl if unsure)";
            if (n > lines.Count)
                return "pick: no line " + n.ToString(CultureInfo.InvariantCulture) + " (input has "
                    + lines.Count.ToString(CultureInfo.InvariantCulture) + " lines)";
            return lines[n - 1];
        }
    }
}