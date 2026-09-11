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

    static void Main()
    {
        Console.WriteLine("ConsoleShell tests");
        CanonicalExact();
        CanonicalCaseAndPunctuation();
        CanonicalLegacyAliases();
        CanonicalFuzzy();
        ProseGuard();
        CanonicalUnknown();
        IsCommandWordTest();
        ParseLineBasic();
        ParseLinePromptPrefixes();
        ParseLineHeaders();
        ParseLinePayload();
        ParseLineQuoted();
        ParseLineGoalSubcommands();
        ParseLineCallStyle();
        ParseReplyMultiLine();
        ParseReplyFences();
        ParseReplyMixedProse();
        ParseReplyEmpty();
        ParseReplyBullets();
        HelpText();
        ExtraNames();
        Console.WriteLine(_passed + " passed, " + _failed + " failed");
        Environment.Exit(_failed);
    }
}
