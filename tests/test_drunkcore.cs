// Tests for DrunkCore.cs — the pure blood-alcohol math + lines behind the
// drunk-roleplay tracker (agents that drink MUST play it). Pure C#, no Unity.
// Run via run_tests.sh (suite "drunkcore").
using System;
using KKLLMNPC;

internal static class TestDrunkCore
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

    private static void Near(double expect, double got, string what)
    {
        if (Math.Abs(expect - got) > 1e-4)
        {
            _failed++;
            Console.WriteLine("FAIL " + what + ": expected ~" + expect + " got " + got);
        }
    }

    private static void True(bool b, string what)
    {
        if (!b) { _failed++; Console.WriteLine("FAIL " + what); }
    }

    public static void Main()
    {
        // ---- tiers ----
        Eq(0, DrunkCore.TierFor(0f), "sober at 0");
        Eq(0, DrunkCore.TierFor(0.14f), "just under tipsy is sober");
        Eq(1, DrunkCore.TierFor(0.15f), "tipsy threshold inclusive");
        Eq(1, DrunkCore.TierFor(0.39f), "still tipsy before drunk");
        Eq(2, DrunkCore.TierFor(0.40f), "drunk threshold inclusive");
        Eq(2, DrunkCore.TierFor(0.69f), "still drunk before wasted");
        Eq(3, DrunkCore.TierFor(0.70f), "wasted threshold inclusive");
        Eq(3, DrunkCore.TierFor(1.2f), "cap is wasted");

        // ---- gain per drink ----
        Near(0f, DrunkCore.Gain(0f, 1f), "no volume, no gain");
        Near(0.325, DrunkCore.Gain(13f, 1f), "a solid glass ≈ +0.325 (13 units / 40)");
        Near(0.5, DrunkCore.Gain(999f, 1f), "one drinking event caps at half a bar");
        Near(0.4875, DrunkCore.Gain(13f, 1.5f), "potency scales the gain");
        Near(0.5, DrunkCore.Gain(13f, 2f), "a doubled glass still respects the cap");
        Near(0.1625, DrunkCore.Gain(13f, 0.5f), "potency halves the gain");

        // ---- metabolism ----
        Near(0.6, DrunkCore.Metabolize(0.6f, 0f), "no time, no decay");
        Near(0.6 - 0.13, DrunkCore.Metabolize(0.6f, 10f), "0.13 per 10 minutes");
        Near(0f, DrunkCore.Metabolize(0.01f, 999f), "floors at zero");
        Near(0.6 / 0.013, DrunkCore.MinutesToSober(0.6f), "minutes-to-sober = bac / decay");
        True(DrunkCore.MinutesToSober(0.6f) > 40 && DrunkCore.MinutesToSober(0.6f) < 50,
            "a full drunk sobers across ~45 minutes");

        // ---- a bender walks the tiers, sobers, and hands back the hangover ----
        float bac = 0f;
        bac += DrunkCore.Gain(13f, 1f); True(bac >= 0.15f, "one glass lands tipsy");
        Eq(1, DrunkCore.TierFor(bac), "tier 1 after one glass");
        bac += DrunkCore.Gain(13f, 1f); True(bac >= 0.40f, "a second pour lands drunk");
        Eq(2, DrunkCore.TierFor(bac), "tier 2 after the second");
        bac += DrunkCore.Gain(13f, 1f); Eq(3, DrunkCore.TierFor(bac), "a third glass is a bender — wasted");
        bac = DrunkCore.Metabolize(bac, 120f); True(bac < 0.15f, "two hours sobers anything");
        Eq(0, DrunkCore.TierFor(bac), "sober after two hours");

        // ---- lines ----
        Eq(null, DrunkCore.AnchorLine(0, 0), "sober anchor is null");
        True(DrunkCore.AnchorLine(1, 12f).Contains("tipsy") && DrunkCore.AnchorLine(1, 12f).Contains("PLAY IT"),
            "tipsy anchor names the tier and the contract");
        True(DrunkCore.AnchorLine(2, 30f).Contains("DRUNK"), "drunk anchor names the tier");
        True(DrunkCore.AnchorLine(3, 60f).Contains("WASTED"), "wasted anchor names the tier");
        True(DrunkCore.AnchorLine(2, 30f).Contains("sober in ~30m"), "anchor carries minutes-to-sober");
        True(DrunkCore.EventText(1).StartsWith("[event]"), "tier events are [event]-tagged");
        True(DrunkCore.EventText(3).Contains("WASTED"), "the wasted hit says so");
        True(DrunkCore.SoberingText(3).Contains("hungover"), "coming down from wasted is a hangover");
        True(DrunkCore.SoberingText(2).Contains("clearing"), "coming down from drunk is gentler");
        True(DrunkCore.SoberingText(1).Contains("fades"), "coming down from tipsy barely mentions it");
        Eq("tipsy", DrunkCore.TierName(1), "tier names");
        Eq("WASTED", DrunkCore.TierName(3), "tier names (wasted shouts)");

        if (_failed == 0) Console.WriteLine("drunkcore: all OK");
        else Console.WriteLine("drunkcore: " + _failed + " failure(s)");
        Environment.Exit(_failed);
    }
}
