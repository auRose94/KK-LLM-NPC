// Unit tests for the swallow-vore feature's pure core (SwallowLogic): the
// release-while-looking-up trigger rule, the pull→digest→settle conversion
// phases, scale curve, digest narration milestones, and the genes fingerprint
// format that the egg→kobold rebirth registry matches on.
// Run via run_tests.sh (suite "swallow").
using System;
using KKLLMNPC;

internal static class TestSwallow
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

    private static void Near(double expect, double got, string what)
    {
        if (Math.Abs(expect - got) > 1e-4)
        {
            _failed++;
            Console.WriteLine("FAIL " + what + ": expected ~" + expect + " got " + got);
        }
    }

    public static void Main()
    {
        const float pull = SwallowLogic.PullSeconds;      // 2.2
        const float settle = SwallowLogic.SettleSeconds;  // 6
        float digest = 45f;                               // the config default

        // ---- trigger rule ----
        Eq(false, SwallowLogic.ShouldTrigger(false, 1f, SwallowLogic.DefaultLookUpDot, true),
            "disabled → no trigger");
        Eq(false, SwallowLogic.ShouldTrigger(true, 1f, SwallowLogic.DefaultLookUpDot, false),
            "non-person victim → no trigger");
        Eq(false, SwallowLogic.ShouldTrigger(true, 0.2f, SwallowLogic.DefaultLookUpDot, true),
            "looking down → no trigger");
        Eq(true, SwallowLogic.ShouldTrigger(true, 1f, SwallowLogic.DefaultLookUpDot, true),
            "looking up + person → trigger");
        Eq(true, SwallowLogic.IsLookUp(SwallowLogic.DefaultLookUpDot, SwallowLogic.DefaultLookUpDot),
            "exactly at threshold counts as looking up");
        Eq(false, SwallowLogic.IsLookUp(0.9399f, SwallowLogic.DefaultLookUpDot),
            "just below the steep threshold does not");
        Eq(true, SwallowLogic.IsLookUp(0.97f, 0.94f), "skyward pitch passes");
        Eq(false, SwallowLogic.IsLookUp(0.9f, 0.94f), "a moderately raised face is below the steep gate");
        Eq(false, SwallowLogic.IsLookUp(-1f, 0.94f), "looking at the floor fails");

        // ---- phase timeline (default 45s digest → total 53.2s) ----
        Eq(0, SwallowLogic.PhaseFor(pull, digest, settle, 0f), "t=0 → pull");
        Eq(0, SwallowLogic.PhaseFor(pull, digest, settle, pull - 0.01f), "just before pull end → pull");
        Eq(1, SwallowLogic.PhaseFor(pull, digest, settle, pull), "pull end → digest");
        Eq(1, SwallowLogic.PhaseFor(pull, digest, settle, pull + digest - 0.01f), "just before digest end → digest");
        Eq(2, SwallowLogic.PhaseFor(pull, digest, settle, pull + digest), "digest end → settle");
        Eq(-1, SwallowLogic.PhaseFor(pull, digest, settle, pull + digest + settle), "settle end → done");
        Eq(-1, SwallowLogic.PhaseFor(pull, digest, settle, pull + digest + settle + 500f), "long after → done");
        Eq(2, SwallowLogic.PhaseFor(pull, 0f, settle, pull), "zero digest at the boundary → already settling");

        // ---- phase progress ----
        Eq(0.0, (double)SwallowLogic.PhaseT(pull, digest, settle, 0f), "phaseT starts at 0");
        Near(0.5, SwallowLogic.PhaseT(pull, digest, settle, pull / 2f), "mid-pull ≈ 0.5");
        Near(0.25, SwallowLogic.PhaseT(pull, digest, settle, pull + digest * 0.25f), "digest 25%");
        Near(1.0, SwallowLogic.PhaseT(pull, digest, settle, 9999f), "past the end → 1");

        // ---- scale curve: 1 → 0.3 → 0.22 → 0.02 ----
        Near(1.0, SwallowLogic.ScaleFor(0, 0f), "pull start = full size");
        Near(SwallowLogic.ScalePullEnd, SwallowLogic.ScaleFor(0, 1f), "pull end = swallow compacted");
        Near(0.5f * (SwallowLogic.ScalePullEnd + SwallowLogic.ScaleDigestEnd),
            SwallowLogic.ScaleFor(1, 0.5f), "mid-digest lies between pull/digest ends");
        Near(SwallowLogic.ScaleDigestEnd, SwallowLogic.ScaleFor(1, 1f), "digest end");
        Near(SwallowLogic.ScaleSettleEnd, SwallowLogic.ScaleFor(2, 1f), "settle end ≈ nothing");
        Near(SwallowLogic.ScaleSettleEnd, SwallowLogic.ScaleFor(42, 1f), "unknown phase resolves to settle end");

        // monotone non-increasing over the whole run
        float prev = SwallowLogic.ScaleFor(0, 0f);
        bool monotone = true;
        for (float s = 0f; s <= pull + digest + settle + 0.01f; s += 0.37f)
        {
            int ph = SwallowLogic.PhaseFor(pull, digest, settle, s);
            float scale = SwallowLogic.ScaleFor(ph, SwallowLogic.PhaseT(pull, digest, settle, s));
            if (scale > prev + 1e-5f) { monotone = false; break; }
            prev = scale;
        }
        True(monotone, "victim only ever shrinks through the conversion");

        // ---- digest narration milestones (one-shot, in order) ----
        Eq(-1, SwallowLogic.MilestoneFor(0.2f, -1), "early digest → nothing to narrate");
        Eq(0, SwallowLogic.MilestoneFor(0.34f, -1), "churn mark crossed");
        Eq(1, SwallowLogic.MilestoneFor(0.71f, 0), "knead mark crossed after churn");
        Eq(2, SwallowLogic.MilestoneFor(0.95f, 1), "egg-forming mark after knead");
        Eq(-1, SwallowLogic.MilestoneFor(0.95f, 2), "all marks narrated → quiet");
        // a big jump narrates just the next mark (caller re-polls for the rest)
        Eq(0, SwallowLogic.MilestoneFor(0.99f, -1), "never skips marks by more than one poll");

        // ---- genes fingerprint ----
        Eq(null, SwallowLogic.FingerprintFromNumbers(null, new byte[] { 1, 2 }, 3), "null floats → null");
        Eq(null, SwallowLogic.FingerprintFromNumbers(new float[] { 1f }, null, 3), "null bytes → null");
        var f1 = SwallowLogic.FingerprintFromNumbers(
            new[] { 5f, 20f, 0f }, new byte[] { 3, 3, 128, 128, 1, 0 }, -1);
        var f1again = SwallowLogic.FingerprintFromNumbers(
            new[] { 5f, 20f, 0f }, new byte[] { 3, 3, 128, 128, 1, 0 }, -1);
        var f2 = SwallowLogic.FingerprintFromNumbers(
            new[] { 5f, 20.0001f, 0f }, new byte[] { 3, 3, 128, 128, 1, 0 }, -1);
        var f3 = SwallowLogic.FingerprintFromNumbers(
            new[] { 5f, 20f, 0f }, new byte[] { 3, 3, 128, 128, 1, 0 }, 5);
        Eq(f1, f1again, "fingerprint is stable");
        True(f1 != f2, "float change → different fingerprint");
        True(f1 != f3, "dickEquip change → different fingerprint");
        True(f1.Contains("20") && f1.Contains("128"), "fingerprint carries the raw numbers");
        // invariant formatting even under exotic locales (decimal point, not comma)
        True(f1.IndexOf(',') < 0, "fingerprint uses '_' separators only");

        // ---- lerp/smooth edges ----
        Eq(0.0, (double)SwallowLogic.Smooth01(0f), "smooth at 0");
        Eq(1.0, (double)SwallowLogic.Smooth01(1f), "smooth at 1");
        Near(0.5, SwallowLogic.Smooth01(0.5f), "smooth at the midpoint");
        Eq(0.0, (double)SwallowLogic.Smooth01(-2f), "smooth clamps low");
        Eq(1.0, (double)SwallowLogic.Smooth01(9f), "smooth clamps high");
        Near(2f, SwallowLogic.Lerp(1f, 5f, 0.25f), "lerp mid");
        Eq(5.0, (double)SwallowLogic.Lerp(1f, 5f, 7f), "lerp clamps high");
        Eq(1.0, (double)SwallowLogic.Lerp(1f, 5f, -7f), "lerp clamps low");

        // ---- digest-abort command matching (chat box, no cheats) ----
        Eq(true, SwallowLogic.MatchesAny(SwallowLogic.NormalizeLetters("Swallow abort!"), SwallowLogic.AbortCommands),
            "natural phrase matches");
        Eq(true, SwallowLogic.MatchesAny(SwallowLogic.NormalizeLetters("swallow_abort"), SwallowLogic.AbortCommands),
            "underscore form matches");
        Eq(true, SwallowLogic.MatchesAny(SwallowLogic.NormalizeLetters("SPIT OUT"), SwallowLogic.AbortCommands),
            "case-insensitive");
        Eq(true, SwallowLogic.MatchesAny(SwallowLogic.NormalizeLetters("cough up"), SwallowLogic.AbortCommands),
            "cough up matches");
        Eq(true, SwallowLogic.MatchesAny(SwallowLogic.NormalizeLetters("regurgitate"), SwallowLogic.AbortCommands),
            "single word matches");
        Eq(false, SwallowLogic.MatchesAny(SwallowLogic.NormalizeLetters("hi friends"), SwallowLogic.AbortCommands),
            "normal chat never matches");
        Eq(false, SwallowLogic.MatchesAny(SwallowLogic.NormalizeLetters("spitting out juice"), SwallowLogic.AbortCommands),
            "phrase inside a sentence does not match (exact only)");
        Eq("", SwallowLogic.NormalizeLetters(""), "empty → empty");
        Eq("", SwallowLogic.NormalizeLetters(null), "null → empty");
        Eq(false, SwallowLogic.MatchesAny(SwallowLogic.NormalizeLetters(null), SwallowLogic.AbortCommands),
            "null input never matches");

        // ---- force-feed placement (dropped on top of a head) ----
        Eq(true, SwallowLogic.IsOnOrAboveHead(1.2f, 1.0f), "victim clearly above the head → head-shove");
        Eq(true, SwallowLogic.IsOnOrAboveHead(1.0f, 1.0f), "at head level → head-shove");
        Eq(true, SwallowLogic.IsOnOrAboveHead(0.9f, 1.0f), "slightly below the head (within tolerance) → head-shove");
        Eq(false, SwallowLogic.IsOnOrAboveHead(0.4f, 1.0f), "well below the head → shoved from underneath, not a head-shove");
        Eq(false, SwallowLogic.IsOnOrAboveHead(0f, 1.0f), "at the receiver's feet → no head-shove");

        if (_failed == 0) Console.WriteLine("test_swallow: all OK");
        else Console.WriteLine("test_swallow: " + _failed + " failure(s)");
        Environment.Exit(_failed);
    }
}