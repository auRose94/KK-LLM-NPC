// Calc — tiny pure arithmetic evaluator for the agents' `calc` console command.
// Pure C# (no Unity/BepInEx deps), unit-testable with the shell suite.
//
// Why: models must do coordinate/vector math (remembered place: facts, sonar
// coords, growth timers) but small models are unreliable at mental arithmetic —
// a calculator makes the numbers trustworthy. NOT a script language: numbers,
// + - * / %, parentheses, unary +/-, and a few functions. Everything else is a
// teaching error. Grammar (recursive descent, no regex, depth-capped):
//   expr    := term (('+'|'-') term)*
//   term    := factor (('*'|'/'|'%') factor)*
//   factor  := ('+'|'-') factor | primary
//   primary := number | name '(' args ')' | '(' expr ')'
using System;
using System.Globalization;

namespace KKLLMNPC
{
    internal static class Calc
    {
        // Evaluate one expression. Returns the FORMATTED result; err != null on a
        // bad expression (return value is then null).
        public static string Evaluate(string expr, out string err)
        {
            err = null;
            if (string.IsNullOrWhiteSpace(expr))
            {
                err = "what should I compute? e.g. calc (12-4)*3 · calc sqrt(2) · calc min(3,9)";
                return null;
            }
            string s = expr.Trim();
            if (s.Length > 200)
            {
                err = "expression too long (max 200 chars)";
                return null;
            }
            // Model-friendly normalizations: × ÷, and "x"/"X" between digits means
            // multiply ("12x3"), never identifiers (there are none — names are
            // function words inside parens).
            s = s.Replace('×', '*').Replace('÷', '/');
            var norm = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if ((c == 'x' || c == 'X') && i > 0 && i + 1 < s.Length
                    && (char.IsDigit(s[i - 1]) || s[i - 1] == ')')
                    && (char.IsDigit(s[i + 1]) || s[i + 1] == '('))
                { norm.Append('*'); continue; }
                norm.Append(c);
            }
            var p = new Parser(norm.ToString());
            double v;
            try { v = p.ParseExpr(0); }
            catch (FormatException fe)
            {
                err = fe.Message;
                return null;
            }
            p.SkipWs();
            if (p.Pos < p.Src.Length)
            {
                err = "unexpected '" + p.Src[p.Pos] + "' — e.g. calc (12-4)*3 · calc sqrt(2) · calc min(3,9)";
                return null;
            }
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                err = "result is not a finite number";
                return null;
            }
            return Format(v);
        }

        public static string Format(double v)
        {
            // Integers print bare ("7"); otherwise up to 6 decimals, trailing
            // zeros trimmed ("3.5", "0.333333"). Invariant only.
            if (v == Math.Round(v) && Math.Abs(v) < 1e15)
                return ((long)v).ToString(CultureInfo.InvariantCulture);
            string r = v.ToString("0.######", CultureInfo.InvariantCulture);
            return r.Length == 0 ? "0" : r;
        }

        private sealed class Parser
        {
            public readonly string Src;
            public int Pos;

            public Parser(string src) { Src = src; }

            public void SkipWs()
            {
                while (Pos < Src.Length && (Src[Pos] == ' ' || Src[Pos] == '\t')) Pos++;
            }

            private void Fail(string why)
            {
                throw new FormatException(why);
            }

            public double ParseExpr(int depth)
            {
                if (++depth > 60) Fail("expression too deeply nested");
                double v = ParseTerm(depth);
                while (true)
                {
                    SkipWs();
                    if (Pos >= Src.Length) return v;
                    char c = Src[Pos];
                    if (c != '+' && c != '-') return v;
                    Pos++;
                    double r = ParseTerm(depth);
                    v = c == '+' ? v + r : v - r;
                }
            }

            private double ParseTerm(int depth)
            {
                double v = ParseFactor(depth);
                while (true)
                {
                    SkipWs();
                    if (Pos >= Src.Length) return v;
                    char c = Src[Pos];
                    if (c != '*' && c != '/' && c != '%') return v;
                    Pos++;
                    double r = ParseFactor(depth);
                    if (c == '*') v = v * r;
                    else if (c == '/')
                    {
                        if (r == 0) Fail("division by zero");
                        v = v / r;
                    }
                    else
                    {
                        if (r == 0) Fail("modulo by zero");
                        v = v % r;
                    }
                }
            }

            private double ParseFactor(int depth)
            {
                if (++depth > 60) Fail("expression too deeply nested");
                SkipWs();
                if (Pos >= Src.Length) Fail("expression ends too soon");
                char c = Src[Pos];
                if (c == '+' || c == '-')
                {
                    Pos++;
                    double v = ParseFactor(depth);
                    return c == '-' ? -v : v;
                }
                return ParsePrimary(depth);
            }

            private double ParsePrimary(int depth)
            {
                if (++depth > 60) Fail("expression too deeply nested");
                SkipWs();
                if (Pos >= Src.Length) Fail("expression ends too soon");
                char c = Src[Pos];
                if (c == '(')
                {
                    Pos++;
                    double v = ParseExpr(depth);
                    SkipWs();
                    if (Pos >= Src.Length || Src[Pos] != ')') Fail("missing ')'");
                    Pos++;
                    return v;
                }
                if (char.IsDigit(c) || c == '.')
                {
                    int start = Pos;
                    while (Pos < Src.Length && (char.IsDigit(Src[Pos]) || Src[Pos] == '.')) Pos++;
                    double v;
                    if (!double.TryParse(Src.Substring(start, Pos - start), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out v))
                        Fail("bad number '" + Src.Substring(start, Pos - start) + "'");
                    return v;
                }
                if (char.IsLetter(c))
                {
                    int start = Pos;
                    while (Pos < Src.Length && char.IsLetter(Src[Pos])) Pos++;
                    string name = Src.Substring(start, Pos - start).ToLowerInvariant();
                    SkipWs();
                    if (Pos >= Src.Length || Src[Pos] != '(')
                        Fail("unknown '" + name + "' — functions: sqrt abs round floor ceil min max");
                    Pos++;
                    double first = ParseExpr(depth);
                    if (name == "sqrt")
                    {
                        if (first < 0) Fail("sqrt of a negative");
                        ExpectClose();
                        return Math.Sqrt(first);
                    }
                    if (name == "abs") { ExpectClose(); return Math.Abs(first); }
                    if (name == "round") { ExpectClose(); return Math.Round(first); }
                    if (name == "floor") { ExpectClose(); return Math.Floor(first); }
                    if (name == "ceil") { ExpectClose(); return Math.Ceiling(first); }
                    if (name == "min" || name == "max")
                    {
                        double best = first;
                        while (true)
                        {
                            SkipWs();
                            if (Pos < Src.Length && Src[Pos] == ',')
                            {
                                Pos++;
                                double next = ParseExpr(depth);
                                best = name == "min" ? Math.Min(best, next) : Math.Max(best, next);
                                continue;
                            }
                            break;
                        }
                        ExpectClose();
                        return best;
                    }
                    Fail("unknown function '" + name + "' — sqrt abs round floor ceil min max");
                }
                Fail("unexpected '" + c + "' — e.g. calc (12-4)*3 · calc sqrt(2) · calc min(3,9)");
                return 0; // unreachable
            }

            private void ExpectClose()
            {
                SkipWs();
                if (Pos >= Src.Length || Src[Pos] != ')') Fail("missing ')'");
                Pos++;
            }
        }
    }
}
