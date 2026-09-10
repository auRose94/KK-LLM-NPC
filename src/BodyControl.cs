// BodyControl module — thrust, erection, mount, unmount, orgasm + swap awareness.
// Partial class of NPCInstance; registers tools/physics/perception via ModuleRegistry.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KKLLMNPC
{
    // ---- Module registration (discovered by ModuleRegistry.Scan) ----
    internal static class BodyControlModule
    {
        private static bool _registered;
        public static void Register()
        {
            if (_registered) return;
            _registered = true;

            var schemas = new Dictionary<string, Dictionary<string, object>>
            {
                ["intensity"] = new Dictionary<string, object>
                {
                    ["type"] = new object[] { "number", "null" },
                    ["description"] = "thrust: 0-1 hip intensity (default 0.6)"
                },
                ["duration"] = new Dictionary<string, object>
                {
                    ["type"] = new object[] { "number", "null" },
                    ["description"] = "thrust: seconds 0-5 (default 1.5)"
                },
                ["size"] = new Dictionary<string, object>
                {
                    ["type"] = new object[] { "number", "null" },
                    ["description"] = "erection: 0-1 dick size (omit to report only)"
                },
                ["target"] = new Dictionary<string, object>
                {
                    ["type"] = new object[] { "string", "null" },
                    ["description"] = "mount: name or id of partner to penetrate"
                },
            };

            ModuleRegistry.Tool("thrust", (n, p) => n.ToolThrust(p), schemas);
            ModuleRegistry.Tool("erection", (n, p) => n.ToolErection(p), schemas);
            ModuleRegistry.Tool("mount", (n, p) => n.ToolMount(p), schemas);
            ModuleRegistry.Tool("unmount", (n, p) => n.ToolUnmount(p), schemas);
            ModuleRegistry.Tool("orgasm", (n, p) => n.ToolOrgasm(p), schemas);

            ModuleRegistry.PhysicsTick((n, dt) => n.BodyControlTick(dt));
            ModuleRegistry.Perception((n, d) => n.BodyControlPerception(d));
        }
    }

    // ---- Partial class: NPCInstance additions for body control ----
    internal partial class NPCInstance
    {
        // ---- Thrust state (LLM thread writes, physics tick reads) ----
        private volatile bool _thrustActive;
        private volatile float _thrustIntensity;
        private volatile float _thrustDuration;
        private float _thrustStartTime;

        // ---- Swap awareness ----
        private bool _swappedBody;

        // ---- Physics tick: drives thrust oscillation ----
        internal void BodyControlTick(float dt)
        {
            if (!_thrustActive) return;
            if (_kobold == null || _charAnimator == null || IsInAnimationStation())
            {
                _thrustActive = false;
                return;
            }
            try
            {
                float elapsed = Time.unscaledTime - _thrustStartTime;
                if (elapsed >= _thrustDuration)
                {
                    _charAnimator.SetHipVector(Vector2.zero);
                    _thrustActive = false;
                    return;
                }
                // ~10Hz sine oscillation
                float t = elapsed * 10f * (float)Math.PI * 2f;
                float y = _thrustIntensity * Mathf.Sin(t);
                _charAnimator.SetHipVector(new Vector2(0f, y));
            }
            catch (Exception e)
            {
                Logger.LogDebug("BodyControlTick: " + e.Message);
                _thrustActive = false;
            }
        }

        // ---- Tool: thrust ----
        internal object ToolThrust(JsonObj p)
        {
            if (_kobold == null || _charAnimator == null)
                return new { ok = false, reason = "no_body" };
            if (IsInAnimationStation())
                return new { ok = false, reason = "in_animation_station" };

            float intensity = Mathf.Clamp01(p.F("intensity", 0.6f));
            float duration = Mathf.Clamp(p.F("duration", 1.5f), 0f, 5f);

            _thrustIntensity = intensity;
            _thrustDuration = duration;
            _thrustStartTime = Time.unscaledTime;
            _thrustActive = true;

            return new { ok = true, thrusting = true, intensity, duration };
        }

        // ---- Tool: erection ----
        internal object ToolErection(JsonObj p)
        {
            if (_kobold == null)
                return new { ok = false, reason = "no_body" };

            float size = p.F("size", -1f);
            if (size >= 0f)
            {
                float clamped = Mathf.Clamp01(size);
                try
                {
                    RunOnMainThread(() =>
                    {
                        try { _kobold.PumpUpDick(clamped); }
                        catch (Exception e) { Logger.LogWarning("PumpUpDick: " + e.Message); }
                        return true;
                    });
                }
                catch (Exception e)
                {
                    return new { ok = false, reason = "main_thread_error", msg = e.Message };
                }
            }

            // Report current state
            float dickSize = 0f;
            int activeDicks = 0;
            try
            {
                var g = _kobold.GetGenes();
                dickSize = g != null ? g.dickSize : 0f;
                activeDicks = _kobold.activeDicks != null ? _kobold.activeDicks.Count : 0;
            }
            catch (Exception) { }

            return new { ok = true, dickSize, activeDicks };
        }

        // ---- Tool: mount (penetrate a target) ----
        internal object ToolMount(JsonObj p)
        {
            if (_kobold == null)
                return new { ok = false, reason = "no_body" };

            string targetName = p.Has("target") ? p.S("target") : null;
            int targetId = p.Has("target") ? (int)p.F("target", -1f) : -1;

            try
            {
                // Enumerate our kobold's penetrators
                var penetrators = _kobold.GetComponentsInChildren<PenetrationTech.Penetrator>(true);
                if (penetrators == null || penetrators.Length == 0)
                    return new { ok = false, reason = "no_penetrators" };

                PenetrationTech.Penetrator bestPen = null;
                PenetrationTech.Penetrable bestTarget = null;
                float bestDist = float.MaxValue;

                foreach (var pr in penetrators)
                {
                    if (pr == null) continue;
                    PenetrationTech.Penetrable pen;
                    if (!pr.TryGetPenetrable(out pen)) continue;
                    if (pen == null) continue;

                    // Filter by target if specified
                    if (targetName != null || targetId > 0)
                    {
                        var penKobold = pen.GetComponentInParent<Kobold>();
                        if (penKobold == null) continue;

                        bool match = false;
                        if (targetId > 0)
                        {
                            int pid = TargetIdFor(penKobold.transform.root, penKobold.name);
                            match = pid == targetId;
                        }
                        if (!match && targetName != null)
                        {
                            string pn = CleanName(penKobold.name);
                            match = string.Equals(pn, targetName, StringComparison.OrdinalIgnoreCase);
                        }
                        if (!match) continue;
                    }

                    float d = Vector3.Distance(_kobold.transform.position, pen.transform.position);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestPen = pr;
                        bestTarget = pen;
                    }
                }

                if (bestPen == null || bestTarget == null)
                    return new { ok = false, reason = "no_valid_target" };

                PenetrationTech.Penetrator capPr = bestPen;
                PenetrationTech.Penetrable capPen = bestTarget;
                RunOnMainThread(() =>
                {
                    try { capPr.Penetrate(capPen); }
                    catch (Exception e) { Logger.LogWarning("Penetrate: " + e.Message); }
                    return true;
                });

                string hole = "unknown";
                try { hole = ClassifyPenetrable(bestTarget); }
                catch (Exception) { }

                return new { ok = true, penetrated = true, hole };
            }
            catch (Exception e)
            {
                return new { ok = false, reason = "error", msg = e.Message };
            }
        }

        // ---- Tool: unmount (release from being penetrated / pull out) ----
        internal object ToolUnmount(JsonObj p)
        {
            if (_kobold == null)
                return new { ok = false, reason = "no_body" };

            // Check if something is inside us (penetrables direction)
            bool wasInside = false;
            string whoName = null;
            Transform whoTransform = null;
            lock (_pen)
            {
                float now = Time.unscaledTime;
                foreach (var kv in _pen)
                {
                    if (now - kv.Value.lastT < 3f)
                    {
                        wasInside = true;
                        whoName = kv.Value.name;
                        whoTransform = kv.Value.src;
                        break;
                    }
                }
            }

            if (!wasInside)
                return new { ok = false, reason = "not_penetrated" };

            // Try to release: stop any hip animation, then move away to break the connection
            try
            {
                RunOnMainThread(() =>
                {
                    try
                    {
                        if (_kobold == null || !IsAlive(_kobold)) return true;
                        // Stop any active thrust/hip animation first
                        if (_charAnimator != null)
                        {
                            try { _charAnimator.SetHipVector(Vector2.zero); }
                            catch (Exception) { }
                        }
                        // Teleport slightly away from the penetrator source
                        Vector3 dir = whoTransform != null
                            ? (_kobold.transform.position - whoTransform.position).normalized
                            : -_kobold.transform.forward;
                        if (dir.sqrMagnitude < 0.01f) dir = Vector3.back;
                        _kobold.transform.position += dir * 2f;
                    }
                    catch (Exception e) { Logger.LogWarning("unmount move: " + e.Message); }
                    return true;
                });
                return new { ok = true, released = true, who = whoName };
            }
            catch (Exception e)
            {
                return new { ok = false, reason = "error", msg = e.Message };
            }
        }

        // ---- Tool: orgasm ----
        internal object ToolOrgasm(JsonObj p)
        {
            if (_kobold == null)
                return new { ok = false, reason = "no_body" };

            try
            {
                RunOnMainThread(() =>
                {
                    try { _kobold.Cum(); }
                    catch (Exception e) { Logger.LogWarning("Cum: " + e.Message); }
                    return true;
                });
            }
            catch (Exception e)
            {
                return new { ok = false, reason = "main_thread_error", msg = e.Message };
            }

            float stim = 0f;
            try { stim = _kobold.stimulation; } catch (Exception) { }
            return new { ok = true, stimulation = stim };
        }

        // ---- Perception hook ----
        internal void BodyControlPerception(Dictionary<string, object> dict)
        {
            if (_kobold == null) return;

            // erection: rough 0-1 from dickSize
            float erection = 0f;
            try
            {
                var g = _kobold.GetGenes();
                erection = g != null ? Mathf.Clamp01(g.dickSize) : 0f;
            }
            catch (Exception) { }

            float stimulation = 0f;
            try { stimulation = _kobold.stimulation; } catch (Exception) { }

            bool inPen = false;
            var penList = new List<object>();
            lock (_pen)
            {
                float now = Time.unscaledTime;
                foreach (var kv in _pen)
                {
                    if (now - kv.Value.lastT >= 3f) continue;
                    inPen = true;
                    int pid = PartnerId(kv.Value.src);
                    penList.Add(new
                    {
                        hole = kv.Value.hole,
                        depth = F(kv.Value.depth),
                        who = pid > 0 ? kv.Value.name : null
                    });
                }
            }

            dict["body_control"] = new
            {
                erection = F(erection),
                stimulation = F(stimulation),
                in_penetration = inPen,
                penetration = penList.Count > 0 ? (object)penList : null,
                hips_active = _thrustActive
            };

            // Swap awareness
            dict["my_swap"] = _swappedBody;

            // Recent swaps (from ring buffer in Body.cs)
            var ring = GetSwapRing();
            if (ring != null && ring.Count > 0)
                dict["swap_recent"] = ring;
        }

        // ---- Swap ring buffer (populated by OnBodySwap in Body.cs) ----
        private readonly List<object> _swapRing = new List<object>();
        private const int SwapRingCap = 10;

        internal void RecordSwap(string prevName, string newName)
        {
            _swappedBody = true;
            _swapRing.Add(new { who = prevName, into_whom = newName, t = F(Time.unscaledTime) });
            while (_swapRing.Count > SwapRingCap) _swapRing.RemoveAt(0);
        }

        internal List<object> GetSwapRing()
        {
            return _swapRing.Count > 0 ? new List<object>(_swapRing) : null;
        }

        internal bool IsSwappedBody()
        {
            return _swappedBody;
        }
    }
}
