// Tests for ConsoleTurnCore (the headless console turn engine) — LONG-HORIZON SOAK.
// Where test_turncore pins individual behaviors, this suite drives three fake
// agents through hundreds of whole turns with a cycling PATHOLOGICAL reply
// stream (normal, empty, whitespace, prose-as-thought, a 120KB giant, an
// identical parrot line, a JSON tool call, broken JSON, a null endpoint
// failure) plus cross-agent chat events, and asserts the ENGINE-LEVEL
// invariants that only show up over distance: nothing escapes Run, the round
// budget holds (no query runaway), transcript/history growth stays linear,
// and the wall clock stays sane. SOAK_ROUNDS scales it (default 40; CI's
// nightly job runs 2000). The real ConsoleShell parser, ConsolePipe filters,
// ConsoleSalvage and ModelFamilies run inside it. Pure C# — no Unity deps.
// Build: mcs -target:exe -out:/tmp/t_soak.exe tests/test_soak.cs src/ConsoleTurnCore.cs src/ConsoleShell.cs src/ConsolePipe.cs src/Manual.cs src/Calc.cs src/DataRecord.cs src/ChatSimilarity.cs src/Json.cs src/ModelFamilies.cs src/TextUtil.cs src/Constants.cs && mono /tmp/t_soak.exe
using System;
using System.Collections.Generic;
using KKLLMNPC;

// ---- the soak environment (same seam as turncore's TurnFakeEnv, leaner) ----

class SoakEnv : IConsoleTurnEnv
{
    private readonly string _name;
    private float _now = 100f;
    private string _lastThought;

    public int TurnIndex;                       // outer turn counter — drives the reply script
    public readonly Queue<string> EventQueue = new Queue<string>();
    public readonly List<object[]> Msgs = new List<object[]>();   // {role, content}
    public readonly List<KeyValuePair<string, string>> History = new List<KeyValuePair<string, string>>();

    public int QueryCount;
    public int TrimCount;
    public int SalvageCount;
    public int InfoLogs, WarnLogs;

    public int MaxRoundsVal = 3;

    public float Now() { return _now; }
    public float SessionStart { get { return 0f; } }
    public bool Running { get { return true; } }
    public string Name { get { return _name; } }
    public int MaxRounds { get { return MaxRoundsVal; } }
    public bool EndpointConfigured { get { return true; } }
    public bool EndpointBlocked { get { return false; } }
    public void EnsureConversation() { }
    public string DrainEvents() { return EventQueue.Count > 0 ? EventQueue.Dequeue() : ""; }
    // Alternates between three anchors that sometimes repeat — exercises the
    // engine's anchor-dedup over distance instead of a constant string.
    public string StateAnchor()
    {
        switch (TurnIndex % 7)
        {
            case 0: case 1: case 2: return "goal: tend the farm\n";
            case 3: case 4: return "goal: (none)\n";
            default: return "goal: find water\n";
        }
    }
    public void AddMsg(string role, object content) { Msgs.Add(new object[] { role, content }); }
    public void Trim() { TrimCount++; }

    // The pathological reply stream. Cycle position comes from the query count
    // so multi-round turns walk through different pathologies, not the same one.
    public string QueryModel()
    {
        QueryCount++;
        _now += 0.5f; // time passes per model call
        int i = (QueryCount - 1) % 10;
        switch (i)
        {
            case 0: return "say Hello from " + _name + ", query " + QueryCount + "\nwalk forward";
            case 1: return "";                                             // empty reply
            case 2: return "   \n\t  ";                                    // whitespace only
            case 3: return "i think i will just stand here being a lizard"; // prose, no command
            case 4: return GiantReply;                                     // 120KB flood
            case 5: return "say i always say exactly this";                // identical parrot line
            case 6: return "{\"action\":\"say\",\"params\":{\"text\":\"json path works\"}}";
            case 7: return "{\"action\":\"say\",\"params\":{\"text\":\"never closed"; // broken JSON
            case 8: return "go_to nest\nuse id:12";
            default: return null;                                          // endpoint problem
        }
    }
    private static readonly string GiantReply = new string('x', 120000) + "\nwalk forward";

