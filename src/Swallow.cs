// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Swallow — player-initiated kobold vore: release a held PERSON while looking up,
// and instead of dropping them you swallow them whole.
//
// Trigger: Harmony prefix on Grabber.TryDrop. When the LOCAL PLAYER drops while the
// view camera points up (forward.y above a threshold) and is holding a person, the
// drop is canceled and a swallow session starts per victim. Anything else in hand
// still gets released like a normal drop; held PLAYER bodies are refused (grief
// guard) and just drop.
//
//   pull    (~2.2s)  the released victim is pinned, locked (kinematic ragdoll, no
//                    controller), pulled up into the player's mouth and shrunk
//   digest  (config, default 45s) they float at the stomach (belly), pin re-asserted
//                    every tick; a claimed NPC victim's steering is gated off
//                    (NPCInstance.Swallowed) so their LLM session keeps running and
//                    narrating the experience without ever moving the body
//   settle  (~6s)    they compact toward nothing
//   complete        an "Egg" Photon room object spawns at the player's feet carrying
//                    KoboldGenes.Mix(player genes, victim genes) — the game's exact
//                    oviposition recombination — and the victim's Photon object is
//                    destroyed. Destroying the body is the mod's normal teardown:
//                    the NPC's persona/facts/name persist to MemoryStore under the
//                    body's key, and every other NPC sees the swallow as a world
//                    event.
//
// Rebirth: eggs plant in soil like vanilla (Seed → Plant → KoboldSpawnPlant), which
// spawns a body holding those mixed genes. Kobold.spawned fires for that body; the
// mod fingerprints its genes (normalized through the same HalfPrecision quantize
// round-trip the BitBuffer genes path uses, so the numbers match what survived
// serialization) against a registry built at egg-spawn time. On a match the swallowed
// NPC's saved life (name, persona, facts, pronouns, goal) is copied onto the new
// body's key, so the normal claim scan wakes the SAME person — in control again,
// in a new body with changed genes.
//
// Everything here is client-side Photon API that works identically in singleplayer
// (Photon offline mode). Timing/trigger math lives in src/SwallowLogic.cs (unit
// tested); this file is the runtime manager around it.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using Photon.Pun;

namespace KKLLMNPC
{
    // What a consumed kobold becomes at the end of the digestion (config
    // [LittleTreats] SwallowEffect). The swallow/digest experience is identical for
    // every mode; only the completion differs.
    public enum SwallowEffect
    {
        Egg = 0,     // an egg pops out at the player's feet (replantable → rebirth)
        BellyEgg = 1, // 'Egg' reagent + the victim's genes ride in the player's belly;
                     // sit on a nest to lay (vanilla oviposition flow) — the person is in THAT egg
        Grow = 2,    // no egg — the player absorbs them: bigger, stronger and/or curvier
        Destroy = 3, // no egg — they are simply gone
    }

    // ------------------------------------------------------------------
    // Runtime manager: the TryDrop interception, the swallow sessions and
    // the egg→rebirth registry. All main-thread (Update tick + Harmony
    // prefix + the game's spawned event, which fires during instantiate).
    // ------------------------------------------------------------------
    internal static class SwallowManager
    {
        // The game's Egg prefab photon name (NetworkedPrefabs/Egg.prefab).
        internal const string EggPhotonName = "Egg";

        internal static LLMNPCPlugin Plugin;

        private static readonly object _lock = new object();
        private static readonly List<SwallowState> _active = new List<SwallowState>();

        // Rebirth registry: mixed-genes fingerprint → the eaten body's MemoryStore
        // key (plus the stomach owner's name for the reborn one's narration — with
        // force-feeding that's not always the player).
        private sealed class RebirthLink
        {
            public string OldKey;
            public string EaterLabel;
            public bool IsPlayer;      // the eaten one is the LOCAL PLAYER — hatch revives
                                       // THEM (spectate flow) instead of restoring an NPC persona
        }

        private static readonly Dictionary<string, RebirthLink> _awaitingRebirth = new Dictionary<string, RebirthLink>();
        private static readonly Dictionary<string, string> _rebirthNames = new Dictionary<string, string>();
        // BellyEgg mode: fingerprint of the genes we SetGenes'd into the eater's
        // belly → old body key. The nest-lay event (OvipositionSpot.oviposition)
        // matches on these, then registers the LAID egg's seed genes for rebirth —
        // the lay-time Mix(player, belly) result can't be predicted here because
        // KoboldGenes.Mix rolls randoms.
        private static readonly Dictionary<string, RebirthLink> _bellyGeneticOwners = new Dictionary<string, RebirthLink>();
        private static readonly Dictionary<string, string> _bellyNames = new Dictionary<string, string>();
        private static bool _spawnHooked;
        private static bool _ovipositionHooked;

        private sealed class SwallowState
        {
            public Kobold Victim;
            public Kobold Eater;
            public Kobold Actor;           // who initiated: eater (self-eat) or the force-feeding dropper
            public int VictimId;
            public NPCInstance Npc;        // null for unclaimed (vanilla AI) victims
            public NPCInstance EaterNpc;   // the eater's own mind (null: player eater / unclaimed body)
            public string Name;            // narration/display name
            public string OldBodyKey;      // MemoryStore key of the eaten body
            public bool VictimIsPlayer;    // the LOCAL PLAYER is inside — spectate takeover runs;
                                           // consume keeps the body parked (never destroyed)
            public float StartUnscaled;
            public float DigestSeconds;
            public Vector3 PullStartPos;
            public int Milestone;          // last narrated digest mark
            public float LastOwnerAssert;
            public float LastColliderAssert;
            public float LastStopAnim;
            public bool Hidden;            // renderers off (set once fully swallowed)
            public bool EggSpawned;
        }

        internal static void Ensure(LLMNPCPlugin plugin)
        {
            Plugin = plugin;
            if (!_spawnHooked)
            {
                try { Kobold.spawned += OnKoboldSpawned; _spawnHooked = true; }
                catch (Exception e) { try { plugin.Logger.LogWarning("swallow spawn hook: " + e.Message); } catch (Exception) { } }
            }
            if (!_ovipositionHooked)
            {
                // BellyEgg mode's relay: nest-lays by the player are matched against the
                // genes we poured into their belly, and the laid egg is registered for
                // the plant-hatch rebirth match.
                try { OvipositionSpot.oviposition += OnOviposition; _ovipositionHooked = true; }
                catch (Exception e) { try { plugin.Logger.LogWarning("swallow oviposition hook: " + e.Message); } catch (Exception) { } }
            }
        }

        // One-line live state of who is digesting inside `eater` — the EATER's own
        // awareness between events: it lands in the state anchors (console anchor
        // line next to the sex line; act perception as a "belly" key) so every
        // turn reminds the body of the squirming guest and the pending outcome.
        // Reads only labels/times captured at swallow start (no scene components),
        // like the rest of the narration. Null when nobody is inside.
        internal static string BellyGuestText(Kobold eater)
        {
            if (eater == null) return null;
            var parts = new List<string>();
            float now = -1f, total = -1f;
            lock (_lock)
                foreach (var st in _active)
                {
                    if (st.EggSpawned || st.Eater != eater) continue;
                    if (total < 0f)
                    {
                        total = 0f;
                        try { now = Time.unscaledTime; } catch (Exception) { }
                    }
                    float left = Mathf.Max(0f, SwallowLogic.PullSeconds + st.DigestSeconds + SwallowLogic.SettleSeconds - (now - st.StartUnscaled));
                    string fate;
                    switch (Effect())
                    {
                        case SwallowEffect.BellyEgg: fate = "they become egg reagent + genes in you; a nest lay brings them back"; break;
                        case SwallowEffect.Grow: fate = "their body (size, curves, groin) and memories absorb into you"; break;
                        case SwallowEffect.Destroy: fate = "nothing will be left of them"; break;
                        default: fate = "an egg carrying their mixed genes pops out of you"; break;
                    }
                    parts.Add(st.Name + " (in your stomach, ~" + Mathf.CeilToInt(left).ToString("0", CultureInfo.InvariantCulture)
                        + "s to conversion — " + fate + ")");
                }
            if (parts.Count == 0) return null;
            return "you have someone INSIDE YOU: " + string.Join(", ", parts.ToArray());
        }

        internal static SwallowEffect Effect()
        {
            var cfg = Plugin != null ? Plugin._cfgSwallowEffect : null;
            if (cfg == null) return SwallowEffect.Egg;
            return cfg.Value;
        }

        internal static float GrowPercentFraction()
        {
            var cfg = Plugin != null ? Plugin._cfgSwallowGrowPercent : null;
            float pct = 10f;
            try { if (cfg != null) pct = cfg.Value; } catch (Exception) { }
            if (pct < 0f) pct = 0f;
            if (pct > 100f) pct = 100f;
            return pct / 100f;
        }

