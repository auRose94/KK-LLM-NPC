// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// ModHooks — KKLLMNPC's PUBLIC event surface for OTHER mods (v1.13.0).
//
// Built for Rose's VR mod and any other BepInEx plugin that needs the mod's
// live events passed HONESTLY: vore sessions, the spectate hand-off (the
// local player eaten → their perspective moves into the eater), REAL sex
// (never vore — the phantom-sex signaling fix rides the same release), and
// possession. Subscribe from anywhere; unsubscribe on your plugin teardown.
//
//     KKLLMNPC.ModHooks.SpectateStarted += e => { /* e.Eater is the body to attach a VR rig to */ };
//     KKLLMNPC.ModHooks.ExternalSpectateCamera = true;  // you drive the spectate camera; the built-in writer stands down
//
// Threading contract: every event fires on the UNITY MAIN THREAD (the fire
// sites are the swallow tick, the render pre-cull gate, penetration
// callbacks and possession wiring). Subscribers must be fast and must not
// block; each subscriber is exception-isolated (ModHooksCore) — a throwing
// handler is counted and skipped, the rest still run, the plugin lives.
//
// The vore events are state transitions, not narration spam: Started (pull
// begins), FullyInside (conversion begins), Ended (Outcome names the ending).
// Sex events fire only for REAL penetration — a labeled "vore-insert:" entry
// is the swallowed meal's body acting as the shaft and never fires them.
using System;
using UnityEngine;

namespace KKLLMNPC
{
    // ---- payloads (public so other mods can read them) ----

    public sealed class VoreHookEvent
    {
        public Kobold Eater;
        public Kobold Victim;
        public bool VictimIsPlayer;   // the LOCAL PLAYER is the meal (spectate runs)
        public string EaterName;
        public string VictimName;
        public int EaterId;
        public int VictimId;
        public string Hole;           // entry hole label ("mouth"/"anal"/"pussy"/"nipple")
        public string Outcome;        // VoreEnded only: thrown_up/poured_out/hold_ended/converted_egg/…
    }

    public sealed class SpectateHookEvent
    {
        public Kobold Player;         // the local player's body (parked inside the eater)
        public Kobold Eater;          // the body whose perspective they now watch — VR rigs attach here
        public string EaterLabel;
        public int Pov;               // SpectatePov of the built-in camera (0 first-person, 1 third)
        public string Cause;          // SpectateEnded only: hatch/abort/eater-lost/scene-leave/restart
        public string Fingerprint;    // SpectateEggLaid only: the egg's gene fingerprint
    }

    public sealed class SexHookEvent
    {
        public Kobold PenetratorOwner;   // kobold whose body carries the shaft (null = ownerless toy)
        public Kobold PenetratedBody;    // the body being entered (null = non-kobold target)
        public int PenetratorOwnerId;
        public int PenetratedBodyId;
        public string Hole;
        public string Model;             // the shaft's model name ("KDEFuta", "dild 1", …)
    }

    public sealed class PossessionHookEvent
    {
        public Kobold Body;
        public int BodyId;
        public string AgentName;         // the NPC mind's name (null on release)
    }

    public static class ModHooks
    {
        // ---- vore lifecycle ----
        public static event Action<VoreHookEvent> VoreStarted;      // a swallow session begins (the pull starts)
        public static event Action<VoreHookEvent> VoreFullyInside;  // the meal is all the way in; conversion begins
        public static event Action<VoreHookEvent> VoreEnded;        // session over — Outcome says how it ended

        // ---- spectate (the local player eaten → perspective hand-off) ----
        public static event Action<SpectateHookEvent> SpectateStarted;   // camera moves into the eater
        public static event Action<SpectateHookEvent> SpectateEggLaid;   // conversion produced the rebirth egg
        public static event Action<SpectateHookEvent> SpectateEnded;     // watch over (Cause says why)
        public static event Action<SpectateHookEvent> SpectateHatched;   // the player woke grown at the egg's hatch spot

