// Unit tests for DecisionParse — the pure, game-independent core of the
// decision-model (clef-flash) POC: recovering a scored Guidance out of
// whatever a small classification model actually emits.
//
// This is the crux of the whole POC: clef-flash is a 9B classifier. It will not
// emit tidy canonical JSON — it wraps fenced blocks, adds prose, answers only
// some questions, clamps its own confidence, or drops it as a string. Every
// one of those must be recovered (or cleanly rejected) without throwing.
//
// Run via run_tests.sh (suite "decisionparse").
using System;
using System.Collections.Generic;
using KKLLMNPC;

internal static class TestDecisionParse
{
    private static int _failed;

    private static void Assert(bool cond, string what)
    {
        if (!cond)
        {
            _failed++;
            System.Console.WriteLine("FAIL " + what);
        }
        else
        {
            System.Console.WriteLine("PASS " + what);
        }
    }

    private static Guidance Parse(string raw)
    {
        return DecisionParse.Parse(raw);
    }

    // A tidy canonical answer is recovered field-for-field.
    private static void TestClean()
    {
        string raw = "{\"task\":\"visit-station\",\"blocked\":true,\"blocker\":\"busy\"," +
                     "\"next_tool\":\"go_to\",\"confidence\":0.87,\"rationale\":\"the bed is busy\"}";
        var d = Parse(raw);
        Assert(d != null, "clean: non-null");
        Assert(d != null && d.Task == "visit-station", "clean: task");
        Assert(d != null && d.Blocked == "busy", "clean: blocker");
        Assert(d != null && d.NextTool == "go_to", "clean: next_tool");
        Assert(d != null && System.Math.Abs(d.Confidence - 0.87f) < 0.001f, "clean: confidence 0.87");
        Assert(d != null && d.Rationale == "the bed is busy", "clean: rationale");
    }

    // Fenced code blocks — the most common wrinkle a model introduces.
    private static void TestFenced()
    {
        string raw = "```json\n{\"task\":\"gather\",\"blocker\":\"none\",\"next_tool\":\"grab\",\"confidence\":0.5,\"rationale\":\"seeds nearby\"}\n```";
        var d = Parse(raw);
        Assert(d != null && d.Task == "gather", "fenced json block: task recovered");
        Assert(d != null && d.Confidence == 0.5f, "fenced json block: confidence");
    }

    // A bare ``` block without a language tag.
    private static void TestBareFence()
    {
        string raw = "```\n{\"task\":\"explore\",\"blocker\":\"none\",\"next_tool\":\"look_around\",\"confidence\":0.4,\"rationale\":\"\"}\n```";
        var d = Parse(raw);
        Assert(d != null && d.Task == "explore", "bare fence: task recovered");
    }

    // task is mandatory: no task => no guidance, even with a valid confidence.
    private static void TestMissingTask()
    {
        string raw = "{\"blocked\":false,\"next_tool\":\"none\",\"confidence\":0.9}";
        Assert(Parse(raw) == null, "missing task: rejected");
    }

    // confidence <= 0 is a real "I don't know" — not guidance.
    private static void TestZeroConfidence()
    {
        string raw = "{\"task\":\"idle\",\"blocker\":\"none\",\"next_tool\":\"none\",\"confidence\":0}";
        Assert(Parse(raw) == null, "zero confidence: rejected");
    }

    // A confidence that came back as a string (models stringify cheaply).
    private static void TestStringConfidence()
    {
        string raw = "{\"task\":\"gather\",\"blocker\":\"none\",\"next_tool\":\"grab\",\"confidence\":\"0.6\",\"rationale\":\"\"}";
        var d = Parse(raw);
        Assert(d != null && System.Math.Abs(d.Confidence - 0.6f) < 0.001f, "string confidence parsed");
    }

    // The model may clamp its confidence above 1; we clamp back to 1.
    private static void TestConfidenceClamp()
    {
        string raw = "{\"task\":\"gather\",\"blocker\":\"none\",\"next_tool\":\"grab\",\"confidence\":1.9}";
        var d = Parse(raw);
        Assert(d != null && d.Confidence == 1f, "confidence clamped to 1");
    }

    // Negative confidence must clamp to 0 (and thus be rejected by the guard).
    private static void TestNegativeConfidence()
    {
        string raw = "{\"task\":\"gather\",\"blocker\":\"none\",\"next_tool\":\"grab\",\"confidence\":-0.3}";
        Assert(Parse(raw) == null, "negative confidence rejected");
    }

    // Leading prose before the JSON — the parser should still find the object.
    private static void TestProseLeading()
    {
        string raw = "Let me think: the most likely task is:\n{\"task\":\"follow-player\",\"blocker\":\"none\",\"next_tool\":\"follow\",\"confidence\":0.7,\"rationale\":\"player left\"}";
        var d = Parse(raw);
        Assert(d != null && d.Task == "follow-player", "leading prose recovered task");
    }

    // Missing fields degrade to sensible defaults rather than crashing.
    private static void TestMissingOptionalFields()
    {
        string raw = "{\"task\":\"nest-eggs\",\"confidence\":0.5}";
        var d = Parse(raw);
        Assert(d != null && d.Task == "nest-eggs", "missing optional: task kept");
        Assert(d != null && d.Blocked == "none", "missing optional: blocker defaults none");
        Assert(d != null && d.NextTool == "", "missing optional: next_tool defaults empty");
        Assert(d != null && d.Rationale == "", "missing optional: rationale defaults empty");
    }

    // Garbage that isn't JSON at all => null, never a throw.
    private static void TestGarbage()
    {
        Assert(Parse("the model went on a tangent about the weather") == null, "garbage: null");
        Assert(Parse("") == null, "empty: null");
        Assert(Parse(null) == null, "null: null");
        Assert(Parse("{\"task\":") == null, "truncated json: null");
    }

