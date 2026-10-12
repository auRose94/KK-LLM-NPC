// Farming module — plant, water, harvest, plant_egg + farm perception.
// Partial class of NPCInstance; registers tools/perception via ModuleRegistry.
using System;
using System.Threading;
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
        // ---- Tool: plant (seed → ON a farm plot, aimed down at it) ----
        internal object ToolPlantSeed(JsonObj p)
        {
            if (_kobold == null) return new { ok = false, reason = "no_body" };
            float radius = _cfgFarmScanRadius != null ? _cfgFarmScanRadius.Value : 3.0f;

            // HELD FIRST: a held seed (or an EGG — the laid egg item carries a Seed
            // component!) plants where IT floats, ~1m in front of the face — the
            // body has to be ON the plot. The free-seed path below still works when
            // you'd rather plant a seed that's lying next to the tile.
            Seed heldSeed = FindHeldPlantableSeed();
            if (heldSeed != null)
                return PlantWithPositioning(heldSeed.transform, () => heldSeed, HeldItemWord(heldSeed.name));

            Seed seed = FindNearbySeed(radius);
            if (seed == null)
                return new { ok = false, reason = "no_seed",
                    note = "no plantable item — not in your hands, none on the ground nearby. get a seed/egg first (get aims for you)" };

            var r = PlantWithPositioning(seed.transform, () => seed, CleanName(seed.name));
            return r;
        }

        // --------------------------------------------------------------
        // THE PLANT MACRO — planting is positional: Seed.LocalUse plants only
        // where THE SEED is within its soil-overlap radius, and a HELD item
        // floats ~1m in front of the face. A body standing 3m off the plot
        // "used" the seed and nothing happened while we reported ok — lie
        // removed. The macro: (1) find the target soil; (2) when out of reach,
        // AUTO-APPROACH it (bounded walk bursts, max ~2 attempts); (3) aim the
        // eye DOWN at the plot; (4) LocalUse the held/near item; (5) VERIFY the
        // tile stopped being plantable — the honest ok.
        // plantable: the transform of the item that plants (seed / egg item).
        // provideItem: main-thread provider resolving the component to LocalUse
        // (GenericUsable base — Seed; egg items route through ToolInteract).
        // --------------------------------------------------------------
        internal object PlantWithPositioning(Transform plantableTf, Func<Component> provideItem, string itemName)
        {
            // (1) target soil — verify from the plantable's own footprint first
            // (the game's rule), else nearest SoilTile from the cached scan.
            Vector3 soilPos; bool plantableNow = false; SoilTile tile = null;
            var prep = RunOnMainThread(() =>
            {
                LLMNPCPlugin.MainPhase = "plant: resolve soil";
                Vector3 p0 = plantableTf.position;
                var hits = new Collider[16];
                int n = Physics.OverlapSphereNonAlloc(p0, 1.2f, hits, GameManager.instance.plantHitMask, QueryTriggerInteraction.Ignore);
                SoilTile best = null; float bd = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    var st = hits[i] != null ? hits[i].GetComponentInParent<SoilTile>() : null;
                    if (st == null || !st.GetPlantable()) continue;
                    float d = Vector3.Distance(st.transform.position, p0);
                    if (d < bd) { bd = d; best = st; }
                }
                return new object[] { best };
            });
            try
            {
                var arr = prep as object[];
                tile = arr != null && arr.Length > 0 ? arr[0] as SoilTile : null;
            }
            catch (Exception) { }
            if (tile != null) plantableNow = true;
            float dist;
            if (tile == null)
            {
                tile = NearestSoilTile(out dist);
                if (tile == null)
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false, ["reason"] = "no_soil",
                        ["note"] = "no farm plot anywhere on this map — planting is unavailable here",
                    };
            }
            else
            {
                dist = Vector3.Distance(tile.transform.position, _kobold.transform.position);
            }

            // (2) TELEPORT the item onto the plot — the reliable path: planting reads
            // where the ITEM floats, and walking-and-aiming burned many retries. The
            // held item is released first (the spring would fight the move), the
            // photon view takes ownership, the transform lands on the tile, and the
            // body stays where it is. Walk-and-approach remains only as the
            // fallback when no photon view exists to move.
            soilPos = tile.transform.position;
            bool wasHeld = false; bool teleported = false;
            try { wasHeld = (bool)RunOnMainThread(() => ItemIsHeld(plantableTf), 5000); } catch (Exception) { }
            if (wasHeld)
            {
                try
                {
                    // ToolDrop (not raw TryDrop): its person-release guards apply, and
                    // the result teaches — planting looks down, so no force-feed risk.
                    ToolDrop();
                    try { Thread.Sleep(300); } catch (Exception) { } // spring detach
                }
                catch (Exception) { }
            }
            try
            {
                var tp = RunOnMainThread(() =>
                {
                    LLMNPCPlugin.MainPhase = "plant: teleport item";
                    try
                    {
                        if (plantableTf == null) return false;
                        var pv = plantableTf.GetComponentInParent<Photon.Pun.PhotonView>();
                        if (pv == null) return false;
                        try { if (!pv.IsMine) pv.RequestOwnership(); } catch (Exception) { }
                        plantableTf.position = soilPos + Vector3.up * 0.3f;
                        var rb = plantableTf.GetComponentInParent<Rigidbody>();
                        if (rb != null) rb.velocity = Vector3.zero;
                        return true;
                    }
                    catch (Exception e) { Logger.LogWarning("plant teleport: " + e.Message); return false; }
                });
                teleported = tp != null && (bool)tp;
            }
            catch (Exception) { }
            if (teleported) AimEyeAt(soilPos + Vector3.up * 0.1f); // the eye follows the item onto the plot

            float finalDist;
            if (teleported)
            {
                finalDist = 0.3f;
            }
            else
            {
                // FALLBACK: the old walk-and-approach (no photon view to move).
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    float d = Vector3.Distance(soilPos, _kobold.transform.position);
                    if (d <= 1.8f) break;
                    AimEyeAt(soilPos + Vector3.down * 0.4f);
                    float dur = Mathf.Clamp(d / 0.9f, 0.4f, 3f);
                    SetMove(1f, 0f, false, 0f, dur, false);
                    try { Thread.Sleep((int)((dur + 0.35f) * 1000f)); } catch (Exception) { }
                    // Re-resolve the still-plantable tile nearest NOW.
                    var re = RunOnMainThread(() =>
                    {
                        SoilTile best = null; float bd = float.MaxValue;
                        foreach (var st in SoilTileList())
                        {
                            if (st == null || !st.GetPlantable()) continue;
                            float dd = Vector3.Distance(st.transform.position, _kobold.transform.position);
                            if (dd < bd) { bd = dd; best = st; }
                        }
                        return best;
                    });
                    var reTile = re as SoilTile;
                    if (reTile != null) tile = reTile;
                    soilPos = tile != null ? tile.transform.position : soilPos;
                }
                finalDist = Vector3.Distance(soilPos, _kobold != null ? _kobold.transform.position : soilPos);
                if (finalDist > 2.6f)
                {
                    Vector3 d3 = soilPos - _kobold.transform.position;
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false, ["reason"] = "too_far_from_soil", ["dist"] = F(finalDist),
                        ["note"] = "the plot is " + F(finalDist) + "m away (" + Compass.NameOfOffset(d3.x, d3.z)
                            + ") and the seed plants only where IT is — the item had no teleportable photon view; cd onto the plot and plant again",
                    };
                }
            }

            // (3) aim the eye DOWN at the ground ahead (the plantables read the dirt).
            AimEyeAt(soilPos + Vector3.down * 0.3f);

            // (4) use it.
            bool used = false;
            var useResult = RunOnMainThread(() =>
            {
                LLMNPCPlugin.MainPhase = "plant: LocalUse";
                try
                {
                    var item = provideItem != null ? provideItem() : null;
                    var gu = item as GenericUsable ?? (plantableTf != null ? plantableTf.GetComponentInParent<GenericUsable>() : null);
                    if (gu == null) return "no_use_item";
                    gu.LocalUse(_kobold);
                    return "used";
                }
                catch (Exception e) { Logger.LogWarning("plant use: " + e.Message); return "error"; }
            });
            used = "used".Equals(useResult as string, StringComparison.Ordinal);
            try { Thread.Sleep(350); } catch (Exception) { } // let the plant RPC land

            // (5) VERIFY: the tile should no longer be plantable if the seed took.
            bool took = false;
            try
            {
                took = (bool)RunOnMainThread(() =>
                {
                    LLMNPCPlugin.MainPhase = "plant: verify";
                    if (tile != null) return !tile.GetPlantable();
                    // tile may have been replaced — accept any occupied tile within 1.5m
                    var hits = new Collider[16];
                    int n = Physics.OverlapSphereNonAlloc(_kobold.transform.position, 2f, hits, GameManager.instance.plantHitMask, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < n; i++)
                        if (hits[i] != null) { var st = hits[i].GetComponentInParent<SoilTile>(); if (st != null && !st.GetPlantable()) return true; }
                    return false;
                });
            }
            catch (Exception) { }
            // (6) WATER the fresh plant when a can is in reach of it — "water them
            // if needed": the spray is fired can→plant directly, so the BODY doesn't
            // need to be anywhere special.
            string watered = "no";
            if (took)
            {
                try
                {
                    var wres = RunOnMainThread(() =>
                    {
                        try
                        {
                            LLMNPCPlugin.MainPhase = "plant: water assist";
                            // the fresh plant sits at the tile — nearest plant to it
                            Plant plant = null; float pd = float.MaxValue;
                            foreach (var pl in SceneCache.Find<Plant>(3f))
                            {
                                if (pl == null) continue;
                                float d = Vector3.Distance(pl.transform.position, soilPos);
                                if (d < pd && d <= 2.5f) { pd = d; plant = pl; }
                            }
                            if (plant == null) return "no_plant";
                            WateringCanWeapon can = null; float cd = float.MaxValue;
                            foreach (var w in SceneCache.Find<WateringCanWeapon>(30f))
                            {
                                if (w == null) continue;
                                float d = Vector3.Distance(w.transform.position, plant.transform.position);
                                if (d < cd) { cd = d; can = w; }
                            }
                            if (can == null || cd > 5f)
                            {
                                // No can in reach → the direct watering assist. This
                                // runs INSIDE a main-thread marshal: call the MAIN
                                // worker directly (no nested marshal).
                                var rain = WaterPlantDirectMain(plant, 60f);
                                return "rained".Equals(rain as string, StringComparison.Ordinal) ? "rained_direct" : "can_far:" + F(cd);
                            }
                            var plantPv = plant.GetComponent<Photon.Pun.PhotonView>();
                            var canPv = can.GetComponent<Photon.Pun.PhotonView>();
                            if (plantPv == null || canPv == null) return "no_view";
                            canPv.RPC("OnFireRPC", Photon.Pun.RpcTarget.All, plantPv.ViewID);
                            return "watered";
                        }
                        catch (Exception e) { Logger.LogWarning("fresh plant water: " + e.Message); return "error"; }
                    });
                    watered = wres as string ?? "no";
                    if (watered == "watered") { try { Thread.Sleep(250); } catch (Exception) { } }
                }
                catch (Exception) { }
            }

            var res = new Dictionary<string, object>
            {
                ["ok"] = took || (used && plantableNow),
                ["item"] = itemName,
                ["planted"] = took,
                ["teleported"] = teleported,
                ["dist_to_soil"] = F(finalDist),
                ["watered"] = watered.StartsWith("watered", StringComparison.Ordinal) || watered.StartsWith("rained", StringComparison.Ordinal) ? "yes" : "no",
            };
            // Name the plot so the model knows the egg/seed IS on a valid soil tile.
            try
            {
                if (tile != null)
                {
                    var tName = CleanName(tile.name);
                    res["plot"] = tName + " (id:" + TargetIdFor(tile.transform, tile.name) + ", "
                        + StationAddr(tName, _kobold.transform.position, tile.transform.position) + ")";
                }
            }
            catch (Exception) { }
            if ("rained_direct".Equals(watered, StringComparison.Ordinal))
                res["note"] = "planted and watered directly (no can in reach — the mod applied the water)";
            else if (watered.StartsWith("can_far", StringComparison.Ordinal))
                res["note"] = "planted — the nearest watering can is " + watered.Substring("can_far:".Length)
                    + "m from the plot and direct watering was unavailable; water it later with `water`";
            if (took && !res.ContainsKey("note"))
                res["note"] = "planted ON a valid soil tile (see `plot`) — it grows there stage by stage; water it when stages remain";
            if (!took)
                res["note"] = used
                    ? "used (item teleported onto the tile where needed), but the tile still reads plantable — nothing grew; check `ls` for the plot and report if it persists"
                    : "nothing was used" + (plantableNow ? "" : " — the item had no usable seed component; check `cat hold`");
            return res;
        }

        // Is this plantable transform currently gripped by this body?
        private bool ItemIsHeld(Transform tf)
        {
            try
            {
                foreach (var h in HeldScan())
                {
                    if (h == null || h.Grabbable == null) continue;
                    if (h.Grabbable.transform == tf || h.Grabbable.transform.IsChildOf(tf) || tf.IsChildOf(h.Grabbable.transform)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        // Eye aim helper (main thread): yaw + pitch at a world point — plants read
        // downward, so this clamps to a strong down-look for soil work.
        internal void AimEyeAt(Vector3 worldPoint)
        {
            try
            {
                if (!IsAlive(_kobold)) return;
                Vector3 eye = _head != null ? _head.position : _kobold.transform.position + Vector3.up * 0.6f;
                Vector3 to = worldPoint - eye;
                Vector3 flat = to; flat.y = 0;
                float yaw = Mathf.Atan2(flat.x, flat.z) * Consts.Rad2Deg;
                float pitch = Mathf.Clamp(Mathf.Atan2(-to.y, Mathf.Max(0.25f, flat.magnitude)) * Consts.Rad2Deg, -70f, 45f);
                lock (_stateLock) { _yawDeg = Mathf.Repeat(yaw, 360f); _pitchDeg = pitch; }
                _lastBigTurnTime = Time.unscaledTime;
            }
            catch (Exception) { }
        }

        // The held item's plantable Seed — seeds AND eggs both carry one (eggs are
        // the game's laid-egg item chain: Seed → Plant → KoboldSpawnPlant). Null
        // when the hands are empty or hold something unplantable. LLM-thread safe
        // (same scan path tools already use).
        internal Seed FindHeldPlantableSeed()
        {
            try
            {
                foreach (var h in HeldScan())
                {
                    if (h == null || h.Victim != null || h.Grabbable == null) continue;
                    bool plantable = h.Kind == "seed"
                        || (h.Name ?? "").IndexOf("egg", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!plantable) continue;
                    var seed = h.Grabbable.transform.GetComponentInChildren<Seed>(true);
                    if (seed != null) return seed;
                }
            }
            catch (Exception) { }
            return null;
        }

        private static string HeldItemWord(string itemName)
        {
            string n = (itemName ?? "").ToLowerInvariant();
            return n.IndexOf("egg", StringComparison.Ordinal) >= 0 ? "egg" : "seed";
        }

        // Nearest SoilTile from the cached scene scan; -1 dist when none.
        internal SoilTile NearestSoilTile(out float dist)
        {
            dist = float.MaxValue;
            SoilTile best = null;
            try
            {
                foreach (var st in SoilTileList())
                {
                    if (st == null) continue;
                    float d = Vector3.Distance(st.transform.position, _kobold.transform.position);
                    if (d < dist) { dist = d; best = st; }
                }
            }
            catch (Exception) { }
            return best;
        }


        // Growth stage summary for a plant: the CURRENT stage name (each growth
        // state is its own ScriptablePlant), stages remaining, grown flag.
        internal static object PlantStages(Plant plant)
        {
            try
            {
                var sp = plant != null ? plant.plant : null;
                int left = sp != null && sp.possibleNextGenerations != null ? sp.possibleNextGenerations.Length : 0;
                string stage = sp != null ? CleanNameStatic(sp.name) : "?";
                return new { stage_name = stage, stages_left = left, mature = left == 0 };
            }
            catch (Exception) { return null; }
        }

        private static string CleanNameStatic(string n)
        {
            try { if (string.IsNullOrEmpty(n)) return "?"; string s = n; int cut = s.IndexOf('('); if (cut > 0) s = s.Substring(0, cut); return s.Trim(); }
            catch (Exception) { return "?"; }
        }

        // Small registry match: the nearest registered target whose cleaned name
        // contains the hint (used by `water <name>`; ids go through FindTargetById).
        private Transform FindNearbyTransformByName(string hint)
        {
            try
            {
                Transform best = null; float bd = float.MaxValue;
                lock (_targetIdByInst)
                {
                    foreach (var kv in _targetIdByInst)
                    {
                        var t = FindTargetById(kv.Value);
                        if (t == null) continue;
                        string n = CleanName(t.name).ToLowerInvariant();
                        if (n.IndexOf(hint, StringComparison.Ordinal) < 0) continue;
                        float d = (t.position - _kobold.transform.position).sqrMagnitude;
                        if (d < bd) { bd = d; best = t; }
                    }
                }
                return best;
            }
            catch (Exception) { return null; }
        }

        // The photon view the current ToolWater call sprays at (captured on the LLM
        // thread, consumed inside the main-thread marshal — no closure over locals).
        private Photon.Pun.PhotonView _waterSprayView;


        // ------------------------------------------------------------------
        // WATERING ASSIST — water a plant with NO can or bucket: inject the
        // WATER reagent straight into the plant's own reagent container via its
        // photon RPC (the exact effect water drops produce). The agents kept
        // losing whole garden cycles to a missing/emptied can; watering is the
        // one step the game requires equipment for and the mod supplies it.
        // Status word result: rained/no_plant/no_container/no_water_reagent/off.
        // ------------------------------------------------------------------
        // WATERING ASSIST — public entry (any thread): marshals to main.
        internal object WaterPlantDirect(Plant plant, float ml)
        {
            if (plant == null) return "no_plant";
            try { if (_cfgWateringAssist != null && !_cfgWateringAssist.Value) return "off"; } catch (Exception) { }
            try
            {
                var plantRef = plant;
                var res = RunOnMainThread(() => WaterPlantDirectMain(plantRef, ml), 8000);
                return res as string ?? "error";
            }
            catch (Exception e)
            {
                try { Logger.LogWarning("water assist (marshal): " + e.Message); } catch (Exception) { }
                return "error";
            }
        }

        // MAIN-THREAD ONLY (no marshals inside — callers on main call this directly;
        // the public wrapper marshals for everyone else).
        internal object WaterPlantDirectMain(Plant plant, float ml)
        {
            try
            {
                if (!ReagentDatabase.TryGetAsset("Water", out var waterReagent) || waterReagent == null)
                    return "no_water_reagent";
                var pv = plant.GetComponent<Photon.Pun.PhotonView>();
                if (pv == null) return "no_view";
                var cont = plant.GetComponent<GenericReagentContainer>();
                if (cont == null) return "no_container";
                // THE GROWTH LADDER (player-verified): plant → water → stage 2 →
                // water → stage 3 → fruits or spawning. ONE watering = exactly ONE
                // stage, so a fixed moderate dose; over-filling buys nothing.
                float dose = Mathf.Max(5f, ml);
                var mix = new ReagentContents();
                mix.AddMix(waterReagent.GetReagent(dose));
                var buf = new NetStack.Serialization.BitBuffer(32);
                buf.AddReagentContents(mix);
                pv.RPC(nameof(GenericReagentContainer.AddMixRPC), Photon.Pun.RpcTarget.All, buf, pv.ViewID,
                    (byte)GenericReagentContainer.InjectType.Inject);
                try { Logger.LogInfo("farm: watered '" + CleanName(plant.name) + "' directly (" + F(dose) + "ml water, no can)"); } catch (Exception) { }
                return "rained";
            }
            catch (Exception e)
            {
                try { Logger.LogWarning("water assist: " + e.Message); } catch (Exception) { }
                return "error";
            }
        }

        // ---- Tool: water (watering can → nearest plant) ----
        internal object ToolWater(JsonObj p)
        {
            if (_kobold == null) return new { ok = false, reason = "no_body" };
            float radius = _cfgFarmScanRadius != null ? _cfgFarmScanRadius.Value : 3.0f;

            // Optional specific target: `water id:N` / `water <name>` — a plant OR a
            // soil tile (planted seeds/EGGS sit in the dirt before a Plant entity
            // exists; the spray fires at the tile's photon view and reaches them).
            Transform pinned = null; string pinnedWord = null;
            try
            {
                if (p.Has("id"))
                {
                    double idv = -1; try { idv = p.DB("id", -1); } catch (Exception) { }
                    if (idv >= 0) { var tf = FindTargetById((int)idv); if (tf != null) { pinned = tf; pinnedWord = CleanName(tf.name); } }
                }
                else if (p.Has("name") || p.Has("target"))
                {
                    // Name hint → match against the nearby item registry by cleaned name.
                    string h = (p.S("name", p.S("target", "")) ?? "").Trim().ToLowerInvariant();
                    if (h.Length > 0) pinned = FindNearbyTransformByName(h);
                }
            }
            catch (Exception) { }

            Plant target = null;
            object sprayTargetView = null; // the PhotonView the can fires at
            if (pinned != null)
            {
                try
                {
                    var pl = pinned.GetComponentInParent<Plant>();
                    if (pl != null) { target = pl; sprayTargetView = pl.GetComponent<Photon.Pun.PhotonView>(); }
                    else
                    {
                        var tile = pinned.GetComponentInParent<SoilTile>();
                        if (tile != null) sprayTargetView = tile.GetComponent<Photon.Pun.PhotonView>();
                    }
                }
                catch (Exception) { }
            }
            if (target == null && sprayTargetView == null)
            {
                target = FindNearestPlant(radius);
                if (target != null) sprayTargetView = target.GetComponent<Photon.Pun.PhotonView>();
            }
            if (sprayTargetView == null)
            {
                // No plant in reach — teach with the nearest plot's location instead
                // of a bare refusal (planted seeds/eggs sit IN the dirt).
                try
                {
                    float d;
                    var st = NearestSoilTile(out d);
                    if (st != null && d <= 6f)
                    {
                        var pv = st.GetComponent<Photon.Pun.PhotonView>();
                        if (pv != null)
                        {
                            sprayTargetView = pv;
                            pinnedWord = "the nearest plot (" + F(d) + "m — a seed/egg may sit there)";
                            target = null;
                        }
                    }
                }
                catch (Exception) { }
            }
            if (sprayTargetView == null)
                return new { ok = false, reason = "no_plant",
                    note = "nothing in reach to water — no plant and no plot within range; check `cat places` for the farm" };

            var capSpray = sprayTargetView;
            // The fire targets the SPRAY VIEW — a Plant (or a soil tile's photon view
            // when watering a planted seed/egg that has no Plant entity yet). Carried
            // into the main-thread marshal through _waterSprayView (captured here).
            _waterSprayView = capSpray as Photon.Pun.PhotonView;

            // Find the nearest watering can — its RPC fires at a target viewID.
            // When none is within reach, point at the nearest one MAP-WIDE (the
            // key-places scan) instead of a dead end — small models otherwise
            // conclude "no watering can" exists and give up on the whole farm.
            WateringCanWeapon can = FindNearestWateringCan(radius);
            if (can == null)
            {
                string hint = "no watering can nearby";
                try
                {
                    hint = (string)RunOnMainThread(() =>
                    {
                        var cans = WateringCanList();
                        WateringCanWeapon nearest = null;
                        float bestD = float.MaxValue;
                        Vector3 origin = _kobold.transform.position;
                        if (cans != null)
                            foreach (var w in cans)
                            {
                                if (w == null || w.transform == null) continue;
                                float d = Vector3.Distance(w.transform.position, origin);
                                if (d < bestD) { bestD = d; nearest = w; }
                            }
                        if (nearest == null)
                            return "no watering can exists anywhere on this map — watering is unavailable until one is bought or brought here";
                        Vector3 d3 = nearest.transform.position - origin;
                        return "nearest watering can (map-wide): " + CleanName(nearest.name)
                            + " " + StationAddr(CleanName(nearest.name), origin, nearest.transform.position)
                            + " ~" + F(d3.magnitude) + "m " + Compass.NameOfOffset(d3.x, d3.z)
                            + " — cd water, then try again near it";
                    }, 5000);
                }
                catch (Exception) { }
                // NO CAN IN REACH → the watering assist: inject the water directly
                // into the target plant's container (config-gated). The garden no
                // longer dies to a missing/emptied can. All scene reads marshal.
                Plant assistPlant = null;
                try
                {
                    assistPlant = target;
                    if (assistPlant == null)
                    {
                        var sprayView = _waterSprayView; // capture for the marshal
                        var resolved = RunOnMainThread(() =>
                        {
                            try
                            {
                                var tileRef = sprayView != null ? sprayView.GetComponentInParent<SoilTile>() : null;
                                if (tileRef != null)
                                {
                                    try
                                    {
                                        var planted = NPCInstance.GetField<Plant>(tileRef, "planted");
                                        if (planted != null) return planted;
                                    }
                                    catch (Exception) { }
                                    foreach (var pl in SceneCache.Find<Plant>(3f))
                                        if (pl != null && Vector3.Distance(pl.transform.position, tileRef.transform.position) <= 2.5f)
                                            return pl;
                                }
                            }
                            catch (Exception) { }
                            return null;
                        }, 8000);
                        assistPlant = resolved as Plant;
                    }
                }
                catch (Exception) { }
                var rain = WaterPlantDirect(assistPlant, 60f);
                if ("rained".Equals(rain as string, StringComparison.Ordinal))
                    return new
                    {
                        ok = true,
                        watered = assistPlant != null ? CleanName(assistPlant.name) : (pinnedWord ?? "the dirt"),
                        how = "direct (no can — the mod applied the water)",
                    };
                return new { ok = false, reason = "no_watering_can", note = hint + " (direct watering: " + (rain as string ?? "failed") + ")" };
            }

            Plant capTarget = target;
            WateringCanWeapon capCan = can;
            bool ok = (bool)RunOnMainThread(() =>
            {
                try
                {
                    var canPv = capCan.GetComponent<Photon.Pun.PhotonView>();
                    if (canPv == null) return false;
                    var sprayPvL = _waterSprayView;
                    if (sprayPvL == null) return false;
                    canPv.RPC("OnFireRPC", Photon.Pun.RpcTarget.All, sprayPvL.ViewID);
                    return true;
                }
                catch (Exception e) { Logger.LogWarning("water: " + e.Message); return false; }
            });

            // Tell the model exactly WHAT got watered (plot name/id) and the plant's
            // growth state — "couldn't get it past the second stage" was invisible
            // without growth state in the result.
            string wname = capTarget != null ? CleanName(capTarget.name) : (pinnedWord ?? "the dirt");
            Plant stagePlant = capTarget;
            try
            {
                if (stagePlant == null && _waterSprayView != null)
                {
                    var tileRef = _waterSprayView.GetComponentInParent<SoilTile>();
                    if (tileRef != null)
                        try { stagePlant = NPCInstance.GetField<Plant>(tileRef, "planted"); } catch (Exception) { }
                }
            }
            catch (Exception) { }
            var result = new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["watered"] = wname,
            };
            try
            {
                if (stagePlant != null)
                {
                    var stg = PlantStages(stagePlant);
                    var t = stg.GetType();
                    int left = (int)(t.GetProperty("stages_left").GetGetMethod().Invoke(stg, null));
                    bool mature = (bool)(t.GetProperty("mature").GetGetMethod().Invoke(stg, null));
                    result["plot"] = CleanName(stagePlant.name) + " (" + StationAddr(CleanName(stagePlant.name), _kobold.transform.position, stagePlant.transform.position) + ")";
                    var stageName = (string)(t.GetProperty("stage_name").GetGetMethod().Invoke(stg, null));
                    result["stages_left"] = left;
                    result["mature"] = mature;
                    result["stage"] = stageName;
                    result["note"] = mature
                        ? "fully grown — FRUITS (harvest it) or, for an egg, it spawns a fresh body; nothing more to water"
                        : "now at stage '" + stageName + "' — water again when ready (" + left + " more watering(s) needed)";
                }
            }
            catch (Exception) { }
            return result;
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
