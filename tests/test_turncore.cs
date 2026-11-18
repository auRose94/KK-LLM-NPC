// Tests for ConsoleTurnCore (the headless console turn engine).
// A scripted fake LLM + a fake world terminal drive WHOLE agent turns through
// the real engine: rounds, guards, nudges, budget, anchor dedup — the control
// flow that used to be Unity-locked in src/Console.cs. The real ConsoleShell
// parser, ConsolePipe filters, ConsoleSalvage and ModelFamilies run inside it.
// Pure C# — no Unity deps.
// Build: mcs -target:exe -out:/tmp/t_turn.exe tests/test_turncore.cs src/ConsoleTurnCore.cs src/ConsoleShell.cs src/ConsolePipe.cs src/Manual.cs src/Calc.cs src/DataRecord.cs src/ChatSimilarity.cs src/Json.cs src/ModelFamilies.cs src/TextUtil.cs src/Constants.cs && mono /tmp/t_turn.exe
using System;
using System.Collections.Generic;
using KKLLMNPC;

// ---- the scripted environment ----

class TurnFakeEnv : IConsoleTurnEnv
{
    public float NowT = 100f;
    public float SessionStartT = 0f;
    public bool RunningFlag = true;
    public string AgentName = "testbot";
    public int MaxRoundsVal = 5;
    public bool EndpointOk = true;
    public bool Blocked = false;
    public string AnchorVal = "goal: (none)\n";
    public string GoalVal = null;
    public bool PromotedReasoning = false;
    public bool SpeakResult = false;      // SpeakBlock return value
    public bool SpeakRecorded = false;    // SpeakBlock out `recorded`
    public bool ImageCooldown = false;
    public string PendingShotVal = null;
    public Dictionary<string, string> World = new Dictionary<string, string>(); // cmd → output

    public readonly Queue<string> EventQ = new Queue<string>();
    public readonly Queue<object> Replies = new Queue<object>(); // string, or null = endpoint problem
    public readonly List<object[]> Msgs = new List<object[]>();  // {role, content}
    public readonly List<string> Executed = new List<string>();  // RunLine raws
    public readonly List<string> PipeInputs = new List<string>();
    public readonly List<KeyValuePair<string, string>> History = new List<KeyValuePair<string, string>>();
    public readonly List<string> ToolCalls = new List<string>();
    public readonly List<string> BufSinkLines = new List<string>();
    public readonly List<string> Logs = new List<string>();

    public int QueryCount;
    public int TrimCount;
    public bool AutoShotCalled;
    public bool ConversationEnsured;
    public int SalvageCount;
    public string LastSpokenBlock;
    public bool LastSpeakEndTurn, LastSpeakHadTrigger;
    public string ThoughtVal;

    public float Now() { return NowT; }
    public float SessionStart { get { return SessionStartT; } }
    public bool Running { get { return RunningFlag; } }
    public string Name { get { return AgentName; } }
    public int MaxRounds { get { return MaxRoundsVal; } }
    public bool EndpointConfigured { get { return EndpointOk; } }
    public bool EndpointBlocked { get { return Blocked; } }
    public void EnsureConversation() { ConversationEnsured = true; }
    public string DrainEvents() { return EventQ.Count > 0 ? EventQ.Dequeue() : ""; }
        public bool AgentFsmTurn(string drainedEvents, out string remainder) { remainder = drainedEvents; return false; }
    public string StateAnchor() { return AnchorVal; }
    public void AddMsg(string role, object content) { Msgs.Add(new object[] { role, content }); }
    public void Trim() { TrimCount++; }
    public string QueryModel()
    {
        QueryCount++;
        NowT += 1f; // each model call takes a moment of world time
        return Replies.Count > 0 ? Replies.Dequeue() as string : null;
    }
    public bool LastReplyPromotedReasoning { get { return PromotedReasoning; } }
    public bool SpeakBlock(string block, bool hadTrigger, bool endTurn, bool promotedReasoning, out bool recorded)
    {
        LastSpokenBlock = block;
        LastSpeakEndTurn = endTurn;
        LastSpeakHadTrigger = hadTrigger;
        recorded = SpeakRecorded;
        return SpeakResult;
    }
    public string RunLine(ConsoleLine line, string pipeIn)
    {
        Executed.Add(line.Raw);
        PipeInputs.Add(pipeIn);
        string outv;
        return World.TryGetValue(line.Cmd, out outv) ? outv : "ok";
    }
    public void PushHistory(string kind, string text) { History.Add(new KeyValuePair<string, string>(kind, text)); }
    public void RecordToolCall(string entry) { ToolCalls.Add(entry); }
    public void RecordCommand(string raw) { }
    public bool IsCommandRepeat(string raw, out int simCount) { simCount = 0; return false; }
    public void BufSink(string text) { BufSinkLines.Add(text); }
    public void AutoScreenshot() { AutoShotCalled = true; }
    public string TakePendingShot() { var s = PendingShotVal; PendingShotVal = null; return s; }
    public bool ImageCooldownActive { get { return ImageCooldown; } }
    public void NoteSalvage(string rawReply) { SalvageCount++; }
    public string Goal { get { return GoalVal; } }
    public string LastThought { get { return ThoughtVal; } set { ThoughtVal = value; } }
    public void LogInfo(string msg) { Logs.Add(msg); }
    public void LogWarning(string msg) { Logs.Add(msg); }

