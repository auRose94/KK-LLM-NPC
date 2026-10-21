// Unit tests for TruncatedArgs — recovering action/say/scalar args from a length-cut JSON
// tool argument. Pure C#, no UnityEngine; compiled as part of run_tests.sh.
//
// These lock in the behavior small/low-capability models rely on: an `act` call whose response
// was cut off mid-JSON should still recover the action (and any say/scalars we can see) instead
// of falling through to a no-op.
using System;
using KKLLMNPC;

internal static class TestTruncatedArgs
{
    private static int _passed;
    private static int _failed;

    private static void Assert(bool cond, string what)
    {
        if (cond)
        {
            _passed++;
            Console.WriteLine("PASS " + what);
        }
        else
        {
            _failed++;
            Console.WriteLine("FAIL " + what);
        }
    }

    // A complete, well-formed object is fully recovered.
    private static void TestCompleteObject()
    {
        var j = TruncatedArgs.Salvage("{\"action\":\"walk\",\"say\":\"hello\"}");
        Assert(j != null, "complete: non-null");
        Assert(j != null && j.S("action") == "walk", "complete: action=walk");
        Assert(j != null && j.S("say") == "hello", "complete: say=hello");
    }

    // A truncated fragment with just the action key still yields an actionable call.
    private static void TestActionOnlyTruncated()
    {
        var j = TruncatedArgs.Salvage("{\"action\":\"go_to\"");
        Assert(j != null, "trunc-action: non-null");
        Assert(j != null && j.S("action") == "go_to", "trunc-action: action=go_to");
    }

    // Action + say survive truncation; trailing commas/args are ignored.
    private static void TestSayAndTrailing()
    {
        var j = TruncatedArgs.Salvage("{\"action\":\"say\",\"say\":\"hi there\",\"range\":5");
        Assert(j != null, "trunc-say: non-null");
        Assert(j != null && j.S("say") == "hi there", "trunc-say: say recovered");
        // The trailing scalar is dangling; it may or may not parse, but the action is what matters.
        Assert(j != null && j.S("action") == "say", "trunc-say: action=say");
    }

    // Scalar numeric args are recovered with their real numeric value (not the raw string).
    private static void TestScalarsNumeric()
    {
        var j = TruncatedArgs.Salvage("{\"action\":\"walk_ray\",\"speed\":-1.5,\"strafe\":2.0,\"range\":3");
        Assert(j != null, "scalars: non-null");
        Assert(j != null && System.Math.Abs(j.DB("speed", 0f) - (-1.5f)) < 0.001f, "scalars: speed=-1.5");
        Assert(j != null && System.Math.Abs(j.DB("strafe", 0f) - 2.0f) < 0.001f, "scalars: strafe=2.0");
        Assert(j != null && System.Math.Abs(j.DB("range", 0f) - 3.0f) < 0.001f, "scalars: range=3");
    }

    // The negative-scalar regex branch: a value starting with a minus sign.
    private static void TestNegativeScalar()
    {
        var j = TruncatedArgs.Salvage("{\"action\":\"crouch\",\"yaw_deg\":-45.5}");
        Assert(j != null && System.Math.Abs(j.DB("yaw_deg", 0f) - (-45.5f)) < 0.001f, "negative scalar: yaw_deg=-45.5");
    }

    // No action key at all => nothing actionable => null (caller falls through).
    private static void TestNoActionKey()
    {
        var j = TruncatedArgs.Salvage("{\"say\":\"orphan\"}");
        Assert(j == null, "no-action: null");
    }

    // A fragment with no JSON at all.
    private static void TestNoJsonAtAll()
    {
        var j = TruncatedArgs.Salvage("i'll just walk forward");
        Assert(j == null, "no-json: null");
    }

    // Empty / null inputs are null, never throw.
    private static void TestEmpty()
    {
        Assert(TruncatedArgs.Salvage("") == null, "empty-string: null");
        Assert(TruncatedArgs.Salvage(null) == null, "null: null");
    }

    internal static int Run()
    {
        TestCompleteObject();
        TestActionOnlyTruncated();
        TestSayAndTrailing();
        TestScalarsNumeric();
        TestNegativeScalar();
        TestNoActionKey();
        TestNoJsonAtAll();
        TestEmpty();

        if (_failed > 0)
        {
            Console.WriteLine("truncatedargs: " + _failed + " assertion(s) failed");
            return 1;
        }
        Console.WriteLine("truncatedargs: all OK");
        return 0;
    }

    internal static void Main()
    {
        int rc = Run();
        if (rc != 0) Environment.Exit(rc);
    }
}
