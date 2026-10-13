// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// ModGrab — carrying correctness for AI bodies.
//
// ROOT CAUSE (from the game source): Grabber.LateUpdate re-anchors every held item
// each frame at `GetViewPos() + OrbitCamera.GetPlayerIntendedRotation() * offset` —
// and GetPlayerIntendedRotation is the LOCAL PLAYER'S camera aim. For an AI body the
// position (the grabber's own view transform) is right, but the ROTATION belongs to
// whoever the human player is looking: held items swing around the body when the
// player turns (the "flys around the thing it's targeting and circles" symptom), and
// TryGrab's sphere + sort ray sit somewhere the agent never looks (small items, the
// water bucket, unpickable).
//
// FIX: body-scoped Harmony prefixes. When the grabber's owner kobold is AI-driven by
// this mod, LateUpdate and TryGrab are REPLACED with versions using the agent's own
// eye state (_yawDeg/_pitchDeg). The human player's grabber runs the original code.
// Registration: Main.Awake creates the Harmony instance; FarmingModule-style
// registration makes `deliver` a module tool for both modes.
using System;
using System.Threading;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KKLLMNPC
{
    internal static class ModGrabManager
    {
        private static bool _patched;
        private const string LoggerTag = "modgrab";

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            if (_patched || harmony == null) return;
            _patched = true;
            try
            {
                // typeof, NOT AccessTools.TypeByName: TypeByName misses the name-only
                // resolution and falls back to scanning EVERY loaded assembly —
                // UnityEngine.DirectorModule throws a VTable TypeLoadException during
                // that scan and HarmonyX emits the "[Warning: HarmonyX]
                // AccessTools.GetTypesFromAssembly … PlayableDirector" warning on every
                // plugin boot. The mod compiles against Assembly-CSharp, so the type is
                // one typeof away.
                var grabberType = typeof(Grabber);
                if (grabberType == null)
                {
                    LLMNPCPlugin.Log?.LogWarning("modgrab: Grabber type not found — carry patches skipped");
                    return;
                }
                harmony.Patch(AccessTools.Method(grabberType, "LateUpdate"),
                    prefix: new HarmonyMethod(typeof(ModGrabManager), nameof(GrabberLateUpdatePrefix)));
                harmony.Patch(AccessTools.Method(grabberType, "TryGrab"),
                    prefix: new HarmonyMethod(typeof(ModGrabManager), nameof(GrabberTryGrabPrefix)));
                LLMNPCPlugin.Log?.LogInfo("modgrab: AI-body grab patches applied (LateUpdate + TryGrab)");
            }
            catch (Exception e)
            {
                LLMNPCPlugin.Log?.LogWarning("modgrab: " + e.Message);
            }
        }

        // True when this grabber belongs to a body this mod is AI-driving.
        // Static resolver handed in at registration (InstanceForBody is an instance
        // method on the plugin; the patches must not depend on reaching an instance).
        internal static Func<Kobold, NPCInstance> ResolveNpc;

        // --------------------------------------------------------------
        // STUTTER FIX: the prefixes run EVERY frame for EVERY grabber (AI and
        // player alike), and the first version did String-keyed reflection per
        // frame — type.GetField scans metadata each call, so a scene with several
        // grabbers hitched every LateUpdate. Now: Grabber handles + AI-ownership
        // are cached per grabber instance; ownership rechecks ~1/s; per-frame
        // work is dictionary reads + cached FieldInfo.GetValue.
        // --------------------------------------------------------------
        private sealed class GrabberCache
        {
            public FieldInfo FPlayer, FView, FOffset, FGrabs, FMaxCount, FSpring, FDamp;
            public Kobold Player;
            public Transform View;
            public Vector3 Offset;
            public NPCInstance Npc;      // null = not AI-owned (checked every ~1s)
            public float NextAiCheck;
            public bool Checked;
        }

        private static readonly object _cacheLock = new object();
        private static readonly Dictionary<int, GrabberCache> _byInstance = new Dictionary<int, GrabberCache>();
        private const float AiRecheckSeconds = 1.0f;

        private static GrabberCache GrabGrabberCache(Grabber g)
        {
            int id = g.GetInstanceID();
            lock (_cacheLock)
            {
                if (_byInstance.TryGetValue(id, out var c) && c.FPlayer != null) return c;
            }
            var nc = new GrabberCache
            {
                FPlayer = typeof(Grabber).GetField("player", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
                FView = typeof(Grabber).GetField("view", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
                FOffset = typeof(Grabber).GetField("defaultOffset", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
                FGrabs = typeof(Grabber).GetField("grabbedObjects", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
                FMaxCount = typeof(Grabber).GetField("maxGrabCount", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
                FSpring = typeof(Grabber).GetField("springStrength", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
                FDamp = typeof(Grabber).GetField("dampingStrength", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            };
            if (nc.FPlayer == null) nc.FPlayer = typeof(Grabber).GetField("__missing__"); // marker: type shape unexpected
            lock (_cacheLock) _byInstance[id] = nc;
            return nc;
        }

        // One ownership pass per grabber per ~1s; per-frame calls just read `Npc`.
        private static NPCInstance OwnerNpc(Grabber grabber)
        {
            try
            {
                if (ResolveNpc == null) return null;
                var c = GrabGrabberCache(grabber);
                float now = Time.unscaledTime;
                if (!c.Checked || now >= c.NextAiCheck)
                {
                    c.Checked = true;
                    c.NextAiCheck = now + AiRecheckSeconds;
                    c.Player = c.FPlayer != null && c.FPlayer != null ? c.FPlayer.GetValue(grabber) as Kobold : null;
                    c.Npc = (c.Player != null) ? ResolveNpc(c.Player) : null;
                    c.View = null; // re-read (cheap) below — ownership changed hands
                }
                return c.Npc;
            }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------------
        // LateUpdate: the held-item spring — anchored to the AGENT's own eye.
        // ------------------------------------------------------------------
        private static bool GrabberLateUpdatePrefix(Grabber __instance)
        {
            NPCInstance npc = OwnerNpc(__instance);
            if (npc == null) return true; // human player (or unclaimed body): vanilla
            // FAIL-SAFE: ANY problem here falls back to the original code; cached
            // handles only (no String reflection per frame).
            try
            {
                var c = GrabGrabberCache(__instance);
                var grabs = c.FGrabs != null ? c.FGrabs.GetValue(__instance) as System.Collections.IList : null;
                if (grabs == null || grabs.Count == 0) return true;
                var first = grabs[0];
                var set = first != null ? AccessTools.Method(first.GetType(), "Set") : null;
                if (set == null) return true; // unknown GrabInfo shape — vanilla owns it
                if (c.View == null) c.View = c.FView != null ? c.FView.GetValue(__instance) as Transform : null;
                var view = c.View;
                var player = c.Player;
                if (c.Offset == default(Vector3)) c.Offset = c.FOffset != null ? (Vector3)(c.FOffset.GetValue(__instance)) : Vector3.forward;
                try { __instance.Validate(); } catch (Exception) { }
                Vector3 vpos = view != null && view.position != Vector3.zero
                    ? view.position
                    : (player != null ? player.transform.position + Vector3.up * 0.6f : npc.EyePosition());
                Quaternion rot = npc.EyeRotation();
                foreach (var g in grabs)
                {
                    if (g == null) continue;
                    try { set.Invoke(g, new object[] { vpos, rot, c.Offset }); }
                    catch (Exception) { }
                }
                return false; // AI body: our spring wins (fully performed)
            }
            catch (Exception)
            {
                return true; // anything unexpected: the original code stays in charge
            }
        }

        private static Type grabberInnerType()
        {
            try
            {
                // typeof — see Apply: TypeByName's all-assembly fallback scan is what
                // produced the HarmonyX DirectorModule warning per boot.
                foreach (var t in typeof(Grabber).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                    if (t.Name == "GrabInfo") return t;
            }
            catch (Exception) { }
            return null;
        }

        // ------------------------------------------------------------------
        // TryGrab: candidate search in the AGENT'S view cone — not the player's.
        // Replicates the original: sphere at eye+rot*offset radius 1, distance-
        // sorted, CanGrab, grab-count capped, then the grabbable's own RPC plus
        // the local bookkeeping the original performs (the spring is LateUpdate,
        // which this file owns for AI bodies anyway).
        // ------------------------------------------------------------------
        private static bool GrabberTryGrabPrefix(Grabber __instance)
        {
            NPCInstance npc = OwnerNpc(__instance);
            if (npc == null) return true;
            try
            {
                var c = GrabGrabberCache(__instance);
                var player = c.Player != null ? c.Player : (c.FPlayer != null ? c.FPlayer.GetValue(__instance) as Kobold : null);
                var bodyObj = npc.KoboldObj;
                if (player == null || bodyObj == null || !ReferenceEquals(bodyObj, player)) return true;
                var grabs = c.FGrabs != null ? c.FGrabs.GetValue(__instance) as System.Collections.IList : null;
                if (grabs == null) return true; // unknown shape — vanilla owns it
                int maxCount;
                try { maxCount = c.FMaxCount != null ? Convert.ToInt32(c.FMaxCount.GetValue(__instance)) : 1; if (maxCount < 1) maxCount = 1; }
                catch (Exception) { maxCount = 1; }
                if (grabs.Count >= maxCount) return false;

                var view = c.FView != null ? c.FView.GetValue(__instance) as Transform : null;
                Vector3 eye = view != null && view.position != Vector3.zero
                    ? view.position
                    : player.transform.position + Vector3.up * 0.6f;
                if (c.Offset == default(Vector3)) c.Offset = c.FOffset != null ? (Vector3)(c.FOffset.GetValue(__instance)) : Vector3.forward;
                Vector3 offset = c.Offset;
                Quaternion rot = npc.EyeRotation();
                Vector3 center = eye + rot * offset;
                Vector3 fwd = rot * Vector3.forward;

                // Already-held set (skip their colliders).
                var held = new HashSet<int>();
                foreach (var g in grabs)
                {
                    try
                    {
                        var pv = g != null ? GetGrabView(g) : null;
                        if (pv != null) held.Add(pv.ViewID);
                    }
                    catch (Exception) { }
                }

                var hits = new Collider[32];
                int n = Physics.OverlapSphereNonAlloc(center, 1.05f, hits, ~0, QueryTriggerInteraction.Ignore);
                float bestScore = -999f;
                Collider best = null;
                for (int i = 0; i < n; i++)
                {
                    var col = hits[i];
                    if (col == null) continue;
                    var grabbable = col.GetComponentInParent<IGrabbable>();
                    if (grabbable == null) continue;
                    try { if (!grabbable.CanGrab(player)) continue; } catch (Exception) { }
                    var pv = grabbable.photonView;
                    if (pv == null || held.Contains(pv.ViewID)) continue;
                    // Exclude the grabber's own owner body hierarchy.
                    try { if (col.transform.root == player.transform.root) continue; } catch (Exception) { }
                    Vector3 to = col.transform.position - center;
                    float score = Vector3.Dot(to.normalized, fwd) / Mathf.Max(0.3f, to.magnitude);
                    if (score > bestScore) { bestScore = score; best = col; }
                }
                if (best == null) return false; // AI body: nothing grabbed, no fallthrough

                var bestGrabbable = best.GetComponentInParent<IGrabbable>();
                if (bestGrabbable == null) return false;
                var bestPv = bestGrabbable.photonView;
                if (bestPv == null || !bestPv) return false;
                float spring = c.FSpring != null ? Convert.ToSingle(c.FSpring.GetValue(__instance)) : 200f;
                float damping = c.FDamp != null ? Convert.ToSingle(c.FDamp.GetValue(__instance)) : 30f;
                var grabInfoType = grabberInnerType();
                if (grabInfoType == null) return true; // reflect failed — vanilla tries
                var info = AccessTools.Constructor(grabInfoType, new[] { typeof(Kobold), typeof(IGrabbable), typeof(float), typeof(float), typeof(Vector3), typeof(Quaternion), typeof(Vector3) })
                    ?.Invoke(new object[] { player, bestGrabbable, spring, damping, center, rot, offset });
                if (info == null) return true;
                var valid = AccessTools.Method(grabInfoType, "Valid");
                if (valid != null && valid.Invoke(info, null) is bool okVal && !okVal) return false;
                try { grabs.Add(info); } catch (Exception) { return true; }
                try { bestPv.RPC("OnGrabRPC", Photon.Pun.RpcTarget.All, __instance.photonView.ViewID); } catch (Exception) { }
                // RemoveGivebackKobold equivalent: not mod-visible — harmless to skip.
            }
            catch (Exception e)
            {
                // The pickup must NEVER crash the game: fall back to the original.
                try { LLMNPCPlugin.Log?.LogWarning(LoggerTag + " trygrab: " + e.Message + " — vanilla TryGrab takes over"); } catch (Exception) { }
                return true;
            }
            return false; // AI body handled
        }

        private static Photon.Pun.PhotonView GetGrabView(object grabInfo)
        {
            try
            {
                var prop = grabInfo.GetType().GetProperty("grabbable");
                var g = prop != null ? prop.GetGetMethod(true)?.Invoke(grabInfo, null) as IGrabbable : null;
                return g != null ? g.photonView : null;
            }
            catch (Exception) { return null; }
        }
    }

    // ------------------------------------------------------------------
    // ModGrabModule — `deliver <place>`: carry the held item to a context target
    // (farm plot / blender / station / another player) and release it THERE.
    // The behavior the user asked for: walk it to the location, release on the
    // context target — simulated player carrying.
    // ------------------------------------------------------------------
    internal static class ModGrabModule
    {
        private static bool _registered;
        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            ModuleRegistry.Tool("deliver", (n, p) => n.ToolDeliver(p), new Dictionary<string, Dictionary<string, object>>
            {
                ["target"] = new Dictionary<string, object>
                {
                    ["type"] = new object[] { "string", "null" },
                    ["description"] = "deliver: where to release the held item — a place kind (soil/blender/station name), a place name, another player's name, or id:N from nearby"
                },
            });
        }
    }

    // ------------------------------------------------------------------
    // NPCInstance additions: the eye state the patches consume.
    // ------------------------------------------------------------------
    internal partial class NPCInstance
    {
        // The agent's own look state as a world rotation — the values every mod-side
        // grab/carry computation uses INSTEAD of the player-camera static.
        internal Quaternion EyeRotation()
        {
            float yaw; float pitch;
            lock (_stateLock)
            {
                yaw = _yawDeg; pitch = _pitchDeg;
            }
            return Quaternion.Euler(pitch, yaw, 0f);
        }

        // deliver <target> — carry what's held to a context target and drop it there.
        // The route re-uses the go_to machinery (world map A* → waypoint steering),
        // polls arrival with a hard cap, then aims down and drops AT the target.
        internal object ToolDeliver(JsonObj p)
        {
            if (!IsAlive(_kobold)) return new { ok = false, reason = "no_body" };
            var held = HeldScan();
            if (held == null || held.Count == 0)
                return new { ok = false, reason = "hands_empty",
                    note = "nothing is carried — get <item> first (it aims for you), then deliver <place>" };

            // 1) resolve the target: id → registry; soil-word → nearest plot;
            // else place name via the shared lookup, else stations/players.
            string hint = (p.S("name", p.S("target", "")) ?? "").Trim();
            double idv = -1; try { idv = p.DB("id", -1); } catch (Exception) { }
            Transform targetTf = null;
            string targetWord = null;
            try
            {
                if (idv >= 0)
                {
                    var tf = FindTargetById((int)idv);
                    if (tf != null) { targetTf = tf; targetWord = CleanName(tf.name); }
                }
                else
                {
                    string h = hint.ToLowerInvariant();
                    bool soilish = h.Contains("soil") || h.Contains("dirt") || h.Contains("plot") || h.Contains("farm");
                    if (soilish)
                    {
                        float d;
                        var st = NearestSoilTile(out d);
                        if (st != null) { targetTf = st.transform; targetWord = "the farm plot"; }
                    }
                }
                if (targetTf == null && hint.Length > 0)
                {
                    // A NAME match gives a POSITION (not a transform) — store it.
                    var place = FindPlaceByName(hint);
                    if (place.HasValue) { _deliveredTargetPos = place.Value; targetWord = hint; }
                }
            }
            catch (Exception) { }
            if (targetTf == null && _deliveredTargetPos == Vector3.zero)
                return new { ok = false, reason = "unknown_target",
                    note = "deliver where? a farm plot (soil/dirt), a blender, a station name, another player's name, or id:N from `ls`" };

            // 2) plan the route (same shapes ToolGoTo stores).
            Vector3 arrive = targetTf != null ? targetTf.position : _deliveredTargetPos;
            object pathObj = null;
            try { pathObj = RunOnMainThread(() => WorldMap.Ready ? (object)WorldMap.FindPathSmoothed(_kobold.transform.position, arrive) : null, 20000); }
            catch (Exception) { }
            var path = pathObj as List<Vector3>;
            if (path == null || path.Count < 2)
            {
                try { pathObj = RunOnMainThread(() => (object)FindPath(_kobold.transform.position, arrive), 12000); } catch (Exception) { }
                path = pathObj as List<Vector3>;
            }
            if (path == null || path.Count < 2)
                return new { ok = false, reason = "no_route", note = "no route found to '" + (targetWord ?? hint) + "' — get closer and deliver again" };
            float pathLen = 0f;
            for (int i = 1; i < path.Count; i++) pathLen += Vector3.Distance(path[i - 1], path[i]);
            lock (_stateLock)
            {
                _path = path;
                _pathIdx = 0;
                _navTarget = path[0];
                _pathGoal = new Vector3(arrive.x, 0f, arrive.z);
                _pathGoalSet = true;
                _trailJumps = null;
            }
            SetMove(1f, 0f, false, 0f, Mathf.Clamp(pathLen / 1.5f, 1f, 30f), false);

            // 3) poll the arrival (hard cap ~40s; nav aborts end the wait).
            float deadline = Time.unscaledTime + 40f;
            while (Time.unscaledTime < deadline && _running && IsAlive(_kobold))
            {
                try { Thread.Sleep(1500); } catch (Exception) { return new { ok = false, reason = "interrupted" }; }
                float d = Vector3.Distance(_kobold != null ? _kobold.transform.position : arrive, arrive);
                if (d <= 1.6f) break;
                // Still steering? fine. Lost the route with no arrival → give up early.
                if (_path == null && _navTarget == null) break;
            }
            float finalD = Vector3.Distance(_kobold != null ? _kobold.transform.position : arrive, arrive);
            // 4) release AT the target: aim down at the spot, drop.
            try
            {
                RunOnMainThread(() => { AimEyeAt(arrive + Vector3.down * 0.3f); return true; }, 5000);
                try { Thread.Sleep(350); } catch (Exception) { }
            }
            catch (Exception) { }
            ToolDrop();
            try { Thread.Sleep(450); } catch (Exception) { }
            var after = HeldScan();
            bool emptyHands = after == null || after.Count == 0;
            return new Dictionary<string, object>
            {
                ["ok"] = emptyHands && finalD <= 2.5f,
                ["where"] = targetWord ?? hint,
                ["dist"] = F(finalD),
                ["dropped"] = emptyHands ? "yes" : "NO (hands still full — check `cat hold`; the drop result says why)",
                ["note"] = emptyHands ? "" : "drop did not take — read drop's reason; deliver again after clearing",
            };
        }

        // The resolved deliver target when a NAME (not a transform) matched.
        private Vector3 _deliveredTargetPos;

        internal Vector3 EyePosition()
        {
            try
            {
                if (_head != null) return _head.position;
            }
            catch (Exception) { }
            try { if (_kobold != null) return _kobold.transform.position + Vector3.up * 0.6f; } catch (Exception) { }
            return Vector3.zero;
        }
    }
}