    // ---- helpers ----
    public string LastUserText()
    {
        for (int i = Msgs.Count - 1; i >= 0; i--)
            if ((string)Msgs[i][0] == "user")
            {
                var c = Msgs[i][1] as string;
                if (c != null) return c;
            }
        return null;
    }
    public string AllUserText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in Msgs)
        {
            if ((string)m[0] != "user") continue;
            var s = m[1] as string;
            if (s != null) sb.Append(s);
        }
        return sb.ToString();
    }
}

static class TurnCoreTests
{
    static int _passed, _failed;

    static void Assert(bool cond, string name)
    {
        if (cond) { _passed++; Console.WriteLine("  PASS: " + name); }
        else { _failed++; Console.WriteLine("  FAIL: " + name); }
    }

    static ConsoleTurnState NewState() { return new ConsoleTurnState(); }

    static TurnFakeEnv NewEnv()
    {
        var env = new TurnFakeEnv();
        env.World["ls"] = "farm/\nkitchen/";
        env.World["sleep"] = "ok — pausing";
        return env;
    }

    // ---- a full command → output → sleep turn ----

    static void HappyTurnEndsOnSleep()
    {
        var st = NewState(); var env = NewEnv();
        env.Replies.Enqueue("ls");
        env.Replies.Enqueue("sleep");
        ConsoleTurnCore.Run(st, env);
        Assert(env.QueryCount == 2, "two model calls (ls, then sleep)");
        Assert(env.Executed.Count == 2 && env.Executed[0] == "ls" && env.Executed[1] == "sleep", "both commands ran");
        Assert(st.Slept, "sleep marks the cycle slept");
        Assert(env.Msgs.Count == 5, "user, assistant, user(output), assistant, user(output)");
        Assert((string)env.Msgs[0][0] == "user" && ((string)env.Msgs[0][1]).EndsWith("$\n"), "turn opens with the $ prompt");
        Assert((string)env.Msgs[1][1] == "ls", "assistant reply echoed into transcript");
        string outMsg = (string)env.Msgs[2][1];
        Assert(outMsg.StartsWith("$ ls\n") && outMsg.Contains("farm/") && outMsg.EndsWith("$\n"), "terminal output shape ($ cmd / result / $)");
        Assert(env.AutoShotCalled, "auto-screenshot hook fired at turn start");
        Assert(env.TrimCount >= 1, "transcript trimmed");
        Assert(env.ConversationEnsured, "conversation ensured");
    }

    // ---- anchor dedup ----

    static void AnchorDedupBarePoll()
    {
        var st = NewState(); var env = NewEnv();
        env.AnchorVal = "goal: plant seeds\n";
        env.Replies.Enqueue("sleep");
        ConsoleTurnCore.Run(st, env);
        string first = (string)env.Msgs[0][1];
        Assert(first == "goal: plant seeds\n$\n", "first turn carries the full anchor");

        var st2 = NewState(); var env2 = NewEnv();
        env2.AnchorVal = "goal: plant seeds\n";
        env2.Replies.Enqueue("sleep");
        ConsoleTurnCore.Run(st2, env2);
        // same state object, second turn, anchor unchanged → bare poll
        // (turn 1 transcript: user, assistant, user-output → turn-2 prompt is Msgs[3])
        env2.Replies.Enqueue("sleep");
        ConsoleTurnCore.Run(st2, env2);
        string second = (string)env2.Msgs[3][1];
        Assert(second == "$\n", "unchanged anchor → bare poll ($ only)");

        env2.AnchorVal = "goal: water farm\n"; // state changed
        ConsoleTurnCore.Run(st2, env2);
        string third = (string)env2.Msgs[6][1];
        Assert(third == "goal: water farm\n$\n", "changed anchor re-emitted");
    }

