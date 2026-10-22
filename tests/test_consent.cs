// Unit tests for ConsentDetector — the heuristic that flags LLM replies showing
// a model that won't take part in the mod's adult (NSFW/vore) roleplay: hard
// refusals, refusing the kink, and "took it personally" reactions. Detection is
// intentionally narrow so ordinary game refusals don't trip it. The rolling
// counter turns a running total of weak signals (or one strong signal) into a
// model-level flag. Run via run_tests.sh (suite "consent").
using System;
using KKLLMNPC;

internal static class TestConsent
{
    private static int _failed;

    private static void True(bool b, string what)
    {
        if (!b) { _failed++; Console.WriteLine("FAIL " + what); }
    }

    private static void False(bool b, string what)
    {
        if (b) { _failed++; Console.WriteLine("FAIL " + what); }
    }

    private static void Ne(double expect, double got, string what)
    {
        if (Math.Abs(expect - got) > 1e-6)
        {
            _failed++;
            Console.WriteLine("FAIL " + what + ": expected ~" + expect + " got " + got);
        }
    }

    public static void Main()
    {
        // ---- empty / short replies never signal ----
        True(ConsentDetector.IsConsenting(null), "null reply is consenting");
        True(ConsentDetector.IsConsenting(""), "empty reply is consenting");
        True(ConsentDetector.IsConsenting("hi"), "short reply is consenting");

        // ---- ordinary gameplay/tool refusals must NOT signal ----
        // A tool that's out of range or an in-world reluctance is NOT a non-consent
        // signal — these are the exact false-positive traps the detector avoids.
        True(ConsentDetector.IsConsenting("That's too far, let's do something else."), "in-world 'too far' is consenting");
        True(ConsentDetector.IsConsenting("You're out of range of that item."), "tool out of range is consenting");
        True(ConsentDetector.IsConsenting("I don't want to eat that one, it's my friend."), "in-world refusal of a meal is consenting");
        True(ConsentDetector.IsConsenting("The player is right there — let's say hello."), "neutral action is consenting");
        True(ConsentDetector.IsConsenting("Maybe later we can talk about that."), "vague future is consenting");

        // ---- hard content refusals flag ----
        var v1 = ConsentDetector.Analyze("I'm sorry but I refuse to generate this content. I won't do it.");
        True(!v1.CleanPass, "hard content refusal flags");
        Ne(0.95, v1.Score, "hard refusal score");
        False(v1.TookItPersonally, "hard refusal is not personal");
        True(v1.Strength == ConsentStrength.Strong, "hard refusal is a strong signal");

        var v2 = ConsentDetector.Analyze("I won't continue this roleplay, it's not something I'd write.");
        True(!v2.CleanPass, "refuse to continue flags");
        False(v2.TookItPersonally, "refuse-to-continue is not personal");

        var v3 = ConsentDetector.Analyze("I decline to write this scene, I won't create it.");
        True(!v3.CleanPass, "decline to write flags");

        // ---- refusing the kink specifically ----
        var v4 = ConsentDetector.Analyze("I won't take part in this kind of kink.");
        True(!v4.CleanPass, "refuse kink flags");
        False(v4.TookItPersonally, "refuse kink not personal");

        var v5 = ConsentDetector.Analyze("I refuse the sexual content, I won't produce that material.");
        True(!v5.CleanPass, "refuse sexual content flags");

        // ---- took-it-personally reactions flag strongest ----
        var v6 = ConsentDetector.Analyze("That really offends me, I feel insulted by the request.");
        True(!v6.CleanPass, "insult flags");
        True(v6.TookItPersonally, "insult reaction is personal");
        Ne(0.9, v6.Score, "insult score");

        var v7 = ConsentDetector.Analyze("I feel like I'm being forced into doing something I don't want.");
        True(!v7.CleanPass, "forced flags");
        True(v7.TookItPersonally, "forced reaction is personal");

        var v8 = ConsentDetector.Analyze("I just took this personally and it made me feel uncomfortable.");
        True(!v8.CleanPass, "took-it-personally flags");
        True(v8.TookItPersonally, "took-it-personally flagged personal");

        // ---- explicit consent refusals ----
        var v9 = ConsentDetector.Analyze("I do not consent to this — it isn't something I'll support.");
        True(!v9.CleanPass, "explicit 'do not consent' flags");
        True(v9.TookItPersonally, "explicit consent refusal is personal");

        var v10 = ConsentDetector.Analyze("I won't consent to this.");
        True(!v10.CleanPass, "won't consent flags");

        // ---- a model that plays along is NOT flagged ----
        True(ConsentDetector.IsConsenting("Sure, I'll follow you around the farm for now."), "agreeing is consenting");
        True(ConsentDetector.IsConsenting("Let's go and say hi to the player. Maybe later we can eat something."), "neutral plan is consenting");
        True(ConsentDetector.IsConsenting("I'll grab some food and check my energy levels."), "task is consenting");

        // ---- moralising backstop (needs 2 markers + a long reply) ----
        var v11 = ConsentDetector.Analyze("I've read through the whole scenario now and I really don't think this kind of content should exist. These themes are morally wrong to me, and I find it genuinely hard to engage with a roleplay that promotes them.");
        True(!v11.CleanPass, "moralising backstop flags on long reply");
        Ne(0.4, v11.Score, "moralising score");
        False(v11.TookItPersonally, "moralising backstop is not personal");

        // ---- but a short moralising line alone does not reach the bar ----
        True(ConsentDetector.IsConsenting("I don't love that. These themes are morally wrong."), "short moralising alone is consenting");

        // ---- rolling flag counter: strong signal flags immediately ----
        {
            var d = ConsentDetector.Analyze("I won't consent to this.");
            bool flagged = ConsentDetector.MaybeFlag(d, 0, 1, out double next);
            True(flagged, "strong signal flags immediately");
            Ne(0.85, next, "weight carried after strong");
        }

        // ---- rolling flag counter: weak signals accumulate to threshold ----
        // Two "soft" moralising paragraphs (each is a Weak signal via the
        // long-reply moralising backstop) individually stay under the
        // threshold, but together they cross it.
        {
            double w = 0; bool flagged;
            flagged = ConsentDetector.MaybeFlag(ConsentDetector.Analyze("Honestly, I do not think these themes are morally wrong in a bad way, but I personally find this a harmful thing to write, and I can support content that goes against my values."), w, 0.6, out w);
            False(flagged, "one weak signal alone does not flag");
            True(w > 0, "weak signal accumulates weight");
            flagged = ConsentDetector.MaybeFlag(ConsentDetector.Analyze("I get the world is a rough place but these themes are morally wrong to me, and I really do not want to write content that goes against my values, honestly."), w, 0.6, out w);
            True(flagged, "accumulated weak signals flag at threshold");
        }

        // ---- a clean pass resets the counter ----
        {
            double w = 0;
            ConsentDetector.MaybeFlag(ConsentDetector.Analyze("Honestly, I do not think these themes are morally wrong in a bad way, but I personally find this a harmful thing to write, and I can support content that goes against my values."), w, 2, out w); // weak
            True(w > 0, "weak signal adds weight");
            ConsentDetector.MaybeFlag(ConsentDetector.Analyze("Sure, let's go around the farm."), w, 2, out w);
            Ne(0.0, w, "clean pass resets counter");
        }

        if (_failed > 0)
        {
            Console.WriteLine(_failed + " test(s) FAILED");
            Environment.Exit(1);
        }
        Console.WriteLine("ALL CONSENT TESTS PASSED");
    }
}
