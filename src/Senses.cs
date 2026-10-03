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
                if (useCache) Logger.LogDebug($"perception cache hit (active={active}, throttle={throttleTicks})");
                if (useCache)
                {
                    // Lightweight update: keep cached perception but refresh dynamic fields
                    var cached = _cachedPerception as Dictionary<string, object>;
                    if (cached != null)
                    {
                        cached["tick"] = _tick;
                        cached["yaw"] = F(_yawDeg);
                        cached["facing"] = Compass.FacingText(BodyYaw());
                        // Update needs quickly without full raycast
                        var needs = new
                        {
                            energy = F(_kobold.GetEnergy()) + "/" + F(_kobold.GetMaxEnergy()),
                            horniness = HorninessText(),
                            eggs = F(GetEggVolume(_kobold)) + (IsReadyToLayEgg(_kobold) ? " ready_to_lay" : ""),
                            crouch = F(_crouch),
                            awake = UptimeText(),
                        };
                        cached["needs"] = needs;
                        try { ModuleRegistry.RunPerception(this, cached); } catch (Exception e) { Logger.LogDebug("module perception (cache): " + e.Message); }
                        return cached;
                    }
                }
                var full = BuildPerceptionSafe(includeImage);
                _lastFullPerceptionTick = _tick;
                _cachedPerception = full is Dictionary<string, object> d ? d : null;
                if (!useCache) Logger.LogDebug($"perception full build (active={active})");
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
                                // Name/kind/size/facing are memoized per collider
                                // (GetRayHitInfo) — the render-bounds walk cost a full
                                // child-hierarchy traversal per ray per build otherwise.
                                // The sill check stays per hit: it reads the ray's own
                                // hit point, which varies with aim.
                                var info = GetRayHitInfo(hit.collider);
                                size = info.Size; facingDeg = info.Facing;
                                if (info.HasRender) wpos = info.Center;
                                name = info.Name; kind = info.Kind;
                                if (name.Length == 0)
                                {
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
            // Facing in fixed compass words (N = +Z world): readable even when the
            // degree number itself means nothing to the model.
            result["facing"] = Compass.FacingText(BodyYaw());
            var nearby = DescribeNearby();
            result["nearby"] = nearby;
            bool radarOn = _cfgRadarEnabled != null && _cfgRadarEnabled.Value;
            if (radarOn) result["radar"] = BuildSonarMap(nearby, _radarFilter);
            if (radarOn && _radarFilter != null && _radarFilter.Count > 0)
                result["radar_filter"] = string.Join(",", new List<string>(_radarFilter).ToArray());
            // What's in OUR hands (HoldSense): the only window the model has onto its
            // own grip — items float ~1m ahead on a spring; grabbing a person matters.
            var held = HeldScan();
            if (held.Count > 0)
            {
                var heldList = new List<object>();
                foreach (var h in held)
                    heldList.Add(new { n = h.Name, kind = h.Kind, d = F(h.Dist), x = F(h.Pos.x), y = F(h.Pos.y), z = F(h.Pos.z),
                        what = h.Victim != null ? (h.IsPlayerVictim ? "THE PLAYER's body — limp while held; let go when they ask" : "a PERSON (" + h.Name + ") — drop when they ask; never throw unasked") : null });
                result["holding"] = heldList;
            }
            // Watched, not memorized: what your player is visibly doing right now.
            // Other players know objectives by watching — this gives the AI the same
            // capacity without a single fact or memory.
            try { var pa = BuildPlayerActivity(); if (pa != null) result["player_activity"] = pa; } catch (Exception) { }
            // The game's real quest chain: the objective letter scroll (DragonMail).
            try { var q = QuestSense.Perceive(); if (q != null) result["quest"] = q; } catch (Exception) { }
            result["blocked"] = _blockedInfo;
            result["walls"] = _bumpInfo;
            result["ground"] = ground;
            result["clearance"] = clearance;
            result["vis_go"] = _visionSteer != null ? _visionSteer.deg.ToString("0") + "deg (" + _visionSteer.reason + ")" : null;
            float eggVol = 0f; bool eggReady = false;
            try { eggVol = GetEggVolume(_kobold); eggReady = IsReadyToLayEgg(_kobold); } catch (Exception) { }
            result["needs"] = new
            {
                energy = F(_kobold.GetEnergy()) + "/" + F(_kobold.GetMaxEnergy()),
                horniness = HorninessText(),
                // Egg need is ONLY real when the belly is full (game rule: nest needs
                // egg > 5ml). With an empty belly the nest can't work — the model used
                // to chase nests with 0ml, so state it explicitly both ways.
                eggs = eggReady
                    ? F(eggVol) + "ml READY_TO_LAY — belly full, find a nest now"
                    : F(eggVol) + "ml belly not full — a nest WON'T work yet; seek a nest ONLY when it says READY_TO_LAY",
                crouch = F(_crouch),
                // The game ships no day/night clock (verified in the game source) —
                // "how long has this body been awake in this session" is the honest
                // stand-in a model can reason with ("I've been here 2h, time to nap").
                awake = UptimeText(),
            };
            // Shared full-scene walk map state (all agents): building %, or ready.
            try { result["map"] = WorldMap.StatusText(); } catch (Exception) { }
            // Follow state (if active the body stays near the host player). Breaking
            // free is a real, teachable action — models got stuck in follow feeling
            // unable to move their own way.
            result["follow"] = new
            {
                on = _followMode,
                player_d = (_followMode && _followDist >= 0f) ? F(_followDist) : (object)null,
                note = _followMode ? "you're attached to the player — follow(on:false) frees your own movement (follow(on:true) re-attaches)" : null,
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
                // Occupancy of the set you're animating in: your slot + the partner's.
                try
                {
                    if (_charAnimator != null && _charAnimator.TryGetAnimationStationSet(out var curSet))
                    {
                        string slots = StationSlotsForSet(curSet, true);
                        if (slots != null) result["station_slots"] = slots;
                    }
                }
                catch (Exception) { }
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
            // Other players in the room: chat name + body mesh + where they are, so the
            // model can recognize and address them (and go_to their name) without
            // mistaking them for the host player or for wild kobolds.
            var people = DescribePeople();
            if (people.Count > 0) result["people"] = people;
            // Scene-wide station map (beds/nests/play stations/doors...) even when
            // rays can't see them — the model's "map" of stations in this world.
            var stations = DescribeStations();
            if (stations.Count > 0) result["stations"] = stations;
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

            // Module perception hooks (BodyControl, Farming, Identity, ...) add their
            // keys here — modules never edit this file.
            try { ModuleRegistry.RunPerception(this, result); } catch (Exception e) { Logger.LogDebug("module perception: " + e.Message); }

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

        // ------------------------------------------------------------------
        // sonar — the north-up ASCII map (never rotates with facing)
        // ------------------------------------------------------------------
        // The old radar plotted the camera-FOV ray fan, so the drawn cone rotated
        // and went blank as the body turned — the "sonar slides around" complaint.
        // This version: a fixed 16-ray COMPASS sweep (absolute world bearings) plus
        // the exact 'nearby' entries, both plotted into a grid whose top edge is
        // always north (+Z). The map never rotates; only the dots slide as the
        // body moves, and the model's facing is the arrow beside @.
        private sealed class CompassSweep
        {
            public float Time, Range;
            public Vector3 Origin;
            public float[] Dist = new float[16];
            public string[] Kind = new string[16];
            public string[] Name = new string[16];
        }

        private CompassSweep _compassSweep;

        // Raycast the 16 compass directions (N, NNE, ... NNW) from chest height.
        // Memoized 0.5s so perception + an on-demand console `sonar` cost one sweep.
        // Main thread (Physics), like the rest of perception.
        private CompassSweep RunCompassSweep()
        {
            if (!IsAlive(_kobold) || !IsAlive(_head)) return null;
            if (_compassSweep != null && Time.unscaledTime - _compassSweep.Time < 0.5f)
                return _compassSweep;
            var sw = new CompassSweep
            {
                Time = Time.unscaledTime,
                Range = Mathf.Min(_cfgRayRange != null ? _cfgRayRange.Value : 25f, 15f),
                Origin = _head.position + Vector3.up * -0.15f, // chest height
            };
            for (int i = 0; i < 16; i++)
            {
                float ang = i * 22.5f; // absolute world bearing, 0 = +Z = north
                sw.Dist[i] = sw.Range;
                sw.Kind[i] = "open";
                var dir = Quaternion.Euler(0, ang, 0) * Vector3.forward;
                RaycastHit hit;
                if (!Physics.Raycast(sw.Origin, dir, out hit, sw.Range, ~0, QueryTriggerInteraction.Ignore)
                    || IsOwnCollider(hit.collider))
                    continue;
                sw.Dist[i] = hit.distance;
                try
                {
                    var info = GetRayHitInfo(hit.collider);
                    string nm = info.Name ?? "";
                    string ik = info.Kind ?? "w";
                    if (ik == "w" && nm.Length == 0 && ProbeSurfaceTop(hit.point) < 1.35f)
                        ik = "s"; // low sill — window sill / counter edge
                    switch (ik)
                    {
                        case "p": sw.Kind[i] = "player"; break;
                        case "k": sw.Kind[i] = "kobold"; break;
                        case "u": sw.Kind[i] = "usable"; break;
                        case "s": sw.Kind[i] = "sill"; break;
                        case "V": sw.Kind[i] = "window"; break;
                        default: sw.Kind[i] = "wall"; break;
                    }
                    sw.Name[i] = nm;
                }
                catch (Exception) { sw.Kind[i] = "wall"; }
            }
            _compassSweep = sw;
            return sw;
        }

        private static char SonarGlyph(string kind)
        {
            switch (kind)
            {
                case "wall": return 'W';
                case "sill": return 'S';
                case "usable": return 'U';
                case "player": return 'P';
                case "kobold": return 'K';
                case "window": return 'V';
                default: return '?';
            }
        }

        // Build the sonar: north-up fixed grid + facing arrow + inline legend
        // (the "table of contents" for the map). The filter is a set of allowed
        // glyph letters (W/S/U/K/P/V) — null/empty shows everything.
        private string BuildSonarMap(List<object> nearbyList, HashSet<string> filter)
        {
            if (!IsAlive(_kobold)) return null;
            CompassSweep sw = RunCompassSweep();

            int S = _cfgRadarSize != null && _cfgRadarSize.Value > 0 ? _cfgRadarSize.Value : Consts.DefaultRadarSize;
            float scale = _cfgRadarScale != null && _cfgRadarScale.Value > 0f ? _cfgRadarScale.Value : Consts.DefaultRadarScale;
            char[,] grid = new char[S * 2 + 1, S * 2 + 1];
            for (int r = 0; r <= S * 2; r++)
                for (int c = 0; c <= S * 2; c++)
                    grid[r, c] = '.';
            Vector3 me = _kobold.transform.position;

            // Plot: world offset (meters from body) → cell. Row decreases toward
            // +Z (north = up), column increases toward +X (east = right).
            if (sw != null)
            {
                for (int i = 0; i < 16; i++)
                {
                    if (sw.Kind[i] == "open") continue;
                    float angRad = i * 22.5f * (float)(Math.PI / 180.0);
                    Vector3 at = me + new Vector3(Mathf.Sin(angRad), 0f, Mathf.Cos(angRad)) * sw.Dist[i];
                    SonarPlot(grid, S, scale, me, at, SonarGlyph(sw.Kind[i]), filter);
                }
            }
            // Exact neighbor positions (from the 'nearby' list) beat the sweep's
            // surface points — draw them after so they win the cell.
            if (nearbyList != null)
            {
                foreach (var obj in nearbyList)
                {
                    var d = obj as Dictionary<string, object>;
                    if (d == null) continue;
                    float x, z;
                    if (!SonarTryF(d, "x", out x) || !SonarTryF(d, "z", out z)) continue;
                    string lbl = "";
                    try { if (d.ContainsKey("k") && d["k"] != null) lbl = d["k"].ToString().ToLowerInvariant(); } catch (Exception) { }
                    char ch = lbl.Contains("player") ? 'P' : lbl.Contains("kobold") ? 'K' : 'U';
                    SonarPlot(grid, S, scale, me, new Vector3(x, me.y, z), ch, filter);
                }
            }

            // Self marker + facing arrow (first free cell along the quantized facing;
            // the exact angle is in the header line).
            grid[S, S] = '@';
            float yaw = BodyYaw();
            int fdx, fdz;
            Compass.OffsetOf(Compass.IndexOf(yaw), out fdx, out fdz);
            char arrow = Compass.GlyphOf(yaw);
            for (int step = 1; step <= 2; step++)
            {
                int row = S - fdz * step, col = S + fdx * step;
                if (row < 0 || row > S * 2 || col < 0 || col > S * 2) break;
                if (grid[row, col] != '.') continue;
                grid[row, col] = arrow;
                break;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("sonar: NORTH-UP, FIXED (never rotates) — top=N(+Z) right=E(+X); cell=" + F(scale) + "m; dots slide only when you move");
            if (filter != null && filter.Count > 0)
                sb.Append("; filter:" + string.Join("", new List<string>(filter).ToArray()));
            sb.Append('\n');
            sb.Append("you=@ facing ").Append(Compass.FacingText(BodyYaw()))
              .Append(" (arrow beside @)").Append(" | @=you ^=facing W=wall S=sill U=usable K=kobold P=player V=window .=clear")
              .Append('\n');
            for (int r = 0; r <= S * 2; r++)
            {
                for (int c = 0; c <= S * 2; c++)
                    sb.Append(grid[r, c]);
                if (r < S * 2) sb.AppendLine();
            }
            return sb.ToString();
        }

        // One dot onto the sonar grid; bounds-checked, respects the filter.
        private static void SonarPlot(char[,] grid, int S, float scale, Vector3 me, Vector3? at, char ch, HashSet<string> filter)
        {
            if (filter != null && filter.Count > 0 && !filter.Contains(ch.ToString())) return;
            if (!at.HasValue) return;
            int col = S + Mathf.RoundToInt((at.Value.x - me.x) / scale);
            int row = S - Mathf.RoundToInt((at.Value.z - me.z) / scale);
            if (row < 0 || row > S * 2 || col < 0 || col > S * 2) return;
            if (row == S && col == S) return;
            grid[row, col] = ch;
        }

        // Float-from-dict helper (nearby entries store F()-formatted strings).
        private static bool SonarTryF(Dictionary<string, object> d, string key, out float v)
        {
            v = 0f;
            object o;
            if (d == null || !d.TryGetValue(key, out o) || o == null) return false;
            return float.TryParse(o.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v);
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

        // The chat name of the PLAYER who owns this kobold body, or null for wild/AI
        // bodies and our own NPC bodies. PUN2 keeps the owning PhotonPlayer on the
        // body's PhotonView: the host player's body is owned by LocalPlayer, and a
        // remote player's body stays owned by THEIR PhotonPlayer — that's how we map
        // "Yipper" (chat) to the "AbsolB" body (mesh) without confusing the host.
        internal static string KoboldOwnerNick(Kobold k)
        {
            try
            {
                if (k == null) return null;
                if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold == k)
                    return PlayerChatName();
                var pv = k.GetComponent<PhotonView>();
                if (pv == null || pv.Owner == null) return null;
                try
                {
                    var local = PhotonNetwork.LocalPlayer;
                    if (local != null && pv.Owner.ActorNumber == local.ActorNumber) return null; // NPC/host body
                }
                catch (Exception) { }
                string nick = pv.Owner.NickName;
                if (string.IsNullOrEmpty(nick)) return null;
                nick = TextUtil.AsciiSafe(nick).Trim();
                return nick.Length > 0 ? nick : null;
            }
            catch (Exception) { return null; }
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

        // ------------------------------------------------------------------
        // Ray-hit cache. Perception rays need name/kind/size/facing of whatever
        // they struck; resolving that costs GetComponentInChildren<Renderer> (a
        // child-hierarchy walk) plus two GetComponentInParent hops PER RAY PER
        // BUILD. Memoize by collider instance id with a short TTL so entity
        // changes (a kobold becoming player-possessed, a machine being named)
        // are re-derived rather than frozen forever.
        // ------------------------------------------------------------------
        private sealed class RayHitInfo
        {
            public string Name = "";
            public string Kind = "w";
            public Vector3 Size;
            public Vector3 Center;
            public float Facing;
            public bool HasRender;
            public float Born;
        }
        private readonly Dictionary<int, RayHitInfo> _rayHitCache = new Dictionary<int, RayHitInfo>(256);
        private const float RayHitInfoTtl = 5f;
        private const int RayHitInfoCacheCap = 512;

        // Main-thread only (perception builds already run there).
        private RayHitInfo GetRayHitInfo(Collider c)
        {
            int id = c.GetInstanceID();
            RayHitInfo info;
            if (_rayHitCache.TryGetValue(id, out info) && Time.unscaledTime - info.Born <= RayHitInfoTtl)
                return info;

            info = new RayHitInfo { Born = Time.unscaledTime };
            try
            {
                var kb = c.GetComponentInParent<Kobold>();
                var usable = c.GetComponent<GenericUsable>() ?? c.GetComponentInParent<GenericUsable>();
                if (kb != null) { info.Kind = IsPlayerKobold(kb) ? "p" : "k"; info.Name = kb.name; }
                else if (usable != null) { info.Kind = "u"; info.Name = usable.name; }
                else
                {
                    // Window/glass fixtures: named geometry, so classify by name and
                    // flag it — the model then knows 'V' can't be walked through the
                    // way a doorway can (this is what made "the one by the window"
                    // impossible to match before).
                    string fw = FixtureWordOf(c);
                    if (fw != null && IsWindowWord(fw)) { info.Kind = "V"; info.Name = fw; }
                }
                var rend = c.GetComponentInChildren<Renderer>();
                if (rend != null) { info.Size = rend.bounds.size; info.Center = rend.bounds.center; info.HasRender = true; }
                info.Facing = c.transform.eulerAngles.y;
            }
            catch (Exception) { }
            if (_rayHitCache.Count >= RayHitInfoCacheCap) _rayHitCache.Clear();
            _rayHitCache[id] = info;
            return info;
        }

        // Drop all memoized ray-hit state (world reload, body loss).
        internal void ClearRayHitCache() { _rayHitCache.Clear(); }

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
            float ang = Mathf.Atan2(to.x, to.z) * Consts.Rad2Deg;
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
            float ang = Mathf.Atan2(to.x, to.z) * Consts.Rad2Deg;
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
            if (s.Contains("breeding") || s.Contains("breed") || s.Contains("threeway") || s.Contains("threesome")
                || s.Contains("mount") || s.Contains("ride") || s.Contains("actionstation")
                || s.Contains("play") || s.Contains("sex") || s.Contains("erotic")
                || s.Contains("rockstation")) return "play";
            if (s.Contains("bed") || s.Contains("sleep") || s.Contains("cot")
                || s.Contains("mattress") || s.Contains("nap") || s.Contains("rest")) return "bed";
            if (s.Contains("toilet") || s.Contains("potty") || s.Contains("bathroom")) return "toilet";
            if (s.Contains("tub") || s.Contains("bath") || s.Contains("shower")) return "bath";
            // The objective LETTER BOX — explicitly distinct from the sell machine
            // (MailMachine): the letterbox is where DragonMail letters arrive.
            if (s.Contains("mailbox") || s.Contains("letterbox") || s.Contains("postbox")) return "mailbox";
            // "ovi" catches OvipositionSpot/OviSpot (egg-laying station).
            if (s.Contains("laying") || s.Contains("ovip") || s.Contains("ovi") || s.Contains("nest") || s.Contains("egg")) return "nest";
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
                case "play": return "pleasure station — play/sex ONLY, never for resting";
                case "bed": return "rest/sleep ONLY — NOT a play station";
                case "nest": return "egg laying (only works when belly is full: egg > 5ml)";
                case "machine": return "mounted play/farming";
                case "toilet": return "relief";
                case "bath": return "clean";
                case "mailbox": return "the objective LETTERBOX — interact it to get mail/objective letters (safe; NOT the sell machine)";
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
                    else if (k != null && LLMNPCPlugin.IsClaimedByAnyLLM(k.GetInstanceID()))
                    {
                        // A sibling agent — name it, so the model can tell fellow
                        // NPC minds from wild kobolds and address them in chat.
                        string agentName = AgentNameForBody(k.GetInstanceID());
                        if (!string.IsNullOrEmpty(agentName) && !string.Equals(agentName, MyName(), StringComparison.OrdinalIgnoreCase))
                        { label = "kobold-agent"; nm = agentName; }
                    }
                    string hrel = d.y > 0.5f ? "above" : d.y < -0.5f ? "below" : "level";

                    // Bounds + world position + facing so the model can plan around it
                    // (a chair you can slide past vs. a cabinet you route around).
                    // Memoized per collider (GetRayHitInfo) — same cache as the rays.
                    Vector3 bsize = Vector3.zero;
                    Vector3 bpos = c.transform.position;
                    float bfacing = 0f;
                    try
                    {
                        var rhi = GetRayHitInfo(c);
                        bsize = rhi.Size;
                        if (rhi.HasRender) bpos = rhi.Center;
                        bfacing = rhi.Facing;
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
                        // Live occupancy for station machines ("slots 1/2 taken — you ARE
                        // in one") so ':busy' can't be misread as 'no room for me'.
                        try
                        {
                            string slots = StationSlotsFor(u);
                            if (slots != null) info += " | " + slots;
                        }
                        catch (Exception) { }
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
                        entry["who"] = "the HOST player" + (chat != null ? " — chat name: " + chat : "") + " (their avatar; mesh name: " + nm + ")";
                    }
                    else if (k != null)
                    {
                        string owner = KoboldOwnerNick(k);
                        if (owner != null)
                            entry["who"] = "another PLAYER — chat name: " + owner + " (their avatar; mesh name: " + nm + ") — address them as " + owner;
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

        // Other players in the room (host + remote), each with their chat name, the
        // body/mesh they wear, and their distance/bearing. Distinct from 'player'
        // (host only) so the model never merges them.
        private List<object> DescribePeople()
        {
            var list = new List<object>();
            if (!IsAlive(_kobold)) return list;
            try
            {
                string hostName = null;
                try { if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null) hostName = pp.kobold.name; } catch (Exception) { }
                foreach (var k in SceneCache.Find<Kobold>(2f))
                {
                    if (k == null || k == _kobold) continue;
                    string nick = KoboldOwnerNick(k);
                    if (string.IsNullOrEmpty(nick))
                    {
                        // A fellow AGENT (one of this plugin's NPC bodies) is visible by
                        // its chat name — siblings can address each other in chat.
                        int kId = k.GetInstanceID();
                        if (!LLMNPCPlugin.IsClaimedByAnyLLM(kId)) continue; // wild/AI body
                        string agentName = AgentNameForBody(kId);
                        if (string.IsNullOrEmpty(agentName) || string.Equals(agentName, MyName(), StringComparison.OrdinalIgnoreCase)) continue;
                        nick = agentName;
                        Vector3 da = k.transform.position - _kobold.transform.position;
                        list.Add(new Dictionary<string, object>
                        {
                            ["name"] = nick,
                            ["body"] = CleanName(k.name),
                            ["d"] = F(da.magnitude),
                            ["dir"] = RelBearing(da),
                            ["dir_deg"] = F(RelBearingDeg(da)),
                            ["who"] = "another agent kobold (an LLM NPC) — a person like you; address them with: their name + a comma",
                        });
                        if (list.Count >= 8) break;
                        continue;
                    }
                    Vector3 d = k.transform.position - _kobold.transform.position;
                    bool isHost = hostName != null && string.Equals(k.name, hostName, StringComparison.Ordinal);
                    var entry = new Dictionary<string, object>
                    {
                        ["name"] = nick,
                        ["body"] = CleanName(k.name),
                        ["d"] = F(d.magnitude),
                        ["dir"] = RelBearing(d),
                        ["dir_deg"] = F(RelBearingDeg(d)),
                        ["who"] = isHost ? "the HOST player (your player)" : "another player in the room (their avatar; mesh " + CleanName(k.name) + ")",
                    };
                    list.Add(entry);
                    if (list.Count >= 8) break;
                }
            }
            catch (Exception e) { Logger.LogWarning("people: " + e.Message); }
            return list;
        }

        // ------------------------------------------------------------------
        // landmark hints — what stands near a station, in words ("window, mailbox")
        // ------------------------------------------------------------------
        // The player says "the one by the window" and the NPC needs machine-readable
        // clues to match it. For each station we scan a small radius for named
        // fixtures/neighbors and remember the closest distinct words, memoized per
        // station object (machines don't move — the scan is cheap after the first).
        private static readonly string[] FixtureKeywords =
        {
            "window", "glass", "pane", "mirror", "mailbox", "mail", "fridge", "refrigerator",
            "sink", "shelf", "rack", "bench", "counter", "table", "stair", "lamp",
            "rug", "carpet", "fence", "gate", "trailer", "shed", "silo", "barn",
            "poster", "screen", "crate", "barrel", "sign", "vent", "pillar", "tree",
            "stove", "toilet", "tub",
        };
        private static readonly Dictionary<string, string> FixtureCanonical = new Dictionary<string, string>
        {
            { "glass", "window" }, { "pane", "window" }, { "mail", "mailbox" },
            { "refrigerator", "fridge" }, { "carpet", "rug" },
        };
        // Words that never name a real landmark (Unity boilerplate).
        private static readonly HashSet<string> FixtureJunk = new HashSet<string>(StringComparer.Ordinal)
        {
            "collider", "mesh", "gameobject", "plane", "cube", "sphere", "cylinder", "capsule",
            "quad", "object", "trigger", "empty", "bone", "hitbox", "collision", "box",
            "point", "light", "camera", "canvas", "terrain", "water", "statue", "model",
            "geometry", "collision", "parent", "root", "child", "group",
        };

        // First fixture-ish keyword found in an object name ("SM_Window_01" →
        // "window"), canonicalized; null when the name carries no landmark word.
        internal static string FixtureWordFor(string anyName)
        {
            string s = CleanName(anyName ?? "").ToLowerInvariant();
            if (s.Length < 3) return null;
            foreach (var k in FixtureKeywords)
            {
                if (!s.Contains(k)) continue;
                string w;
                return FixtureCanonical.TryGetValue(k, out w) ? w : k;
            }
            return null;
        }

        // Nearest fixture keyword across the collider's parent chain (windows and
        // furniture live on parent nodes, colliders on leaves).
        internal static string FixtureWordOf(Collider c)
        {
            try
            {
                for (var t = c != null ? c.transform : null; t != null; t = t.parent)
                {
                    string w = FixtureWordFor(t.gameObject.name);
                    if (w != null) return w;
                }
            }
            catch (Exception) { }
            return null;
        }

        // Letters-only leftover of a name ("Blender-2" → "blender"); null when empty.
        internal static string NameWord(string name)
        {
            string s = CleanName(name ?? "").ToLowerInvariant();
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s) if (char.IsLetter(ch)) sb.Append(ch);
            string w = sb.ToString();
            if (w.Length < 3 || FixtureJunk.Contains(w)) return null;
            return w;
        }

        // The window-family fixture words that classify as a 'V' (window/glass)
        // ray kind — solid geometry the model must not try to walk through.
        internal static bool IsWindowWord(string w)
        {
            return w == "window" || w == "glass" || w == "pane" || w == "mirror";
        }

        private struct LandmarkHintMemo { public float SavedAt; public string Hints; }
        private readonly Dictionary<int, LandmarkHintMemo> _landmarkHints = new Dictionary<int, LandmarkHintMemo>(64);
        private readonly Collider[] _hintScanBuf = new Collider[64]; // per-instance: commands run on the LLM thread
        private const float LandmarkHintTtl = 15f;
        private const float LandmarkHintRadius = 4.5f;

        // Scan around a station for the 1-2 closest distinct landmark words.
        // Main thread (Physics); memoized per station object.
        private string LandmarkHintsFor(GenericUsable u)
        {
            try
            {
                if (u == null || u.transform == null) return null;
                int key = u.GetInstanceID();
                LandmarkHintMemo memo;
                if (_landmarkHints.TryGetValue(key, out memo) && Time.unscaledTime - memo.SavedAt < LandmarkHintTtl)
                    return memo.Hints;

                Vector3 pos = u.transform.position;
                int n = Physics.OverlapSphereNonAlloc(pos, LandmarkHintRadius, _hintScanBuf, ~0, QueryTriggerInteraction.Collide);
                var bestDist = new Dictionary<string, float>(StringComparer.Ordinal);
                for (int i = 0; i < n; i++)
                {
                    Collider c = _hintScanBuf[i];
                    if (c == null || IsOwnCollider(c)) continue;
                    Kobold kb = null;
                    try { kb = c.GetComponentInParent<Kobold>(); } catch (Exception) { }
                    if (kb != null) continue; // bodies move — never a stable clue
                    string word = null;
                    try
                    {
                        var ou = c.GetComponent<GenericUsable>() ?? c.GetComponentInParent<GenericUsable>();
                        if (ou != null && ou.transform != null && ou.GetInstanceID() == key) continue; // the station itself
                        if (ou != null)
                        {
                            string cn = CleanName(ou.name);
                            word = FixtureWordFor(cn) ?? (ClassifyUsable(cn) != "usable" ? ClassifyUsable(cn) : NameWord(cn));
                        }
                        else word = FixtureWordOf(c);
                    }
                    catch (Exception) { continue; }
                    if (string.IsNullOrEmpty(word) || word.Length < 3) continue;
                    float d = Vector3.Distance(c.transform.position, pos);
                    float prev;
                    if (!bestDist.TryGetValue(word, out prev) || d < prev) bestDist[word] = d;
                }
                // Closest distinct words first, at most two — hints are for matching
                // a player's description ("the one by the window"), not a census.
                var ordered = new List<string>(bestDist.Keys);
                ordered.Sort((a, b) => bestDist[a].CompareTo(bestDist[b]));
                while (ordered.Count > 2) ordered.RemoveAt(ordered.Count - 1);
                string hints = ordered.Count > 0
                    ? "near " + string.Join(", ", ordered.ToArray())
                    : null;
                if (_landmarkHints.Count > 256) _landmarkHints.Clear();
                _landmarkHints[key] = new LandmarkHintMemo { SavedAt = Time.unscaledTime, Hints = hints };
                return hints;
            }
            catch (Exception) { return null; }
        }

        // Stable short address the model (and the player!) can refer to:
        // "Blender#SW4" = kind + world compass bearing + meters. Bearing is
        // world-fixed (north = +Z): it does NOT rotate when the body turns.
        internal static string StationAddr(string name, Vector3 from, Vector3 to)
        {
            Vector3 d = to - from; d.y = 0;
            float m = d.magnitude;
            string word = NameWord(name);
            if (word == null) word = "station";
            if (m < 0.5f) return word + "#here";
            return word + "#" + Compass.NameOfOffset(d.x, d.z) + Math.Max(1, (int)Math.Round(m));
        }

        // Every station/usable in the scene, classified with purpose and distance —
        // the model's station map. Cached ~5s (FindObjectsOfType is a scene-wide find)
        // and shared by all instances. Each entry also gets: a stable numeric 'id'
        // (go_to/interact target), an addr like "Blender#SW4" (compass + distance —
        // the ID Ornith asked for), a world-fixed compass 'bearing', landmark 'hint'
        // ("near window, mailbox"), and per-kind closest_you/closest_player flags so
        // "which station do we use?" has a decisive answer.
        private List<object> DescribeStations()
        {
            var list = new List<object>();
            if (!IsAlive(_kobold)) return list;
            try
            {
                Vector3 pos = _kobold.transform.position;
                Vector3 playerPos = Vector3.zero;
                bool hasPlayer = false;
                try
                {
                    if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null && IsAlive(pp.kobold))
                    {
                        playerPos = pp.kobold.transform.position;
                        hasPlayer = true;
                    }
                }
                catch (Exception) { }

                // First pass: gather every station with its data.
                var stations = new List<System.Tuple<float, string, bool, Vector3, float, GenericUsable>>(); // dist, nm, canUse, pos, playerDist, u
                foreach (var u in StationList())
                {
                    if (u == null || u.transform == null) continue;
                    string nm = CleanName(u.name);
                    if (nm.Length == 0) continue;
                    bool canUse = true;
                    try { canUse = u.CanUse(_kobold); } catch (Exception) { }
                    Vector3 p = u.transform.position;
                    Vector3 d = p - pos;
                    float playerDist = hasPlayer ? Vector3.Distance(p, playerPos) : -1f;
                    stations.Add(System.Tuple.Create(d.magnitude, nm, canUse, p, playerDist, u));
                }

                // Per-kind minimums (self + player) — computed across ALL stations so
                // the flags stay true even when the display cap cuts the list.
                var minSelf = new Dictionary<string, float>(StringComparer.Ordinal);
                var minPlayer = new Dictionary<string, float>(StringComparer.Ordinal);
                foreach (var t in stations)
                {
                    string kind = ClassifyUsable(t.Item2);
                    float ds; if (!minSelf.TryGetValue(kind, out ds) || t.Item1 < ds) minSelf[kind] = t.Item1;
                    if (t.Item5 >= 0f)
                    {
                        float dp; if (!minPlayer.TryGetValue(kind, out dp) || t.Item5 < dp) minPlayer[kind] = t.Item5;
                    }
                }

                stations.Sort((a, b) => a.Item1.CompareTo(b.Item1));
                int cap = 16;
                for (int i = 0; i < stations.Count && i < cap; i++)
                {
                    var t = stations[i];
                    string nm = t.Item2;
                    string kind = ClassifyUsable(nm);
                    Vector3 p = t.Item4;
                    Vector3 d = p - pos;
                    var entry = new Dictionary<string, object>
                    {
                        ["id"] = TargetIdFor(t.Item6.transform, nm),
                        ["n"] = nm,
                        ["addr"] = StationAddr(nm, pos, p),
                        ["i"] = kind + (t.Item3 ? "" : ":busy") + (PurposeFor(kind) != null ? " (" + PurposeFor(kind) + ")" : ""),
                        ["d"] = F(d.magnitude),
                        // 'bearing' = world compass (N/SW...), stable while you turn;
                        // 'dir' = facing-relative, for pointing/walking right now.
                        ["bearing"] = Compass.NameOfOffset(d.x, d.z),
                        ["dir"] = RelBearing(d),
                        ["h"] = d.y > 1f ? "above" : (d.y < -1f ? "below" : "level"),
                        ["x"] = F(p.x),
                        ["y"] = F(p.y),
                        ["z"] = F(p.z),
                    };
                    string hints = LandmarkHintsFor(t.Item6);
                    if (hints != null) entry["hint"] = hints;
                    string slots = StationSlotsFor(t.Item6);
                    if (slots != null) entry["slots"] = slots;
                    float ms; if (minSelf.TryGetValue(kind, out ms) && t.Item1 <= ms + 0.05f) entry["closest_you"] = true;
                    if (t.Item5 >= 0f)
                    {
                        float mp; if (minPlayer.TryGetValue(kind, out mp) && t.Item5 <= mp + 0.05f) entry["closest_player"] = true;
                        entry["player_d"] = F(t.Item5);
                    }
                    list.Add(entry);
                }
            }
            catch (Exception e) { Logger.LogWarning("stations: " + e.Message); }
            return list;
        }

        private static List<GenericUsable> _stationCache;
        private static float _stationCacheTime = -99f;
        private static readonly object _stationLock = new object();

        private static List<GenericUsable> StationList()
        {
            lock (_stationLock)
            {
                if (_stationCache == null || Time.unscaledTime - _stationCacheTime > 5f)
                {
                    _stationCache = new List<GenericUsable>();
                    try
                    {
                        foreach (var u in UnityEngine.Object.FindObjectsOfType<GenericUsable>())
                            if (u != null) _stationCache.Add(u);
                    }
                    catch (Exception) { }
                    _stationCacheTime = Time.unscaledTime;
                }
                return _stationCache;
            }
        }

        // The plain activity wording (shared by the perception object and the console).
        private string PlayerActivityWords(bool ragdolled, bool inStation, string stKind, string stName, float vel, float heading)
        {
            if (ragdolled) return "ragdolled (limp — anyone could pick them up)";
            if (inStation)
            {
                string kind = string.IsNullOrEmpty(stKind) ? "station" : stKind;
                string word = kind == "food" ? "cooking/feeding the blender"
                    : kind == "bed" ? "resting on a bed (only bother them for real reasons)"
                    : kind == "play" ? "at a PLAY station (pleasure)"
                    : kind == "nest" ? "at the nest (laying/attending eggs)"
                    : kind == "toilet" ? "on the toilet"
                    : kind == "bath" ? "bathing"
                    : kind == "seat" ? "sitting down"
                    : kind == "door" ? "passing through a door"
                    : kind == "machine" ? "working a machine"
                    : "using a " + kind + " station";
                return word + (!string.IsNullOrEmpty(stName) ? " (" + stName + ")" : "");
            }
            if (vel > 0.6f) return "walking " + Compass.PreciseNameOf(heading) + " at " + F(vel) + " m/s";
            return "standing still";
        }

        // Live observation of the host player — position relative to you, what they're
        // doing (station/heading), what they hold. This is the player's "current
        // objective" the way OTHER PLAYERS know it: read off their behavior, fresh
        // every moment, never stored as a fact or memory.
        private object BuildPlayerActivity()
        {
            var s = PlayerTrail.Snap;
            if (s == null || !s.Has || Time.unscaledTime - s.SampleTime > 10f) return null;
            Vector3 d = s.Pos - _kobold.transform.position;
            var holds = new List<string>();
            if (s.Held != null)
                foreach (var h in s.Held)
                    holds.Add(h.Name + " (" + h.Kind + ")");
            // "Use the station I'm in" — give the exact station and whether the partner
            // seat is free for THIS body, with an id in the same namespace as 'ls' ids.
            int stationId = -1;
            string seat = null;
            if (s.StationTf != null)
            {
                try
                {
                    stationId = TargetIdFor(s.StationTf, s.StationName);
                    var u = s.StationTf.GetComponentInParent<GenericUsable>();
                    if (u != null)
                        seat = u.CanUse(_kobold) ? "seat free — use id:" + stationId + " to join them" : "seat blocked/taken for your body (other station or wrong side)";
                }
                catch (Exception) { stationId = -1; seat = null; }
            }
            return new
            {
                activity = PlayerActivityWords(s.Ragdolled, s.InStation, s.StationKind, s.StationName, s.Vel, s.Heading),
                d = F(d.magnitude),
                bearing = Compass.NameOfOffset(d.x, d.z),
                station_id = stationId,
                seat = seat,
                holds = holds.Count > 0 ? (object)holds.ToArray() : null,
            };
        }

        // ------------------------------------------------------------------
        // station slots — occupancy of animation-station machines, in words
        // ------------------------------------------------------------------
        // Play/bed stations are IAnimationStationSet machines with per-slot occupants
        // (AnimationStation.info.user). Models kept misreading the station's own
        // ":busy" (their presence making CanUse false) as "the station is full" and
        // leaving — so occupancy comes as explicit text: who's in, you included.
        // Main thread (components).
        private string StationSlotsForSet(IAnimationStationSet set, bool youAreIn)
        {
            if (set == null) return null;
            try
            {
                var stations = set.GetAnimationStations();
                if (stations == null || stations.Count == 0) return null;
                int taken = 0;
                var who = new List<string>();
                foreach (var st in stations)
                {
                    Kobold user = null;
                    try { user = st.info.user; } catch (Exception) { }
                    if (user == null) continue;
                    taken++;
                    if (user == _kobold) continue; // counted in taken; separately said below
                    try { who.Add(IsPlayerKobold(user) ? "the player" : CleanName(user.name)); }
                    catch (Exception) { who.Add("someone"); }
                }
                if (taken == 0 && !youAreIn) return null;
                var sb = new System.Text.StringBuilder();
                sb.Append("slots ").Append(taken).Append('/').Append(stations.Count).Append(" taken");
                if (youAreIn) sb.Append(" — YOU ARE in one (that's why the station itself can read busy; your slot exists)");
                if (who.Count > 0) sb.Append("; occupied by ").Append(string.Join(", ", who.ToArray()));
                if (taken < stations.Count) sb.Append("; ").Append(stations.Count - taken).Append(" slot(s) FREE");
                else sb.Append(" (full for now)");
                return sb.ToString();
            }
            catch (Exception) { return null; }
        }

        // Slot text for a station usable (null when it has no station set).
        private string StationSlotsFor(GenericUsable u)
        {
            try
            {
                if (u == null) return null;
                var set = u.GetComponentInParent<IAnimationStationSet>();
                if (set == null) set = u.GetComponentInChildren<IAnimationStationSet>();
                return StationSlotsForSet(set, false);
            }
            catch (Exception) { return null; }
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