    public bool LastReplyPromotedReasoning { get { return false; } }
    public bool SpeakBlock(string block, bool hadTrigger, bool endTurn, bool promotedReasoning, out bool recorded)
    { recorded = true; return true; }
    public string RunLine(ConsoleLine line, string pipeIn) { return "ok"; }
    public void PushHistory(string kind, string text) { History.Add(new KeyValuePair<string, string>(kind, text)); }
    public void RecordToolCall(string entry) { }
    public void RecordCommand(string raw) { }
    public bool IsCommandRepeat(string raw, out int simCount) { simCount = 0; return false; }
    public void BufSink(string text) { }
    public void AutoScreenshot() { }
    public string TakePendingShot() { return null; }
    public bool ImageCooldownActive { get { return false; } }
    public void NoteSalvage(string rawReply) { SalvageCount++; }
    public string Goal { get { return null; } }
    public string LastThought { get { return _lastThought; } set { _lastThought = value; } }
    public void LogInfo(string msg) { InfoLogs++; }
    public void LogWarning(string msg) { WarnLogs++; }

    public SoakEnv(string name) { _name = name; }
}

internal static class TestSoak
{
    private static int _failed;
    private static void Ok(bool cond, string what)
    {
        if (!cond) { _failed++; Console.WriteLine("  FAIL: " + what); }
    }

    private sealed class Agent
    {
        public readonly string Name;
        public readonly SoakEnv Env;
        public readonly ConsoleTurnState State = new ConsoleTurnState();
        public Agent(string name) { Name = name; Env = new SoakEnv(name); }
    }

    internal static int Run()
    {
        // SOAK_ROUNDS scales the soak (default 40; the nightly CI job runs 2000).
        int rounds = 40;
        try
        {
            string v = Environment.GetEnvironmentVariable("SOAK_ROUNDS");
            if (!string.IsNullOrEmpty(v)) rounds = Math.Max(1, int.Parse(v));
        }
        catch (Exception) { }

        var agents = new List<Agent> { new Agent("soakA"), new Agent("soakB"), new Agent("soakC") };
        int turn = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int r = 0; r < rounds; r++)
        {
            foreach (var a in agents)
            {
                a.Env.TurnIndex = turn++;
                // Cross-agent chatter: every 5th outer turn each agent's event queue
                // receives a line from the OTHERS — the overheard-chat path under volume.
                if (turn % 5 == 0)
                    foreach (var b in agents)
                        if (b != a) b.Env.EventQueue.Enqueue("[chat] " + a.Name + ": round " + r + " ping");
                try { ConsoleTurnCore.Run(a.State, a.Env); }
                catch (Exception e) { Ok(false, "exception escaped Run (" + a.Name + ", round " + r + "): " + e.GetType().Name + " " + e.Message); }
            }
        }
        sw.Stop();

        foreach (var a in agents)
        {
            // Round budget: MaxRounds(3) queries per Run plus a nudge retry —
            // a runaway loop (the historical empty-reply bug class) blows past this.
            Ok(a.Env.QueryCount <= rounds * 4 + 8,
                a.Name + ": query budget holds (" + a.Env.QueryCount + " queries / " + rounds + " turns)");
            // Linear growth: events+anchor+reply+salvage per round, bounded slack.
            Ok(a.Env.Msgs.Count <= rounds * 10 + 32,
                a.Name + ": transcript growth linear (" + a.Env.Msgs.Count + " msgs / " + rounds + " turns)");
            Ok(a.Env.History.Count <= rounds * 10 + 32,
                a.Name + ": history growth linear (" + a.Env.History.Count + " entries / " + rounds + " turns)");
        }
        // Wall clock bound scales with rounds so a slow CI runner can't false-fail
        // while a genuine runaway still blows it: 60s base + 50ms per round.
        long msBound = 60000L + (long)rounds * 50;
        Ok(sw.ElapsedMilliseconds < msBound,
            "soak wall clock < " + msBound + "ms (took " + sw.ElapsedMilliseconds + " ms for " + rounds + " turns x 3 agents)");

        if (_failed > 0)
        {
            Console.WriteLine("soak: " + _failed + " check(s) failed");
            return 1;
        }
        Console.WriteLine("soak: all OK (" + rounds + " turns x 3 agents, " + sw.ElapsedMilliseconds + " ms)");
        return 0;
    }

    internal static void Main()
    {
        int rc = Run();
        if (rc != 0) Environment.Exit(rc);
    }
}
