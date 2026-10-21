// Tests for ConsoleShell (bash-like command vocabulary + line/reply parsing).
// Pure C# — no Unity deps.
// Build: mcs -target:exe -out:/tmp/t_cs.exe tests/test_console_shell.cs src/ConsoleShell.cs && mono /tmp/t_cs.exe
using System;
using System.Collections.Generic;
using KKLLMNPC;

static class ConsoleShellTests
{
    static int _passed, _failed;

    static void Assert(bool cond, string name)
    {
        if (cond) { _passed++; Console.WriteLine("  PASS: " + name); }
        else { _failed++; Console.WriteLine("  FAIL: " + name); }
    }

    // ---- Canonical: exact + aliases + fuzzy ----

    static void CanonicalExact()
    {
        Assert(ConsoleShell.Canonical("ls") == "ls", "exact ls");
        Assert(ConsoleShell.Canonical("cd") == "cd", "exact cd");
        Assert(ConsoleShell.Canonical("echo") == "echo", "exact echo");
        Assert(ConsoleShell.Canonical("help") == "help", "exact help");
    }

    static void CanonicalCaseAndPunctuation()
    {
        Assert(ConsoleShell.Canonical("LS") == "ls", "case-insensitive LS");
        Assert(ConsoleShell.Canonical("cd.") == "cd", "trailing dot stripped");
        Assert(ConsoleShell.Canonical(" echo ") == "echo", "spaces trimmed");
    }

    static void CanonicalLegacyAliases()
    {
        // old act-schema tool names must land on the console commands
        Assert(ConsoleShell.Canonical("say") == "echo", "say -> echo");
        Assert(ConsoleShell.Canonical("go_to") == "cd", "go_to -> cd");
        Assert(ConsoleShell.Canonical("interact") == "use", "interact -> use");
        Assert(ConsoleShell.Canonical("walk") == "run", "walk -> run");
        Assert(ConsoleShell.Canonical("exit_station") == "exit", "exit_station -> exit");
        Assert(ConsoleShell.Canonical("grab") == "get", "grab -> get");
        Assert(ConsoleShell.Canonical("set_goal") == "goal", "set_goal -> goal");
        Assert(ConsoleShell.Canonical("look_around") == "look", "look_around -> look");
        Assert(ConsoleShell.Canonical("survey") == "find", "survey -> find");
        Assert(ConsoleShell.Canonical("none") == "sleep", "none -> sleep");
        Assert(ConsoleShell.Canonical("wait") == "sleep", "wait -> sleep");
        Assert(ConsoleShell.Canonical("stat") == "status", "stat -> status");
        Assert(ConsoleShell.Canonical("who") == "ps", "who -> ps");
        Assert(ConsoleShell.Canonical("shot") == "screenshot", "shot -> screenshot");
        Assert(ConsoleShell.Canonical("sonar") == "sonar", "sonar exact");
        Assert(ConsoleShell.Canonical("radar") == "sonar", "radar -> sonar");
        Assert(ConsoleShell.Canonical("report") == "report", "report exact");
        Assert(ConsoleShell.Canonical("report_issue") == "report", "report_issue -> report");
        Assert(ConsoleShell.Canonical("complain") == "report", "complain -> report");
    }

    static void CanonicalFuzzy()
    {
        Assert(ConsoleShell.Canonical("lss") == "ls", "fuzzy lss -> ls");
        Assert(ConsoleShell.Canonical("ech") == "echo", "fuzzy ech -> echo");
        Assert(ConsoleShell.Canonical("cdd") == "cd", "fuzzy cdd -> cd");
        Assert(ConsoleShell.Canonical("remember") == "remember", "remember exact");
        Assert(ConsoleShell.Canonical("remembe") == "remember", "fuzzy remebe -> remember");
        Assert(ConsoleShell.Canonical("remmber") == "remember", "alias remmber -> remember");
    }

    // prose guard: common sentence-starters must NOT become commands
    static void ProseGuard()
    {
        Assert(ConsoleShell.Canonical("I") == null, "'I' is not a command");
        Assert(ConsoleShell.Canonical("ok") == null, "'ok' is not a command");
        Assert(ConsoleShell.Canonical("let") == null, "'let' is not a command");
        Assert(ConsoleShell.Canonical("the") == null, "'the' is not a command");
        Assert(ConsoleShell.Canonical("want") == null, "'want' is not a command");
        Assert(ConsoleShell.Canonical("hello") == null, "'hello' is not a command");
    }

