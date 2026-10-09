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
    }
}