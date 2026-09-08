// Unit tests for ContextManager.cs — dynamic context compaction.
// These tests validate the compaction escalation/de-escalation logic.

namespace KKLLMNPC.Tests
{
    using KKLLMNPC;
    using System;
    using System.Collections.Generic;

    public static class ContextManagerTests
    {
        // Note: ContextManager requires an NPCInstance. These tests
        // validate the algorithmic logic that can be tested in isolation.

        public static void TestCompactionEscalation()
        {
            // Simulate compaction escalation without a real NPCInstance.
            // Level 0 → 1 at >90% fill, → 2 at >90% for 3+ turns, → 3+ at 90%+

            // Level 0 → 1 (first time above 90%)
            int level = 0;
            int consecutiveHigh = 0;
            float fill = 0.95f;

            if (fill > 0.9f)
            {
                consecutiveHigh++;
                int targetLevel = Math.Min(5, level + (consecutiveHigh >= 3 ? 2 : 1));
                if (targetLevel > level) level = targetLevel;
            }
            if (level != 1) throw new Exception("Expected level 1 after first 90%+ fill");

            // Level 1 → 2 (third consecutive 90%+ fill)
            fill = 0.92f;
            if (fill > 0.9f)
            {
                consecutiveHigh++;
                int targetLevel = Math.Min(5, level + (consecutiveHigh >= 3 ? 2 : 1));
                if (targetLevel > level) level = targetLevel;
            }
            if (level != 1) throw new Exception("Expected level 1 after second 90%+ fill");

            fill = 0.95f;
            if (fill > 0.9f)
            {
                consecutiveHigh++;
                int targetLevel = Math.Min(5, level + (consecutiveHigh >= 3 ? 2 : 1));
                if (targetLevel > level) level = targetLevel;
            }
            if (level != 3) throw new Exception("Expected level 3 after third consecutive 90%+ fill");
        }

        public static void TestCompactionDeescalation()
        {
            // Start at level 2, fill drops below 50% — should go to level 1
            int level = 2;
            float fill = 0.4f;

            if (fill < 0.5f && level > 0)
            {
                level = Math.Max(0, level - 1);
            }
            if (level != 1) throw new Exception("Expected level 1 after de-escalation");
        }

        public static void TestDynamicMaxHistory()
        {
            // Level 0: base limit (10)
            // Level 1: 10/2 = 5
            // Level 2: 5/2 = 2 (minimum)
            // Level 3: 2/2 = 1 → clamped to 2 (minimum)

            int baseMax = 10;
            int level = 0;
            int max = baseMax;
            for (int i = 0; i < level && max > 2; i++)
                max = Math.Max(2, max / 2);
            if (max != 10) throw new Exception("Level 0: expected 10");

            level = 1;
            max = baseMax;
            for (int i = 0; i < level && max > 2; i++)
                max = Math.Max(2, max / 2);
            if (max != 5) throw new Exception("Level 1: expected 5");

            level = 2;
            max = baseMax;
            for (int i = 0; i < level && max > 2; i++)
                max = Math.Max(2, max / 2);
            if (max != 2) throw new Exception("Level 2: expected 2");
        }

        public static void TestDynamicMaxFacts()
        {
            // Level 0: base limit (24)
            // Level 1: 24 (facts kept at level 1)
            // Level 2: 24/2 = 12
            // Level 3: 12/2 = 6
            // Level 4: 6/2 = 3 (minimum)

            int baseMax = 24;

            // Level 1: keep facts
            int level = 1;
            int max = baseMax;
            for (int i = 1; i < level && max > 3; i++)
                max = Math.Max(3, max / 2);
            if (max != 24) throw new Exception("Level 1: expected 24");

            level = 2;
            max = baseMax;
            for (int i = 1; i < level && max > 3; i++)
                max = Math.Max(3, max / 2);
            if (max != 12) throw new Exception("Level 2: expected 12");
        }

        public static void TestDynamicMaxThoughts()
        {
            // Level 0: base limit (6)
            // Level 1: 6/2 = 3
            // Level 2: 3/2 = 1 (minimum)
            // Level 3: 1/2 = 1 (minimum)

            int baseMax = 6;

            int level = 2;
            int max = baseMax;
            for (int i = 0; i < level && max > 1; i++)
                max = Math.Max(1, max / 2);
            if (max != 1) throw new Exception("Level 2: expected 1");
        }

        public static void TestMergeFacts()
        {
            var facts = new List<string>
            {
                "bed: is left here",
                "toilet: is ahead here",
                "bed: is also upstairs",
            };
            var merged = ContextManager.MergeFacts(facts);
            if (merged.Count != 2) throw new Exception($"Expected 2 merged facts, got {merged.Count}");

            // "bed" should have the last value
            bool hasBed = false;
            foreach (var f in merged)
            {
                if (f.StartsWith("bed:"))
                {
                    hasBed = true;
                    if (!f.Contains("upstairs"))
                        throw new Exception("Merged bed should have 'upstairs' (last value)");
                }
            }
            if (!hasBed) throw new Exception("Merged list should contain 'bed'");
        }

        public static void TestSummarizeHistory()
        {
            var history = new List<string>();
            for (int i = 0; i < 10; i++)
                history.Add("walk");
            history.Add("go_to");
            history.Add("interact");

            var summarized = ContextManager.SummarizeHistory(history, 3);
            // Should contain "(earlier: 7x walk)" for the old entries
            if (!summarized.Contains("walk"))
                throw new Exception("Summarized history should still reference 'walk'");
        }
    }
}