    static void EventsAlwaysDrainAndRetrigger()
    {
        var st = NewState(); var env = NewEnv();
        env.Replies.Enqueue("sleep");
        ConsoleTurnCore.Run(st, env); // turn 1: anchor emitted
        env.EventQ.Enqueue("[chat] player: hello kobold\n");
        env.Replies.Enqueue("sleep");
        ConsoleTurnCore.Run(st, env); // turn 2: same anchor but an event arrived
        string second = (string)env.Msgs[3][1];
        Assert(second.Contains("[chat] player: hello kobold") && second.Contains("goal: (none)"),
            "event + anchor re-emitted together");
        Assert(st.LastTriggerT == env.NowT - 1f, "trigger stamped (hadTrigger)");
    }

    // ---- empty replies: ENTER frames, then back off ----

    static void EmptyRepliesFramesThenBackoff()
    {
        var st = NewState(); var env = NewEnv();
        env.Replies.Enqueue("");
        env.Replies.Enqueue("");
        env.Replies.Enqueue("");
        ConsoleTurnCore.Run(st, env);
        Assert(env.QueryCount == 3, "three empty replies → three calls, then back off");
        string all = env.AllUserText();
        int frames = 0, idx = 0;
        while ((idx = all.IndexOf("you pressed ENTER", idx, StringComparison.Ordinal)) >= 0) { frames++; idx++; }
        Assert(frames == 2, "two ENTER frames served, third backs off (got " + frames + ")");
        Assert(env.Executed.Count == 0, "nothing executed on empty replies");
    }

    // ---- prose ladder: one nudge per turn, then thought ----

    static void ProseNudgeThenThought()
    {
        var st = NewState(); var env = NewEnv();
        env.Replies.Enqueue("I wonder about the farm.");
        env.Replies.Enqueue("the farm is nice and warm today");
        ConsoleTurnCore.Run(st, env);
        Assert(env.QueryCount == 2, "nudge + thought = two calls");
        string all = env.AllUserText();
        Assert(all.Contains("That's not a command line"), "first prose gets the gentle nudge");
        Assert(!all.Contains("REPLY WITH COMMAND LINES ONLY"), "second prose NOT nudged again (one nudge per turn)");
        Assert(env.ThoughtVal == "the farm is nice and warm today", "prose accepted as the thought");
        Assert(env.History.Count == 1 && env.History[0].Key == "think", "thought pushed to history");
        Assert(!env.SpeakResult || env.LastSpokenBlock == null, "plain prose is not spoken");
    }

    static void ProseLadderEscalatesAcrossTurns()
    {
        var st = NewState(); var env = NewEnv();
        env.GoalVal = "paint the fence";
        env.Replies.Enqueue("prose one");
        env.Replies.Enqueue("prose two");
        ConsoleTurnCore.Run(st, env); // turn 1: streak 1→2 (nudge + thought)
        env.Replies.Enqueue("prose three");
        env.Replies.Enqueue("prose four");
        ConsoleTurnCore.Run(st, env); // turn 2: streak 3 (help nudge) → 4 (thought)
        string all = env.AllUserText();
        Assert(all.Contains("Current goal: paint the fence"), "streak-3 nudge names the goal");
        Assert(env.ThoughtVal == "prose four", "turn-2 prose lands as the thought");
    }

    // ---- identical-reply guard (cross-turn) ----

    static void IdenticalRepeatGuard()
    {
        var st = NewState(); var env = NewEnv();
        env.Replies.Enqueue("hello there friend");
        env.Replies.Enqueue("hello there friend"); // turn 1: nudge → thought exit
        ConsoleTurnCore.Run(st, env);
        Assert(env.QueryCount == 2, "turn 1 ends via the thought exit");
        env.Replies.Enqueue("hello there friend"); // turn 2: repeat streak hits 2
        ConsoleTurnCore.Run(st, env);
        Assert(env.QueryCount == 3, "repeat guard ends turn 2 after one call");
        Assert(env.AllUserText().Contains("THE SAME text"), "corrective injected");
        Assert(env.Executed.Count == 0, "repeated prose never executed");
    }

