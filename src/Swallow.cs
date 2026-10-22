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
            public string EaterKey;    // the EATER's MemoryStore key — the hatched child
                                       // gets a compaction of BOTH lives (its own + tastes
                                       // of who carried them)
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

        internal sealed class SwallowState
        {
            public Kobold Victim;
            public Kobold Eater;
            public Kobold Actor;           // who initiated: eater (self-eat) or the force-feeding dropper
            public int VictimId;
            public int VictimOwnerActorNr; // their original client (returned on release)
            public NPCInstance Npc;        // null for unclaimed (vanilla AI) victims
            public NPCInstance EaterNpc;   // the eater's own mind (null: player eater / unclaimed body)
            public string Name;            // narration/display name
            public string OldBodyKey;      // MemoryStore key of the eaten body
            public string EaterKey;        // MemoryStore key of the EATER (rebirth blend + absorb mirror)
            public string Hole;            // entry hole label ("mouth"/"anal"/"pussy"/"nipple")
            public SwallowDestination Entry; // the parsed enum (pull-target resolver)
            public bool VictimIsPlayer;    // the LOCAL PLAYER is inside — spectate takeover runs;
                                           // consume keeps the body parked (never destroyed)
            public float StartUnscaled;
            public float DigestSeconds;
            public Vector3 PullStartPos;
            public int Milestone;          // last narrated digest mark
            public int LastPhase = -9;     // previous Advance phase (for the 0→1 full-insert edge)
            public int InsertNotified;     // last narrated 25/50/75/98% intake step
            public bool InsertFullNar;     // the "fully in — conversion begins" line
            public float LastProgressEvent; // recurrent digest-progress heartbeat
            public float LastKick;         // cooldown for the victim's `kick`
            public bool Surrendered;       // the victim gave up wriggling (`surrender`)
            public float LastOwnerAssert;
            public float LastColliderAssert;
            public float LastStopAnim;
            public bool Hidden;            // renderers off (set once fully swallowed)
            public bool EggSpawned;
            // VICTIM AS A LARGE PENIS: a REAL game penis prefab spawned under the
            // victim, scaled off their cached body scale — PenetrationTech sees a
            // legitimate penetrator, so the pred's orifice WIDENS via the game's own
            // shape-key machinery and the pred's arousal machinery runs normally
            // (it can feel sexual, in context). Destroyed with every session end.
            public GameObject VirtualPen;
            public float BodyScale = -1f;   // girth source (captured once at Begin)
            // Vore-pen hardening: the victim's own Penetrators captured inert for
            // the session (AutoSeek restored on every release path).
            public List<DisarmedPen> DisarmedPens;
            // Launch brake: the pred's previous altitude + tick time; consecutive
            // jettison-rate ticks end the session before PlayAreaEnforcer deletes
            // the pair out of the world.
            public float LastWatchY;
            public float LastWatchT;
            public int LaunchTicks;
        }

        // A victim-owned Penetrator captured inert for the duration of the swallow.
        internal sealed class DisarmedPen
        {
            public PenetrationTech.Penetrator Pen;
            public object OldMode;
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
                    float elapsed = now - st.StartUnscaled;
                    // INTAKE (the virtual-insertion window): the held person is being
                    // drawn in — the pred can CHECK the insert fraction every turn
                    // ("full insert" is where the actual digestion starts).
                    if (elapsed < SwallowLogic.PullSeconds)
                    {
                        float t = SwallowLogic.Smooth01(Mathf.Clamp01(elapsed / SwallowLogic.PullSeconds));
                        parts.Add("TAKING IN " + st.Name + " — " + Mathf.RoundToInt(t * 100f).ToString("0", CultureInfo.InvariantCulture)
                            + "% into your " + holeLabelFor(st.Hole) + " (" + SwallowLogic.InsertStageText(t)
                            + "); the conversion starts when they are FULLY in");
                        continue;
                    }
                    float left = Mathf.Max(0f, SwallowLogic.PullSeconds + st.DigestSeconds + SwallowLogic.SettleSeconds - elapsed);
                    string fate;
                    switch (EffectAt(st))
                    {
                        case SwallowEffect.BellyEgg: fate = "they become egg reagent + genes in you; a nest lay brings them back"; break;
                        case SwallowEffect.Grow: fate = "their body (size, curves, groin) and memories absorb into you"; break;
                        case SwallowEffect.Destroy: fate = "nothing will be left of them"; break;
                        default: fate = "an egg carrying their mixed genes pops out of you"; break;
                    }
                    string noun = SwallowLogic.InsideNoun(st.Entry);
                    parts.Add(st.Name + " (deep in your " + noun
                        + (st.Entry == SwallowDestination.Mouth ? "" : ", taken in through your " + holeLabelFor(st.Hole))
                        + (st.Surrendered ? ", they have SURRENDERED (limp)" : "")
                        + ", ~" + Mathf.CeilToInt(left).ToString("0", CultureInfo.InvariantCulture)
                        + "s to conversion — " + fate + ")");
                }
            if (parts.Count == 0) return null;
            // "INSIDE YOU" used to make RP models read the guest as PENETRATION
            // ("you have someone inside you" = sex, in their priors). A swallowed
            // person is a conversion in progress, not hole-sex — say so explicitly
            // every time, and name the ROUTE so the model knows where they went.
            return "SWALLOWED GUEST"
                + " (a MEAL-like conversion: the only penetration tied to the guest is their own body acting as a large penis — [sex]/[vore-pen] lines named vore-insert ARE that insert, and CAN feel sexual; nothing else's 'inside' means the guest): "
                + string.Join(", ", parts.ToArray());
        }

        internal static SwallowEffect Effect()
        {
            var cfg = Plugin != null ? Plugin._cfgSwallowEffect : null;
            if (cfg == null) return SwallowEffect.Egg;
            return cfg.Value;
        }

        // --------------------------
        // PER-AGENT VORE SETTINGS — the ACTOR decides target + fate. Each agent's
        // mind can set its own route (`vore_target`) and fate mode (`vore_mode`);
        // they persist with the body's memory. The OPERATOR's globals
        // (F7 destination / F8 fate / WebUI) stay the PLAYER's separate control and
        // the default: a player actor (or an agent that never set an override)
        // follows them. One master switch remains above everybody:
        // [LittleTreats] SwallowEnabled — off = nobody eats anybody, overrides
        // included. SwallowDestination=OFF disarms GESTURE (release-triggered)
        // swallows for actors without an explicit personal pick — an agent's own
        // deliberate override bypasses that accident-gate, because setting it IS
        // the deliberate act.
        // --------------------------
        internal static SwallowDestination DestinationFor(Kobold actor)
        {
            var enabled = Plugin != null ? Plugin._cfgSwallowEnabled : null;
            if (enabled == null || !enabled.Value) return SwallowDestination.Off;
            try
            {
                if (actor != null && !NPCInstance.IsPlayerKobold(actor))
                {
                    var npc = Plugin.InstanceForBody(actor);
                    string t = npc != null ? npc.VoreTargetOverride : null;
                    if (!string.IsNullOrEmpty(t)) return SwallowLogic.ParseDestination(t);
                }
            }
            catch (Exception) { }
            return Destination();
        }

        internal static SwallowEffect EffectFor(Kobold actor)
        {
            try
            {
                if (actor != null && !NPCInstance.IsPlayerKobold(actor))
                {
                    var npc = Plugin.InstanceForBody(actor);
                    string m = npc != null ? npc.VoreModeOverride : null;
                    if (!string.IsNullOrEmpty(m))
                    {
                        SwallowEffect parsed;
                        bool clear, bad;
                        if (SwallowLogic.TryParseEffect(m, out parsed, out clear, out bad) && !clear) return parsed;
                    }
                }
            }
            catch (Exception) { }
            return Effect();
        }

        // The fate a SESSION follows: the initiating actor's setting, else the eater.
        internal static SwallowEffect EffectAt(SwallowState st)
        {
            return EffectFor(st != null && st.Actor != null ? st.Actor : (st != null ? st.Eater : null));
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
                // Double gate: SwallowEnabled AND a destination picked. Destination OFF
                // (the operator's default, F7/WebUI controllable) deliberately
                // disarms every release-triggered swallow — the accidental
                // "PrecisionGrabber bumped a head and the swallow started on release"
                // failure can't happen when nothing is armed. EXCEPT: an agent with
                // their OWN vore target set is acting deliberately (setting it is the
                // decision) — their override arms their own release-gesture swallow
                // even while the operator's destination is OFF. Everyone else (the
                // player, agents that never picked) follows the global.
                if (cfgEnabled == null || !cfgEnabled.Value)
                {
                    MaybeHintDestination();
                    return true;
                }
                Kobold dropper = null;
                try { dropper = __instance.GetComponentInParent<Kobold>(); } catch (Exception) { }
                if (DestinationFor(dropper) == SwallowDestination.Off)
                {
                    MaybeHintDestination();
                    return true;
                }

                if (dropper == null) return true;

                var held = NPCInstance.HeldScanFor(dropper);
                var persons = new List<Kobold>();
                // MULTIPLAYER VORE (SwallowPlayerVictims): a PLAYER dropper may
                // target OTHER PLAYERS' bodies. NPC droppers never can.
                bool playerVictimsOK = PlayerVictimsAllowed() && NPCInstance.IsPlayerKobold(dropper);
                foreach (var h in held)
                {
                    if (h == null || h.Victim == null) continue;
                    if (h.IsPlayerVictim)
                    {
                        if (playerVictimsOK) persons.Add(h.Victim);
                        continue;
                    }
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

        private static bool PlayerVictimsAllowed()
        {
            var cfg = Plugin != null ? Plugin._cfgSwallowPlayerVictims : null;
            return cfg != null && cfg.Value;
        }

        private static bool ForceFeeding()
        {
            var cfg = Plugin != null ? Plugin._cfgSwallowForceFeeding : null;
            return cfg != null && cfg.Value;
        }

        // The armed entry hole for ALL swallow triggers (config [LittleTreats]
        // SwallowDestination — F7 cycles it, WebUI sets it). OFF disarms everything.
        internal static SwallowDestination Destination()
        {
            var cfg = Plugin != null ? Plugin._cfgSwallowDestination : null;
            if (cfg == null) return SwallowDestination.Off;
            return cfg.Value;
        }

        // The armed-vore readout for the small HUD line (SpectateHudUI): the
        // PLAYER's current settings, shown only while swallows can actually happen.
        // Everything disarmed → null (quiet). Agents' own picks live with the
        // agents (their vore tools + saved memory), not here.
        internal static string ArmedHudLine()
        {
            try
            {
                var pl = Plugin; if (pl == null) return null;
                if (pl._cfgSwallowEnabled == null || !pl._cfgSwallowEnabled.Value) return null;
                var dest = Destination();
                if (dest == SwallowDestination.Off) return null;
                return "vore: target " + SwallowLogic.DestinationLabel(dest)
                    + " · fate " + SwallowLogic.EffectLabel(Effect())
                    + " — F7 target · F8 fate (each agent picks their own via vore_target/vore_mode)";
            }
            catch (Exception) { return null; }
        }

        // One-line hint (throttled) when vore is ENABLED but the destination is OFF —
        // otherwise an enabled SwallowEnabled silently doing nothing looks broken.
        private static float _hintedDestinationOff;
        private static void MaybeHintDestination()
        {
            var cfgEnabled = Plugin != null ? Plugin._cfgSwallowEnabled : null;
            if (cfgEnabled == null || !cfgEnabled.Value) return;
            if (Destination() != SwallowDestination.Off) return;
            float now;
            try { now = Time.unscaledTime; } catch (Exception) { return; }
            if (now - _hintedDestinationOff < 120f) return;
            _hintedDestinationOff = now;
            try { LLMNPCPlugin.Log.LogInfo("swallow: SwallowEnabled is ON but SwallowDestination is OFF — nothing swallows. Cycle with F7 (off→mouth→anal→pussy→dick→nipple), set [LittleTreats] SwallowDestination, or use the WebUI settings tab."); } catch (Exception) { }
        }

        // Entry anchor for the pull phase: the chosen hole's world position on the
        // EATER's body. Mouth uses the head bone (the original route); the other
        // holes anchor on the hips/chest bones so the offset scales with the body's
        // actual size and pose. Main-thread only (reads bones).
        private static Vector3 HolePos(Kobold eater, SwallowDestination d)
        {
            if (d == SwallowDestination.Mouth) return MouthOf(eater);
            try
            {
                var desc = eater.GetComponent<CharacterDescriptor>();
                var anim = desc != null ? desc.GetDisplayAnimator() : null;
                if (anim != null)
                {
                    Transform anchor = null;
                    switch (d)
                    {
                        case SwallowDestination.Anal:
                            anchor = anim.GetBoneTransform(HumanBodyBones.Hips);
                            if (anchor != null) return anchor.TransformPoint(-0.02f, -0.03f, -0.06f);
                            break;
                        case SwallowDestination.Pussy:
                            anchor = anim.GetBoneTransform(HumanBodyBones.Hips);
                            if (anchor != null) return anchor.TransformPoint(0f, -0.06f, 0.05f);
                            break;
                        case SwallowDestination.Nipple:
                            // Chest-level, forward — a breast anchor without bone-perfect
                            // offsets (the narration says which side anyway).
                            anchor = anim.GetBoneTransform(HumanBodyBones.Chest);
                            if (anchor != null) return anchor.TransformPoint(0f, 0.02f, 0.10f);
                            break;
                        case SwallowDestination.Dick:
                            // Cock vore enters through the EATER's own groin: anchor on
                            // the actual penetrator (the dick's base) when they have
                            // one, so the pull visibly lands on their equipment; a
                            // dickless eater (gained-dick pending, females) falls back
                            // to the hips-forward groin point.
                            try
                            {
                                var pen = eater.GetComponentInChildren<PenetrationTech.Penetrator>(true);
                                if (pen != null && pen.transform != null)
                                    return pen.transform.position;
                            }
                            catch (Exception) { }
                            anchor = anim.GetBoneTransform(HumanBodyBones.Hips);
                            if (anchor != null) return anchor.TransformPoint(0f, -0.02f, 0.08f);
                            break;
                    }
                    if (anchor != null) return anchor.position;
                }
            }
            catch (Exception) { }
            return BellyOf(eater); // bone fallback — pull straight to the belly
        }

        // COMPLETION for swallowed REMOTE PLAYERS: the body is NEVER destroyed (it
        // belongs to their client's session — destroying another player's avatar
        // can break their whole game). Release them at the eater's feet, return
        // their ownership, and announce the outcome to their mod.
        internal static bool ReleaseRemotePlayerVictim(SwallowState st, string outcome)
        {
            try
            {
                var v = st.Victim;
                RestoreVictimEquipment(st);
                var at = st.Eater != null
                    ? st.Eater.transform.position + st.Eater.transform.forward * 1.2f
                    : v.transform.position;
                RestoreVictimBody(v, at);
                try
                {
                    var pv = v.photonView;
                    if (pv != null && st.VictimOwnerActorNr > 0 && pv.OwnerActorNr != st.VictimOwnerActorNr)
                    {
                        var room = Photon.Pun.PhotonNetwork.CurrentRoom;
                        var ownerPl = room != null ? room.GetPlayer(st.VictimOwnerActorNr) : null;
                        if (ownerPl != null) pv.TransferOwnership(ownerPl);
                    }
                }
                catch (Exception) { }
                try { BroadcastEnd(st, outcome); } catch (Exception) { }
                try { LLMNPCPlugin.Log.LogInfo("swallow: REMOTE player '" + st.Name + "' released (" + outcome + ") — body returned to their client"); } catch (Exception) { }
                return true;
            }
            catch (Exception) { return false; }
        }

        // Completion gate: remote player victims never see the destroy paths.
        private static bool MaybeFinishRemoteVictim(SwallowState st, string outcome)
        {
            if (!IsRemotePlayerVictim(st)) return false;
            return ReleaseRemotePlayerVictim(st, outcome);
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
                    // grief guard: receivers can't be players — unless the player-
                    // vs-player vore permission is on AND the dropper is a player.
                    try { if (NPCInstance.IsPlayerKobold(k) && !(PlayerVictimsAllowed() && NPCInstance.IsPlayerKobold(dropper))) continue; } catch (Exception) { }
                    try { if (k.grabbed) continue; } catch (Exception) { } // being held themselves
                    if (EaterBusy(k)) continue;                   // already digesting someone
                    // FULL-INSERT RULE (replaces the bump-on-head check): the held
                    // body must be stuffed INTO the receiver's mouth region — its
                    // center below the maw point and within reach. Merely resting or
                    // bumping ON TOP of the head no longer triggers anything (that
                    // check started digestions from casual PrecisionGrabber bumps).
                    Vector3 mouthPos = MouthOf(k);
                    if (vPos.y > mouthPos.y + 0.12f) continue; // above the maw = not inserted
                    float d = Vector3.Distance(mouthPos, vPos);
                    if (d >= bestDist) continue;
                    bestDist = d;
                    best = k;
                }
            }
            catch (Exception) { return null; }
            return best;
        }

        // internal for the spectate manager (its Tick parks the player at the belly).
        // Is this kobold currently being digested (an active swallow session's
        // victim)? The victim's body parts ARE penetrator props — their dick
        // "enters" the eater's mouth on the way down — and the penetration tech
        // registered all of it as sex: the eater read the victim's penis as a
        // penetration, applied a rhythm, and both minds narrated "let us enjoy
        // this rhythm" about a digestion. Digestion is never sex.
        // Does this body belong to a pending REBIRTH (an egg that will hatch into a
        // person — a swallowed player or NPC)? Claim scans must SKIP these: the
        // hatch flow revives the person in the fresh body, and a second agent
        // possessing it mid-flow stole the rebirth (observed: "player didn't spawn
        // after the egg went all cycles — another agent took over the body").
        internal static bool IsAwaitingRebirth(Kobold k)
        {
            if (k == null || Plugin == null) return false;
            // HOT-PATH GUARD: the claim scan calls this per candidate per tick —
            // a fingerprint pass is work we must only pay while an egg is actually
            // pending (registry empty in every session without a swallowed body).
            lock (_lock)
            {
                if (_awaitingRebirth.Count == 0) return false;
            }
            try
            {
                string fp = NPCInstance.GenesFingerprint(k.GetGenes());
                if (string.IsNullOrEmpty(fp)) return false;
                lock (_lock) return _awaitingRebirth.ContainsKey(fp);
            }
            catch (Exception) { return false; }
        }

        internal static bool IsDigestingVictim(Kobold k)
        {
            if (k == null) return false;
            lock (_lock)
                foreach (var st in _active)
                    if (st != null && st.Victim == k) return true;
            return false;
        }

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
        // The EATER (the acting agent) sets the fate; their override rides here.
        private static string PlayerFateHint(Kobold eater)
        {
            switch (EffectFor(eater))
            {
                case SwallowEffect.BellyEgg: return "the egg reagent around them waits in your belly — nest-lay them and plant the egg";
                case SwallowEffect.Grow:
                case SwallowEffect.Destroy: return null;
                default: return "after conversion you lay an egg — plant it in soil and they hatch out of it";
            }
        }

        // The LLM-facing outcome summary for a successful eat_player result.
        internal static string PlayerOutcomeNote(Kobold eater)
        {
            switch (EffectFor(eater))
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
            string eaterKey = null;
            try { eaterKey = NPCInstance.BodyKey(eater); } catch (Exception) { }
            string who = BodyLabel(actor);
            string eaterLabel = BodyLabel(eater);
            bool selfEat = actor == eater;
            var holeDest = DestinationFor(actor); // per-agent override wins; global = player control
            string hole = SwallowLogic.DestinationLabel(holeDest).ToLowerInvariant();
            string entryVerb = SwallowLogic.EntryVerb(holeDest);

            // WHO ATE WHO (facts, both sides — the user-visible fix): the old narration
            // named the ACTOR (the dropper — usually "the player"), so a victim fed
            // into an NPC by any means remembered "Rosemary ate me". The records now
            // name the EATER as the one whose stomach they're in, with the dropper as
            // a secondary clause; both facts persist in their live rings and their
            // saved lives (and ride rebirth copies).
            try
            {
                if (npc != null)
                    npc.RememberFact("eaten:" + eaterLabel + " has me in their stomach — " + entryVerb
                        + (selfEat ? "" : " (" + who + " did the feeding)")
                        + " — this is a MEAL, not sex");
            }
            catch (Exception) { }
            try
            {
                if (st_EaterFactAllowed(eater))
                {
                    var eatNpc0 = Plugin.InstanceForBody(eater);
                    if (eatNpc0 != null)
                        eatNpc0.RememberFact("ate:" + (name ?? "someone") + " — I am digesting them whole"
                            + (selfEat ? "" : " (fed to me by " + who + ")")
                            + "; their fate follows this stomach's rules");
                }
            }
            catch (Exception) { }

            // Leave any station animation quietly — the station would fight our pin.
            try { victim.photonView.RPC("StopAnimationRPC", RpcTarget.All); } catch (Exception) { }

            var st = new SwallowState
            {
                Victim = victim,
                Eater = eater,
                Actor = actor,
                VictimId = vid,
                VictimOwnerActorNr = victim.photonView != null ? victim.photonView.OwnerActorNr : 0,
                Npc = npc,
                Name = name ?? "the kobold",
                OldBodyKey = oldKey,
                EaterKey = eaterKey,
                Hole = hole,
                Entry = holeDest,
                VictimIsPlayer = playerVictim,
                StartUnscaled = Time.unscaledTime,
                DigestSeconds = DigestSeconds(),
                PullStartPos = victim.transform.position,
                Milestone = -1,
            };
            lock (_lock) _active.Add(st);
            DisarmVictimPenetrators(st);
            SpawnVirtualPen(st);
            // MOD EVENT CHANNEL (multiplayer): a remote victim's mod learns of the
            // swallow (begin + digest lines + end) and can abort from THEIR machine.
            try { BroadcastBegin(st); } catch (Exception) { }
            try { st.EaterNpc = Plugin.InstanceForBody(eater); } catch (Exception) { st.EaterNpc = null; }
            // Mid-play swallow belt: if the victim was penetrating the eater when
            // swallowed, the eater's _pen entry never sees a "pull out" — purge
            // both sides of the false penetration so no rhythm survives the meal.
            try { if (npc != null) npc.PurgePenForVictim(victim); } catch (Exception) { }
            try { if (st.EaterNpc != null) st.EaterNpc.PurgePenForVictim(victim); } catch (Exception) { }

            // Player victims: if the eater was HOLDING them, let go first — the
            // grabber's per-frame anchor would otherwise fight the pull-in pin.
            if (playerVictim)
                ReleaseHeldVictim(eater, victim);

            if (npc != null)
            {
                try
                {
                    npc.Swallowed = true;
                    npc.PushWorldEvent(BeginPromiseText(eaterLabel, holeDest, EffectFor(actor)) + (selfEat ? "" : " (" + who + " did the feeding)"));
                    npc.PersistLifeForSwallow(); // snapshot BEFORE any end-of-digest destroy
                }
                catch (Exception) { }
            }

            if (playerVictim)
            {
                // The eater gets one honest first-person line; the world gets the
                // spectator line; and the PLAYER'S camera is taken over right now —
                // the pull-in starts this frame and they watch from outside.
                if (st.EaterNpc != null)
                {
                    try
                    {
                        st.EaterNpc.PushWorldEvent("you just " + entryVerb + " the PLAYER WHOLE — " + st.Name
                            + " is inside your " + SwallowLogic.InsideNoun(holeDest) + " now via your " + holeLabelFor(hole)
                            + " (a meal-like conversion, NOT sex): their camera is gone, they WATCH everything through your body's eyes and CANNOT act (about "
                            + Math.Ceiling(st.DigestSeconds + SwallowLogic.PullSeconds + SwallowLogic.SettleSeconds).ToString("0", CultureInfo.InvariantCulture)
                            + "s to conversion). " + PlayerFateHint(eater) + " Keep living your ordinary life — go to work, play, talk — and speak to them whenever; they hear you. They are a person in your care: never sell, abandon or hurt them.");
                    }
                    catch (Exception) { }
                }
                try { Plugin.FanoutWorldEventExcept("you watch " + eaterLabel + " " + entryVerb + " the PLAYER WHOLE — the player is inside them now, watching through their body's eyes.", st.EaterNpc, null); } catch (Exception) { }
                // (the shared tail below bubbles the eater's "You swallow …" line)
                try { SpectateManager.BeginForPlayer(victim, eater, eaterLabel, PlayerFateHint(eater)); } catch (Exception) { }
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
                        st.EaterNpc.PushWorldEvent("you just " + entryVerb + " " + st.Name
                             + " WHOLE — they are deep inside your " + SwallowLogic.InsideNoun(holeDest)
                             + " now (a meal-like conversion, not sex), digesting (about "
                            + Math.Ceiling(st.DigestSeconds + SwallowLogic.PullSeconds + SwallowLogic.SettleSeconds).ToString("0", CultureInfo.InvariantCulture)
                            + "s to conversion). Their fate follows this stomach's rules; you can speak while they convert.");
                    }
                    catch (Exception) { }
                }
                string inside = who == "the player" ? "them" : who + "'s " + SwallowLogic.InsideNoun(holeDest);
                try { Plugin.FanoutWorldEventExcept("you watch " + who + " " + entryVerb + " " + st.Name + " WHOLE — they vanished into " + inside, st.EaterNpc, npc); }
                catch (Exception) { }
            }
            else
            {
                string fedBy = who == "the player" ? "the player" : who;
                try { Plugin.FanoutWorldEventExcept("you watch " + fedBy + " shove " + st.Name + " against " + eaterLabel
                    + (hole == "mouth" ? "'s mouth" : " — " + eaterLabel + " " + entryVerb + " them!")
                    + " — " + eaterLabel + " has " + st.Name + " inside them now!", st.EaterNpc, npc); }
                catch (Exception) { }
                if (st.EaterNpc != null)
                {
                    try
                    {
                        st.EaterNpc.PushWorldEvent("someone force-fed you: " + fedBy + " just fed " + st.Name + " into you"
                            + (hole == "mouth" ? " through your mouth" : " through your " + holeLabelFor(hole))
                            + " and " + entryVerb + " — they are deep inside YOUR " + SwallowLogic.InsideNoun(holeDest) + " now, digesting. Their fate follows this stomach's rules; you can speak around them while they convert.");
                    }
                    catch (Exception) { }
                }
            }
            try
            {
                Bubble(eater, selfEat
                    ? ("You " + entryVerb + " " + st.Name + " whole" + (hole != "mouth" ? " (" + hole + ")" : "") + ".")
                    : ("You " + entryVerb + " " + st.Name + " whole" + (hole != "mouth" ? " (" + hole + ")" : "") + " — " + who + " dropped them right in."));
            }
            catch (Exception) { }

            try
            {
                LLMNPCPlugin.Log.LogInfo("swallow: '" + st.Name + "' eaten by " + eaterLabel
                    + (selfEat ? "" : " (force-fed by " + who + ")")
                    + " — entry: " + hole
                    + " (digest " + st.DigestSeconds.ToString("0", CultureInfo.InvariantCulture) + "s"
                    + ", body key '" + (st.OldBodyKey ?? "?") + "')");
            }
            catch (Exception) { }
            return true;
        }

        // The eater's "ate:" fact only makes sense while a mind drives the eater AND
        // it isn't the player's own body (the player has no fact ring).
        private static bool st_EaterFactAllowed(Kobold eater)
        {
            try { if (eater == null || NPCInstance.IsPlayerKobold(eater)) return false; } catch (Exception) { }
            return Plugin.InstanceForBody(eater) != null;
        }

        // "mouth" -> "mouth"; the other holes read naturally in "through your ...".
        private static string holeLabelFor(string hole)
        {
            if (hole == "anal") return "rear";
            if (hole == "pussy") return "pussy";
            if (hole == "nipple") return "breast";
            if (hole == "dick") return "cock";
            return "mouth";
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
        private static string BeginPromiseText(string who, SwallowDestination holeDest, SwallowEffect mode)
        {
            string noun = SwallowLogic.InsideNoun(holeDest);
            // Second-person throughout: this text is the VICTIM's own promise — the
            // old "they went in through X's mouth" let 'they' read as the eater, so
            // victims mis-narrated who did what to whom.
            string via = holeDest == SwallowDestination.Mouth
                ? " — you went in through " + who + "'s mouth"
                : " — you were taken in through " + who + "'s " + holeLabelFor(SwallowLogic.DestinationLabel(holeDest).ToLowerInvariant());
            switch (mode)
            {
                case SwallowEffect.BellyEgg:
                    return who + " swallowed you WHOLE — you are deep inside their " + noun + " now"
                        + via + " (a MEAL-like conversion — no sex is happening, the walls are just churning); you can still speak but you cannot move. You are slowly being converted into an EGG that will ride in " + who + "'s belly — when they sit on a nest and lay you, you will hatch out of it with new genes.";
                case SwallowEffect.Grow:
                {
                    var taken = new List<string>();
                    if (Plugin._cfgSwallowGrowLarger != null && Plugin._cfgSwallowGrowLarger.Value) taken.Add("your size");
                    if (Plugin._cfgSwallowGrowStrength != null && Plugin._cfgSwallowGrowStrength.Value) taken.Add("your strength");
                    if (Plugin._cfgSwallowGrowSex != null && Plugin._cfgSwallowGrowSex.Value) taken.Add("your curves (breasts, groin)");
                    string what = taken.Count > 0
                        ? "They are ABSORBING you — " + JoinEnglish(taken) + " " + (taken.Count == 1 ? "is" : "are") + " becoming theirs."
                        : "They are ABSORBING you — your whole body is becoming theirs.";
                    return who + " swallowed you WHOLE — you are deep inside their " + noun + " now" + via + " (a MEAL-like conversion — no sex is happening); you can still speak but you cannot move. " + what + " There is no coming back.";
                }
                case SwallowEffect.Destroy:
                    return who + " swallowed you WHOLE — you are deep inside their " + noun + " now" + via + " (a MEAL-like conversion — no sex is happening); you can still speak but you cannot move. They are consuming you entirely. There is no coming back.";
                default:
                    return who + " swallowed you WHOLE — you are deep inside their " + noun + " now" + via + " (a MEAL-like conversion — no sex is happening); you can still speak but you cannot move. You feel the walls around you shift and churn. You are slowly being converted into an EGG — when it's done, you will be reborn from it with new genes.";
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
                    try { SuppressVictimArousal(st); } catch (Exception) { }
                    try { Advance(st, i); }
                    catch (Exception e)
                    {
                        try { LLMNPCPlugin.Log.LogWarning("swallow tick (" + st.Name + "): " + e.Message); } catch (Exception) { }
                    }
                }
            }
        }

        // The VICTIM AS A LARGE PENIS: spawn a real penis prefab (the victim's own
        // equip when they have one, else the database's default), parented to the
        // victim so the pull literally drags it into the pred's hole, scaled off the
        // victim's cached body scale. GirthData comes pre-baked on the prefab; the
        // WORLD girth scales with the object — that is the "girth built off the
        // body's scale and cached" the design needs (BodyScale is captured once).
        // The pred's PenetrationTech treats it exactly like any penetrator: the
        // mouth/penetrable widens around it, and the pred's own listeners deliver
        // stimulation — the insert CAN feel sexual; the events are labeled so the
        // model always knows it is the swallowed meal, not a lover.
        private const string VorePenPrefix = "vore:";
        private static void SpawnVirtualPen(SwallowState st)
        {
            try
            {
                if (st == null || st.Victim == null || st.VirtualPen != null) return;
                st.BodyScale = 1f;
                try
                {
                    var sc = st.Victim.transform.localScale;
                    if (sc.x > 0.0001f) st.BodyScale = sc.x;
                }
                catch (Exception) { }
                GameObject prefab = null;
                try
                {
                    var vg = st.Victim.GetGenes();
                    short equip = vg != null ? vg.dickEquip : (short)0;
                    var db = GameManager.GetPenisDatabase().GetValidPrefabReferenceInfos();
                    if (db != null && db.Count > 0)
                    {
                        int idx = (equip >= 1 && equip <= db.Count) ? equip - 1 : 0;
                        prefab = db[idx].GetPrefab();
                    }
                }
                catch (Exception) { prefab = null; }
                if (prefab == null) { st.BodyScale = -1f; return; }
                var pen = UnityEngine.Object.Instantiate(prefab, st.Victim.transform, false);
                pen.name = VorePenPrefix + (st.Name ?? "someone");
                pen.transform.localPosition = Vector3.zero;
                pen.transform.localRotation = Quaternion.identity;
                pen.transform.localScale = Vector3.one; // parent (the victim root) carries the body scale
                st.VirtualPen = pen;
                try { LLMNPCPlugin.Log.LogInfo("vore-pen: virtual penetrator spawned for '" + st.Name + "' (cached body scale " + st.BodyScale.ToString("0.###", CultureInfo.InvariantCulture) + ") — the pred's orifice widens via PenetrationTech"); } catch (Exception) { }
                HardenPen(st, pen);
            }
            catch (Exception) { }
        }

        // PEN HARDENING — the "fed the person they were eating, then both got
        // launched into the sky" glitch. The PenisDatabase prefabs (vanilla and
        // modded alike) ship `autoPenetrate = AutoSeek|AutoDecouple` with an EMPTY
        // ignorePenetrables list: the vanilla equip flow (DickDescriptor.AttachTo)
        // fills the owner's own holes in at runtime, which a raw instantiate never
        // does. With a BODY-scale shaft the AutoSeek sphere covers the pred's whole
        // upper body, so the shaft locks whatever hole it "touches just right" —
        // the pred's other orifices, the victim's own, a bystander's — and every
        // lock is a REAL Penetrate(): genuine second-holes for the victim-as-shaft,
        // re-lock/decouple loops fighting the per-tick pull pin. The pair jettisons
        // and PlayAreaEnforcer (5s cadence) PhotonNetwork.Destroys every own-View
        // body once it leaves the map bounds — "moved up into space and deleted".
        //
        // The hardened shaft is a SOLO penetrator: seek off, the ONE intended hole
        // forced via Penetrate(), everything else in reach on the ignore list, and
        // any item machinery (modded pens can be physics items) stripped. Main
        // thread only (Begin guarantees it).
        private static void HardenPen(SwallowState st, GameObject pen)
        {
            if (pen == null) return;
            var pens = new List<PenetrationTech.Penetrator>();
            try { foreach (var pr in pen.GetComponentsInChildren<PenetrationTech.Penetrator>(true)) if (pr != null) pens.Add(pr); }
            catch (Exception) { }
            StripPenItemMachinery(pen);
            foreach (var pr in pens) SetPenAutoMode(pr, 0); // PenetrationTech AutoPenetrateMode.None
            var intended = IntendedVoreHole(st);
            if (intended != null)
            {
                FillVorePenIgnores(st, pens, intended);
                foreach (var pr in pens)
                {
                    try { pr.Penetrate(intended); }
                    catch (Exception e) { try { LLMNPCPlugin.Log.LogWarning("vore-pen force-insert: " + e.Message); } catch (Exception) { } }
                }
            }
            else
            {
                // No matching body hole (the DICK route — no urethral Penetrable
                // exists anywhere): the shaft stays UNINSERTED — pure geometry at
                // the pred's own groin, narrated by the intake lines. That route
                // previously had to autoseek "whatever it touched", which WAS the
                // misfire.
                try { LLMNPCPlugin.Log.LogInfo("vore-pen: no matching body hole for route '" + st.Hole + "' — shaft left uninserted (narration-only)"); } catch (Exception) { }
            }
        }

        // Penetrator.autoPenetrate is a private flags enum (None=0, AutoSeek=1,
        // AutoDecouple=2). False (renamed/missing field) = leave everything as
        // shipped; the ignore-fill in HardenPen is the fallback restraint.
        private static FieldInfo _penAutoField;
        private static bool SetPenAutoMode(PenetrationTech.Penetrator pr, int mode)
        {
            try
            {
                if (pr == null) return false;
                if (_penAutoField == null)
                    _penAutoField = typeof(PenetrationTech.Penetrator).GetField("autoPenetrate", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_penAutoField == null) return false;
                _penAutoField.SetValue(pr, Enum.ToObject(_penAutoField.FieldType, mode));
                return true;
            }
            catch (Exception) { return false; }
        }
        private static bool SetPenAutoModeValue(PenetrationTech.Penetrator pr, object value)
        {
            try
            {
                if (pr == null || value == null) return false;
                if (_penAutoField == null)
                    _penAutoField = typeof(PenetrationTech.Penetrator).GetField("autoPenetrate", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_penAutoField == null) return false;
                _penAutoField.SetValue(pr, value);
                return true;
            }
            catch (Exception) { return false; }
        }
        private static object PenAutoModeOf(PenetrationTech.Penetrator pr)
        {
            try
            {
                if (pr == null) return null;
                if (_penAutoField == null)
                    _penAutoField = typeof(PenetrationTech.Penetrator).GetField("autoPenetrate", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_penAutoField == null) return null;
                return _penAutoField.GetValue(pr);
            }
            catch (Exception) { return null; }
        }

        // Modded PenisDatabase entries may be full physics items (rigidbody,
        // colliders, photon views) — vanilla entries are static. Inside a pinned,
        // per-tick-teleported body, live physics items fight the pin and their own
        // rig: the jettison engine. The shaft needs only its deform machinery, so
        // joints go, photon plumbing goes, colliders go, and any rigidbody is
        // frozen (its listener forces are no-ops on a kinematic body).
        private static void StripPenItemMachinery(GameObject pen)
        {
            try
            {
                foreach (var j in pen.GetComponentsInChildren<Joint>(true)) UnityEngine.Object.Destroy(j);
            }
            catch (Exception) { }
            try
            {
                foreach (var pv in pen.GetComponentsInChildren<PhotonView>(true)) UnityEngine.Object.Destroy(pv);
            }
            catch (Exception) { }
            try
            {
                foreach (var s in pen.GetComponentsInChildren<SmoothRigidbodyPhoton>(true)) UnityEngine.Object.Destroy(s);
            }
            catch (Exception) { }
            try
            {
                foreach (var h in pen.GetComponentsInChildren<PenetratorPhotonHelper>(true)) UnityEngine.Object.Destroy(h);
            }
            catch (Exception) { }
            try
            {
                foreach (var rb in pen.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (rb == null) continue;
                    try { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.detectCollisions = false; rb.useGravity = false; rb.isKinematic = true; }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            try
            {
                foreach (var c in pen.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(c);
            }
            catch (Exception) { }
            // Force-aim listeners drive the rigidbody toward the hole; their body is
            // inert now. Drop them so their FixedUpdate can't fight the pin.
            try
            {
                foreach (var pr in pen.GetComponentsInChildren<PenetrationTech.Penetrator>(true))
                {
                    if (pr == null || pr.listeners == null) continue;
                    for (int i = pr.listeners.Count - 1; i >= 0; i--)
                    {
                        var l = pr.listeners[i];
                        if (l == null) continue;
                        if (l is PenetrationTech.PenetratorRigidbodyListener || l is PenetrationTech.PenetratorNoCollideListener)
                            pr.listeners.RemoveAt(i);
                    }
                }
            }
            catch (Exception) { }
        }

        // The ONE hole the shaft may live in: the pred's own penetrable matching the
        // route label (mouth/anal/pussy/nipple). The DICK route returns null — no
        // urethral body hole exists in the game, so nothing is inserted.
        private static PenetrationTech.Penetrable IntendedVoreHole(SwallowState st)
        {
            string want;
            string hole = st != null ? st.Hole : null;
            if (hole == "mouth") want = "mouth";
            else if (hole == "anal") want = "ass";
            else if (hole == "pussy") want = "vagina";
            else if (hole == "nipple") want = "chest";
            else return null;
            try
            {
                var eater = st.Eater;
                if (eater == null) return null;
                foreach (var p in eater.GetComponentsInChildren<PenetrationTech.Penetrable>(true))
                {
                    if (p == null) continue;
                    bool active = true;
                    try { active = p.gameObject != null && p.gameObject.activeInHierarchy; } catch (Exception) { }
                    if (!active) continue;
                    if (PenetrableHoleWord(p) == want) return p;
                }
            }
            catch (Exception) { }
            return null;
        }

        // Same word rules as the body classifier (hole labels the narration uses).
        private static string PenetrableHoleWord(PenetrationTech.Penetrable pen)
        {
            try
            {
                string n = (pen.gameObject != null ? pen.gameObject.name : "").ToLowerInvariant();
                if (n.Contains("mouth") || n.Contains("face") || n.Contains("head")) return "mouth";
                if (n.Contains("vag") || n.Contains("crotch")) return "vagina";
                if (n.Contains("anus") || n.Contains("ass") || n.Contains("butt") || n.Contains("rear")) return "ass";
                if (n.Contains("breast") || n.Contains("chest")) return "chest";
                return n.Length > 0 ? n : "hole";
            }
            catch (Exception) { return "hole"; }
        }

        // The meal's own holes and the pred's remaining ones can never be seeked by
        // the vore shaft, plus everything else AutoSeek could see in reach
        // (bystanders, stuffing stations) — the same layer scan the seek itself uses.
        private static readonly Collider[] _penScan = new Collider[64];
        private static void FillVorePenIgnores(SwallowState st, List<PenetrationTech.Penetrator> pens, PenetrationTech.Penetrable intended)
        {
            if (pens == null || pens.Count == 0) return;
            try
            {
                if (st.Victim != null)
                    foreach (var p in st.Victim.GetComponentsInChildren<PenetrationTech.Penetrable>(true))
                    {
                        if (p == null) continue;
                        foreach (var pr in pens) try { pr.AddIgnorePenetrable(p); } catch (Exception) { }
                    }
            }
            catch (Exception) { }
            try
            {
                if (st.Eater != null)
                    foreach (var p in st.Eater.GetComponentsInChildren<PenetrationTech.Penetrable>(true))
                    {
                        if (p == null || p == intended) continue;
                        foreach (var pr in pens) try { pr.AddIgnorePenetrable(p); } catch (Exception) { }
                    }
            }
            catch (Exception) { }
            try
            {
                Vector3 at = pens[0].transform != null ? pens[0].transform.position : (st.Victim != null ? st.Victim.transform.position : Vector3.zero);
                int hits = Physics.OverlapSphereNonAlloc(at, 8f, _penScan, PenetrationTech.PenetrationTechTools.GetPenetrableMask(), QueryTriggerInteraction.Collide);
                for (int i = 0; i < hits; i++)
                {
                    var col = _penScan[i];
                    if (col == null) continue;
                    PenetrationTech.Penetrable p = null;
                    try
                    {
                        var po = col.GetComponent<PenetrationTech.PenetrableOwner>();
                        if (po != null) p = po.owner;
                    }
                    catch (Exception) { }
                    if (p == null)
                    {
                        try { p = col.GetComponentInParent<PenetrationTech.Penetrable>(); } catch (Exception) { p = null; }
                    }
                    if (p == null || p == intended) continue;
                    foreach (var pr in pens)
                        try { pr.AddIgnorePenetrable(p); } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        // The VICTIM'S OWN equipment rides along: live AutoSeek'd penetrators right
        // at the pred — their real part autolocking the pred's other holes "just
        // right" mid-pull was the classic misfire (PenetrationTech machinery ran
        // even where our narration gate kept quiet). Inert for the session; every
        // release path restores them.
        private static void DisarmVictimPenetrators(SwallowState st)
        {
            try
            {
                Kobold victim = st != null ? st.Victim : null;
                if (victim == null) return;
                foreach (var pr in victim.GetComponentsInChildren<PenetrationTech.Penetrator>(true))
                {
                    if (pr == null) continue;
                    try
                    {
                        object old = PenAutoModeOf(pr);
                        if (st.DisarmedPens == null) st.DisarmedPens = new List<DisarmedPen>();
                        st.DisarmedPens.Add(new DisarmedPen { Pen = pr, OldMode = old });
                        SetPenAutoMode(pr, 0);
                        pr.Penetrate(null); // drop any current lock; the meal is coming out of every hole
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        // Restore the values captured by DisarmVictimPenetrators (release paths).
        private static void RestoreVictimEquipment(SwallowState st)
        {
            try
            {
                if (st == null || st.DisarmedPens == null) return;
                foreach (var d in st.DisarmedPens)
                {
                    if (d == null || d.Pen == null) continue;
                    try { SetPenAutoModeValue(d.Pen, d.OldMode); } catch (Exception) { }
                }
                st.DisarmedPens.Clear();
            }
            catch (Exception) { }
        }

        // True when this "vore:"-named shaft is precisely THIS body's own swallow
        // (the pred). Everything else it touches — another hole, another body — is
        // meal machinery that must never register as a partner.
        internal static bool IsVoreInsertFor(Kobold eater, string penGoName)
        {
            if (eater == null || penGoName == null) return false;
            lock (_lock)
            {
                for (int i = 0; i < _active.Count; i++)
                {
                    var s = _active[i];
                    if (s == null || s.Eater != eater || s.VirtualPen == null) continue;
                    string nm = null;
                    try { nm = s.VirtualPen != null ? s.VirtualPen.name : null; } catch (Exception) { nm = null; }
                    if (nm != null && nm == penGoName) return true;
                }
            }
            return false;
        }

        private static void DestroyVirtualPen(SwallowState st)
        {
            try { if (st != null && st.VirtualPen != null) UnityEngine.Object.Destroy(st.VirtualPen); } catch (Exception) { }
            if (st != null) st.VirtualPen = null;
        }

        // AROUSAL SPLIT — victim side: a swallowed victim's own machinery used to
        // run until the Cum RPC fired an accidental cumshot that sprayed particles
        // THROUGH the eater. Every tick we zero the victim's stimulation + private
        // arousal so the only cum inside someone is DELIBERATE (`masturbate`).
        // The PRED side is deliberately NOT clamped (victim-as-penis edit): their
        // body registers the vore insert like any penetrator, so the stretch can
        // feel sexual in context — the events say it is a vore insert.
        private static FieldInfo _arousalField;
        private static void SuppressVictimArousal(SwallowState st)
        {
            var v = st.Victim;
            if (v == null || st.Surrendered == false) { } // (flag kept for future nuance)
            try
            {
                var pv = v.photonView;
                if (pv != null && !pv.IsMine) return; // remote victim keeps their own machinery; narration rides the mod channel
                v.stimulation = 0f;
                try
                {
                    if (_arousalField == null)
                        _arousalField = typeof(Kobold).GetField("arousal", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_arousalField != null) _arousalField.SetValue(v, 0f);
                }
                catch (Exception) { }
            }
            catch (Exception) { }
        }

        // In-body commands (victim side, main thread). Each no-ops politely when
        // nobody is actually inside anyone.
        private static SwallowState StateForVictim(Kobold victim)
        {
            try
            {
                int vid = victim.GetInstanceID();
                foreach (var st in _active)
                    if (!st.EggSpawned && st.VictimId == vid) return st;
            }
            catch (Exception) { }
            return null;
        }

        // `masturbate` (also aliased cum): the DELIBERATE in-body climax. The
        // passive one is suppressed (see SuppressVictimArousal) — this fires the
        // game's own Cum RPC under our control AND lands Cum reagent in the pred's
        // belly container with the victim's genes riding along, narrated to both.
        internal static bool IntentionalCumInside(Kobold victim)
        {
            if (victim == null) return false;
            var st = StateForVictim(victim);
            if (st == null) return false;
            try
            {
                if (victim.photonView != null)
                    victim.photonView.RPC("Cum", RpcTarget.All);
                try
                {
                    var belly = st.Eater != null ? st.Eater.bellyContainer : null;
                    var bellyPv = belly != null ? belly.GetComponent<PhotonView>() : null;
                    if (belly != null && bellyPv != null && ReagentDatabase.TryGetAsset("Cum", out var cum) && cum != null)
                    {
                        var contents = new ReagentContents();
                        contents.AddMix(cum.GetReagent(25f));
                        var buf = new NetStack.Serialization.BitBuffer(4);
                        buf.AddReagentContents(contents);
                        if (victim.photonView != null)
                            bellyPv.RPC(nameof(GenericReagentContainer.ForceMixRPC), RpcTarget.All, buf,
                                victim.photonView.ViewID, (byte)GenericReagentContainer.InjectType.Inject);
                    }
                }
                catch (Exception) { }
                string inside = InsideLine(st);
                try { if (st.EaterNpc != null) st.EaterNpc.PushWorldEvent(inside + " — the person inside you deliberately cums: a warm burst goes off deep in your " + InsideNoun(st) + ", pooling inside."); } catch (Exception) { }
                try { if (st.Npc != null) st.Npc.PushWorldEvent("you cum DELIBERATELY, deep inside " + BodyLabel(st.Eater) + " — warm, deliberate, yours; it pools in their " + InsideNoun(st) + "."); } catch (Exception) { }
                try { Plugin.FanoutWorldEventExcept("you hear a muffled, deliberate climax from inside " + BodyLabel(st.Eater) + " — someone cums deep within them.", st.EaterNpc, st.Npc); } catch (Exception) { }
                return true;
            }
            catch (Exception) { return false; }
        }

        // `kick`: the victim squirms deliberately — the pred FEELS it and their
        // model is told; cooldown keeps it from becoming spam.
        internal static bool KickInside(Kobold victim)
        {
            if (victim == null) return false;
            var st = StateForVictim(victim);
            if (st == null) return false;
            float now = Time.unscaledTime;
            if (now - st.LastKick < 5f) return true; // quietly throttled
            st.LastKick = now;
            try
            {
                if (st.EaterNpc != null)
                {
                    string[] kicks = {
                        "something inside your " + InsideNoun(st) + " KICKS — " + st.Name + " squirms hard from within",
                        "a strong jolt from inside: " + st.Name + " kicked against your gut",
                        "movement inside you — " + st.Name + " squirms and presses outward",
                    };
                    st.EaterNpc.PushWorldEvent("[kick] " + kicks[UnityEngine.Random.Range(0, kicks.Length)] + ".");
                }
                if (st.Npc != null)
                    st.Npc.PushWorldEvent("[kick] you kick hard inside " + BodyLabel(st.Eater) + " — you can feel it land; they will notice.");
                // Bystanders sense the squirm too.
                try { Plugin.FanoutWorldEventExcept("[kick] something in " + BodyLabel(st.Eater) + " visibly squirms — a kick from within.", st.EaterNpc, st.Npc); } catch (Exception) { }
            }
            catch (Exception) { }
            return true;
        }

        // `surrender`: the victim goes limp and accepting; the pred's mind learns
        // the ride stopped fighting.
        internal static bool SurrenderInside(Kobold victim)
        {
            if (victim == null) return false;
            var st = StateForVictim(victim);
            if (st == null) return false;
            if (st.Surrendered) return true;
            st.Surrendered = true;
            try
            {
                if (st.Npc != null)
                    st.Npc.PushWorldEvent("[surrender] you go limp inside " + BodyLabel(st.Eater) + " — no more kicking, just settling into the warmth and the conversion.");
                if (st.EaterNpc != null)
                    st.EaterNpc.PushWorldEvent("[surrender] the one in your " + InsideNoun(st) + " has gone LIMP — " + st.Name + " surrendered; the squirming stops, only the conversion's rhythm is left.");
                try { Plugin.FanoutWorldEventExcept("[surrender] the squirming inside " + BodyLabel(st.Eater) + " stops — " + st.Name + " has gone limp within them.", st.EaterNpc, st.Npc); } catch (Exception) { }
            }
            catch (Exception) { }
            return true;
        }

        // Hole-aware inside wording: "stomach" only for a mouth entry — every other
        // hole holds the person deep inside the body (the models kept recasting
        // unbirths/cock-vore as "a meal in my stomach").
        private static string InsideNoun(SwallowState st)
        {
            return SwallowLogic.InsideNoun(st.Entry);
        }
        private static string InsideLine(SwallowState st)
        {
            return st.Name + " is deep inside your " + InsideNoun(st)
                + (st.Entry == SwallowDestination.Mouth ? " (they went in through your mouth)" : " (taken in through your " + holeLabelFor(st.Hole) + ")")
                + " — " + st.Name + ", a whole person, converting";
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
                DestroyVirtualPen(st);
                _active.RemoveAt(index);
                return;
            }

            float t = Time.unscaledTime - st.StartUnscaled;
            int phase = SwallowLogic.PhaseFor(SwallowLogic.PullSeconds, st.DigestSeconds, SwallowLogic.SettleSeconds, t);
            float phaseT = SwallowLogic.PhaseT(SwallowLogic.PullSeconds, st.DigestSeconds, SwallowLogic.SettleSeconds, t);

            // LAUNCH BRAKE: nothing in the pin/pull escalates vertically, so a fast,
            // sustained climb of the pred is a physics/animation break carrying the
            // pair out of the world. End the session NOW and put the victim back
            // down — PlayAreaEnforcer would otherwise PhotonNetwork.Destroy both
            // bodies once they clear the map bounds ("deleted once reaching a
            // certain point"). Two-plus m/s of climbing sustained a quarter second
            // trips it; walking up terrain and single hops decay back out.
            bool launched = false;
            try
            {
                float ey = st.Eater != null ? st.Eater.transform.position.y : 0f;
                float nt = Time.unscaledTime;
                if (st.LastWatchT > 0f && nt - st.LastWatchT > 0.01f)
                {
                    float upRate = (ey - st.LastWatchY) / (nt - st.LastWatchT);
                    if (upRate > 2.5f) st.LaunchTicks++;
                    else st.LaunchTicks = Mathf.Max(0, st.LaunchTicks - 2);
                    if (st.LaunchTicks >= 15) launched = true;
                }
                st.LastWatchY = ey;
                st.LastWatchT = nt;
            }
            catch (Exception) { }
            if (launched)
            {
                try { LLMNPCPlugin.Log.LogWarning("swallow launch brake: pair climbing out of the world — session '" + st.Name + "' released"); } catch (Exception) { }
                try { if (st.Npc != null) st.Npc.PushWorldEvent("the swallow came apart: the two of you were being flung through the world together. You were put down before it could carry you both away."); } catch (Exception) { }
                try { if (st.EaterNpc != null) st.EaterNpc.PushWorldEvent("the vore went wrong — you and " + st.Name + " were being flung out of the world together. You dropped them at once; nothing more was swallowed."); } catch (Exception) { }
                DestroyVirtualPen(st);
                RestoreVictimEquipment(st);
                try
                {
                    var eb = st.Eater != null ? st.Eater.body : null;
                    if (eb != null && !eb.isKinematic) { eb.velocity = Vector3.zero; eb.angularVelocity = Vector3.zero; }
                }
                catch (Exception) { }
                try { RestoreVictimBody(st.Victim, st.Eater != null ? st.Eater.transform.position : st.Victim.transform.position); } catch (Exception) { }
                if (st.Npc != null) try { st.Npc.Swallowed = false; } catch (Exception) { }
                _active.RemoveAt(index);
                return;
            }

            PinVictim(st);
            HideOnceSwallowed(st, phase);

            if (phase == 0)
            {
                // Pull toward the ARMED entry hole (mouth by default; F7/WebUI can
                // route through the rear/pussy/dick/breast instead — same outcome).
                Vector3 holePos = HolePos(st.Eater, st.Entry);
                float k = SwallowLogic.Smooth01(phaseT);
                st.Victim.transform.position = Vector3.Lerp(st.PullStartPos, holePos, k);
                // VIRTUAL-INSERTION PROGRESS: the held body "goes down fully" — both
                // minds get 25/50/75% checkpoints ("can be checked for full insert";
                // the FULLY-IN edge is narrated at the phase handoff below).
                int step = (int)(k * 4f); // 0..3
                if (step > st.InsertNotified && phaseT < 1f)
                {
                    st.InsertNotified = step;
                    int pct = Mathf.RoundToInt(k * 100f);
                    string stage = SwallowLogic.InsertStageText(k);
                    try
                    {
                        if (st.EaterNpc != null)
                            st.EaterNpc.PushWorldEvent("[intake] " + st.Name + " is " + pct.ToString("0", CultureInfo.InvariantCulture)
                                + "% of the way into your " + holeLabelFor(st.Hole) + " — " + stage + "; the conversion starts when they are FULLY in. Their body acts as a large penis as it slides — your orifice widens around it and the stretch can feel genuinely good; that is the insert, not a lover.");
                        if (st.Npc != null)
                            st.Npc.PushWorldEvent("[intake] you are " + pct.ToString("0", CultureInfo.InvariantCulture)
                                + "% of the way " + (st.Entry == SwallowDestination.Mouth ? "down the throat" : "into the body")
                                + " — " + stage + " (" + BodyLabel(st.Eater) + "'s " + holeLabelFor(st.Hole) + ").");
                    }
                    catch (Exception) { }
                }
            }
            else
            {
                st.Victim.transform.position = BellyOf(st.Eater);
            }
            float scale = SwallowLogic.ScaleFor(phase, phaseT);
            st.Victim.transform.localScale = new Vector3(scale, scale, scale);

            if (phase == 1)
            {
                // FULL-INSERT edge (pull → digest): the body went down all the way —
                // the conversion begins. One line to each mind, hole-aware.
                if (st.LastPhase == 0 && !st.InsertFullNar)
                {
                    st.InsertFullNar = true;
                    try
                    {
                        if (st.EaterNpc != null)
                            st.EaterNpc.PushWorldEvent("[intake] " + st.Name + " slides FULLY in — the conversion begins; you can feel them settling deep in your " + SwallowLogic.InsideNoun(st.Entry) + ", stretched wide around their body-shaft (you may savor the fullness — it is the meal, it is not a partner).");
                        if (st.Npc != null)
                            st.Npc.PushWorldEvent("[intake] you slide FULLY in — deep inside " + BodyLabel(st.Eater) + "'s " + SwallowLogic.InsideNoun(st.Entry) + "; the conversion begins.");
                    }
                    catch (Exception) { }
                }
                // RECURRENT STATUS: both minds get a numbered conversion heartbeat
                // (~10s) so neither model has to guess where the process stands.
                float nowT = Time.unscaledTime;
                if (nowT - st.LastProgressEvent < 0f || nowT - st.LastProgressEvent > 10f)
                {
                    st.LastProgressEvent = nowT;
                    float totalSec = SwallowLogic.PullSeconds + st.DigestSeconds + SwallowLogic.SettleSeconds;
                    float leftS = Mathf.Max(0f, totalSec - (nowT - st.StartUnscaled));
                    float pct = Mathf.Clamp01((nowT - st.StartUnscaled) / totalSec);
                    int pctI = Mathf.RoundToInt(pct * 100f);
                    try
                    {
                        string where = "deep inside " + BodyLabel(st.Eater) + "'s " + SwallowLogic.InsideNoun(st.Entry)
                            + (st.Entry == SwallowDestination.Mouth ? "" : " (entered through the " + holeLabelFor(st.Hole) + ")");
                        if (st.Npc != null)
                            st.Npc.PushWorldEvent("[conversion] you are " + where + " — " + pctI.ToString("0", CultureInfo.InvariantCulture) + "% through the conversion (about " + Mathf.CeilToInt(leftS).ToString("0", CultureInfo.InvariantCulture) + "s left). No sex is happening; your body's urges are being held off until it finishes.");
                        if (st.EaterNpc != null)
                            st.EaterNpc.PushWorldEvent("[conversion] " + st.Name + " is " + where.Replace("deep inside " + BodyLabel(st.Eater) + "'s", "deep inside your") + " — " + pctI.ToString("0", CultureInfo.InvariantCulture) + "% through the conversion (about " + Mathf.CeilToInt(leftS).ToString("0", CultureInfo.InvariantCulture) + "s left). The insert inside you may feel sexual as it shifts — that's the meal's body doing its job; react however feels right");
                    }
                    catch (Exception) { }
                }
                int mark = SwallowLogic.MilestoneFor(phaseT, st.Milestone);
                if (mark > st.Milestone)
                {
                    st.Milestone = mark;
                    if (st.Npc != null)
                    {
                        try
                        {
                            if (mark == SwallowLogic.MarkChurn)
                                st.Npc.PushWorldEvent("the walls around you churn and press — warm, rhythmic, patient");
                            else if (mark == SwallowLogic.MarkKnead)
                                st.Npc.PushWorldEvent("the walls knead you steadily — your shape is starting to change, limbs going soft and smooth");
                            else if (mark == SwallowLogic.MarkEggForming)
                            {
                                SwallowEffect mode = EffectAt(st);
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
                            ? "your " + SwallowLogic.InsideNoun(st.Entry) + " churns around " + st.Name + " — they are in there, alive"
                            : "your " + SwallowLogic.InsideNoun(st.Entry) + " kneads " + st.Name + " steadily — the conversion is underway";
                        try { st.EaterNpc.PushWorldEvent(feeling + " (about " + Mathf.CeilToInt(leftS) + "s left; their fate follows this stomach's rules)"); }
                        catch (Exception) { }
                    }
                    // MOD EVENT CHANNEL: remote victims (players with the mod elsewhere)
                    // get the same digest-lines on THEIR machine.
                    try { MaybeForwardLine(st, "the digestion continues (" + mark + "/3): " + (st.Eater != null ? BodyLabel(st.Eater) : "the eater") + "'s stomach works around you"); } catch (Exception) { }
                }
            }

            st.LastPhase = phase;
            if (phase >= 0) return;

            // Done converting: apply the chosen effect, then remove the body.
            if (Consume(st, EffectAt(st)))
            {
                st.EggSpawned = true; // "handled" — remove the session
                DestroyVirtualPen(st);
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
                    DestroyVirtualPen(st);
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
            RestoreVictimEquipment(st);
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
        // Completion dispatch: apply the CHOSEN effect (the actor's per-agent
        // setting, else the operator's global) to the fully-digested victim. Every
        // mode ends by destroying the victim body (the swallow snapshotted their
        // persona at Begin AND again here-before-destroy).
        // Returning false keeps the victim pinned for a retry tick.
        // ------------------------------------------------------------------
        private static bool Consume(SwallowState st, SwallowEffect mode)
        {
            // A real swallow is the only sating event for the vore drive.
            try
            {
                var eatNpc = st != null && st.Eater != null ? Plugin.InstanceForBody(st.Eater) : null;
                if (eatNpc != null) eatNpc.VoreAppetiteSated();
            }
            catch (Exception) { }
            switch (mode)
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

                // A live LLM eater? Inject the memories into their session NOW — as a
                // COMPACTED summary (one fact line), not a verbatim flood: the fact
                // ring is a budget, a digested person's whole raw list used to evict
                // the eater's own knowledge. Their own teardown persists the merged
                // life to disk.
                var eatNpc = Plugin.InstanceForBody(st.Eater);
                if (eatNpc != null)
                {
                    try
                    {
                        eatNpc.PushWorldEvent("[absorb] " + st.Name + " was fully digested by you — their life poured in: " + facts.Count
                            + " of their MEMORIES are yours now (compacted into one absorbed: line — read your facts). Places/things they knew, you know. Mention or use them naturally.");
                        eatNpc.AbsorbVictimFacts(st.Name, facts);
                    }
                    catch (Exception e) { LLMNPCPlugin.Log.LogWarning("swallow absorb live: " + e.Message); }
                    // SKILL TRANSFER: the digested person's procedures pour in as
                    // well — muscle memory goes to whoever absorbed them.
                    try { if (rec.Skills != null && rec.Skills.Count > 0) eatNpc.SkillsAbsorbFrom(st.Name, rec.Skills); } catch (Exception) { }
                }

                // And mirror the merged life under the EATER's own body key, so an
                // eater we're not driving right now still wakes up remembering:
                // the eater's own facts win, the victim's life rides along COMPACTED
                // (MemoryCompaction) instead of appended raw.
                try
                {
                    var eaterKey = st.EaterKey != null ? st.EaterKey : NPCInstance.BodyKey(st.Eater);
                    if (string.IsNullOrEmpty(eaterKey))
                    {
                        MemoryStore.Delete(st.OldBodyKey); // absorbed but no eater file to mirror — the person is still consumed
                        return;
                    }
                    string eaterName = eatNpc != null ? eatNpc.GetMyName() : NPCInstance.CleanName(st.Eater.name);
                    var eatRec = MemoryStore.Load(eaterKey);
                    var ownFacts = eatRec != null && eatRec.Facts != null ? eatRec.Facts : new List<string>();
                    var merged = MemoryCompaction.MergeLife(ownFacts, st.Name, facts);
                    if (eatRec != null)
                        MemoryStore.Save(eaterKey, eaterName ?? eatRec.Name, eatRec.Persona, merged, eatRec.Goal, eatRec.Pronouns,
                            skills: eatRec.Skills, bodyFp: eatRec.BodyFp, species: eatRec.Species,
                            voreTarget: eatRec.VoreTarget, voreMode: eatRec.VoreMode); // keep the eater's whole record shape (prefs+skills+identity included)
                    else
                        MemoryStore.Save(eaterKey, NPCInstance.CleanName(st.Eater.name), null, merged, null, null);
                }
                catch (Exception e) { LLMNPCPlugin.Log.LogWarning("swallow absorb persist: " + e.Message); }
                // The person is consumed — their knowledge now lives under the eater's
                // key. The victim's own file must NOT survive the meal (it used to:
                // Grow absorbed the facts but left the origin file, and a later body
                // with the same serialized object name would wake up as a ghost of
                // someone the world saw eaten). Same end state as Destroy; Grow just
                // reads the records first.
                try
                {
                    MemoryStore.Delete(st.OldBodyKey);
                    LLMNPCPlugin.Log.LogInfo("swallow: '" + st.Name + "' absorbed — their saved life moved into the eater and their record was erased");
                }
                catch (Exception e) { LLMNPCPlugin.Log.LogWarning("swallow absorb delete: " + e.Message); }
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
                            _bellyGeneticOwners[victimFp] = new RebirthLink { EaterLabel = BodyLabel(st.Eater), EaterKey = st.EaterKey, IsPlayer = true };
                            _bellyNames[victimFp] = st.Name;
                        }
                        SpectateManager.MarkEggLaid(st.Victim, victimFp, "the nest lay + plant");
                    }
                    else if (!string.IsNullOrEmpty(st.OldBodyKey))
                    {
                        lock (_lock)
                        {
                            _bellyGeneticOwners[victimFp] = new RebirthLink { OldKey = st.OldBodyKey, EaterLabel = BodyLabel(st.Eater), EaterKey = st.EaterKey };
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
            if (MaybeFinishRemoteVictim(st, "released after the egg conversion")) return true;
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
                // A freshly-gained penis spawns as a live penetrator at the crotch;
                // without a purge the first frames registered it against the OWNER'S
                // own penetrables ("a phantom is fucking me", begging not to stop).
                try
                {
                    var grewNpc = Plugin.InstanceForBody(st.Eater);
                    if (grewNpc != null) grewNpc.PurgePenForVictim(st.Eater);
                    // OWNERSHIP FACT (persistent — the event text below fades): the
                    // user's playtests kept hitting "this dick isn't mine" RP drift
                    // on a GAINED penis. It is the body's own groin now; say so in
                    // the facts (readable with `cat facts` every session) AND name
                    // it in body perception.
                    if (grewNpc != null && dickGained)
                        grewNpc.RememberFact("body: the penis between your legs is YOURS — you gained it digesting " + st.Name
                            + "; it is attached to your own crotch, belongs to your body alone, and can NEVER be inside you");
                    else if (grewNpc != null && dickGrew)
                        grewNpc.RememberFact("body: your own penis grew bigger digesting " + st.Name + " — still yours, still attached to you");
                }
                catch (Exception) { }
                var bodyChanged = new List<string>();
                if (sizeGained) bodyChanged.Add("you are bigger now");
                if (strengthGained) bodyChanged.Add("you are stronger (one more carry slot, more stamina)");
                if (breastsGrew) bodyChanged.Add("your breasts grew curvier");
                if (dickGained) bodyChanged.Add("you GREW a dick — you had none; theirs is yours now. It is attached to YOU and can never penetrate you — if you feel \"someone inside you\" it is a misread of your own body (feel reports holes truthfully)");
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
            if (MaybeFinishRemoteVictim(st, "released")) return true;
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
            if (MaybeFinishRemoteVictim(st, "released")) return true;
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
                        _awaitingRebirth[fingerprint] = new RebirthLink { EaterLabel = BodyLabel(st.Eater), EaterKey = st.EaterKey, IsPlayer = true };
                    else
                        _awaitingRebirth[fingerprint] = new RebirthLink { OldKey = st.OldBodyKey, EaterLabel = BodyLabel(st.Eater), EaterKey = st.EaterKey };
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
                RebirthLink link = null;
                bool playerLink = false;
                lock (_lock)
                {
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
                    // "A compaction of the two": the child keeps its OWN previous life
                    // (the swallowed person's) and gets a SHORT taste of the carrier's
                    // life (whoever's genes ate/stored them into the egg) — MemoryCompaction
                    // caps and dedupes both halves so the blend fits a fact ring.
                    List<string> eaterFacts = null;
                    if (!string.IsNullOrEmpty(link != null ? link.EaterKey : null))
                    {
                        try
                        {
                            var eatRec = MemoryStore.Load(link.EaterKey);
                            if (eatRec != null) eaterFacts = eatRec.Facts;
                        }
                        catch (Exception) { }
                    }
                    string hatchLine = "was swallowed whole and digested into an egg"
                        + (link != null && !string.IsNullOrEmpty(link.EaterLabel)
                            ? " by " + link.EaterLabel
                            : "")
                        + "; hatched back in a NEW body with MIXED genes (the carrier's genes and theirs blended). You are the same person.";
                    var facts = MemoryCompaction.MergeRebirth(rec.Facts, link != null ? link.EaterLabel : null, eaterFacts, hatchLine);
                    MemoryStore.Save(newKey, rec.Name, rec.Persona, facts, rec.Goal, rec.Pronouns, rec.Skills,
                        bodyFp: NPCInstance.BodyFullFp(k), species: NPCInstance.CleanName(k.name),
                        voreTarget: rec.VoreTarget, voreMode: rec.VoreMode); // the CHILD egg inherits the skills + vore preferences
                    try
                    {
                        LLMNPCPlugin.Log.LogInfo("swallow: rebirth — '" + (rec.Name ?? "?") + "' hatched from an egg"
                            + (link != null && !string.IsNullOrEmpty(link.EaterLabel) ? " (carried by " + link.EaterLabel + ")" : "")
                            + "; persona + carrier-memory blend restored to body key '" + newKey + "' (genes changed)");
                    }
                    catch (Exception) { }
                    // The old body's key stays only while the person can still come back
                    // THROUGH that body. After the life is copied onto the hatched body's
                    // key it is a duplicate of someone the world already ate — drop it.
                    if (!string.Equals(newKey, oldKey, StringComparison.Ordinal))
                        MemoryStore.Delete(oldKey);
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
        // ------------------------------------------------------------------
        // The mod event channel: photon event code (byte)'B' (the game's own
        // chat event is 'A'). Carries the swallow narration + cross-machine
        // aborts between modded clients in multiplayer: the remote VICTIM's mod
        // sees begin/milestones/end + can abort the EATER's digest from their
        // own machine.
        // ------------------------------------------------------------------
        internal const byte ModSwallowEvent = (byte)'B';
        internal const byte ModPresenceEvent = (byte)'C'; // reserved: future presence chatter

        // Broadcast one swallow line to the OTHER clients. One line of text, plain.
        internal static void BroadcastLine(string line)
        {
            try
            {
                if (!Photon.Pun.PhotonNetwork.InRoom || string.IsNullOrEmpty(line)) return;
                var opts = new Photon.Realtime.RaiseEventOptions
                {
                    CachingOption = Photon.Realtime.EventCaching.DoNotCache,
                    Receivers = Photon.Realtime.ReceiverGroup.Others,
                };
                Photon.Pun.PhotonNetwork.RaiseEvent(ModSwallowEvent, line, opts, ExitGames.Client.Photon.SendOptions.SendReliable);
            }
            catch (Exception) { }
        }

        // A swallow line worth broadcasting — sent only when the OTHER side has the
        // reason to care (a remote victim's mod).
        private static string EventName(SwallowState st)
        {
            try
            {
                int vid = st.Npc != null || st.VictimIsPlayer ? st.VictimId : -1;
                var pv = st.Victim.photonView;
                return pv != null ? "v" + pv.ViewID : "i" + vid;
            }
            catch (Exception) { return "?" ; }
        }

        private static void BroadcastBegin(SwallowState st)
        {
            try
            {
                var pv = st.Victim.photonView;
                if (pv == null) return;
                BroadcastLine("swallow|begin|" + pv.ViewID + "|" + (st.Name ?? "?") + "|"
                    + EffectAt(st).ToString() + "|" + Math.Round(st.DigestSeconds + SwallowLogic.PullSeconds + SwallowLogic.SettleSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception) { }
        }

        private static void BroadcastEnd(SwallowState st, string outcome)
        {
            try
            {
                var pv = st.Victim.photonView;
                if (pv == null) return;
                BroadcastLine("swallow|end|" + pv.ViewID + "|" + (outcome ?? "released"));
            }
            catch (Exception) { }
        }

        // A digest milestone line forwarded to a REMOTE victim's mod (their client
        // shows it; nothing happens locally — the eater already said it as a world
        // event). Cheap string send.
        private static void MaybeForwardLine(SwallowState st, string line)
        {
            try
            {
                if (st == null || st.Victim == null || st.VictimIsPlayer) return;
                if (IsRemotePlayerVictim(st)) BroadcastLine("swallow|line|" + st.Name + "|" + line);
            }
            catch (Exception) { }
        }

        // A remote VICTIM = a player body owned by another client. Their machine
        // learns through the event channel; physics already syncs (ownership moves).
        internal static bool IsRemotePlayerVictim(SwallowState st)
        {
            try
            {
                if (st == null || st.Victim == null) return false;
                var pv = st.Victim.photonView;
                return pv != null && pv.OwnerActorNr != Photon.Pun.PhotonNetwork.LocalPlayer.ActorNumber
                    && NPCInstance.IsPlayerKobold(st.Victim);
            }
            catch (Exception) { return false; }
        }

        // Inbound: the remote victim's machine asked to abort (their "swallow abort"
        // types on a digest WE run). Runs on the main thread (Photon callback).
        internal static void HandleRemoteSwallowEvent(int senderActorNr, string content)
        {
            try
            {
                if (string.IsNullOrEmpty(content)) return;
                var parts = content.Split('|');
                if (parts[0] != "swallow") return;
                string kind = parts[1];
                if (kind == "abort-me")
                {
                    // Their player is a victim of OUR local digest — run the honest
                    // abort (same path the eater's own chat uses).
                    try { LLMNPCPlugin.Log.LogWarning("swallow: received a REMOTE abort from actor " + senderActorNr); } catch (Exception) { }
                    TryAbortFromChat("swallow abort");
                }
                else if (kind == "begin")
                {
                    // A REMOTE eater swallowed... whose body? If it's MY local
                    // player: tell the mind + the NPCs.
                    if (parts.Length < 5) return;
                    int victimView = ParseInt(parts[2]);
                    if (!LocalPlayerViewId(victimView)) return;
                    string victimName = parts[3], effectName = parts[4];
                    string secs = parts.Length > 5 ? parts[5] : "?";
                    string msg = "you are inside " + victimName + "'s stomach — swallowed WHOLE (a meal, not sex); they will digest for ~" + secs + "s (effect: " + effectName + "). Your body is theirs to carry for now; your chat still works and \"swallow abort\" (typed in game chat) releases you.";
                    try { LLMNPCPlugin.Log.LogWarning("[modswallow] " + msg); } catch (Exception) { }
                    try { Plugin.FanoutWorldEvent(msg); } catch (Exception) { }
                }
                else if (kind == "line")
                {
                    if (parts.Length < 3) return;
                    try { Plugin.FanoutWorldEvent("[modswallow] " + parts[2]); } catch (Exception) { }
                }
                else if (kind == "end")
                {
                    if (parts.Length < 3) return;
                    int victimView = ParseInt(parts[2]);
                    if (!LocalPlayerViewId(victimView)) return;
                    try { Plugin.FanoutWorldEvent("the swallow ENDED: you were released (outcome: " + parts[3] + "). Your body is yours again."); } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        private static int ParseInt(string s)
        {
            int v; return int.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        private static bool LocalPlayerViewId(int viewId)
        {
            try
            {
                if (viewId < 0 || !Photon.Pun.PhotonNetwork.InRoom) return false;
                if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null)
                {
                    var pv = pp.kobold.photonView;
                    return pv != null && pv.ViewID == viewId;
                }
            }
            catch (Exception) { }
            return false;
        }

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
        // WHERE the person goes in (and the master off switch) — F7/WebUI controllable.
        internal ConfigEntry<SwallowDestination> _cfgSwallowDestination;
        // Autonomy + memory (bound in Main.cs Awake; consumed by Llm/Console/NPCInstance).
        internal ConfigEntry<float> _cfgIdleInitiative;
        internal ConfigEntry<bool> _cfgAgentOverhear;
        internal ConfigEntry<float> _cfgAgentOverhearRadius;
        internal ConfigEntry<int> _cfgAgentChatPerMinute;
        internal ConfigEntry<float> _cfgSiblingNudgeGap;
        internal ConfigEntry<int> _cfgFactCap;
        internal ConfigEntry<string> _cfgDefaultFacts;
        // Spectate (agent eats the PLAYER) —see src/Spectate.cs.
        internal ConfigEntry<bool> _cfgSwallowAgentEat;
        internal ConfigEntry<bool> _cfgSwallowPlayerVictims;
        internal ConfigEntry<bool> _cfgHandshake;
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

        // Command gating while swallowed: the mind stays (it can be talked to), but
        // nothing body-attached can act — hands/legs/machines all reach nothing from
        // inside a stomach. Allowed: speech (echo/emote/say/emote), memory & goals
        // (remember/forget/goal/plan), read-only polling (ls/ps/pwd/whoami/cat/status/
        // hold/sonar/look/find/screenshot), turn-enders (sleep/help/clear — clear is
        // screen-only) and the dev report. Everything else is refused with an
        // explanation, so a swallowed model doesn't burn turns retrying grab/drop/use.
        internal bool ConsoleCommandAllowedWhileSwallowed(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            try
            {
                string c = ConsoleShell.Canonical(cmd);
                if (c == null) return false; // unknown words are prose anyway
                if (c == "echo" || c == "emote" || c == "sleep" || c == "help" || c == "clear" || c == "report"
                    || c == "remember" || c == "forget" || c == "goal" || c == "plan") return true;
                // The in-body verbs are MEANINGFUL only from inside someone.
                if (c == "kick" || c == "masturbate" || c == "surrender") return true;
                switch (c)
                {
                    case "ls": case "ps": case "pwd": case "whoami": case "cat": case "status":
                    case "hold": case "sonar": case "look": case "find": case "screenshot": case "items":
                        return true;
                    default: return false; // cd/use/get/drop/throw/run/turn/jump/crouch/exit/follow + module tools
                }
            }
            catch (Exception) { return false; }
        }

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

        // Grow-mode absorption: the digested person's facts pour into this LIVE
        // instance as ONE compacted category-safe line ("absorbed:<name>: ..." —
        // RememberFact dedupes one slot per ':'-prefix category, so a single marker
        // carries the whole compaction; MemoryCompaction picks what survives).
        internal void AbsorbVictimFacts(string victimName, List<string> facts)
        {
            if (facts == null) return;
            try { RememberFact(MemoryCompaction.LiveAbsorbLine(victimName, facts)); }
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