    static void CanonicalUnknown()
    {
        Assert(ConsoleShell.Canonical("frobnicate") == null, "unknown word -> null");
        Assert(ConsoleShell.Canonical("") == null, "empty -> null");
        Assert(ConsoleShell.Canonical(null) == null, "null -> null");
    }

    // prose guard 2: words common at the start of a prose sentence must NOT map to
    // the report command — thinking out loud should never file a dev bug note
    static void ProseGuardReport()
    {
        Assert(ConsoleShell.Canonical("problem") == null, "'problem' is not a command");
        Assert(ConsoleShell.Canonical("issue") == null, "'issue' is not a command");
        Assert(ConsoleShell.Canonical("bug") == null, "'bug' is not a command");
        Assert(ConsoleShell.Canonical("feedback") == null, "'feedback' is not a command");
    }

    static void ParseSonarArgs()
    {
        var l = ConsoleShell.Parse("sonar U");
        Assert(l.Cmd == "sonar" && l.Args.Length == 1 && l.Args[0] == "U", "sonar U arg");
        var ls = ConsoleShell.Parse("sonar U,P");
        Assert(ls.Cmd == "sonar" && ls.Args.Length == 1 && ls.Args[0] == "U,P", "sonar U,P single token (filter splits commas)");
        var lk = ConsoleShell.Parse("radar(filter=\"U,P\")");
        Assert(lk.Cmd == "sonar" && lk.Args.Length == 1 && lk.Args[0] == "U,P",
            "radar(filter=\"U,P\") quoted keeps the comma list");
        var la = ConsoleShell.Parse("sonar all");
        Assert(la.Cmd == "sonar" && la.Args.Length == 1 && la.Args[0] == "all", "sonar all resets");
    }

    static void ParseReportPayload()
    {
        var l = ConsoleShell.Parse("report sonar shows garbage after turning");
        Assert(l.Cmd == "report" && l.Payload == "sonar shows garbage after turning", "report keeps payload");
        var c = ConsoleShell.Parse("report_issue(cd keeps failing on the blender)");
        Assert(c.Cmd == "report" && c.Payload == "cd keeps failing on the blender", "report_issue(...) payload");
        Assert(ConsoleShell.IsPayloadCommand("report"), "report is a payload command");
    }

    // ---- IsCommandWord ----

    static void IsCommandWordTest()
    {
        Assert(ConsoleShell.IsCommandWord("ls"), "ls is a command word");
        Assert(ConsoleShell.IsCommandWord("say"), "say (alias) is a command word");
        Assert(!ConsoleShell.IsCommandWord("hello"), "hello is not a command word");
        Assert(!ConsoleShell.IsCommandWord(""), "empty is not a command word");
    }

    // ---- ParseLine ----

    static void ParseLineBasic()
    {
        var l = ConsoleShell.Parse("cd bed");
        Assert(l != null && l.Cmd == "cd", "cd bed: cmd");
        Assert(l.Args.Length == 1 && l.Args[0] == "bed", "cd bed: arg");
    }

    static void ParseLinePromptPrefixes()
    {
        Assert(ConsoleShell.Parse("$ cd bed").Cmd == "cd", "$ prompt prefix");
        Assert(ConsoleShell.Parse("> echo hi").Cmd == "echo", "> prompt prefix");
        Assert(ConsoleShell.Parse("- ls").Cmd == "ls", "bullet prefix");
        Assert(ConsoleShell.Parse("* cd kitchen").Cmd == "cd", "star bullet prefix");
        Assert(ConsoleShell.Parse("• status").Cmd == "status", "round bullet prefix");
        Assert(ConsoleShell.Parse("# cat facts").Cmd == "cat", "hash prefix");
        // transcript-echo artifacts: bare prompt lines must vanish (not "unknown")
        Assert(ConsoleShell.Parse("$") == null, "bare $ vanishes");
        Assert(ConsoleShell.Parse("$ $") == null, "bare $$ vanishes");
        Assert(ConsoleShell.Parse(">") == null, "bare > vanishes");
        Assert(ConsoleShell.Parse("#") == null, "bare # vanishes");
        Assert(ConsoleShell.Parse("$ ") == null, "bare $ space vanishes");
    }

