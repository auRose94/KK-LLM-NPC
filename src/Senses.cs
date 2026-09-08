// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Perception: ray fan, clearance sectors, ground/ledge probes, nearby objects, identity.
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
using System.Threading.Tasks;
using System.Net.Http;
namespace KKLLMNPC
{
    internal partial class NPCInstance
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
        // The JSON sent to the LLM every tick. Everything the model can act on:
        // identity, body state, vision caption, rays, clearance, nearby, needs, history.
        // Wrapped in per-body try/catch — a perception failure returns {ok:false} rather
        // than breaking the loop.
        private object BuildPerception(bool includeImage)
        {
            try
            {
                // Throttle full perception when idle to save work
                bool active = Time.unscaledTime - _lastMoveTime < 2f
                    || IsInAnimationStation()
                    || IsPenetrated()
                    || IsDickInside();
                int throttleTicks = active ? 1 : 2;
                bool useCache = _tick - _lastFullPerceptionTick < throttleTicks && _cachedPerception != null;
                if (useCache) Logger.LogInfo($"perception cache hit (active={active}, throttle={throttleTicks})");
                if (useCache)
                {
                    // Lightweight update: keep cached perception but refresh dynamic fields
                    var cached = _cachedPerception as Dictionary<string, object>;
                    if (cached != null)
                    {
                        cached["tick"] = _tick;
                        cached["yaw"] = F(_yawDeg);
                        // Update needs quickly without full raycast
                        var needs = new
                        {
                            energy = F(_kobold.GetEnergy()) + "/" + F(_kobold.GetMaxEnergy()),
                            horniness = HorninessText(),
                            eggs = F(GetEggVolume(_kobold)) + (IsReadyToLayEgg(_kobold) ? " ready_to_lay" : ""),
                            crouch = F(_crouch),
                        };
                        cached["needs"] = needs;
                        return cached;
                    }
                }
                var full = BuildPerceptionSafe(includeImage);
                _lastFullPerceptionTick = _tick;
                _cachedPerception = full is Dictionary<string, object> d ? d : null;
                if (!useCache) Logger.LogInfo($"perception full build (active={active})");
                return full;
            }
            catch (Exception e) { return new { ok = false, reason = "perception_error", msg = e.Message }; }
        }

        private object BuildPerceptionSafe(bool includeImage)
        {
            if (!IsAlive(_kobold) || !IsAlive(_head)) return new { ok = false, reason = "no_body" };
            // Build perception as dict for caching
            var result = new Dictionary<string, object>();

