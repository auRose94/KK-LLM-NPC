// KKLLMNPC — a BepInEx plugin for KoboldKare that lets an LLM embody and play
// as an unoccupied Kobold NPC.
//
// The plugin runs inside the game process. It:
//   1. Hijacks the nearest wild (AIPlayer) Kobold, takes Photon ownership and
//      suppresses its built-in wander/look AI.
//   2. Gives the LLM two senses:
//        - a frustum fan of raycasts around the kobold's facing  (structure)
//        - a first-person camera render read back as a base64 PNG (vision)
//   3. Reports kobold stats/genes/energy + world position.
//   4. Exposes tool commands (move/turn/jump/look/interact/grab/drop/eat...)
//      by driving the same KoboldCharacterController/User/Grabber the local
//      player uses, so movement & interaction behave exactly like a player.
//   5. Talks to an OpenAI-compatible chat-completions endpoint with tool
//      calling: it pushes perceptions and executes returned tool_calls in a
//      loop on its own thread, so the LLM continuously plays the NPC.
//
// Build against BepInEx + UnityEngine + Photon + Assembly-CSharp (see build.sh).
// Drop the DLL into <game>/BepInEx/plugins/ and configure the endpoint in
// BepInEx/config/com.kk.llmnpc.cfg after first launch.

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

        // ------------------------------------------------------------------
        // tool commands (invoked via the LLM tool-call loop)
        // ------------------------------------------------------------------
        private object ToolMoveTo(JsonObj p)
        {
            float x = p.F("x"), y = p.F("y"), z = p.F("z");
            if (_kobold == null) return new { ok = false, reason = "no_body" };
            Vector3 target = new Vector3(x, y, z);
            Vector3 toT = target - _kobold.transform.position;
            float dist = toT.magnitude;
            toT.y = 0;
            float yaw = Mathf.Atan2(toT.x, toT.z) * 57.29578f;
            lock (_stateLock) { _yawDeg = yaw; }
            // Actually start walking there (bounded burst) so move_to isn't a no-op.
            float dur = Mathf.Clamp(dist / 2f, 0.3f, 8f);
            SetMove(1f, false, 0f, dur, p.B("run", false));
            return new { ok = true, dist = F(dist), walked_for = F(dur) };
        }


        private object ToolWalk(JsonObj p)
        {
            float speed = p.F("speed", 1f);
            bool jump = p.B("jump", false);
            float turn = p.F("turn_deg", 0f);
            // Default a 2s burst so a forgotten duration can't make it walk forever.
            float dur = Mathf.Clamp(p.F("duration", 2f), 0.1f, 8f);
            bool run = p.B("run", false); // default: walk
            if (_photonView != null && !_photonView.IsMine && PhotonNetwork.InRoom)
                Logger.LogWarning($"walk: not Photon owner (owner={_photonView.Owner?.NickName ?? "?"}) — movement won't apply");
            SetMove(speed, jump, 0f, dur, run);
            if (Mathf.Abs(turn) > 0.001f)
                lock (_stateLock) { _yawOffsetDeg += turn; }
            return new { ok = true, speed, jump, turn_deg = turn, duration = dur, run };
        }


        // Steer+walk toward one of the raycast fan directions the model can see.
        // The rays share the vision camera's yaw basis, so a ray index (or signed
        // degrees left/right of center) maps directly onto a camera-relative heading.
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
                _yawDeg = Mathf.Repeat(_yawDeg + deltaDeg, 360f);
                yawOut = _yawDeg;
            }
            SetMove(speed, jump, 0f, dur, run);
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
                            var us = hit.collider.GetComponentInParent<GenericUsable>();
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


        private object ToolCrouch(JsonObj p)
        {
            float amount = Mathf.Clamp01(p.F("crouch", 1f));
            lock (_stateLock) { _crouch = amount; _manualCrouchSet = Time.unscaledTime; }
            return new { ok = true, crouch = amount, note = amount >= 0.05f ? "crouching" : "standing" };
        }


        // Path the body toward a world position or a named place/usable. The game's
        // NavMesh API isn't accessible from this Unity build, so this drives our own
        // movement: face the target + walk with obstacle auto-steer (from FixedUpdate).
        private object ToolGoTo(JsonObj p)
        {
            if (!IsAlive(_kobold)) return new { ok = false, reason = "no_body" };

            string name = p.S("name", "");
            Vector3 target;
            if (!string.IsNullOrEmpty(name))
            {
                var hit = FindPlaceByName(name);
                if (!hit.HasValue) return new { ok = false, reason = "unknown_place", tried = name };
                target = hit.Value;
                _navTargetName = name;
            }
            else { target = new Vector3(p.F("x"), 0, p.F("z")); _navTargetName = null; }

            bool run = p.B("run", false);
            Vector3 to = target - _kobold.transform.position; to.y = 0;
            float dist = to.magnitude;
            float yaw = Mathf.Atan2(to.x, to.z) * 57.29578f;
            lock (_stateLock) { _yawDeg = yaw; }
            _navTarget = new Vector3(target.x, 0, target.z);
            SetMove(1f, false, 0f, Mathf.Clamp(dist / 2f, 0.3f, 12f), run);
            return new { ok = true, to = _navTargetName ?? "position", dist = F(dist), note = "walking with obstacle steering; re-issue go_to to update heading" };
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
                if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null)
                    return pp.kobold.transform.position;
            }
            // Best matching GenericUsable (bed/toilet/tub/seat/door/swap...) in range.
            GenericUsable best = null; float bestD = float.MaxValue;
            foreach (var c in Physics.OverlapSphere(_kobold.transform.position, 60f, ~0, QueryTriggerInteraction.Collide))
            {
                if (c == null || IsOwnCollider(c)) continue;
                var u = c.GetComponentInParent<GenericUsable>();
                if (u == null) continue;
                string n = CleanName(u.name).ToLowerInvariant();
                string cls = ClassifyUsable(n).ToLowerInvariant();
                if (n.Contains(needle) || cls.Contains(needle) || needle.Contains(cls))
                {
                    float d = Vector3.Distance(u.transform.position, _kobold.transform.position);
                    if (d < bestD) { bestD = d; best = u; }
                }
            }
            return best != null ? best.transform.position : (Vector3?)null;
        }


        // Store a fact in long-term memory ("bed upstairs", "player is friendly").
        private object ToolRemember(JsonObj p)
        {
            string fact = p.S("mem", "");
            if (string.IsNullOrWhiteSpace(fact)) return new { ok = false, reason = "empty_mem" };
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
                if (!float.IsNaN(yaw)) _yawDeg = Mathf.Repeat(yaw, 360f);
                if (!float.IsNaN(pitch)) _pitchDeg = Mathf.Clamp(pitch, -89f, 89f);
                _yawDeg = Mathf.Repeat(_yawDeg + dYaw, 360f);
                _pitchDeg = Mathf.Clamp(_pitchDeg + dPitch, -89f, 89f);
            }
            // Turn the whole body when looking around — so the body follows its gaze
            // like a real player would, instead of the head drifting off sideways.
            RunOnMainThreadAsync(() =>
            {
                try
                {
                    if (_controller != null && _controller.body != null)
                        _controller.body.MoveRotation(Quaternion.Euler(0, _yawDeg, 0));
                }
                catch (Exception) { }
            });
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
        private object ToolInteract()
        {
            if (!IsAlive(_kobold)) return new { ok = false, reason = "no_body" };
            return RunOnMainThread(() =>
            {
                var target = FindBestUsable(out float dist);
                if (target == null) return new { ok = false, reason = "nothing_usable_nearby" };

                // Face it first (yaw + slight pitch down toward the object) and turn
                // the *camera* — the model's vision follows this, so it will see the
                // object it just interacted with on the next tick.
                Vector3 to = target.transform.position - _kobold.transform.position;
                Vector3 flat = to; flat.y = 0;
                float yaw = Mathf.Atan2(flat.x, flat.z) * 57.29578f;
                float pitch = Mathf.Clamp(Mathf.Atan2(-to.y, Mathf.Max(0.2f, flat.magnitude)) * 57.29578f, -60f, 60f);
                lock (_stateLock) { _yawDeg = Mathf.Repeat(yaw, 360f); _pitchDeg = pitch; }
                try { _controller.body?.MoveRotation(Quaternion.Euler(0, _yawDeg, 0)); } catch (Exception) { }
                _lastBigTurnTime = Time.unscaledTime; // make next tick attach a fresh image

                if (!target.CanUse(_kobold)) return new { ok = false, reason = "cannot_use", name = CleanName(target.name), hint = "maybe busy/occupied or wrong state", dist = F(dist) };
                try { target.LocalUse(_kobold); }
                catch (Exception e) { Logger.LogWarning("use: " + e.Message); return new { ok = false, reason = "use_failed", name = CleanName(target.name) }; }
                return (object)new { ok = true, used = CleanName(target.name), type = ClassifyUsable(CleanName(target.name)), dist = F(dist) };
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
                    var u = hit.collider.GetComponentInParent<GenericUsable>();
                    if (u != null && u.CanUse(_kobold)) { best = u; dist = hit.distance; }
                }
            }

            // 2) Otherwise nearest within the touch bubble, preferring forward-facing.
            float bestScore = best != null ? dist : float.MaxValue;
            foreach (var c in Physics.OverlapSphere(pos, InteractRange, ~0, QueryTriggerInteraction.Collide))
            {
                if (c == null || IsOwnCollider(c)) continue;
                var u2 = c.GetComponentInParent<GenericUsable>();
                if (u2 == null) continue;
                try { if (!u2.CanUse(_kobold)) continue; } catch (Exception) { continue; }
                Vector3 d = u2.transform.position - pos;
                float m = d.magnitude;
                float facing = Vector3.Dot(d.normalized, fwd);
                float score = m * (facing > -0.2f ? 1f : 2.5f);
                if (score < bestScore) { bestScore = score; dist = m; best = u2; }
            }
            return best;
        }


        private const float InteractRange = 2.6f;


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
