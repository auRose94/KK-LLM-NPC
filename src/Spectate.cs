// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Spectate — when an AI agent eats the LOCAL PLAYER, the player's camera is taken
// over and forced to watch the AI play until the egg is planted and hatches (or
// the player aborts/quits).
//
// The flow rides the swallow pipeline (src/Swallow.cs) with a player victim:
//   eat_player act  the agent (any mod-driven NPC) swallows the local player
//                   whole when adjacent — SpectateManager takes the camera
//   digest          the player body is pinned/hidden inside the eater like any
//                   victim; the camera orbits the EATER on every pre-cull so
//                   nothing else (OrbitCamera configs, camera hotkeys, pause)
//                   can fight the view; movement/input is suppressed
//   hatch (Egg)     the egg pops out, gets planted somewhere (usually by the
//                   eater agent), the vanilla plant chain spawns a body carrying
//                   the mixed genes — the mod copies those genes onto the parked
//                   player body, revives it at the hatch spot, despawns the
//                   duplicate NPC body, and hands the camera back
//   belly (BellyEgg) conversion puts the genetics in the eater's belly; the
//                   spectate continues until they nest-lay and the laid egg is
//                   planted and hatches
//   no-hatch modes  Grow/Destroy: the player watches until they type the
//                   "swallow abort" chat command or leave the game
//
// Deliberate choices:
// * The player body is NEVER destroyed (the game's death machinery would respawn
//   them; TagObject would rot). It is parked invisible/kinematic at the eater's
//   belly and restored on hatch/abort. The parked body follows the eater, so the
//   player's map/senses keep making sense from inside.
// * The camera is overridden LAST-WRITER on Camera.onPreCull rather than through
//   OrbitCamera configurations: no config surgery means restoring is a no-op —
//   the camera simply returns to whatever the game's own CameraSwitcher had.
//
// All main-thread work (Tick from Update, the pre-cull hook from the render
// loop); the eat-player entry point runs through the module tool dispatch.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using Photon.Pun;

namespace KKLLMNPC
{
    // Where the spectating camera lives: FirstPerson = through the eater's own
    // eyes (the AI's eye placement — same head-local offset the vision
    // screenshots use), ThirdPerson = a wall-safe follow orbit behind them.
    public enum SpectatePov
    {
        FirstPerson = 0,
        ThirdPerson = 1,
    }

    internal static class SpectateManager
    {
        internal static LLMNPCPlugin Plugin;

        private static readonly object _lock = new object();
        private static SpectateState _active; // only ONE possible spectating body: the local player
        private static bool _camHooked;

        // Hatch despawn queue: the plant chain's duplicate spawn is destroyed a
        // beat later — destroying DURING OnPhotonInstantiate is dicey, so it's
        // deferred (and retried a few times if Photon refuses).
        private sealed class Despawn
        {
            public GameObject Obj;
            public float At;
            public int Tries;
        }
        private static readonly List<Despawn> _despawn = new List<Despawn>();

        private sealed class SpectateState
        {
            public Kobold Player;
            public Kobold Eater;
            public string EaterLabel;      // "the player", NPC name, mesh name
            public string HatchHint;       // HUD promise per SwallowEffect (null = no hatch)
            public string Fingerprint;     // egg genes the hatch will arrive with
            public bool HatchPending;
            public float StartUnscaled;
            public Vector3 CamPos;         // last overwritten camera pose (diagnostics)
            public bool CamInit;           // first frame snaps instead of lerping
            public Vector3 LastEaterPos;   // for eater-lost release after despawn
            public PlayerPossession PP;    // disabled while watching (all input dead)
            public CameraSwitcher CamSwitch;
            public int Pov;                // SpectatePov
            public float OrigNear;         // game camera's near clip, restored on release
            public bool NearTaken;         // OrigNear valid
        }

        // reflection cache: PlayerPossession's private booleans read directly from
        // input handlers — cleared each tick best-effort so a grabbed/mid-action
        // player can't grab/rotate/hip-track while pinned (movement already zeroed
        // via SetMovementEnabled(false)).
        private static FieldInfo _ppGrab, _ppRotate, _ppHip, _ppSwitch, _ppStaticInstance;