    static void ParseLineHeaders()
    {
        Assert(ConsoleShell.Parse("command: cd bed").Cmd == "cd", "command: header");
        Assert(ConsoleShell.Parse("action: go_to(name=\"bed\")").Cmd == "cd", "action: header + legacy call");
    }

    static void ParseLinePayload()
    {
        var l = ConsoleShell.Parse("echo Hi there, friend!");
        Assert(l.Cmd == "echo", "echo cmd");
        Assert(l.Payload == "Hi there, friend!", "echo payload raw: '" + (l.Payload ?? "null") + "'");
        var r = ConsoleShell.Parse("remember bed is upstairs, near the door");
        Assert(r.Payload == "bed is upstairs, near the door", "remember payload");
        var g = ConsoleShell.Parse("goal find a nest for the eggs");
        Assert(g.Payload == "find a nest for the eggs", "goal payload");
    }

    static void ParseLineQuoted()
    {
        var l = ConsoleShell.Parse("echo \"hello world\" extra");
        Assert(l.Args.Length == 2, "quoted echo arg count: " + l.Args.Length);
        Assert(l.Args[0] == "hello world", "quoted echo arg content");
    }

    static void ParseLineGoalSubcommands()
    {
        Assert(ConsoleShell.Parse("goal done").Args[0] == "done", "goal done arg");
        var l = ConsoleShell.Parse("goal drop too hard");
        Assert(l.Args[0] == "drop" && l.Payload == "drop too hard", "goal drop args");
    }

    // ---- legacy call-style args ----

    static void ParseLineCallStyle()
    {
        var l = ConsoleShell.Parse("cd(name=\"BedStation\")");
        Assert(l.Cmd == "cd" && l.Args.Length == 1 && l.Args[0] == "BedStation", "cd(name=...) → cd BedStation");

        var u = ConsoleShell.Parse("use(id=3)");
        Assert(u.Cmd == "use" && u.Args[0] == "id:3", "use(id=3) → use id:3");

        var e = ConsoleShell.Parse("echo(\"hi there\")");
        Assert(e.Cmd == "echo" && e.Payload == "hi there", "echo(\"...\") payload");

        var w = ConsoleShell.Parse("walk(duration=2)");
        Assert(w.Cmd == "run" && w.Args.Length == 1 && w.Args[0] == "2", "walk(duration=2) → run 2");

        var f = ConsoleShell.Parse("follow(on:false)");
        Assert(f.Cmd == "follow" && f.Args.Length == 1 && f.Args[0] == "off", "follow(on:false) → follow off");

        var r2 = ConsoleShell.Parse("run(strafe=-1)");
        Assert(r2.Cmd == "run" && r2.Args.Length == 1 && r2.Args[0] == "left", "run(strafe=-1) → run left");

        var r3 = ConsoleShell.Parse("run(duration=2, strafe=1)");
        Assert(r3.Cmd == "run" && r3.Args.Length == 2, "run(duration, strafe) two args");
    }

    // ---- ParseReply ----

    static void ParseReplyMultiLine()
    {
        var lines = ConsoleShell.ParseReply("ls\ncd bed\necho hi");
        Assert(lines.Count == 3, "three lines parsed: " + lines.Count);
        Assert(lines[0].Cmd == "ls" && lines[1].Cmd == "cd" && lines[2].Cmd == "echo", "commands in order");
    }

    static void ParseReplyFences()
    {
        var lines = ConsoleShell.ParseReply("```bash\nls\ncd bed\n```");
        Assert(lines.Count == 2, "fenced block: 2 lines: " + lines.Count);
    }

    static void ParseReplyMixedProse()
    {
        var lines = ConsoleShell.ParseReply("I'm feeling hungry\nls\nI want food");
        Assert(lines.Count == 3, "mixed: 3 lines (prose kept as unknown)");
        Assert(lines[0].IsUnknown, "prose line 1 unknown");
        Assert(lines[1].Cmd == "ls", "prose line 2 = ls");
        Assert(lines[2].IsUnknown, "prose line 3 unknown");
    }

    static void ParseReplyEmpty()
    {
        Assert(ConsoleShell.ParseReply("").Count == 0, "empty reply → 0 lines");
        Assert(ConsoleShell.ParseReply(null).Count == 0, "null reply → 0 lines");
        Assert(ConsoleShell.ParseReply("\n  \n").Count == 0, "blank lines → 0 lines");
    }

