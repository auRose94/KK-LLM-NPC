// act tool implementations (walk, look, go_to, interact, say, memory, crouch, ...).
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

        // ------------------------------------------------------------------
        // tool commands (invoked via the LLM tool-call loop)
        // ------------------------------------------------------------------
        private object ToolMoveTo(JsonObj p)
        {
            if (!IsAlive(_kobold)) return new { ok = false, reason = "no_body" };
            float x = p.F("x"), y = p.F("y"), z = p.F("z");
            Vector3 target = new Vector3(x, y, z);
            Vector3 toT = target - _kobold.transform.position;
            float dist = toT.magnitude;
            // Set nav target — FixedUpdateSafe handles smooth turning and arrival braking.
            _navTarget = target;
            _navTargetName = null;
            float dur = Mathf.Clamp(dist / 2f, 0.3f, 8f);
            SetMove(1f, 0f, false, 0f, dur, p.B("run", false));
            return new { ok = true, dist = F(dist), walked_for = F(dur) };
        }

        private object ToolWalk(JsonObj p)
        {
            float speed = p.F("speed", 1f);
            float strafe = p.F("strafe", 0f);         // NEW: right=+/left=-, -1..1
            bool jump = p.B("jump", false);
            float turn = p.F("turn_deg", 0f);
            // Default a 2s burst so a forgotten duration can't make it walk forever.
            float dur = Mathf.Clamp(p.F("duration", 2f), 0.1f, 8f);
            bool run = p.B("run", false); // default: walk
            if (_photonView != null && !_photonView.IsMine && PhotonNetwork.InRoom)
            {
                try
                {
                    string safeNick = _photonView.Owner?.NickName ?? "?";
                    safeNick = new string(safeNick.Where(c => c >= 32 && c < 127).ToArray());
                    Logger.LogWarning($"walk: not Photon owner (owner={safeNick}) — movement won't apply");
                }
                catch (Exception) { Logger.LogWarning("walk: not Photon owner — movement won't apply"); }
            }
            SetMove(speed, strafe, jump, 0f, dur, run);
            if (Mathf.Abs(turn) > 0.001f)
                lock (_stateLock) { _yawOffsetDeg += turn; }
            return new { ok = true, speed, jump, turn_deg = turn, duration = dur, run };
        }

        // Steer+walk toward one of the raycast fan directions the model can see.
        // The rays share the vision camera's yaw basis, so a ray index (or signed
        // degrees left/right of center) maps directly onto a camera-relative heading.
        // Camera-relative steering: pick one of the raycast fan rays by index (left→right
        // -1=center) or signed degrees left/right of camera center, and walk there.
        private object ToolWalkRay(JsonObj p)
        {
            float speed = p.F("speed", 1f);
            float dur = Mathf.Clamp(p.F("duration", 2f), 0.1f, 8f);
            bool jump = p.B("jump", false);
            bool run = p.B("run", false); // default: walk
            float yawOut;
            lock (_stateLock)
            {
                float deltaDeg = 0f;
                float rd = p.F("ray_deg", float.NaN);
                if (!float.IsNaN(rd)) deltaDeg = Mathf.Clamp(rd, -90f, 90f);
                else
                {
                    float idx = p.F("ray", -1f);
                    int n = Mathf.Max(2, _cfgRayCount.Value);
                    float fov = _cam != null ? _cam.fieldOfView : 90f;
                    if (idx < 0f) idx = (n - 1) * 0.5f; // center
                    float t = Mathf.Clamp(idx, 0f, n - 1) / (n - 1);
                    deltaDeg = Mathf.Lerp(-fov * 0.5f, fov * 0.5f, t);
                }
                // Rays inherit camera pitch+yaw; we steer the whole body to that yaw.
                _yawDeg = Mathf.Repeat(_yawDeg + deltaDeg, 360f); // immediate for scan — no smooth needed
                yawOut = _yawDeg;
            }
            SetMove(speed, p.F("strafe", 0f), jump, 0f, dur, run);
            return new { ok = true, yaw = F(yawOut), dur = F(dur), run };
        }

        private object ToolLookAround(JsonObj p)
        {
            // Result goes into the NEXT perception; here we kick off a sweep and
            // immediately return what the ray fan sees in each sector.
            float sweep = Mathf.Clamp(p.F("sweep", 120f), 30f, 360f);
            lock (_stateLock) { _yawOffsetDeg += sweep; } // physically start turning
            _lastBigTurnTime = Time.unscaledTime;         // trigger an image next tick
            var seen = (Dictionary<string, object>)RunOnMainThread(() =>
            {
                // Sector scan via the 8-way fan: report what's in each direction.
                var res = new Dictionary<string, object>();
                if (!IsAlive(_kobold) || !IsAlive(_head)) return res;
                string[] names = { "front", "front-right", "right", "back-right", "back", "back-left", "left", "front-left" };
                float[] offs = { 0f, 45f, 90f, 135f, 180f, -135f, -90f, -45f };
                for (int i = 0; i < 8; i++)
                {
                    Vector3 dir = Quaternion.Euler(0, _yawDeg + offs[i], 0) * Vector3.forward;
                    string what = "nothing";
                    RaycastHit hit;
                    if (Physics.Raycast(_head.position, dir, out hit, _cfgRayRange.Value, ~0, QueryTriggerInteraction.Ignore) && !IsOwnCollider(hit.collider))
                    {
                        try
                        {
                            var kb = hit.collider.GetComponentInParent<Kobold>();
                            var us = hit.collider.GetComponent<GenericUsable>() ?? hit.collider.GetComponentInParent<GenericUsable>();
                            if (kb != null) what = (IsPlayerKobold(kb) ? "player:" : "kobold:") + kb.name;
                            else if (us != null) what = "usable:" + us.name;
                            else what = "wall:" + hit.collider.gameObject.name;
                        }
                        catch (Exception) { what = "object"; }
                        what += "@" + F(hit.distance) + "m";
                    }
                    res[names[i]] = what;
                }
                return res;
            });
            return new { ok = true, turning_to_sweep = sweep, scan = seen };
        }

        // Dynamically inspect a direction the model cares about and get back what's
        // there, with stable ids it can feed straight into go_to/interact. Unlike the
        // static 'nearby' (14m, capped), this probes whichever heading you ask for at
        // full ray range, so the model can react to *what it's looking at* in that turn.
        private object ToolSurvey(JsonObj p)
        {
            if (!IsAlive(_kobold) || !IsAlive(_head)) return new { ok = false, reason = "no_body" };
            float heading = p.F("heading_deg", float.NaN);   // absolute yaw, else current facing
            float pitch = p.F("pitch_deg", float.NaN);
            float range = Mathf.Clamp(p.F("range", _cfgRayRange.Value), 2f, 40f);
            var seen = (Dictionary<string, object>)RunOnMainThread(() =>
            {
                var res = new Dictionary<string, object>();
                float yaw = float.IsNaN(heading) ? _yawDeg : heading;
                float pit = float.IsNaN(pitch) ? _pitchDeg : pitch;
                var dir = Quaternion.Euler(pit, yaw, 0) * Vector3.forward;
                // A pair of parallel rays a little apart so wide objects aren't missed
                // and we can estimate distance + width.
                for (float off = -0.35f; off <= 0.35f; off += 0.35f)
                {
                    var o2 = Quaternion.Euler(0, yaw, 0) * new Vector3(off, 0, 0);
                    RaycastHit hit;
                    var org = _head.position + o2;
                    if (!Physics.Raycast(org, dir, out hit, range, ~0, QueryTriggerInteraction.Ignore) || IsOwnCollider(hit.collider)) continue;
                    string what = "wall:" + hit.collider.gameObject.name;
                    int tid = -1;
                    string cat = "geometry";
                    try
                    {
                        var kb = hit.collider.GetComponentInParent<Kobold>();
                        var us = hit.collider.GetComponent<GenericUsable>() ?? hit.collider.GetComponentInParent<GenericUsable>();
                        if (kb != null) { what = (IsPlayerKobold(kb) ? "player:" : "kobold:") + kb.name; cat = "kobold"; tid = TargetIdFor(kb.transform, kb.name); }
                        else if (us != null) { string cn = CleanName(us.name); what = "usable:" + cn; cat = ClassifyUsable(cn); tid = TargetIdFor(us.transform, cn); }
                    }
                    catch (Exception) { }
                    string key = res.ContainsKey("hit") ? "hit2" : "hit";
                    res[key] = new { n = what, d = F(hit.distance), cat, id = tid };
                }
                if (res.Count == 0) res["clear"] = "nothing within " + F(range) + "m";
                return res;
            });
            return new { ok = true, asked_yaw = float.IsNaN(heading) ? F(_yawDeg) : F(heading), range = F(range), result = seen };
        }

        private object ToolCrouch(JsonObj p)
        {
            float amount = Mathf.Clamp01(p.F("crouch", 1f));
            lock (_stateLock) { _crouch = amount; _manualCrouchSet = Time.unscaledTime; }
            return new { ok = true, crouch = amount, note = amount >= 0.05f ? "crouching" : "standing" };
        }

        // Path the body toward a world position or a named/identified place/usable. The
        // game's NavMesh API isn't accessible from this Unity build, so this drives our
        // own movement: face the target + walk with obstacle auto-steer (from FixedUpdate).
        // Accepts id: (from 'nearby') or name: or raw x,z. 'at' stops the approach that
        // many meters short (defaults: named/usable targets stop ~1m short so the model
        // reaches the object instead of bumping it; raw coords go all the way).
        private object ToolGoTo(JsonObj p)
        {
            if (!IsAlive(_kobold)) return new { ok = false, reason = "no_body" };

            string name = p.S("name", "");
            bool hasId = p.Has("id");
            Vector3 target;
            bool namedTarget = true;
            if (hasId)
            {
                Transform tf = null;
                try
                {
                    double idv = p.DB("id", -1);
                    int id = (int)idv;
                    if (id > 0) tf = (Transform)RunOnMainThread(() => FindTargetById(id), 8000);
                }
                catch (Exception) { }
                if (tf == null) return new { ok = false, reason = "unknown_target", id = p.S("id"), note = "that id wasn't nearby; re-read 'nearby' for current ids" };
                target = tf.position;
                name = CleanName(tf.name);
                _navTargetName = name;
            }
            else if (!string.IsNullOrEmpty(name))
            {
                var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Mechanics", "FarmRegion" };
                if (ignore.Contains(name.ToLowerInvariant())) return new { ok = false, reason = "ignored_place", tried = name };
                var hit = FindPlaceByName(name);
                if (!hit.HasValue) return new { ok = false, reason = "unknown_place", tried = name };

                target = hit.Value;
                _navTargetName = name;
            }
            else { target = new Vector3(p.F("x"), 0, p.F("z")); _navTargetName = null; namedTarget = false; }

            // Movement can't apply at all without the body's character controller.
            // Say so explicitly instead of "ok" + doing nothing (the model then
            // wrongly thinks it moved).
            if (_controller == null || _controller.body == null)
                return new { ok = false, reason = "no_movement_controller", note = "this body can't move right now" };

            // Auto-recover from an animation-station lock — but only if the new
            // destination is actually DIFFERENT from what this station provides.
            // If the model re-issues go_to to the same kind of place, stay put.
            if (IsInAnimationStation())
            {
                bool shouldExit = true;
                if (_stayInStation)
                {
                    shouldExit = false;
                    Logger.LogInfo("go_to: player asked to stay — ignoring navigation while in station");
                }
                else if (_stationPurpose != null && namedTarget)
                {
                    string destClass = ClassifyUsable(name).ToLowerInvariant();
                    // Same purpose (e.g. play→play, bed→bed) — don't exit.
                    if (string.Equals(_stationPurpose, destClass, StringComparison.OrdinalIgnoreCase))
                        shouldExit = false;
                    // Also stay if the destination name contains the current station's name.
                    else if (!string.IsNullOrEmpty(_navTargetName) && name.ToLowerInvariant().Contains(_navTargetName.ToLowerInvariant()))
                        shouldExit = false;
                }
                if (shouldExit)
                {
                    try { _photonView?.RPC("StopAnimationRPC", RpcTarget.All); }
                    catch (Exception e) { Logger.LogWarning("go_to exit station: " + e.Message); }
                    _stationPurpose = null;
                    _stationEntryThought = null;
                }
                else
                {
                    _blockedInfo = "staying in " + (_stationPurpose ?? "station") + " (same purpose)";
                    return new { ok = true, to = _navTargetName ?? "position", dist = F(0f), note = "already in a " + (_stationPurpose ?? "station") + " — staying" };
                }
            }

            // Stop short so we arrive AT the target rather than plowing through it.
            float at = p.F("at", namedTarget ? 1f : 0f);
            at = Mathf.Clamp(at, 0f, 8f);
            Vector3 to = target - _kobold.transform.position; to.y = 0;
            float fullDist = to.magnitude;

            // No chasing phantom targets across a huge map: beyond our reachable
            // A* window a straight-line/wall-grind "walk" is worse than a refusal.
            float maxReach = (_cfgPathSpan != null ? _cfgPathSpan.Value : 20f) * 2f + 30f;
            if (fullDist > maxReach + at)
                return new { ok = false, reason = "too_far", name = _navTargetName ?? "position", id = hasId ? p.S("id") : (object)null, dist = F(fullDist), note = "in range of ~" + F(maxReach) + "m only — pick something from 'nearby'/'survey' or ask the player to take you" };
            float stopAt = Mathf.Max(0f, fullDist - at);
            float yaw = Mathf.Atan2(to.x, to.z) * 57.29578f;
            lock (_stateLock) { _yawDeg = yaw; }
            if (fullDist > at + 0.1f)
            {
                var arrive = _kobold.transform.position + to.normalized * stopAt;
                List<Vector3> path = null;
                if (_cfgPathEnabled.Value && stopAt > 1.5f)
                {
                    // Reuse an active path to ~the same place instead of replanning
                    // every think tick (planning is physics-heavy, main-thread).
                    bool reuse = false;
                    lock (_stateLock)
                    {
                        if (_path != null && _pathIdx < _path.Count && _pathGoalSet
                            && Time.unscaledTime - _pathLastPlanTime < 6f)
                        {
                            var g = new Vector3(arrive.x, 0, arrive.z);
                            if (Vector3.Distance(g, _pathGoal) <= 1.5f) reuse = true;
                        }
                    }
                    if (reuse)
                    {
                        lock (_stateLock)
                        {
                            if (_pathIdx >= _path.Count) _pathIdx = Math.Max(0, _path.Count - 1);
                            _navTarget = _path[_pathIdx];
                            _pathLastPlanTime = Time.unscaledTime;
                        }
                        SetMove(1f, 0f, false, 0f, Mathf.Clamp(stopAt / 2f, 0.3f, 12f), p.B("run", false));
                        return new { ok = true, to = _navTargetName ?? "position", id = hasId ? p.S("id") : (object)null, dist = F(fullDist), at = F(at), note = "continuing path; re-issue go_to to replan" };
                    }
                    try
                    {
                        path = (List<Vector3>)RunOnMainThread(() => FindPath(_kobold.transform.position, arrive), 8000);
                    }
                    catch (Exception e) { Logger.LogWarning("pathfind: " + e.Message); }
                }
                if (path != null && path.Count >= 2)
                {
                    lock (_stateLock)
                    {
                        _path = path;
                        _pathIdx = 0;
                        _navTarget = path[0];
                        _pathGoal = new Vector3(arrive.x, 0, arrive.z);
                        _pathGoalSet = true;
                        _pathLastPlanTime = Time.unscaledTime;
                    }
                    float pathLen = 0f;
                    for (int i = 1; i < path.Count; i++) pathLen += Vector3.Distance(path[i - 1], path[i]);
                    SetMove(1f, 0f, false, 0f, Mathf.Clamp(pathLen / 1.5f, 1f, 30f), p.B("run", false));
                    string dnote = _pathDoorBlocked ? " (a CLOSED door is on the way — the body will open it when it gets there, or use interact on it)" : "";
                    return new { ok = true, to = _navTargetName ?? "position", id = hasId ? p.S("id") : (object)null, dist = F(fullDist), at = F(at), note = "a* path (" + (path.Count - 2) + " posts)" + dnote + "; re-issue go_to to replan" };
                }
                // Fallback: direct steering toward the arrive point (previous behavior).
                lock (_stateLock) { _path = null; _pathIdx = 0; _pathGoalSet = false; }
                _navTarget = new Vector3(arrive.x, 0, arrive.z);
                SetMove(1f, 0f, false, 0f, Mathf.Clamp(stopAt / 2f, 0.3f, 12f), p.B("run", false));
            }
            else { _navTarget = null; _path = null; _pathIdx = 0; _pathGoalSet = false; StopMove(); _blockedInfo = "already at " + name; }
            return new { ok = true, to = _navTargetName ?? "position", id = hasId ? p.S("id") : (object)null, dist = F(fullDist), at = F(at), note = "walking with obstacle steering; re-issue go_to to update heading" };
        }

        // Resolve a human-ish name ("bed", "toilet", "player", "door") to a world
        // position from what we can currently find around the map body.
        private Vector3? FindPlaceByName(string name)
        {
            if (!IsAlive(_kobold)) return null;
            string needle = name.ToLowerInvariant();
            // The player.
            if (needle.Contains("player") || needle.Contains("me") || needle.Contains("you"))
            {
                try
                {
                    if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null)
                        return pp.kobold.transform.position;
                }
                catch (Exception) { }
            }
            // Best matching GenericUsable (bed/toilet/tub/seat/door/swap...) in range.
            GenericUsable best = null; float bestD = float.MaxValue;
            try
            {
                var colliders = Physics.OverlapSphere(_kobold.transform.position, 60f, ~0, QueryTriggerInteraction.Collide);
                if (colliders != null)
                {
                    foreach (var c in colliders)
                    {
                        if (c == null) continue;
                        if (IsOwnCollider(c)) continue;
                        GenericUsable u = null;
                        try { u = c.GetComponent<GenericUsable>() ?? c.GetComponentInParent<GenericUsable>(); } catch (Exception) { continue; }
                        if (u == null) continue;
                        string n = CleanName(u.name).ToLowerInvariant();
                        string cls = ClassifyUsable(n).ToLowerInvariant();
                        if (n.Contains(needle) || cls.Contains(needle) || needle.Contains(cls))
                        {
                            float d = Vector3.Distance(u.transform.position, _kobold.transform.position);
                            if (d < bestD) { bestD = d; best = u; }
                        }
                    }
                }
            }
            catch (Exception) { }
            return best != null ? best.transform.position : (Vector3?)null;
        }

        // Store a fact in long-term memory ("bed upstairs", "player is friendly").
        // Deliberately store a fact (long-term memory, 24-item ring) the model reads
        // every tick.
        private object ToolRemember(JsonObj p)
        {
            string fact = p.S("mem", "");
            if (string.IsNullOrWhiteSpace(fact)) return new { ok = false, reason = "empty_mem" };
            fact = Sanitize(fact);
            RememberFact(fact);
            return new { ok = true, remembered = fact.Trim(), facts = _facts.Count };
        }

        private object ToolStop()
        {
            StopMove();
            return new { ok = true };
        }

        // Get out of any animation station (bed/sex/mount) the kobold is locked in.
        // Identical to a player pressing Jump/Cancel: raises StopAnimationRPC.
        // Get off a station: the same RPC the real player sends on Jump/Cancel while
        // in one — PhotonView.RPC("StopAnimationRPC", RpcTarget.All).
        private object ToolExitStation()
        {
            if (!IsAlive(_kobold)) return new { ok = false, reason = "no_body" };
            bool inStation = IsInAnimationStation();
            bool sent = (bool)RunOnMainThread(() =>
            {
                try { _photonView?.RPC("StopAnimationRPC", RpcTarget.All); return true; }
                catch (Exception e) { Logger.LogWarning("exit station: " + e.Message); return false; }
            });
            StopMove(); // also release local input (controller ignores it while animating)
            _stimSource = null; // no longer mounted: the machine's not the stim source
            _stationPurpose = null; // no longer in a station
            _stationEntryThought = null;
            _stationEntryTime = 0f;
            _stayInStation = false;
            return new { ok = sent, was_in_station = inStation, note = "exits any animation station — same as pressing jump" };
        }

        private object ToolLook(JsonObj p)
        {
            float yaw = p.F("yaw_deg", float.NaN);
            float pitch = p.F("pitch_deg", float.NaN);
            float dYaw = p.F("dyaw_deg", 0f);
            float dPitch = p.F("dpitch_deg", 0f);
            lock (_stateLock)
            {
                if (!float.IsNaN(yaw)) _yawDeg = MoveAngleTowards(_yawDeg, Mathf.Repeat(yaw, 360f), 360f);
                if (!float.IsNaN(pitch)) _pitchDeg = Mathf.Clamp(pitch, -89f, 89f);
                if (dYaw != 0f) _yawDeg = MoveAngleTowards(_yawDeg, Mathf.Repeat(_yawDeg + dYaw, 360f), 360f);
                _pitchDeg = Mathf.Clamp(_pitchDeg + dPitch, -89f, 89f);
            }
            // Turn the whole body when looking around — the animator handles body yaw
            // via facingRot since inputWalking is false; we just feed it the target.
            // No rigidbody writes from us.
            if (Mathf.Abs(dYaw) >= 30f || Mathf.Abs((!float.IsNaN(yaw) ? yaw : _yawDeg) - _yawDeg) >= 30f)
                _lastBigTurnTime = Time.unscaledTime; // trigger a fresh image next tick
            return new { ok = true, yaw = F(_yawDeg), pitch = F(_pitchDeg) };
        }

        private object ToolJump()
        {
            // If locked in an animation station, jumping is how players get out —
            // do that first so "jump" behaves the way the model/player expects.
            if (IsInAnimationStation()) return ToolExitStation();

            lock (_stateLock) { _moveJump = true; }
            RunOnMainThreadAsync(() => { if (_controller != null) _controller.inputJump = true; });
            return new { ok = true };
        }

        // Interact with a usable machine/object. The game's User component only
        // populates closestUsable for the *local player's* tagged body, so calling
        // User.Use() on our possessed body no-ops. We instead find the nearest
        // GenericUsable ourselves, turn to face it, then drive LocalUse directly
        // (the same thing the player's Use() calls once one is in range).
        // Look for a GenericUsable in front (eye-ray first, then a 2.6m bubble), TURN to
        // face it, then LocalUse() it — that's what drives stations/machines.
        private object ToolInteract(JsonObj p)
        {
            if (!IsAlive(_kobold)) return new { ok = false, reason = "no_body" };
            return RunOnMainThread(() =>
            {
                GenericUsable target;
                float dist;
                // Optional id: act on a *specific* nearby object (from the 'nearby'
                // list) rather than whatever happens to be in front. This is the
                // addressable-interact win — the model picks the exact bed/station.
                if (p != null && p.Has("id"))
                {
                    double idv = -1; try { idv = p.DB("id", -1); } catch (Exception) { }
                    Transform tf = FindTargetById((int)idv);
                    if (tf == null) return new { ok = false, reason = "unknown_target", id = p.S("id"), note = "that id wasn't nearby; re-read 'nearby' for current ids" };
                    target = tf.GetComponent<GenericUsable>() ?? tf.GetComponentInParent<GenericUsable>();
                    dist = Vector3.Distance(tf.position, _kobold.transform.position);
                    if (target == null) return new { ok = false, reason = "target_not_usable", name = CleanName(tf.name) };
                }
                else
                {
                    target = FindBestUsable(out dist);
                    if (target == null) return new { ok = false, reason = "nothing_usable_nearby" };
                }

                // Face it first (yaw + slight pitch down toward the object) and turn
                // the *camera* — the model's vision follows this, so it will see the
                // object it just interacted with on the next tick.
                Vector3 to = target.transform.position - _kobold.transform.position;
                Vector3 flat = to; flat.y = 0;
                float yaw = Mathf.Atan2(flat.x, flat.z) * 57.29578f;
                float pitch = Mathf.Clamp(Mathf.Atan2(-to.y, Mathf.Max(0.2f, flat.magnitude)) * 57.29578f, -60f, 60f);
                lock (_stateLock) { _yawDeg = Mathf.Repeat(yaw, 360f); _pitchDeg = pitch; }
                _lastBigTurnTime = Time.unscaledTime; // make next tick attach a fresh image

                if (!target.CanUse(_kobold)) return new { ok = false, reason = "cannot_use", name = CleanName(target.name), hint = "maybe busy/occupied or wrong state", dist = F(dist) };
                try { target.LocalUse(_kobold); }
                catch (Exception e) { Logger.LogWarning("use: " + e.Message); return new { ok = false, reason = "use_failed", name = CleanName(target.name) }; }
                // The machine/station is what we just climbed onto — this is the
                // source of any pleasure that follows, not "the player".
                _stimSource = CleanName(target.name);
                _stimSourceT = Time.unscaledTime;
                // Track station purpose for smart exit decisions.
                _stationPurpose = ClassifyUsable(CleanName(target.name));
                _stationEntryThought = _lastThought;
                _stationEntryTime = Time.unscaledTime;
                _stayInStation = false; // entering a new station clears any previous stay request
                return (object)new { ok = true, used = CleanName(target.name), type = _stationPurpose, dist = F(dist) };
            });
        }

        // Nearest GenericUsable the kobold can actually use. Prefers what's roughly
        // in front of it, then falls back to a close 360° bubble (machines need to be
        // touched, not just seen). Range mirrors the User capsule's effective radius.
        private GenericUsable FindBestUsable(out float dist)
        {
            dist = float.MaxValue;
            if (!IsAlive(_kobold)) return null;
            Vector3 pos = _kobold.transform.position;
            Vector3 fwd = Quaternion.Euler(0, _yawDeg, 0) * Vector3.forward;

            // 1) What the forward rays are pointing at.
            GenericUsable best = null;
            if (_head != null)
            {
                Vector3 dir = Quaternion.Euler(_pitchDeg, _yawDeg, 0) * Vector3.forward;
                if (Physics.Raycast(_head.position, dir, out RaycastHit hit, InteractRange, ~0, QueryTriggerInteraction.Collide)
                    && !IsOwnCollider(hit.collider))
                {
                    try
                    {
                        var u = hit.collider.GetComponent<GenericUsable>() ?? hit.collider.GetComponentInParent<GenericUsable>();
                        if (u != null && u.CanUse(_kobold)) { best = u; dist = hit.distance; }
                    }
                    catch (Exception) { }
                }
            }

            // 2) Otherwise nearest within the touch bubble, preferring forward-facing.
            float bestScore = best != null ? dist : float.MaxValue;
            try
            {
                var colliders = Physics.OverlapSphere(pos, InteractRange, ~0, QueryTriggerInteraction.Collide);
                if (colliders != null)
                {
                    foreach (var c in colliders)
                    {
                        if (c == null || IsOwnCollider(c)) continue;
                        GenericUsable u2 = null;
                        try { u2 = c.GetComponent<GenericUsable>() ?? c.GetComponentInParent<GenericUsable>(); } catch (Exception) { continue; }
                        if (u2 == null) continue;
                        try { if (!u2.CanUse(_kobold)) continue; } catch (Exception) { continue; }
                        Vector3 d = u2.transform.position - pos;
                        float m = d.magnitude;
                        float facing = Vector3.Dot(d.normalized, fwd);
                        float score = m * (facing > -0.2f ? 1f : 2.5f);
                        if (score < bestScore) { bestScore = score; dist = m; best = u2; }
                    }
                }
            }
            catch (Exception) { }
            return best;
        }

        private object ToolGrab(JsonObj p)
        {
            if (_grabber == null) return new { ok = false, reason = "no_grabber" };
            bool multi = p.B("multi", false);
            bool done = (bool)RunOnMainThread(() =>
            {
                try { _grabber.TryGrab(multi); return true; } catch (Exception e) { Logger.LogWarning("grab: " + e.Message); return false; }
            });
            return new { ok = done };
        }

        private object ToolDrop()
        {
            if (_grabber == null) return new { ok = false, reason = "no_grabber" };
            bool done = (bool)RunOnMainThread(() =>
            {
                try { _grabber.TryDrop(); return true; } catch (Exception e) { Logger.LogWarning("drop: " + e.Message); return false; }
            });
            return new { ok = done };
        }

        private object ToolStatus()
        {
            return BuildPerception(false);
        }
    }
}
