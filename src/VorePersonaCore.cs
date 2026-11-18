// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// VorePersonaCore — ROLLED VORE PERSONALITY (v1.11.0, RimVore-2 quirks parity
// + RimWorld-style identity traits, Rose's design): every body rolls a stable
// persona from its identity key — the SAME body always rolls the SAME persona,
// so it survives restarts and body swaps without any storage at all. Traits
// surface in the agent's vore state line (the LLM reads them as self-knowledge)
// and the appetite trait scales the vore-drive climb rate: a gluttonous agent
// yearns for the game's vore loops, a reluctant one mostly ignores them.
//
// Pure C#: no UnityEngine — compiled into the headless test suite.
using System;
using System.Collections.Generic;

namespace KKLLMNPC
{
    internal static class VorePersonaCore
    {
        internal sealed class Persona
        {
            public string Appetite;         // gluttonous / yearning / even-keeled / picky / reluctant
            public string Style;            // gentle / fierce / playful / shy
            public string Curiosity;        // adventurous / curious / cautious
            public double ClimbMultiplier;  // appetite trait's effect on the vore-drive climb
        }

        // Appetite trait → climb multiplier for the vore drive (how fast the agent
        // yearns for its next meal). Mirrors RimVore-2's quirk-weight idea.
        private static readonly string[][] AppetitePool =
        {
            new[] { "gluttonous", "1.6" },
            new[] { "yearning", "1.25" },
            new[] { "even-keeled", "1.0" },
            new[] { "picky", "0.75" },
            new[] { "reluctant", "0.45" },
        };
        private static readonly string[] StylePool = { "gentle", "fierce", "playful", "shy" };
        private static readonly string[] CuriosityPool = { "adventurous", "curious", "cautious" };

        // Deterministic 64-bit hash of the identity key (FNV-1a — stable across
        // runs, unlike Object.GetHashCode).
        internal static ulong Hash(string key)
        {
            if (string.IsNullOrEmpty(key)) key = "?";
            ulong h = 14695981039346656037UL;
            foreach (char c in key)
            {
                h ^= c;
                h *= 1099511628211UL;
            }
            return h;
        }

        private static string Pick(string[] pool, ulong hash)
        {
            return pool[hash % (ulong)pool.Length];
        }

        // Roll the persona for a body identity key.
        public static Persona Roll(string bodyKey)
        {
            ulong h = Hash(bodyKey ?? "");
            var p = new Persona();
            p.Appetite = Pick(AppetitePool[0], h);
            double mult = 1.0;
            foreach (var row in AppetitePool)
                if (row[0] == p.Appetite) mult = double.Parse(row[1], System.Globalization.CultureInfo.InvariantCulture);
            p.ClimbMultiplier = mult;
            p.Style = Pick(StylePool, h >> 16);
            p.Curiosity = Pick(CuriosityPool, h >> 32);
            return p;
        }

        // The self-knowledge line for the vore state (LLM-facing): identity-flavored,
        // honest about what it changes.
        public static string StateLine(Persona p)
        {
            if (p == null) return null;
            string pace = p.ClimbMultiplier > 1.2 ? "your vore drive climbs FAST"
                : p.ClimbMultiplier < 0.9 ? "your vore drive climbs slowly — meals rarely call to you"
                : "your vore drive climbs at a normal pace";
            return "vore persona (who you are, rolled into this body): " + p.Appetite + ", " + p.Style
                + ", " + p.Curiosity + " — " + pace + ". Let it color HOW you feel about vore, not WHETHER you obey";
        }
    }
}
