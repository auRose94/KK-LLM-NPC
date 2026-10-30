// Unit tests for the console pipelines: quote-aware stage splitting (| ; && ||),
// the pipe-explosion rules (payload-command lines stay whole, every stage must be
// a known command), and the stream filters (grep/head/tail/wc/sort/uniq).
// Run via run_tests.sh (suite "consolepipe").
using System;
using System.Collections.Generic;
using KKLLMNPC;

internal static class TestConsolePipe
{
    private static int _failed;

    private static void Eq(object expect, object got, string what)
    {
        if (!string.Equals(Convert.ToString(expect), Convert.ToString(got), StringComparison.Ordinal))
        {
            _failed++;
            Console.WriteLine("FAIL " + what + ": expected '" + expect + "' got '" + got + "'");
        }
    }

    private static void True(bool b, string what)
    {
        if (!b) { _failed++; Console.WriteLine("FAIL " + what); }
    }

    private static string StagesStr(string raw)
    {
        var s = ConsolePipe.StagesOf(raw);
        if (s == null) return "(none)";
        var parts = new List<string>();
        foreach (var t in s) parts.Add(t.Raw + (t.PipeDown ? ">" : ";"));
        return string.Join(" | ", parts.ToArray());
    }

    public static void Main()
    {
        // --- stage splitting: pipes, sequences, quotes ---
        Eq("ps> | grep k> | head -n 3;", StagesStr("ps | grep k | head -n 3"),
            "three pipe stages, pipe flags on the feeders");
        Eq("cd kitchen; | ls;", StagesStr("cd kitchen && ls"), "&& sequences without pipes");
        Eq("a; | b; | c;", StagesStr("a; b; c"), "semicolons sequence");
        Eq("echo \"a | b\"> | wc;", StagesStr("echo \"a | b\" | wc"), "quoted pipe is text, outer pipe splits");
        Eq("(none)", StagesStr("ps arg"), "no operator: not a pipeline");
        Eq("(none)", StagesStr(""), "empty: not a pipeline");
        Eq("(none)", StagesStr("ps |"), "dangling pipe: not a pipeline");
        Eq("a; | b;", StagesStr("a; ; b"), "empty middle stage dropped, real stages kept");
        Eq("ps> | grep  k;", StagesStr("ps |  grep  k "), "stage text trimmed");

        // --- filter set ---
        True(ConsolePipe.IsFilter("grep") && ConsolePipe.IsFilter("head")
            && ConsolePipe.IsFilter("tail") && ConsolePipe.IsFilter("wc")
            && ConsolePipe.IsFilter("sort") && ConsolePipe.IsFilter("uniq")
            && ConsolePipe.IsFilter("sed") && ConsolePipe.IsFilter("cut")
            && ConsolePipe.IsFilter("tr") && ConsolePipe.IsFilter("nl")
            && ConsolePipe.IsFilter("tac") && ConsolePipe.IsFilter("pick")
            && ConsolePipe.IsFilter("tee"), "filters recognized");
        True(!ConsolePipe.IsFilter("ps") && !ConsolePipe.IsFilter(null), "game commands are not filters");

        // --- pipeline explosion rules (needs the command table) ---
        var chain = ConsoleShell.ExplodePipeline("ps | grep k | head -n 3");
        Eq(3, chain == null ? -1 : chain.Count, "ps|grep|head explodes to 3 stages");
        Eq("ps", chain != null && chain.Count > 0 ? chain[0].Cmd : "?", "first stage = ps");
        Eq(true, chain != null && chain[0].PipeDown, "ps pipes down");
        Eq(true, chain != null && chain[1].Cmd == "grep" && chain[1].IsFilter, "grep is a filter stage");
        Eq(true, chain != null && chain.Count > 1 && chain[1].PipeDown, "grep pipes down");
        Eq(false, chain != null && chain.Count > 2 && chain[2].PipeDown, "last stage never pipes down");

        Eq("(none)", ConsoleShell.ExplodePipeline("echo hi | wc") == null ? "(none)" : "exploded",
            "payload-command lines never split ('|' is data)");
        Eq("(none)", ConsoleShell.ExplodePipeline("skill add garden | cd id:1; use id:1") == null ? "(none)" : "exploded",
            "skill steps stay whole");
        Eq("(none)", ConsoleShell.ExplodePipeline("ps | wibble") == null ? "(none)" : "exploded",
            "unknown stage: run the line unsplit");
        Eq("(none)", ConsoleShell.ExplodePipeline("ls") == null ? "(none)" : "exploded", "single command: no explode");

        var seq = ConsoleShell.ExplodePipeline("cd kitchen && ps");
        Eq(2, seq == null ? -1 : seq.Count, "cd && ps explodes in order");
        Eq(true, seq != null && seq[0].Cmd == "cd" && !seq[0].IsFilter && !seq[0].PipeDown, "cd is a real command, no pipe");
        Eq(true, seq != null && seq[1].Cmd == "ps", "ps after &&");

        var mixed = ConsoleShell.ExplodePipeline("ps | grep y | wc");
        Eq(3, mixed == null ? -1 : mixed.Count, "read commands pipe together");

        // --- the filters ---
        string inp = "kobold near\na rock\nKobold big\n";
        Eq("kobold near", ConsolePipe.RunFilter("grep", new[] { "kobold" }, inp), "grep default case-sensitive");
        Eq("kobold near\nKobold big", ConsolePipe.RunFilter("grep", new[] { "-i", "kobold" }, inp), "grep -i");
        Eq("a rock\nKobold big", ConsolePipe.RunFilter("grep", new[] { "-v", "kobold" }, inp), "grep -v");
        Eq("2", ConsolePipe.RunFilter("grep", new[] { "-ic", "kobold" }, inp), "grep -ic counts matches");
        Eq("grep needs a text to filter for — grep <text>", ConsolePipe.RunFilter("grep", new string[0], inp), "grep without a pattern teaches");
        Eq("kobold near\na rock\nKobold big", ConsolePipe.RunFilter("head", new string[0], inp), "head default 10 (all 3 here)");
        Eq("kobold near", ConsolePipe.RunFilter("head", new[] { "-n", "1" }, inp), "head -n 1 = the first line");
        Eq("Kobold big", ConsolePipe.RunFilter("tail", new[] { "-n", "1" }, inp), "tail -n 1 = the last line");
        Eq("3", ConsolePipe.RunFilter("wc", new string[0], inp), "wc counts lines");
        Eq("6", ConsolePipe.RunFilter("wc", new[] { "-w" }, inp), "wc -w counts words (2+2+2)");
        Eq("walnut\nwobble", ConsolePipe.RunFilter("sort", new string[0], "walnut\nwobble"), "sort ascending");
        Eq("wobble\nwalnut", ConsolePipe.RunFilter("sort", new[] { "-r" }, "walnut\nwobble"), "sort -r");
        Eq("apple\npear\napple", ConsolePipe.RunFilter("uniq", new string[0], "apple\napple\npear\napple"), "uniq collapses ADJACENT dups only (like bash)");
        Eq("2 apple\n1 pear\n1 apple", ConsolePipe.RunFilter("uniq", new[] { "-c" }, "apple\napple\npear\napple"), "uniq -c counts");
        Eq(true, ConsolePipe.RunFilter("grep", new[] { "x" }, null).IndexOf("needs pipe input", StringComparison.Ordinal) >= 0,
            "filters without a pipe teach the shape");

        // --- the powertools: sed / cut / tr / nl / tac / pick / sort -n / tee ---
        string body = "kobold near the door\na rock\nKobold big and round\n";
        Eq("kobold near the gate\na rock\nKobold big and round",
            ConsolePipe.RunFilter("sed", new[] { "s/door/gate/" }, body), "sed substitutes first occurrence per line");
        Eq("kobold near the gate\na rock\nKobold big and round",
            ConsolePipe.RunFilter("sed", new[] { "s/door/gate/g" }, body), "sed /g replaces every occurrence");
        Eq("kobold xear the door\na rock\nKobold big axd round",
            ConsolePipe.RunFilter("sed", new[] { "s/n/x/" }, body), "sed without /g = first occurrence PER LINE");
        Eq("and now this\na rock\nKobold big and round",
            ConsolePipe.RunFilter("sed", new[] { "s/kobold near the door/and now this/" }, body),
            "sed can rewrite a whole line");
        Eq(true, ConsolePipe.RunFilter("sed", new[] { "oops" }, body).IndexOf("needs a substitution", StringComparison.Ordinal) >= 0,
            "sed without s/// teaches");
        Eq(true, ConsolePipe.RunFilter("sed", new[] { "s//x/" }, body).IndexOf("empty OLD", StringComparison.Ordinal) >= 0,
            "sed with empty OLD teaches");
        Eq("kobold near\nx y", ConsolePipe.RunFilter("cut", new[] { "-f", "1-2" }, "kobold near the door\nx y z"), "cut whitespace fields 1-2");
        Eq("z\ne", ConsolePipe.RunFilter("cut", new[] { "-f2", "-f3" }, "kobold near z\nq w e r"), "cut keeps the last -f spec (like real cut)");
        Eq("the door", ConsolePipe.RunFilter("cut", new[] { "-d", " ", "-f3-4" }, "kobold near the door"), "cut -d space with range");
        Eq("a", ConsolePipe.RunFilter("cut", new[] { "-d:", "-f2" }, "x:a:b"), "cut -d: single char delim");
        Eq(true, ConsolePipe.RunFilter("cut", new string[0], body).IndexOf("needs fields", StringComparison.Ordinal) >= 0,
            "cut without -f teaches");
        Eq("obold near the door\na rock", ConsolePipe.RunFilter("tr", new[] { "-d", "K" }, "Kobold near the door\na rock"), "tr -d deletes chars");
        Eq("kobold near the door", ConsolePipe.RunFilter("tr", new[] { "K", "k" }, "Kobold near the door"), "tr X Y replaces one char");
        Eq("   1  a\n   2  b", ConsolePipe.RunFilter("nl", new string[0], "a\nb"), "nl numbers lines");
        Eq("b\na", ConsolePipe.RunFilter("tac", new string[0], "a\nb"), "tac reverses line order");
        Eq("Kobold big and round", ConsolePipe.RunFilter("pick", new[] { "3" }, body), "pick grabs line N");
        Eq(true, ConsolePipe.RunFilter("pick", new[] { "9" }, body).IndexOf("no line 9", StringComparison.Ordinal) >= 0,
            "pick out of range teaches");
        Eq("1 apple\n1 pear\n2 apple", ConsolePipe.RunFilter("sort", new[] { "-n" }, "2 apple\n1 pear\n1 apple"), "sort -n numeric by leading number");
        Eq("2 apple\n1 pear\n1 apple", ConsolePipe.RunFilter("sort", new[] { "-rn" }, "2 apple\n1 pear\n1 apple"), "sort -rn reverse numeric");
        // tee: passes the stream through unchanged AND hands it to the sink.
        string teed = null;
        string teeOut = ConsolePipe.RunFilter("tee", new string[0], "a\nb", delegate(string s) { teed = s; });
        Eq("a\nb", teeOut, "tee passes the stream through");
        Eq("a\nb", teed, "tee hands the stream to the sink");
        Eq("a\nb", ConsolePipe.RunFilter("tee", new string[0], "a\nb"), "tee without a sink still passes through");

        if (_failed == 0) Console.WriteLine("test_consolepipe: all OK");
        else Console.WriteLine("test_consolepipe: " + _failed + " failure(s)");
        Environment.Exit(_failed);
    }
}