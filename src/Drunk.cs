// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Drunk — the per-agent blood-alcohol tracker (2026-11-16, Rose's ask: agents
// that drink the game's alcohol MUST roleplay being drunk). The belly postfix
// (Patches -> SwallowManager.OnBellyChanged) calls NoteBellyContents on the
// main thread whenever the belly changes; a RISE in alcohol volume is a drink.
// The BAC metabolizes lazily (computed on read, no tick); the anchor line and
// tier-change events carry the state to the model. Pure math in DrunkCore.
using System;
using System.Collections.Generic;

namespace KKLLMNPC
{
    internal partial class NPCInstance
    {
        private float _bac;
        private float _bacUpdatedAt = -1f;
        private int _drunkTier = DrunkCore.Sober;
        private float _lastBellyAlcohol = -1f;

        // Resolved booze assets (name -> reagent), shared by all instances.
        private static List<ScriptableReagent> _booze;
        private static bool _boozeTried;

        private List<ScriptableReagent> BoozeList()
        {
            if (_boozeTried) return _booze;
            _boozeTried = true;
            _booze = new List<ScriptableReagent>();
            try
            {
                var cfg = Plugin != null ? Plugin._cfgDrunkReagents : null;
                string csv = cfg != null ? (cfg.Value ?? "") : "";
                foreach (var raw in csv.Split(','))
                {
                    string name = raw.Trim();
                    if (name.Length == 0) continue;
                    ScriptableReagent r;
                    if (ReagentDatabase.TryGetAsset(name, out r) && r != null)
                    {
                        _booze.Add(r);
                        try { Logger.LogInfo("drunk: watching reagent '" + name + "'"); } catch (Exception) { }
                    }
                    else
                    {
                        try { Logger.LogInfo("drunk: reagent '" + name + "' not installed — skipping"); } catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            return _booze;
        }

        // Main thread (belly postfix): a RISE in the belly's alcohol volume is a
        // drink — however it got there (quaffing, being fed, hijinks).
        internal void NoteBellyContents(GenericReagentContainer belly)
        {
            try
            {
                var en = Plugin != null ? Plugin._cfgDrunkEnabled : null;
                if (en != null && !en.Value) return;
                if (belly == null) return;
                var booze = BoozeList();
                if (booze == null || booze.Count == 0) return;
                float vol = 0f;
                foreach (var r in booze) vol += belly.GetVolumeOf(r);
                if (_lastBellyAlcohol >= 0f && vol > _lastBellyAlcohol + 0.05f)
                {
                    float delta = vol - _lastBellyAlcohol;
                    DecayToNow();
                    float potency = 1f;
                    try { var pc = Plugin != null ? Plugin._cfgDrunkPotency : null; if (pc != null) potency = pc.Value; } catch (Exception) { }
                    _bac += DrunkCore.Gain(delta, potency);
                    if (_bac > DrunkCore.BacCap) _bac = DrunkCore.BacCap;
                    int tier = DrunkCore.TierFor(_bac);
                    if (tier > _drunkTier)
                        try { PushWorldEvent(DrunkCore.EventText(tier)); } catch (Exception) { }
                    _drunkTier = tier;
                }
                _lastBellyAlcohol = vol;
            }
            catch (Exception) { }
        }

        // Lazy metabolism: decay the BAC by the time since the last touch and
        // narrate tier drops (sobering — and the hangover, coming down from
        // WASTED).
        private void DecayToNow()
        {
            try
            {
                float now = UnityEngine.Time.unscaledTime;
                if (_bacUpdatedAt > 0f && _bac > 0f)
                {
                    float minutes = (now - _bacUpdatedAt) / 60f;
                    _bac = DrunkCore.Metabolize(_bac, minutes);
                    int tier = DrunkCore.TierFor(_bac);
                    if (tier < _drunkTier)
                    {
                        try { PushWorldEvent(DrunkCore.SoberingText(_drunkTier)); } catch (Exception) { }
                        _drunkTier = tier;
                    }
                }
                _bacUpdatedAt = now;
            }
            catch (Exception) { }
        }

        // The anchor line (null when sober) — a body state the model MUST play,
        // same contract as the touch field.
        internal string DrunkAnchorLine()
        {
            try
            {
                var en = Plugin != null ? Plugin._cfgDrunkEnabled : null;
                if (en != null && !en.Value) return null;
                DecayToNow();
                int tier = DrunkCore.TierFor(_bac);
                if (tier <= DrunkCore.Sober) return null;
                return DrunkCore.AnchorLine(tier, DrunkCore.MinutesToSober(_bac));
            }
            catch (Exception) { return null; }
        }
    }
}