    // ---- terminal-copy guard ----

    static void TerminalCopyRefused()
    {
        var st = NewState(); var env = NewEnv();
        env.AnchorVal = "goal: plant seeds | awake: 0h01m\n";
        env.Replies.Enqueue("goal: plant seeds | awake: 0h01m"); // copies the anchor verbatim
        ConsoleTurnCore.Run(st, env);
        Assert(env.QueryCount == 2, "copy refused → corrective, model gets another round");
        Assert(env.Executed.Count == 0, "copied anchor never executed (the old ghost-goal bug)");
        Assert(env.AllUserText().Contains("That was a COPY of the terminal"), "copy corrective shown");
    }

    static void OutputCopyRefusedWithinTurn()
    {
        var st = NewState(); var env = NewEnv();
        env.Replies.Enqueue("ls");   // round 1 output: "$ ls\nfarm/\nkitchen/\n$"
        env.Replies.Enqueue("kitchen/"); // round 2 copies an output line
        ConsoleTurnCore.Run(st, env);
        Assert(env.Executed.Count == 1, "copied output line never executed");
        Assert(env.AllUserText().Contains("That was a COPY of the terminal"), "copy corrective within turn");
    }

    // ---- roleplay response blocks ----

    static void RoleplayBlockSpoken()
    {
        var st = NewState(); var env = NewEnv();
        env.SpeakResult = true;
        env.Replies.Enqueue("\"Hello there, traveler!\" *waves happily*");
        ConsoleTurnCore.Run(st, env);
        Assert(env.QueryCount == 1, "spoken block ends the turn");
        Assert(env.LastSpeakEndTurn, "SpeakBlock called with endTurn=true");
        Assert(env.Executed.Count == 0, "rp block runs no commands");
        Assert(!env.AllUserText().Contains("not a command line"), "no nudge for a spoken block");
    }

    static void StarNarrationNudgeThrottled()
    {
        var st = NewState(); var env = NewEnv();
        env.SpeakResult = true;
        env.Replies.Enqueue("*walks to the kitchen and grabs an egg*");
        ConsoleTurnCore.Run(st, env);
        Assert(env.AllUserText().Contains("*starred action* was narration only"), "star-narration reminder shown");
        float firstNudgeT = st.StarNudgeT;
        env.Replies.Enqueue("*walks to the barn*");
        ConsoleTurnCore.Run(st, env); // 1s later — throttle (45s) still holds
        int nudges = 0, idx = 0;
        string all = env.AllUserText();
        while ((idx = all.IndexOf("*starred action*", idx, StringComparison.Ordinal)) >= 0) { nudges++; idx++; }
        Assert(nudges == 1, "star nudge throttled within 45s");
        Assert(st.StarNudgeT == firstNudgeT, "nudge timestamp unchanged while throttled");
    }

    static void MixedReplySpeaksAndExecutes()
    {
        var st = NewState(); var env = NewEnv();
        env.SpeakResult = true;
        env.Replies.Enqueue("\"Hi over there!\"\nls");
        ConsoleTurnCore.Run(st, env);
        Assert(env.LastSpeakEndTurn == false, "mixed-reply prose spoken WITHOUT ending the turn");
        Assert(env.LastSpokenBlock.Contains("Hi over there"), "rp part went to the speaker");
        Assert(env.Executed.Count == 1 && env.Executed[0] == "ls", "command part still executed");
        Assert(env.QueryCount == 2, "turn continues after a mixed reply");
    }

    // ---- rounds / budget / stuck / unknown ----

    static void MaxRoundsCutoff()
    {
        var st = NewState(); var env = NewEnv();
        env.MaxRoundsVal = 2;
        env.Replies.Enqueue("ls");
        env.Replies.Enqueue("ls");
        env.Replies.Enqueue("ls");
        ConsoleTurnCore.Run(st, env);
        Assert(env.QueryCount == 2, "round budget caps the model calls");
    }

