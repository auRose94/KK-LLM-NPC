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
            && ConsolePipe.IsFilter("sort") && ConsolePipe.IsFilter("uniq"), "filters recognized");
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

        if (_failed == 0) Console.WriteLine("test_consolepipe: all OK");
        else Console.WriteLine("test_consolepipe: " + _failed + " failure(s)");
        Environment.Exit(_failed);
    }
}