        internal static void Ensure(LLMNPCPlugin plugin)
        {
            Plugin = plugin;
            if (_camHooked) return;
            // KOBOLDKARE IS URP — Camera.onPreCull is NOT invoked under the Scriptable
            // Render Pipeline (both playtests sat frozen at a degenerate configless
            // OrbitCamera pose). The URP-sanctioned per-camera pre-cull hook is
            // RenderPipelineManager.beginCameraRendering; the Camera.onPreCull
            // subscription is kept only as built-in-pipeline insurance (no-ops here).
            try { RenderPipelineManager.beginCameraRendering += OnCameraRendering; _camHooked = true; }
            catch (Exception e)
            {
                try { plugin.Logger.LogWarning("spectate URP beginCameraRendering hook: " + e.Message); } catch (Exception) { }
                try { Camera.onPreCull += OnPreCull; _camHooked = true; } catch (Exception) { }
            }
            CachePPFields();
        }

        private static void CachePPFields()
        {
            if (_ppGrab != null) return;
            try
            {
                var t = typeof(PlayerPossession);
                const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
                _ppGrab = t.GetField("grabbing", F);
                _ppRotate = t.GetField("rotating", F);
                _ppHip = t.GetField("trackingHip", F);
                _ppSwitch = t.GetField("switchedMode", F);
                _ppStaticInstance = t.GetField("playerInstance", BindingFlags.NonPublic | BindingFlags.Static);
            }
            catch (Exception) { }
        }

