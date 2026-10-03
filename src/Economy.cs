// Economy — coins + buying. The prompts promise that NPCs can buy machine
// contracts / shop items / kobold deliveries; this module makes that real.
// Research-backed APIs (KoboldKare source, 2026-10):
//   * every kobold (NPC bodies included) carries a MoneyHolder — GetMoney() is
//     public; charges sync via IPunObservable on the view owner.
//   * purchases are plain local calls: CanUse(kobold) then LocalUse(kobold) —
//     LocalUse charges OUR kobold's own wallet and broadcasts the RPCUse to All
//     (this is byte-for-byte what a human player's click does).
//   * CanUse is mandatory on GenericPurchasable (its LocalUse charges
//     unconditionally) and KoboldDelivery/ConstructionContract are self-guarded,
//     but the shared CanUse check covers all shapes.
// Main-thread marshalled; scans go through SceneCache.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KKLLMNPC
{
    internal static class EconomyModule
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
                    ["description"] = "name or nearby id of the thing to buy (contract/dispenser/shop item); omit for whatever's closest"
                },
            };
            ModuleRegistry.Tool("buy", (n, p) => n.ToolBuy(p), schemas);
            ModuleRegistry.Perception((n, d) => n.EconomyPerception(d));
        }
    }

    internal partial class NPCInstance
    {
        // Coins in the body's own wallet (-1 when unknown: no body / no holder).
        internal int GetCoins()
        {
            try
            {
                if (_kobold == null) return -1;
                var mh = _kobold.GetComponent<MoneyHolder>();
                if (mh != null) return Mathf.RoundToInt(mh.GetMoney());
            }
            catch (Exception) { }
            return -1;
        }

        // Perception key 'economy': wallet + nearby purchasables (name/kind/cost/dist).
        internal void EconomyPerception(Dictionary<string, object> dict)
        {
            try
            {
                if (_kobold == null) return;
                int coins = GetCoins();
                if (coins >= 0)
                {
                    var d = new Dictionary<string, object> { ["coins"] = coins };
                    var list = new List<object>();
                    var buys = NearBuyables(6f);
                    int shown = 0;
                    foreach (var b in buys)
                    {
                        if (shown++ >= 6) break;
                        var e = new Dictionary<string, object>
                        {
                            ["name"] = CleanName(b.Obj.name),
                            ["kind"] = b.Kind,
                            ["d"] = F(Vector3.Distance(b.Obj.transform.position, _kobold.transform.position)),
                        };
                        if (b.Cost >= 0) e["cost"] = Mathf.RoundToInt(b.Cost);
                        if (!b.Can) e["note"] = "unaffordable/star-locked";
                        list.Add(e);
                    }
                    if (list.Count > 0) d["purchasables"] = list;
                    dict["economy"] = d;
                }
            }
            catch (Exception e) { Logger.LogDebug("economy perception: " + e.Message); }
        }

        private sealed class Buyable
        {
            public Component Obj;
            public string Kind;    // "contract" / "machine" / "dispenser" / "shop_item"
            public float Cost = -1f;
            public bool Can;
        }

        // Purchasables within radius: machine construction contracts (the ":needs_buy"
        // tags the model already sees), the kobold dispenser, and shop stations.
        private List<Buyable> NearBuyables(float radius)
        {
            var list = new List<Buyable>();
            try
            {
                Vector3 pos = _kobold.transform.position;
                foreach (var c in SceneCache.Find<ConstructionContract>(3f))
                {
                    if (c == null) continue;
                    var b = new Buyable { Obj = c, Kind = "contract", Cost = ReflectCost(c) };
                    try { b.Can = c.CanUse(_kobold); } catch (Exception) { b.Can = false; }
                    if (Vector3.Distance(c.transform.position, pos) <= radius) list.Add(b);
                }
                foreach (var k in SceneCache.Find<KoboldDelivery>(3f))
                {
                    if (k == null) continue;
                    var b = new Buyable { Obj = k, Kind = "dispenser", Cost = ReflectPrice(k) };
                    try { b.Can = k.CanUse(_kobold); } catch (Exception) { b.Can = false; }
                    if (Vector3.Distance(k.transform.position, pos) <= radius) list.Add(b);
                }
                foreach (var s in SceneCache.Find<GenericPurchasable>(3f))
                {
                    if (s == null) continue;
                    var b = new Buyable { Obj = s, Kind = "shop_item", Cost = ReflectPrice(s) };
                    try { b.Can = s.CanUse(_kobold); } catch (Exception) { b.Can = false; }
                    if (Vector3.Distance(s.transform.position, pos) <= radius) list.Add(b);
                }
            }
            catch (Exception) { }
            list.Sort((a, b) =>
                Vector3.Distance(a.Obj.transform.position, _kobold.transform.position)
                .CompareTo(Vector3.Distance(b.Obj.transform.position, _kobold.transform.position)));
            return list;
        }

        // cost is private on ConstructionContract, price/GetPrice() private elsewhere.
        private static float ReflectCost(object target)
        {
            try
            {
                var f = target.GetType().GetField("cost",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (f != null && f.FieldType == typeof(float)) return (float)f.GetValue(target);
            }
            catch (Exception) { }
            return -1f;
        }

        private static float ReflectPrice(object target)
        {
            try
            {
                var m = target.GetType().GetMethod("GetPrice",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                    null, Type.EmptyTypes, null);
                if (m != null && m.ReturnType == typeof(float)) return (float)m.Invoke(target, null);
            }
            catch (Exception) { }
            return ReflectCost(target);
        }

        // ---- Tool: buy (target optional: name/id; default whatever's closest) ----
        internal object ToolBuy(JsonObj p)
        {
            if (_kobold == null) return new { ok = false, reason = "no_body" };
            // S("target") returns "" (never null) when absent, so a ?? chain would
            // never consult "name" — check both keys explicitly.
            string want = (p != null ? p.S("target", "") : "") ?? "";
            if (string.IsNullOrEmpty(want)) want = (p != null ? p.S("name", "") : "") ?? "";
            want = want.Trim().ToLowerInvariant();
            double idv = -1;
            bool hasId = false;
            try { if (p.Has("id")) { idv = p.DB("id", -1); hasId = true; } } catch (Exception) { }

            var res = RunOnMainThread(() =>
            {
                Buyable pick = null;
                try
                {
                    var buys = NearBuyables(Mathf.Max(InteractRange * 1.5f, 4f));
                    foreach (var b in buys)
                    {
                        if (hasId)
                        {
                            var c = b.Obj.GetComponent<GenericUsable>() ?? b.Obj.GetComponentInParent<GenericUsable>();
                            if (c == null) continue;
                            var tid = FindTargetById((int)idv);
                            if (tid == null) continue;
                            if (c.transform.IsChildOf(tid) || tid.IsChildOf(c.transform)
                                || c.transform == tid) { pick = b; break; }
                            continue;
                        }
                        if (want.Length > 0)
                        {
                            string n = CleanName(b.Obj.name).ToLowerInvariant();
                            if (!n.Contains(want) && !b.Kind.Contains(want)) continue;
                        }
                        pick = b; // buys sorted nearest-first
                        break;
                    }
                }
                catch (Exception e) { Logger.LogWarning("buy resolve: " + e.Message); }
                if (pick == null) return (object)new
                {
                    ok = false,
                    reason = "nothing_to_buy",
                    note = "no purchasable within reach — walk to a machine contract, the kobold dispenser, or a shop item first (economy.purchasables lists them)"
                };

                // Fail CLOSED: GenericPurchasable.LocalUse charges unconditionally, so
                // any doubt (missing component, CanUse throwing) = refuse to buy.
                var gu = pick.Obj.GetComponent<GenericUsable>();
                if (gu == null) return (object)new
                {
                    ok = false,
                    reason = "cannot_buy",
                    name = CleanName(pick.Obj.name),
                    note = "not a purchasable after all — economy.purchasables lists real ones"
                };
                bool canBuy = false;
                try { canBuy = gu.CanUse(_kobold); } catch (Exception) { canBuy = false; }
                if (!canBuy) return (object)new
                {
                    ok = false,
                    reason = "cannot_buy",
                    name = CleanName(pick.Obj.name),
                    cost = pick.Cost >= 0 ? (object)Mathf.RoundToInt(pick.Cost) : null,
                    coins = GetCoins(),
                    hint = "unaffordable (charge money first) or the contract needs completion stars; economy.purchasables marks unaffordable ones"
                };

                var coinHolder = _kobold.GetComponent<MoneyHolder>();
                float before = coinHolder != null ? coinHolder.GetMoney() : -1f;
                gu.LocalUse(_kobold);
                float after = coinHolder != null ? coinHolder.GetMoney() : -1f;
                float paid = before >= 0f && after >= 0f ? before - after : 0f;
                return (object)new
                {
                    ok = true,
                    bought = CleanName(pick.Obj.name),
                    kind = pick.Kind,
                    paid = Mathf.RoundToInt(paid),
                    coins = after >= 0f ? Mathf.RoundToInt(after) : (int?)null,
                    note = pick.Kind == "contract"
                        ? "machine unlocked (master client constructs it — wait, then it appears)"
                        : pick.Kind == "dispenser" ? "paid — a kobold delivery is incoming"
                        : "purchased — the item drops at the station shortly"
                };
            });
            return res;
        }
    }
}