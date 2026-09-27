// Written by @auRose94 (https://github.com/auRose94) under MIT license.
// Pure C# context-compaction core — no UnityEngine dependency.
// Extracted from ContextManager.cs so it compiles as a standalone testable
// library (mcs -target:exe -out:/tmp/t_cm.exe tests/test_contextcore.cs \
//     src/ContextCore.cs src/Json.cs src/Constants.cs).
//
// ContextManager.cs is now a thin Unity-coupled adapter: it owns the wall-clock
// (Time.unscaledTime), the ModelProbe lookups, the config entries and the admin
// API model switch. Every decision it makes is delegated here, so the policy is
// testable without a game install.
//
// Contents:
//   ContextPolicy     — compaction escalation state machine + limit scaling
//   ContextMath       — token estimation / context fill ratio
//   ContextCompaction — fact merging + history summarization
//
using System;
using System.Collections.Generic;
using System.Text;

namespace KKLLMNPC
{
    // ------------------------------------------------------------------
    // ContextPolicy — escalation / de-escalation under context pressure
    // ------------------------------------------------------------------
    internal static class ContextPolicy
    {
        /// <summary>Highest compaction level. Level 5 means "try a bigger model".</summary>
        public const int MaxLevel = Consts.ContextManagerMaxCompactionLevel;

        /// <summary>Number of consecutive over-threshold turns before escalating 2 levels at once.</summary>
        public const int FastEscalateAfter = Consts.ContextManagerConsecutiveHighThreshold;

        /// <summary>
        /// True when the context is full enough to escalate.
        /// </summary>
        public static bool ShouldEscalate(float fillRatio)
        {
            return fillRatio > Consts.ContextManagerHighFillThreshold;
        }

        /// <summary>
        /// True when the context has eased enough to step back down. Never true
        /// at level 0 (nothing to step down from).
        /// </summary>
        public static bool ShouldDeEscalate(float fillRatio, int level)
        {
            return level > 0 && fillRatio < Consts.ContextManagerLowFillThreshold;
        }

        /// <summary>
        /// The level to move to after another over-threshold turn.
        /// Escalates 2 levels at once once the pressure has been sustained for
        /// <see cref="FastEscalateAfter"/> turns, otherwise 1. Clamped to
        /// <see cref="MaxLevel"/>.
        /// </summary>
        public static int NextEscalatedLevel(int level, int consecutiveHigh)
        {
            int step = consecutiveHigh >= FastEscalateAfter ? 2 : 1;
            return Math.Min(MaxLevel, level + step);
        }

        /// <summary>
        /// The level to step down to when pressure eases. One level at a time.
        /// </summary>
        public static int DeEscalatedLevel(int level)
        {
            return Math.Max(0, level - 1);
        }

        /// <summary>
        /// True when escalating would actually change anything. At
        /// <see cref="MaxLevel"/> this is false, so a saturated context stops
        /// re-reporting and the level-5 model switch is attempted only once per
        /// climb to the ceiling.
        /// </summary>
        public static bool IsEscalating(int level, int consecutiveHigh)
        {
            return NextEscalatedLevel(level, consecutiveHigh) > level;
        }

        // ---- dynamic limits -------------------------------------------------
        //
        // Each level halves the budget (floored), but the loop start index
        // differs per budget: history and thoughts thin out immediately, facts
        // only from level 2 (they're compact and expensive to lose), and the
        // chat log only from level 3 (it's the cheapest thing to drop).

        public static int MaxHistory(int level, int baseMax)
        {
            return Halve(level, baseMax, 0, 2);
        }

        public static int MaxFacts(int level, int baseMax)
        {
            return Halve(level, baseMax, 1, 3);
        }

        public static int MaxThoughts(int level, int baseMax)
        {
            return Halve(level, baseMax, 0, 1);
        }

        public static int MaxChatLog(int level, int baseMax)
        {
            return Halve(level, baseMax, 2, 2);
        }

        // Halve the budget once per level from startLevel upward, never below floor.
        private static int Halve(int level, int baseMax, int startLevel, int floor)
        {
            int max = baseMax;
            for (int i = startLevel; i < level && max > floor; i++)
                max = Math.Max(floor, max / 2);
            return max;
        }

        // ---- descriptions ---------------------------------------------------

        /// <summary>
        /// Human-readable description of a compaction level, for the log.
        /// Returns null for level 0 (nothing to report) and for level 5, whose
        /// message depends on the outcome of the model-switch attempt and is
        /// therefore produced by the Unity-coupled adapter.
        /// </summary>
        public static string Describe(int level)
        {
            switch (level)
            {
                case 1: return "compaction level 1: trimmed old history and thoughts";
                case 2: return "compaction level 2: aggressive fact trimming";
                case 3: return "compaction level 3: fact merging + history summary";
                case 4: return "compaction level 4: maximum compression — essentials only";
                default: return null;
            }
        }

