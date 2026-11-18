// Tests for VorePersonaCore.cs — rolled vore personality (RimVore-2 quirks
// parity): deterministic per identity key (stable across sessions without
// storage), appetite multiplier from the trait, self-knowledge line. Pure C#,
// no UnityEngine; compiled as part of run_tests.sh.
using System;
using KKLLMNPC;

internal static class TestVorePersona
{
    private static int _passed;
    private static int _failed;

    private static void True(bool ok, string what)
    {
        if (!ok) { _failed++; Console.WriteLine("  FAIL: " + what); }
        else _passed++;
    }

    private static void TestRoll()
    {
        // Deterministic: same key → same persona.
        var a = VorePersonaCore.Roll("Kobold-ee4eb91abe91a675ef3d1644b24d039-1002");
        var b = VorePersonaCore.Roll("Kobold-ee4eb91abe91a675ef3d1644b24d039-1002");
        True(a.Appetite == b.Appetite && a.Style == b.Style && a.Curiosity == b.Curiosity, "roll: deterministic per key");
        // Multiplier matches the appetite trait's table row.
        True((a.Appetite == "gluttonous" && a.ClimbMultiplier > 1.2)
            || (a.Appetite == "yearning" && a.ClimbMultiplier > 1.0 && a.ClimbMultiplier <= 1.3)
            || (a.Appetite == "even-keeled" && a.ClimbMultiplier == 1.0)
            || (a.Appetite == "picky" && a.ClimbMultiplier < 1.0 && a.ClimbMultiplier > 0.5)
            || (a.Appetite == "reluctant" && a.ClimbMultiplier < 0.6),
            "roll: multiplier matches the appetite trait");
        // Different keys roll differently (a 15-trait space — two fixed keys
        // colliding on all three traits is vanishingly unlikely).
        var c = VorePersonaCore.Roll("Lovander-f7303aa48c0f3427fa49b4a3609bef5-361");
        True(a.Appetite != c.Appetite || a.Style != c.Style || a.Curiosity != c.Curiosity, "roll: distinct bodies differ");
        // Traits are drawn from the pools.
        string[] appetite = { "gluttonous", "yearning", "even-keeled", "picky", "reluctant" };
        string[] style = { "gentle", "fierce", "playful", "shy" };
        string[] curiosity = { "adventurous", "curious", "cautious" };
        True(Array.IndexOf(appetite, a.Appetite) >= 0, "roll: appetite in pool");
        True(Array.IndexOf(style, a.Style) >= 0, "roll: style in pool");
        True(Array.IndexOf(curiosity, a.Curiosity) >= 0, "roll: curiosity in pool");
        // Junk/null keys still roll (the "?" fallback).
        True(VorePersonaCore.Roll(null) != null, "roll: null key still yields a persona");
    }

    private static void TestStateLine()
    {
        var p = VorePersonaCore.Roll("Kobold-ee4eb91abe91a675ef3d1644b24d039-1002");
        string line = VorePersonaCore.StateLine(p);
        True(line != null && line.Contains("vore persona") && line.Contains(p.Appetite), "line: names the persona");
        True(line.Contains("obey"), "line: carries the obey-not-feel contract");
        True(VorePersonaCore.StateLine(null) == null, "line: null persona → null");
    }

    internal static int Run()
    {
        TestRoll();
        TestStateLine();

        if (_failed > 0)
        {
            Console.WriteLine("vorepersona: " + _failed + " assertion(s) failed");
            return 1;
        }
        Console.WriteLine("vorepersona: all OK");
        return 0;
    }

    internal static void Main()
    {
        int rc = Run();
        if (rc != 0) Environment.Exit(rc);
    }
}