    static void ParseReplyBullets()
    {
        var lines = ConsoleShell.ParseReply("- ls\n- cd bed\n- echo hello");
        Assert(lines.Count == 3 && lines[0].Cmd == "ls" && lines[1].Cmd == "cd" && lines[2].Cmd == "echo",
            "bulleted command list");
    }

    // ---- echo-tail split ("echo done cat quest" rescues the trailing cat) ----
    static void EchoCatTailTests()
    {
        string h, t;
        Assert(ConsoleShell.TrySplitEchoCatTail("echo I will go check cat quest", out h, out t)
            && t == "cat quest" && h == "echo I will go check", "glued cat quest splits: h='" + h + "' t='" + t + "'");
        var lines = ConsoleShell.ParseReply("echo I will go check cat quest");
        Assert(lines.Count == 2 && lines[0].Cmd == "echo" && lines[1].Cmd == "cat", "split → echo then cat lines");
        Assert(lines[0].Payload == "I will go check", "speech keeps the head only: '" + lines[0].Payload + "'");
        Assert(ConsoleShell.TrySplitEchoCatTail("say done, cat objective", out h, out t)
            && t == "cat objective" && h == "say done,", "say alias works");
        Assert(ConsoleShell.TrySplitEchoCatTail("echo checking the objective cat mail.", out h, out t)
            && t == "cat mail", "punctuated subject trimmed");
        // false positives stay whole
        Assert(!ConsoleShell.TrySplitEchoCatTail("echo I love the cat facts here", out h, out t),
            "article before cat → no split");
        Assert(ConsoleShell.ParseReply("echo I love the cat facts here").Count == 1, "article FP = one speech line");
        Assert(!ConsoleShell.TrySplitEchoCatTail("echo my cat quests today", out h, out t),
            "unknown subject → no split");
        Assert(!ConsoleShell.TrySplitEchoCatTail("echo \"check cat quest\"", out h, out t),
            "quoted speech → no split");
        Assert(!ConsoleShell.TrySplitEchoCatTail("echo cat quest", out h, out t),
            "bare echo cat quest = pure command, nothing to rescue");
        Assert(!ConsoleShell.TrySplitEchoCatTail("echo we should use the door", out h, out t),
            "action verb tail → no split (hands/feet stay echo-bound)");
        Assert(!ConsoleShell.TrySplitEchoCatTail("cd bed", out h, out t),
            "non-echo lines untouched");
        Assert(!ConsoleShell.TrySplitEchoCatTail("echo I check cat quest | tail -1", out h, out t),
            "pipes stay the payload-data rule's business");
    }

    // ---- help ----

    static void HelpText()
    {
        string h = ConsoleShell.Help();
        Assert(h.Contains("ls") && h.Contains("cd") && h.Contains("echo"), "help lists core commands");
        Assert(h.Length > 200, "help is substantial");
    }

    // ---- module-registered extras (RegisterExtra) ----

    static void ExtraNames()
    {
        // register a couple of module tool names, like the plugin does at startup
        ConsoleShell.RegisterExtra("thrust");
        ConsoleShell.RegisterExtra("plant");
        ConsoleShell.RegisterExtra("feed_blender");

        Assert(ConsoleShell.Canonical("thrust") == "thrust", "extra: exact thrust");
        Assert(ConsoleShell.Canonical("THRUSt") == "thrust", "extra: case-insensitive");
        Assert(ConsoleShell.Canonical("thrust.") == "thrust", "extra: trailing dot stripped");
        Assert(ConsoleShell.IsCommandWord("plant"), "extra: plant is a command word");

        var l = ConsoleShell.Parse("thrust 0.8 3");
        Assert(l.Cmd == "thrust", "extra: parses cmd");
        Assert(l.Args.Length == 2 && l.Args[0] == "0.8" && l.Args[1] == "3", "extra: positional args");

        var b = ConsoleShell.Parse("feed_blender id:7");
        Assert(b.Cmd == "feed_blender" && b.Args.Length == 1 && b.Args[0] == "id:7", "extra: id: target arg");

        // extras must not break the prose guard or unknown handling
        Assert(ConsoleShell.Canonical("frobnicate") == null, "unknown still null after extras");
        Assert(ConsoleShell.Canonical("let") == null, "prose guard intact after extras");

        // idempotent re-registration
        ConsoleShell.RegisterExtra("thrust");
        Assert(ConsoleShell.Canonical("thrust") == "thrust", "extra: idempotent re-register");

        // help lists module commands
        string h = ConsoleShell.Help();
        Assert(h.Contains("MODULE COMMANDS"), "help: module section present");
        Assert(h.Contains("thrust") && h.Contains("feed_blender"), "help: lists extra names");
    }

