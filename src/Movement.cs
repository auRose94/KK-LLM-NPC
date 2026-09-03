// FixedUpdate: walk validation, burst timers, idle friction damping, gaze steering, camera-clip fix.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Photon.Pun;
using Photon.Realtime;

namespace KKLLMNPC
{
    public partial class LLMNPCPlugin : BaseUnityPlugin, Photon.Realtime.IOnEventCallback
    {
        private float _lastBumpTime = -99f;   // Time.unscaledTime of last wall-contact

        private string _bumpInfo;             // human-readable "hit wall to the left"

        private const float WalkProbeRange = 1.4f; // how far ahead we validate walking

        // True if the collider belongs to our own kobold (its body/limbs) — ignore it.
        private bool IsOwnCollider(Collider c)
        {
            if (c == null || _kobold == null) return false;
            return c.transform.root == _kobold.transform.root;
        }

        // Fan out left/right to find a heading with no near obstacle; return the
        // yaw delta to steer, or 0 if everything is blocked.
        // Obstacle fanning: scan progressively wider left/right offsets from the blocked
        // direction (biased along the wall's tangent) until a clear heading is found,
        // so the kobold slides along walls instead of grinding into them.
        private float FindClearHeading(Vector3 eye, Vector3 hitNormal)
        {
            // Try increasingly wide offsets, preferring the side of the surface normal
            // so we slide along walls instead of bouncing off them.
            float side = Vector3.Cross(hitNormal, Vector3.up).y >= 0f ? 1f : -1f;
            float[] offsets = { 20f * side, -20f * side, 40f * side, -40f * side, 65f * side, -65f * side, 90f * side, -90f * side };
            foreach (var off in offsets)
            {
                Vector3 dir = Quaternion.Euler(0, _yawDeg + off, 0) * Vector3.forward;
                if (!Physics.Raycast(eye, dir, WalkProbeRange * 1.3f, ~0, QueryTriggerInteraction.Ignore))
                    return off;
            }
            return 0f; // boxed in
        }

        // LLM-facing continuous movement command, written by LLM thread,
        // consumed in FixedUpdate on the main thread. Plain fields + Interlocked.
        private float _moveLocalZ;      // forward speed
        private float _moveLocalX;      // strafe (right=+, left=-)

        private bool  _moveJump;

        private bool  _moveRun;         // false => controller.inputWalking (slow); true => full speed

        private float _moveUntilTime;   // unscaled time when the current walk burst ends

        private float _crouch = 0f;     // 0..1 — applied every FixedUpdate

        private float _manualCrouchSet = -99f; // last time the model chose crouch

        private float _clipSince = -99f;       // when camera first appeared clipped

        private float _lastClipFix;            // cooldown on auto-crouch adjustments

        private float _yawOffsetDeg;    // pending yaw to apply relative to current facing

        private float _pitchDeg;        // absolute pitch for the eye/camera

        private float _yawDeg;          // absolute yaw (derived from body + offset)

        private readonly object _stateLock = new object();

        // ------------------------------------------------------------------
        // movement plumbing: LLM thread sets fields; FixedUpdate applies them
        // ------------------------------------------------------------------
        // Queue a bounded walk burst. duration>0 auto-stops after that many simulated
        // seconds (a burst expires promptly, so a forgotten command can't drift).
        private void SetMove(float forwardSpeed, float strafeSpeed, bool jump, float turnDeg, float durationSec, bool run)
        {
            lock (_stateLock)
            {
                _moveLocalZ = Mathf.Clamp(forwardSpeed, -8f, 8f);
                _moveLocalX = Mathf.Clamp(strafeSpeed, -8f, 8f);
                _moveJump = jump;
                _moveRun = run;
                _yawOffsetDeg = turnDeg;
                _moveUntilTime = durationSec > 0f ? Time.unscaledTime + durationSec : 0f;
            }
        }

        // Small unsolicited reactions to stimuli the LLM might miss or react to too
        // slowly. Only fires on state changes; cooldown keeps it from spamming.
        private void MaybeAmbientComment()
        {
            if (!IsAlive(_kobold)) return;
            if (Time.unscaledTime - _lastAmbientTime < 4f) return;

            // Arousal spiking while being played with — moan without waiting on the LLM.
            if (IsInAnimationStation() || IsPenetrated() || IsDickInside())
            {
                float stim = _kobold.stimulation;
                if (_ambientStimPrev >= 0f && stim > _ambientStimPrev + 0.06f)
                {
                    EmitAmbient("mmm~");
                    _ambientStimPrev = stim;
                    return;
                }
            }
            _ambientStimPrev = _kobold.stimulation;
        }

