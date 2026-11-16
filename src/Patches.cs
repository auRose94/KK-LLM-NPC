// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Harmony patches that tame the game's animator behaviors that fight our control.
//
// Problem: CharacterControllerAnimator spawns coroutines (AnimationRoutine when entering
// a station, StopAnimationRoutine when leaving) that write rigidbody.rotation directly.
// Those run on coroutine time and will pull the body back to a recorded rotation — which
// was the persistent "rotate back one direction" bug. We patch those coroutine MoveNext
// methods to skip the rotation write specifically when the rigidbody belongs to a body
// the LLM is currently driving.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KKLLMNPC
{
    internal static class Patches
    {
        private static Harmony _harmony;
        internal static Harmony HarmonyInstance { get { return _harmony; } }

        public static void Apply(BepInEx.Logging.ManualLogSource log)
        {
            if (_harmony != null) return;
            _harmony = new Harmony("com.kk.llmnpc.patches");
            Log = log;
            try
            {
                // Suppress the station-entry rotation grab: AnimationRoutine lerps the body
                // to the station's rotation during mounting. Patch the method's MoveNext and
                // neutralize the rigidbody write for LLM-driven bodies.
                var animRoutineMoveNext = typeof(CharacterControllerAnimator)
                    .GetNestedType("<AnimationRoutine>d__79", BindingFlags.NonPublic)
                    ?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic);
                if (animRoutineMoveNext != null)
                    _harmony.Patch(animRoutineMoveNext,
                        prefix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(AnimationRoutine_MoveNext_Prefix), BindingFlags.Static | BindingFlags.NonPublic)));

                var stopRoutineMoveNext = typeof(CharacterControllerAnimator)
                    .GetNestedType("<StopAnimationRoutine>d__86", BindingFlags.NonPublic)
                    ?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic);
                if (stopRoutineMoveNext != null)
                    _harmony.Patch(stopRoutineMoveNext,
                        prefix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(StopAnimationRoutine_MoveNext_Prefix), BindingFlags.Static | BindingFlags.NonPublic)));

                // STATION-CANUSE CRASH GUARD (bug fix 2026-11-08): the game's own
                // User.FixedUpdate -> SortGrabbables -> BreedingStation.CanUse() throws a
                // NullReferenceException every physics frame when a station's
                // AnimationStation.info (or an entry in its station list) is null — CanUse
                // ignores its Kobold argument and dereferences station.info.user. It fires
                // after a body is destroyed/sold mid-mount, leaving a slot whose info is
                // null, and it spammed the log 3800+ times. Our own interact() already
                // wraps its CanUse() call in try/catch, so this only ever fires from the
                // GAME's FixedUpdate; a finalizer on SortGrabbables swallows that one
                // exception at the single point the game calls it, leaving the game's
                // interaction menu (closestUsable) intact.
                var sortGrabbables = typeof(User).GetMethod("SortGrabbables",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (sortGrabbables != null)
                    _harmony.Patch(sortGrabbables,
                        finalizer: new HarmonyMethod(typeof(Patches).GetMethod(nameof(SortGrabbables_Finalizer), BindingFlags.Static | BindingFlags.NonPublic)));

                // Swallow-vore trigger: the player's drop while looking up becomes a
                // swallow (SwallowManager owns the trigger rule + suppresses the drop).
                SwallowManager.ApplyPatches(_harmony);

                // SWIFT SELL SILENCE (2026-11-16, v1.3.0 playtest): MailMachine is
                // the game's sell box — LocalUse runs the mail animation, then
                // WaitThenVoreKobold PhotonNetwork.Destroy's the body. During those
                // seconds (and until our reconcile notices) a claimed agent kept
                // turning and SPEAKING from inside the mailbox ("where's my body?").
                // When the machine takes one of OUR bodies, silence and retire the
                // mind immediately — a sold kobold is gone the moment it's mailed.
                var mailUse = typeof(MailMachine).GetMethod("LocalUse",
                    BindingFlags.Instance | BindingFlags.Public);
                if (mailUse != null)
                    _harmony.Patch(mailUse,
                        prefix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(MailMachine_LocalUse_Prefix), BindingFlags.Static | BindingFlags.NonPublic)));

                log.LogInfo("KKLLMNPC: animator rotation patches applied.");
            }
            catch (Exception e)
            {
                log.LogWarning("KKLLMNPC: failed to apply animator patches: " + e.Message);
            }
        }

        // The state-machine objects hold a reference to the owning animator as <>4__this.
        // If that body belongs to us, null the rotation write; otherwise let it run.
        private static void AnimationRoutine_MoveNext_Prefix(object __instance)
        {
            SkipRotationIfOurs(__instance);
        }
        private static void StopAnimationRoutine_MoveNext_Prefix(object __instance)
        {
            SkipRotationIfOurs(__instance);
        }

        private static void SkipRotationIfOurs(object routineState)
        {
            if (routineState == null) return;
            try
            {
                var ownerField = routineState.GetType().GetField("<>4__this");
                if (ownerField == null) return;
                var self = ownerField.GetValue(routineState) as CharacterControllerAnimator;
                if (self == null) return;
                // kobold is a private field on CharacterControllerAnimator — reflect it.
                var kf = typeof(CharacterControllerAnimator).GetField("kobold",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var kobold = kf != null ? kf.GetValue(self) as Kobold : null;
                if (kobold == null) return;
                int id = kobold.GetInstanceID();
                if (LLMNPCPlugin.IsClaimedByAnyLLM(id))
                {
                    // Wipe stored <startRotation> so Lerp(start, end, t) can't resurrect a
                    // stale bearing the next time the routine steps.
                    var startRot = routineState.GetType().GetField("<startRotation>5__4");
                    startRot?.SetValue(routineState, kobold.body != null
                        ? kobold.body.rotation
                        : self.transform.rotation);
                }
            }
            catch (Exception) { /* never break the game's coroutine — errors here are benign */ }
        }

        // Finalizer for User.SortGrabbables — the game's per-physics-frame interaction
        // scan. A breeding/bed/milking/etc. station whose AnimationStation.info is null
        // throws inside CanUse (station.info.user deref), which the game's FixedUpdate
        // does NOT catch and which then floods the log every frame. We swallow the
        // exception here so the game keeps running; closestUsable simply stays whatever
        // it was (the game re-scans next frame). We log once, throttled, so the crash
        // is visible without spamming.
        private static Exception SortGrabbables_Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            try
            {
                if (Log == null) return __exception;
                float now = Time.unscaledTime;
                if (now - _lastStationCrashLog > 30f)
                {
                    _lastStationCrashLog = now;
                    Log.LogWarning("KKLLMNPC: swallowed station CanUse NRE (null station.info) — "
                        + __exception.GetType().Name + ": " + __exception.Message
                        + " — game interaction menu left intact, will re-scan next frame.");
                }
            }
            catch (Exception) { }
            return null; // swallow — do not rethrow
        }
        private static volatile float _lastStationCrashLog = -99f;
        internal static BepInEx.Logging.ManualLogSource Log;

        // Instance-for-body resolver, set by the plugin at startup (same delegate
        // idiom as ModGrabManager.ResolveNpc) — lets this static class reach the
        // NPCInstance driving a body without holding a plugin reference.
        internal static System.Func<Kobold, NPCInstance> ResolveNpc;

        // Prefix on MailMachine.LocalUse(Kobold k): silence the mind the moment the
        // mail machine takes its body. Never blocks the game's sale (returns true).
        private static bool MailMachine_LocalUse_Prefix(MailMachine __instance, Kobold k)
        {
            try
            {
                if (k == null) return true;
                var npc = ResolveNpc != null ? ResolveNpc(k) : null;
                if (npc != null && !npc.IsGone)
                {
                    npc.MarkGone("sold through the mail machine");
                    try { Log?.LogInfo("KKLLMNPC: mail machine took '" + NPCInstance.CleanName(k.name) + "' — agent silenced (sold)."); } catch (Exception) { }
                }
            }
            catch (Exception) { }
            return true; // the sale itself always proceeds
        }
    }
}