    // ---- ConsoleSalvage (inline tool-markup recovery) ----

    static void SalvageTests()
    {
        Console.WriteLine("--- ConsoleSalvage ---");
        // The observed live glitch: speech + the whole command queue in mangled markup.
        string r1 = ConsoleSalvage.Clean("echo Hello! I am Basil and it is good morning!<br/>ls<|whoami?<k>sleep]<tool_call|>");
        Assert(r1.Contains("echo Hello! I am Basil and it is good morning!"), "clean echo head kept");
        Assert(r1.Contains("\nls\n") || r1.EndsWith("ls") || r1.StartsWith("ls"), "ls recovered");
        Assert(r1.Contains("whoami"), "whoami recovered");
        Assert(r1.Contains("sleep"), "sleep recovered");
        Assert(!r1.Contains("tool_call") && !r1.Contains("whoami?"), "junk markers dropped");

        // Prose head + goall/lsl mangles.
        string r2 = ConsoleSalvage.Clean("Let me get my bearings.<br/>whoami<|ls|<p/><goall explore house}<lsl-> sleep]<tool_call|>_");
        Assert(r2.StartsWith("Let me get my bearings.", StringComparison.Ordinal), "prose head kept");
        Assert(r2.Contains("whoami"), "whoami recovered from prose-junk");
        Assert(r2.Contains("goal explore house"), "goall -> goal with payload");
        Assert(r2.Contains("sleep"), "sleep recovered");

        // Clean replies pass through untouched.
        string clean = "ls\nwhoami\nsleep";
        Assert(ConsoleSalvage.Clean(clean) == clean, "clean reply untouched");
        string prose = "I want food and company today.";
        Assert(ConsoleSalvage.Clean(prose) == prose, "prose untouched");

        // Empty after pure junk.
        Assert(ConsoleSalvage.Clean("") == "", "empty passthrough");

        // Glue-split: the whole terminal turn glued onto speech (observed live).
        string r3 = ConsoleSalvage.Clean("Hmm, I's feeling kinda sleepy and hungry today! maybe food first! ps whoami status ls sleep");
        Assert(r3.Contains("ps\n") || r3.EndsWith("ps"), "glue: ps split out");
        Assert(r3.Contains("whoami") && r3.Contains("status") && r3.Contains("sleep"), "glue: commands recovered");
        Assert(r3.StartsWith("Hmm,", StringComparison.Ordinal), "glue: speech head kept");
        // glued sleep digits
        string r4 = ConsoleSalvage.Clean("time to nap then rest! ps sleep1h05m");
        Assert(r4.Contains("sleep 1h05m"), "glued sleep1h05m -> sleep 1h05m, got: " + r4.Replace("\n","|"));

        // ParseReply accepts the salvaged output end-to-end.
        var lines = ConsoleShell.ParseReply(r1);
        Assert(lines.Count >= 3, "salvaged reply parses to " + lines.Count + " lines");
        bool sawEcho = false, sawLs = false;
        foreach (var l in lines)
        {
            if (l.Cmd == "echo") sawEcho = true;
            if (l.Cmd == "ls") sawLs = true;
        }
        Assert(sawEcho && sawLs, "echo + ls both parsed from salvaged reply");
    }