        // Say a short ambient line: bubble + console, no chat-window spam.
        private void EmitAmbient(string text)
        {
            _lastAmbientTime = Time.unscaledTime;
            Logger.LogInfo("[" + MyName() + "] " + text);
            RunOnMainThreadAsync(() =>
            {
                try
                {
                    var chatter = _kobold != null ? _kobold.GetComponentInChildren<Chatter>(true) : null;
                    if (chatter != null) chatter.DisplayMessage(text, 2f);
                }
                catch (Exception) { }
            });
        }

        // direction of stimulation, reused by ambient.
        private float _ambientStimPrev = -1f;

        // True if the kobold was addressed by its name in the chat text.
        private bool AddressedToMe(string chat)
        {
            if (string.IsNullOrEmpty(chat)) return false;
            string me = MyName().ToLowerInvariant();
            string c = chat.ToLowerInvariant();
            return c.Contains(me) || c.StartsWith(me + ",") || c.StartsWith("hey " + me);
        }

        // Hard stop: clears the queued intent AND the controller input immediately,
        // so the body doesn't coast on a stale direction between think ticks.
        private void StopMove()
        {
            lock (_stateLock) { _moveLocalZ = 0f; _moveJump = false; _moveUntilTime = 0f; _crouch = 0f; }
            // Also zero the controller immediately — otherwise it keeps the last
            // inputDir applied and drifts until the next physics write.
            if (_controller != null)
            {
                try { _controller.inputDir = Vector3.zero; _controller.inputJump = false; } catch (Exception) { }
            }
        }

        private void FixedUpdate()
        {
            try { FixedUpdateSafe(); }
            catch (Exception e)
            {
                // A destroyed/invalid body must never take down the physics loop.
                if (Time.frameCount % 120 == 0) Logger.LogWarning("FixedUpdate: " + e.Message);
            }
        }

