// Cooking module — feed_blender, grind tools + cooking perception.
// Partial class of NPCInstance; registers tools/perception via ModuleRegistry.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KKLLMNPC
{
    internal static class CookingModule
    {
        private static bool _registered;
        public static void Register()
        {
            if (_registered) return;
            _registered = true;

            var schemas = new Dictionary<string, Dictionary<string, object>>
            {
                ["target"] = new Dictionary<string, object>
                {
                    ["type"] = new object[] { "string", "null" },
                    ["description"] = "name or id of target (omit for nearest)"
                },
                ["amount"] = new Dictionary<string, object>
                {
                    ["type"] = new object[] { "number", "null" },
                    ["description"] = "amount of reagent to transfer (default: all)"
                },
            };

            ModuleRegistry.Tool("feed_blender", (n, p) => n.ToolFeedBlender(p), schemas);
            ModuleRegistry.Tool("grind", (n, p) => n.ToolGrind(p), schemas);

            ModuleRegistry.Perception((n, d) => n.CookingPerception(d));
        }
    }

    internal partial class NPCInstance
    {
        // ---- Tool: feed_blender (transfer belly reagents into a blender) ----
        internal object ToolFeedBlender(JsonObj p)
        {
            if (_kobold == null) return new { ok = false, reason = "no_body" };
            if (_kobold.bellyContainer == null || _kobold.bellyContainer.isEmpty)
                return new { ok = false, reason = "empty_belly", note = "eat something first" };
            float radius = _cfgFarmScanRadius != null ? _cfgFarmScanRadius.Value : 3.0f;

            ElectricBlender blender = FindNearestBlender(radius);
            if (blender == null)
                return new { ok = false, reason = "no_blender", note = "no blender nearby" };

            float amount = Mathf.Clamp(p.F("amount", -1f), 0f, 999f);
            bool transferAll = amount <= 0f;

            ElectricBlender capBlender = blender;
            float capAmount = amount;
            bool capAll = transferAll;
            var result = (Dictionary<string, object>)RunOnMainThread(() =>
            {
                var res = new Dictionary<string, object>();
                try
                {
                    var blenderContainer = capBlender.GetComponent<GenericReagentContainer>();
                    if (blenderContainer == null)
                    {
                        res["ok"] = false;
                        res["reason"] = "no_container";
                        return res;
                    }

                    float bellyVol = _kobold.bellyContainer.volume;
                    float toTransfer = capAll ? bellyVol : Mathf.Min(capAmount, bellyVol);
                    if (toTransfer <= 0f)
                    {
                        res["ok"] = false;
                        res["reason"] = "nothing_to_transfer";
                        return res;
                    }

                    // TransferMix is private on GenericReagentContainer — use reflection
                    var transferMethod = typeof(GenericReagentContainer).GetMethod("TransferMix",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (transferMethod == null)
                    {
                        res["ok"] = false;
                        res["reason"] = "api_error";
                        res["note"] = "TransferMix method not found";
                        return res;
                    }
                    transferMethod.Invoke(_kobold.bellyContainer, new object[] { blenderContainer, toTransfer, GenericReagentContainer.InjectType.Inject });
                    res["ok"] = true;
                    res["transferred"] = toTransfer;
                    res["belly_remaining"] = _kobold.bellyContainer.volume;
                }
                catch (Exception e)
                {
                    Logger.LogWarning("feed_blender: " + e.Message);
                    res["ok"] = false;
                    res["reason"] = "error";
                    res["msg"] = e.Message;
                }
                return res;
            });

            return result;
        }

        // ---- Tool: grind (use the grinder) ----
        internal object ToolGrind(JsonObj p)
        {
            if (_kobold == null) return new { ok = false, reason = "no_body" };
            float radius = _cfgFarmScanRadius != null ? _cfgFarmScanRadius.Value : 3.0f;

            GrinderManager grinder = FindNearestGrinder(radius);
            if (grinder == null)
                return new { ok = false, reason = "no_grinder", note = "no grinder nearby" };

            GrinderManager capGrinder = grinder;
            bool ok = (bool)RunOnMainThread(() =>
            {
                try
                {
                    if (!capGrinder.CanUse(_kobold))
                        return false;
                    capGrinder.LocalUse(_kobold);
                    return true;
                }
                catch (Exception e)
                {
                    Logger.LogWarning("grind: " + e.Message);
                    return false;
                }
            });

            return new { ok, grinder = CleanName(capGrinder.name) };
        }

        // ---- Perception: cooking ----
        internal void CookingPerception(Dictionary<string, object> dict)
        {
            if (_kobold == null) return;
            float radius = _cfgFarmScanRadius != null ? _cfgFarmScanRadius.Value : 3.0f;

            var cookingList = new List<object>();
            try
            {
                Vector3 pos = _kobold.transform.position;

                foreach (var b in UnityEngine.Object.FindObjectsOfType<ElectricBlender>())
                {
                    if (b == null) continue;
                    float d = Vector3.Distance(pos, b.transform.position);
                    if (d > radius * 2f) continue;
                    cookingList.Add(new { name = CleanName(b.name), kind = "blender", d = F(d) });
                }

                foreach (var g in UnityEngine.Object.FindObjectsOfType<GrinderManager>())
                {
                    if (g == null) continue;
                    float d = Vector3.Distance(pos, g.transform.position);
                    if (d > radius * 2f) continue;
                    cookingList.Add(new { name = CleanName(g.name), kind = "grinder", d = F(d) });
                }
            }
            catch (Exception e) { Logger.LogDebug("cooking perception: " + e.Message); }

            if (cookingList.Count > 0)
                dict["cooking"] = new { stations = cookingList };
        }

        // ---- Helpers ----

        private ElectricBlender FindNearestBlender(float radius)
        {
            if (_kobold == null) return null;
            ElectricBlender best = null;
            float bestD = float.MaxValue;
            try
            {
                foreach (var b in UnityEngine.Object.FindObjectsOfType<ElectricBlender>())
                {
                    if (b == null) continue;
                    float d = Vector3.Distance(_kobold.transform.position, b.transform.position);
                    if (d < radius && d < bestD) { bestD = d; best = b; }
                }
            }
            catch (Exception) { }
            return best;
        }

        private GrinderManager FindNearestGrinder(float radius)
        {
            if (_kobold == null) return null;
            GrinderManager best = null;
            float bestD = float.MaxValue;
            try
            {
                foreach (var g in UnityEngine.Object.FindObjectsOfType<GrinderManager>())
                {
                    if (g == null) continue;
                    float d = Vector3.Distance(_kobold.transform.position, g.transform.position);
                    if (d < radius && d < bestD) { bestD = d; best = g; }
                }
            }
            catch (Exception) { }
            return best;
        }
    }
}