    // A confidence that's a string but unparseable counts as no confidence.
    private static void TestUnparseableConfidence()
    {
        string raw = "{\"task\":\"gather\",\"blocker\":\"none\",\"next_tool\":\"grab\",\"confidence\":\"lots\"}";
        Assert(Parse(raw) == null, "unparseable confidence rejected");
    }

        // A stringified-but-valid float still works.
        private static void TestStringFloat()
        {
            string raw = "{\"task\":\"visit-station\",\"blocker\":\"locked_or_unreachable\",\"next_tool\":\"walk\",\"confidence\":\"0.314\",\"rationale\":\"\"}";
            var d = Parse(raw);
            Assert(d != null && d.Blocked == "locked_or_unreachable", "stringified confidence + blocker recovered");
        }

        // --- /v1/systemone noul-answer recognition ---

        // The task question set used by the mod (questionKey -> task id).
        private static readonly (string, string)[] TASKS =
        {
            ("task_follow", "follow-player"), ("task_talk", "talk-to-player"),
            ("task_station", "visit-station"), ("task_nest", "nest-eggs"),
            ("task_gather", "gather"), ("task_idle", "idle"),
        };
        private static readonly (string, string)[] BLOCKERS =
        {
            ("block_moved", "locked_or_unreachable"), ("block_busy", "busy"),
            ("block_locked", "locked_or_unreachable"), ("block_needs_buy", "needs_buy"),
        };

        private static Dictionary<string, object> Answers(params (string, double)[] qs)
        {
            var a = new Dictionary<string, object>();
            foreach (var kv in qs)
                a[kv.Item1] = new Dictionary<string, object> { ["noul"] = kv.Item2 };
            return a;
        }

        // The recognised task is the argmax of the task-question scores.
        private static void TestArgmaxTask()
        {
            var a = Answers(("task_follow", 0.3), ("task_gather", 0.62), ("task_idle", 0.2));
            var g = DecisionParse.SystemoneAnswers(a, TASKS, BLOCKERS);
            Assert(g != null && g.Task == "gather", "noul: argmax task selected");
            Assert(g != null && System.Math.Abs(g.Confidence - 0.62f) < 0.001f, "noul: confidence = argmax");
        }

        // Below the task bar the recognizer didn't recognise a task → null (keep prior guidance).
        private static void TestBelowTaskBar()
        {
            var a = Answers(("task_follow", 0.2), ("task_gather", 0.18), ("task_idle", 0.3));
            Assert(DecisionParse.SystemoneAnswers(a, TASKS, BLOCKERS) == null, "noul: below bar -> no task");
        }

        // All questions scoring near zero is a "I don't know" → null.
        private static void TestAllZero()
        {
            var a = Answers(("task_follow", 0.0), ("task_gather", 0.0));
            Assert(DecisionParse.SystemoneAnswers(a, TASKS, BLOCKERS) == null, "noul: all-zero -> no task");
        }

        // The blocker is the highest blocker question; the label is recorded, not "blocked".
        private static void TestBlockerLabel()
        {
            var a = Answers(("task_gather", 0.7), ("block_busy", 0.71), ("block_moved", 0.2));
            var g = DecisionParse.SystemoneAnswers(a, TASKS, BLOCKERS);
            Assert(g != null && g.Blocked == "busy", "noul: blocked label is busy");
            Assert(g != null && g.Rationale.Length > 0, "noul: rationale mentions blocker");
        }

        // Nothing crossing the blocker bar → blocker is "none", but the task still counts.
        private static void TestNoBlocker()
        {
            var a = Answers(("task_gather", 0.7), ("block_busy", 0.3), ("block_moved", 0.2));
            var g = DecisionParse.SystemoneAnswers(a, TASKS, BLOCKERS);
            Assert(g != null && g.Blocked == "none", "noul: no blocker -> none");
        }

        // A noul value sent as a string is still parsed.
        private static void TestNoulStringValue()
        {
            var a = new Dictionary<string, object>
            {
                ["task_gather"] = new Dictionary<string, object> { ["noul"] = "0.77" },
            };
            var g = DecisionParse.SystemoneAnswers(a, TASKS, BLOCKERS);
            Assert(g != null && System.Math.Abs(g.Confidence - 0.77f) < 0.001f, "noul: stringified score parsed");
        }

        // Null answers / absent keys never throw.
        private static void TestNoulRobust()
        {
            Assert(DecisionParse.SystemoneAnswers((Dictionary<string, object>)null, TASKS, BLOCKERS) == null, "noul: null answers -> null");
            var a = new Dictionary<string, object>();
            Assert(DecisionParse.SystemoneAnswers(a, TASKS, BLOCKERS) == null, "noul: empty answers -> null");
        }

        internal static int Run()
    {
        TestClean();
        TestFenced();
        TestBareFence();
        TestMissingTask();
        TestZeroConfidence();
        TestStringConfidence();
        TestConfidenceClamp();
        TestNegativeConfidence();
        TestProseLeading();
        TestMissingOptionalFields();
        TestGarbage();
        TestUnparseableConfidence();
        TestStringFloat();
        TestArgmaxTask();
        TestBelowTaskBar();
        TestAllZero();
        TestBlockerLabel();
        TestNoBlocker();
        TestNoulStringValue();
        TestNoulRobust();
        if (_failed > 0)
        {
            System.Console.WriteLine("decisionparse: " + _failed + " assertion(s) failed");
            return 1;
        }
        System.Console.WriteLine("decisionparse: all OK");
        return 0;
    }

    internal static void Main()
    {
        int rc = Run();
        if (rc != 0) Environment.Exit(rc);
    }
}