        // The only Unity-physics touchpoint for motion. Reads queued movement intent
        // written by the LLM thread, expends burst timers, probes walls/ledges ahead,
        // keeps yaw in sync (only when there's actual intent), then writes inputDir +
        // inputJump + inputWalking + SetInputCrouched on the game's own controller.
        // Idle → rigidbody velocity is damped (prevents the 'magnetized drift' bug).
        private void FixedUpdateSafe()
        {
            if (_controller == null || _kobold == null) return;
            if (!IsPlayableScene() || !IsAlive(_kobold) || !IsAlive(_controller)) { RunOnMainThreadAsync(TeardownBody); return; }

            // Ownership watchdog: controller only applies velocity when we own the
            // PhotonView. Re-assert a few times (other mods can transfer it away).
            if (_photonView != null && !_photonView.IsMine && PhotonNetwork.InRoom
                && Time.unscaledTime - _lastOwnershipTry > 3f)
            {
                _lastOwnershipTry = Time.unscaledTime;
                Logger.LogWarning($"KKLLMNPC: not owner of '{_kobold.name}' (owner={_photonView.Owner?.NickName ?? "?"}) — re-asserting");
                try { _photonView.TransferOwnership(PhotonNetwork.LocalPlayer); } catch (Exception) { }
                try { if (!_photonView.IsMine) _photonView.RequestOwnership(); } catch (Exception) { }
            }

            float fwd, strafe, turn, crouch; bool jump; float until;
            lock (_stateLock) { fwd = _moveLocalZ; strafe = _moveLocalX; turn = _yawOffsetDeg; jump = _moveJump; crouch = _crouch; _yawOffsetDeg = 0f; until = _moveUntilTime; }

            // If a go_to target is active, steer toward it (a bounded "approach"
            // behavior); clear it once we're close or the walk burst expired.
            if (_navTarget.HasValue)
            {
                Vector3 toT = _navTarget.Value - _kobold.transform.position;
                toT.y = 0;
                float dist = toT.magnitude;
                if (dist < 0.8f)
                {
                    _navTarget = null;
                    StopMove();
                    _blockedInfo = "arrived" + (_navTargetName != null ? " at " + _navTargetName : "");
                }
                else
                {
                    // Keep heading fresh so the obstacle-steer fans around the target.
                    float yaw = Mathf.Atan2(toT.x, toT.z) * 57.29578f;
                    _yawDeg = Mathf.Repeat(yaw, 360f);
                }
            }

            // Burst timed out — stop before applying any more drive.
            if (until > 0f && Time.unscaledTime >= until)
            {
                lock (_stateLock) { _moveLocalZ = 0f; _moveLocalX = 0f; _moveUntilTime = 0f; }
                fwd = 0f; strafe = 0f;
            }

            // When there's no active walk command, zero the drive every frame so no
            // residual inputDir lingers between LLM calls (the "magnetize" drift).
            if (Mathf.Approximately(fwd, 0f) && Mathf.Approximately(strafe, 0f))
            {
                try { _controller.inputDir = Vector3.zero; } catch (Exception) { }
                // And bleed off the rigidbody's leftover velocity — Friction() alone
                // is too gentle and only runs when the controller considers itself
                // in control.
                if (_controller.body != null)
                {
                    var rb = _controller.body;
                    Vector3 v = rb.velocity;
                    v.x *= 0.78f; v.z *= 0.78f; // per-physics-frame damping
                    if (Mathf.Abs(v.x) < 0.05f) v.x = 0f;
                    if (Mathf.Abs(v.z) < 0.05f) v.z = 0f;
                    try { rb.velocity = v; } catch (Exception) { }
                    // And kill any residual spin so the facing axis stops creeping.
                    try { rb.angularVelocity = Vector3.zero; } catch (Exception) { }
                }
            }

            // Turning rotates the rigidbody (whole body) via physics, not the
            // transform — so animations and colliders follow and nothing clips.
            if (Mathf.Abs(turn) > 0.001f)
                _yawDeg = Mathf.Repeat(_yawDeg + turn, 360f);

            // WALK VALIDATION: raycast where we're about to go. If blocked ahead,
            // don't walk into it — stop forward drive and auto-steer to a clear
            // heading, then report the blockage so the LLM picks a new direction.
            _blockedInfo = null;
            // Wall-proximity scan every physics frame: short rays in 4 directions
            // around the body so the model knows "wall on my left" before it hits.
            _bumpInfo = null;
            ProbeWallProximity();
            float fwdOut = Mathf.Clamp(fwd, -1f, 1f);

            // "Hit a wall": trying to move but the body barely advances (friction
            // holds us against an obstacle the forward ray already caught).
            if (fwdOut != 0f && _controller.body != null)
            {
                Vector3 hv = _controller.body.velocity; hv.y = 0;
                if (hv.magnitude < 0.15f && Time.unscaledTime - _lastBumpTime > 0.6f)
                {
                    _lastBumpTime = Time.unscaledTime;
                    _blockedInfo = "bumped a wall — turn";
                    _needImageAfterBump = true; // show the model what it hit
                }
            }
            if (fwdOut > 0.01f)
            {
                Vector3 eye = _head != null
                    ? _head.position
                    : _kobold.transform.position + Vector3.up * 0.6f;
                eye += Vector3.up * -0.15f; // chest height, not eye, for body clearance
                Vector3 dir = Quaternion.Euler(0, _yawDeg, 0) * Vector3.forward;
                RaycastHit hit;
                if (Physics.Raycast(eye, dir, out hit, WalkProbeRange, ~0, QueryTriggerInteraction.Ignore)
                    && !IsOwnCollider(hit.collider))
                {
                    // Surface in front — try to find a clear heading by fan-steering.
                    float steer = FindClearHeading(eye, hit.normal);
                    if (steer != 0f)
                    {
                        _yawDeg = Mathf.Repeat(_yawDeg + steer, 360f);
                        _blockedInfo = "blocked; steered " + (steer > 0 ? "right" : "left");
                    }
                    else
                    {
                        fwdOut = 0f;
                        _blockedInfo = "blocked, no clear way (dist " + F(hit.distance) + ")";
                    }
                }
                // LEDGE GUARD: only hard-stop on a *big* drop. Small drops (<=1.6m)
                // are fine to hop down — and if the model is jumping, it's choosing
                // to go over on purpose. Report the drop height so it can decide.
                if (fwdOut > 0.01f && _blockedInfo == null)
                {
                    Vector3 aheadDown = _kobold.transform.position + dir * 0.8f + Vector3.up * 0.5f;
                    RaycastHit lhit;
                    float drop = Physics.Raycast(aheadDown, Vector3.down, out lhit, 8f, ~0, QueryTriggerInteraction.Ignore)
                        ? lhit.distance - 0.5f : 8f;
                    _ledgeDrop = drop > 0.5f ? drop : (float?)null; // expose to perception
                    if (drop > 2.6f && !jump)   // big fall → stop
                    {
                        fwdOut = 0f;
                        _blockedInfo = "big drop ahead (" + F(drop) + "m); stopped";
                        _needImageAfterBump = true;
                    }
                    else if (drop > 0.7f && drop <= 2.6f && !jump)
                        _blockedInfo = "ledge " + F(drop) + "m — you can walk off or jump down";
                    // jump=true or small drop → let it proceed (controller handles fall).
                }
            }

            // inputDir is consumed as a world-space direction by the controller's
            // Accelerate(); walls stop it via normal rigidbody collision.
            Vector3 worldDir = Quaternion.Euler(0, _yawDeg, 0) * new Vector3(Mathf.Clamp(strafe, -1f, 1f), 0f, fwdOut);
            float mag = worldDir.magnitude;
            if (mag > 1f) worldDir /= mag;
            _controller.inputDir = new Vector3(worldDir.x, 0f, worldDir.z);
            _controller.inputJump = jump;
            // Walk vs run: inputWalking=true scales effectiveSpeed down by the
            // game's walkSpeedMultiplier; default is walk, run only when asked.
            // inputWalking=false means "run" which ALSO enables the CharacterControllerAnimator's
            // body-follows-eye rule (IL: when !inputWalking, facingRot is taken from eyeRot).
            // We WANT body == eye, so we always run the body in "run" mode here. True walk
            // speed is still gated by the controller's own speedMultiplier elsewhere.
            try { _controller.inputWalking = false; } catch (Exception) { }
            try { _controller.SetInputCrouched(crouch); } catch (Exception) { }
            if (jump) lock (_stateLock) { _moveJump = false; } // one-shot

            // The Game Drives The Body: the CharacterControllerAnimator contains a
            // LookAtHandler that turns the head AND the hip toward `eyeRot`. The only
            // way to stop the two systems fighting is to feed the animator the yaw we
            // actually want the body to face, and write NO rotation to the rigidbody
            // ourselves. Walking: eyeRot = movement heading (so the animator pivots the
            // body into the walk direction). Idle: eyeRot = body's current yaw (so the
            // hips stop chasing and the body just stays put).
            if (_charAnimator != null)
            {
                bool walking = worldDir.sqrMagnitude > 0.0004f;
                float eyeYaw = walking ? Mathf.Atan2(worldDir.x, worldDir.z) * 57.29578f : BodyYaw();
                _charAnimator.SetEyeRot(new Vector2(eyeYaw, -_pitchDeg));
            }
            if (_cam != null)
                _cam.transform.rotation = Quaternion.Euler(_pitchDeg, _yawDeg, 0f);

            // CAMERA-CLIP detection + auto-crouch fix. If the head/camera is buried
            // in geometry (rays from the head hit something nearer than the camera
            // offset, or we collide with something right at the head), the first-person
            // image is uselessly full of face/wall. Ease crouch up to clear it — the
            // camera rides up as the body lowers.
            CheckCameraClip();

            // Ambient commentary: react to what's happening even when not in a station.
            MaybeAmbientComment();

            // While mounted on a station, body is driven by the animator; we still
            // steer the eyes/camera so it visibly looks around instead of staring
            // blankly, and react to rising arousal.
            if (IsInAnimationStation())
            {
                // If someone's penetrating us (or we're inside someone), lock gaze on
                // them — like watching who you're with. Otherwise wander.
                Transform partner = MostRecentPenetrationSource();
                if (partner != null)
                {
                    Vector3 to = partner.position - _head.position;
                    Vector3 flat = to; flat.y = 0;
                    float desiredYaw = Mathf.Atan2(flat.x, flat.z) * 57.29578f;
                    float desiredPitch = Mathf.Clamp(Mathf.Atan2(-to.y, Mathf.Max(0.2f, flat.magnitude)) * 57.29578f, -89f, 89f);
                    _yawDeg = MoveAngleTowards(_yawDeg, desiredYaw, 80f * Time.fixedDeltaTime);
                    _pitchDeg = Mathf.MoveTowards(_pitchDeg, desiredPitch, 80f * Time.fixedDeltaTime);
                }
                else
                {
                    _gazeTimer -= Time.fixedDeltaTime;
                    if (_gazeTimer <= 0f)
                    {
                        _gazeTimer = UnityEngine.Random.Range(0.8f, 2.2f);
                        _gazeYaw = UnityEngine.Random.Range(-35f, 35f);
                        _gazePitch = UnityEngine.Random.Range(-12f, 18f);
                    }
                    // Slowly drift the gaze toward the current target so it looks like it's scanning.
                    _yawDeg = Mathf.Repeat(_yawDeg + Mathf.Clamp(_gazeYaw, -1f, 1f) * 20f * Time.fixedDeltaTime, 360f);
                    _pitchDeg = Mathf.MoveTowards(_pitchDeg, Mathf.Clamp(_gazePitch, -89f, 89f), 20f * Time.fixedDeltaTime);
                }

                // Arousal reaction: on a noticeable stim jump, make a sound (koba's
                // chatter yowl pack) — visible to nearby players, like the real thing.
                float stim = _kobold.stimulation;
                if (_lastStim >= 0f && stim > _lastStim + 0.08f && Time.unscaledTime - _lastMoan > 3f)
                {
                    _lastMoan = Time.unscaledTime;
                    RunOnMainThreadAsync(() =>
                    {
                        try
                        {
                            var chatter = _kobold.GetComponentInChildren<Chatter>(true);
                            if (chatter != null)
                            {
                                // Use the game's yowl pack directly — one short vocal.
                                var pack = typeof(Chatter).GetField("yowls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                                    ?.GetValue(chatter) as AudioPack;
                                var src = chatter.GetComponent<AudioSource>();
                                if (pack != null && src != null) { pack.PlayOneShot(src); return; }
                                // Fallback: tiny self-talk bubble so others see something.
                                chatter.DisplayMessage("~", 1.2f);
                            }
                        }
                        catch (Exception e) { Logger.LogWarning("moan: " + e.Message); }
                    });
                }
                _lastStim = stim;
            }
            else _lastStim = -1f;
        }

        // Idle behavior while in a station: wandering gaze + reaction to arousal.
        private float _gazeTimer;

        private float _gazeYaw;

        private float _gazePitch;

        private float _lastStim = -1f;

        private float _lastMoan;

        // Heuristic camera-clip detector: the first-person camera sits ahead of the
        // head at _cfgCamForward; if a short forward ray from just behind the camera
        // hits geometry *closer than the camera*, the lens is inside something and
        // the image is mostly face/wall. When detected, ease crouch up (so the head
        // rises with the body as it drops) until it clears or maxes out.
        // Camera-clip detector: if a short forward ray from behind the camera hits
        // geometry *closer than the camera offset*, the lens is buried in the face/wall
        // and the first-person image is useless — ease crouch up so the body drops and
        // the head+camera clear the surface. Only acts when the model hasn't explicitly
        // set a crouch level recently.
        private void CheckCameraClip()
        {
            if (!IsAlive(_head) || !IsAlive(_cam)) { _clipSince = -99f; return; }
            float camFwd = _cfgCamForward.Value;
            // Ray from just behind the camera toward view direction; shorter distance
            // than the camera's forward offset means the camera pokes *into* geometry.
            Vector3 start = _cam.transform.position - _cam.transform.forward * Math.Max(0.02f, camFwd * 0.8f);
            RaycastHit hit;
            bool clipped = Physics.Raycast(start, _cam.transform.forward, out hit, camFwd, ~0, QueryTriggerInteraction.Ignore)
                           && !IsOwnCollider(hit.collider);

            if (clipped)
            {
                if (_clipSince < 0f) _clipSince = Time.unscaledTime;
                // Only interfere if the model hasn't explicitly chosen a crouch level.
                if (Time.unscaledTime - _manualCrouchSet < 5f) return;
                if (Time.unscaledTime - _clipSince > 0.4f && Time.unscaledTime - _lastClipFix > 1.5f && _crouch < 1f)
                {
                    _crouch = Mathf.Min(1f, _crouch + 0.15f);
                    _lastClipFix = Time.unscaledTime;
                    _blockedInfo = "camera clipped; crouching to clear view (crouch=" + F(_crouch) + ")";
                }
            }
            else
            {
                _clipSince = -99f;
                // Ease back out if the clip cleared — but don't stomp the model's
                // explicit crouch setting.
                if (_crouch > 0f && Time.unscaledTime - _manualCrouchSet > 10f)
                {
                    _crouch = Mathf.MoveTowards(_crouch, 0f, Time.fixedDeltaTime * 0.5f);
                }
            }
        }
    }
}
