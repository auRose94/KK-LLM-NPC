// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// SensationSense — turns the body's live arousal / stimulation / egg signals into
// ROLEPLAY FEEDBACK for the LLM, two ways:
//   1. EVENTS on major transitions (the user's "notify me of every sensation
//      change" ask) — but only on a REAL transition, deduped per category, riding
//      the existing [event] queue so it costs nothing when nothing happens.
//   2. a compact POLLABLE `sensation` block each turn (the real value): live game
//      state the model reads without waiting for an event.
//
// Runs on the main thread via the PhysicsTick hook (PollSensation) and via the
// ModuleRegistry.Perception hook (SensationPerception). The agent can quiet the
// events it finds distracting through the `set_events` filter tool (task 2).
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KKLLMNPC
{
    // ---- Module registration (discovered by ModuleRegistry.Scan) ----
    internal static class SensationSenseModule
    {
        private static bool _registered;
        public static void Register()
        {
            if (_registered) return;
            _registered = true;

            // `set_events`: the agent controls which event categories reach it, so
            // it can silence roleplay noise it finds distracting (or turn it back
            // on later). Module-registered → auto-merges into the act enum + schema.
            var schemas = new Dictionary<string, Dictionary<string, object>>
            {
                ["category"] = new Dictionary<string, object>
                {
                    ["type"] = new object[] { "string" },
                    ["description"] = "which category to adjust: 'arousal'(big arousal/stim jumps), 'climax', 'lay'(egg-lay readiness), 'sex'(penetration/insertion reactions), 'vore'(swallow/vore events), 'nav'(pathfinding chatter), 'gossip'(overheard chat), 'looked'(someone watching you) — the 'danger' and 'other' buckets carry objectives/warnings and CANNOT be muted"
                },
                ["action"] = new Dictionary<string, object>
                {
                    ["type"] = new object[] { "string" },
                    ["description"] = "'on' to start seeing this category again, 'off' to stop it — default is everything on"
                },
            };
            ModuleRegistry.Tool("set_events", (n, p) => n.ToolSetEvents(p), schemas);
            ModuleRegistry.Tool("filter_events", (n, p) => n.ToolSetEvents(p)); // friendly alias
            ModuleRegistry.PhysicsTick((n, dt) => n.PollSensation(dt));
            ModuleRegistry.Perception((n, d) => n.SensationPerception(d));
        }
    }

    // ---- Partial class: NPCInstance additions for sensation ----
    internal partial class NPCInstance
    {
        // ---- Sensation-detection state (main thread only) ----
        private float _sensationPrevStim = -1f;      // normalized stim, last tick
        private bool _sensationWasEggReady;          // egg-lay readiness last seen
        private float _climaxCooldownT = -99f;       // avoid a double climax read

        // Per-category dedup cooldowns: a slowly-rising need must not fire every
        // time it nudges past a threshold. Category → last-broadcast timestamp.
        private readonly Dictionary<string, float> _sensationCooldown = new Dictionary<string, float>();
        private const float SensationCooldownMin = 12f; // seconds between broadcasts of one category

        // ---- Default-event filter (task 2) ----
        // Categories the agent has asked to STOP seeing. 'danger' and 'other'
        // (objective / system lines) are never filterable — the agent can quiet
        // roleplay noise without blinding itself to its goals or its safety.
        private readonly HashSet<string> _disabledCategories = new HashSet<string>();

        // Is a queued event of a category the agent currently allows? Called from
        // Body.cs DrainReagentEvents. A null/unknown line → 'other' → passes, so we
        // never drop something we didn't recognise.
        internal bool CategoryAllowed(string line)
        {
            string cat = CategoryOf(line);
            if (cat == "danger" || cat == "other") return true; // never muted
            return !_disabledCategories.Contains(cat);
        }

        // Map an event line's leading [tag] (or a DANGER: marker) to a stable
        // filter bucket.
        private static string CategoryOf(string line)
        {
            if (string.IsNullOrEmpty(line)) return "other";
            if (line.IndexOf("DANGER", StringComparison.OrdinalIgnoreCase) >= 0) return "danger";
            int open = line.IndexOf('[');
            if (open < 0) return "other";
            int close = line.IndexOf(']', open);
            if (close < 0) return "other";
            string tag = line.Substring(open + 1, close - open - 1).Trim().ToLowerInvariant();
            switch (tag)
            {
                case "sex": return "sex";
                case "vore-pen": return "vore";
                case "kick":
                case "surrender":
                case "intake":
                case "conversion": return "vore"; // all swallow/vore reactions
                case "lay": return "lay";
                case "sensation": return "arousal";
                case "nav": return "nav";
                case "overheard": return "gossip";
                case "looked": return "looked";
                default: return "other"; // [event] and anything unclassified — never muted
            }
        }

        // ---- Tool: the agent-controlled default-event filter ----
        internal object ToolSetEvents(JsonObj p)
        {
            string cat = (p.S("category") ?? "").Trim().ToLowerInvariant();
            string act = (p.S("action") ?? "").Trim().ToLowerInvariant();

            bool known = cat == "arousal" || cat == "climax" || cat == "lay"
                || cat == "sex" || cat == "vore" || cat == "nav" || cat == "gossip" || cat == "looked";
            if (!known)
                return new { ok = false, reason = "bad_category",
                    note = "unknown category '" + cat + "'. Filterable: arousal, climax, lay, sex, vore, nav, gossip, looked. The 'danger' and 'other' buckets carry your objectives and warnings and can't be muted." };
            if (act != "on" && act != "off")
                return new { ok = false, reason = "bad_action", note = "action must be 'on' or 'off'" };
            if (act == "off" && (cat == "danger" || cat == "other"))
                return new { ok = false, reason = "cannot_mute",
                    note = "you can't silence '" + cat + "' — it carries your objectives and danger warnings" };

            if (act == "off") { lock (_disabledCategories) _disabledCategories.Add(cat); }
            else { lock (_disabledCategories) _disabledCategories.Remove(cat); }

            string disabled;
            lock (_disabledCategories) disabled = _disabledCategories.Count > 0
                ? string.Join(", ", _disabledCategories.ToArray())
                : "(none — all categories on)";
            return new { ok = true, category = cat, action = act, disabled };
        }

        // Poll the body's sensation each physics tick. Cheap most frames (a few
        // float reads); an event only fires on a real transition, deduped per
        // category. Registered as the PhysicsTick hook, so it runs after
        // UpdateHorniness (which sets _horny / _hornyPrevStim) — the values it
        // compares against are always fresh.
        internal void PollSensation(float dt)
        {
            if (_kobold == null || !IsAlive(_kobold) || Swallowed) return;
            try
            {
                float max = _kobold.stimulationMax > 0f ? _kobold.stimulationMax : 10f;
                float stimN = Mathf.Clamp01(_kobold.stimulation / max);

                // ---- 1. Climax: stim swung from high (>=0.8) down to low (<0.3).
                // The game spends the slow-burn need here (see UpdateHorniness).
                bool crossed = _sensationPrevStim >= 0.8f && stimN < 0.3f;
                _sensationPrevStim = stimN;
                if (crossed && Time.unscaledTime - _climaxCooldownT > 5f)
                {
                    _climaxCooldownT = Time.unscaledTime;
                    _sensationCooldown.Clear(); // a whole need was spent — cooldowns are moot now
                    PushWorldEvent("[climax] you just climaxed — your body locks and a burst of release floods through you; your slow-burn arousal need has been spent. Catch your breath and re-orient yourself before anything else.");
                }

                // ---- 2. Big arousal jump: normalized stim rose a meaningful amount.
                // The game's own yowl gate (0.08 raw) is far twitchy for a broadcast;
                // 0.15 normalized (~1.5 raw) is a real shift, and the per-category
                // cooldown stops a long play session from firing on every crossing.
                float jump = stimN - _sensationPrevStim;
                if (jump >= 0.15f && CooldownHit("arousal"))
                    PushWorldEvent("[sensation] your arousal just spiked (" + F(stimN) + " arousal, " + F(_horny) + " slow-burn) — something just turned you on hard. React to it.");

                // ---- 3. Egg-lay readiness transitions (new — nothing else reports
                // the moment you become / stop being ready to lay).
                bool eggReady;
                try { eggReady = IsReadyToLayEgg(_kobold); } catch (Exception) { eggReady = false; }
                if (eggReady && !_sensationWasEggReady && CooldownHit("lay"))
                {
                    float ml; try { ml = GetEggVolume(_kobold); } catch (Exception) { ml = 0f; }
                    PushWorldEvent("[lay] you feel READY to lay — your belly holds " + F(ml) + "ml of egg and you have the energy to lay (find a nest NOW, laying spends energy); this is oviposition, not sex.");
                }
                if (!eggReady && _sensationWasEggReady && CooldownHit("lay"))
                    PushWorldEvent("[lay] the pressure to lay has eased — you're not ready to lay anymore.");
                _sensationWasEggReady = eggReady;
            }
            catch (Exception e) { Logger.LogDebug("pollsensation: " + e.Message); }
        }

        // True if this category's cooldown has elapsed; marks it fired.
        private bool CooldownHit(string cat)
        {
            float now = Time.unscaledTime;
            float t;
            if (!_sensationCooldown.TryGetValue(cat, out t)) t = -99f;
            if (now - t >= SensationCooldownMin) { _sensationCooldown[cat] = now; return true; }
            return false;
        }

        // The pollable sensation block (task 4): compact live body state the model
        // reads each turn instead of waiting for an event.
        internal void SensationPerception(Dictionary<string, object> dict)
        {
            if (_kobold == null) return;
            try
            {
                float max = _kobold.stimulationMax > 0f ? _kobold.stimulationMax : 10f;
                float stimN = Mathf.Clamp01(_kobold.stimulation / max);
                float horny; lock (_stateLock) horny = _horny;
                // Trend vs. last tick's stim (best-effort; _sensationPrevStim is
                // updated on the main thread every tick by PollSensation).
                float trend = stimN - (_sensationPrevStim >= 0f ? _sensationPrevStim : 0f);
                bool driven = stimN >= 0.15f;
                dict["sensation"] = new
                {
                    stim = F(stimN),
                    horny = F(horny),
                    trend = F(trend),
                    driven = driven,
                    egg_ml = F(GetEggVolume(_kobold)),
                    egg_ready = IsReadyToLayEgg(_kobold),
                };
            }
            catch (Exception e)
            {
                dict["sensation"] = new { error = Sanitize(e.Message) };
            }
        }

        // Reset sensation + filter state on (re)bind and world reload — the next
        // body starts clean.
        internal void ResetSensationSense()
        {
            _sensationPrevStim = -1f;
            _sensationWasEggReady = false;
            _climaxCooldownT = -99f;
            _sensationCooldown.Clear();
            lock (_disabledCategories) _disabledCategories.Clear();
        }
    }
}
