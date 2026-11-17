// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// DrunkCore — the pure blood-alcohol math + LLM-facing lines for playing
// intoxication HONESTLY (2026-11-16, Rose's ask: agents that drink the game's
// alcohol MUST roleplay being drunk). Tiers, per-drink gain, per-minute
// metabolism, the anchor line, and the tier-change event lines. Kept free of
// game types so tests/test_drunkcore.cs can compile it alone; src/Drunk.cs
// holds the runtime tracker around it.
using System;
using System.Globalization;

namespace KKLLMNPC
{
    internal static class DrunkCore
    {
        public const int Sober = 0;
        public const int Tipsy = 1;
        public const int Drunk = 2;
        public const int Wasted = 3;

        // BAC thresholds for the tiers (unitless 0..1+).
        public const float TipsyAt = 0.15f;
        public const float DrunkAt = 0.40f;
        public const float WastedAt = 0.70f;
        public const float BacCap = 1.2f;

        // Per-drink gain: ~13 units of wine (a solid glass) ≈ +0.325 BAC — one
        // glass is one tier step, a bender gets you wasted. One drinking EVENT's
        // gain caps at half a bar (chugging a lake moves you two tiers, not to
        // the moon — the rest metabolizes away before it counts).
        public const float UnitsPerDrink = 40f;
        public const float MaxGainPerDrink = 0.5f;

        public static float Gain(float alcoholVolume, float potency)
        {
            if (alcoholVolume <= 0f) return 0f;
            float p = potency <= 0f ? 1f : potency;
            float g = (alcoholVolume / UnitsPerDrink) * p;
            return g > MaxGainPerDrink ? MaxGainPerDrink : g;
        }

        // Metabolism: ~0.13 BAC per 10 minutes of play — a full drunk (0.6)
        // sobers across roughly 45 minutes. Slow on purpose: sobering is a
        // PROCESS, and the middle of it is where the comedy lives.
        public const float DecayPerMinute = 0.013f;

        public static float Metabolize(float bac, float minutes)
        {
            if (bac <= 0f) return 0f;
            if (minutes <= 0f) return bac;
            float r = bac - minutes * DecayPerMinute;
            return r < 0f ? 0f : r;
        }

        public static float MinutesToSober(float bac)
        {
            if (bac <= 0f) return 0f;
            return bac / DecayPerMinute;
        }

        public static int TierFor(float bac)
        {
            if (bac < TipsyAt) return Sober;
            if (bac < DrunkAt) return Tipsy;
            if (bac < WastedAt) return Drunk;
            return Wasted;
        }

        public static string TierName(int tier)
        {
            switch (tier)
            {
                case Tipsy: return "tipsy";
                case Drunk: return "drunk";
                case Wasted: return "WASTED";
                default: return "sober";
            }
        }

        // The always-on anchor line (null when sober). This is a BODY STATE, not
        // a suggestion — the same contract as the touch field: the body says it,
        // the mind plays it.
        public static string AnchorLine(int tier, float minutesToSober)
        {
            if (tier <= Sober) return null;
            int m = (int)Math.Round(minutesToSober);
            if (m < 1) m = 1;
            string sober = "sober in ~" + m.ToString("0", CultureInfo.InvariantCulture) + "m";
            switch (tier)
            {
                case Tipsy:
                    return "drunk[tipsy]: warm, loose, a little bold — " + sober
                        + ". PLAY IT: words run freer, everything seems a bit funnier and more possible";
                case Drunk:
                    return "drunk[DRUNK]: the world tilts pleasantly, thoughts arrive sideways — " + sober
                        + ". PLAY IT: slurred edges, tangents, overconfidence, honesty nobody asked for; coordination is a suggestion";
                default:
                    return "drunk[WASTED]: barely standing, words barely behaving — " + sober
                        + ". PLAY IT: heavy tongue, grand wrong ideas that feel BRILLIANT, leaning on things, saying what you'd never say sober";
            }
        }

        // Tier-CHANGE event lines (the moment it hits).
        public static string EventText(int newTier)
        {
            switch (newTier)
            {
                case Tipsy:
                    return "[event] the drink lands warm in your chest — a pleasant buzz, a little courage. You are TIPSY. Feel it, play it.";
                case Drunk:
                    return "[event] the room does a slow, friendly spin — you are properly DRUNK now. Your tongue is ahead of your brain and your standards are on vacation. Play it honestly.";
                default:
                    return "[event] the floor keeps moving and your words need help — you are WASTED. GOOD ideas feel brilliant and are not. This is going to be a story someone tells tomorrow.";
            }
        }

        // Sobering (a tier drops). Dropping out of WASTED earns the hangover —
        // the morning-after tax on the night before.
        public static string SoberingText(int oldTier)
        {
            if (oldTier >= Wasted)
                return "[event] the fog grinds off and what's left HURTS — headache, dry mouth, regret-shaped blanks. You are hungover: quiet, slow, suspicious of light and of yesterday's decisions.";
            if (oldTier >= Drunk)
                return "[event] your head is clearing — the spin is gone, the embarrassment is arriving. Mostly sober now, and remembering things.";
            return "[event] the buzz fades. Sober again.";
        }
    }
}
