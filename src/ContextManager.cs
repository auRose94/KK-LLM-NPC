// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace KKLLMNPC
{
    /// <summary>
    /// Dynamic context manager for an NPC instance.  Tracks token pressure and
    /// applies escalating compaction strategies when the context window fills up.
    ///
    /// Compaction levels (in order of escalation):
    ///   0 = normal (full context)
    ///   1 = trim old history + thoughts, keep facts
    ///   2 = trim facts aggressively, keep recent
    ///   3 = merge similar facts, summarize old history
    ///   4 = maximum compression — only essentials
    ///   5 = attempt model switch to larger-context model
    /// </summary>
    internal class ContextManager
    {
        private readonly NPCInstance _npc;
        private int _compactionLevel;     // current level (0-5)
        private int _consecutiveHigh;     // how many turns in a row context was >70%
        private float _lastSwitchAttempt; // Time.unscaledTime of last model switch try
        private const float SwitchCooldown = 120f; // seconds between switch attempts

        // Compaction level thresholds
        private const int MaxCompactionLevel = 5;

        internal ContextManager(NPCInstance npc)
        {
            _npc = npc;
        }

        /// <summary>
        /// Current compaction level (0=none, 5=max).  Read by NPCInstance to
        /// adjust limits dynamically.
        /// </summary>
        internal int CompactionLevel { get { return _compactionLevel; } }

        /// <summary>
        /// Called after each turn with the estimated context fill ratio (0.0-1.0).
        /// Adjusts compaction level and returns a description of what changed.
        /// Returns null if no action was taken.
        /// </summary>
        internal string Update(float fillRatio)
        {
            string action = null;

            if (fillRatio > 0.9f)
            {
                _consecutiveHigh++;
                // Escalate faster if it's been high for multiple turns
                int targetLevel = Math.Min(MaxCompactionLevel, _compactionLevel + (_consecutiveHigh >= 3 ? 2 : 1));
                if (targetLevel > _compactionLevel)
                {
                    _compactionLevel = targetLevel;
                    action = ApplyCompaction();
                }
            }
            else if (fillRatio < 0.5f && _compactionLevel > 0)
            {
                // Context is comfortably under half — de-escalate one level
                _compactionLevel = Math.Max(0, _compactionLevel - 1);
                _consecutiveHigh = 0;
                action = "context pressure eased — compaction level → " + _compactionLevel;
            }
            else
            {
                _consecutiveHigh = 0;
            }

            return action;
        }

        /// <summary>
        /// Apply the current compaction level and return a human-readable description
        /// of what was done.
        /// </summary>
        private string ApplyCompaction()
        {
            switch (_compactionLevel)
            {
                case 1:
                    return "compaction level 1: trimmed old history and thoughts";
                case 2:
                    return "compaction level 2: aggressive fact trimming";
                case 3:
                    return "compaction level 3: fact merging + history summary";
                case 4:
                    return "compaction level 4: maximum compression — essentials only";
                case 5:
                    return AttemptModelSwitch();
                default:
                    return null;
            }
        }

        /// <summary>
        /// Get the dynamic max history count based on current compaction level.
        /// Level 0 = base limit, each level halves it (minimum 2).
        /// </summary>
        internal int DynamicMaxHistory(int baseMax)
        {
            int max = baseMax;
            for (int i = 0; i < _compactionLevel && max > 2; i++)
                max = Math.Max(2, max / 2);
            return max;
        }

        /// <summary>
        /// Get the dynamic max facts count based on current compaction level.
        /// </summary>
        internal int DynamicMaxFacts(int baseMax)
        {
            int max = baseMax;
            // Level 1: keep facts (they're compact)
            // Level 2+: start trimming
            for (int i = 1; i < _compactionLevel && max > 3; i++)
                max = Math.Max(3, max / 2);
            return max;
        }

        /// <summary>
        /// Get the dynamic max thoughts count based on current compaction level.
        /// </summary>
        internal int DynamicMaxThoughts(int baseMax)
        {
            int max = baseMax;
            for (int i = 0; i < _compactionLevel && max > 1; i++)
                max = Math.Max(1, max / 2);
            return max;
        }

        /// <summary>
        /// Get the dynamic max chat log lines based on current compaction level.
        /// </summary>
        internal int DynamicMaxChatLog(int baseMax)
        {
            int max = baseMax;
            // Chat log is cheap — only trim at higher compaction levels
            for (int i = 2; i < _compactionLevel && max > 2; i++)
                max = Math.Max(2, max / 2);
            return max;
        }

        /// <summary>
        /// At compaction level 3+, merge facts with the same category prefix
        /// into a single entry (keeping the most recent).  Returns the merged list.
        /// </summary>
        internal static List<string> MergeFacts(List<string> facts)
        {
            var merged = new Dictionary<string, string>();
            foreach (var f in facts)
            {
                string prefix = f.Split(':')[0];
                merged[prefix] = f;  // last wins (most recent)
            }
            return new List<string>(merged.Values);
        }

        /// <summary>
        /// At compaction level 3+, summarize old history entries.
        /// Keeps recent entries verbatim, summarizes older ones.
        /// </summary>
        internal static string SummarizeHistory(List<string> history, int keepRecent)
        {
            if (history.Count <= keepRecent)
            {
                // Nothing to summarize — return all
                var sb = new StringBuilder("[");
                for (int i = 0; i < history.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Json.Write(history[i]));
                }
                sb.Append(']');
                return sb.ToString();
            }

            // Split: recent (verbatim) + old (summarized)
            var result = new StringBuilder("[");
            int summarizeEnd = history.Count - keepRecent;
            var oldCounts = new Dictionary<string, int>();
            for (int i = 0; i < summarizeEnd; i++)
            {
                string action = history[i].Split(new[] { ' ' }, 2)[0];
                int count;
                oldCounts.TryGetValue(action, out count);
                oldCounts[action] = count + 1;
            }

            // Emit summary first
            bool first = true;
            foreach (var kv in oldCounts)
            {
                if (!first) result.Append(',');
                first = false;
                result.Append(Json.Write("(earlier: " + kv.Value + "x " + kv.Key + ")"));
            }

            // Then recent entries verbatim
            for (int i = summarizeEnd; i < history.Count; i++)
            {
                if (!first) result.Append(',');
                first = false;
                result.Append(Json.Write(history[i]));
            }
            result.Append(']');
            return result.ToString();
        }

        /// <summary>
        /// Attempt to switch to a larger-context model via KoboldCpp admin API.
        /// Returns a status message.
        /// </summary>
        private string AttemptModelSwitch()
        {
            float now = Time.unscaledTime;
            if (now - _lastSwitchAttempt < SwitchCooldown)
                return "compaction level 5: model switch attempted too recently — waiting";
            _lastSwitchAttempt = now;

            if (!ModelProbe.AdminAvailable)
                return "compaction level 5: max compression but admin mode unavailable for model switch";

            // Check if auto-switch is enabled
            if (_npc._cfgAutoSwitch == null || !_npc._cfgAutoSwitch.Value)
                return "compaction level 5: max compression reached — enable AutoSwitchModel to attempt model switch";

            // Find a model with more context than current
            int currentCtx = ModelProbe.DetectedContextLength;
            string currentTier = ModelProbe.DetectedTier ?? "small";
            string targetTier = currentTier == "small" ? "medium" : "large";
            string bestModel = ModelProbe.FindBestModel(targetTier, currentCtx + 2048);

            if (bestModel == null)
                return "compaction level 5: no larger-context model available on server";

            // Try the switch — get API key from the NPC's config
            string apiKey = "";
            try
            {
                // Access the config entry through reflection — the field name is _cfgApiKey
                var f = _npc.GetType().GetField("_cfgApiKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f != null)
                {
                    var entry = f.GetValue(_npc) as ConfigEntry<string>;
                    if (entry != null) apiKey = entry.Value ?? "";
                }
            }
            catch (Exception) { }
            if (ModelProbe.TrySwitchModel(bestModel, apiKey))
                return "compaction level 5: switching to model '" + bestModel + "' (server will restart)";
            else
                return "compaction level 5: model switch failed for '" + bestModel + "'";
        }

        /// <summary>
        /// Inject compaction status into the perception JSON so the model knows
        /// it's running in compressed mode.
        /// </summary>
        internal string CompactionStatusJson()
        {
            if (_compactionLevel == 0) return null;
            return "{\"level\":" + _compactionLevel
                + ",\"mode\":\"" + CompactionModeName() + "\"}";
        }

        private string CompactionModeName()
        {
            switch (_compactionLevel)
            {
                case 1: return "trimmed";
                case 2: return "aggressive";
                case 3: return "merged";
                case 4: return "minimal";
                case 5: return "switching";
                default: return "normal";
            }
        }
    }
}