    static void BudgetSpentStillHonorsSleep()
    {
        var st = NewState(); var env = NewEnv();
        // 12 distinct acting commands fill the budget exactly; sleep must still run.
        string[] cmds = { "ls", "ps", "pwd", "whoami", "cat", "status", "find", "look", "sonar", "report", "cd", "use" };
        var sb = new System.Text.StringBuilder();
        foreach (var c in cmds) sb.Append(c).Append('\n');
        sb.Append("sleep");
        env.Replies.Enqueue(sb.ToString());
        ConsoleTurnCore.Run(st, env);
        Assert(st.CmdsThisCycle == 12, "budget counted 12 acting commands");
        Assert(!env.AllUserText().Contains("[limit]"), "no limit message at exactly 12");
        Assert(env.Executed.Contains("sleep"), "sleep honored with the budget spent");
        Assert(st.Slept, "sleep ends the cycle");
    }

    static void BudgetLimitRefusesThirteenthCommand()
    {
        var st = NewState(); var env = NewEnv();
        string[] cmds = { "ls", "ps", "pwd", "whoami", "cat", "status", "find", "look", "sonar", "report", "cd", "use", "run" };
        var sb = new System.Text.StringBuilder();
        foreach (var c in cmds) sb.Append(c).Append('\n');
        sb.Append("sleep");
        env.Replies.Enqueue(sb.ToString());
        ConsoleTurnCore.Run(st, env);
        // The limit-cut turn simply ends: the [limit] text is assembled but the
        // original loop breaks before sending it — the fresh budget next tick is
        // the model's signal. (Faithful port pins that shape.)
        Assert(!env.AllUserText().Contains("[limit]"), "limit-cut turn ends without sending [limit]");
        Assert(st.CmdsThisCycle == 12, "budget counted 12 acting commands");
        Assert(!env.Executed.Contains("run"), "over-budget command not executed");
        Assert(!env.Executed.Contains("sleep"), "lines after [limit] never ran (turn cut)");
        Assert(!st.Slept, "limit-cut turn is not a sleep");
        Assert(env.QueryCount == 1, "turn ended without asking the model again");
    }

    static void UnknownCommandsEscalate()
    {
        var st = NewState(); var env = NewEnv();
        env.Replies.Enqueue("cd\nzzz_frobnicate");
        env.Replies.Enqueue("ps\nzzz_frobnicate");
        env.Replies.Enqueue("whoami\nzzz_frobnicate");
        ConsoleTurnCore.Run(st, env);
        string all = env.AllUserText();
        int nags = 0, idx = 0;
        while ((idx = all.IndexOf("unknown command — type `help`", idx, StringComparison.Ordinal)) >= 0) { nags++; idx++; }
        Assert(nags == 1, "unknown escalation nags once (first two quiet), got " + nags);
        Assert(st.UnknownLines == 3, "unknown-line counter tracked the cycle");
    }

    static void StuckEscalationEndsTurn()
    {
        var st = NewState(); var env = NewEnv();
        env.MaxRoundsVal = 8;
        // Same canonical command (stuck key) but different raw replies — identical
        // raw replies would trip the identical-repeat guard first.
        string[] looks = { "look", "Look", "LOOK", "looK", "lOok" };
        foreach (var l in looks) env.Replies.Enqueue(l);
        ConsoleTurnCore.Run(st, env);
        string all = env.AllUserText();
        Assert(all.Contains("stuck — you're repeating similar commands"), "stuck rung 2 shown");
        Assert(all.Contains("still stuck on the same thing (look)"), "stuck rung 3 shown");
        Assert(all.Contains("you've tried look many times"), "stuck hard cap shown");
        Assert(env.QueryCount == 5, "hard cap ends the turn after 5 rounds");
        Assert(st.StuckBreak && st.Slept, "stuck break reuses the end-cycle path");
    }

    // ---- scratch commands ride free ----

    static void ScratchCommandsRideFree()
    {
        var st = NewState(); var env = NewEnv();
        env.Replies.Enqueue("think plan the farm\nbuf note to self\ncalc 1+1\nls");
        ConsoleTurnCore.Run(st, env);
        Assert(env.Executed.Count == 4, "scratch + acting commands all ran");
        Assert(st.CmdsThisCycle == 1, "scratch commands cost no budget (only ls counted)");
        Assert(env.History.Count == 1 && env.History[0].Key == "ls", "no history rows for scratch commands");
    }

    // ---- suppressed echo refunds the budget slot ----

