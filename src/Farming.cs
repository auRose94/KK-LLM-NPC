// Farming module — plant, water, harvest, plant_egg + farm perception.
// Partial class of NPCInstance; registers tools/perception via ModuleRegistry.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KKLLMNPC
{
    internal static class FarmingModule
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

            ModuleRegistry.Tool("plant", (n, p) => n.ToolPlantSeed(p), schemas);
            ModuleRegistry.Tool("water", (n, p) => n.ToolWater(p), schemas);
            ModuleRegistry.Tool("harvest", (n, p) => n.ToolHarvest(p), schemas);
            ModuleRegistry.Tool("plant_egg", (n, p) => n.ToolPlantEgg(p), schemas);

            ModuleRegistry.Perception((n, d) => n.FarmingPerception(d));
        }
    }

    internal partial class NPCInstance
    {
        // ---- Tool: plant (seed → nearest farm plot or ground) ----
        internal object ToolPlantSeed(JsonObj p)
        {
            if (_kobold == null) return new { ok = false, reason = "no_body" };
            float radius = _cfgFarmScanRadius != null ? _cfgFarmScanRadius.Value : 3.0f;

            Seed seed = FindNearbySeed(radius);
            if (seed == null)
                return new { ok = false, reason = "no_seed", note = "no seed nearby — grab one or stand near one" };

            Seed capSeed = seed;
            bool result = (bool)RunOnMainThread(() =>
            {
                try { capSeed.LocalUse(_kobold); return true; }
                catch (Exception e) { Logger.LogWarning("plant: " + e.Message); return false; }
            });

            return new { ok = result, seed = CleanName(capSeed.name) };
        }

        // ---- Tool: water (watering can → nearest plant) ----
        internal object ToolWater(JsonObj p)
        {
            if (_kobold == null) return new { ok = false, reason = "no_body" };
            float radius = _cfgFarmScanRadius != null ? _cfgFarmScanRadius.Value : 3.0f;

            Plant target = FindNearestPlant(radius);
            if (target == null)
                return new { ok = false, reason = "no_plant", note = "no plant nearby to water" };

            // Find the nearest watering can — its RPC fires at a target viewID
            WateringCanWeapon can = FindNearestWateringCan(radius);
            if (can == null)
                return new { ok = false, reason = "no_watering_can", note = "no watering can nearby" };

            Plant capTarget = target;
            WateringCanWeapon capCan = can;
            bool ok = (bool)RunOnMainThread(() =>
            {
                try
                {
                    var plantPv = capTarget.GetComponent<Photon.Pun.PhotonView>();
                    if (plantPv == null) return false;
                    var canPv = capCan.GetComponent<Photon.Pun.PhotonView>();
                    if (canPv == null) return false;
                    canPv.RPC("OnFireRPC", Photon.Pun.RpcTarget.All, plantPv.ViewID);
                    return true;
                }
                catch (Exception e) { Logger.LogWarning("water: " + e.Message); return false; }
            });

            return new { ok, watered = CleanName(capTarget.name) };
        }

        // ---- Tool: harvest (collect produce from mature plant) ----
        internal object ToolHarvest(JsonObj p)
        {
            if (_kobold == null) return new { ok = false, reason = "no_body" };
            if (_kobold.bellyContainer == null)
                return new { ok = false, reason = "no_belly" };
            float radius = _cfgFarmScanRadius != null ? _cfgFarmScanRadius.Value : 3.0f;

            Plant target = FindNearestPlant(radius);
            if (target == null)
                return new { ok = false, reason = "no_plant" };

            Plant capTarget = target;
            var harvestResult = (Dictionary<string, object>)RunOnMainThread(() =>
            {
                var res = new Dictionary<string, object>();
                try
                {
                    var sp = capTarget.plant;
                    if (sp == null) { res["ok"] = false; res["reason"] = "no_plant_data"; return res; }

                    // Mature = no possibleNextGenerations left
                    bool mature = sp.possibleNextGenerations == null || sp.possibleNextGenerations.Length == 0;
                    if (!mature)
                    {
                        res["ok"] = false;
                        res["reason"] = "not_mature";
                        res["stage"] = CleanName(sp.name);
                        res["note"] = "plant is still growing — water it and wait";
                        return res;
                    }

                    // Plant.container is private — use GetComponent fallback
                    var plantContainer = capTarget.GetComponent<GenericReagentContainer>();
                    if (plantContainer == null || plantContainer.isEmpty)
                    {
                        res["ok"] = false;
                        res["reason"] = "no_produce";
                        res["note"] = "plant has no produce to harvest";
                        return res;
                    }

                    float volume = plantContainer.volume;
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
                    transferMethod.Invoke(_kobold.bellyContainer, new object[] { plantContainer, volume, GenericReagentContainer.InjectType.Inject });
                    res["ok"] = true;
                    res["harvested"] = CleanName(capTarget.name);
                    res["volume"] = volume;
                }
                catch (Exception e)
                {
                    Logger.LogWarning("harvest: " + e.Message);
                    res["ok"] = false; res["reason"] = "error"; res["msg"] = e.Message;
                }
                return res;
            });

            return harvestResult;
        }

        // ---- Tool: plant_egg (spawn an egg into the world) ----
        internal object ToolPlantEgg(JsonObj p)
        {
            if (_kobold == null) return new { ok = false, reason = "no_body" };
            float radius = _cfgFarmScanRadius != null ? _cfgFarmScanRadius.Value : 3.0f;

            EggSpawner spawner = FindNearestEggSpawner(radius);
            if (spawner == null)
                return new { ok = false, reason = "no_egg_spawner", note = "no egg spawner nearby" };

            float volume = Mathf.Clamp(p.F("amount", 5f), 0.1f, 50f);

            EggSpawner capSpawner = spawner;
            float capVol = volume;
            bool ok = (bool)RunOnMainThread(() =>
            {
                try { capSpawner.SpawnEgg(capVol); return true; }
                catch (Exception e) { Logger.LogWarning("plant_egg: " + e.Message); return false; }
            });

            return new { ok, volume };
        }

        // ---- Perception: farm ----
        internal void FarmingPerception(Dictionary<string, object> dict)
        {
            if (_kobold == null) return;
            float radius = _cfgFarmScanRadius != null ? _cfgFarmScanRadius.Value : 3.0f;
            int maxEntries = _cfgFarmScanMax != null ? _cfgFarmScanMax.Value : 8;

            var farmList = new List<object>();
            try
            {
                Vector3 pos = _kobold.transform.position;
                foreach (var plant in SceneCache.Find<Plant>(3f))
                {
                    if (plant == null) continue;
                    float d = Vector3.Distance(pos, plant.transform.position);
                    if (d > radius * 2f) continue;
                    if (farmList.Count >= maxEntries) break;

                    var sp = plant.plant;
                    bool growing = sp != null && sp.possibleNextGenerations != null && sp.possibleNextGenerations.Length > 0;
                    string plantName = sp != null ? CleanName(sp.name) : "unknown";
                    bool hasProduce = sp != null && sp.produces != null && sp.produces.Length > 0;

                    farmList.Add(new
                    {
                        name = CleanName(plant.name),
                        plant_name = plantName,
                        growing,
                        stage = growing ? "growing" : "mature",
                        has_produce = hasProduce,
                        d = F(d),
                    });
                }
            }
            catch (Exception e) { Logger.LogDebug("farm perception: " + e.Message); }
            if (farmList.Count > 0) dict["farm"] = farmList;
        }

        // ---- Helpers ----

        // Nearest of T within `radius`, farm-flavored. Marshals internally: the tool
        // handlers call these on the LLM thread, but FindObjectsOfType/transform
        // access are main-thread APIs — and scans go through the shared TTL
        // SceneCache so each type is scanned once per window across ALL modules.
        private T FindNearestOf<T>(float radius) where T : Component
        {
            if (_kobold == null) return null;
            var capBest = RunOnMainThread(() =>
            {
                T best = null;
                float bestD = float.MaxValue;
                try
                {
                    foreach (var o in SceneCache.Find<T>(3f))
                    {
                        if (o == null) continue;
                        float d = Vector3.Distance(_kobold.transform.position, o.transform.position);
                        if (d < radius && d < bestD) { bestD = d; best = o; }
                    }
                }
                catch (Exception) { }
                return best;
            }, 5000);
            return capBest as T;
        }

        private Seed FindNearbySeed(float radius) { return FindNearestOf<Seed>(radius); }

        private Plant FindNearestPlant(float radius) { return FindNearestOf<Plant>(radius); }

        private WateringCanWeapon FindNearestWateringCan(float radius) { return FindNearestOf<WateringCanWeapon>(radius); }

        private EggSpawner FindNearestEggSpawner(float radius) { return FindNearestOf<EggSpawner>(radius); }
    }
}