        // Take ALL of the player's control away: disable the possession component
        // (its OnDisable unsubscribes every input handler) + the camera mode
        // switcher (its hotkeys would flip movement back on), then re-pin the
        // static `playerInstance` — the whole mod reads the player's body through
        // PlayerPossession.TryGetPlayerInstance and must keep seeing them.
        private static void LockPlayerControl(SpectateState a)
        {
            try
            {
                CachePPFields();
                var pp = a.Player.GetComponentInChildren<PlayerPossession>(true);
                if (pp != null)
                {
                    a.PP = pp;
                    pp.enabled = false;
                }
                try
                {
                    var camSwitch = a.Player.GetComponentInChildren<CameraSwitcher>(true);
                    if (camSwitch != null) { a.CamSwitch = camSwitch; camSwitch.enabled = false; }
                }
                catch (Exception) { }
                try { if (_ppStaticInstance != null && pp != null) _ppStaticInstance.SetValue(null, pp); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        private static void UnlockPlayerControl(SpectateState a)
        {
            try { if (a.CamSwitch != null) a.CamSwitch.enabled = true; } catch (Exception) { }
            try { if (a.PP != null) a.PP.enabled = true; } catch (Exception) { }
            try
            {
                if (PlayerPossession.TryGetPlayerInstance(out var pp)) pp.SetMovementEnabled(true);
            }
            catch (Exception) { }
        }

        internal static bool Active { get { lock (_lock) return _active != null; } }

        internal static bool IsSpectating(Kobold body)
        {
            if (body == null) return false;
            lock (_lock) return _active != null && _active.Player == body;
        }

        // The eater's label when THIS body is inside them (spectating), else null —
        // observation surfaces (the player perception / player_activity) use it to
        // tell a consuming agent the truth instead of reading the parked body's
        // position as "the player is using my sex station right now".
        internal static string SpectatingEaterLabel(Kobold playerBody)
        {
            if (playerBody == null) return null;
            lock (_lock)
            {
                if (_active == null || _active.Player != playerBody) return null;
                return _active.EaterLabel ?? "someone";
            }
        }

        // The always-on HUD/status line for the spectating player (null = idle).
        // HatchHint non-empty = there IS a hatch coming (digest or parked phase);
        // empty (only after MarkNoHatch for Grow/Destroy) = the no-hatch variant.
        internal static string HudText()
        {
            lock (_lock)
            {
                if (_active == null) return null;
                string who = _active.EaterLabel ?? "someone";
                return SpectateLogic.HudLine(who, string.IsNullOrEmpty(_active.HatchHint) ? null : _active.HatchHint);
            }
        }

        // ------------------------------------------------------------------
        // Begin: a swallow with the local player as victim just started. Takes
        // the camera immediately (the pull-in animation begins this frame) and
        // suppresses player input for the whole watch.
        // ------------------------------------------------------------------
        internal static void BeginForPlayer(Kobold player, Kobold eater, string eaterLabel, string hatchHint)
        {
            if (player == null || eater == null) return;
            lock (_lock)
            {
                if (_active != null && _active.Player == player)
                {
                    _active.Eater = eater; // same player re-swallowed — re-target
                }
                else
                {
                    _active = new SpectateState
                    {
                        Player = player,
                        Eater = eater,
                        EaterLabel = string.IsNullOrEmpty(eaterLabel) ? "someone" : eaterLabel,
                        HatchHint = hatchHint,
                        HatchPending = false,
                        StartUnscaled = Time.unscaledTime,
                        CamInit = false,
                        Pov = ReadPov(),
                        OrigNear = float.NaN,
                        NearTaken = false,
                    };
                }
                try { _active.LastEaterPos = eater.transform.position; } catch (Exception) { }
            }
            SpectateState created;
            lock (_lock) created = _active != null && _active.Player == player ? _active : null;
            if (created != null)
            {
                LockPlayerControl(created);
                SuppressPlayerInput();
            }
            try { LLMNPCPlugin.Log.LogInfo("spectate: camera taken over — the player watches from inside '" + (eaterLabel ?? "?") + "'"); } catch (Exception) { }
        }

        // Called at consume time (Egg/BellyEgg): the hatch is now pending; the
        // egg's genes fingerprint is what the planted egg will arrive as.
        internal static void MarkEggLaid(Kobold player, string fingerprint, string hatchHint)
        {
            lock (_lock)
            {
                if (_active == null || _active.Player != player) return;
                _active.Fingerprint = fingerprint;
                _active.HatchPending = !string.IsNullOrEmpty(fingerprint);
                if (!string.IsNullOrEmpty(hatchHint)) _active.HatchHint = hatchHint;
            }
        }

        // Called at consume time (Grow/Destroy): no hatch will come.
        internal static void MarkNoHatch(Kobold player)
        {
            lock (_lock)
            {
                if (_active == null || _active.Player != player) return;
                _active.HatchPending = false;
                _active.HatchHint = null; // HudText renders the no-hatch variant
            }
        }

        // ------------------------------------------------------------------
        // Per-frame: keep the parked body at the eater's belly, feed the
        // eater's last known position for the lost-eater release, suppress
        // player input, and run the deferred hatch despawns.
        // ------------------------------------------------------------------
        internal static void Tick()
        {
            if (Plugin == null) return;
            ProcessDespawns();

            SpectateState a;
            lock (_lock) a = _active;
            if (a == null) return;

            try { if (a.Player == null) { lock (_lock) { if (_active == a) _active = null; } return; } } catch (Exception) { }

            try
            {
                if (a.Eater == null) { ReleaseFor(a.Player, SpectateLogic.CauseEaterLost); return; }
            }
            catch (Exception) { }

            try
            {
                a.LastEaterPos = a.Eater.transform.position;
                a.Player.transform.position = SwallowManager.BellyOf(a.Eater);
            }
            catch (Exception) { }

            SuppressPlayerInput();
        }

        private static void SuppressPlayerInput()
        {
            try
            {
                if (!PlayerPossession.TryGetPlayerInstance(out var pp) || pp == null) return;
                try { pp.SetMovementEnabled(false); } catch (Exception) { }
                CachePPFields();
                if (_ppGrab != null) try { _ppGrab.SetValue(pp, false); } catch (Exception) { }
                if (_ppRotate != null) try { _ppRotate.SetValue(pp, false); } catch (Exception) { }
                if (_ppHip != null) try { _ppHip.SetValue(pp, false); } catch (Exception) { }
                if (_ppSwitch != null) try { _ppSwitch.SetValue(pp, false); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // End of watch. player == null means "whoever is active". Body restore
        // is best-effort: on scene leave the old body is dying anyway.
        // ------------------------------------------------------------------
        internal static void ReleaseFor(Kobold player, int cause)
        {
            SpectateState a;
            lock (_lock)
            {
                if (_active == null) return;
                if (player != null && _active.Player != player) return;
                a = _active;
                _active = null;
            }
            if (a == null) return;

            UnlockPlayerControl(a);

            if (a.Player != null)
            {
                Vector3 at;
                try { at = a.Player.transform.position; } catch (Exception) { at = Vector3.zero; }
                if (cause == SpectateLogic.CauseEaterLost && a.LastEaterPos != Vector3.zero)
                    at = a.LastEaterPos + Vector3.up * 0.1f;
                try { SwallowManager.RestoreVictimBody(a.Player, at); } catch (Exception) { }
            }
            try { LLMNPCPlugin.Log.LogInfo("spectate: released (cause " + CauseName(cause) + ")"); } catch (Exception) { }
        }

        // Scene teardown / back to menu: state must go; the old bodies go with it.
        internal static void ResetForSceneLeave()
        {
            try { ReleaseFor(null, SpectateLogic.CauseSceneLeave); } catch (Exception) { }
        }

        // DLL hot-reload on the SAME scene: the player body lives — restore it now
        // (the freshly re-created plugin has no spectate state to finish it).
        internal static void ResetAfterPluginRestart()
        {
            try { ReleaseFor(null, SpectateLogic.CauseRestart); } catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // Hatch: the plant chain just spawned a body whose genes fingerprint
        // matches our spectating player's egg. Copy the genes onto the parked
        // player body, revive it at the hatch spot, schedule the duplicate NPC
        // body for despawn, and hand the camera back.
        // Returns false when there is no matching spectate (the body stays a
        // plain NPC for the claim scan).
        // ------------------------------------------------------------------
        internal static bool HatchRevive(Kobold spawned, string fingerprint)
        {
            SpectateState a;
            lock (_lock)
            {
                if (_active == null || _active.Fingerprint != fingerprint || !_active.HatchPending) return false;
                a = _active;
            }
            try
            {
                if (a.Player == null || spawned == null) return false;
                KoboldGenes genes = null;
                try { genes = spawned.GetGenes(); } catch (Exception) { }
                try { if (genes != null) a.Player.SetGenes(genes); } catch (Exception e) { try { LLMNPCPlugin.Log.LogWarning("spectate hatch genes: " + e.Message); } catch (Exception) { } }
                Vector3 at;
                try { at = spawned.transform.position + Vector3.up * 0.1f; } catch (Exception) { at = a.Player.transform.position; }
                try { QueueDespawn(spawned.gameObject); } catch (Exception) { }
                try { SwallowManager.RestoreVictimBody(a.Player, at); } catch (Exception) { }
            }
            catch (Exception)
            {
                return false;
            }
            lock (_lock) { if (_active == a) { _active = null; a.HatchPending = false; } }
            UnlockPlayerControl(a);
            try { LLMNPCPlugin.Log.LogInfo("spectate: HATCH — the player was revived at the egg's hatch spot with the egg's genes"); } catch (Exception) { }
            return true;
        }

        private static void QueueDespawn(GameObject go)
        {
            if (go == null) return;
            lock (_despawn) _despawn.Add(new Despawn { Obj = go, At = Time.unscaledTime + 0.5f, Tries = 0 });
        }

        private static void ProcessDespawns()
        {
            Despawn[] due;
            lock (_despawn)
            {
                if (_despawn.Count == 0) return;
                float now = Time.unscaledTime;
                due = _despawn.FindAll(d => now >= d.At).ToArray();
            }
            foreach (var d in due)
            {
                bool done = false;
                try { if (d.Obj != null) PhotonNetwork.Destroy(d.Obj); done = true; }
                catch (Exception e) { try { LLMNPCPlugin.Log.LogWarning("spectate despawn: " + e.Message + " (try " + d.Tries + ")"); } catch (Exception) { } }
                if (d.Obj == null) done = true; // already gone
                lock (_despawn)
                {
                    _despawn.Remove(d);
                    if (!done && d.Tries < 20) { d.Tries++; d.At = Time.unscaledTime + 0.5f; _despawn.Add(d); }
                }
            }
        }

        // ------------------------------------------------------------------
        // The camera itself: written on every pre-cull so the render always
        // shows the eater's world — no other system (OrbitCamera configs, the
        // camera hotkeys, the player's mouse) ever wins a frame.
        //   FirstPerson  glued to the eater's EYES — position/head bone with the
        //                AI-vision eye offset, rotation = the head's; this is
        //                watching through the thing that ate you.
        //   ThirdPerson  follow orbit behind the eater; a linecast pulls the
        //                boom in when walls/furniture get between you.
        // ------------------------------------------------------------------
        private static string CauseName(int cause)
        {
            switch (cause)
            {
                case SpectateLogic.CauseHatch: return "hatch";
                case SpectateLogic.CauseAbort: return "abort";
                case SpectateLogic.CauseEaterLost: return "eater-lost";
                case SpectateLogic.CauseSceneLeave: return "scene-leave";
                default: return "restart";
            }
        }

        private static int ReadPov()
        {
            var cfg = Plugin != null ? Plugin._cfgSwallowPov : null;
            try { if (cfg != null) return (int)cfg.Value; } catch (Exception) { }
            return 0; // FirstPerson
        }

        // The eater's head BONE transform (the AI's real eyes are slightly ahead
        // of it); falls back to the body root. Same resolution Body.cs uses for
        // the NPC vision cameras, so the spectate view matches what the AI sees.
        private static Transform HeadBoneTf(Kobold k)
        {
            Transform head = null;
            try
            {
                var desc = k.GetComponent<CharacterDescriptor>();
                var anim = desc != null ? desc.GetDisplayAnimator() : null;
                head = anim != null ? anim.GetBoneTransform(HumanBodyBones.Head) : null;
            }
            catch (Exception) { }
            return head != null ? head : k.transform;
        }

        private static bool _hookLiveLogged;

        // URP path (the one that actually fires in KoboldKare).
        private static void OnCameraRendering(ScriptableRenderContext ctx, Camera cam) { WriteCamera(cam); }

        // Built-in pipeline fallback (no-ops under URP).
        private static void OnPreCull(Camera cam) { WriteCamera(cam); }

        private static void WriteCamera(Camera cam)
        {
            SpectateState a;
            lock (_lock) a = _active;
            if (a == null || cam == null || a.Eater == null) return;
            try { if (cam != OrbitCamera.GetCamera()) return; } catch (Exception) { return; }
            if (!_hookLiveLogged)
            {
                _hookLiveLogged = true;
                try { LLMNPCPlugin.Log.LogInfo("spectate: camera override live — hook is writing the view each frame"); } catch (Exception) { }
            }
            try
            {
                Transform head = HeadBoneTf(a.Eater);
                Transform t = cam.transform;
                if (a.Pov == (int)SpectatePov.FirstPerson)
                {
                    // The AI's own eye placement: head-local (0, up 0.06, fwd
                    // configured offset) — TransformPoint scales the offset with
                    // the body, so grown eaters get grown eye offsets.
                    float fwd = SpectateLogic.FpForwardOffset(
                        Plugin != null && Plugin._cfgCamForward != null ? Plugin._cfgCamForward.Value : 0.22f);
                    Vector3 pos = head.TransformPoint(new Vector3(0f, 0.06f, fwd));
                    t.position = pos;
                    t.rotation = head.rotation;
                    if (!a.NearTaken)
                    {
                        try
                        {
                            a.OrigNear = cam.nearClipPlane;
                            a.NearTaken = true;
                            cam.nearClipPlane = Plugin != null && Plugin._cfgCamNearClip != null
                                ? Mathf.Clamp(Plugin._cfgCamNearClip.Value, 0.01f, 0.5f)
                                : 0.10f;
                        }
                        catch (Exception) { }
                    }
                    a.CamInit = true;
                    a.CamPos = pos;
                    return;
                }

                Vector3 headPos = head.position;
                Vector3 f = a.Eater.transform.forward;
                double yaw = SpectateLogic.AzimuthDeg(f.x, f.z);
                float size = 1f;
                try { size = a.Eater.sizeInflater.GetSize(); } catch (Exception) { }
                float dist = SpectateLogic.FollowDistance(size);
                var pose = SpectateLogic.FollowPose(headPos.x, headPos.y, headPos.z, yaw, dist);
                Vector3 want = new Vector3((float)pose.Cx, (float)pose.Cy, (float)pose.Cz);
                Vector3 look = new Vector3((float)pose.Lx, (float)pose.Ly, (float)pose.Lz);
                // Wall-safe boom: pull in when geometry sits between the head and
                // the wanted spot (cramped rooms otherwise swallow the view).
                try
                {
                    Vector3 dir = want - headPos;
                    float len = dir.magnitude;
                    if (len > 0.01f)
                    {
                        Vector3 nd = dir / len;
                        RaycastHit hit;
                        if (Physics.Raycast(headPos, nd, out hit, len) && hit.distance < len)
                            want = headPos + nd * SpectateLogic.BoomClampDist(len, hit.distance);
                    }
                }
                catch (Exception) { }
                Vector3 next = want;
                if (a.CamInit)
                {
                    float k = SpectateLogic.SmoothFactor(8f, Time.unscaledDeltaTime);
                    next = Vector3.Lerp(t.position, want, k);
                }
                if (a.NearTaken)
                {
                    try { cam.nearClipPlane = a.OrigNear; a.NearTaken = false; } catch (Exception) { }
                }
                t.position = next;
                t.rotation = Quaternion.LookRotation(look - next, Vector3.up);
                a.CamInit = true;
                a.CamPos = next;
            }
            catch (Exception) { }
        }

        internal static void ResetForTests()
        {
            lock (_lock) _active = null;
            lock (_despawn) _despawn.Clear();
            Plugin = null;
        }
    }

    // ------------------------------------------------------------------
    // eat_player — the module tool. Registers like every other module
    // (Cooking/Farming/…): act-mode gets the JSON tool + schema, console mode
    // gets the exact command and the Help() line.
    // ------------------------------------------------------------------
    internal static class EatPlayerModule
    {
        private static bool _registered;
        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            ModuleRegistry.Tool("eat_player", (n, p) => n.ToolEatPlayer(p));
        }
    }

    internal partial class NPCInstance
    {
        // eat_player — swallow the LOCAL PLAYER whole. Gated by [LittleTreats]
        // SwallowAgentEat; adjacency checked against SwallowAgentEatRange. The
        // spectate takeover starts inside Begin() and lasts until the egg's
        // hatch or the swallow-abort chat command.
        //
        // CRASH FIX: everything below is MAIN-THREAD work — Photon ownership
        // + RPCs, grabber/Rigidbody surgery, the player input-lock and body
        // parking. The tool used to run it all raw on the agent thread (the
        // in-body verbs kick/cum/surrender marshal; eat_player was the last
        // act left) and the first real invocation killed the process — Photon
        // + Unity component APIs are main-thread-only (the wave-41 lesson).
        // The whole flow now marshals like every other mutating act.
        internal object ToolEatPlayer(JsonObj p)
        {
            try
            {
                return RunOnMainThread(() => (object)EatPlayerOnMain(), 10000)
                    ?? (object)new { ok = false, reason = "main_thread_busy", note = "the main thread did not answer in 10s — try again next turn" };
            }
            catch (Exception e)
            {
                return new { ok = false, reason = "main_thread_failed", note = Shorten(e.Message, 160) };
            }
        }

        // The eat_player gates + takeover, guaranteed to run on the main thread.
        private object EatPlayerOnMain()
        {
            var plugin = SwallowManager.Plugin;
            if (plugin == null) return new { ok = false, reason = "no_plugin" };
            var cfg = plugin._cfgSwallowAgentEat;
            if (cfg == null || !cfg.Value)
                return new { ok = false, reason = "disabled", note = "eat_player is not enabled ([LittleTreats] SwallowAgentEat)" };
            if (_kobold == null) return new { ok = false, reason = "no_body" };

            Kobold player = null;
            try { if (PlayerPossession.TryGetPlayerInstance(out var pp)) player = pp.kobold; } catch (Exception) { }
            if (player == null) return new { ok = false, reason = "no_player", note = "there is no player body in the world" };

            float range = SwallowManager.AgentEatRange();
            float distSq = 0f;
            try { distSq = Vector3.SqrMagnitude(_kobold.transform.position - player.transform.position); } catch (Exception) { }
            bool busy = false, spectating = false;
            try { busy = SwallowManager.EaterBusy(_kobold); } catch (Exception) { }
            try { spectating = SpectateManager.IsSpectating(player); } catch (Exception) { }
            string reason = SpectateLogic.AgentEatGate(true, true, true, _kobold == player,
                distSq, range * range, busy, spectating);
            if (reason != null)
                return new { ok = false, reason = reason, note = "the player must be within "
                    + range.ToString("0.0", CultureInfo.InvariantCulture) + "m of your mouth, and you must not already be digesting someone" };

            if (!SwallowManager.TryEatPlayer(this))
                return new { ok = false, reason = "refused", note = "they are already being swallowed by someone" };

            return new
            {
                ok = true,
                note = "you swallowed the player WHOLE — they are inside you now, watching everything through your body's eyes and unable to act. "
                     + SwallowManager.PlayerOutcomeNote(_kobold),
            };
        }
    }
}