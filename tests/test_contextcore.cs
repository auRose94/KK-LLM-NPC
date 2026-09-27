// Tests for ContextCore — compaction policy, token estimation, lossy reductions.
// Pure C# — no Unity/BepInEx/game assemblies.
//
// Build: mcs -target:exe -out:/tmp/t_cm.exe tests/test_contextcore.cs \
//          src/ContextCore.cs src/Json.cs src/Constants.cs && mono /tmp/t_cm.exe
using System;
using System.Collections.Generic;
using KKLLMNPC;

static class ContextCoreTests
{
    static int _passed, _failed;

    static void Assert(bool cond, string name)
    {
        if (cond) { _passed++; Console.WriteLine("  PASS: " + name); }
        else { _failed++; Console.WriteLine("  FAIL: " + name); }
    }

    static void AssertEq(int actual, int expected, string name)
    {
        Assert(actual == expected, name + " (got " + actual + ", want " + expected + ")");
    }

    static void AssertEq(string actual, string expected, string name)
    {
        Assert(actual == expected, name + " (got \"" + actual + "\", want \"" + expected + "\")");
    }

    // Drive the escalation state machine the way ContextManager.Update does,
    // minus the Unity clock — this is the policy the adapter delegates to.
    class Machine
    {
        public int Level;
        public int ConsecutiveHigh;
        public string LastAction;

        public string Update(float fill)
        {
            if (ContextPolicy.ShouldEscalate(fill))
            {
                ConsecutiveHigh++;
                if (ContextPolicy.IsEscalating(Level, ConsecutiveHigh))
                {
                    Level = ContextPolicy.NextEscalatedLevel(Level, ConsecutiveHigh);
                    LastAction = Level >= ContextPolicy.MaxLevel
                        ? "switch"
                        : ContextPolicy.Describe(Level);
                    return LastAction;
                }
                return null;
            }
            if (ContextPolicy.ShouldDeEscalate(fill, Level))
            {
                Level = ContextPolicy.DeEscalatedLevel(Level);
                ConsecutiveHigh = 0;
                return "eased to " + Level;
            }
            ConsecutiveHigh = 0;
            return null;
        }
    }

    // ---- thresholds ----

    static void Thresholds()
    {
        Assert(!ContextPolicy.ShouldEscalate(0.89f), "no escalation below 90%");
        Assert(ContextPolicy.ShouldEscalate(0.91f), "escalation above 90%");
        Assert(!ContextPolicy.ShouldEscalate(0.90f), "exactly 90% does not escalate (strict >)");
        Assert(ContextPolicy.ShouldDeEscalate(0.49f, 2), "de-escalate below 50%");
        Assert(!ContextPolicy.ShouldDeEscalate(0.50f, 2), "exactly 50% does not de-escalate");
        Assert(!ContextPolicy.ShouldDeEscalate(0.1f, 0), "never de-escalate from level 0");
    }

    // ---- escalation ladder ----

    static void EscalatesOneLevelAtATime()
    {
        var m = new Machine();
        m.Update(0.95f);
        AssertEq(m.Level, 1, "first over-threshold turn → level 1");
        Assert(m.LastAction != null && m.LastAction.Contains("level 1"), "level 1 action described");
        m.Update(0.95f);
        AssertEq(m.Level, 2, "second turn → level 2");
    }

    static void EscalatesTwoAtOnceWhenSustained()
    {
        var m = new Machine();
        // Turns 1 and 2 climb one level each (consecutiveHigh 1, 2).
        m.Update(0.95f);
        m.Update(0.95f);
        AssertEq(m.ConsecutiveHigh, 2, "consecutive-high counter tracks sustained pressure");
        // Turn 3 crosses FastEscalateAfter → +2.
        m.Update(0.95f);
        AssertEq(m.Level, 4, "sustained pressure jumps 2 levels on the 3rd turn");
    }

    static void EscalationClampsAtCeiling()
    {
        var m = new Machine();
        for (int i = 0; i < 20; i++) m.Update(0.99f);
        AssertEq(m.Level, ContextPolicy.MaxLevel, "saturates at MaxLevel");
        AssertEq(ContextPolicy.MaxLevel, 5, "MaxLevel is 5");
    }