    static void Main()
    {
        Console.WriteLine("ConsoleShell tests");
        CanonicalExact();
        CanonicalCaseAndPunctuation();
        CanonicalLegacyAliases();
        CanonicalFuzzy();
        ProseGuard();
        ProseGuardReport();
        CanonicalUnknown();
        IsCommandWordTest();
        ParseLineBasic();
        ParseLinePromptPrefixes();
        ParseLineHeaders();
        ParseLinePayload();
        ParseLineQuoted();
        ParseLineGoalSubcommands();
        ParseSonarArgs();
        ParseReportPayload();
        SalvageTests();
        ParseLineCallStyle();
        ParseReplyMultiLine();
        ParseReplyFences();
        ParseReplyMixedProse();
        ParseReplyEmpty();
        ParseReplyBullets();
        EchoCatTailTests();
        HelpText();
        ExtraNames();
        ToolCallToLineTests();
        ToolCallMarkupTests();
        SalvageMarkupTests();
        SalvageRpProseGuardTests();
        SkillParseTests();
        PlanParseTests();
        CompactDigestTests();
        BuildToolsTests();
        AskRemovedTests();
        Console.WriteLine(_passed + " passed, " + _failed + " failed");
        Environment.Exit(_failed);
    }

    // ---- ConsoleShell.ToolCallToLine: structured call → console line ----

    static void ToolCallToLineTests()
    {
        Assert(ConsoleShell.ToolCallToLine("cd", "{\"target\":\"bed\"}") == "cd bed", "cd target=bed");
        Assert(ConsoleShell.ToolCallToLine("echo", "{\"text\":\"hi there\"}") == "echo hi there", "echo text");
        Assert(ConsoleShell.ToolCallToLine("go_to", "{\"name\":\"bed\"}") == "cd bed", "legacy go_to+name");
        Assert(ConsoleShell.ToolCallToLine("say", "{\"say\":\"Hello!\"}") == "echo Hello!", "say via say key");
        Assert(ConsoleShell.ToolCallToLine("follow", "{\"state\":false}") == "follow off", "bool state false");
        Assert(ConsoleShell.ToolCallToLine("follow", "{\"state\":true}") == "follow on", "bool state true");
        Assert(ConsoleShell.ToolCallToLine("run", "{\"duration\":2,\"direction\":\"left\"}") == "run 2 left", "run + direction");
        Assert(ConsoleShell.ToolCallToLine("cd", "{\"arguments\":\"{\\\"target\\\":\\\"bed\\\"}\"}") == "cd bed", "double-encoded args");
        Assert(ConsoleShell.ToolCallToLine("cd", "") == "cd", "blank args → bare command");
        Assert(ConsoleShell.ToolCallToLine("nope", "{}") == null, "unknown tool name → null");
        // tool-name "id" vs args id:
        Assert(ConsoleShell.ToolCallToLine("use", "{\"id\":3}") == "use id:3", "id arg → id:3");
    }

    // ---- ConsoleTools.ToolCallMarkupLines: inline tool-call JSON recovery ----

    static void ToolCallMarkupTests()
    {
        string oc = ConsoleTools.ToolCallOpen;
        string cc = ConsoleTools.ToolCallClose;
        // single call, narration before
        var r = ConsoleTools.ToolCallMarkupLines("Let me look.\n" + oc + "{\"name\":\"cd\",\"arguments\":{\"target\":\"bed\"}}" + cc);
        Assert(r.Count >= 1 && r[0] == "Let me look." || (r.Count >= 2 && r[0] == "Let me look." && r[1] == "cd bed"),
            "narration kept + call extracted");
        bool hasCd = false;
        foreach (var l in r) if (l == "cd bed") hasCd = true;
        Assert(hasCd, "cd bed recovered from markup");

        // bare call
        var r2 = ConsoleTools.ToolCallMarkupLines(oc + "{\"name\":\"ls\",\"arguments\":{}}" + cc);
        Assert(r2.Count == 1 && r2[0] == "ls", "bare ls call");

        // two calls in sequence
        var r3 = ConsoleTools.ToolCallMarkupLines(
            oc + "{\"name\":\"echo\",\"arguments\":{\"text\":\"going\"}}" + cc
            + oc + "{\"name\":\"cd\",\"arguments\":{\"id\":3}}" + cc);
        Assert(r3.Count == 2 && r3[0] == "echo going" && r3[1] == "cd id:3", "two calls → two lines");

        // fenced json (act-schema shape)
        var r4 = ConsoleTools.ToolCallMarkupLines("```json\n{\"action\":\"say\",\"say\":\"checking\"}\n```");
        Assert(r4.Count == 1 && r4[0] == "echo checking", "fenced act-shape call");

        // no markup at all → empty (falls back to regular parsing)
        Assert(ConsoleTools.ToolCallMarkupLines("echo hi\ncd bed").Count == 0, "plain reply → no markup lines");
        Assert(ConsoleTools.ToolCallMarkupLines("{\"reasoning\": \"just thinking\"}").Count == 0, "nameless json → no markup lines");
        // degenerate marker shapes must never loop or throw
        Assert(ConsoleTools.ToolCallMarkupLines("```\ncd bed\n```").Count == 0, "fenced prose → no markup lines");
        Assert(ConsoleTools.ToolCallMarkupLines("echo hi" + oc).Count == 0, "marker without json");
        Assert(ConsoleTools.ToolCallMarkupLines(oc + "{\"name\":\"ls\"").Count == 0, "unclosed json → no markup lines");
    }