        /// <summary>Short mode name surfaced to the model in the perception payload.</summary>
        public static string ModeName(int level)
        {
            switch (level)
            {
                case 1: return "trimmed";
                case 2: return "aggressive";
                case 3: return "merged";
                case 4: return "minimal";
                case 5: return "switching";
                default: return "normal";
            }
        }

        /// <summary>
        /// The perception JSON fragment describing compression state, or null at
        /// level 0 so the key is omitted entirely when not compressing.
        /// </summary>
        public static string StatusJson(int level)
        {
            if (level <= 0) return null;
            return "{\"level\":" + level
                + ",\"mode\":\"" + ModeName(level) + "\"}";
        }
    }

    // ------------------------------------------------------------------
    // ContextMath — token estimation
    // ------------------------------------------------------------------
    internal static class ContextMath
    {
        /// <summary>
        /// Rough token count for a payload of the given shape. Deliberately
        /// cheap and linear — it only has to be good enough to decide when the
        /// context window is filling up, and the per-bucket weights are all in
        /// Consts so they can be tuned in one place.
        /// </summary>
        public static int EstimateTokens(int factCount, int histCount, int thoughtCount,
            int chatCount, int nearbyCount)
        {
            return Consts.TokenEstimateSystemPrompt
                + (nearbyCount * Consts.TokenEstimatePerNearby)
                + (factCount * Consts.TokenEstimatePerFact)
                + (histCount * Consts.TokenEstimatePerHistory)
                + (thoughtCount * Consts.TokenEstimatePerThought)
                + (chatCount * Consts.TokenEstimatePerChatLine);
        }

        /// <summary>
        /// Fraction of the model's context window in use. Returns 0 when the
        /// context length is unknown (server not probed yet) — an unknown window
        /// must not look like an empty one, and must not look full either.
        /// </summary>
        public static float FillRatio(int factCount, int histCount, int thoughtCount,
            int chatCount, int nearbyCount, int contextLength)
        {
            if (contextLength <= 0) return 0f;
            return (float)EstimateTokens(factCount, histCount, thoughtCount, chatCount, nearbyCount) / contextLength;
        }
    }

    // ------------------------------------------------------------------
    // ContextCompaction — lossy reductions applied at high compaction
    // ------------------------------------------------------------------
    internal static class ContextCompaction
    {
        /// <summary>
        /// Collapse facts that share a category prefix (the text before the first
        /// ':') down to one entry, keeping the most recent. Fact text is stored
        /// oldest-first, so last-wins keeps the freshest version.
        /// </summary>
        public static List<string> MergeFacts(List<string> facts)
        {
            if (facts == null) return new List<string>();
            var merged = new Dictionary<string, string>();
            foreach (var f in facts)
            {
                if (f == null) continue;
                string prefix = f.Split(':')[0];
                merged[prefix] = f;  // last wins (most recent)
            }
            return new List<string>(merged.Values);
        }

        /// <summary>
        /// Keep the most recent <paramref name="keepRecent"/> history entries
        /// verbatim and collapse everything older into "(earlier: Nx action)"
        /// counts. Returns a JSON array so it can be dropped straight into the
        /// perception payload.
        /// </summary>
        public static string SummarizeHistory(List<string> history, int keepRecent)
        {
            if (history == null) return "[]";
            if (keepRecent < 0) keepRecent = 0;

            var result = new StringBuilder("[");
            bool first = true;

            int summarizeEnd = history.Count - keepRecent;
            if (summarizeEnd > 0)
            {
                var oldCounts = new Dictionary<string, int>();
                var order = new List<string>();
                for (int i = 0; i < summarizeEnd; i++)
                {
                    if (history[i] == null) continue;
                    string action = history[i].Split(new[] { ' ' }, 2)[0];
                    if (!oldCounts.ContainsKey(action)) order.Add(action);
                    int count;
                    oldCounts.TryGetValue(action, out count);
                    oldCounts[action] = count + 1;
                }
                foreach (var action in order)
                {
                    if (!first) result.Append(',');
                    first = false;
                    result.Append(Json.Write("(earlier: " + oldCounts[action] + "x " + action + ")"));
                }
            }

            // Recent entries verbatim.
            for (int i = Math.Max(0, summarizeEnd); i < history.Count; i++)
            {
                if (!first) result.Append(',');
                first = false;
                result.Append(Json.Write(history[i]));
            }

            result.Append(']');
            return result.ToString();
        }
    }
}