    static void AtCeilingUpdateIsSilent()
    {
        // Important: a permanently full context must not spam the log, and the
        // level-5 model switch must be attempted once per climb, not every turn.
        var m = new Machine();
        for (int i = 0; i < 10; i++) m.Update(0.99f);
        AssertEq(m.Level, 5, "reached ceiling");
        Assert(ContextPolicy.Describe(5) == null, "level 5 has no static description (adapter supplies it)");
        string action = m.Update(0.99f);
        Assert(action == null, "further over-threshold turns at the ceiling report nothing");
    }

    static void PressureEasingStepsDownOneLevel()
    {
        var m = new Machine();
        m.Update(0.95f);
        m.Update(0.95f);
        AssertEq(m.Level, 2, "climbed to 2");
        m.Update(0.2f);
        AssertEq(m.Level, 1, "eased pressure drops one level");
        m.Update(0.2f);
        AssertEq(m.Level, 0, "eases all the way back to 0");
    }

    static void MidBandResetsStreakWithoutChangingLevel()
    {
        var m = new Machine();
        m.Update(0.95f);
        m.Update(0.95f);
        AssertEq(m.Level, 2, "climbed to 2");
        m.Update(0.7f); // between 0.5 and 0.9: hold level, reset streak
        AssertEq(m.Level, 2, "mid-band fill holds the level");
        AssertEq(m.ConsecutiveHigh, 0, "mid-band fill resets the streak");
        m.Update(0.95f);
        AssertEq(m.Level, 3, "streak reset means +1 again, not +2");
    }

    // ---- dynamic limits ----

    static void LimitsHoldAtLevelZero()
    {
        AssertEq(ContextPolicy.MaxHistory(0, 15), 15, "level 0 history untouched");
        AssertEq(ContextPolicy.MaxFacts(0, 30), 30, "level 0 facts untouched");
        AssertEq(ContextPolicy.MaxThoughts(0, 8), 8, "level 0 thoughts untouched");
        AssertEq(ContextPolicy.MaxChatLog(0, 20), 20, "level 0 chat untouched");
    }

    static void HistoryAndThoughtsThinImmediately()
    {
        // These start halving from level 1.
        AssertEq(ContextPolicy.MaxHistory(1, 16), 8, "level 1 halves history");
        AssertEq(ContextPolicy.MaxThoughts(1, 8), 4, "level 1 halves thoughts");
    }

    static void FactsSurviveLevelOne()
    {
        // Facts only start trimming at level 2 — they're compact and costly to lose.
        AssertEq(ContextPolicy.MaxFacts(1, 20), 20, "level 1 keeps all facts");
        AssertEq(ContextPolicy.MaxFacts(2, 20), 10, "level 2 halves facts");
    }

    static void ChatLogSurvivesUntilLevelThree()
    {
        // Chat is the cheapest thing to drop, so it holds longest.
        AssertEq(ContextPolicy.MaxChatLog(2, 20), 20, "level 2 keeps chat log");
        AssertEq(ContextPolicy.MaxChatLog(3, 20), 10, "level 3 halves chat log");
    }

    static void LimitsRespectFloors()
    {
        // Never halve below the per-budget floor, however deep the compression.
        for (int level = 0; level <= 5; level++)
        {
            Assert(ContextPolicy.MaxHistory(level, 3) >= 2, "history floor 2 at level " + level);
            Assert(ContextPolicy.MaxFacts(level, 4) >= 3, "facts floor 3 at level " + level);
            Assert(ContextPolicy.MaxThoughts(level, 2) >= 1, "thoughts floor 1 at level " + level);
            Assert(ContextPolicy.MaxChatLog(level, 3) >= 2, "chat floor 2 at level " + level);
        }
    }

    static void LimitsMonotonicallyDecrease()
    {
        int prevH = int.MaxValue, prevF = int.MaxValue, prevT = int.MaxValue, prevC = int.MaxValue;
        for (int level = 0; level <= ContextPolicy.MaxLevel; level++)
        {
            int h = ContextPolicy.MaxHistory(level, 40);
            int f = ContextPolicy.MaxFacts(level, 40);
            int t = ContextPolicy.MaxThoughts(level, 40);
            int c = ContextPolicy.MaxChatLog(level, 40);
            Assert(h <= prevH && f <= prevF && t <= prevT && c <= prevC,
                "all budgets non-increasing at level " + level);
            prevH = h; prevF = f; prevT = t; prevC = c;
        }
    }

    // ---- descriptions / status JSON ----