    static void SuppressedEchoRefundsBudget()
    {
        var st = NewState(); var env = NewEnv();
        env.World["echo"] = "suppressed — you just said that";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 13; i++) sb.Append("echo thing ").Append(i).Append('\n');
        env.Replies.Enqueue(sb.ToString());
        ConsoleTurnCore.Run(st, env);
        Assert(st.CmdsThisCycle == 0, "13 suppressed echoes refunded their slots");
        Assert(!env.AllUserText().Contains("[limit]"), "no limit trip on refunded echoes");
        Assert(env.Executed.Count == 13, "all echo lines processed");
    }

    // ---- talking-to-nobody guard ----

    static void IdleChatterNudgeThrottled()
    {
        var st = NewState(); var env = NewEnv();
        env.World["echo"] = "said: hello";
        env.Replies.Enqueue("echo hello");
        ConsoleTurnCore.Run(st, env);
        Assert(env.AllUserText().Contains("nobody spoke this turn"), "pure-speech turn nudged (no trigger)");
        float stamp = st.IdleChatterT;
        env.Replies.Enqueue("echo hello again");
        ConsoleTurnCore.Run(st, env); // ~1s later, still throttled
        int nudges = 0, idx = 0;
        string all = env.AllUserText();
        while ((idx = all.IndexOf("nobody spoke this turn", idx, StringComparison.Ordinal)) >= 0) { nudges++; idx++; }
        Assert(nudges == 1, "idle-chatter nudge throttled");
        Assert(st.IdleChatterT == stamp, "nudge timestamp unchanged while throttled");
    }

    // ---- pipes + tee sink ----

    static void PipeAndTeeSink()
    {
        var st = NewState(); var env = NewEnv();
        env.World["buf"] = "[buf] 1 lines";
        env.Replies.Enqueue("ls | tee\nbuf");
        ConsoleTurnCore.Run(st, env);
        Assert(env.Executed.Count == 2, "ls and buf ran (tee is a filter, not a command)");
        string sunk = string.Join("\n", env.BufSinkLines.ToArray());
        Assert(sunk.Contains("farm/") && sunk.Contains("kitchen/"), "tee copied the ls stream into the scratch buffer");
        Assert(env.PipeInputs[1] == null, "buf after a non-piped tee gets no pipe input");
        Assert(env.AllUserText().Contains("[buf] 1 lines"), "buf output shown");
    }

    // ---- screenshots: multipart output + cooldown ----

    static void PendingShotAttachesMultipart()
    {
        var st = NewState(); var env = NewEnv();
        env.PendingShotVal = "data:image/jpeg;base64,QUJD";
        env.Replies.Enqueue("ls");
        env.Replies.Enqueue("sleep");
        ConsoleTurnCore.Run(st, env);
        object content = null;
        foreach (var m in env.Msgs)
            if ((string)m[0] == "user" && m[1] is object[]) { content = m[1]; break; }
        Assert(content != null, "output message became multipart with a pending shot");
        var parts = content as object[];
        Assert(parts != null && parts.Length == 2, "two parts (text + image)");
        var p0 = parts[0] as Dictionary<string, object>;
        var p1 = parts[1] as Dictionary<string, object>;
        Assert(p0 != null && (string)p0["type"] == "text", "part 0 is text");
        Assert(p1 != null && (string)p1["type"] == "image_url", "part 1 is image_url");
        var url = p1["image_url"] as Dictionary<string, object>;
        Assert(url != null && (string)url["url"] == "data:image/jpeg;base64,QUJD", "image url carried");
    }

    static void PendingShotCooldownSkipsImage()
    {
        var st = NewState(); var env = NewEnv();
        env.ImageCooldown = true;
        env.PendingShotVal = "data:image/jpeg;base64,QUJD";
        env.Replies.Enqueue("ls");
        ConsoleTurnCore.Run(st, env);
        Assert(env.AllUserText().Contains("(image skipped:"), "cooldown tells the model why the image is missing");
        foreach (var m in env.Msgs)
            if ((string)m[0] == "user") Assert(!(m[1] is object[]), "no multipart while cooling down");
    }

    // ---- endpoint gates ----

    static void EndpointGates()
    {
        var st = NewState(); var env = NewEnv();
        env.EndpointOk = false;
        ConsoleTurnCore.Run(st, env);
        Assert(env.QueryCount == 0 && env.Msgs.Count == 0, "no endpoint: turn is a no-op");

        var st2 = NewState(); var env2 = NewEnv();
        env2.Blocked = true; // circuit breaker open
        ConsoleTurnCore.Run(st2, env2);
        Assert(env2.QueryCount == 0 && env2.Msgs.Count == 0, "blocked endpoint: turn is a no-op");
    }

    static void QueryModelNullEndsQuietly()
    {
        var st = NewState(); var env = NewEnv();
        env.Replies.Enqueue(null); // endpoint problem mid-turn
        ConsoleTurnCore.Run(st, env);
        Assert(env.QueryCount == 1, "null reply = one probe");
        Assert(env.Executed.Count == 0, "nothing executed");
        Assert(env.TrimCount == 1, "final trim still ran");
    }

    // ---- turn clock ----

    static void TurnClockRebuildAndGap()
    {
        var st = NewState(); var env = NewEnv();
        env.NowT = 30f;
        env.Replies.Enqueue("sleep");
        ConsoleTurnCore.Run(st, env);
        Assert(st.ClockLine != null && st.ClockLine.StartsWith("now: 00:00:30"), "session clock built (got '" + st.ClockLine + "')");
        Assert(!st.ClockLine.Contains("since your last turn"), "first turn has no gap suffix");

        env.NowT = 95f; // 65s later — past ClockRebuildInterval(5s)
        ConsoleTurnCore.Run(st, env);
        Assert(st.ClockLine.StartsWith("now: 00:01:35"), "clock rebuilt with new session time");
        Assert(st.ClockLine.Contains("(+65s since your last turn"), "turn-gap pressure line present");

        env.NowT = 97f; // only 2s later — under ClockRebuildInterval: string must stand
        ConsoleTurnCore.Run(st, env);
        Assert(st.ClockLine.StartsWith("now: 00:01:35"), "clock NOT rebuilt inside the interval (dedup stable)");
    }

    // ---- real salvage + think-strip integration ----

    static void SalvageRecoversMarkupCommands()
    {
        var st = NewState(); var env = NewEnv();
        env.Replies.Enqueue("ls<|whoami?<k>sleep]");
        env.Replies.Enqueue("sleep");
        ConsoleTurnCore.Run(st, env);
        Assert(env.SalvageCount == 1, "garbled markup reported to the salvage hook");
        Assert(env.Executed.Count >= 1 && env.Executed[0] == "ls", "clean head recovered as a command");
    }

    static void ThinkBlocksStrippedBeforeParsing()
    {
        var st = NewState(); var env = NewEnv();
        env.Replies.Enqueue("<think>I should look around first</think>\nls");
        env.Replies.Enqueue("sleep");
        ConsoleTurnCore.Run(st, env);
        Assert(env.Executed.Contains("ls"), "command after the think block executed");
        Assert(!env.AllUserText().Contains("I should look around"), "reasoning never reached the terminal");
    }

    static void Main()
    {
        Console.WriteLine("ConsoleTurnCore tests");
        HappyTurnEndsOnSleep();
        AnchorDedupBarePoll();
        EventsAlwaysDrainAndRetrigger();
        EmptyRepliesFramesThenBackoff();
        ProseNudgeThenThought();
        ProseLadderEscalatesAcrossTurns();
        IdenticalRepeatGuard();
        TerminalCopyRefused();
        OutputCopyRefusedWithinTurn();
        RoleplayBlockSpoken();
        StarNarrationNudgeThrottled();
        MixedReplySpeaksAndExecutes();
        MaxRoundsCutoff();
        BudgetSpentStillHonorsSleep();
        BudgetLimitRefusesThirteenthCommand();
        UnknownCommandsEscalate();
        StuckEscalationEndsTurn();
        ScratchCommandsRideFree();
        SuppressedEchoRefundsBudget();
        IdleChatterNudgeThrottled();
        PipeAndTeeSink();
        PendingShotAttachesMultipart();
        PendingShotCooldownSkipsImage();
        EndpointGates();
        QueryModelNullEndsQuietly();
        TurnClockRebuildAndGap();
        SalvageRecoversMarkupCommands();
        ThinkBlocksStrippedBeforeParsing();
        Console.WriteLine(_passed + " passed, " + _failed + " failed");
        Environment.Exit(_failed == 0 ? 0 : 1);
    }
}