        internal static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            var tryDrop = typeof(Grabber).GetMethod("TryDrop", BindingFlags.Instance | BindingFlags.Public);
            if (tryDrop == null)
            {
                if (Plugin != null) Plugin.Logger.LogWarning("swallow: TryDrop not found — trigger disabled");
                return;
            }
            harmony.Patch(tryDrop,
                prefix: new HarmonyLib.HarmonyMethod(typeof(SwallowManager).GetMethod(
                    nameof(TryDropPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            if (Plugin != null) Plugin.Logger.LogInfo("swallow: TryDrop trigger patched (drop while looking up).");
        }

        // ------------------------------------------------------------------
        // Trigger: returns TRUE to let the normal drop run, FALSE when the
        // release gesture was consumed by a swallow (or several, one per held
        // person). Two gestures, in precedence order:
        //   1. Self-eat (v1): the PLAYER's drop while steeply looking up —
        //      everything held that is a person goes inside the player.
        //   2. Force-feeding: ANY kobold's drop whose held person sits right on
        //      another kobold's HEAD makes the receiver swallow them.
        // Everything else in hand still gets released, like a drop.
        // ------------------------------------------------------------------
        private static bool TryDropPrefix(Grabber __instance)
        {
            try
            {
                if (Plugin == null) return true;
                var cfgEnabled = Plugin._cfgSwallowEnabled;
                if (cfgEnabled == null || !cfgEnabled.Value) return true;

                Kobold dropper = null;
                try { dropper = __instance.GetComponentInParent<Kobold>(); } catch (Exception) { }
                if (dropper == null) return true;

                var held = NPCInstance.HeldScanFor(dropper);
                var persons = new List<Kobold>();
                foreach (var h in held)
                {
                    if (h == null || h.Victim == null || h.IsPlayerVictim) continue;
                    if (h.Victim == dropper) continue;
                    persons.Add(h.Victim);
                }
                if (persons.Count == 0) return true;

                // Gesture 1: self-eat — player-only, requires the steep look-up.
                if (NPCInstance.IsPlayerKobold(dropper)
                    && SwallowLogic.IsLookUp(CameraForwardY(), LookUpDot()))
                {
                    bool started = false;
                    foreach (var victim in persons)
                        if (Begin(dropper, victim, dropper)) started = true;
                    if (!started) return true;
                    ReleaseAllFromGrabber(__instance, persons);
                    return false;
                }

                // Gesture 2: force-feeding — any dropper, no look-up needed; the
                // held person must be right on a receiver's head.
                if (!ForceFeeding()) return true;
                var accepted = new List<Kobold>();
                foreach (var victim in persons)
                {
                    var receiver = FindShoveEater(dropper, victim);
                    if (receiver != null && Begin(receiver, victim, dropper)) accepted.Add(victim);
                }
                if (accepted.Count == 0) return true;
                ReleaseAllFromGrabber(__instance, accepted);
                return false;
            }
            catch (Exception e)
            {
                try { LLMNPCPlugin.Log.LogWarning("swallow trigger: " + e.Message); } catch (Exception) { }
                return true; // never break the game's own drop
            }
        }

        private static bool ForceFeeding()
        {
            var cfg = Plugin != null ? Plugin._cfgSwallowForceFeeding : null;
            return cfg != null && cfg.Value;
        }

        private static float CameraForwardY()
        {
            try
            {
                var cam = OrbitCamera.GetCamera();
                if (cam != null && cam.transform != null) return cam.transform.forward.y;
            }
            catch (Exception) { }
            return 0f;
        }

        // Force-feed placement: the held person is right on/at a receiver's HEAD —
        // nearest alive non-player kobold that is neither the dropper nor busy.
        internal const float HeadShoveRadius = 0.85f;

        private static Kobold FindShoveEater(Kobold dropper, Kobold victim)
        {
            Kobold best = null;
            float bestDist = HeadShoveRadius;
            try
            {
                Vector3 vPos = victim.transform.position;
                foreach (var k in UnityEngine.Object.FindObjectsOfType<Kobold>())
                {
                    if (k == null || !SafeAlive(k) || k == dropper || k == victim) continue;
                    if (NPCInstance.IsPlayerKobold(k)) continue;  // grief guard: receivers can't be players
                    try { if (k.grabbed) continue; } catch (Exception) { } // being held themselves
                    if (EaterBusy(k)) continue;                   // already digesting someone
                    Vector3 headPos = HeadOf(k);
                    if (!SwallowLogic.IsOnOrAboveHead(vPos.y, headPos.y)) continue;
                    float d = Vector3.Distance(headPos, vPos);
                    if (d >= bestDist) continue;
                    bestDist = d;
                    best = k;
                }
            }
            catch (Exception) { return null; }
            return best;
        }

        // internal for the spectate manager (its Tick parks the player at the belly).
        internal static bool EaterBusy(Kobold k)
        {
            try
            {
                lock (_lock)
                    foreach (var s in _active)
                        if (s.Eater == k) return true;
            }
            catch (Exception) { }
            return false;
        }

        // internal for the spectate manager (follow-camera aims at the head).
        internal static Vector3 HeadOf(Kobold k)
        {
            try
            {
                var desc = k.GetComponent<CharacterDescriptor>();
                var anim = desc != null ? desc.GetDisplayAnimator() : null;
                var head = anim != null ? anim.GetBoneTransform(HumanBodyBones.Head) : null;
                if (head != null) return head.position;
            }
            catch (Exception) { }
            return k.transform.position + Vector3.up * 1.0f;
        }

        // internal for the spectate manager: where a parked player body rides.
        internal static Vector3 BellyOf(Kobold eater)
        {
            return eater.transform.position + Vector3.up * 0.35f;
        }

        // Alive-or-destroyed check (Unity's == null covers both).
        private static bool SafeAlive(Kobold k)
        {
            try { return k != null; } catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------
        // Spectate (agent eats the PLAYER) helpers, shared with SpectateManager.
        // ------------------------------------------------------------------

        // Full body restore shared by the abort release and the spectate
        // hatch-revive: undo everything the pin/park changed. at = set-down spot.
        internal static void RestoreVictimBody(Kobold v, Vector3 at)
        {
            if (!SafeAlive(v)) return;
            try
            {
                var pv = v.photonView;
                try { if (pv != null && !pv.IsMine) pv.RequestOwnership(); } catch (Exception) { }

                v.transform.localScale = Vector3.one;
                v.transform.position = at;

                var ragdoller = v.GetComponent<Ragdoller>();
                if (ragdoller != null)
                {
                    ragdoller.SetLocked(false);
                    if (ragdoller.ragdolled)
                    {
                        try { ragdoller.photonView.RPC("PopRagdoll", RpcTarget.All); } catch (Exception) { }
                    }
                    foreach (var b in ragdoller.GetRagdollBodies())
                        if (b != null) b.isKinematic = false;
                }
                var root = v.body;
                if (root != null) { root.isKinematic = false; root.velocity = Vector3.zero; }
                var controller = v.GetComponent<KoboldCharacterController>();
                if (controller != null) controller.enabled = true;
                foreach (var c in v.GetComponentsInChildren<Collider>(true))
                    if (c != null && !c.isTrigger) c.enabled = true;
                foreach (var rend in v.GetComponentsInChildren<Renderer>(true))
                {
                    if (rend == null) continue;
                    rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    rend.enabled = true;
                }
            }
            catch (Exception e)
            {
                try { LLMNPCPlugin.Log.LogWarning("swallow body restore: " + e.Message); } catch (Exception) { }
            }
        }

        // Let go of ONE held victim without releasing anything else the grabber
        // holds (tools stay in hand): velocity pre-bumped so the release takes
        // the game's throw path, released entry removed from the grabbed list.
        private static void ReleaseHeldVictim(Kobold eater, Kobold victim)
        {
            if (eater == null || victim == null) return;
            try
            {
                foreach (var grabber in eater.GetComponentsInChildren<Grabber>(true))
                {
                    try
                    {
                        var raw = NPCInstance.GetField<object>(grabber, "grabbedObjects") as IList;
                        if (raw == null) continue;
                        for (int i = raw.Count - 1; i >= 0; i--)
                        {
                            try
                            {
                                var kob = NPCInstance.GetProp(raw[i], "kobold") as Kobold;
                                if (kob != victim) continue;
                                var rb = NPCInstance.GetProp(raw[i], "body") as Rigidbody
                                    ?? NPCInstance.GetField<object>(raw[i], "body") as Rigidbody;
                                if (rb != null) rb.velocity = Vector3.up * 5f;
                                var ragdoller = victim.GetComponent<Ragdoller>();
                                if (ragdoller != null)
                                    foreach (var b in ragdoller.GetRagdollBodies())
                                        if (b != null) b.velocity = Vector3.up * 5f;
                                var rel = raw[i].GetType().GetMethod("Release", BindingFlags.Instance | BindingFlags.Public);
                                rel?.Invoke(raw[i], null);
                                raw.RemoveAt(i);
                            }
                            catch (Exception) { }
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception e)
            {
                try { LLMNPCPlugin.Log.LogWarning("swallow release-one: " + e.Message); } catch (Exception) { }
            }
        }

        // The act entry: the agent's mind decides to swallow the LOCAL PLAYER.
        // Full gate (the tool's own gate already ran — this catches any other
        // caller). True = a swallow session + spectate takeover are live.
        internal static bool TryEatPlayer(NPCInstance eaterNpc)
        {
            if (Plugin == null || eaterNpc == null) return false;
            var cfg = Plugin._cfgSwallowAgentEat;
            if (cfg == null || !cfg.Value) return false;
            Kobold eater = null;
            try { eater = eaterNpc.KoboldObj as Kobold; } catch (Exception) { }
            if (eater == null) return false;
            Kobold player = null;
            try { if (PlayerPossession.TryGetPlayerInstance(out var pp)) player = pp.kobold; } catch (Exception) { }
            if (player == null || eater == player) return false;
            if (EaterBusy(eater)) return false;
            lock (_lock)
                foreach (var s in _active)
                    if (s.Victim == player) return false;
            try { if (SpectateManager.IsSpectating(player)) return false; } catch (Exception) { }
            return Begin(eater, player, eater, true);
        }

        internal static float AgentEatRange()
        {
            var cfg = Plugin != null ? Plugin._cfgSwallowEatRange : null;
            float v = SpectateLogic.DefaultEatRange;
            try { if (cfg != null) v = cfg.Value; } catch (Exception) { }
            if (v < 1f) v = 1f;
            if (v > 8f) v = 8f;
            return v;
        }

        // What the egg modes promise for a swallowed PLAYER (null = no hatch).
        private static string PlayerFateHint()
        {
            switch (Effect())
            {
                case SwallowEffect.BellyEgg: return "the egg reagent around them waits in your belly — nest-lay them and plant the egg";
                case SwallowEffect.Grow:
                case SwallowEffect.Destroy: return null;
                default: return "after conversion you lay an egg — plant it in soil and they hatch out of it";
            }
        }

        // The LLM-facing outcome summary for a successful eat_player result.
        internal static string PlayerOutcomeNote()
        {
            switch (Effect())
            {
                case SwallowEffect.BellyEgg: return "the conversion turns them into egg reagent + genes in YOUR belly — nest-lay them, then plant the egg to hatch them out (a nest lay needs energy >1 as well as the egg — eat before sitting if you're low)";
                case SwallowEffect.Grow: return "the conversion absorbs them into your body — their genes (size, strength, curves) and memories pour into yours (no egg, no hatch — their escape is the swallow-abort chat command)";
                case SwallowEffect.Destroy: return "the conversion consumes them entirely (no egg, no hatch — their escape is the swallow-abort chat command)";
                default: return "after conversion an egg pops out at your feet — plant it in soil and they hatch out of it";
            }
        }

        // Take one held victim. Main-thread only (Harmony prefix). False = don't.
        // actor = whoever swallowed them into `eater`: the eater themself (self-eat,
        // the player's look-up gesture) or a different kobold force-feeding.
        // playerVictim = the LOCAL PLAYER is the one being swallowed: the spectate
        // takeover starts here and the consume phase parks their body instead of
        // destroying it (SpectateManager owns the camera/park from this moment on).
        private static bool Begin(Kobold eater, Kobold victim, Kobold actor, bool playerVictim = false)
        {
            if (actor == null) actor = eater;
            int vid;
            try { vid = victim.GetInstanceID(); } catch (Exception) { return false; }
            if (victim.photonView == null) return false;

            lock (_lock)
            {
                foreach (var s in _active) if (s.VictimId == vid) return false;
            }

            var npc = Plugin.InstanceForBody(victim);

            // Deterministic ownership: take the victim so the releases/destroys land.
            try
            {
                var pv = victim.photonView;
                if (!pv.IsMine) pv.TransferOwnership(PhotonNetwork.LocalPlayer);
                if (!pv.IsMine) pv.RequestOwnership();
            }
            catch (Exception e) { LLMNPCPlugin.Log.LogWarning("swallow ownership: " + e.Message); }

            string name;
            if (playerVictim) name = "the player";
            else name = npc != null ? npc.GetMyName() : NPCInstance.CleanName(victim.name);
            string oldKey = null;
            if (!playerVictim)
                try { oldKey = NPCInstance.BodyKey(victim); } catch (Exception) { oldKey = null; }
            string who = BodyLabel(actor);
            string eaterLabel = BodyLabel(eater);

            // Leave any station animation quietly — the station would fight our pin.
            try { victim.photonView.RPC("StopAnimationRPC", RpcTarget.All); } catch (Exception) { }

            var st = new SwallowState
            {
                Victim = victim,
                Eater = eater,
                Actor = actor,
                VictimId = vid,
                Npc = npc,
                Name = name ?? "the kobold",
                OldBodyKey = oldKey,
                VictimIsPlayer = playerVictim,
                StartUnscaled = Time.unscaledTime,
                DigestSeconds = DigestSeconds(),
                PullStartPos = victim.transform.position,
                Milestone = -1,
            };
            lock (_lock) _active.Add(st);
            try { st.EaterNpc = Plugin.InstanceForBody(eater); } catch (Exception) { st.EaterNpc = null; }

            // Player victims: if the eater was HOLDING them, let go first — the
            // grabber's per-frame anchor would otherwise fight the pull-in pin.
            if (playerVictim)
                ReleaseHeldVictim(eater, victim);

            if (npc != null)
            {
                try
                {
                    npc.Swallowed = true;
                    npc.PushWorldEvent(BeginPromiseText(who));
                    npc.PersistLifeForSwallow(); // snapshot BEFORE any end-of-digest destroy
                }
                catch (Exception) { }
            }

            bool selfEat = actor == eater;
            if (playerVictim)
            {
                // The eater gets one honest first-person line; the world gets the
                // spectator line; and the PLAYER'S camera is taken over right now —
                // the pull-in starts this frame and they watch from outside.
                if (st.EaterNpc != null)
                {
                    try
                    {
                        st.EaterNpc.PushWorldEvent("you just swallowed the PLAYER WHOLE — the player is inside you now: their camera is gone, they WATCH everything through your body's eyes and CANNOT act (about "
                            + Math.Ceiling(st.DigestSeconds + SwallowLogic.PullSeconds + SwallowLogic.SettleSeconds).ToString("0", CultureInfo.InvariantCulture)
                            + "s to conversion). " + PlayerFateHint() + " Keep living your ordinary life — go to work, play, talk — and speak to them whenever; they hear you. They are a person in your care: never sell, abandon or hurt them.");
                    }
                    catch (Exception) { }
                }
                try { Plugin.FanoutWorldEventExcept("you watch " + eaterLabel + " swallow the PLAYER WHOLE — the player is inside them now, watching through their body's eyes.", st.EaterNpc, null); } catch (Exception) { }
                // (the shared tail below bubbles the eater's "You swallow …" line)
                SpectateManager.BeginForPlayer(victim, eater, eaterLabel, PlayerFateHint());
            }
            else if (selfEat)
            {
                // The EATER (when the mod drives that body) gets a DIRECT first-person
                // line — the generic spectator fanout would reach them as "you watch
                // yourself swallow…", which confuses exactly the body that ate.
                if (st.EaterNpc != null)
                {
                    try
                    {
                        st.EaterNpc.PushWorldEvent("you just swallowed " + st.Name
                            + " WHOLE — they are inside you now, digesting (about "
                            + Math.Ceiling(st.DigestSeconds + SwallowLogic.PullSeconds + SwallowLogic.SettleSeconds).ToString("0", CultureInfo.InvariantCulture)
                            + "s to conversion). Their fate follows this stomach's rules; you can speak while they convert.");
                    }
                    catch (Exception) { }
                }
                string inside = who == "the player" ? "them" : who + "'s body";
                try { Plugin.FanoutWorldEventExcept("you watch " + who + " swallow " + st.Name + " WHOLE — they disappeared inside " + inside, st.EaterNpc, npc); }
                catch (Exception) { }
            }
            else
            {
                try { Plugin.FanoutWorldEventExcept("you watch " + who + " shove " + st.Name + " into " + eaterLabel + "'s face — " + eaterLabel + " swallowed them whole!", st.EaterNpc, npc); }
                catch (Exception) { }
                if (st.EaterNpc != null)
                {
                    try
                    {
                        st.EaterNpc.PushWorldEvent("someone force-fed you: " + who + " just dropped " + st.Name + " straight into your mouth and you swallowed them WHOLE — they are in YOUR stomach now, digesting. Their fate follows this stomach's rules; you can speak around them while they convert.");
                    }
                    catch (Exception) { }
                }
            }
            try { Bubble(eater, selfEat ? "You swallow " + st.Name + " whole." : "You swallow " + st.Name + " whole — " + who + " dropped them right in."); } catch (Exception) { }

            try
            {
                LLMNPCPlugin.Log.LogInfo("swallow: '" + st.Name + "' swallowed by " + who
                    + (selfEat ? "" : " — force-fed into " + eaterLabel)
                    + " (digest " + st.DigestSeconds.ToString("0", CultureInfo.InvariantCulture) + "s, body key '" + (st.OldBodyKey ?? "?") + "')");
            }
            catch (Exception) { }
            return true;
        }

        // Human-readable label of a kobold body: "the player", its NPC name, or the
        // mesh name — used in every swallow narration so who-did-what stays legible.
        private static string BodyLabel(Kobold k)
        {
            try { if (k != null && NPCInstance.IsPlayerKobold(k)) return "the player"; } catch (Exception) { }
            var npc = Plugin != null && k != null ? Plugin.InstanceForBody(k) : null;
            if (npc != null)
            {
                try { string n = npc.GetMyName(); if (!string.IsNullOrEmpty(n)) return n; } catch (Exception) { }
            }
            try { return NPCInstance.CleanName(k.name) ?? "someone"; } catch (Exception) { }
            return "someone";
        }

        // What the swallowed one is told happens to them — honest per mode, and
        // naming who did it. Grow and Destroy deliberately do NOT promise a rebirth
        // (there isn't one). Grow names what is actually taken (the gain toggles).
        private static string BeginPromiseText(string who)
        {
            switch (Effect())
            {
                case SwallowEffect.BellyEgg:
                    return who + " swallowed you WHOLE — you are inside their stomach now; you can still speak but you cannot move. You are slowly being converted into an EGG that will ride in their belly — when they sit on a nest and lay you, you will hatch out of it with new genes.";
                case SwallowEffect.Grow:
                {
                    var taken = new List<string>();
                    if (Plugin._cfgSwallowGrowLarger != null && Plugin._cfgSwallowGrowLarger.Value) taken.Add("your size");
                    if (Plugin._cfgSwallowGrowStrength != null && Plugin._cfgSwallowGrowStrength.Value) taken.Add("your strength");
                    if (Plugin._cfgSwallowGrowSex != null && Plugin._cfgSwallowGrowSex.Value) taken.Add("your curves (breasts, groin)");
                    string what = taken.Count > 0
                        ? "They are ABSORBING you — " + JoinEnglish(taken) + " " + (taken.Count == 1 ? "is" : "are") + " becoming theirs."
                        : "They are ABSORBING you — your whole body is becoming theirs.";
                    return who + " swallowed you WHOLE — you are inside their stomach now; you can still speak but you cannot move. " + what + " There is no coming back.";
                }
                case SwallowEffect.Destroy:
                    return who + " swallowed you WHOLE — you are inside their stomach now; you can still speak but you cannot move. They are consuming you entirely. There is no coming back.";
                default:
                    return who + " swallowed you WHOLE — you are inside their stomach now; you can still speak but you cannot move. You feel the walls churn. You are slowly being converted into an EGG — when it's done, you will be reborn from it with new genes.";
            }
        }

        // "a, b and c" for narration strings built from optional parts.
        private static string JoinEnglish(List<string> parts)
        {
            if (parts == null || parts.Count == 0) return "";
            if (parts.Count == 1) return parts[0];
            var head = new string[parts.Count - 1];
            for (int i = 0; i < parts.Count - 1; i++) head[i] = parts[i];
            return string.Join(", ", head) + " and " + parts[parts.Count - 1];
        }

        // Config-steep "looking up" threshold — default is near max-up so a casual
        // drop can never eat someone by accident (playtest request).
        internal static float LookUpDot()
        {
            var cfg = Plugin != null ? Plugin._cfgSwallowLookUpDot : null;
            float v = SwallowLogic.DefaultLookUpDot;
            try { if (cfg != null) v = cfg.Value; } catch (Exception) { }
            if (v < 0.1f) v = 0.1f;
            if (v > 0.999f) v = 0.999f;
            return v;
        }

        private static float DigestSeconds()
        {
            var cfg = Plugin != null ? Plugin._cfgSwallowDigest : null;
            float v = 45f;
            try { if (cfg != null) v = cfg.Value; } catch (Exception) { }
            if (v < 5f) v = 5f;
            if (v > 600f) v = 600f;
            return v;
        }

        // Fire OnReleaseRPC for every held entry of the eater's grabber, giving the
        // person victims a small upward velocity first so the release takes the game's
        // throw path (limp ragdoll) instead of auto-using a nearby station.
        private static void ReleaseAllFromGrabber(Grabber grabber, List<Kobold> swallowVictims)
        {
            try
            {
                var raw = NPCInstance.GetField<object>(grabber, "grabbedObjects") as IList;
                if (raw == null) return;
                // Pre-velocity the person victims' root + ragdoll bodies (GrabInfo.Release
                // passes root velocity to OnReleaseRPC; >3 m/s = ThrowRoutine ragdoll,
                // NOT the dropped-on-a-station auto-use path).
                for (int i = 0; i < raw.Count; i++)
                {
                    try
                    {
                        var kob = NPCInstance.GetProp(raw[i], "kobold") as Kobold;
                        if (kob == null || !swallowVictims.Contains(kob)) continue;
                        var rb = NPCInstance.GetProp(raw[i], "body") as Rigidbody
                            ?? NPCInstance.GetField<object>(raw[i], "body") as Rigidbody;
                        if (rb != null) rb.velocity = Vector3.up * 5f;
                        var ragdoller = kob.GetComponent<Ragdoller>();
                        if (ragdoller != null)
                            foreach (var b in ragdoller.GetRagdollBodies())
                                if (b != null) b.velocity = Vector3.up * 5f;
                    }
                    catch (Exception) { }
                }
                for (int i = 0; i < raw.Count; i++)
                {
                    try
                    {
                        var rel = raw[i].GetType().GetMethod("Release", BindingFlags.Instance | BindingFlags.Public);
                        rel?.Invoke(raw[i], null);
                    }
                    catch (Exception) { }
                }
                raw.Clear();
            }
            catch (Exception e) { LLMNPCPlugin.Log.LogWarning("swallow release: " + e.Message); }
        }

        // ------------------------------------------------------------------
        // Per-frame pin + phase advance. Runs on the main thread from Update
        // (like WorldMap/PlayerTrail ticks).
        // ------------------------------------------------------------------
        internal static void Tick()
        {
            if (Plugin == null) return;
            lock (_lock)
            {
                for (int i = _active.Count - 1; i >= 0; i--)
                {
                    var st = _active[i];
                    try { Advance(st, i); }
                    catch (Exception e)
                    {
                        try { LLMNPCPlugin.Log.LogWarning("swallow tick (" + st.Name + "): " + e.Message); } catch (Exception) { }
                    }
                }
            }
        }

        private static void Advance(SwallowState st, int index)
        {
            if (st.EggSpawned) return; // completed this tick; removed below
            if (!SafeAlive(st.Victim) || st.Victim.photonView == null)
            {
                // The victim was destroyed out from under us. End the session without
                // an egg; the NPC instance tears down through its BodyLost path.
                LLMNPCPlugin.Log.LogWarning("swallow: victim '" + st.Name + "' vanished mid-swallow — no egg laid");
                try { if (st.Npc != null) st.Npc.PushWorldEvent("the swallow was interrupted — you were pulled out of the world entirely, not reborn from an egg."); } catch (Exception) { }
                try { if (st.EaterNpc != null) st.EaterNpc.PushWorldEvent("the swallow was interrupted: " + st.Name + " was pulled out of the world from inside you — nothing of them was converted."); } catch (Exception) { }
                _active.RemoveAt(index);
                return;
            }

            float t = Time.unscaledTime - st.StartUnscaled;
            int phase = SwallowLogic.PhaseFor(SwallowLogic.PullSeconds, st.DigestSeconds, SwallowLogic.SettleSeconds, t);
            float phaseT = SwallowLogic.PhaseT(SwallowLogic.PullSeconds, st.DigestSeconds, SwallowLogic.SettleSeconds, t);

            PinVictim(st);
            HideOnceSwallowed(st, phase);

            if (phase == 0)
            {
                Vector3 mouth = MouthOf(st.Eater);
                float k = SwallowLogic.Smooth01(phaseT);
                st.Victim.transform.position = Vector3.Lerp(st.PullStartPos, mouth, k);
            }
            else
            {
                st.Victim.transform.position = BellyOf(st.Eater);
            }
            float scale = SwallowLogic.ScaleFor(phase, phaseT);
            st.Victim.transform.localScale = new Vector3(scale, scale, scale);

            if (phase == 1)
            {
                int mark = SwallowLogic.MilestoneFor(phaseT, st.Milestone);
                if (mark > st.Milestone)
                {
                    st.Milestone = mark;
                    if (st.Npc != null)
                    {
                        try
                        {
                            if (mark == SwallowLogic.MarkChurn)
                                st.Npc.PushWorldEvent("the stomach walls churn and press around you — warm, rhythmic, patient");
                            else if (mark == SwallowLogic.MarkKnead)
                                st.Npc.PushWorldEvent("the walls knead you steadily — your shape is starting to change, limbs going soft and smooth");
                            else if (mark == SwallowLogic.MarkEggForming)
                            {
                                SwallowEffect mode = Effect();
                                if (mode == SwallowEffect.Grow)
                                    st.Npc.PushWorldEvent("you are fading — " + BodyLabel(st.Eater) + " body is absorbing you completely. Almost over.");
                                else if (mode == SwallowEffect.Destroy)
                                    st.Npc.PushWorldEvent("you are fading fast — almost gone. There is nothing being made from you.");
                                else
                                    st.Npc.PushWorldEvent("you are compacting, reshaping — you are becoming an EGG. It won't be long now.");
                            }
                        }
                        catch (Exception) { }
                    }
                    // The EATER feels it too — one line per mark, so they stay aware of
                    // who is inside them and how close the conversion is. Decoupled
                    // from the victim's NPC (a vanilla victim must not stall the marks).
                    if (st.EaterNpc != null)
                    {
                        float leftS = Mathf.Max(0f, SwallowLogic.PullSeconds + st.DigestSeconds + SwallowLogic.SettleSeconds - (Time.unscaledTime - st.StartUnscaled));
                        string feeling = mark == SwallowLogic.MarkChurn
                            ? "your stomach churns around " + st.Name + " — they are in there, alive"
                            : "your stomach kneads " + st.Name + " steadily — the conversion is underway";
                        try { st.EaterNpc.PushWorldEvent(feeling + " (about " + Mathf.CeilToInt(leftS).ToString("0", CultureInfo.InvariantCulture) + "s left; their fate follows this stomach's rules)"); }
                        catch (Exception) { }
                    }
                }
            }

            if (phase >= 0) return;

            // Done converting: apply the configured effect, then remove the body.
            if (Consume(st))
            {
                st.EggSpawned = true; // "handled" — remove the session
                _active.RemoveAt(index);
            }
            // else: this tick's completion failed — keep the victim pinned and retry.
        }

        // The pull shows the victim sliding in; from the digest on they're fully
        // swallowed and hidden — the playtest found the shrunk body clipping
        // stick-out-of-the-stomach visuals otherwise (their meshes don't scale
        // cleanly with the root). Their speech stays functional in the chat
        // channel; only the in-world body disappears. No restore — the completion
        // destroys the body anyway.
        private static void HideOnceSwallowed(SwallowState st, int phase)
        {
            if (st.Hidden || phase < 1) return;
            st.Hidden = true;
            try
            {
                foreach (var r in st.Victim.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.enabled = false;
                }
            }
            catch (Exception) { }
        }

        // Freeze + park the body: kinematic ragdoll, no controller, no colliders
        // (so the eaten blob can't shove the eater's capsule), ownership kept.
        private static void PinVictim(SwallowState st)
        {
            var victim = st.Victim;
            try
            {
                var pv = victim.photonView;
                if (pv != null && !pv.IsMine && Time.unscaledTime - st.LastOwnerAssert > 3f)
                {
                    st.LastOwnerAssert = Time.unscaledTime;
                    pv.TransferOwnership(PhotonNetwork.LocalPlayer);
                    pv.RequestOwnership();
                }
            }
            catch (Exception) { }

            try
            {
                var controller = victim.GetComponent<KoboldCharacterController>();
                if (controller != null && controller.enabled) controller.enabled = false;
                // Root body too — it isn't in the ragdoll list, and a free (non-kinematic)
                // root would fall with gravity between pins and fight the transform lock
                // (the game's own station animations pin it exactly this way).
                try
                {
                    var root = victim.body;
                    if (root != null) { root.isKinematic = true; root.velocity = Vector3.zero; }
                }
                catch (Exception) { }
                var ragdoller = victim.GetComponent<Ragdoller>();
                if (ragdoller != null)
                {
                    ragdoller.SetLocked(true);
                    foreach (var b in ragdoller.GetRagdollBodies())
                    {
                        if (b == null) continue;
                        b.isKinematic = true;
                        b.velocity = Vector3.zero;
                    }
                }
            }
            catch (Exception) { }

            try
            {
                if (Time.unscaledTime - st.LastColliderAssert > 1f)
                {
                    st.LastColliderAssert = Time.unscaledTime;
                    foreach (var c in victim.GetComponentsInChildren<Collider>(true))
                        if (c != null && c.enabled) c.enabled = false;
                }
            }
            catch (Exception) { }

            try
            {
                if (Time.unscaledTime - st.LastStopAnim > 2f)
                {
                    st.LastStopAnim = Time.unscaledTime;
                    victim.photonView.RPC("StopAnimationRPC", RpcTarget.All);
                }
            }
            catch (Exception) { }
        }

        private static Vector3 MouthOf(Kobold eater)
        {
            try
            {
                var desc = eater.GetComponent<CharacterDescriptor>();
                var anim = desc != null ? desc.GetDisplayAnimator() : null;
                var head = anim != null ? anim.GetBoneTransform(HumanBodyBones.Head) : null;
                if (head != null) return head.position - Vector3.up * 0.1f;
            }
            catch (Exception) { }
            return eater.transform.position + Vector3.up * 1.1f;
        }

        private static Vector3 EggPopPos(Kobold eater)
        {
            Vector3 fwd = Vector3.forward;
            try
            {
                var cam = OrbitCamera.GetCamera();
                Vector3 f = cam != null && cam.transform != null
                    ? cam.transform.forward
                    : eater.transform.forward;
                f.y = 0f;
                if (f.sqrMagnitude > 0.001f) fwd = f.normalized;
            }
            catch (Exception) { }
            return eater.transform.position + fwd * 0.8f + Vector3.up * 0.1f;
        }

        private static void Bubble(Kobold body, string msg)
        {
            if (body == null || string.IsNullOrEmpty(msg)) return;
            try
            {
                var chatter = body.GetComponentInChildren<Chatter>(true);
                if (chatter != null) chatter.DisplayMessage(msg, 3f);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // Digest abort — the no-cheat escape hatch. The player types a command
        // into the game chat ("swallow abort", "swallow stop", "spit out",
        // "cough up", "regurgitate", …) and everyone currently IN a swallow is
        // released alive: scale/colliders/renderers/kinematics/controller
        // restored, set down in front of the eater, steering un-gated. Nothing
        // was laid yet by construction (the effect only fires on the final tick
        // and the session is removed before restoring). Only the LOCAL player's
        // lines are honored; the matching line is CONSUMED here so the NPCs never
        // hear the escape hatch typed as chat. With the feature off, the line is
        // never hijacked.
        // ------------------------------------------------------------------
        internal static bool TryAbortFromChat(string rawMsg)
        {
            if (Plugin == null) return false;
            var cfgEnabled = Plugin._cfgSwallowEnabled;
            var cfgAgent = Plugin._cfgSwallowAgentEat;
            bool swallowOn = cfgEnabled != null && cfgEnabled.Value;
            bool agentOn = cfgAgent != null && cfgAgent.Value;
            if (!swallowOn && !agentOn) return false;
            if (!SwallowLogic.MatchesAny(SwallowLogic.NormalizeLetters(rawMsg), SwallowLogic.AbortCommands))
                return false;
            bool hadSessions = ActiveCount > 0;
            if (hadSessions) AbortAllDigests();
            // A PARKED player (digest already completed, egg laid/riding, spectate
            // ongoing) has no session anymore — the release handles it too.
            SpectateManager.ReleaseFor(null, SpectateLogic.CauseAbort);
            if (!hadSessions && !SpectateManager.Active)
            {
                try
                {
                    if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null)
                        Bubble(pp.kobold, "Your stomach is empty — nobody is being swallowed.");
                }
                catch (Exception) { }
            }
            return true;
        }

        internal static void AbortAllDigests()
        {
            lock (_lock)
            {
                if (_active.Count == 0)
                {
                    try
                    {
                        if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null)
                            Bubble(pp.kobold, "Your stomach is empty — nobody is being swallowed.");
                    }
                    catch (Exception) { }
                    return;
                }
                for (int i = _active.Count - 1; i >= 0; i--)
                {
                    var st = _active[i];
                    _active.RemoveAt(i);
                    try { ReleaseSwallowed(st); }
                    catch (Exception e)
                    {
                        try { LLMNPCPlugin.Log.LogWarning("swallow abort (" + st.Name + "): " + e.Message); } catch (Exception) { }
                    }
                }
            }
        }

        // Undo the entire pin for one session and put the victim back into the
        // world in front of the eater (their OnReleaseRPC-style recovery, minus
        // what we changed ourselves).
        private static void ReleaseSwallowed(SwallowState st)
        {
            var v = st.Victim;
            if (!SafeAlive(v))
            {
                LLMNPCPlugin.Log.LogWarning("swallow abort: '" + st.Name + "' was already gone — nothing to release");
                return;
            }
            // Player victims: hand the camera + input back and drop the spectate
            // record — the body restore below then puts them down at the feet.
            if (st.VictimIsPlayer)
                try { SpectateManager.ReleaseFor(v, SpectateLogic.CauseAbort); } catch (Exception) { }
            try
            {
                var pv = v.photonView;
                try { if (pv != null && !pv.IsMine) pv.RequestOwnership(); } catch (Exception) { }

                RestoreVictimBody(v, EggPopPos(st.Eater) + Vector3.up * 0.15f);
            }
            catch (Exception e) { LLMNPCPlugin.Log.LogWarning("swallow restore (" + st.Name + "): " + e.Message); }

            if (st.Npc != null)
            {
                try
                {
                    st.Npc.Swallowed = false;
                    st.Npc.PushWorldEvent("the digestion was ABORTED before any conversion — you were released from " + BodyLabel(st.Eater) + "'s stomach alive and set down AT THEIR FEET. You are NOT an egg, nothing of you was consumed or changed. Recover and react to this!");
                    st.Npc.PersistLifeForSwallow();
                }
                catch (Exception) { }
            }

            try { Bubble(st.Eater, "You heave " + st.Name + " back up — alive and whole."); } catch (Exception) { }
            if (st.EaterNpc != null)
            {
                try
                {
                    st.EaterNpc.PushWorldEvent("mid-swallow ABORT — you just heaved " + st.Name
                        + " back up (the player called it off in chat). They are alive and set down right at your feet; nothing of them was consumed.");
                }
                catch (Exception) { }
            }
            try { Plugin.FanoutWorldEventExcept("you watch " + BodyLabel(st.Eater) + " heave " + st.Name + " back up mid-swallow — released alive, not converted", st.EaterNpc, st.Npc); } catch (Exception) { }
            try { LLMNPCPlugin.Log.LogInfo("swallow: DIGEST ABORTED — '" + st.Name + "' released by chat command"); } catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // Completion dispatch: apply the configured effect to the fully-digested
        // victim. Every mode ends by destroying the victim body (the swallow
        // snapshotted their persona at Begin AND again here-before-destroy).
        // Returning false keeps the victim pinned for a retry tick.
        // ------------------------------------------------------------------
        private static bool Consume(SwallowState st)
        {
            switch (Effect())
            {
                case SwallowEffect.BellyEgg: return PourIntoBelly(st);
                case SwallowEffect.Grow: return AbsorbIntoEater(st);
                case SwallowEffect.Destroy: return EraseVictim(st);
                default: return LayEggAtFeet(st);
            }
        }

        // Snapshot + remove the eaten body. False only when Photon refuses (victim
        // stays pinned; callers retry next tick).
        private static bool DestroyVictim(SwallowState st)
        {
            // Final life snapshot (captures remembers from the digest) BEFORE the
            // body is destroyed — the teardown's own save no-ops on a destroyed body.
            try { if (st.Npc != null) st.Npc.PersistLifeForSwallow(); } catch (Exception) { }
            try
            {
                PhotonNetwork.Destroy(st.Victim.gameObject);
                return true;
            }
            catch (Exception e)
            {
                LLMNPCPlugin.Log.LogWarning("swallow victim destroy: " + e.Message + " — retrying from pin");
                return false;
            }
        }

        // Grow-mode memory absorption (user request — was plain deletion): the eaten
        // person's saved life pours into the EATER — their knowledge survives inside
        // whoever consumed them. The eater's OWN identity (name/persona/pronouns) is
        // never touched: absorbing someone is not BECOMING them; only the knowledge
        // (facts) transfers, plus one 'absorbed:' marker fact. Destroy stays
        // deletion (ForgetVictim); egg modes keep the records for rebirth.
        private static void AbsorbVictimLife(SwallowState st)
        {
            try
            {
                if (string.IsNullOrEmpty(st.OldBodyKey)) return;
                var rec = MemoryStore.Load(st.OldBodyKey);
                if (rec == null)
                {
                    try { LLMNPCPlugin.Log.LogInfo("swallow: '" + st.Name + "' had no saved life to absorb (unclaimed/vanilla body with no records)"); } catch (Exception) { }
                    return;
                }
                var facts = new List<string>();
                if (rec.Facts != null)
                    foreach (var f in rec.Facts)
                        if (!string.IsNullOrWhiteSpace(f)) facts.Add(f);
                try { LLMNPCPlugin.Log.LogInfo("swallow: absorbing " + facts.Count + " memories of '" + (rec.Name ?? st.Name) + "' into " + BodyLabel(st.Eater)); } catch (Exception) { }

                // A live LLM eater? Inject the memories into their session NOW — the
                // mind knows things it never learned this session; their own teardown
                // persists them with the body. (RememberFact dedupes per ':'-prefix
                // category, so absorbed facts go in PLAIN — prefixed they'd collapse
                // into one slot.)
                var eatNpc = Plugin.InstanceForBody(st.Eater);
                if (eatNpc != null)
                {
                    try
                    {
                        eatNpc.PushWorldEvent("[absorb] " + st.Name + " was fully digested by you — their life poured in: " + facts.Count + " of their MEMORIES are yours now. Places/things they knew, you know. Mention or use them naturally.");
                        eatNpc.AbsorbVictimFacts(st.Name, facts);
                    }
                    catch (Exception e) { LLMNPCPlugin.Log.LogWarning("swallow absorb live: " + e.Message); }
                }

                // And mirror the merged life under the EATER's own body key, so an
                // eater we're not driving right now still wakes up remembering.
                try
                {
                    var eaterKey = NPCInstance.BodyKey(st.Eater);
                    if (string.IsNullOrEmpty(eaterKey)) return;
                    string eaterName = eatNpc != null ? eatNpc.GetMyName() : NPCInstance.CleanName(st.Eater.name);
                    var eatRec = MemoryStore.Load(eaterKey);
                    var merged = new List<string>();
                    if (eatRec != null && eatRec.Facts != null)
                        foreach (var f in eatRec.Facts)
                            if (!string.IsNullOrWhiteSpace(f)) merged.Add(f);
                    while (merged.Count >= 180 && merged.Count > 0) merged.RemoveAt(0);
                    merged.Add("absorbed:" + st.Name + " was digested whole by this body and their memories were taken in — their knowledge is yours now");
                    foreach (var f in facts) if (merged.Count < 220) merged.Add(f);
                    if (eatRec != null)
                        MemoryStore.Save(eaterKey, eaterName ?? eatRec.Name, eatRec.Persona, merged, eatRec.Goal, eatRec.Pronouns);
                    else
                        MemoryStore.Save(eaterKey, NPCInstance.CleanName(st.Eater.name), null, merged, null, null);
                }
                catch (Exception e) { LLMNPCPlugin.Log.LogWarning("swallow absorb persist: " + e.Message); }
            }
            catch (Exception e)
            {
                try { LLMNPCPlugin.Log.LogWarning("swallow absorb: " + e.Message); } catch (Exception) { }
            }
        }

        private static bool PourIntoBelly(SwallowState st)
        {
            try
            {
                var victimGenes = st.Victim.GetGenes();
                var belly = st.Eater.bellyContainer;
                if (belly == null) { LLMNPCPlugin.Log.LogWarning("swallow BellyEgg: eater has no belly container — egg withheld"); return false; }

                ScriptableReagent eggReagent;
                if (!ReagentDatabase.TryGetAsset("Egg", out eggReagent) || eggReagent == null)
                {
                    LLMNPCPlugin.Log.LogWarning("swallow BellyEgg: 'Egg' reagent asset missing — egg withheld");
                    return false;
                }
                var contents = new ReagentContents();
                contents.AddMix(eggReagent.GetReagent(20f));
                belly.AddMix(contents, GenericReagentContainer.InjectType.Inject);
                // The belly's GeneHolder carries the payload genes: the nest lay mixes
                // the player's genes with THESE (OvipositionSpot's Mix(k, belly)).
                if (victimGenes != null) belly.SetGenes(victimGenes);
                var victimFp = NPCInstance.GenesFingerprint(victimGenes);
                // A player victim's spectate record decides: park (hatch revives
                // them) or — record lost to a restart — the old destroy path.
                bool parkPlayer = st.VictimIsPlayer;
                try { parkPlayer = parkPlayer && SpectateManager.IsSpectating(st.Victim); } catch (Exception) { parkPlayer = false; }
                if (!string.IsNullOrEmpty(victimFp))
                {
                    if (parkPlayer)
                    {
                        lock (_lock)
                        {
                            _bellyGeneticOwners[victimFp] = new RebirthLink { EaterLabel = BodyLabel(st.Eater), IsPlayer = true };
                            _bellyNames[victimFp] = st.Name;
                        }
                        SpectateManager.MarkEggLaid(st.Victim, victimFp, "the nest lay + plant");
                    }
                    else if (!string.IsNullOrEmpty(st.OldBodyKey))
                    {
                        lock (_lock)
                        {
                            _bellyGeneticOwners[victimFp] = new RebirthLink { OldKey = st.OldBodyKey, EaterLabel = BodyLabel(st.Eater) };
                            _bellyNames[victimFp] = st.Name;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                LLMNPCPlugin.Log.LogWarning("swallow BellyEgg pour: " + e.Message + " — retrying from pin");
                return false;
            }

            try { Bubble(st.Eater, st.Name + " becomes egg inside your belly — sit on a nest to lay."); } catch (Exception) { }
            if (st.EaterNpc != null)
            {
                try
                {
                    st.EaterNpc.PushWorldEvent("the conversion is complete: " + st.Name
                        + " is now Egg reagent + genes inside YOUR belly — their fate is a NEST LAY: sit on a nest and lay them back into the world, then plant the laid egg. THE POUR ALONE BIRTHS NOBODY: a nest lays only with egg >5ml (you now carry 20) AND energy >1 — if a nest refuses you, EAT FOOD and return; no station, no play and no waiting ever raises energy.");
                }
                catch (Exception) { }
            }
            try
            {
                string eat = BodyLabel(st.Eater);
                Plugin.FanoutWorldEventExcept("you watch " + eat + " finish digesting " + st.Name + " — the converted egg now waits in " + eat + "'s belly; a nest lay will bring them into the world again (mixing " + eat + "'s genes with theirs — the nest needs egg >5ml AND energy >1, so eat before sitting).", st.EaterNpc, st.Npc);
            }
            catch (Exception) { }
            try { LLMNPCPlugin.Log.LogInfo("swallow: '" + st.Name + "' poured into " + BodyLabel(st.Eater) + "'s belly as Egg reagent (20u) with their genes"); } catch (Exception) { }

            try { if (st.Npc != null) st.Npc.PushWorldEvent("the conversion is complete — you are now a carried egg in " + BodyLabel(st.Eater) + "'s belly, waiting to be laid."); } catch (Exception) { }

            if (st.VictimIsPlayer)
            {
                // Spectate flow: the player's body parks inside (SpectateManager
                // owns it until the nest lay's egg is planted and hatched).
                if (SpectateManager.IsSpectating(st.Victim)) return true;
            }
            return DestroyVictim(st);
        }

        // Grow/Destroy have no rebirth — erase the consumed one's saved life so the
        // person is gone from disk too ("sold the effect": no records, no return).
        private static void ForgetVictim(SwallowState st)
        {
            try
            {
                if (string.IsNullOrEmpty(st.OldBodyKey)) return;
                MemoryStore.Delete(st.OldBodyKey);
                LLMNPCPlugin.Log.LogInfo("swallow: erased saved memories of '" + st.Name + "' (no-rebirth mode)");
            }
            catch (Exception) { }
        }

        private static bool AbsorbIntoEater(SwallowState st)
        {
            try
            {
                var vg = st.Victim.GetGenes();
                var eg = st.Eater.GetGenes();
                if (vg == null || eg == null) { LLMNPCPlugin.Log.LogWarning("swallow Grow: genes unreadable — retrying"); return false; }
                float frac = GrowPercentFraction();
                var next = eg;
                var largerOn = Plugin._cfgSwallowGrowLarger != null && Plugin._cfgSwallowGrowLarger.Value;
                var strengthOn = Plugin._cfgSwallowGrowStrength != null && Plugin._cfgSwallowGrowStrength.Value;
                var sexOn = Plugin._cfgSwallowGrowSex != null && Plugin._cfgSwallowGrowSex.Value;
                bool sizeGained = false, strengthGained = false;
                bool breastsGrew = false, dickGrew = false, dickGained = false;
                if (largerOn && vg.baseSize > 0f)
                {
                    // baseSize is the inflate gene (log-scaled size) — proportional take
                    // from the person absorbed, same knob both ways so big meals = big change.
                    next = next.With(baseSize: next.baseSize + Mathf.Max(0.5f, vg.baseSize * frac));
                    sizeGained = true;
                }
                if (strengthOn)
                {
                    // grabCount = how many things you can hold (the strength gene the
                    // game's Grabber reads); maxEnergy = stamina — both take from the meal.
                    next = next.With(
                        grabCount: (byte)Math.Min(8, (int)next.grabCount + 1),
                        maxEnergy: Math.Min(20f, next.maxEnergy + vg.maxEnergy * frac));
                    strengthGained = true;
                }
                if (sexOn)
                {
                    // Curves/groin take (see SwallowLogic.GrowSexGenes for the rules):
                    // breasts and an existing dick take the victim's × frac; a victim's
                    // dick on a dickless eater is gained whole. SetGenes live-applies
                    // every one of these: inflate sizes AND the dick spawn/unspawn.
                    var sex = SwallowLogic.GrowSexGenes(eg.dickEquip, eg.breastSize, eg.dickSize, eg.dickThickness,
                        vg.dickEquip, vg.breastSize, vg.dickSize, vg.dickThickness, vg.ballSize, frac);
                    if (sex != null)
                    {
                        if (sex.BreastsGrew)
                        {
                            next = next.With(breastSize: sex.BreastSize);
                            breastsGrew = true;
                        }
                        if (sex.DickGained)
                        {
                            next = next.With(dickEquip: sex.DickEquip, dickSize: sex.DickSize,
                                dickThickness: sex.DickThickness, ballSize: sex.BallSize);
                            dickGained = true;
                        }
                        else if (sex.DickGrew)
                        {
                            next = next.With(dickSize: sex.DickSize, dickThickness: sex.DickThickness);
                            dickGrew = true;
                        }
                    }
                }
                if (!sizeGained && !strengthGained && !breastsGrew && !dickGrew && !dickGained)
                    LLMNPCPlugin.Log.LogWarning("swallow Grow: all SwallowGrow toggles off — consuming with no gain (use SwallowEffect=Destroy for that intent)");
                st.Eater.SetGenes(next); // live-applies: resize, belly volume, curves, dick
                var bodyChanged = new List<string>();
                if (sizeGained) bodyChanged.Add("you are bigger now");
                if (strengthGained) bodyChanged.Add("you are stronger (one more carry slot, more stamina)");
                if (breastsGrew) bodyChanged.Add("your breasts grew curvier");
                if (dickGained) bodyChanged.Add("you GREW a dick — you had none; theirs is yours now");
                else if (dickGrew) bodyChanged.Add("your dick grew bigger");
                var bubbleBits = new List<string>();
                if (sizeGained) bubbleBits.Add("size");
                if (strengthGained) bubbleBits.Add("strength");
                if (breastsGrew) bubbleBits.Add("curves");
                if (dickGained) bubbleBits.Add("their groin");
                else if (dickGrew) bubbleBits.Add("a bigger dick");
                try
                {
                    Bubble(st.Eater, st.Name + " is absorbed"
                        + (bubbleBits.Count > 0 ? " — " + JoinEnglish(bubbleBits) : "")
                        + "; memories and all.");
                }
                catch (Exception) { }
                if (st.EaterNpc != null && bodyChanged.Count > 0)
                {
                    // The eater's mind learns its OWN body changed — the fanout below
                    // deliberately skips this instance, so it needs its own line.
                    try
                    {
                        st.EaterNpc.PushWorldEvent("the conversion is complete: absorbing " + st.Name
                            + " changed your body — " + JoinEnglish(bodyChanged) + ". Their memories poured in too; this is your body now.");
                    }
                    catch (Exception) { }
                }
                try
                {
                    string eat = BodyLabel(st.Eater);
                    NPCInstance grownNpc = null;
                    try { grownNpc = Plugin.InstanceForBody(st.Eater); } catch (Exception) { }
                    var looks = new List<string>();
                    if (sizeGained) looks.Add("looks bigger");
                    if (strengthGained) looks.Add("is stronger");
                    if (breastsGrew) looks.Add("has grown curvier");
                    if (dickGained) looks.Add("now wears a new dick");
                    else if (dickGrew) looks.Add("has a bigger dick");
                    string lookText = looks.Count > 0 ? JoinEnglish(looks) : "looks unchanged";
                    Plugin.FanoutWorldEventExcept("you watch " + eat + " finish digesting " + st.Name + " — nothing comes back out; " + eat
                        + " " + lookText + ", and " + st.Name + "'s memories are theirs now.", grownNpc, st.Npc);
                }
                catch (Exception) { }
                try
                {
                    LLMNPCPlugin.Log.LogInfo("swallow: '" + st.Name + "' absorbed by " + BodyLabel(st.Eater)
                        + " (size=" + (sizeGained ? "+" + (vg.baseSize * frac).ToString("0.#", CultureInfo.InvariantCulture) : "no")
                        + " strength=" + (strengthGained ? "yes" : "no")
                        + " curves=" + (breastsGrew ? "yes" : "no")
                        + " dick=" + (dickGained ? "gained" : dickGrew ? "bigger" : "no") + ")");
                }
                catch (Exception) { }
                try { if (st.Npc != null) st.Npc.PushWorldEvent("the conversion is complete — you have been absorbed: body, strength... and your MEMORIES now live on in " + BodyLabel(st.Eater) + "."); } catch (Exception) { }
            }
            catch (Exception e)
            {
                LLMNPCPlugin.Log.LogWarning("swallow Grow apply: " + e.Message + " — retrying from pin");
                return false;
            }
            if (st.VictimIsPlayer)
            {
                // Spectate flow: no egg will come from this stomach — the player
                // body parks inside and the watch continues until they abort or
                // leave ("no coming back" is exactly what Destroy/Grow say).
                try
                {
                    if (SpectateManager.IsSpectating(st.Victim))
                    {
                        SpectateManager.MarkNoHatch(st.Victim);
                        return true;
                    }
                }
                catch (Exception) { }
            }
            if (!DestroyVictim(st)) return false;
            AbsorbVictimLife(st);
            return true;
        }

        private static bool EraseVictim(SwallowState st)
        {
            try { Bubble(st.Eater, st.Name + " is fully consumed."); } catch (Exception) { }
            if (st.EaterNpc != null)
            {
                try
                {
                    st.EaterNpc.PushWorldEvent("the conversion is complete: " + st.Name
                        + " is fully digested — there is nothing left of them and nothing was gained. You consumed them entirely.");
                }
                catch (Exception) { }
            }
            try
            {
                string eat = BodyLabel(st.Eater);
                Plugin.FanoutWorldEventExcept("you watch " + eat + " finish digesting " + st.Name + " — there is nothing left of them.", st.EaterNpc, st.Npc);
            }
            catch (Exception) { }
            try { LLMNPCPlugin.Log.LogInfo("swallow: '" + st.Name + "' consumed entirely (Destroy mode)"); } catch (Exception) { }
            try { if (st.Npc != null) st.Npc.PushWorldEvent("the conversion is complete — you have been consumed entirely. There is no egg, no rebirth."); } catch (Exception) { }

            if (st.VictimIsPlayer)
            {
                try
                {
                    if (SpectateManager.IsSpectating(st.Victim))
                    {
                        SpectateManager.MarkNoHatch(st.Victim);
                        return true;
                    }
                }
                catch (Exception) { }
            }
            if (!DestroyVictim(st)) return false;
            ForgetVictim(st);
            return true;
        }

        // ------------------------------------------------------------------
        // Egg mode (default): spawn the genes-mixing egg at the player's feet and
        // destroy the victim body.
        // ------------------------------------------------------------------
        private static bool LayEggAtFeet(SwallowState st)
        {
            // Latest genes (they may have drifted during the digest) and its fingerprint
            // normalized through the same half-precision round-trip a plant-spawned
            // body will carry, so the rebirth match survives serialization.
            KoboldGenes mixed = null;
            Vector3 pos = EggPopPos(st.Eater);

            try
            {
                var eaterGenes = st.Eater.GetGenes() ?? new KoboldGenes();
                var victimGenes = st.Victim.GetGenes() ?? new KoboldGenes();
                mixed = KoboldGenes.Mix(eaterGenes, victimGenes);
            }
            catch (Exception)
            {
                try { mixed = st.Victim.GetGenes(); } catch (Exception) { }
            }
            if (mixed == null) { LLMNPCPlugin.Log.LogWarning("swallow: no genes resolved — egg withheld"); return false; }

            string fingerprint = NPCInstance.GenesFingerprint(mixed);
            if (string.IsNullOrEmpty(fingerprint)) { LLMNPCPlugin.Log.LogWarning("swallow: fingerprint failed — egg withheld"); return false; }

            GameObject eggObj = null;
            try
            {
                var spawnData = new NetStack.Serialization.BitBuffer(16);
                spawnData.AddKoboldGenes(mixed);
                eggObj = PhotonNetwork.InstantiateRoomObject(EggPhotonName, pos, Quaternion.identity, 0,
                    new object[] { spawnData });
            }
            catch (Exception e)
            {
                LLMNPCPlugin.Log.LogWarning("swallow egg spawn: " + e.Message);
                return false; // retry next tick, victim stays pinned
            }
            if (eggObj == null) return false;

                lock (_lock)
                {
                    if (st.VictimIsPlayer)
                        _awaitingRebirth[fingerprint] = new RebirthLink { EaterLabel = BodyLabel(st.Eater), IsPlayer = true };
                    else
                        _awaitingRebirth[fingerprint] = new RebirthLink { OldKey = st.OldBodyKey, EaterLabel = BodyLabel(st.Eater) };
                    _rebirthNames[fingerprint] = st.Name;
                }

            // Flavor parity with oviposition: seed the egg with ScrambledEgg so it can
            // also be eaten (like vanilla eggs) with the layer's genes riding along.
            try
            {
                ScriptableReagent scrambled;
                if (ReagentDatabase.TryGetAsset("ScrambledEgg", out scrambled) && scrambled != null)
                {
                    var contents = new ReagentContents();
                    contents.AddMix(scrambled.GetReagent(20f));
                    var buf = new NetStack.Serialization.BitBuffer(4);
                    buf.AddReagentContents(contents);
                    var eggPv = eggObj.GetComponentInChildren<PhotonView>();
                    if (eggPv != null && st.Eater.photonView != null)
                        eggPv.RPC(nameof(GenericReagentContainer.ForceMixRPC), RpcTarget.All, buf,
                            st.Eater.photonView.ViewID, (byte)GenericReagentContainer.InjectType.Inject);
                }
            }
            catch (Exception) { }

            try { Bubble(st.Eater, st.Name + " is fully an egg now."); } catch (Exception) { }
            if (st.EaterNpc != null)
            {
                try
                {
                    st.EaterNpc.PushWorldEvent("the conversion is complete: you laid an EGG — inside is " + st.Name
                        + " (their genes mixed with yours). Plant it in soil to reconstitute them.");
                }
                catch (Exception) { }
            }
            try
            {
                string eat = BodyLabel(st.Eater);
                string possess = eat == "the player" ? "their" : eat + "'s";
                Plugin.FanoutWorldEventExcept("you watch " + eat + " lay an egg — inside is " + st.Name + ", mixed with " + possess + " genes. Plant it in soil to reconstitute them.", st.EaterNpc, st.Npc);
            }
            catch (Exception) { }
            try { LLMNPCPlugin.Log.LogInfo("swallow: egg laid for '" + st.Name + "' (fingerprint " + fingerprint + ")"); } catch (Exception) { }
            try { if (st.Npc != null) st.Npc.PushWorldEvent("the conversion is complete — an egg bearing your new genes rests at " + BodyLabel(st.Eater) + "'s feet; plant it in soil to be born again."); } catch (Exception) { }

            if (st.VictimIsPlayer)
            {
                // Spectate flow: the player's body parks in the stomach; the
                // planted egg's hatch revives them there with the egg's genes.
                try
                {
                    if (SpectateManager.IsSpectating(st.Victim))
                    {
                        SpectateManager.MarkEggLaid(st.Victim, fingerprint, "the egg is planted in soil");
                        return true;
                    }
                }
                catch (Exception) { }
            }

            if (!DestroyVictim(st))
            {
                // Photon refused the destroy — de-register so a later unrelated spawn
                // can't match this fingerprint.
                lock (_lock) _awaitingRebirth.Remove(fingerprint);
                _rebirthNames.Remove(fingerprint);
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Rebirth: a kobold carrying one of our egg's gene sets just spawned
        // (the vanilla plant chain hatched it). Player victims: revive THEM at
        // the hatch spot (spectate flow). NPC victims: copy the eaten body's
        // saved life onto the new body's MemoryStore key so the claim scan
        // wakes the same person — same name, same persona, changed genes.
        // ------------------------------------------------------------------
        private static void OnKoboldSpawned(Kobold k)
        {
            try
            {
                if (k == null || Plugin == null) return;
                string fingerprint = NPCInstance.GenesFingerprint(k.GetGenes());
                if (string.IsNullOrEmpty(fingerprint)) return;
                string oldKey, name;
                bool playerLink = false;
                lock (_lock)
                {
                    RebirthLink link;
                    if (!_awaitingRebirth.TryGetValue(fingerprint, out link)) return;
                    oldKey = link != null ? link.OldKey : null;
                    if (link != null && link.IsPlayer) playerLink = true;
                    _rebirthNames.TryGetValue(fingerprint, out name);
                }
                if (playerLink)
                {
                    // Spectate flow: a player is parked inside the eater waiting for
                    // THIS egg. Revive the player with the hatched genes and despawn
                    // the duplicate NPC body; if the spectate record is gone (plugin
                    // restarted mid-egg), the hatched body stays a plain NPC instead.
                    bool revived = false;
                    try { revived = SpectateManager.HatchRevive(k, fingerprint); } catch (Exception) { }
                    try
                    {
                        Plugin.FanoutWorldEvent(revived
                            ? "you watch the egg split open — THE PLAYER stands up out of it, back in the world in a fresh body with changed genes."
                            : "you watch the egg split open — a fresh kobold with changed genes stands up: " + (name ?? "the swallowed one"));
                    }
                    catch (Exception) { }
                    lock (_lock)
                    {
                        _awaitingRebirth.Remove(fingerprint);
                        _rebirthNames.Remove(fingerprint);
                    }
                    return;
                }
                string newKey = NPCInstance.BodyKey(k);
                var rec = string.IsNullOrEmpty(oldKey) ? null : MemoryStore.Load(oldKey);
                if (rec != null && !string.IsNullOrEmpty(newKey))
                {
                    var facts = new List<string>();
                    if (rec.Facts != null) foreach (var f in rec.Facts) if (!string.IsNullOrEmpty(f)) facts.Add(f);
                    facts.Add("was swallowed whole and digested into an egg; hatched back in a NEW body with MIXED genes (the eater's genes and theirs blended). You are the same person.");
                    MemoryStore.Save(newKey, rec.Name, rec.Persona, facts, rec.Goal, rec.Pronouns);
                    try
                    {
                        LLMNPCPlugin.Log.LogInfo("swallow: rebirth — '" + (rec.Name ?? "?") + "' hatched from an egg; persona restored to body key '" + newKey + "' (genes changed)");
                    }
                    catch (Exception) { }
                }
                lock (_lock)
                {
                    _awaitingRebirth.Remove(fingerprint);
                    _rebirthNames.Remove(fingerprint);
                }
                try { Plugin.FanoutWorldEvent("you watch the egg split open — a fresh kobold with changed genes stands up: " + (name ?? "the swallowed one")); } catch (Exception) { }
            }
            catch (Exception e)
            {
                try { LLMNPCPlugin.Log.LogWarning("swallow rebirth: " + e.Message); } catch (Exception) { }
            }
        }

        // ------------------------------------------------------------------
        // BellyEgg relay: the player sat on a nest and laid. If the belly genes
        // belong to a swallowed person (our SetGenes'd payload), the LAID egg's
        // seed genes get the rebirth registration — because the lay-time
        // Mix(player, belly) rolls randoms and can't be predicted from here.
        // Fires on the main thread (Photon event).
        // ------------------------------------------------------------------
        private static void OnOviposition(int layerViewId, int eggViewId)
        {
            try
            {
                if (Plugin == null) return;
                RebirthLink link;
                string name;
                string layerFp;
                lock (_lock)
                {
                    if (_bellyGeneticOwners.Count == 0) return;
                }
                var layerPv = PhotonNetwork.GetPhotonView(layerViewId);
                var layer = layerPv != null ? layerPv.GetComponent<Kobold>() : null;
                if (layer == null || layer.bellyContainer == null) return;
                layerFp = NPCInstance.GenesFingerprint(layer.bellyContainer.GetGenes());
                if (string.IsNullOrEmpty(layerFp)) return;
                lock (_lock)
                {
                    if (!_bellyGeneticOwners.TryGetValue(layerFp, out link)) return;
                    _bellyNames.TryGetValue(layerFp, out name);
                }

                var eggPv = PhotonNetwork.GetPhotonView(eggViewId);
                var seed = eggPv != null ? eggPv.GetComponentInChildren<Seed>(true) : null;
                var eggGenes = seed != null ? NPCInstance.GetField<KoboldGenes>(seed, "genes") : null;
                string eggFp = NPCInstance.GenesFingerprint(eggGenes);
                if (string.IsNullOrEmpty(eggFp))
                {
                    LLMNPCPlugin.Log.LogWarning("swallow: nest-lay of '" + (name ?? "?") + "' couldn't read the egg's genes — the rebirth link is lost for THIS egg");
                    return;
                }
                lock (_lock)
                {
                    _awaitingRebirth[eggFp] = link;
                    _rebirthNames[eggFp] = name;
                }
                try { LLMNPCPlugin.Log.LogInfo("swallow: " + (link.EaterLabel ?? "?") + " laid the belly egg for '" + (name ?? "?") + "' — planted-and-hatched, this egg carries their persona (fingerprint " + eggFp + ")"); } catch (Exception) { }
            }
            catch (Exception e)
            {
                try { LLMNPCPlugin.Log.LogWarning("swallow oviposition: " + e.Message); } catch (Exception) { }
            }
        }

        // Test/inspect helpers.
        internal static int ActiveCount { get { lock (_lock) return _active.Count; } }

        // Drop-tool feedback: names of swallow sessions begun (as dropper) by this
        // body in the last ~2s — called on the main thread right after TryDrop so
        // the tool's result can tell the truth ("force-fed") instead of "dropped".
        internal static string[] SwallowsBegunBy(Kobold body)
        {
            if (body == null) return new string[0];
            var names = new List<string>();
            try
            {
                float now = Time.unscaledTime;
                lock (_lock)
                    foreach (var s in _active)
                        if (s.Actor == body && now - s.StartUnscaled < 2f) names.Add(s.Name);
            }
            catch (Exception) { }
            return names.ToArray();
        }
        internal static void ResetForTests()
        {
            lock (_lock) _active.Clear();
            _awaitingRebirth.Clear();
            _rebirthNames.Clear();
            _bellyGeneticOwners.Clear();
            _bellyNames.Clear();
            Plugin = null;
        }
    }

    // ------------------------------------------------------------------
    // Plugin surface for the swallow manager.
    // ------------------------------------------------------------------
    public partial class LLMNPCPlugin
    {
        // The live instance currently driving this body, or null (unclaimed body,
        // e.g. a vanilla AI kobold — the swallow still runs, just without an LLM).
        internal NPCInstance InstanceForBody(Kobold target)
        {
            try
            {
                lock (_instancesLock)
                {
                    foreach (var npc in _instances)
                        try { if (npc.HoldsBody(target)) return npc; } catch (Exception) { }
                }
            }
            catch (Exception) { }
            return null;
        }

        // Swallow config (bound in Main.cs Awake, all under [LittleTreats]).
        internal ConfigEntry<bool> _cfgSwallowEnabled;
        internal ConfigEntry<float> _cfgSwallowDigest;
        internal ConfigEntry<float> _cfgSwallowLookUpDot;
        internal ConfigEntry<bool> _cfgSwallowForceFeeding;
        internal ConfigEntry<SwallowEffect> _cfgSwallowEffect;
        internal ConfigEntry<bool> _cfgSwallowGrowLarger;
        internal ConfigEntry<bool> _cfgSwallowGrowStrength;
        internal ConfigEntry<bool> _cfgSwallowGrowSex;
        internal ConfigEntry<float> _cfgSwallowGrowPercent;
        // Spectate (agent eats the PLAYER) —see src/Spectate.cs.
        internal ConfigEntry<bool> _cfgSwallowAgentEat;
        internal ConfigEntry<float> _cfgSwallowEatRange;
        internal ConfigEntry<SpectatePov> _cfgSwallowPov;
    }

    // ------------------------------------------------------------------
    // NPCInstance surface: the swallowed gate + the gene fingerprint.
    // ------------------------------------------------------------------
    internal partial class NPCInstance
    {
        // True while this instance's body is inside someone's stomach. Movement.cs
        // early-returns steering; the LLM session keeps running (it can speak), and
        // the swallow manager narrates the conversion into its world events.
        internal volatile bool Swallowed;

        // Persist this body's life NOW (name/persona/facts/pronouns/goal under this
        // body's key). The normal teardown saves too, but it runs AFTER the swallow
        // has already destroyed the body — and SaveBodyMemory skips destroyed bodies
        // — so the swallow manager snapshots before the destroy. Facts gained during
        // the digest (a mid-stomach remember(...)) are captured by a second snapshot
        // right before the egg is laid.
        internal void PersistLifeForSwallow()
        {
            try { SaveBodyMemory(); } catch (Exception) { }
        }

        // Grow-mode absorption: pour the digested person's facts into this LIVE
        // instance. Facts go in PLAIN (RememberFact dedupes one-slot-per ':'-prefix
        // category — a shared category prefix would collapse the whole set into one
        // slot); one 'absorbed:' marker fact summarizes the event so the mind has a
        // named memory of whose knowledge it inherited.
        internal void AbsorbVictimFacts(string victimName, List<string> facts)
        {
            if (facts == null) return;
            try
            {
                RememberFact("absorbed:" + (victimName ?? "someone") + " was digested whole by this body — their memories below were taken in and are yours now");
                foreach (var f in facts) RememberFact(f);
            }
            catch (Exception) { }
        }

        // Fingerprint of a genes object for the swallow rebirth registry: every float
        // is passed through the same HalfPrecision quantize→dequantize round-trip the
        // BitBuffer gene serialization applies, so a registered fingerprint matches
        // the genes a plant-hatched body actually carries after serialization.
        internal static string GenesFingerprint(KoboldGenes g)
        {
            if (g == null) return null;
            try
            {
                float[] floats =
                {
                    DequantizeRound(g.maxEnergy), DequantizeRound(g.baseSize), DequantizeRound(g.fatSize),
                    DequantizeRound(g.ballSize), DequantizeRound(g.dickSize), DequantizeRound(g.breastSize),
                    DequantizeRound(g.bellySize), DequantizeRound(g.metabolizeCapacitySize),
                    DequantizeRound(g.dickThickness),
                };
                byte[] bytes =
                {
                    g.hue, g.clothingHue, g.brightness, g.saturation, g.grabCount, g.species,
                };
                return SwallowLogic.FingerprintFromNumbers(floats, bytes, g.dickEquip);
            }
            catch (Exception) { return null; }
        }

        private static float DequantizeRound(float f)
        {
            try { return NetStack.Quantization.HalfPrecision.Dequantize(NetStack.Quantization.HalfPrecision.Quantize(f)); }
            catch (Exception) { return f; }
        }
    }
}