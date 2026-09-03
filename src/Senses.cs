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

        // Compress the level row of the ray fan into per-direction clearance so the
        // model can reason "open right" instead of parsing 27 rays.
        private object FanClearance(Vector3 originForFan)
        {
            try
            {
                // Probe 8 compass directions, chest height, RayRange.
                string[] names = { "front", "front-right", "right", "back-right", "back", "back-left", "left", "front-left" };
                float[] offs = { 0f, 45f, 90f, 135f, 180f, -135f, -90f, -45f };
                var outp = new Dictionary<string, object>();
                float range = _cfgRayRange.Value;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 dir = Quaternion.Euler(0, _yawDeg + offs[i], 0) * Vector3.forward;
                    RaycastHit hit;
                    float d = range;
                    if (Physics.Raycast(originForFan, dir, out hit, range, ~0, QueryTriggerInteraction.Ignore) && !IsOwnCollider(hit.collider))
                        d = hit.distance;
                    // Bucket into coarse but useful distances.
                    string bucket = d < 1f ? "blocked" : d < 3f ? "close(" + F(d) + ")" : d < 8f ? "near(" + F(d) + ")" : "open";
                    outp[names[i]] = bucket;
                }
                return outp;
            }
            catch (Exception) { return new { }; }
        }


        // ------------------------------------------------------------------
        // senses
        // ------------------------------------------------------------------
        private object BuildPerception(bool includeImage)
        {
            try { return BuildPerceptionSafe(includeImage); }
            catch (Exception e) { return new { ok = false, reason = "perception_error", msg = e.Message }; }
        }


        private object BuildPerceptionSafe(bool includeImage)
        {
            if (!IsAlive(_kobold) || !IsAlive(_head)) return new { ok = false, reason = "no_body" };

            var rays = new List<object>();
            int n = Mathf.Max(1, _cfgRayCount.Value);
            float range = _cfgRayRange.Value;
            Vector3 origin = _head.position + _head.forward * 0.1f;
            float fov = _cam != null ? _cam.fieldOfView : 90f;
            float aspect = 1f;

            // Vertical rows: down (floor/ledges), level (obstacles), up (ceilings/high).
            // Each entry: a(ngle) d(ist) k(ind w/u/k/p/n) and pitch row p(d/l/u).
            float[] rows = { -35f, 0f, 25f };
            string[] rowNames = { "d", "l", "u" };
            for (int rI = 0; rI < rows.Length; rI++)
            {
                float pitchRow = rows[rI];
                for (int i = 0; i < n; i++)
                {
                    float t = n == 1 ? 0.5f : (float)i / (n - 1);
                    float hAngle = Mathf.Lerp(-fov * aspect * 0.5f, fov * aspect * 0.5f, t);
                    var dir = Quaternion.Euler(0, _yawDeg + hAngle, 0) * Quaternion.Euler(_pitchDeg + pitchRow, 0, 0) * Vector3.forward;
                    RaycastHit hit;
                    object r;
                    if (Physics.Raycast(origin, dir, out hit, range, ~0, QueryTriggerInteraction.Ignore) && !IsOwnCollider(hit.collider))
                    {
                        string name = "", kind = "w";
                        try
                        {
                            if (hit.collider != null)
                            {
                                var kb = hit.collider.GetComponentInParent<Kobold>();
                                var usable = hit.collider.GetComponentInParent<GenericUsable>();
                                if (kb != null) { kind = IsPlayerKobold(kb) ? "p" : "k"; name = kb.name; }
                                else if (usable != null) { kind = "u"; name = usable.name; }
                                else
                                {
                                    // Thin sill/window/ledge: low collision height means
                                    // you can see over it and probably climb it — "barrier".
                                    float topH = ProbeSurfaceTop(hit.point);
                                    if (topH < 1.35f) kind = "barrier";
                                }
                            }
                        }
                        catch (Exception) { }
                        r = name.Length == 0
                            ? (object)new { p = rowNames[rI], a = F(hAngle), d = F(hit.distance), k = kind }
                            : new { p = rowNames[rI], a = F(hAngle), d = F(hit.distance), k = kind, n = name };
                    }
                    else r = new { p = rowNames[rI], a = F(hAngle), k = "n" };
                    rays.Add(r);
                }
            }

            var pos = _kobold.transform.position;
            var ground = ProbeGround(pos);
            var clearance = FanClearance(originForFan: _head.position);

            return new {
                ok = true,
                me = MyName(),
                body = DescribeEquipment(),
                pos = new { x = F(pos.x), y = F(pos.y), z = F(pos.z) },
                yaw = F(_yawDeg),
                blocked = _blockedInfo,
                walls = _bumpInfo,
                ground = ground,
                clearance = clearance,
                vis_go = _visionSteer != null ? _visionSteer.deg.ToString("0") + "deg (" + _visionSteer.reason + ")" : null,
                needs = new {
                    energy = F(_kobold.GetEnergy()) + "/" + F(_kobold.GetMaxEnergy()),
                    horniness = F(_kobold.stimulation) + StimTrend(_kobold.stimulation)
                              + (_kobold.stimulation > 0.5f ? " very" : _kobold.stimulation > 0.25f ? "" : " low"),
                    eggs = F(GetEggVolume(_kobold)) + (IsReadyToLayEgg(_kobold) ? " ready_to_lay" : ""),
                    crouch = F(_crouch),
                },
                consumed = DrainReagentEvents(),
                in_station = IsInAnimationStation(),
                penetrated = IsPenetrated() ? PenetrationInfo() : null,
                penetrating = IsDickInside() ? DickInInfo() : null,
                heard = RecentPlayerChat(),
                asked = _pendingQuestion,
                answered = _answerBusy ? null : _lastAnswer,
                grabbed = _kobold.grabbed,
                rays = rays,
                nearby = DescribeNearby(),
            };
        }


        // The body's actual world yaw right now (rigidbody first, transform fallback).
        private float BodyYaw()
        {
            try
            {
                if (_controller != null && _controller.body != null) return _controller.body.rotation.eulerAngles.y;
                if (_kobold != null) return _kobold.transform.eulerAngles.y;
            }
            catch (Exception) { }
            return _yawDeg;
        }


        // True when k is the local human player's body (not an AI/wild kobold).
        private static bool IsPlayerKobold(Kobold k)
        {
            try
            {
                if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null && pp.kobold == k) return true;
                var desc = k.GetComponent<CharacterDescriptor>();
                return desc != null && desc.GetPlayerControlled() == CharacterDescriptor.ControlType.LocalPlayer;
            }
            catch (Exception) { return false; }
        }


        // What's underfoot / ahead at floor level: ground distance, whether we're
        // supported, and if a ledge or a low step is in front.
        private object ProbeGround(Vector3 pos)
        {
            try
            {
                // Down from the body.
                float groundDist = 0f; bool supported = false;
                RaycastHit ghit;
                if (Physics.Raycast(pos + Vector3.up * 0.6f, Vector3.down, out ghit, 10f, ~0, QueryTriggerInteraction.Ignore)
                    && !IsOwnCollider(ghit.collider))
                {
                    groundDist = ghit.distance - 0.6f;
                    supported = groundDist < 0.5f;
                }

                // Forward at knee vs. chest vs. head height to tell step vs window
                // vs wall.
                Vector3 fwd = Quaternion.Euler(0, _yawDeg, 0) * Vector3.forward;
                bool kneeBlocked  = CastBlocked(pos + Vector3.up * 0.3f, fwd, 1.3f);
                bool chestBlocked = CastBlocked(pos + Vector3.up * 0.9f, fwd, 1.3f);
                bool headBlocked  = CastBlocked(pos + Vector3.up * 1.6f, fwd, 1.3f);
                string ahead = "clear";
                if (kneeBlocked && !chestBlocked && !headBlocked) ahead = "step";         // auto-step
                else if (chestBlocked && !headBlocked) ahead = "sill";                    // window/counter — look over it, maybe climb
                else if (kneeBlocked || chestBlocked || headBlocked) ahead = "wall";     // solid

                // Ledge: clear at knee/chest but ground drops away just past our feet.
                bool ledge = false;
                if (!kneeBlocked && !chestBlocked)
                {
                    RaycastHit lhit;
                    Vector3 aheadDown = pos + fwd * 0.7f + Vector3.up * 0.5f;
                    if (!Physics.Raycast(aheadDown, Vector3.down, 6f, ~0, QueryTriggerInteraction.Ignore) ||
                        (Physics.Raycast(aheadDown, Vector3.down, out lhit, 6f, ~0, QueryTriggerInteraction.Ignore) && lhit.distance > 3f))
                        ledge = true;
                }

                return new { dist = F(groundDist), supported, ahead, ledge, drop = _ledgeDrop.HasValue ? F(_ledgeDrop.Value) : null };
            }
            catch (Exception) { return new { dist = "0", supported = true, ahead = "clear", ledge = false }; }
        }


        // Short 4-way rays around the body (front/back/left/right at chest height)
        // recording which directions have a wall within arm's reach. Result goes
        // into the next perception as `walls`.
        private void ProbeWallProximity()
        {
            try
            {
                if (!IsAlive(_kobold)) return;
                Vector3 center = _kobold.transform.position + Vector3.up * 0.6f;
                const float R = 1.1f;
                string[] names = { "front", "right", "back", "left" };
                float[] offs = { 0f, 90f, 180f, -90f };
                var found = new List<string>();
                for (int i = 0; i < 4; i++)
                {
                    Vector3 dir = Quaternion.Euler(0, _yawDeg + offs[i], 0) * Vector3.forward;
                    if (CastBlocked(center, dir, R)) found.Add(names[i]);
                }
                if (found.Count > 0) _bumpInfo = "wall " + string.Join("+", found.ToArray());
            }
            catch (Exception) { }
        }


        private bool CastBlocked(Vector3 origin, Vector3 dir, float range)
        {
            RaycastHit h;
            return Physics.Raycast(origin, dir, out h, range, ~0, QueryTriggerInteraction.Ignore) && !IsOwnCollider(h.collider);
        }


        // How tall is the obstacle at the hit point? Stack CheckSphere upward from
        // the hit; the first free height is the obstacle's top. <~1.3m => sill/
        // ledge/window (barrier), >= => real wall. Works for glass since it reads
        // collision geometry, not opacity.
        private float ProbeSurfaceTop(Vector3 hitPoint)
        {
            try
            {
                float baseY = _kobold != null ? _kobold.transform.position.y : hitPoint.y - 1f;
                for (float up = 0.15f; up <= 2.0f; up += 0.15f)
                {
                    var p = new Vector3(hitPoint.x, baseY + up, hitPoint.z);
                    var colliders = Physics.OverlapSphere(p, 0.09f, ~0, QueryTriggerInteraction.Ignore);
                    bool blocked = false;
                    foreach (var c in colliders) { if (c != null && !IsOwnCollider(c)) { blocked = true; break; } }
                    if (!blocked) return up; // first height with clear air
                }
                return 2.5f; // still blocked at head height — real wall
            }
            catch (Exception) { return 2.5f; }
        }


        // Convert a world offset into a compass bearing the model can act on directly,
        // relative to where the kobold is facing: "ahead", "right", "behind-left"…
        private string RelBearing(Vector3 to)
        {
            to.y = 0;
            if (to.sqrMagnitude < 0.0001f) return "here";
            float ang = Mathf.Atan2(to.x, to.z) * 57.29578f;   // absolute yaw of target
            float rel = Mathf.DeltaAngle(_yawDeg, ang);        // signed relative to facing
            float a = Mathf.Abs(rel);
            if (a < 22.5f) return "ahead";
            if (a < 67.5f) return rel > 0 ? "front-right" : "front-left";
            if (a < 112.5f) return rel > 0 ? "right" : "left";
            if (a < 157.5f) return rel > 0 ? "back-right" : "back-left";
            return "behind";
        }


        private float _lastGreetTime = -99f;

        private float _playerSeenTime = -99f;

        private float _lastStimLevel = -1f;   // for stim trend calc

        private float _lastAmbientTime = -99f; // when we last said something unprompted


        // Direction of stimulation as a short suffix: "↑"/"↓"/"" so the model sees it changing.
        private string StimTrend(float stim)
        {
            string t;
            if (_lastStimLevel < 0f) t = "";
            else if (stim > _lastStimLevel + 0.01f) t = "↑";
            else if (stim < _lastStimLevel - 0.01f) t = "↓";
            else t = "";
            _lastStimLevel = stim;
            return t;
        }


        // Turn "BedStation(2) (Clone)" into "BedStation".
        internal static string CleanName(string n)
        {
            if (string.IsNullOrEmpty(n)) return n;
            int i = n.IndexOf("(Clone", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) n = n.Substring(0, i).TrimEnd();
            i = n.IndexOf('(');
            if (i >= 0) n = n.Substring(0, i).TrimEnd();
            return n;
        }


        // Human-readable category so the model can act on needs, not object noise.
        private static string ClassifyUsable(string name)
        {
            if (string.IsNullOrEmpty(name)) return "usable";
            string s = name.ToLowerInvariant();
            if (s.Contains("bed"))        return "bed";
            if (s.Contains("toilet") || s.Contains("potty") || s.Contains("bathroom")) return "toilet";
            if (s.Contains("tub") || s.Contains("bath") || s.Contains("shower")) return "bath";
            if (s.Contains("breeding") || s.Contains("threeway") || s.Contains("mount") || s.Contains("actionstation")) return "play";
            if (s.Contains("sex") ) return "play";
            if (s.Contains("laying") || s.Contains("ovip") || s.Contains("nest") || s.Contains("egg")) return "nest";
            if (s.Contains("kitchen") || s.Contains("stove") || s.Contains("blender") || s.Contains("food") || s.Contains("cook")) return "food";
            if (s.Contains("swap") || s.Contains("possess") || s.Contains("body")) return "bodyswap";
            if (s.Contains("door") || s.Contains("gate")) return "door";
            if (s.Contains("upgrade") || s.Contains("machine")) return "machine";
            if (s.Contains("table") || s.Contains("chair") || s.Contains("sofa") || s.Contains("couch")) return "seat";
            return "usable";
        }


        private List<object> DescribeNearby()
        {
            var list = new List<object>();
            if (!IsAlive(_kobold)) return list;
            bool sawPlayer = false;
            try
            {
                var seen = new HashSet<int>();
                foreach (var c in Physics.OverlapSphere(_kobold.transform.position, 14f, ~0, QueryTriggerInteraction.Collide))
                {
                    if (c == null) continue;
                    GenericUsable u = c.GetComponentInParent<GenericUsable>();
                    Kobold k = c.GetComponentInParent<Kobold>();
                    if (k != null && k == _kobold) continue;
                if (u == null && k == null) continue;
                    var rootComp = (Component)k ?? u;
                    if (!seen.Add(rootComp.transform.root.GetInstanceID() * 31 + rootComp.GetInstanceID())) continue;
                    Vector3 d = c.transform.position - _kobold.transform.position;
                    string label = k != null ? "kobold" : "usable";
                    string nm = k != null ? k.name : u.name;
                    if (k != null && IsPlayerKobold(k)) { label = "player"; sawPlayer = true; }
                    string hrel = d.y > 0.5f ? "above" : d.y < -0.5f ? "below" : "level";

                    // For usables: strip Unity's "(Clone)", report whether it's
                    // useable right now (bed free? station occupied?), and a guess
                    // at what it is so the model can plan toward needs.
                    string info = null;
                    if (u != null)
                    {
                        nm = CleanName(nm);
                        string kind = ClassifyUsable(nm);
                        bool canUse = true;
                        try { canUse = u.CanUse(_kobold); } catch (Exception) { }
                    info = kind + (canUse ? "" : ":busy");
                    // Landmark memory: remember where things are once seen.
                    if (canUse) RememberFact(kind + " is " + RelBearing(d) + " here");
                }
                    list.Add(new {
                        k = label,
                        n = nm,
                        d = F(d.magnitude),
                        dir = RelBearing(d),      // ahead/front-right/right/behind... relative to facing
                        h = hrel,                 // above/level/below
                        i = info,                 // e.g. "bed", "toilet", "sex:busy" for usables
                    });
                    if (list.Count >= 8) break;
                }
            }
            catch (Exception e) { Logger.LogWarning("nearby: " + e.Message); }

            // Notice the player: greet them the first time (and re-greet after a
            // cooldown), and record when we last saw them so the prompt can say so.
            if (sawPlayer)
            {
                bool firstSight = _playerSeenTime < -90f;
                _playerSeenTime = Time.unscaledTime;
                if ((firstSight && Time.unscaledTime - _lastGreetTime > 6f) || Time.unscaledTime - _lastGreetTime > 45f)
                {
                    _lastGreetTime = Time.unscaledTime;
                    string greet = firstSight ? "oh, hi!" : "hey again";
                    // Use ToolSay, which posts to the real chat window too.
                    RunOnMainThreadAsync(() => { try { ToolSay(new TextArgs(greet)); } catch (Exception e) { Logger.LogWarning("greet: " + e.Message); } });
                    Logger.LogInfo("KKLLMNPC: noticed player, greeting.");
                }
            }
            return list;
        }


        private string CaptureImageB64()
        {
            byte[] jpg = CaptureImageBytes();
            return jpg != null ? "data:image/jpeg;base64," + Convert.ToBase64String(jpg) : null;
        }


        private byte[] CaptureImageBytes()
        {
            if (_cam == null || !RtCreated(_rt)) return null;
            try
            {
                _cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = _rt;
                Texture2D tex = null;
                try
                {
                    tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0, false);
                    tex.Apply();
                    return UnityEngine.ImageConversion.EncodeToJPG(tex, 60);
                }
                finally
                {
                    RenderTexture.active = prev;
                    if (tex != null) Destroy(tex);
                }
            }
            catch (Exception e) { Logger.LogWarning("screenshot: " + e.Message); return null; }
        }
    }
}