        // ---- REAL sex only (the labeled vore insert NEVER fires these) ----
        public static event Action<SexHookEvent> SexStarted;
        public static event Action<SexHookEvent> SexEnded;

        // ---- possession (an agent mind takes / releases a body) ----
        public static event Action<PossessionHookEvent> PossessionStarted;
        public static event Action<PossessionHookEvent> PossessionEnded;

        // THE VR HAND-OFF: set TRUE to drive the spectate camera yourself. While
        // set, the built-in last-writer camera (SpectateManager.WriteCamera) does
        // not touch the camera at all — no fighting on pre-cull. The manager
        // clears it on SpectateEnded so a forgotten flag can't strand the player's
        // camera on the next watch. Polling alternative: PlayerSpectating/EaterOf.
        public static bool ExternalSpectateCamera;

        // THE SECOND HAND-OFF: set TRUE to own the player's INPUT during a
        // spectate watch (a VR locomotion/interaction rig). While set, the
        // spectate machinery does not disable PlayerPossession/CameraSwitcher
        // and does not zero the input fields each tick — the claiming mod runs
        // the player's body (and owns suppressing flat-game input itself, if it
        // wants to). The claim may be set any time before or during the watch
        // (VoreStarted fires before SpectateStarted, so early claims work). The
        // player's body still parks at the eater's belly every tick — that part
        // is the honest state, not a control fight. Cleared on release/hatch.
        public static bool ExternalPlayerControl;

        // The Harmony instance IDs this plugin patches under. Before patching an
        // engine method, check for a conflict:
        // Harmony.GetPatchInfo(method).Owners.ContainsAny(ModHooks.PatchOwnerIds)
        // — the full inventory (and what each patch does) is in docs/MODHOOKS.md.
        public static readonly string[] PatchOwnerIds = { "com.kk.llmnpc.patches" };

        // The spectate HUD line in words (null = not spectating): who is hosting,
        // the hatch promise, the struggle meter note. A VR rig renders its own
        // HUD from this instead of the flat OnGUI box.
        public static string SpectateHudLine()
        {
            try { return SpectateManager.HudText(); } catch (Exception) { return null; }
        }

        // The escape-meter read (0-1): how close the swallowed player's fighting
        // is to forcing a throw-up (1 = it happens this press). VR HUD gauge.
        public static float SpectateStruggle01()
        {
            try { return SwallowManager.PlayerStruggle01(); } catch (Exception) { return 0f; }
        }

        // ONE struggle press from ANY input device — the flat game polls SPACE;
        // a VR rig maps a controller gesture to this and gets the same escape
        // route: meter gain scaled by meal size, narration to the host, vomit at
        // the threshold, and the remote-eater abort RPC. Returns true when the
        // press was consumed by an active watch (false = not swallowed).
        public static bool SubmitPlayerStruggle()
        {
            try { return SwallowManager.PlayerStruggleHit(); } catch (Exception) { return false; }
        }

        // Prey click-to-point from ANY input device (v1.11.0's flat mouse click,
        // opened up): hand in a world position — the hit of a VR controller ray —
        // and the host agent receives the guest's pointing event (what it is,
        // bearing relative to the host's facing, distance, world coords the
        // gazetteer can route to). hitName optional (null = "something").
        // Returns a feedback line for your own HUD ("pointed at X — your host
        // was told", "not spectating — no host to point at", …).
        public static string SubmitPreyPoint(Vector3 worldPos, string hitName)
        {
            try
            {
                var eaterNpc = SpectateManager.SpectatingEaterNpc();
                if (eaterNpc == null) return "not spectating — no host to point at";
                string nm = string.IsNullOrEmpty(hitName) ? "something" : NPCInstance.CleanName(hitName);
                float yaw = 0f;
                Vector3 ep = Vector3.zero;
                try { yaw = eaterNpc.YawDeg; var eb = eaterNpc.KoboldObj as Kobold; if (eb != null) ep = eb.transform.position; } catch (Exception) { }
                string msg = SpectateLogic.PointText(nm, worldPos.x, worldPos.y, worldPos.z, ep.x, ep.y, ep.z, yaw);
                eaterNpc.PushWorldEvent("[point] " + msg);
                return "pointed at " + nm + " — your host was told";
            }
            catch (Exception e) { return "point failed: " + e.Message; }
        }

