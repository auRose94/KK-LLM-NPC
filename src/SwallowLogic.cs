// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// SwallowLogic — the pure (no-Unity, no-NetStack) core of the swallow-vore feature:
// the trigger rule, the phase/scale math of a conversion, and the genes fingerprint
// formatting. Kept free of game types so tests/test_swallow.cs can compile it alone;
// src/Swallow.cs holds the runtime manager around it.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace KKLLMNPC
{
    // WHERE a swallow takes its victim in (config [LittleTreats] SwallowDestination,
    // F7 hotkey cycles it, WebUI settings toggle it). OFF is the default and the
    // point: with OFF no swallow trigger arms at all — a grabbed kobold released
    // near a head (or the player's own look-up drop) just drops like normal, so a
    // PrecisionGrabber bump can never start a digestion by accident. Picking a hole
    // aims the entry at that hole and names it in every narration. Lives in the
    // PURE file so the swallow test suite can compile it.
    public enum SwallowDestination
    {
        Off = 0,
        Mouth = 1,
        Anal = 2,
        Pussy = 3,
        Nipple = 4,
        Dick = 5,    // urethral entry — the eater's own groin (cock vore)
    }

    internal static class SwallowLogic
    {
        // Camera-forward.y above this counts as "looking up" for the swallow trigger.
        // Default is deliberately steep — asin(0.94) ≈ 70°+, close to max up — so a
        // casual drop can never eat someone by accident. Config-tunable
        // ([LittleTreats] SwallowLookUpDot; 0.97 ≈ 76°, 0.87 ≈ 60°).
        internal const float DefaultLookUpDot = 0.94f;

        internal const float PullSeconds = 2.2f;
        internal const float SettleSeconds = 6f;
        internal const float ScalePullEnd = 0.3f;   // scale after the swallow-in
        internal const float ScaleDigestEnd = 0.22f;
        internal const float ScaleSettleEnd = 0.02f;

        // The gesture rule: enabled + actually looking up + the thing in hand is a
        // person. Player victims are filtered by the caller's grief guard.
        public static bool ShouldTrigger(bool enabled, float cameraForwardY, float lookUpDot,
            bool victimIsPerson)
        {
            if (!enabled || !victimIsPerson) return false;
            return IsLookUp(cameraForwardY, lookUpDot);
        }

        public static bool IsLookUp(float cameraForwardY, float threshold)
        {
            return cameraForwardY >= threshold;
        }

        // Phase of a swallow: 0 = pull, 1 = digest, 2 = settle, -1 = done.
        public static int PhaseFor(float pull, float digest, float settle, float seconds)
        {
            if (seconds < pull) return 0;
            if (seconds < pull + digest) return 1;
            if (seconds < pull + digest + settle) return 2;
            return -1;
        }

        // 0..1 progress inside the current phase (-1 → 1).
        public static float PhaseT(float pull, float digest, float settle, float seconds)
        {
            if (seconds < pull) return pull <= 0f ? 1f : seconds / pull;
            if (seconds < pull + digest) return digest <= 0f ? 1f : (seconds - pull) / digest;
            if (seconds < pull + digest + settle) return settle <= 0f ? 1f : (seconds - pull - digest) / settle;
            return 1f;
        }

        // Victim scale through the phases: full → compact → gone.
        public static float ScaleFor(int phase, float phaseT)
        {
            switch (phase)
            {
                case 0: return Lerp(1f, ScalePullEnd, Smooth01(phaseT));
                case 1: return Lerp(ScalePullEnd, ScaleDigestEnd, Smooth01(phaseT));
                case 2: return Lerp(ScaleDigestEnd, ScaleSettleEnd, Smooth01(phaseT));
                default: return ScaleSettleEnd;
            }
        }

        public static float Lerp(float a, float b, float t)
        {
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return a + (b - a) * t;
        }

        public static float Smooth01(float x)
        {
            if (x < 0f) x = 0f;
            if (x > 1f) x = 1f;
            return x * x * (3f - 2f * x);
        }

        // Digest progress milestones (0..1 fractions of the digest phase) that get
        // narrated to the swallowed NPC, plus their indexes. One-shot per mark.
        internal static readonly float[] DigestMarks = { 0.33f, 0.66f, 0.92f };
        internal const int MarkChurn = 0;
        internal const int MarkKnead = 1;
        internal const int MarkEggForming = 2;

        // Smallest mark index the progress has crossed but we haven't narrated yet
        // (marks before `lastNarrated` are already told); -1 when nothing new.
        public static int MilestoneFor(float digestT, int lastNarrated)
        {
            for (int i = lastNarrated + 1; i < DigestMarks.Length; i++)
                if (digestT >= DigestMarks[i]) return i;
            return -1;
        }

        // ---- digest-abort chat commands ----
        public static readonly string[] AbortCommands =
        {
            "swallowabort", "abortswallow", "swallowstop", "stopswallow",
            "abortdigest", "stopdigest", "coughup", "spitout", "regurgitate", "vomitup",
        };

        // Letters-only lowercase normalization: "Swallow abort!" and "swallow_abort"
        // both become "swallowabort", so the player can type the words naturally.
        public static string NormalizeLetters(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char c in s)
                if (char.IsLetter(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public static bool MatchesAny(string normalized, string[] words)
        {
            if (string.IsNullOrEmpty(normalized) || words == null) return false;
            foreach (string w in words)
                if (w != null && normalized == w) return true;
            return false;
        }

        // ---- force-feed placement ----
        internal const float HeadAboveTolerance = 0.15f;

        // "Dropped on top of another's head": the victim must be at/above the
        // receiver's head bone (a shove from below isn't a face-shove).
        public static bool IsOnOrAboveHead(float victimY, float headY)
        {
            return victimY >= headY - HeadAboveTolerance;
        }

        // ---- Grow-mode sex genes ----
        // What absorbing a body does to the eater's curves and groin, computed from
        // the two bodies' numbers (no game types — src/Swallow.cs maps these onto
        // KoboldGenes). dickEquip != 0 means the body has a dick equipped (the game's
        // 1-based penis-database ID; 0 = none). Rules mirror the size/strength takes:
        //   breasts: eater's breastSize += victim's × frac (a flat meal adds nothing)
        //   an eater that ALREADY has a dick grows it by the victim's × frac (length
        //            and thickness together)
        //   a dickless eater that ate a dick-having victim GAINS that groin wholesale —
        //            equip id plus the victim's dick/ball sizes copied, not sampled
        //            (10% of a dick is invisible; absorbing it means owning it)
        // Null = nothing changed and the caller has no sex genes to apply.
        public sealed class GrowSexResult
        {
            public bool BreastsGrew;
            public bool DickGrew;         // eater's existing dick got bigger
            public bool DickGained;       // dickless eater took the victim's groin wholesale
            public float BreastSize;      // new breastSize (BreastsGrew)
            public float DickSize;        // new dickSize (DickGrew/DickGained)
            public float DickThickness;   // new dickThickness (DickGrew/DickGained)
            public float BallSize;        // new ballSize (DickGained — comes with the groin)
            public short DickEquip;       // new dickEquip (DickGained)
        }

        public static GrowSexResult GrowSexGenes(
            short eaterDickEquip, float eaterBreastSize, float eaterDickSize, float eaterDickThickness,
            short vicDickEquip, float vicBreastSize, float vicDickSize, float vicDickThickness,
            float vicBallSize, float frac)
        {
            if (frac < 0f) frac = 0f;
            if (frac > 1f) frac = 1f;
            var r = new GrowSexResult();
            if (vicBreastSize > 0f && frac > 0f)
            {
                r.BreastsGrew = true;
                r.BreastSize = eaterBreastSize + vicBreastSize * frac;
            }
            if (vicDickEquip != 0)
            {
                if (eaterDickEquip == 0)
                {
                    r.DickGained = true;
                    r.DickEquip = vicDickEquip;
                    r.DickSize = vicDickSize > 0f ? vicDickSize : 0f;
                    r.DickThickness = vicDickThickness > 0f ? vicDickThickness : 0f;
                    r.BallSize = vicBallSize > 0f ? vicBallSize : 0f;
                }
                else if (vicDickSize > 0f && frac > 0f)
                {
                    r.DickGrew = true;
                    r.DickSize = eaterDickSize + vicDickSize * frac;
                    r.DickThickness = eaterDickThickness + vicDickThickness * frac;
                }
            }
            return r.BreastsGrew || r.DickGrew || r.DickGained ? r : null;
        }

        // Stable string of a genes object's numbers, in a fixed field order — the
        // fingerprint the rebirth registry matches on. Floats must already be
        // normalized (half-precision round-tripped) or the serialization drift breaks
        // the match; the game-typed wrapper in src/Swallow.cs does that.
        public static string FingerprintFromNumbers(float[] floats, byte[] bytes, int dickEquip)
        {
            if (floats == null || bytes == null) return null;
            var parts = new List<string>();
            foreach (float f in floats)
                parts.Add(f.ToString("0.####", CultureInfo.InvariantCulture));
            foreach (byte b in bytes)
                parts.Add(b.ToString(CultureInfo.InvariantCulture));
            parts.Add(dickEquip.ToString(CultureInfo.InvariantCulture));
            return string.Join("_", parts.ToArray());
        }

        // ------------------------------------------------------------------
        // Destination picker (config enum SwallowDestination): the F7/WebUI cycle
        // order, the human label, and the entry phrase per hole. Pure.
        // ------------------------------------------------------------------
        // Cycle: off -> mouth -> anal -> pussy -> dick -> nipple -> off.
        public static SwallowDestination NextDestination(SwallowDestination d)
        {
            switch (d)
            {
                case SwallowDestination.Off: return SwallowDestination.Mouth;
                case SwallowDestination.Mouth: return SwallowDestination.Anal;
                case SwallowDestination.Anal: return SwallowDestination.Pussy;
                case SwallowDestination.Pussy: return SwallowDestination.Dick;
                case SwallowDestination.Dick: return SwallowDestination.Nipple;
                default: return SwallowDestination.Off;
            }
        }

        public static string DestinationLabel(SwallowDestination d)
        {
            switch (d)
            {
                case SwallowDestination.Mouth: return "MOUTH";
                case SwallowDestination.Anal: return "ANAL";
                case SwallowDestination.Pussy: return "PUSSY";
                case SwallowDestination.Nipple: return "NIPPLE";
                case SwallowDestination.Dick: return "DICK";
                default: return "OFF";
            }
        }

        // How a body "goes in" through each destination — the pull narration uses it.
        public static string EntryVerb(SwallowDestination d)
        {
            switch (d)
            {
                case SwallowDestination.Anal: return "pulled into their rear — taken in through the ass";
                case SwallowDestination.Pussy: return "drawn into their body — taken in through the pussy (unbirth)";
                case SwallowDestination.Nipple: return "drawn into their breast — taken in through the nipple";
                case SwallowDestination.Dick: return "drawn into their groin — taken in through the cock (urethra)";
                default: return "swallowed whole";
            }
        }

        // Where the victim RIDEs inside the eater — "stomach" only for a mouth entry.
        // Every other hole holds them deep inside the body; saying "stomach" there
        // confused the models ("a meal in my stomach" for an unbirth). Pure.
        public static string InsideNoun(SwallowDestination d)
        {
            return d == SwallowDestination.Mouth ? "stomach" : "body";
        }

        // Intake progress wording for the virtual-insertion narration (0..1).
        public static string InsertStageText(float t)
        {
            float clamped = t < 0f ? 0f : (t > 1f ? 1f : t);
            if (clamped >= 1f) return "fully in";
            if (clamped >= 0.75f) return "almost fully in";
            if (clamped >= 0.5f) return "about halfway in";
            if (clamped >= 0.25f) return "well in";
            return "barely in";
        }

        // VORE DRIVE flavor (agent-side encouragement): a 2D stat — appetite (how much
        // food-thoughts rise) and comfort (a per-body affinity for "invitingly big and
        // comfy"). Pure so tests cover the banding.
        public static string VoreFlavorText(float appetite, float comfort)
        {
            float a = appetite < 0f ? 0f : (appetite > 1f ? 1f : appetite);
            if (a < 0.2f) return null;
            if (a < 0.45f) return comfort >= 0.5f
                ? "a passing daydream about a comfortable, snug meal"
                : "kobolds look faintly tasty right now";
            if (a < 0.75f) return comfort >= 0.5f
                ? "you keep sizing kobolds up — who would be invitingly big and comfy to keep inside"
                : "you keep sizing kobolds up — several of them look tasty";
            return comfort >= 0.5f
                ? "a full, warm, comfortable meal is almost all you can think about — every kobold looks invitingly big and soft"
                : "food-thoughts dominate: kobolds look downright TASTY, and each of them would make a proper meal";
        }
    }
}