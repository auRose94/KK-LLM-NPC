// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
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
    internal partial class NPCInstance
    {
        // True if the collider belongs to our own kobold (its body/limbs) — ignore it.
        internal bool IsOwnCollider(Collider c)
        {
            if (c == null || _kobold == null) return false;
            return c.transform.root == _kobold.transform.root;
        }

        // Fan out left/right to find a heading with no near obstacle; return the
        // yaw delta to steer, or 0 if everything is blocked.
        // Obstacle fanning: scan progressively wider left/right offsets from the blocked
        // direction (biased along the wall's tangent) until a clear heading is found,
        // so the kobold slides along walls instead of grinding into them.
        // True when the obstacle the forward ray hit is hop-and-over-able: top within
        // ~1m of the feet, ground beyond it, body grounded, not a kobold/usable.
        // Sets _autoHopPending — consumed below as the jump input for this frame.
        private float _lastAutoHop;
        private bool AutoHopLowObstacle(RaycastHit hit, Vector3 dir, bool jumpAlready)
        {
            if (jumpAlready) return false;
            if (Time.unscaledTime - _lastAutoHop < 1.4f) return false;
            try
            {
                if (hit.collider == null || IsOwnCollider(hit.collider)) return false;
                if (hit.collider.GetComponentInParent<Kobold>() != null) return false;
                if (hit.collider.GetComponentInParent<GenericUsable>() != null
                    || hit.collider.GetComponentInParent<UsableMachine>() != null) return false; // use it, don't hop it
                // Grounded check: we can't hop mid-air.
                if (_controller.body != null && Mathf.Abs(_controller.body.velocity.y) > 0.3f) return false;
                // Obstacle top: straight down from above the hit point.
                Vector3 above = hit.point + dir * 0.15f + Vector3.up * 2.5f;
                RaycastHit topHit;
                float topY;
                if (Physics.Raycast(above, Vector3.down, out topHit, 3f, ~0, QueryTriggerInteraction.Ignore))
                    topY = above.y - topHit.distance;
                else return false; // no solid top within reach — not a hop target
                float feetY = _kobold != null ? _kobold.transform.position.y : topY;
                if (topY - feetY <= 0.05f) return false;            // flush — it's floor
                if (topY - feetY > 1.0f) return false;              // too tall to hop
                // Ground beyond: landable surface past the obstacle, not a big drop.
                RaycastHit groundHit;
                Vector3 beyond = hit.point + dir * 1.2f + Vector3.up * 1.6f;
                if (!Physics.Raycast(beyond, Vector3.down, out groundHit, 4f, ~0, QueryTriggerInteraction.Ignore)) return false;
                float beyondY = groundHit.point.y;
                if (beyondY < feetY - 3f) return false;             // cliff, not a landing
                _lastAutoHop = Time.unscaledTime;
                _autoHopPending = true;
                return true;
            }
            catch (Exception) { return false; }
        }

        // Sampled every ~1.2s while steering at a nav target: accumulate the ABSOLUTE
        // bearing rotation; decay it when the distance is actually shrinking. When
        // ~110°+ of bearing rotation accumulated WITHOUT distance progress, the body
        // is circling — drop into TryRepathPastStuck (hard replan) and reset.
        private void NavCircleWatch(Vector3 toT, float dist)
        {
            try
            {
                Vector3 flatTarget = toT; flatTarget.y = 0f;
                // Route changed (or first sight) — reset everything.
                if (_watchTarget != flatTarget || _watchLastDist < 0f)
                {
                    _watchTarget = flatTarget;
                    _watchLastDist = dist;
                    _watchLastSample = Time.unscaledTime;
                    _watchLastBearing = dist > 0.01f ? Mathf.Atan2(toT.x, toT.z) * Consts.Rad2Deg : 0f;
                    _watchBearingSum = 0f;
                    return;
                }
                if (Time.unscaledTime - _watchLastSample < 1.2f) return;
                float bearing = dist > 0.01f ? Mathf.Atan2(toT.x, toT.z) * Consts.Rad2Deg : _watchLastBearing;
                float dB = Mathf.Abs(Mathf.Repeat(bearing - _watchLastBearing, 360f));
                if (dB > 180f) dB = 360f - dB;
                _watchBearingSum += dB;
                bool progress = dist < _watchLastDist - 0.35f;
                if (progress)
                    _watchBearingSum = Mathf.Max(0f, _watchBearingSum - 60f);
                else if (_watchBearingSum > 110f)
                {
                    _watchBearingSum = 0f;
                    _blockedInfo = "circling the target — re-planning the route";
                    Logger.LogInfo("movement: anti-circle breaker fired (bearing rotated, distance stalled at " + F2(dist) + "m)");
                    TryRepathPastStuck();
                }
                _watchLastDist = dist;
                _watchLastBearing = bearing;
                _watchLastSample = Time.unscaledTime;
            }
            catch (Exception) { }
        }

        private static string F2(float f) { return f.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture); }

        // Third-strike unstick: re-plan the route around the fixed spot the body
        // keeps grinding. Falls back to the local grid when the world map has no
        // answer. Main thread (FixedUpdate) — planners here are physics-safe.
        private void TryRepathPastStuck()
        {
            try
            {
                Vector3 goal;
                lock (_stateLock)
                {
                    if (_pathGoalSet) goal = _pathGoal;
                    else if (_navTarget.HasValue) goal = SetYFlat(_navTarget.Value);
                    else return;
                }
                List<Vector3> np = null;
                if (WorldMap.Ready)
                {
                    try { np = WorldMap.FindPathSmoothed(_kobold.transform.position, new Vector3(goal.x, _kobold.transform.position.y, goal.z)); }
                    catch (Exception) { }
                }
                if (np == null || np.Count < 2)
                {
                    try { np = FindPath(_kobold.transform.position, new Vector3(goal.x, _kobold.transform.position.y, goal.z)); }
                    catch (Exception) { }
                }
                if (np == null || np.Count < 2) return;
                lock (_stateLock)
                {
                    _path = np;
                    _pathIdx = 0;
                    _navTarget = np[0];
                    _trailJumps = null;
                }
                Logger.LogInfo("movement: stuck-breaker re-planned the route (" + (np.Count - 1) + " legs)");
            }
            catch (Exception e) { try { Logger.LogWarning("repath: " + e.Message); } catch (Exception) { } }
        }

        private static Vector3 SetYFlat(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        // Which way the last fan-steer leaned — the stuck-breaker sidesteps toward it.
        private float _lastFanSteerSide;
        private float _activeSteerOff;
        private float _steerLockedUntil;
        // Anti-circling watch state (see NavCircleWatch).
        private Vector3 _watchTarget;
        private float _watchLastSample;
        private float _watchLastDist = -1f;
        private float _watchLastBearing;
        private float _watchBearingSum;
        private float _bumpStreak;
        private float _unstickStrafeSide;
        private float _unstickUntil;
        private bool _autoHopPending;

        private float FindClearHeading(Vector3 eye, Vector3 hitNormal)
        {
            // Try increasingly wide offsets, preferring the side of the surface normal
            // so we slide along walls instead of bouncing off them. Each candidate
            // must clear THREE lanes — center + both body-half-width side rays; a
            // single center ray "fits" door frames the body then clips (the stuck-on-
            // door-frames failure).
            float side = Vector3.Cross(hitNormal, Vector3.up).y >= 0f ? 1f : -1f;
            // HYSTERESIS (the bee-in-a-window fix): without it, offset +20 clears this
            // frame and −20 the next, and the body wobbles at a wall forever. A steer
            // that actually cleared stays preferred for a stability window.
            if (_activeSteerOff != 0f && Time.unscaledTime - _steerLockedUntil < 0f)
            {
                // still locked in: retest the SAME offset first
                Vector3 keepDir = Quaternion.Euler(0, _yawDeg + _activeSteerOff, 0) * Vector3.forward;
                Vector3 keepRight = new Vector3(-keepDir.z, 0f, keepDir.x).normalized;
                if (LaneClear(eye, keepDir, 0f) && LaneClear(eye, keepDir, WorldMap.BodyHalfWidthM)
                    && LaneClear(eye, keepDir, -WorldMap.BodyHalfWidthM))
                    return _activeSteerOff;
            }
            float[] offsets = _activeSteerOff != 0f && Time.unscaledTime - _steerLockedUntil < 1.2f
                ? new float[] { _activeSteerOff, 20f * side, -20f * side, 40f * side, -40f * side, 65f * side, -65f * side, 90f * side, -90f * side }
                : new float[] { 20f * side, -20f * side, 40f * side, -40f * side, 65f * side, -65f * side, 90f * side, -90f * side };
            foreach (var off in offsets)
            {
                Vector3 dir = Quaternion.Euler(0, _yawDeg + off, 0) * Vector3.forward;
                Vector3 right = new Vector3(-dir.z, 0f, dir.x).normalized;
                if (LaneClear(eye, dir, 0f) && LaneClear(eye, dir, WorldMap.BodyHalfWidthM)
                    && LaneClear(eye, dir, -WorldMap.BodyHalfWidthM))
                {
                    _lastFanSteerSide = off > 0f ? 1f : -1f;
                    // Lock this heading for a moment so the next blocked frame keeps
                    // steering the same way instead of flip-flopping.
                    _activeSteerOff = off;
                    _steerLockedUntil = Time.unscaledTime + 1.2f;
                    return off;
                }
            }
            return 0f; // boxed in
        }

        private bool LaneClear(Vector3 eye, Vector3 dir, float lateral)
        {
            Vector3 o = eye + (dir.y == 0 ? new Vector3(-dir.z, 0f, dir.x).normalized * lateral : Vector3.zero);
            return !Physics.Raycast(o, dir, WalkProbeRange * 1.3f, ~0, QueryTriggerInteraction.Ignore);
        }

        // If the object a forward ray hit is a door/gate (a GenericUsable classified as
        // 'door'), open it via LocalUse so the body can walk through — unless we just did
        // so for this same door (avoids toggle-fluttering open↔closed every physics frame).
        private bool MaybeOpenDoorAhead(RaycastHit hit)
        {
            try
            {
                var u = hit.collider.GetComponent<GenericUsable>() ?? hit.collider.GetComponentInParent<GenericUsable>();
                if (u == null) return false;
                int inst = u.GetInstanceID();
                if (inst == _lastDoorTried && Time.unscaledTime - _lastDoorTryTime < 1.5f) return false;
                string n = CleanName(u.name);
                string cls = ClassifyUsable(n);
                if (cls != "door") return false;
                if (u.CanUse(_kobold))
                {
                    u.LocalUse(_kobold);
                    _lastDoorTried = inst;
                    _lastDoorTryTime = Time.unscaledTime;
                    return true;
                }
                return false;
            }
            catch (Exception e) { Logger.LogDebug($"MaybeOpenDoorAhead error: {e.Message}"); return false; }
        }

        // ------------------------------------------------------------------
        // movement plumbing: LLM thread sets fields; FixedUpdate applies them
        // ------------------------------------------------------------------
        // Queue a bounded walk burst. duration>0 auto-stops after that many simulated
        // seconds (a burst expires promptly, so a forgotten command can't drift).
        private void SetMove(float forwardSpeed, float strafeSpeed, bool jump, float turnDeg, float durationSec, bool run)
        {
            lock (_stateLock)
            {
                _moveTargetZ = Mathf.Clamp(forwardSpeed, -8f, 8f);
                _moveTargetX = Mathf.Clamp(strafeSpeed, -8f, 8f);
                _moveJump = jump;
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
                try
                {
                    float stim = _kobold.stimulation;
                    if (_ambientStimPrev >= 0f && stim > _ambientStimPrev + 0.06f)
                    {
                        //EmitAmbient("mmm~");
                        _ambientStimPrev = stim;
                        return;
                    }
                    _ambientStimPrev = stim;
                }
                catch (Exception e) { Logger.LogDebug($"MaybeAmbientComment: {e.Message}"); }
            }
        }

        // Slow-burn horniness (main thread). Climbs only while the body is getting
        // NO stimulation — any ongoing play (game stim, station, penetration)
        // holds it; a climax (stim swinging from high to low) spends it.
        //
        // VERIFIED against the game (2026-10): kobold.stimulation is a RAW value in
        // [-20, +10] (stimulationMax=10), and the game only resets it (orgasm) when
        // energy ≥ 1 — with low energy a play station can saturate it near max and
        // never climax, which used to mean horniness was held high FOREVER while the
        // NPC sat in the station, looping. Two fixes: thresholds below are compared
        // against stim normalized to 0-1 (the Consts.HornyStim* fractions are
        // normalized by design), and a sustained ≥90%-saturated stretch (no climax,
        // presumably energy-blocked) bleeds the slow-burn down like a climax does.
        // Thread-safe: written on main thread (FixedUpdate), read on LLM thread.
        private void UpdateHorniness(float dt)
        {
            if (!IsAlive(_kobold)) return;
            try
            {
                float max = _kobold.stimulationMax > 0f ? _kobold.stimulationMax : 10f;
                float stimN = Mathf.Clamp01(_kobold.stimulation / max);
                float rate = _cfgHornyRate != null ? Mathf.Max(0f, _cfgHornyRate.Value) : 5f;

                if (_hornyPrevStim >= Consts.HornyStimHigh && stimN < Consts.HornyStimLow)
                {
                    // Climax: the game swings stimulation from high to (near) zero —
                    // and resting in a BED also relaxes stimulation down from high.
                    // Both spend the slow-burn need.
                    lock (_stateLock) { _horny = Consts.HornyPostClimax; }
                    _hornyPrevStim = stimN;
                    _hornySaturatedFor = 0f;
                    return;
                }

                // Near-max stimulation that never climaxes (low energy): bleed the
                // need down gradually after a sustained stretch, at twice the climb
                // rate, so the NPC eventually stops seeking play.
                if (stimN >= 0.9f)
                {
                    _hornySaturatedFor += dt;
                    if (_hornySaturatedFor > 60f)
                        lock (_stateLock) { _horny = Mathf.MoveTowards(_horny, Consts.HornyPostClimax, (rate / 60f) * dt * 2f); }
                }
                else _hornySaturatedFor = 0f;

                bool driven = stimN >= Consts.HornyStimDriven || IsInAnimationStation() || IsPenetrated() || IsDickInside();
                if (!driven) lock (_stateLock) { _horny = Mathf.Min(1f, _horny + (rate / 60f) * dt); }
                _hornyPrevStim = stimN;
                // Vore drive always climbs (its own cadence, weaker than lust) — only a
                // real swallow sates it (VoreAppetiteSated at Consume).
                VoreAppetiteClimb(dt);
            }
            catch (Exception e) { Logger.LogDebug($"UpdateHorniness: {e.Message}"); }
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
            lock (_stateLock) { _moveTargetZ = 0f; _moveTargetX = 0f; _moveJump = false; _moveUntilTime = 0f; _crouch = 0f; }
            // Also zero the controller immediately — otherwise it keeps the last
            // inputDir applied and drifts until the next physics write.
            if (_controller != null)
            {
                try { _controller.inputDir = Vector3.zero; _controller.inputJump = false; } catch (Exception) { }
            }
        }

        // FixedUpdateSafe is called from the plugin's FixedUpdate for each instance.
        internal void FixedUpdateSafe()
        {
            if (_controller == null || _kobold == null) return;
            if (!IsPlayableScene() || !IsAlive(_kobold) || !IsAlive(_controller)) { RunOnMainThreadAsync(TeardownBody); return; }

            // Swallow manager pins us (position/scale/lock are the manager's): skip
            // steering entirely while inside a stomach. The LLM session keeps running
            // (speaking/observing), and the body-death teardown above still runs so
            // the conversion's Photon destroy saves the persona as usual.
            if (Swallowed) { StopMove(); return; }

            // Ownership watchdog: controller only applies velocity when we own the
            // PhotonView. Re-assert a few times (other mods can transfer it away).
            if (_photonView != null && !_photonView.IsMine && PhotonNetwork.InRoom
                && Time.unscaledTime - _lastOwnershipTry > 3f)
            {
                _lastOwnershipTry = Time.unscaledTime;
                // Re-assert every 3s but warn only every 30s — a persistent fight
                // would otherwise spam the log forever.
                bool warn = Time.unscaledTime - _lastOwnershipWarn > 30f;
                if (warn) _lastOwnershipWarn = Time.unscaledTime;
                try
                {
                    string safeNick = _photonView.Owner?.NickName ?? "?";
                    safeNick = TextUtil.AsciiSafe(safeNick);
                    if (warn) Logger.LogWarning($"KKLLMNPC: not owner of '{_npcName ?? "?"}' (owner={safeNick}) — re-asserting");
                }
                catch (Exception) { if (warn) Logger.LogWarning("KKLLMNPC: not owner — re-asserting"); }
                try { _photonView.TransferOwnership(PhotonNetwork.LocalPlayer); } catch (Exception) { }
                try { if (!_photonView.IsMine) _photonView.RequestOwnership(); } catch (Exception) { }
            }

            float dt = Time.fixedDeltaTime;
            UpdateHorniness(dt);
            // Grab/carried/thrown awareness + sell-machine danger (main thread).
            try { PollCarrySense(); } catch (Exception e) { Logger.LogDebug("carry sense: " + e.Message); }
            // What OUR grabber holds (items or people it accidentally grabbed).
            try { PollHoldSense(); } catch (Exception e) { Logger.LogDebug("hold sense: " + e.Message); }
            // Route-proofing: this body standing/walking here upgrades stale "wall"
            // verdicts in the shared grid (Destiny-style demonstrated paths, e.g. a
            // room behind a door that was closed when the map baked).
            try { if (_kobold != null) WorldMap.PatchWalkable(_kobold.transform.position); } catch (Exception) { }
            // Module hooks (BodyControl etc.) — each guarded, never breaks the tick.
            try { ModuleRegistry.RunPhysics(this, dt); } catch (Exception e) { Logger.LogDebug("module physics: " + e.Message); }
            float turnRate = _cfgTurnRate != null ? _cfgTurnRate.Value : 180f;
            float accel = _cfgAccel != null ? _cfgAccel.Value : 4f;
            float decel = _cfgDecel != null ? _cfgDecel.Value : 6f;
            float brakeDist = _cfgBrakeDist != null ? _cfgBrakeDist.Value : 2f;

            // Acceleration / deceleration: lerp current speed toward target.
            lock (_stateLock)
            {
                _moveLocalZ = Mathf.MoveTowards(_moveLocalZ, _moveTargetZ, (_moveTargetZ != 0f ? accel : decel) * dt);
                _moveLocalX = Mathf.MoveTowards(_moveLocalX, _moveTargetX, (_moveTargetX != 0f ? accel : decel) * dt);
            }
            float fwd = _moveLocalZ;
            float strafe = _moveLocalX;
            float turn; bool jump; float crouch; float until;
            lock (_stateLock) { turn = _yawOffsetDeg; jump = _moveJump; crouch = _crouch; _yawOffsetDeg = 0f; until = _moveUntilTime; }

            // FOLLOW MODE: stay within a band of the host player while the mind keeps
            // working — the state the player can request ("follow me") and the model
            // can call (follow(on:true)). Overrides go_to navigation; speech/look/
            // interact still work. Re-paths via the shared world map when ready.
            if (_followMode)
            {
                bool haveP = false;
                Vector3 ppos = Vector3.zero;
                try
                {
                    if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null)
                    {
                        ppos = pp.kobold.transform.position;
                        haveP = true;
                    }
                }
                catch (Exception) { }

                if (haveP)
                {
                    Vector3 toP = ppos - _kobold.transform.position; toP.y = 0f;
                    float dp = toP.magnitude;
                    _followDist = dp;
                    if (dp > 1.8f)
                    {
                        // Decide whether to replan using milestones (PathPolicy).
                        bool needPath;
                        lock (_stateLock)
                        {
                            {
                                int remaining = _path != null ? Math.Max(0, _path.Count - 1 - _pathIdx) : 0;
                                float distToGoal = _pathGoalSet ? Vector3.Distance(_pathGoal, new Vector3(ppos.x, 0f, ppos.z)) : 0f;
                                float deviation = _path != null && _pathIdx < _path.Count ? Vector3.Distance(_kobold.transform.position, _path[Math.Min(_pathIdx, _path.Count - 1)]) : 0f;
                                float timeSincePath = _followLastPath > 0 ? Time.unscaledTime - _followLastPath : 999f;
                                bool teleported = _pathGoalSet && distToGoal > PathPolicy.TeleportDistance;

                                needPath = PathPolicy.ShouldReplan(
                                    _path != null,
                                    remaining,
                                    distToGoal,
                                    dp > 1.8f,
                                    deviation,
                                    timeSincePath,
                                    teleported);
                            }
                        }

                        if (needPath && !IsInAnimationStation())
                        {
                            List<Vector3> fpath = null;
                            if (WorldMap.Ready)
                            {
                                try { fpath = WorldMap.FindPathSmoothed(_kobold.transform.position, ppos); }
                                catch (Exception e) { Logger.LogWarning("follow path: " + e.Message); }
                            }
                            if (fpath == null && _cfgPathEnabled.Value)
                            {
                                try { fpath = FindPath(_kobold.transform.position, ppos); }
                                catch (Exception e) { Logger.LogWarning("follow path: " + e.Message); }
                            }
                            if (fpath != null && fpath.Count >= 2)
                            {
                                lock (_stateLock)
                                {
                                    _path = fpath; _pathIdx = 0;
                                    _pathGoal = new Vector3(ppos.x, 0f, ppos.z);
                                    _pathGoalSet = true;
                                    _trailJumps = null; // ordinary A* path — no jump replays
                                    _followLastPath = Time.unscaledTime;
                                }
                            }
                            else
                            {
                                // Nothing A* could route (gaps, parkour, a room the
                                // bake never saw): copy the player's own demonstrated
                                // route — their actual walked samples, jumps replayed
                                // as hop bursts by the advance block.
                                List<Vector3> tpts = null; List<bool> tjumps = null;
                                bool haveTrail = false;
                                try { haveTrail = PlayerTrail.TryPath(_kobold.transform.position, ppos, out tpts, out tjumps); }
                                catch (Exception t) { Logger.LogWarning("follow trail: " + t.Message); }
                                if (haveTrail && tpts != null && tjumps != null)
                                {
                                    tpts.Add(ppos);
                                    tjumps.Add(false); // parallel to _path: the jump-replay guard requires equal counts
                                    lock (_stateLock)
                                    {
                                        _path = tpts; _pathIdx = 0;
                                        _pathGoal = new Vector3(ppos.x, 0f, ppos.z);
                                        _pathGoalSet = true;
                                        _trailJumps = tjumps;
                                        _followLastPath = Time.unscaledTime;
                                    }
                                    Logger.LogInfo("follow: using the player's demonstrated trail (" + (tpts.Count - 1) + " posts, " + (tjumps != null && tjumps.Exists(x => x) ? "with jumps" : "no jumps") + ")");
                                }
                                else
                                {
                                    lock (_stateLock)
                                    {
                                        _path = null; _pathIdx = 0; _pathGoalSet = false; _trailJumps = null;
                                    }
                                    _navTarget = ppos; // straight-line steer fallback
                                }
                            }
                        }
                    }
                    else if (dp < 1.0f)
                    {
                        StopMove();
                        lock (_stateLock) { _path = null; _pathIdx = 0; _pathGoalSet = false; }
                    }
                }
                else
                {
                    StopMove();
                }
            }

            // If a go_to target is active, steer toward it; clear it once we're close.
            if (_navTarget.HasValue)
            {
                Vector3 toT = _navTarget.Value - _kobold.transform.position;
                toT.y = 0;
                float dist = toT.magnitude;
                if (dist < 0.8f)
                {
                    // A* path: advance to the next waypoint (keep going) instead of
                    // declaring arrival while posts remain.
                    bool advance;
                    bool hop = false;
                    lock (_stateLock)
                    {
                        advance = _path != null && _pathIdx < _path.Count - 1;
                        if (advance)
                        {
                            _pathIdx++;
                            // Player-trail replays: a flagged post is somewhere the
                            // player leapt to — jump toward it like they did.
                            if (_trailJumps != null && _trailJumps.Count == _path.Count && _trailJumps[_pathIdx])
                                hop = true;
                        }
                    }
                    if (advance)
                    {
                        lock (_stateLock) { _navTarget = _path[_pathIdx]; }
                        if (hop && Time.unscaledTime - _lastTrailJump > 0.8f)
                        {
                            _lastTrailJump = Time.unscaledTime;
                            SetMove(1f, 0f, true, 0f, 0.5f, true);
                        }
                        toT = _navTarget.Value - _kobold.transform.position;
                        toT.y = 0;
                        dist = toT.magnitude;
                    }
                    else
                    {
                        _navTarget = null;
                        _path = null;
                        _pathIdx = 0;
                        _pathGoalSet = false;
                        StopMove();
                        _blockedInfo = "arrived" + (_navTargetName != null ? " at " + _navTargetName : "");
                    }
                }
                if (_navTarget.HasValue)
                {
                    // ANTI-CIRCLING watch ("flies around the thing it's targeting"):
                    // the target BEARING rotating while the DISTANCE stalls means the
                    // body is orbiting an obstacle that steering will never open.
                    // Break out: hard re-plan around it. Resets whenever the route
                    // changes (a different goal position invalidates the watch).
                    NavCircleWatch(toT, dist);

                    // PURE PURSUIT: near a waypoint, aim at a point blended AHEAD of it
                    // toward the next post — steering through a corner without the
                    // stop-turn-go-stop zigzag that looked like a bee at a window.
                    Vector3 aimPt = _navTarget.Value;
                    try
                    {
                        Vector3 nextPost;
                        lock (_stateLock)
                        {
                            if (_path != null && _pathIdx >= 0 && _pathIdx < _path.Count - 1)
                                nextPost = _path[Mathf.Min(_path.Count - 1, _pathIdx + 1)];
                            else if (_path != null && _path.Count > 0)
                                nextPost = _path[_path.Count - 1];
                            else nextPost = _navTarget.Value;
                        }
                        if (dist < 1.1f)
                        {
                            Vector3 lead = nextPost - aimPt;
                            lead.y = 0f;
                            if (lead.sqrMagnitude > 0.01f)
                                aimPt = aimPt + lead.normalized * Mathf.Min(0.9f, lead.magnitude * 0.6f);
                        }
                    }
                    catch (Exception) { }
                    Vector3 toAim = aimPt - _kobold.transform.position; toAim.y = 0f;
                    if (toAim.sqrMagnitude > 1e-4f)
                    {
                        float yaw = Mathf.Atan2(toAim.x, toAim.z) * Consts.Rad2Deg;
                        _yawDeg = MoveAngleTowards(_yawDeg, yaw, turnRate * dt);
                    }
                    // Arrival braking: slow down as we approach.
                    float speedScale = Mathf.Clamp01(dist / brakeDist);
                    lock (_stateLock) { _moveTargetZ = Mathf.Max(_moveTargetZ, speedScale); }
                    fwd = Mathf.Max(fwd, speedScale);
                }
            }

            // Burst timed out — decelerate to zero.
            if (until > 0f && Time.unscaledTime >= until)
            {
                lock (_stateLock) { _moveTargetZ = 0f; _moveTargetX = 0f; _moveUntilTime = 0f; }
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

            // Turning: smooth interpolation instead of instant snap.
            if (Mathf.Abs(turn) > 0.001f)
                _yawDeg = MoveAngleTowards(_yawDeg, Mathf.Repeat(_yawDeg + turn, 360f), turnRate * dt);

            // WALK VALIDATION: raycast where we're about to go. If blocked ahead,
            // don't walk into it — stop forward drive and auto-steer to a clear
            // heading, then report the blockage so the LLM picks a new direction.
            _blockedInfo = null;
            // Wall-proximity scan: short rays in 4 directions around the body so the
            // model knows "wall on my left" before it hits. Throttled to 4 Hz — most
            // physics frames re-derive the same answer; the last scan persists meanwhile.
            if (Time.unscaledTime - _lastWallProbe > 0.25f)
            {
                _lastWallProbe = Time.unscaledTime;
                _bumpInfo = null;
                ProbeWallProximity();
            }
            float fwdOut = Mathf.Clamp(fwd, -1f, 1f);

            // "Hit a wall": trying to move but the body barely advances (friction
            // holds us against an obstacle the forward ray already caught).
            if (fwdOut != 0f && _controller.body != null)
            {
                Vector3 hv = _controller.body.velocity; hv.y = 0;
                if (hv.magnitude < 0.15f && Time.unscaledTime - _lastBumpTime > 0.6f)
                {
                    bool rapid = Time.unscaledTime - _lastBumpTime < 6f;
                    _lastBumpTime = Time.unscaledTime;
                    _needImageAfterBump = true; // show the model what it hit
                    // STUCK BREAKER: the first bump just reports ("turn"), but a SECOND
                    // rapid bump means steering isn't finding the gap; a THIRD means
                    // the path itself grinds a fixed spot. 2nd → sidestep shove toward
                    // the last fan-steer lean; 3rd → drop the plan so the goal
                    // re-plans fresh (A* then routes AROUND the thing we keep hitting).
                    if (rapid) _bumpStreak++; else _bumpStreak = 1;
                    if (_bumpStreak == 2)
                    {
                        _unstickStrafeSide = _lastFanSteerSide != 0f ? _lastFanSteerSide : 1f;
                        _unstickUntil = Time.unscaledTime + 0.7f;
                        _blockedInfo = "stuck — sidestepping " + (_unstickStrafeSide > 0 ? "right" : "left");
                    }
                    else if (_bumpStreak >= 3)
                    {
                        _bumpStreak = 0;
                        TryRepathPastStuck();
                        _blockedInfo = "stuck — repathing around the blocked spot";
                    }
                    else _blockedInfo = "bumped a wall — turn";
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
                    // If it's a door/gate in the way, open it instead of walking around
                    // (a spatial reaction the model doesn't have to micromanage). Guard
                    // against toggling open/closed every frame with a per-door cooldown.
                    if (MaybeOpenDoorAhead(hit))
                    {
                        _blockedInfo = "opening door ahead";
                    }
                    else if (AutoHopLowObstacle(hit, dir, jump))
                    {
                        // FENCES / SILLS / LOW WALLS: an obstacle whose top is within
                        // ~1m with clear ground beyond is HOPPED, not braked at. This
                        // was the "stuck ON the fence" failure — chest-height rays
                        // caught it, and the body stood there steering forever.
                        _blockedInfo = "hopped the low obstacle";
                    }
                    else
                    {
                        // WALL BRAKING: quadratic deceleration as we approach.
                        // Full stop only when boxed in (no clear heading); otherwise
                        // slow down smoothly so the body stops *before* touching.
                        float wallBrakeDist = 3.0f;
                        if (hit.distance < wallBrakeDist)
                        {
                            float t = hit.distance / wallBrakeDist;
                            fwdOut = Mathf.Min(fwdOut, t * t);  // quadratic: aggressive near wall
                        }

                        // Surface in front — try to find a clear heading by fan-steering.
                        // While steering, keep a trickle of forward drive: full-stop
                        // turn-go-turn-go cycles read as bumbling even when they converge.
                        float steer = FindClearHeading(eye, hit.normal);
                        if (steer != 0f)
                        {
                            _yawDeg = MoveAngleTowards(_yawDeg, Mathf.Repeat(_yawDeg + steer, 360f), turnRate * dt);
                            if (hit.distance > 0.45f) fwdOut = Mathf.Max(fwdOut, 0.25f);
                            _blockedInfo = "blocked; steered " + (steer > 0 ? "right" : "left");
                        }
                        else
                        {
                            // Boxed in — full stop regardless of distance.
                            fwdOut = 0f;
                            _blockedInfo = "blocked, no clear way (dist " + F(hit.distance) + ")";
                        }
                    }
                }
                // LEDGE GUARD: only hard-stop on a drop beyond LedgeDropHardStop
                // (the planner never routes through bigger falls). Drops above
                // LedgeDropSoftWarning are walkable but reported so the model knows
                // — and if it's jumping, it's choosing to go over on purpose.
                if (fwdOut > 0.01f && _blockedInfo == null)
                {
                    Vector3 aheadDown = _kobold.transform.position + dir * 0.8f + Vector3.up * 0.5f;
                    RaycastHit lhit;
                    float drop = Physics.Raycast(aheadDown, Vector3.down, out lhit, 8f, ~0, QueryTriggerInteraction.Ignore)
                        ? lhit.distance - 0.5f : 8f;
                    _ledgeDrop = drop > 0.5f ? drop : (float?)null; // expose to perception
                    if (drop > Consts.LedgeDropHardStop && !jump)   // big fall → stop
                    {
                        fwdOut = 0f;
                        _blockedInfo = "big drop ahead (" + F(drop) + "m); stopped";
                        _needImageAfterBump = true;
                    }
                    else if (drop > Consts.LedgeDropSoftWarning && drop <= Consts.LedgeDropHardStop && !jump)
                        _blockedInfo = "ledge " + F(drop) + "m — you can walk off or jump down";
                    // jump=true or small drop → let it proceed (controller handles fall).
                }
            }

            // Unstick sidestep shove (stacked on whatever the LLM asked for).
            if (Time.unscaledTime < _unstickUntil)
                strafe = Mathf.Clamp(strafe + _unstickStrafeSide, -1f, 1f);

            // inputDir is consumed as a world-space direction by the controller's
            // Accelerate(); walls stop it via normal rigidbody collision.
            Vector3 worldDir = Quaternion.Euler(0, _yawDeg, 0) * new Vector3(Mathf.Clamp(strafe, -1f, 1f), 0f, fwdOut);
            float mag = worldDir.magnitude;
            if (mag > 1f) worldDir /= mag;
            _controller.inputDir = new Vector3(worldDir.x, 0f, worldDir.z);
            // The auto-hop is a same-frame jump decision (low obstacle ahead).
            bool jumpNow = jump;
            bool jumpWasAutoHop = _autoHopPending;
            if (_autoHopPending) { jumpNow = true; _autoHopPending = false; }
            // JUMP PUSH: the game's controller ignores input mid-air, so a plain
            // inputJump hopped IN PLACE. At jump start apply a horizontal impulse
            // along the facing — auto-hops push harder (they aim at a landing).
            if (jumpNow)
            {
                try
                {
                    var rbNow = _controller.body;
                    if (rbNow != null && rbNow.velocity.y < 1.5f)
                    {
                        float pushMag = jumpWasAutoHop ? 3.0f : Mathf.Max(0.9f, Mathf.Abs(fwdOut) * 2.4f);
                        Vector3 push = Quaternion.Euler(0, _yawDeg, 0) * Vector3.forward * pushMag;
                        rbNow.velocity = new Vector3(rbNow.velocity.x + push.x, rbNow.velocity.y, rbNow.velocity.z + push.z);
                    }
                }
                catch (Exception) { }
            }
            _controller.inputJump = jumpNow;
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
                float eyeYaw = walking ? Mathf.Atan2(worldDir.x, worldDir.z) * Consts.Rad2Deg : BodyYaw();
                _charAnimator.SetEyeRot(new Vector2(eyeYaw, -_pitchDeg));
            }
            if (_cam != null)
                _cam.transform.rotation = Quaternion.Euler(_pitchDeg, _yawDeg, 0f);
            if (_camR != null)
                _camR.transform.rotation = Quaternion.Euler(_pitchDeg, _yawDeg, 0f);

            // CAMERA-CLIP detection + auto-crouch fix. If the head/camera is buried
            // in geometry (rays from the head hit something nearer than the camera
            // offset, or we collide with something right at the head), the first-person
            // image is uselessly full of face/wall. Ease crouch up to clear it — the
            // camera rides up as the body lowers.
            // Camera clip probe is the one ray that made the idle cost real — 5 Hz
            // is plenty for a detector that reacts over 0.4s+ anyway.
            if (Time.unscaledTime - _lastClipCheck > 0.2f)
            {
                _lastClipCheck = Time.unscaledTime;
                CheckCameraClip();
            }

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
                    float desiredYaw = Mathf.Atan2(flat.x, flat.z) * Consts.Rad2Deg;
                    float desiredPitch = Mathf.Clamp(Mathf.Atan2(-to.y, Mathf.Max(0.2f, flat.magnitude)) * Consts.Rad2Deg, -89f, 89f);
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
                try
                {
                    float stim = _kobold.stimulation;
                    if (_lastStim >= 0f && stim > _lastStim + 0.08f && Time.unscaledTime - _lastMoan > 3f)
                    {
                        _lastMoan = Time.unscaledTime;
                        RunOnMainThreadAsync(() =>
                        {
                            try
                            {
                                if (!IsAlive(_kobold)) return;
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
                catch (Exception) { _lastStim = -1f; }
            }
            else _lastStim = -1f;
        }

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
