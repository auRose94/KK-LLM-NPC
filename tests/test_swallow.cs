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

        // ---- Grow sex genes (curves/groin absorption) ----
        // Busty female meal into a flat-chested, dickless male eater: breasts grow by
        // the fraction, no groin change.
        var sx1 = SwallowLogic.GrowSexGenes(0, 2.5f, 0f, 0f, 0, 15f, 0f, 0f, 0f, 0.1f);
        True(sx1 != null && sx1.BreastsGrew && !sx1.DickGrew && !sx1.DickGained, "busty meal → breasts grow only");
        Near(4f, sx1.BreastSize, "breast gain = eater 2.5 + 10% of 15 → 4.0");
        // Flat meal on every curve: nothing changes → null (caller applies no genes).
        Eq(null, SwallowLogic.GrowSexGenes(0, 2.5f, 0f, 0f, 0, 0f, 0f, 0f, 0f, 0.1f),
            "flat dickless meal → nothing to apply");
        // A zero percent also absorbs nothing.
        Eq(null, SwallowLogic.GrowSexGenes(0, 2.5f, 0f, 0f, 0, 20f, 0f, 0f, 0f, 0f),
            "0% take → nothing to apply");
        // The fraction is clamped both ways.
        Near(5f, SwallowLogic.GrowSexGenes(0, 2.5f, 0f, 0f, 0, 2.5f, 0f, 0f, 0f, 2f).BreastSize,
            "frac > 1 clamps to a full 100% take");
        // Two equipped groins: the eater's dick grows by the meal's (length + thickness
        // together), the equip id is untouched.
        var sx2 = SwallowLogic.GrowSexGenes(1, 5f, 12f, 2f, 2, 0f, 20f, 4f, 9f, 0.5f);
        True(sx2 != null && sx2.DickGrew && !sx2.DickGained && !sx2.BreastsGrew, "dicked eater + dicked meal → own dick grows");
        Near(22f, sx2.DickSize, "dick length = 12 + 50% of 20 → 22");
        Near(4f, sx2.DickThickness, "dick thickness = 2 + 50% of 4 → 4");
        Eq(0, (int)sx2.DickEquip, "no equip change when the eater already had a dick");
        // A dickless eater that ate a dick-having victim GAINS the groin wholesale:
        // equip id copied, sizes copied exactly (no fraction stub).
        var sx3 = SwallowLogic.GrowSexGenes(0, 2.5f, 0f, 0f, 7, 0f, 18f, 3f, 9f, 0.1f);
        True(sx3 != null && sx3.DickGained && !sx3.DickGrew, "dickless eater's gain is wholesale, not growth");
        Near(18f, sx3.DickSize, "gained length = the victim's exactly");
        Near(3f, sx3.DickThickness, "gained thickness = the victim's exactly");
        Near(9f, sx3.BallSize, "balls come with the groin");
        Eq(7, (int)sx3.DickEquip, "gained equip id = the victim's");
        // A dickless meal never strips or grows the eater's existing dick.
        Eq(null, SwallowLogic.GrowSexGenes(4, 0f, 15f, 2f, 0, 0f, 0f, 0f, 0f, 0.1f),
            "dickless meal → the eater's own dick is untouched");
        // Negative fractional input can't invert a take (clamped to 0 → nothing applies).
        Eq(null, SwallowLogic.GrowSexGenes(0, 2.5f, 0f, 0f, 0, 5f, 0f, 0f, 0f, -0.5f),
            "negative frac clamps to 0 — no shrinking takes");

        // --- destination picker (F7/WebUI cycle): OFF is the accident-stop, and the
        // cycle must return OFF → mouth → anal → pussy → nipple → OFF in order. ---
        Eq("Mouth", SwallowLogic.NextDestination(SwallowDestination.Off), "off cycles to mouth");
        Eq("Anal", SwallowLogic.NextDestination(SwallowDestination.Mouth), "mouth cycles to anal");
        Eq("Pussy", SwallowLogic.NextDestination(SwallowDestination.Anal), "anal cycles to pussy");
        Eq("Dick", SwallowLogic.NextDestination(SwallowDestination.Pussy), "pussy cycles to dick");
        Eq("Nipple", SwallowLogic.NextDestination(SwallowDestination.Dick), "dick cycles to nipple");
        Eq("Off", SwallowLogic.NextDestination(SwallowDestination.Nipple), "nipple cycles back to off");
        Eq("OFF", SwallowLogic.DestinationLabel(SwallowDestination.Off), "off label");
        Eq("MOUTH", SwallowLogic.DestinationLabel(SwallowDestination.Mouth), "mouth label");
        Eq("ANAL", SwallowLogic.DestinationLabel(SwallowDestination.Anal), "anal label");
        Eq("PUSSY", SwallowLogic.DestinationLabel(SwallowDestination.Pussy), "pussy label");
        Eq("DICK", SwallowLogic.DestinationLabel(SwallowDestination.Dick), "dick label");
        Eq("NIPPLE", SwallowLogic.DestinationLabel(SwallowDestination.Nipple), "nipple label");
        // Entry narration names the hole for every non-mouth destination; mouth stays
        // the classic "swallowed whole".
        Eq("swallowed whole", SwallowLogic.EntryVerb(SwallowDestination.Mouth), "mouth entry verb");
        Eq(true, SwallowLogic.EntryVerb(SwallowDestination.Anal).Contains("ass"), "anal entry names the ass");
        Eq(true, SwallowLogic.EntryVerb(SwallowDestination.Pussy).Contains("pussy"), "pussy entry names the pussy");
        Eq(true, SwallowLogic.EntryVerb(SwallowDestination.Dick).Contains("cock"), "dick entry names the cock");
        Eq(true, SwallowLogic.EntryVerb(SwallowDestination.Nipple).Contains("nipple"), "nipple entry names the nipple");

        // --- hole-aware inside wording: "stomach" is a MOUTH-only noun — models kept
        // recasting unbirths/cock-vore/anals as "a meal in my stomach". ---
        Eq("stomach", SwallowLogic.InsideNoun(SwallowDestination.Mouth), "mouth rides in the stomach");
        Eq("body", SwallowLogic.InsideNoun(SwallowDestination.Anal), "anal rides in the body");
        Eq("body", SwallowLogic.InsideNoun(SwallowDestination.Pussy), "pussy rides in the body");
        Eq("body", SwallowLogic.InsideNoun(SwallowDestination.Dick), "dick rides in the body");
        Eq("body", SwallowLogic.InsideNoun(SwallowDestination.Nipple), "nipple rides in the body");

        // --- intake progress wording (virtual insertion, % in feel/anchor) ---
        Eq("barely in", SwallowLogic.InsertStageText(0.1f), "10% barely");
        Eq("about halfway in", SwallowLogic.InsertStageText(0.5f), "50% halfway");
        Eq("almost fully in", SwallowLogic.InsertStageText(0.8f), "80% almost");
        Eq("fully in", SwallowLogic.InsertStageText(1.0f), "100% fully");
        Eq("fully in", SwallowLogic.InsertStageText(5f), "clamps over-unity");

        // --- vore drive flavor (2D: appetite + comfort) ---
        Eq(null, SwallowLogic.VoreFlavorText(0.1f, 0.5f), "below threshold: no flavor");
        Eq(true, SwallowLogic.VoreFlavorText(0.3f, 0.8f).IndexOf("comfortable", StringComparison.OrdinalIgnoreCase) >= 0,
            "low-mid appetite + comfort: comfy-daydream flavor");
        Eq(true, SwallowLogic.VoreFlavorText(0.3f, 0.2f).Contains("tasty"), "low-mid appetite + low comfort: tasty flavor");
        Eq(true, SwallowLogic.VoreFlavorText(0.9f, 0.9f).Contains("invitingly big"), "max appetite + comfort: invitingly big");
        Eq(true, SwallowLogic.VoreFlavorText(0.9f, 0.1f).Contains("TASTY"), "max appetite + low comfort: TASTY");
        Eq(true, SwallowLogic.VoreFlavorText(5f, -2f).Length > 0, "clamps over/under-unity inputs");

        if (_failed == 0) Console.WriteLine("test_swallow: all OK");
        else Console.WriteLine("test_swallow: " + _failed + " failure(s)");
        Environment.Exit(_failed);
    }
}