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

        public static void Apply(BepInEx.Logging.ManualLogSource log)
        {
            if (_harmony != null) return;
            _harmony = new Harmony("com.kk.llmnpc.patches");
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
            catch (Exception) { /* never break the game's coroutine */ }
        }
    }
}