        // Polling helpers (events can be missed during scene loads; these never are).
        public static bool PlayerSpectating
        {
            get { try { return SpectateManager.Active; } catch (Exception) { return false; } }
        }

        public static Kobold EaterOfPlayer
        {
            get { try { return SpectateManager.ActiveEater; } catch (Exception) { return null; } }
        }

        // ---- fire helpers (internal — the mod's own call sites; C# events may
        // only be raised inside their declaring class, so these live HERE) ----

        internal static void FireVoreStarted(VoreHookEvent ev)
        {
            if (ev == null) return;
            Invoke(VoreStarted, ev);
        }

        internal static void FireVoreFullyInside(VoreHookEvent ev)
        {
            if (ev == null) return;
            Invoke(VoreFullyInside, ev);
        }

        internal static void FireVoreEnded(VoreHookEvent ev, string outcome)
        {
            if (ev == null) return;
            ev.Outcome = outcome;
            Invoke(VoreEnded, ev);
        }

        internal static void FireSpectateStart(Kobold player, Kobold eater, string label, int pov)
        {
            Invoke(SpectateStarted, Spec(player, eater, label, pov, null, null));
        }

        internal static void FireSpectateEggLaid(Kobold player, Kobold eater, string label, string fingerprint)
        {
            Invoke(SpectateEggLaid, Spec(player, eater, label, -1, null, fingerprint));
        }

        internal static void FireSpectateEnd(Kobold player, Kobold eater, string label, string cause)
        {
            Invoke(SpectateEnded, Spec(player, eater, label, -1, cause, null));
        }

        internal static void FireSpectateHatch(Kobold player, Kobold eater, string label, string fingerprint)
        {
            Invoke(SpectateHatched, Spec(player, eater, label, -1, "hatch", fingerprint));
        }

        internal static void FireSexStarted(Kobold shaftOwner, int shaftOwnerId,
            Kobold penetrated, int penetratedId, string hole, string model)
        {
            Invoke(SexStarted, Sex(shaftOwner, shaftOwnerId, penetrated, penetratedId, hole, model));
        }

        internal static void FireSexEnded(Kobold shaftOwner, int shaftOwnerId,
            Kobold penetrated, int penetratedId, string hole, string model)
        {
            Invoke(SexEnded, Sex(shaftOwner, shaftOwnerId, penetrated, penetratedId, hole, model));
        }

        internal static void FirePossessionStarted(Kobold body, int bodyId, string agentName)
        {
            Invoke(PossessionStarted, new PossessionHookEvent { Body = body, BodyId = bodyId, AgentName = agentName });
        }

        internal static void FirePossessionEnded(Kobold body, int bodyId)
        {
            Invoke(PossessionEnded, new PossessionHookEvent { Body = body, BodyId = bodyId, AgentName = null });
        }

        private static SpectateHookEvent Spec(Kobold player, Kobold eater, string label, int pov, string cause, string fingerprint)
        {
            return new SpectateHookEvent
            {
                Player = player, Eater = eater, EaterLabel = label, Pov = pov,
                Cause = cause, Fingerprint = fingerprint,
            };
        }

        private static SexHookEvent Sex(Kobold shaftOwner, int shaftOwnerId,
            Kobold penetrated, int penetratedId, string hole, string model)
        {
            return new SexHookEvent
            {
                PenetratorOwner = shaftOwner, PenetratorOwnerId = shaftOwnerId,
                PenetratedBody = penetrated, PenetratedBodyId = penetratedId,
                Hole = hole, Model = model,
            };
        }

        private static void Invoke(MulticastDelegate h, object arg)
        {
            try { ModHooksCore.Invoke(h, arg); }
            catch (Exception) { } // the core never throws; belt for the braces
        }
    }
}