    static void SalvageMarkupTests()
    {
        string oc = ConsoleTools.ToolCallOpen;
        string cc = ConsoleTools.ToolCallClose;
        string in1 = oc + "{\"name\":\"cd\",\"arguments\":{\"target\":\"farm\"}}" + cc;
        string clean = ConsoleSalvage.Clean(in1);
        Assert(clean == "cd farm", "Clean converts markup call, got: " + clean);
        var lines = ConsoleShell.ParseReply(clean);
        Assert(lines.Count == 1 && lines[0].Cmd == "cd" && lines[0].Args[0] == "farm", "end-to-end cd farm");
    }

    // ---- plan command parsing ----

    static void PlanParseTests()
    {
        var l1 = ConsoleShell.ParseReply("plan check the farm first");
        Assert(l1.Count == 1 && l1[0].Cmd == "plan", "plan is a known command");
        Assert(l1[0].Payload == "check the farm first", "plan payload preserved");
        var l2 = ConsoleShell.ParseReply("plan next");
        Assert(l2.Count == 1 && l2[0].Cmd == "plan" && l2[0].Args[0] == "next", "plan next args");
        var l3 = ConsoleShell.ParseReply("plan clear");
        Assert(l3.Count == 1 && l3[0].Cmd == "plan" && l3[0].Args[0] == "clear", "plan clear args");
    }

    // ---- ConsoleCompact.Digest ----

    static void CompactDigestTests()
    {
        var none = ConsoleShell.ConsoleCompact.Digest(new List<string>());
        Assert(none == "(nothing)", "empty digest");
        var d1 = ConsoleShell.ConsoleCompact.Digest(new List<string> { "a|I will go check the farm first", "u|cd farm -> near farm id:3" });
        Assert(d1.StartsWith("[trimmed 2 earlier messages]", StringComparison.Ordinal), "digest header: " + d1);
        Assert(d1.Contains("a: I will go check the farm first"), "assistant first item");
        Assert(d1.Contains("u: cd farm"), "user last item");
        // spread: first three + last two of many
        var many = new List<string>();
        for (int i = 0; i < 10; i++) many.Add("a|step number " + i + " happened here");
        var d2 = ConsoleShell.ConsoleCompact.Digest(many);
        Assert(d2.Contains("[trimmed 10 earlier messages]"), "count in digest");
        Assert(d2.Contains("step number 0") && d2.Contains("step number 9"), "first+last spread");
        Assert(!d2.Contains("step number 5"), "middle dropped from digest");
    }

    // ---- BuildTools: the console vocabulary as an OpenAI tools array ----

    static void BuildToolsTests()
    {
        var tools = ConsoleTools.BuildTools();
        Assert(tools != null && tools.Count >= 25, "tools array size " + (tools == null ? -1 : tools.Count));
        bool sawCd = false, sawEcho = false, sawClear = false;
        foreach (var t in tools)
        {
            var td = t as Dictionary<string, object>;
            var fn = td == null ? null : td["function"] as Dictionary<string, object>;
            var name = fn == null ? null : (string)fn["name"];
            if (name == "cd") sawCd = true;
            if (name == "echo") sawEcho = true;
            if (name == "clear") sawClear = true;
        }
        Assert(sawCd && sawEcho, "console commands present");
        Assert(!sawClear, "clear not offered as a tool");
    }

    // ---- salvage must NOT butcher roleplay prose (real playtest samples) ----

