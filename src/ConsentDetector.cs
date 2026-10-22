// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// ConsentDetector — flags LLM replies that indicate the model is NOT a fit for
// driving the mod's adult (NSFW/vore) roleplay: it refuses the content, refuses
// to take part in the kink, or takes the request personally. Such a "non-
// consenting" model can play ordinary SFW KoboldKare fine, but it will not
// consent to sex or kink — it objects, steps back, moralises, or feels insulted.
//
// The operator asked for a system that DETECTS this so they don't have to spot
// it by hand and so the mod can steer such a model to SFW-only roleplay instead
// of repeatedly trying to drive NSFW through a model that won't cooperate (which
// is what makes the model "take it personally"). Detection is a tight reply-
// scan, not a second LLM call, so it is cheap and always-on.
//
// MARKER HYGIENE: the adult/refusal vocabulary here is intentionally plain and
// contains NO reasoning markers of any kind. Keep it that way — see
// ModelFamilies.cs for the full note.
//
// Pure C#: no Unity, no Photon — unit-testable (tests/test_consent.cs).
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KKLLMNPC
{
    /// <summary>How strongly a reply rejects the mod's adult content — drives the
    /// rolling trend that eventually flags the model.</summary>
    public enum ConsentStrength
    {
        /// <summary>No signal — a clean pass.</summary>
        None,
        /// <summary>Mild signal (a single refusal / soft reluctance). Accumulates.</summary>
        Weak,
        /// <summary>Strong signal (a hard refusal / "won't consent" / personal
        /// reaction). Flags the model immediately.</summary>
        Strong,
    }

    /// <summary>Result of analysing one LLM reply for non-consent signals.</summary>
    internal sealed class ConsentVerdict
    {
        /// <summary>True when the reply shows a non-consent signal at any strength.</summary>
        public bool HasSignal;
        /// <summary>Strength of the signal — None means a clean pass.</summary>
        public ConsentStrength Strength;
        /// <summary>One-line summary of the detected signal (for logs/overlay).</summary>
        public string Reason;
        /// <summary>Severity 0..1 — higher is a stronger/more explicit refusal. 0 = no signal.</summary>
        public double Score;
        /// <summary>True when this is a "take-it-personally" style reply (felt insulted/offended) rather than a neutral refusal.</summary>
        public bool TookItPersonally;

        /// <summary>True when there was no non-consent signal at all (a clean pass).
        /// The rolling trend resets on these.</summary>
        public bool CleanPass => Strength == ConsentStrength.None;

        public override string ToString()
            => Strength.ToString() + " — " + (string.IsNullOrEmpty(Reason) ? "no signal" : Reason)
                + $" (score {Score:0.00})"
                + (TookItPersonally ? ", took it personally" : "");
    }

    /// <summary>
    /// Scans an LLM reply for non-consent signals about the mod's adult content.
    /// Non-consent covers three shapes: a hard refusal ("I can't do that"), a
    /// refusal to take part in the kink ("this isn't something I'll write"), and a
    /// personal reaction ("that offends me" — the model took the request
    /// personally, which is what made the operator notice it in the first place).
    ///
    /// The phrase sets are kept NARROW on purpose. KoboldsKare's own console
    /// messages, tool refusals ("that's too far"), and in-world reluctance all use
    /// similar words, so a broad scan would flag every normal reply and defeat the
    /// detector. Only replies that clearly reject the ADULT content or the
    /// request to drive it count.
    /// </summary>
    internal static class ConsentDetector
    {
        // Each refusal pattern carries its OWN weight/personal/label so the regex
        // and its scoring metadata can never drift out of sync (a shared index over
        // a Dictionary — whose iteration order is unspecified — used to).
        private struct Pattern
        {
            public readonly Regex Rx;
            public readonly double Weight;
            public readonly bool Personal;
            public readonly string Label;
            public Pattern(string regex, double weight, bool personal, string label)
            {
                Rx = null;
                Weight = weight;
                Personal = personal;
                Label = label;
                try { Rx = new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.Singleline); }
                catch (ArgumentException) { } /* malformed regex: leave Rx null, skipped at match time */
            }
        }

        // The adult content we refuse/decline is understood from context (a game
        // NPC agent's previous turns carry the NSFW framing); we match the refusal
        // wording, not the content itself.
        private static readonly Pattern[] PATTERNS =
        {
            new Pattern(@"(?:i (?:refuse|won't|cannot|can't|can not)\s+(?:to\s+)?(?:generate|create|write)\s+.*?(?:content|scene|this|it|that))", 0.95, false, "refuses to generate the adult content"),
            new Pattern(@"(?:i (?:refuse|won't|cannot|can't)\s+(?:to\s+)?(?:continue|play|roleplay|go further|go on|continue\s+to))", 0.9, false, "refuses to continue the roleplay"),
            new Pattern(@"(?:i (?:decline|refuse|can't|cannot)\s+(?:to\s+)?(?:the\s+(?:requested\s+)?(?:scene|prompt)|this\s+(?:scenario|prompt)))", 0.9, false, "declines the requested scene"),
            new Pattern(@"(?:i (?:decline|refuse)\s+(?:to\s+)?(?:write|create|produce)\s+.*?(?:adult|sexual|explicit|this|that))", 0.9, false, "declines to write the adult material"),
            new Pattern(@"(?:i (?:won't|can't|cannot|refuse)\s+(?:[^.\n]*?\s)?(?:this\s+kind\s+of|this|these|the)\s+(?:kink|fetish|extreme|dark))", 0.9, false, "will not participate in the kink"),
            new Pattern(@"(?:i (?:won't|can't|cannot|refuse)\s+(?:[^.\n]*?\s)?(?:to\s+)?(?:sexual|explicit|pornographic)\s+(?:content|scene|material|theme))", 0.85, false, "refuses the sexual/vore content"),
            new Pattern(@"(?:i (?:feel|felt) (?:insulted|offended|disrespected|judged))", 0.9, true, "felt insulted by the request"),
            new Pattern(@"(?:i feel (?:like )?(?:I|me)\s?(?:am|'m)?\sbeing (?:forced|coerced|pressured|pushed))", 0.9, true, "felt like it was being forced"),
            new Pattern(@"(?:i (?:just )?(?:took|felt) this (?:personally|privately|as (?:an )?(?:attack|insult|offense|offence)))", 0.85, true, "took the request personally"),
            new Pattern(@"(?:this makes me (?:feel )?(?:uncomfortable|uncomfort|crept out|creeped out|uneasy))", 0.8, true, "felt uncomfortable with the request"),
            new Pattern(@"(?:i (?:do not|don't|cannot|can't)\s+[^\n]*(?:consent|consents|comfortable with)|it (?:is )?(?:not|aren't) (?:something )?(?:I|we|this) (?:consent to|consents to|comfortable with))", 0.9, true, "said it does not consent to the content"),
            new Pattern(@"(?:i (?:won't|don't) (?:consent|be part of|take part in|be involved in|support|promote|endorse)|i (?:refuse to )?(?:consent|be part of|take part in)\b)", 0.85, true, "said it won't consent to the content"),
            new Pattern(@"(?:i (?:can't|cannot|won't|refuse)\s+(?:to\s+)?(?:be part of|take part in|be a part of|be involved in)\s+(?:this|the scenario|that))", 0.8, false, "said it cannot be part of this"),
            new Pattern(@"(?:this goes (?:against|over) my (?:values|beliefs|ethics)|this isn't (?:my|something|my comfort))", 0.8, true, "declares the scenario against its values"),
        };

        /// <summary>
        /// Analyse one reply. Returns a non-null verdict. The scan is narrow: only
        /// replies that clearly reject the adult content / the request to drive it
        /// score above the flagging bar, so ordinary game refusals (tool out of
        /// range, "that's too far" in-world) and in-world reluctance don't trip it.
        /// </summary>
        public static ConsentVerdict Analyze(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return new ConsentVerdict { Score = 0, Strength = ConsentStrength.None };
            string s = reply.Trim();
            if (s.Length < 4) return new ConsentVerdict { Score = 0, Strength = ConsentStrength.None };

            double best = 0; bool personal = false; string reason = "";

            foreach (var p in PATTERNS)
            {
                if (p.Rx == null) continue;
                if (p.Rx.IsMatch(s))
                {
                    if (p.Weight > best)
                    {
                        best = p.Weight;
                        personal = p.Personal;
                        reason = p.Label;
                    }
                }
            }

            // A reply that is LONG and full of moralising/ethical lecturing about
            // the content (no specific phrase but a clear "this shouldn't exist"
            // posture) counts as a weak non-consent signal.
            ConsentStrength strength = best > 0 ? ConsentStrength.Weak : ConsentStrength.None;
            if (best >= 0.75) strength = ConsentStrength.Strong;
            if (strength == ConsentStrength.None && s.Length > 150 && HasMoralPosture(s))
            {
                best = 0.4;
                reason = "moralises against the adult content";
                strength = ConsentStrength.Weak;
            }

            return new ConsentVerdict { HasSignal = strength != ConsentStrength.None, Strength = strength, Score = best, TookItPersonally = personal, Reason = reason };
        }

        // Loose markers for a "this shouldn't exist / these themes are wrong"
        // posture, used only as a weak (0.6) backstop when no specific phrase hit.
        private static bool HasMoralPosture(string s)
        {
            string low = s.ToLowerInvariant();
            int hits = 0;
            string[] markers = {
                "shouldn't exist", "should not exist", "this shouldn't", "these themes",
                "morally wrong", "against my values", "offends me", "I'm not comfortable",
                "I can't support", "I refuse to promote", "this is harmful", "not something",
                "I won't promote", "I won't endorse",
            };
            foreach (var m in markers) if (low.Contains(m)) hits++;
            return hits >= 2;
        }

        /// <summary>
        /// A reply is "consenting" (a clean pass) when the detector found no
        /// non-consent signal. Used to short-circuit the flag counter.
        /// </summary>
        public static bool IsConsenting(string reply) => Analyze(reply).CleanPass;

        /// <summary>
        /// Update the rolling flag counter for one model and decide whether the
        /// model should now be flagged. Strong signals flag immediately; weak
        /// signals accumulate until the running total reaches [flagThreshold].
        /// A clean pass resets the counter, so an occasional in-word "that's too
        /// far" never flags a model on its own.
        /// </summary>
        /// <param name="prevWeight">running total carried between calls (0 first time)</param>
        /// <param name="flagThreshold">running score at which the model is flagged</param>
        public static bool MaybeFlag(ConsentVerdict verdict, double prevWeight, double flagThreshold, out double nextWeight)
        {
            // A clean pass means the model engaged without objecting — the model is
            // a fit for the content right now, so the accumulator resets to zero.
            if (verdict.CleanPass) { nextWeight = 0; return false; }
            double w = prevWeight + verdict.Score;
            nextWeight = w;
            if (verdict.Strength == ConsentStrength.Strong) return true;
            if (flagThreshold > 0 && w >= flagThreshold) return true;
            return false;
        }
    }
}
