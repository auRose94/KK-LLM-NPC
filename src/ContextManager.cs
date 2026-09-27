// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Unity-coupled adapter around the pure ContextCore policy (ContextPolicy /
// ContextMath / ContextCompaction). Everything testable lives in ContextCore.cs.
using System;
using BepInEx.Configuration;
using UnityEngine;

namespace KKLLMNPC
{
    /// <summary>
    /// Dynamic context manager for an NPC instance. Owns the wall clock, the
    /// ModelProbe lookups, config and the admin-API model switch; every decision
    /// is delegated to <see cref="ContextPolicy"/> / <see cref="ContextMath"/> /
    /// <see cref="ContextCompaction"/>, which are pure and unit-tested.
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
        private int _consecutiveHigh;     // how many turns in a row context was >90%
        private float _lastSwitchAttempt; // Time.unscaledTime of last model switch try

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
        /// Feed this turn's fill ratio and get back a human-readable description
        /// of what changed, or null if nothing did.
        /// </summary>
        internal string Update(float fillRatio)
        {
            if (ContextPolicy.ShouldEscalate(fillRatio))
            {
                _consecutiveHigh++;
                if (ContextPolicy.IsEscalating(_compactionLevel, _consecutiveHigh))
                {
                    _compactionLevel = ContextPolicy.NextEscalatedLevel(_compactionLevel, _consecutiveHigh);
                    return DescribeCurrentLevel();
                }
                // Already at the ceiling — stay quiet instead of repeating the
                // same message every turn.
                return null;
            }

            if (ContextPolicy.ShouldDeEscalate(fillRatio, _compactionLevel))
            {
                _compactionLevel = ContextPolicy.DeEscalatedLevel(_compactionLevel);
                _consecutiveHigh = 0;
                return "context pressure eased — compaction level → " + _compactionLevel;
            }

            _consecutiveHigh = 0;
            return null;
        }

        /// <summary>
        /// Update the estimated token count and return the current fill ratio.
        /// </summary>
        internal float GetFillRatio(int factCount, int histCount, int thoughtCount, int chatCount, int nearbyCount)
        {
            return ContextMath.FillRatio(factCount, histCount, thoughtCount, chatCount,
                nearbyCount, ModelProbe.DetectedContextLength);
        }

        // ---- dynamic limits (pure policy, see ContextPolicy) ----

        internal int DynamicMaxHistory(int baseMax) { return ContextPolicy.MaxHistory(_compactionLevel, baseMax); }
        internal int DynamicMaxFacts(int baseMax) { return ContextPolicy.MaxFacts(_compactionLevel, baseMax); }
        internal int DynamicMaxThoughts(int baseMax) { return ContextPolicy.MaxThoughts(_compactionLevel, baseMax); }
        internal int DynamicMaxChatLog(int baseMax) { return ContextPolicy.MaxChatLog(_compactionLevel, baseMax); }

        /// <summary>
        /// Inject compaction status into the perception JSON so the model knows
        /// it's running in compressed mode.
        /// </summary>
        internal string CompactionStatusJson()
        {
            return ContextPolicy.StatusJson(_compactionLevel);
        }

        // ---- the one part that genuinely needs Unity + the network ----

        /// <summary>
        /// Describe the level we just moved to. Levels 1-4 are a fixed string;
        /// level 5 has to try a model switch, which needs the wall clock, the
        /// probe results and the admin API.
        /// </summary>
        private string DescribeCurrentLevel()
        {
            if (_compactionLevel >= ContextPolicy.MaxLevel) return AttemptModelSwitch();
            return ContextPolicy.Describe(_compactionLevel);
        }

        /// <summary>
        /// Attempt to switch to a larger-context model via KoboldCpp admin API.
        /// Returns a status message.
        /// </summary>
        private string AttemptModelSwitch()
        {
            float now = Time.unscaledTime;
            if (now - _lastSwitchAttempt < Consts.ContextManagerSwitchCooldown)
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
            catch (Exception e) { _npc.Logger.LogInfo("context manager model switch: " + e.Message); }
            if (ModelProbe.TrySwitchModel(bestModel, apiKey))
                return "compaction level 5: switching to model '" + bestModel + "' (server will restart)";
            else
                return "compaction level 5: model switch failed for '" + bestModel + "'";
        }
    }
}