            var rays = new List<object>();
            result["rays"] = rays;
            int n = Mathf.Max(1, _cfgRayCount != null ? _cfgRayCount.Value : 9);
            float range = _cfgRayRange != null ? _cfgRayRange.Value : 25f;
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
                        Vector3 size = Vector3.zero;
                        Vector3 wpos = hit.point;
                        float facingDeg = 0f;
                        try
                        {
                            if (hit.collider != null)
                            {
                                // Physical footprint of the thing the ray hit — the model
                                // can tell a wall from a chair from a kobold by dimensions,
                                // and plan around corners, not just names.
                                var rend = hit.collider.GetComponentInChildren<Renderer>();
                                if (rend != null) { size = rend.bounds.size; wpos = rend.bounds.center; }
                                facingDeg = hit.collider.transform.eulerAngles.y;

                                var kb = hit.collider.GetComponentInParent<Kobold>();
                                var usable = hit.collider.GetComponent<GenericUsable>() ?? hit.collider.GetComponentInParent<GenericUsable>();
                                if (kb != null) { kind = IsPlayerKobold(kb) ? "p" : "k"; name = kb.name; }
                                else if (usable != null) { kind = "u"; name = usable.name; }
                                else
                                {
                                    // Low anonymous geometry — sill/window, step-over and
                                    // climbable — NOT an obstacle worth narrating. Neutral
                                    // letter so models don't dramatize it as a "barrier".
                                    float topH = ProbeSurfaceTop(hit.point);
                                    if (topH < 1.35f) kind = "s";
                                }
                            }
                        }
                        catch (Exception) { }
                        // Compact: only named things get the extra fields (world-position,
                        // bounding-box size, which way it's facing). Anonymous world geometry
                        // stays terse to keep the prompt small.
                        r = name.Length == 0
                            ? (object)new { p = rowNames[rI], a = F(hAngle), d = F(hit.distance), k = kind }
                            : new
                            {
                                p = rowNames[rI],
                                a = F(hAngle),
                                d = F(hit.distance),
                                k = kind,
                                n = name,
                                w = F(size.x),
                                l = F(size.z),
                                h = F(size.y),
                                x = F(wpos.x),
                                y = F(wpos.y),
                                z = F(wpos.z),
                                f = F(facingDeg),
                            };
                    }
                    else r = new { p = rowNames[rI], a = F(hAngle), k = "n" };
                    rays.Add(r);
                }
            }

            var pos = _kobold.transform.position;
            var ground = ProbeGround(pos);
            var clearance = FanClearance(originForFan: _head.position);

            result["ok"] = true;
            result["me"] = MyName();
            // The human player: chat name + the body/mesh they're wearing. A nearby
            // kobold named after that body IS the player (their avatar), not another
            // kobold — the model otherwise can't connect "AbsolB" to "Rosemary".
            result["player"] = PlayerIdentity();
            // Session clock: elapsed time since this NPC instance started.
            float sessionElapsed = Time.unscaledTime - _sessionStartTime;
            int eh = (int)(sessionElapsed / 3600f);
            int em = (int)((sessionElapsed % 3600f) / 60f);
            int es = (int)(sessionElapsed % 60f);
            result["time"] = string.Format("{0:D2}:{1:D2}:{2:D2}", eh, em, es);
            // Model info from the startup probe — tells the model what it is and its limits.
            if (ModelProbe.DetectedModelName != null)
                result["model"] = ModelProbe.DetectedModelName + (ModelProbe.DetectedContextLength > 0 ? " (ctx:" + ModelProbe.DetectedContextLength + ")" : "")
                    + (_resolvedTier != null ? " tier:" + _resolvedTier : "");
            result["gender"] = InferGender();
            result["pronouns"] = InferPronouns();
            result["body"] = DescribeEquipment();
            result["pos"] = new { x = F(pos.x), y = F(pos.y), z = F(pos.z) };
            result["yaw"] = F(_yawDeg);
            if (_cfgRadarEnabled != null && _cfgRadarEnabled.Value)
                result["radar"] = BuildRadarMap(rays);
            result["blocked"] = _blockedInfo;
            result["walls"] = _bumpInfo;
            result["ground"] = ground;
            result["clearance"] = clearance;
            result["vis_go"] = _visionSteer != null ? _visionSteer.deg.ToString("0") + "deg (" + _visionSteer.reason + ")" : null;
            result["needs"] = new
            {
                energy = F(_kobold.GetEnergy()) + "/" + F(_kobold.GetMaxEnergy()),
                horniness = HorninessText(),
                eggs = F(GetEggVolume(_kobold)) + (IsReadyToLayEgg(_kobold) ? " ready_to_lay" : ""),
                crouch = F(_crouch),
            };
            result["consumed"] = DrainReagentEvents();
            result["in_station"] = IsInAnimationStation();
            bool inStn = IsInAnimationStation();
            if (inStn && _stationPurpose != null)
            {
                float elapsed = Time.unscaledTime - _stationEntryTime;
                string elapsedStr = elapsed < 60f ? ((int)elapsed) + "s"
                    : elapsed < 3600f ? ((int)(elapsed / 60)) + "m" + ((int)(elapsed % 60)) + "s"
                    : ((int)(elapsed / 3600)) + "h" + ((int)((elapsed % 3600) / 60)) + "m";
                result["station_use"] = _stationPurpose + " (" + PurposeFor(_stationPurpose) + ") for " + elapsedStr;
            }
            else result["station_use"] = null;
            result["station_stay"] = _stayInStation;
            result["penetrated"] = IsPenetrated() ? PenetrationInfo() : null;
            result["penetrating"] = IsDickInside() ? DickInInfo() : null;
            result["stim_from"] = StimFromText();
            result["partners"] = PartnersList();
            result["heard"] = RecentPlayerChat();
            result["asked"] = _pendingQuestion;
            result["answered"] = _answerBusy ? null : _lastAnswer;
            // Goal machine: the persistent goal ("bigger thinking") + repetition guard.
            // This is what the model works toward instead of re-deriving a goal each turn.
            result["goal"] = GoalPerception();
            string nudge = RepetitionNudge();
            if (nudge != null) result["nudge"] = nudge;
            result["grabbed"] = _kobold.grabbed;
            result["rays"] = rays;
            var nearby = DescribeNearby();
            result["nearby"] = nearby;
            string areaTxt = SpatialLayout();
            result["area"] = areaTxt;
            // Visionless fallback: the caption pass never runs (Vision.Enabled=false),
            // so give commentary / ask / scene memory a real description of the area.
            if (!_cfgVision.Value || string.IsNullOrEmpty(_sceneDesc) || _sceneDesc == "unknown")
                _sceneDesc = areaTxt;

            // Context pressure: tell the model how full its context window is.
            // Helps it know to be terse when context is running low.
            if (ModelProbe.DetectedContextLength > 0)
            {
                int factCount = 0, histCount = 0, thoughtCount = 0;
                lock (_facts) { factCount = _facts.Count; }
                lock (_history) { histCount = _history.Count; }
                lock (_thoughtHistory) { thoughtCount = _thoughtHistory.Count; }
                // Rough estimate: ~300 tokens system prompt, ~200 per nearby item,
                // ~50 per fact, ~30 per history, ~20 per thought, ~15 per chat line.
                int estTokens = 300 + (nearby.Count * 200) + (factCount * 50) + (histCount * 30) + (thoughtCount * 20);
                float fill = (float)estTokens / ModelProbe.DetectedContextLength;
                if (fill > 0.7f)
                    result["context_pressure"] = fill > 0.9f ? "critical" : "high";
            }

            // Compaction status: tell the model if it's running in compressed mode.
            if (_ctxMgr != null && _ctxMgr.CompactionLevel > 0)
                result["compaction"] = _ctxMgr.CompactionStatusJson();

            return result;
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

        // ASCII radar: top-down grid from level-row rays.  Center = '@' (self).
        // Row 0 = farthest forward (in front of the kobold).
        private string BuildRadarMap(List<object> rays)
        {
            int S = _cfgRadarSize != null && _cfgRadarSize.Value > 0 ? _cfgRadarSize.Value : 10;
            float scale = _cfgRadarScale != null && _cfgRadarScale.Value > 0f ? _cfgRadarScale.Value : 1.2f;
            char[,] grid = new char[S * 2 + 1, S * 2 + 1];
            for (int r = 0; r <= S * 2; r++)
                for (int c = 0; c <= S * 2; c++)
                    grid[r, c] = '.';
            grid[S, S] = '@';

            float yaw = _yawDeg;
            foreach (var obj in rays)
            {
                if (!(obj is Dictionary<string, object> ray)) continue;
                string p = ""; string k = "n";
                float a = 0, d = 999;
                if (ray.ContainsKey("p")) p = ray["p"].ToString();
                if (ray.ContainsKey("k")) k = ray["k"].ToString();
                if (ray.ContainsKey("a")) { try { a = Convert.ToSingle(ray["a"]); } catch { } }
                if (ray.ContainsKey("d")) { try { d = Convert.ToSingle(ray["d"]); } catch { } }
                if (p != "l" || k == "n") continue;

                float worldAngle = yaw + a;
                float rad = worldAngle * (float)(Math.PI / 180.0);
                float gx = Mathf.Sin(rad) * d / scale;   // +X = right in Unity
                float gz = Mathf.Cos(rad) * d / scale;
                int col = Mathf.RoundToInt(gx) + S;
                int row = S - Mathf.RoundToInt(gz);
                if (row < 0 || row > S * 2 || col < 0 || col > S * 2) continue;
                if (row == S && col == S) continue;

                char ch;
                switch (k)
                {
                    case "w": ch = 'W'; break;
                    case "u": ch = 'U'; break;
                    case "k": ch = 'K'; break;
                    case "p": ch = 'P'; break;
                    case "barrier":
                    case "s": ch = 'S'; break;
                    default: ch = '?'; break;
                }
                grid[row, col] = ch;
            }

            var sb = new System.Text.StringBuilder();
            for (int r = 0; r <= S * 2; r++)
            {
                for (int c = 0; c <= S * 2; c++)
                    sb.Append(grid[r, c]);
                if (r < S * 2) sb.AppendLine();
            }
            return sb.ToString();
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

        // The local player's chat name (Photon nickname), or null if not in a room.
        internal static string PlayerChatName()
        {
            try
            {
                var lp = PhotonNetwork.LocalPlayer;
                if (lp != null && !string.IsNullOrEmpty(lp.NickName)) return lp.NickName;
            }
            catch (Exception) { }
            return null;
        }

        // The player's identity for perception: their chat name plus the body/mesh
        // they're currently wearing, so the model can recognize the player's avatar
        // kobold (named after the mesh prefab) as THE player.
        private object PlayerIdentity()
        {
            try
            {
                string chat = PlayerChatName();
                string body = null;
                if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null)
                    body = CleanName(pp.kobold.name);
                if (chat == null && body == null) return null;
                return new { chat, body };
            }
            catch (Exception) { return null; }
        }

        // What's underfoot / ahead at floor level: ground distance, whether we're
        // supported, and if a ledge or a low step is in front.
        // Vertical/forward probing that teaches apart step vs. sill vs. wall and detects
        // ledges with drop height, so the model can decide 'hop down' vs 'turn'.
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
                bool kneeBlocked = CastBlocked(pos + Vector3.up * 0.3f, fwd, 1.3f);
                bool chestBlocked = CastBlocked(pos + Vector3.up * 0.9f, fwd, 1.3f);
                bool headBlocked = CastBlocked(pos + Vector3.up * 1.6f, fwd, 1.3f);
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

        // Prose description of the surrounding area, aimed at visionless models:
        // a full 360° chest-height sweep that reports how far each cardinal direction
        // stays clear, which headings are open, a suggested best heading, and the
        // nearest named things (wall vs. usable vs. kobold). Lets the model describe
        // the room and pick a corridor/doorway without ever seeing it.
        private string SpatialLayout()
        {
            try
            {
                if (!IsAlive(_kobold) || !IsAlive(_head)) return "unknown";
                const int N = 16; // 22.5° per step, full circle
                float range = Mathf.Min(_cfgRayRange.Value, 15f);
                Vector3 origin = _head.position + Vector3.up * -0.15f; // chest height
                float[] dist = new float[N];
                string[] kind = new string[N];
                string[] name = new string[N];
                for (int i = 0; i < N; i++)
                {
                    float relDeg = i * 22.5f;
                    if (relDeg > 180f) relDeg -= 360f;
                    var dir = Quaternion.Euler(0, _yawDeg + relDeg, 0) * Vector3.forward;
                    RaycastHit hit;
                    dist[i] = range; kind[i] = "open";
                    if (Physics.Raycast(origin, dir, out hit, range, ~0, QueryTriggerInteraction.Ignore) && !IsOwnCollider(hit.collider))
                    {
                        dist[i] = hit.distance;
                        try
                        {
                            var kb = hit.collider.GetComponentInParent<Kobold>();
                            var us = hit.collider.GetComponent<GenericUsable>() ?? hit.collider.GetComponentInParent<GenericUsable>();
                            if (kb != null) { kind[i] = IsPlayerKobold(kb) ? "player" : "kobold"; name[i] = CleanName(kb.name); }
                            else if (us != null) { kind[i] = "usable"; name[i] = CleanName(us.name); }
                            else { float topH = ProbeSurfaceTop(hit.point); kind[i] = topH < 1.35f ? "sill" : "wall"; }
                        }
                        catch (Exception) { kind[i] = "wall"; }
                    }
                }

                // Cardinal distances (0/front, 90/right, 180/back, -90/left).
                string[] card = { "front", "right", "back", "left" };
                int[] cardIdx = { 0, 4, 8, 12 };
                var cards = new List<string>();
                for (int c = 0; c < 4; c++)
                {
                    int i = cardIdx[c];
                    cards.Add(card[c] + " " + F(dist[i]) + "m " + (name[i].Length > 0 ? name[i] : kind[i]));
                }

                // Open headings (>=8m and not blocked by anything) + best one, biased
                // toward straight ahead, then farthest.
                var open = new List<string>();
                int bestI = 0;
                bool anyOpen = false;
                for (int i = 0; i < N; i++)
                {
                    if (dist[i] >= 8f && kind[i] == "open")
                    {
                        anyOpen = true;
                        float rel = i * 22.5f; if (rel > 180f) rel -= 360f;
                        open.Add((rel == 0 ? "front" : rel > 0 ? "+" + F(rel) : F(rel)) + "(" + F(dist[i]) + "m)");
                    }
                    if (dist[i] > dist[bestI]) bestI = i;
                }
                if (anyOpen)
                {
                    // Prefer the nearest-to-front open heading; tie-break by distance.
                    for (int i = 0; i < N; i++)
                    {
                        if (!(dist[i] >= 8f && kind[i] == "open")) continue;
                        float ra = Mathf.Abs(i * 22.5f > 180f ? i * 22.5f - 360f : i * 22.5f);
                        float rb = Mathf.Abs(bestI * 22.5f > 180f ? bestI * 22.5f - 360f : bestI * 22.5f);
                        if (ra + 1f < rb || (Mathf.Abs(ra - rb) <= 1f && dist[i] > dist[bestI])) bestI = i;
                    }
                }
                float bestRel = bestI * 22.5f; if (bestRel > 180f) bestRel -= 360f;
                string bestTxt = anyOpen
                    ? "best " + (bestRel == 0 ? "front" : bestRel > 0 ? "+" + F(bestRel) : F(bestRel))
                    : (kind[bestI] == "open" ? "can go " + F(dist[bestI]) + "m" : "surrounded by " + kind[bestI]);

                var parts = new List<string>();
                parts.Add(string.Join(" ", cards.ToArray()));
                if (open.Count > 0) parts.Add("open " + string.Join(" ", open.ToArray()));
                parts.Add(bestTxt);

                // Nearest named things (dupes collapsed) so it can comment on them.
                var seen = new HashSet<string>();
                var near = new List<string>();
                for (int pass = 1; pass <= 3 && near.Count < 3; pass++)
                {
                    for (int i = 0; i < N; i++)
                    {
                        if (name[i].Length == 0 || dist[i] > 10f || seen.Contains(name[i])) continue;
                        if (dist[i] <= (pass == 1 ? 4f : pass == 2 ? 7f : 10f))
                        {
                            seen.Add(name[i]);
                            float rel = i * 22.5f; if (rel > 180f) rel -= 360f;
                            string dw = "front";
                            if (rel >= 22.5f && rel < 67.5f) dw = "front-right";
                            else if (rel >= 67.5f && rel < 112.5f) dw = "right";
                            else if (rel >= 112.5f && rel < 157.5f) dw = "back-right";
                            else if (rel >= 157.5f || rel <= -157.5f) dw = "back";
                            else if (rel > -157.5f && rel <= -112.5f) dw = "back-left";
                            else if (rel > -112.5f && rel < -67.5f) dw = "left";
                            else if (rel >= -67.5f && rel < -22.5f) dw = "front-left";
                            near.Add(name[i] + " " + dw + " " + F(dist[i]) + "m");
                        }
                    }
                }
                if (near.Count > 0) parts.Add("near " + string.Join(", ", near.ToArray()));

                string s = "area: " + string.Join(". ", parts.ToArray());
                return s.Length <= 320 ? s : s.Substring(0, 320);
            }
            catch (Exception) { return "area: unknown"; }
        }

        // How tall is the obstacle at the hit point? Stack CheckSphere upward from
        // the hit; the first free height is the obstacle's top. <~1.3m => sill/
        // ledge/window (barrier), >= => real wall. Works for glass since it reads
        // collision geometry, not opacity.
        // When a ray hits a solid, measure how tall it actually is by stacking sphere
        // checks upward; under ~1.35m it's a 'sill/window/barrier' — visible and climable.
        private float ProbeSurfaceTop(Vector3 hitPoint)
        {
            try
            {
                float baseY = _kobold != null ? _kobold.transform.position.y : hitPoint.y - 1f;
                for (float up = 0.15f; up <= 2.0f; up += 0.15f)
                {
                    var p = new Vector3(hitPoint.x, baseY + up, hitPoint.z);
                    var colliders = Physics.OverlapSphere(p, 0.09f, ~0, QueryTriggerInteraction.Ignore);
                    if (colliders == null || colliders.Length == 0) return up;
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
        // Compass bearing relative to the current facing (ahead/front-right/right/...) —
        // the model reads these as words instead of doing vector trig. The reference is
        // the BODY yaw (BodyYaw), not the camera yaw: that's the facing other characters
        // actually see as "in front".
        private string RelBearing(Vector3 to)
        {
            to.y = 0;
            if (to.sqrMagnitude < 0.0001f) return "here";
            float ang = Mathf.Atan2(to.x, to.z) * 57.29578f;
            float rel = Mathf.DeltaAngle(BodyYaw(), ang);
            float a = Mathf.Abs(rel);
            if (a < 22.5f) return "ahead";
            if (a < 67.5f) return rel > 0 ? "front-right" : "front-left";
            if (a < 112.5f) return rel > 0 ? "right" : "left";
            if (a < 157.5f) return rel > 0 ? "back-right" : "back-left";
            return "behind";
        }

        // Numeric relative bearing, in degrees: +right/-left of current facing.
        // The model feeds this into turn_deg directly: walk(turn_deg:dir_deg).
        private float RelBearingDeg(Vector3 to)
        {
            to.y = 0;
            if (to.sqrMagnitude < 0.0001f) return 0f;
            float ang = Mathf.Atan2(to.x, to.z) * 57.29578f;
            return Mathf.DeltaAngle(BodyYaw(), ang);
        }

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

        // Horniness the model sees: the body's live stimulation while being played
        // with, otherwise the slowly-climbing slow-burn value (so the NPC starts
        // wanting play even when nothing is happening).
        private string HorninessText()
        {
            try
            {
                float stim = _kobold.stimulation;
                bool driven = stim >= 0.15f;
                float val = driven ? stim : _horny;
                string tier = val > 0.5f ? " very" : val > 0.25f ? "" : " low";
                string src = (!driven && _horny > 0.3f)
                    ? " (slow-burn: unstimulated a while — horniness climbing)"
                    : "";
                return F(val) + StimTrend(stim) + tier + src;
            }
            catch (Exception) { return F(_horny); }
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
        // Turn a usable's Unity object name into the semantic bucket the model plans on:
        // play/bed/toilet/bath/nest/seat/door/bodyswap/machine/food.
        // Order matters: "play" before "bed" so a "PlayBed"/"PlayStation" is a play
        // station (pleasure), NOT a bed (rest) — that mislabel is what made the model
        // think play stations were for sleeping.
        internal static string ClassifyUsable(string name)
        {
            if (string.IsNullOrEmpty(name)) return "usable";
            string s = name.ToLowerInvariant();
            if (s.Contains("breeding") || s.Contains("threeway") || s.Contains("mount")
                || s.Contains("actionstation") || s.Contains("play") || s.Contains("sex")
                || s.Contains("erotic")) return "play";
            if (s.Contains("bed") || s.Contains("sleep") || s.Contains("cot")
                || s.Contains("mattress") || s.Contains("nap") || s.Contains("rest")) return "bed";
            if (s.Contains("toilet") || s.Contains("potty") || s.Contains("bathroom")) return "toilet";
            if (s.Contains("tub") || s.Contains("bath") || s.Contains("shower")) return "bath";
            if (s.Contains("laying") || s.Contains("ovip") || s.Contains("nest") || s.Contains("egg")) return "nest";
            if (s.Contains("kitchen") || s.Contains("stove") || s.Contains("blender") || s.Contains("food") || s.Contains("cook")) return "food";
            if (s.Contains("swap") || s.Contains("possess") || s.Contains("body")) return "bodyswap";
            if (s.Contains("door") || s.Contains("gate")) return "door";
            if (s.Contains("contract") || s.Contains("blueprint") || s.Contains("plan") || s.Contains("construction")) return "contract";
            if (s.Contains("upgrade") || s.Contains("machine") || s.Contains("milk")) return "machine";
            if (s.Contains("table") || s.Contains("chair") || s.Contains("sofa") || s.Contains("couch") || s.Contains("seat")) return "seat";
            return "usable";
        }

        // One short phrase explaining what a category is FOR, appended to the
        // nearby 'i' field so the model never has to guess (play ≠ resting).
        internal static string PurposeFor(string kind)
        {
            switch (kind)
            {
                case "play": return "pleasure station";
                case "bed": return "resting - can also be used for play";
                case "nest": return "egg laying";
                case "machine": return "mounted play/farming";
                case "toilet": return "relief";
                case "bath": return "clean";
                case "seat": return "just a seat";
                case "door": return "passage";
                case "food": return "cook/eat — drop items into blender to make edible food";
                case "contract": return "buy to unlock a machine";
                case "bodyswap": return "swap bodies";
                default: return null;
            }
        }

        // OverlapSphere within 14m, deduped by root, tagged: k=kobold p=player u=usable,
        // with name, distance, relative bearing, and the usable category (bed/toilet/play/nest).
        // Also detects the human player for hello-greetings.
        private List<object> DescribeNearby()
        {
            var list = new List<object>();
            if (!IsAlive(_kobold)) return list;
            bool sawPlayer = false;
            try
            {
                var seen = new HashSet<int>();
                var nearbyRadius = 14f;
                var colliders = Physics.OverlapSphere(_kobold.transform.position, nearbyRadius, ~0, QueryTriggerInteraction.Collide);
                if (colliders == null) return list;
                foreach (var c in colliders)
                {
                    if (c == null) continue;
                    GenericUsable u = null;
                    Kobold k = null;
                    try
                    {
                        u = c.GetComponent<GenericUsable>() ?? c.GetComponentInParent<GenericUsable>();
                        k = c.GetComponentInParent<Kobold>();
                    }
                    catch (Exception) { continue; }
                    if (k != null && k == _kobold) continue;
                    if (u == null && k == null) continue;
                    var rootComp = (Component)k ?? u;
                    if (rootComp == null) continue;
                    if (!seen.Add(rootComp.transform.root.GetInstanceID() * 31 + rootComp.GetInstanceID())) continue;
                    Vector3 d = c.transform.position - _kobold.transform.position;
                    string label = k != null ? "kobold" : "usable";
                    string nm = k != null ? k.name : u.name;
                    bool isPlayer = k != null && IsPlayerKobold(k);
                    if (isPlayer) { label = "player"; sawPlayer = true; nm = CleanName(nm); }
                    string hrel = d.y > 0.5f ? "above" : d.y < -0.5f ? "below" : "level";

                    // Bounds + world position + facing so the model can plan around it
                    // (a chair you can slide past vs. a cabinet you route around).
                    Vector3 bsize = Vector3.zero;
                    Vector3 bpos = c.transform.position;
                    float bfacing = 0f;
                    try
                    {
                        var rend = c.GetComponentInChildren<Renderer>();
                        if (rend != null) { bsize = rend.bounds.size; bpos = rend.bounds.center; }
                        bfacing = c.transform.eulerAngles.y;
                    }
                    catch (Exception) { }

                    // For usables: strip Unity's "(Clone)", report whether it's
                    // useable right now (bed free? station occupied?), and a guess
                    // at what it is so the model can plan toward needs.
                    string info = null;
                    string stateNote = null;
                    if (u != null)
                    {
                        nm = CleanName(nm);
                        string kind = ClassifyUsable(nm);
                        bool canUse = true;
                        try { canUse = u.CanUse(_kobold); } catch (Exception) { }

                        // Detect unbought upgrades and unbuilt machines so the
                        // model doesn't waste time trying to use them.
                        string stateTag = "";
                        try
                        {
                            // ConstructionContract: purchasable station blueprint
                            if (u.GetType().Name == "ConstructionContract")
                            {
                                bool bought = GetField<bool>(u, "bought");
                                float cost = GetField<float>(u, "cost");
                                if (bought)
                                    stateTag = ":done";
                                else
                                {
                                    stateTag = ":needs_buy";
                                    stateNote = "costs " + cost + " coins — must buy before the machine works";
                                }
                            }
                            // UsableMachine: the machine itself — may not be constructed yet
                            if (u is UsableMachine)
                            {
                                bool constructed = GetField<bool>(u, "constructed");
                                if (!constructed)
                                {
                                    stateTag = ":not_built";
                                    stateNote = "not built yet — find and buy its ConstructionContract first";
                                }
                            }
                        }
                        catch (Exception) { }

                        info = kind + stateTag + (canUse ? "" : ":busy")
                               + (PurposeFor(kind) != null ? " (" + PurposeFor(kind) + ")" : "");
                        // Landmark memory: remember where things are once seen.
                        if (canUse) RememberFact(kind + " is " + RelBearing(d) + " here");
                    }
                    // Stable addressable id so the model can target this exact object:
                    // go_to id:N / interact id:N. Door/usable/kobold all get one.
                    // Use rootComp.transform (the actual object) not .root (scene root)
                    // so the id resolves to the station, not the parent container.
                    int tid = TargetIdFor(rootComp.transform, nm);
                    var entry = new Dictionary<string, object>
                    {
                        ["id"] = tid,
                        ["k"] = label,
                        ["n"] = nm,
                        ["d"] = F(d.magnitude),
                        ["dir"] = RelBearing(d),
                        ["dir_deg"] = F(RelBearingDeg(d)),
                        ["h"] = hrel,
                        ["i"] = info,
                        ["w"] = F(bsize.x),
                        ["l"] = F(bsize.z),
                        ["ht"] = F(bsize.y),
                        ["x"] = F(bpos.x),
                        ["y"] = F(bpos.y),
                        ["z"] = F(bpos.z),
                        ["f"] = F(bfacing),
                    };
                    if (stateNote != null) entry["note"] = stateNote;
                    if (isPlayer)
                    {
                        string chat = PlayerChatName();
                        entry["who"] = "the player" + (chat != null ? " — chat name: " + chat : "") + " (their avatar; mesh name: " + nm + ")";
                    }
                    list.Add(entry);
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
            if (jpg != null && jpg.Length > 0 && _cfgVisionDebug != null && _cfgVisionDebug.Value)
                DumpVisionFrame(jpg);
            return jpg != null && jpg.Length > 0 ? "data:image/jpeg;base64," + Convert.ToBase64String(jpg) : null;
        }

        private byte[] CaptureImageBytes()
        {
            if (_cam == null || _rt == null || !RtCreated(_rt)) return null;
            try
            {
                // Left eye.
                _cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = _rt;
                // Pool texture reuse
                if (_texPoolL == null || _texPoolL.width != _rt.width || _texPoolL.height != _rt.height)
                {
                    if (_texPoolL != null) Destroy(_texPoolL);
                    _texPoolL = new Texture2D(_rt.width, _rt.height, TextureFormat.RGB24, false);
                }
                Texture2D texL = _texPoolL;
                texL.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0, false);
                texL.Apply();
                RenderTexture.active = prev;

                // Right eye (stereo mode).
                if (_camR != null && _rtR != null && RtCreated(_rtR))
                {
                    _camR.Render();
                    prev = RenderTexture.active;
                    RenderTexture.active = _rtR;
                    if (_texPoolR == null || _texPoolR.width != _rtR.width || _texPoolR.height != _rtR.height)
                    {
                        if (_texPoolR != null) Destroy(_texPoolR);
                        _texPoolR = new Texture2D(_rtR.width, _rtR.height, TextureFormat.RGB24, false);
                    }
                    Texture2D texR = _texPoolR;
                    texR.ReadPixels(new Rect(0, 0, _rtR.width, _rtR.height), 0, 0, false);
                    texR.Apply();
                    RenderTexture.active = prev;

                    // Stitch side-by-side: left half = left eye, right half = right eye.
                    int w = texL.width, h = texL.height;
                    if (_texPoolStereo == null || _texPoolStereo.width != w * 2 || _texPoolStereo.height != h)
                    {
                        if (_texPoolStereo != null) Destroy(_texPoolStereo);
                        _texPoolStereo = new Texture2D(w * 2, h, TextureFormat.RGB24, false);
                    }
                    var stereo = _texPoolStereo;
                    var pxL = texL.GetPixels32();
                    var pxR = texR.GetPixels32();
                    var pxOut = new Color32[pxL.Length * 2];
                    for (int y = 0; y < h; y++)
                    {
                        Array.Copy(pxL, y * w, pxOut, y * w * 2, w);
                        Array.Copy(pxR, y * w, pxOut, y * w * 2 + w, w);
                    }
                    stereo.SetPixels32(pxOut);
                    stereo.Apply();
                    byte[] stereoJpg = UnityEngine.ImageConversion.EncodeToJPG(stereo, _cfgImageQuality.Value);
                    return stereoJpg;
                }

                return UnityEngine.ImageConversion.EncodeToJPG(texL, _cfgImageQuality.Value);
            }
            catch (Exception e) { Logger.LogWarning("screenshot: " + e.Message); return null; }
        }
    }
}