    static void Descriptions()
    {
        for (int level = 1; level <= 4; level++)
            Assert(ContextPolicy.Describe(level) != null, "level " + level + " has a description");
        Assert(ContextPolicy.Describe(0) == null, "level 0 has no description");
        AssertEq(ContextPolicy.ModeName(0), "normal", "level 0 mode name");
        AssertEq(ContextPolicy.ModeName(3), "merged", "level 3 mode name");
        AssertEq(ContextPolicy.ModeName(5), "switching", "level 5 mode name");
        Assert(ContextPolicy.ModeName(99) == "normal", "out-of-range mode name falls back to normal");
    }

    static void StatusJson()
    {
        Assert(ContextPolicy.StatusJson(0) == null, "level 0 omits the status key");
        AssertEq(ContextPolicy.StatusJson(2), "{\"level\":2,\"mode\":\"aggressive\"}", "level 2 status JSON");
        AssertEq(ContextPolicy.StatusJson(5), "{\"level\":5,\"mode\":\"switching\"}", "level 5 status JSON");
    }

    // ---- token estimation ----

    static void TokenEstimateWeights()
    {
        // 300 system + 200*nearby + 50*fact + 30*hist + 20*thought + 15*chat
        AssertEq(ContextMath.EstimateTokens(0, 0, 0, 0, 0), 300, "empty payload is just the system prompt");
        AssertEq(ContextMath.EstimateTokens(1, 0, 0, 0, 0), 350, "one fact");
        AssertEq(ContextMath.EstimateTokens(0, 1, 0, 0, 0), 330, "one history entry");
        AssertEq(ContextMath.EstimateTokens(0, 0, 1, 0, 0), 320, "one thought");
        AssertEq(ContextMath.EstimateTokens(0, 0, 0, 1, 0), 315, "one chat line");
        AssertEq(ContextMath.EstimateTokens(0, 0, 0, 0, 1), 500, "one nearby item");
    }

    static void TokenEstimateIsAdditive()
    {
        int a = ContextMath.EstimateTokens(2, 3, 4, 5, 6);
        int sum = 300 + (6 * 200) + (2 * 50) + (3 * 30) + (4 * 20) + (5 * 15);
        AssertEq(a, sum, "estimate is a plain weighted sum");
    }

    static void FillRatio()
    {
        int tokens = ContextMath.EstimateTokens(1, 1, 1, 1, 1);
        float r = ContextMath.FillRatio(1, 1, 1, 1, 1, tokens);
        Assert(r > 0.99f && r < 1.01f, "fill ratio is 1.0 when the estimate matches the window");
        // 300 system + 200 + 50 + 30 + 20 + 15 = 615 tokens; 615/1000 = 0.615
        AssertEq((int)(ContextMath.FillRatio(1, 1, 1, 1, 1, 1000) * 100), 61,
            "fill ratio scales with the window (615/1000)");
    }

    static void UnknownContextLengthIsNotFull()
    {
        // An unprobed server must not look like a full context, or every
        // instance would immediately escalate to maximum compaction.
        AssertEq((int)ContextMath.FillRatio(99, 99, 99, 99, 99, 0), 0, "contextLength 0 → fill 0");
        AssertEq((int)ContextMath.FillRatio(99, 99, 99, 99, 99, -1), 0, "negative contextLength → fill 0");
        Assert(!ContextPolicy.ShouldEscalate(ContextMath.FillRatio(99, 99, 99, 99, 99, 0)),
            "unknown window never triggers escalation");
    }

    // ---- fact merging ----

    static void MergeFactsKeepsNewestPerCategory()
    {
        var facts = new List<string> { "nest: upstairs", "goal: find a nest", "nest: basement" };
        var merged = ContextCompaction.MergeFacts(facts);
        AssertEq(merged.Count, 2, "same-prefix facts collapse");
        Assert(merged.Contains("nest: basement"), "newest fact per category wins");
        Assert(merged.Contains("goal: find a nest"), "unrelated category kept");
        Assert(!merged.Contains("nest: upstairs"), "older duplicate dropped");
    }

    static void MergeFactsEdgeCases()
    {
        AssertEq(ContextCompaction.MergeFacts(new List<string>()).Count, 0, "empty input");
        AssertEq(ContextCompaction.MergeFacts(null).Count, 0, "null input");
        var one = ContextCompaction.MergeFacts(new List<string> { "noColonHere" });
        AssertEq(one.Count, 1, "fact without a colon is its own category");
        AssertEq(ContextCompaction.MergeFacts(new List<string> { ":empty", ":empty" }).Count, 1,
            "empty category prefix still collapses");
    }