    static void SalvageRpProseGuardTests()
    {
        // RP prose scatters command words one at a time; the density rule keeps the
        // whole paragraph as speech/thought instead of carving junk commands out of it.
        string rp = "*Moss mewls softly as he presses closer* \"please, just let me use you~\" *they grind shamelessly* \"stop it, you're insufferable\" *and yet the warmth spreads* get lost in the feeling, the tension building until nothing exists but the two of them and the slow burn between them and the bed that waits";
        var rpLines = ConsoleShell.ParseReply(ConsoleSalvage.Clean(rp));
        Assert(rpLines.Count >= 1, "rp prose parses as " + rpLines.Count + " line(s)");
        var bad = new List<string>();
        foreach (var l in rpLines)
        {
            string c = l.Cmd ?? "?";
            if (!(l.IsUnknown || c == "echo") || c == "use" || c == "get" || c == "cd")
                bad.Add(c);
        }
        Assert(bad.Count == 0, "rp prose NOT carved into actions (saw: " + string.Join(",", bad.ToArray()) + ")");

        // real glued command queue (the shape GlueSplit exists for) still splits
        string glue = "ohh and my tummy! ps whoami status go find bed echo Zzzzzz~ 15m";
        var glueLines = ConsoleShell.ParseReply(ConsoleSalvage.Clean(glue));
        bool sawPs = false, sawCd = false, sawEcho = false;
        foreach (var l in glueLines)
        {
            if (l.Cmd == "ps") sawPs = true;
            if (l.Cmd == "cd") sawCd = true;
            if (l.Cmd == "echo") sawEcho = true;
        }
        Assert(sawPs && sawCd && sawEcho, "dense glue queue still recovered");

        // bare DANGEROUS commands are never manufactured from prose debris
        var risky = ConsoleShell.ParseReply(ConsoleSalvage.Clean("echo oops! " + ConsoleTools.ToolCallOpen + "<|stop|>prose use stop get drop garbage"));
        foreach (var l in risky)
        {
            bool bareAction = !l.IsUnknown && (l.Cmd == "use" || l.Cmd == "get" || l.Cmd == "drop" || l.Cmd == "throw")
                && (l.Args == null || l.Args.Length == 0);
            Assert(!bareAction, "no bare dangerous command (saw: " + (l.Cmd ?? "prose") + ")");
        }
    }
    // ---- skill add parsing (pure) ----

    static void SkillParseTests()
    {
        var r = ConsoleShell.SkillParseAdd("garden | plant; cd id:3; ls", 24, 10, 90);
        Assert(r.Error == null, "basic add parses");
        Assert(r.Name == "garden", "name split at |");
        Assert(r.Steps != null && r.Steps.Count == 3, "3 steps (got " + (r.Steps == null ? -1 : r.Steps.Count) + ")");
        Assert(r.Steps != null && r.Steps.Count == 3 && r.Steps[0] == "plant" && r.Steps[2] == "ls", "steps trimmed");

        // leading 'add ' keyword tolerated; junk steps dropped
        var r2 = ConsoleShell.SkillParseAdd("add carry | get egg; and then the magic happens; drop", 24, 10, 90);
        Assert(r2.Error == null && r2.Steps.Count == 2, "junk step dropped (kept " + (r2.Steps == null ? -1 : r2.Steps.Count) + ")");
        Assert(r2.Steps[0] == "get egg", "first step kept");

        // errors
        Assert(ConsoleShell.SkillParseAdd("no bar here", 24, 10, 90).Error != null, "missing | refused");
        Assert(ConsoleShell.SkillParseAdd("x | ; ; ;", 24, 10, 90).Error != null, "no valid steps refused");
        // long name trimmed; cap on steps
        var r3 = ConsoleShell.SkillParseAdd("a-very-long-skill-name-beyond-the-cap | ls", 8, 10, 90);
        Assert(r3.Name.Length <= 8, "name capped");
    }

    // ---- the `ask` tool is gone everywhere ----

    static void AskRemovedTests()
    {
        Assert(ConsoleShell.Canonical("ask") == null, "no ask command");
        Assert(ConsoleShell.Canonical("query") == null, "no query alias");
        var lines = ConsoleShell.ParseReply("ask what is this place");
        Assert(lines[0].IsUnknown, "ask parses as prose now");
        var tools = ConsoleTools.BuildTools();
        foreach (var t in tools)
        {
            var td = t as Dictionary<string, object>;
            var fn = td == null ? null : td["function"] as Dictionary<string, object>;
            var fnName = fn == null ? null : (string)fn["name"];
            Assert(fnName != "ask", "no ask function");
        }
    }
}