    // ---- history summarization ----

    static void SummarizeHistoryShortEnoughToKeepAll()
    {
        string json = ContextCompaction.SummarizeHistory(new List<string> { "walk to door", "open door" }, 5);
        Assert(json.Contains("walk to door") && json.Contains("open door"), "nothing dropped when under budget");
        Assert(!json.Contains("earlier:"), "no summary emitted when under budget");
    }

    static void SummarizeHistoryCollapsesOldEntries()
    {
        var h = new List<string> { "walk x", "walk y", "walk z", "say hi", "say bye" };
        string json = ContextCompaction.SummarizeHistory(h, 2);
        Assert(json.Contains("say hi") && json.Contains("say bye"), "recent entries kept verbatim");
        Assert(json.Contains("3x walk"), "older entries collapsed into a count");
        Assert(!json.Contains("walk x"), "oldest entry not kept verbatim");
    }

    static void SummarizeHistoryIsValidJsonArray()
    {
        var h = new List<string> { "walk a", "talk b", "walk c" };
        string json = ContextCompaction.SummarizeHistory(h, 1);
        Assert(json.StartsWith("[") && json.EndsWith("]"), "output is a JSON array");
        // Round-trip through the real parser to prove the escaping is valid.
        var parsed = Json.Parse(json) as List<object>;
        Assert(parsed != null, "summary parses as a JSON array");
        // "walk a" and "talk b" are different actions, so they summarize
        // separately: 2 summaries + 1 recent entry.
        AssertEq(parsed == null ? 0 : parsed.Count, 3, "two distinct-action summaries + one recent entry");
    }

    static void SummarizeHistoryEdgeCases()
    {
        AssertEq(ContextCompaction.SummarizeHistory(new List<string>(), 3), "[]", "empty history");
        AssertEq(ContextCompaction.SummarizeHistory(null, 3), "[]", "null history");
        // keepRecent beyond the list length keeps everything.
        var h = new List<string> { "walk a" };
        Assert(ContextCompaction.SummarizeHistory(h, 99).Contains("walk a"), "over-large keepRecent keeps all");
        // keepRecent 0 summarizes everything.
        string all = ContextCompaction.SummarizeHistory(new List<string> { "walk a", "walk b" }, 0);
        Assert(all.Contains("2x walk"), "keepRecent 0 summarizes the whole list");
    }

    static void SummarizeHistoryPreservesFirstSeenOrder()
    {
        var h = new List<string> { "walk a", "talk b", "walk c" };
        string json = ContextCompaction.SummarizeHistory(h, 1);
        // "walk a" and "talk b" are old, so they become "1x walk" / "1x talk";
        // only "walk c" survives verbatim.
        Assert(json.Contains("1x walk") && json.Contains("1x talk"), "old distinct actions summarized separately");
        Assert(json.IndexOf("1x walk") >= 0 && json.IndexOf("1x walk") < json.IndexOf("\"walk c\""),
            "summaries precede recent entries");
    }

    static int RunAll()
    {
        Console.WriteLine("== ContextCore Tests ==");
        Thresholds();
        EscalatesOneLevelAtATime();
        EscalatesTwoAtOnceWhenSustained();
        EscalationClampsAtCeiling();
        AtCeilingUpdateIsSilent();
        PressureEasingStepsDownOneLevel();
        MidBandResetsStreakWithoutChangingLevel();
        LimitsHoldAtLevelZero();
        HistoryAndThoughtsThinImmediately();
        FactsSurviveLevelOne();
        ChatLogSurvivesUntilLevelThree();
        LimitsRespectFloors();
        LimitsMonotonicallyDecrease();
        Descriptions();
        StatusJson();
        TokenEstimateWeights();
        TokenEstimateIsAdditive();
        FillRatio();
        UnknownContextLengthIsNotFull();
        MergeFactsKeepsNewestPerCategory();
        MergeFactsEdgeCases();
        SummarizeHistoryShortEnoughToKeepAll();
        SummarizeHistoryCollapsesOldEntries();
        SummarizeHistoryIsValidJsonArray();
        SummarizeHistoryEdgeCases();
        SummarizeHistoryPreservesFirstSeenOrder();
        Console.WriteLine();
        Console.WriteLine("Results: " + _passed + " passed, " + _failed + " failed");
        return _failed;
    }

    static int Main()
    {
        return RunAll() == 0 ? 0 : 1;
    }
}
