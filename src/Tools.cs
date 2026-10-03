// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
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
        // sonar tool — radar shows/re-filters the north-up ASCII map
        // ------------------------------------------------------------------
        // radar(filter='U,P') sets the persisted glyph filter (also applied to the
        // perception sonar each turn) and returns the fresh map. Models that misread
        // 'K' marks as figures staring at them — or that only care about stations —
        // can thin the map out themselves.
        private HashSet<string> _radarFilter;

        private static HashSet<string> ParseSonarFilter(string raw, out string err)
        {
            err = null;
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            foreach (var rawTok in raw.ToLowerInvariant().Split(new[] { ',', ' ', ';', '/', '|', '+', '=' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = rawTok.Trim().Trim('.', ':');
                if (t.Length == 0) continue;
                string letter;
                if (t.Length == 1 && "wsuvkp".Contains(t)) letter = t.ToUpperInvariant();
                else if (t == "wall" || t == "walls" || t == "building" || t == "structures") letter = "W";
                else if (t == "sill" || t == "sills" || t == "barrier" || t == "ledges") letter = "S";
                else if (t == "window" || t == "windows" || t == "glass") letter = "V";
                else if (t == "usable" || t == "usables" || t == "station" || t == "stations" || t == "machines" || t == "furniture") letter = "U";
                else if (t == "kobold" || t == "kobolds" || t == "entity" || t == "entities" || t == "npc" || t == "npcs" || t == "creatures") letter = "K";
                else if (t == "player" || t == "players") letter = "P";
                else { err = "unknown filter word '" + t + "'"; return null; }
                set.Add(letter);
            }
            return set.Count > 0 ? set : null;
        }

        private object ToolRadar(JsonObj p)
        {
            string f = p.S("filter", p.S("kind", p.S("show", "")));
            string err;
            HashSet<string> set = ParseSonarFilter(f ?? "", out err);
            if (err != null)
                return new { ok = false, reason = "bad_filter", tried = f ?? "",
                    valid = "W wall, S sill, V window, U usable/station, K kobold, P player — or 'all' for everything", note = err };
            _radarFilter = set;
            string map = null;
            try { map = BuildSonarMap(DescribeNearby(), _radarFilter); }
            catch (Exception e) { Logger.LogWarning("radar tool: " + e.Message); }
            if (map == null) return new { ok = false, reason = "no_body" };
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["filter"] = _radarFilter != null ? string.Join(",", new List<string>(_radarFilter).ToArray()) : "all",
                ["map"] = map,
            };
        }

        // ------------------------------------------------------------------
        // AI feedback — report_issue: the model's bug/complaint channel
        // ------------------------------------------------------------------
        // When tools fail oddly, outputs lie (sonar garbage, phantom ids), or an
        // action just can't be done, the model files a one-line note. With the
        // [Debug] AIReportLog option on it appends to BepInEx/config/
        // kkllmnpc_ai_reports.log — read by the dev between sessions to improve the
        // mod ("tool call was frustrating, do X instead", "radar shows garbage").
        private object ToolReportIssue(JsonObj p)
        {
            string msg = p.S("msg", "");
            if (msg.Length == 0) msg = p.S("text", "");
            if (msg.Length == 0) msg = p.S("say", "");
            if (msg.Length == 0) msg = p.S("message", "");
            if (msg.Length == 0) msg = p.S("issue", "");
            msg = Sanitize(msg ?? "").Trim();
            if (msg.Length == 0)
                return new { ok = false, reason = "empty_report",
                    note = "report_issue(msg='what is broken') — one line: what failed or frustrated you + what you tried" };
            bool filed;
            try { filed = LogAIReport(msg); }
            catch (Exception e) { Logger.LogWarning("ai report: " + e.Message); filed = false; }
            if (filed)
                return new { ok = true, filed = true, note = "your report is written down for the human maintainers — act normally; they read it later" };
            return new { ok = true, filed = false, note = "AI report logging is OFF ([Debug] AIReportLog) — mention the problem to the player in chat instead" };
        }

        // Recent tool calls (oldest→newest) attached to reports so the dev sees what
        // the model did right before it got frustrated.
        private readonly List<string> _toolLog = new List<string>(16);
        private const int ToolLogCap = 12;

        private void RecordToolCall(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            try
            {
                string s = line.Replace('\n', ' ').Trim();
                if (s.Length > 120) s = s.Substring(0, 120);
                if (_toolLog.Count >= ToolLogCap) _toolLog.RemoveAt(0);
                _toolLog.Add(s);
            }
            catch (Exception) { }
        }

        // Brief "k=v,k=v" of an act call's args for the tool log.
        internal string ToolArgsBrief(JsonObj args)
        {
            try
            {
                if (args == null || args.Dict == null) return "";
                var parts = new List<string>();
                foreach (var kv in args.Dict)
                {
                    if (kv.Key == "action" || kv.Key == "thought" || kv.Key == "plan"
                        || kv.Key == "progress" || kv.Key == "why" || kv.Key == "wait") continue;
                    string v = kv.Value == null ? "" : kv.Value.ToString();
                    if (v.Length > 24) v = v.Substring(0, 24) + "…";
                    parts.Add(kv.Key + "=" + v);
                    if (parts.Count >= 4) break;
                }
                return string.Join(",", parts.ToArray());
            }
            catch (Exception) { return ""; }
        }

        private static readonly object _reportFileLock = new object();

        // Where reports go (created on demand); null when even the fallback fails.
        internal static string ReportFilePath()
        {
            try
            {
                string data = Application.dataPath;
                if (!string.IsNullOrEmpty(data))
                    return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(data), "BepInEx", "config", "kkllmnpc_ai_reports.log");
            }
            catch (Exception) { }
            try
            {
                string asm = System.IO.Path.GetDirectoryName(typeof(NPCInstance).Assembly.Location);
                if (!string.IsNullOrEmpty(asm)) return System.IO.Path.Combine(asm, "kkllmnpc_ai_reports.log");
            }
            catch (Exception) { }
            return null;
        }

        // Write one report block. Returns false when the debug option is off (or the
        // write failed); the message still reaches the BepInEx log above.
        internal bool LogAIReport(string msg)
        {
            try
            {
                string who = MyName();
                Logger.LogInfo("[AIReport] " + who + ": " + msg);
                if (_cfgAIReportLog == null || !_cfgAIReportLog.Value) return false;

                var sb = new System.Text.StringBuilder();
                sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
                  .Append("] ").Append(who);
                try { sb.Append(" (body: " + CleanName(_kobold.name) + ")"); } catch (Exception) { }
                try
                {
                    Vector3 pos = _kobold.transform.position;
                    sb.Append("  pos(" + F(pos.x) + ", " + F(pos.y) + ", " + F(pos.z) + ")  facing " + Compass.FacingText(BodyYaw()));
                }
                catch (Exception) { }
                sb.Append("  awake ").Append(UptimeText());
                try { sb.Append("  scene ").Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name); } catch (Exception) { }
                if (IsInAnimationStation()) sb.Append("  in_station ").Append(_stationPurpose ?? "?");
                sb.Append('\n');
                string goal;
                lock (_goalLock) { goal = _goal; }
                if (!string.IsNullOrEmpty(goal)) sb.Append("  goal: ").Append(goal).Append('\n');
                sb.Append("  says: ").Append(msg.Replace("\n", " ")).Append('\n');
                for (int i = Math.Max(0, _toolLog.Count - 10); i < _toolLog.Count; i++)
                    sb.Append("   - ").Append(_toolLog[i]).Append('\n');
                sb.Append('\n');

                lock (_reportFileLock)
                {
                    string path = ReportFilePath();
                    if (path == null) return false;
                    System.IO.File.AppendAllText(path, sb.ToString());
                }
                return true;
            }
            catch (Exception e)
            {
                Logger.LogWarning("ai report write: " + e.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // tool commands (invoked via the LLM tool-call loop)
        // ------------------------------------------------------------------
        // *curls up by the fire* — a non-dialog action line. Same three channels as
        // say (bubble + real chat window + local echo) but wrapped in asterisks so
        // the chat reads as body language, not speech. Repeat suppression still
        // applies through ToolSay.
        internal object ToolEmote(string text)
        {
            string t = (text ?? "").Trim();
            if (t.Length == 0) return new { ok = false, reason = "empty" };
            // Accept already-wrapped emotes ("*scampers*") without double-wrapping.
            if (t.StartsWith("*") && t.EndsWith("*") && t.Length > 2)
                t = t.Substring(1, t.Length - 2).Trim();
            if (t.Length > 240) t = t.Substring(0, 240);
            try { return ToolSay(new TextArgs("*" + t + "*")); } catch (Exception e)
            { return new { ok = false, reason = "emote_failed", msg = e.Message }; }
        }
        // One movement primitive besides go_to: a bounded burst in the body's current
        // heading — nudges, squeezes, strafes, and the jump (which also exits a
        // station, the way the old standalone jump tool did). Long travel is go_to.
        private object ToolWalk(JsonObj p)
        {
            float speed = p.F("speed", 1f);
            float strafe = p.F("strafe", 0f);         // right=+/left=-, -1..1
            bool jump = p.B("jump", false);
            float turn = p.F("turn_deg", 0f);
            // walk_ray leftovers: ray_deg is a camera-relative heading delta.
            float rd = p.F("ray_deg", float.NaN);
            if (!float.IsNaN(rd)) turn += Mathf.Clamp(rd, -180f, 180f);
            // Default a 2s burst so a forgotten duration can't make it walk forever.
            float dur = Mathf.Clamp(p.F("duration", 2f), 0.1f, 8f);
            bool run = p.B("run", false); // default: walk
            // Jump out of a station first — jumping is how players get out.
            if (jump && IsInAnimationStation())
            {
                try { ToolExitStation(); } catch (Exception) { }
            }
            if (_photonView != null && !_photonView.IsMine && PhotonNetwork.InRoom)
            {
                try
                {
                    string safeNick = _photonView.Owner?.NickName ?? "?";
                    safeNick = TextUtil.AsciiSafe(safeNick);
                    Logger.LogWarning($"walk: not Photon owner (owner={safeNick}) — movement won't apply");
                }
                catch (Exception) { Logger.LogWarning("walk: not Photon owner — movement won't apply"); }
            }
            SetMove(speed, strafe, jump, 0f, dur, run);
            if (Mathf.Abs(turn) > 0.001f)
                lock (_stateLock) { _yawOffsetDeg += turn; }
            return new { ok = true, speed, jump, turn_deg = turn, duration = dur, run };
        }

        // FOLLOW MODE: the body stays within a band of the host player (steering is
        // driven in FixedUpdateSafe) while the model keeps thinking/talking/acting.
        // follow(on:false) releases it to free movement.
        private object ToolFollow(JsonObj p)
        {
            bool on = p.Has("on") ? p.B("on", true) : p.B("follow", true);
            _followMode = on;
            if (on)
            {
                // Following means leaving whatever station we're locked in.
                if (IsInAnimationStation())
                {
                    try { ToolExitStation(); } catch (Exception) { }
                }
                _followLastPath = -99f;
            }
            else
            {
                StopMove();
                lock (_stateLock) { _path = null; _pathIdx = 0; _pathGoalSet = false; }
            }
            Logger.LogInfo("[" + MyName() + "] follow mode " + (on ? "ON (staying near the player)" : "off"));
            return new { ok = true, follow = on,
                note = on ? "you now stay near the player while you keep acting; call follow(on:false) when done" : "follow off — free movement again" };
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
        // Jumps along a player-trail path: _trailJumps[i] = "hop to reach post i" —
        // replayed by the movement loop as a forward-jump burst (parkour copying).
        private List<bool> _trailJumps;
        private float _lastTrailJump;

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
            // With the shared full-scene map ready (and both points in bounds) the
            // whole scene is routable — stairs, ramps, other floors included.
            bool mapCovers = false;
            try { mapCovers = WorldMap.Ready && WorldMap.InBounds(_kobold.transform.position) && WorldMap.InBounds(target); } catch (Exception) { }
            float maxReach = mapCovers ? 99999f : (_cfgPathSpan != null ? _cfgPathSpan.Value : 20f) * 2f + 30f;
            bool trailRoute = false; // path came from the player's demonstrated trail
            List<Vector3> preTrailPts = null;
            List<bool> preTrailJumps = null;
            if (fullDist > maxReach + at)
            {
                // Beyond A*'s reach — but a route YOUR PLAYER demonstrated by walking
                // it is proof the way exists; try the trail before refusing.
                try
                {
                    if (PlayerTrail.TryPath(_kobold.transform.position, target, out preTrailPts, out preTrailJumps))
                    {
                        preTrailJumps.Add(false);
                        trailRoute = true;
                    }
                }
                catch (Exception e) { Logger.LogWarning("trail reach: " + e.Message); }
            }
            if (fullDist > maxReach + at && !trailRoute)
                return new { ok = false, reason = "too_far", name = _navTargetName ?? "position", id = hasId ? p.S("id") : (object)null, dist = F(fullDist), note = "in range of ~" + F(maxReach) + "m only — pick something from 'nearby'/'survey' or ask the player to take you" };

            // Nest gate: a nest physically can't be used until the belly is full
            // (OvipositionSpot rule: egg > 5ml). The model used to camp nests with
            // an empty belly — hard-refuse the travel itself, not just the interact.
            string destCls = ClassifyUsable(name).ToLowerInvariant();
            if (destCls == "nest")
            {
                bool ready = true; float vol = 0f;
                try { vol = GetEggVolume(_kobold); ready = IsReadyToLayEgg(_kobold); } catch (Exception) { }
                if (!ready)
                    return new { ok = false, reason = "belly_not_full", name = _navTargetName, id = hasId ? p.S("id") : (object)null, dist = F(fullDist), eggs = F(vol), note = "a nest WON'T work with a belly of " + F(vol) + "ml (needs >5ml). Do NOT seek a nest until needs.eggs says READY_TO_LAY — play, explore or keep company instead" };
            }
            float stopAt = Mathf.Max(0f, fullDist - at);
            float yaw = Mathf.Atan2(to.x, to.z) * Consts.Rad2Deg;
            lock (_stateLock) { _yawDeg = yaw; }
            if (fullDist > at + 0.1f)
            {
                var arrive = _kobold.transform.position + to.normalized * stopAt;
                List<Vector3> path = null;
                if (stopAt > 1.5f)
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
                    // 1) Shared full-scene map: routes the WHOLE scene (stairs, ramps,
                    //    stacked floors, any distance in bounds) — the fix for stations
                    //    that are "too far" for the local window grid.
                    if (WorldMap.Ready)
                    {
                        try { path = (List<Vector3>)RunOnMainThread(() => WorldMap.FindPathSmoothed(_kobold.transform.position, arrive), 20000); }
                        catch (Exception e) { Logger.LogWarning("world-map path: " + e.Message); }
                    }
                    // 2) Local window A* (map building, or points outside map bounds).
                    if (path == null && _cfgPathEnabled.Value)
                    {
                        try { path = (List<Vector3>)RunOnMainThread(() => FindPath(_kobold.transform.position, arrive), 12000); }
                        catch (Exception e) { Logger.LogWarning("pathfind: " + e.Message); }
                    }
                    // 3) A route YOUR PLAYER demonstrated (their actual walked path —
                    //    samples + jump flags). When A* has nothing (too_far, parkour,
                    //    a gap, a room the bake never saw open), somebody walking it
                    //    IS proof the route exists: copy their trail. (preTrail = the
                    //    attempt that already passed the too_far gate.)
                    if (path == null && trailRoute && preTrailPts != null)
                    {
                        preTrailPts.Add(arrive);
                        path = preTrailPts;
                        lock (_stateLock) { _trailJumps = preTrailJumps; }
                    }
                    else if (path == null)
                    {
                        try
                        {
                            List<Vector3> tpts;
                            List<bool> tjumps;
                            if (PlayerTrail.TryPath(_kobold.transform.position, arrive, out tpts, out tjumps))
                            {
                                tpts.Add(arrive);
                                tjumps.Add(false);
                                path = tpts;
                                lock (_stateLock) { _trailJumps = tjumps; }
                                trailRoute = true;
                            }
                        }
                        catch (Exception e) { Logger.LogWarning("trail path: " + e.Message); }
                    }
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
                        if (!trailRoute) _trailJumps = null;
                    }
                    float pathLen = 0f;
                    for (int i = 1; i < path.Count; i++) pathLen += Vector3.Distance(path[i - 1], path[i]);
                    SetMove(1f, 0f, false, 0f, Mathf.Clamp(pathLen / 1.5f, 1f, 30f), p.B("run", false));
                    string dnote = _pathDoorBlocked ? " (a CLOSED door is on the way — the body will open it when it gets there, or use interact on it)" : "";
                    string kind = trailRoute ? "player-trail route (their walked path — flagged posts are hopped)"
                                             : "a* path (" + (path.Count - 2) + " posts)";
                    return new { ok = true, to = _navTargetName ?? "position", id = hasId ? p.S("id") : (object)null, dist = F(fullDist), at = F(at), note = kind + dnote + "; re-issue go_to to replan" };
                }
                // Fallback: direct steering toward the arrive point (previous behavior).
                lock (_stateLock) { _path = null; _pathIdx = 0; _pathGoalSet = false; _trailJumps = null; }
                _navTarget = new Vector3(arrive.x, 0, arrive.z);
                SetMove(1f, 0f, false, 0f, Mathf.Clamp(stopAt / 2f, 0.3f, 12f), p.B("run", false));
            }
            else { _navTarget = null; _path = null; _pathIdx = 0; _pathGoalSet = false; StopMove(); _blockedInfo = "already at " + name; }
            return new { ok = true, to = _navTargetName ?? "position", id = hasId ? p.S("id") : (object)null, dist = F(fullDist), at = F(at), note = "walking with obstacle steering; re-issue go_to to update heading" };
        }

        // Resolve a human-ish name ("bed", "toilet", "player", "door") to a world
        // position from what we can currently find around the map body.
        // Collider scratch buffer for name→usable scans (main-thread use only).
        private readonly Collider[] _toolColliderBuf = new Collider[64];
        private Vector3? FindPlaceByName(string name)
        {
            if (!IsAlive(_kobold)) return null;
            string needle = name.ToLowerInvariant();
            // The host player.
            if (needle.Contains("player") || needle.Contains("me") || needle.Contains("you"))
            {
                try
                {
                    if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null)
                        return pp.kobold.transform.position;
                }
                catch (Exception) { }
            }
            // Another player by their chat name (or their avatar mesh name) — resolves
            // to their kobold body, so go_to(name='Yipper') reaches that player.
            try
            {
                object at = RunOnMainThread(() =>
                {
                    foreach (var k in SceneCache.Find<Kobold>(2f))
                    {
                        if (k == null || k == _kobold) continue;
                        string nick = KoboldOwnerNick(k);
                        if (nick == null) continue;
                        if (nick.ToLowerInvariant() == needle || CleanName(k.name).ToLowerInvariant() == needle)
                            return k.transform.position;
                    }
                    return null;
                }, 5000);
                if (at != null) return (Vector3)at;
            }
            catch (Exception) { }
            // Best matching GenericUsable (bed/toilet/tub/seat/door/swap...) in range.
            // Marshalled to the main thread — Physics is a main-thread API and this ran
            // on the LLM thread before. NonAlloc with our own buffer: a 60m sphere
            // fully-allocation in a busy scene otherwise.
            try
            {
                object res = RunOnMainThread(() =>
                {
                    var origin = _kobold.transform.position;
                    int count = Physics.OverlapSphereNonAlloc(origin, 60f, _toolColliderBuf, ~0, QueryTriggerInteraction.Collide);
                    GenericUsable best = null; float bestD = float.MaxValue;
                    for (int i = 0; i < count; i++)
                    {
                        var c = _toolColliderBuf[i];
                        if (c == null || IsOwnCollider(c)) continue;
                        GenericUsable u = null;
                        try { u = c.GetComponent<GenericUsable>() ?? c.GetComponentInParent<GenericUsable>(); } catch (Exception) { continue; }
                        if (u == null) continue;
                        string uName = CleanName(u.name).ToLowerInvariant();
                        string uCls = ClassifyUsable(uName).ToLowerInvariant();
                        if (uName.Contains(needle) || uCls.Contains(needle) || needle.Contains(uCls))
                        {
                            float d = Vector3.Distance(u.transform.position, origin);
                            if (d < bestD) { bestD = d; best = u; }
                        }
                    }
                    return best != null ? (object)best.transform.position : null;
                }, 5000);
                if (res != null) return (Vector3)res;
            }
            catch (Exception) { }
            return null;
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

        // Forget a fact: drop the fact whose category prefix (or text) matches. This is the
        // explicit half of forgetting — the model decides something is no longer relevant.
        // Accepts a category ("map"), a full fact, or a substring.
        private object ToolForget(JsonObj p)
        {
            string what = p.S("mem", p.S("fact", p.S("what", "")));
            if (string.IsNullOrWhiteSpace(what))
            {
                // No argument: forget the oldest scratch fact (least useful).
                lock (_facts)
                {
                    if (_facts.Count == 0) return new { ok = false, reason = "no_facts" };
                    int oldest = 0;
                    for (int i = 1; i < _facts.Count; i++)
                        if (_facts[i].Tick < _facts[oldest].Tick) oldest = i;
                    string oldestText = _facts[oldest].Text;
                    _facts.RemoveAt(oldest);
                    PushHistory("forget", oldestText);
                    return new { ok = true, forgotten = oldestText, facts = _facts.Count };
                }
            }
            what = what.Trim();
            int removedCount = 0;
            var removed = new List<string>();
            lock (_facts)
            {
                for (int i = _facts.Count - 1; i >= 0; i--)
                {
                    string f = _facts[i].Text;
                    if (f == what || f.Split(':')[0] == what || f.IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0)
                    { removed.Add(f); _facts.RemoveAt(i); removedCount++; }
                }
            }
            if (removedCount == 0) return new { ok = false, reason = "not_found", tried = what };
            PushHistory("forget", removed[0] + (removedCount > 1 ? " +" + (removedCount - 1) : ""));
            return new { ok = true, forgotten = removed[0], count = removedCount, facts = _facts.Count };
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

        // Legacy alias: jump is now a parameter of walk (the model-facing tool set is
        // go_to + walk + stop — one way to travel, one way to nudge).
        private object ToolJump()
        {
            // If locked in an animation station, jumping is how players get out —
            // do that first so "jump" behaves the way the model/player expects.
            if (IsInAnimationStation()) return ToolExitStation();
            return ToolWalk(new JsonObj(new Dictionary<string, object>
            {
                ["jump"] = true,
                ["speed"] = 0f,
                ["duration"] = 0.4f,
            }));
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

                // The mail/sell machine SELLS the kobold that climbs into it: its "use"
                // starts a swallow timer, and its intake sucks in ragdolled bodies it
                // can't climb out of. Never allowed, even if the model insists.
                if (target is MailMachine)
                    return new { ok = false, reason = "sell_machine", name = CleanName(target.name), dist = F(dist),
                        hint = "that machine SELLS kobolds — using it means being swallowed and destroyed. Never use it and never go near it" };

                // Face it first (yaw + slight pitch down toward the object) and turn
                // the *camera* — the model's vision follows this, so it will see the
                // object it just interacted with on the next tick.
                Vector3 to = target.transform.position - _kobold.transform.position;
                Vector3 flat = to; flat.y = 0;
                float yaw = Mathf.Atan2(flat.x, flat.z) * Consts.Rad2Deg;
                float pitch = Mathf.Clamp(Mathf.Atan2(-to.y, Mathf.Max(0.2f, flat.magnitude)) * Consts.Rad2Deg, -60f, 60f);
                lock (_stateLock) { _yawDeg = Mathf.Repeat(yaw, 360f); _pitchDeg = pitch; }
                _lastBigTurnTime = Time.unscaledTime; // make next tick attach a fresh image

                // Nest gate (again, at use-time): empty belly can't lay — don't let
                // the model grind a nest that will refuse it every turn.
                if (ClassifyUsable(CleanName(target.name)).ToLowerInvariant() == "nest")
                {
                    float vol = 0f; bool ready = true;
                    try { vol = GetEggVolume(_kobold); ready = IsReadyToLayEgg(_kobold); } catch (Exception) { }
                    if (!ready)
                        return new { ok = false, reason = "belly_not_full", name = CleanName(target.name), dist = F(dist), eggs = F(vol),
                            note = "a nest needs a FULL belly (>5ml egg); you have " + F(vol) + "ml — it will not work. Stop seeking nests until needs.eggs says READY_TO_LAY" };
                }
                if (!target.CanUse(_kobold))
                {
                    // cannot_use now explains WHY — the model used to read the station's
                    // own ":busy" (its own body inside) as "the station is full" and
                    // leave, or get silently refused on empty energy.
                    string hint = null; string reason = "cannot_use";
                    try
                    {
                        if (_kobold.GetEnergy() <= 0.01f)
                        {
                            reason = "out_of_energy";
                            hint = "your energy is EMPTY — stations refuse 0-energy bodies. Eat FOOD to recover (food metabolizes into energy)";
                        }
                        else if (IsInAnimationStation() && _charAnimator != null
                                 && _charAnimator.TryGetAnimationStationSet(out var curSet))
                        {
                            bool alreadyThisOne = false;
                            try { alreadyThisOne = StationSlotsForSet(curSet, true) != null && target.GetComponentInParent<IAnimationStationSet>() == curSet; }
                            catch (Exception) { }
                            if (alreadyThisOne)
                                hint = "you are ALREADY in this station — your slot is occupied by you; that's why it reads busy";
                        }
                        if (hint == null)
                        {
                            string slots = StationSlotsFor(target);
                            hint = slots != null
                                ? slots + " — if it's full, invite a partner (say) or pick another; don't just retry"
                                : "maybe busy/occupied or wrong state; if the player says seats exist, check 'slots'";
                        }
                    }
                    catch (Exception) { hint = "maybe busy/occupied or wrong state"; }
                    return new { ok = false, reason = reason, name = CleanName(target.name), hint = hint, dist = F(dist) };
                }
                try { target.LocalUse(_kobold); }
                catch (Exception e) { Logger.LogWarning("use: " + e.Message); return new { ok = false, reason = "use_failed", name = CleanName(target.name) }; }
                // The machine/station is what we just climbed onto — this is the
                // source of any pleasure that follows, not "the player".
                _stimSource = CleanName(target.name);
                _stimSourceT = Time.unscaledTime;
                // Track station purpose for smart exit decisions.
                _stationPurpose = ClassifyUsable(CleanName(target.name));
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

        // grab takes whatever is grabbable ~1m in front of the face (the game's own
        // grab bubble). The result NAMES what ended up in the grip — the AI used to
        // grab the player by accident and never know it was holding a person.
        private object ToolGrab(JsonObj p)
        {
            if (_grabber == null) return new { ok = false, reason = "no_grabber" };
            bool multi = p.B("multi", false);
            bool done = (bool)RunOnMainThread(() =>
            {
                try { _grabber.TryGrab(multi); return true; } catch (Exception e) { Logger.LogWarning("grab: " + e.Message); return false; }
            });
            try { Thread.Sleep(250); } catch (Exception) { }
            var held = HeldScan();
            if (held.Count == 0)
                return new Dictionary<string, object>
                {
                    ["ok"] = done,
                    ["grabbed"] = "",
                    ["note"] = "grabbed NOTHING — grab takes whatever is ~1m in front of your FACE; aim first (turn/look at the thing), get close, then grab again",
                };
            var names = new List<string>();
            string warning = null;
            foreach (var h in held)
            {
                names.Add(h.Name + " (" + h.Kind + ")");
                if (h.Victim != null)
                    warning = h.IsPlayerVictim
                        ? "THAT IS YOUR PLAYER — they are limp from being grabbed. Release right away with drop unless they asked to be carried; and always drop when they ask"
                        : "YOU ARE HOLDING ANOTHER PERSON (" + h.Name + ") — not an item. Let them go with drop unless they want to be carried; never throw them unasked";
            }
            var res = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["grabbed"] = string.Join(", ", names.ToArray()),
                ["note"] = "everything held by you now floats ~1m in front of your face on a physics spring — it lags your turns, bumps/pushes what you walk into, and rides your momentum",
            };
            if (warning != null) res["warning"] = warning;
            return res;
        }

        // Drop = gentle release. Verified: reports what left the hands; a PERSON still
        // joint-locked after an ordinary drop gets the game's OnReleaseRPC directly
        // (idempotent) so "the AI can't let go" can't happen silently.
        private object ToolDrop()
        {
            if (_grabber == null) return new { ok = false, reason = "no_grabber" };
            var before = HeldScan();
            if (before.Count == 0) return new { ok = true, dropped = "", note = "nothing was in your hands" };
            bool droppedOk = (bool)RunOnMainThread(() =>
            {
                try { _grabber.TryDrop(); return true; } catch (Exception e) { Logger.LogWarning("drop: " + e.Message); return false; }
            });
            try { Thread.Sleep(300); } catch (Exception) { }
            var after = HeldScan();
            foreach (var h in after)
            {
                if (h.Victim != null) { HardReleaseVictim(h.Victim);
                    PushWorldEvent("drop didn't take (" + h.Name + " was still gripped) — sent a direct release; verify with cat hold"); }
            }
            var dropped = new List<string>();
            foreach (var b in before)
            {
                bool still = false;
                foreach (var a in after) if (b.Grabbable == a.Grabbable) { still = true; break; }
                if (!still) dropped.Add(b.Name);
            }
            var stillHeld = new List<string>();
            foreach (var a in after) stillHeld.Add(a.Name + " (" + a.Kind + ")");
            var res = new Dictionary<string, object>
            {
                ["ok"] = stillHeld.Count == 0 && droppedOk,
                ["dropped"] = string.Join(", ", dropped.ToArray()),
                ["note"] = "released gently — a dropped PERSON stands back up; items fall and can bump/roll",
            };
            if (stillHeld.Count > 0)
            {
                res["still_held"] = string.Join(", ", stillHeld.ToArray());
                res["note"] = "STILL HELD: " + res["still_held"] + " — the release didn't take; try drop again, and if it persists report_issue it";
            }
            return res;
        }

        // throw/activate — the game's own "use held thing" button: non-weapons are
        // HURLED in the view direction (~+10 m/s, released — that's a throw); weapons
        // (water bucket spray, watering can squirt, guns) fire instead. Jump right
        // before activating to add arc/range. This is how the AI plays with the
        // physics props: throw fruit, spray the bucket, shoot.
        private object ToolThrow(JsonObj p)
        {
            if (_grabber == null) return new { ok = false, reason = "no_grabber" };
            var held = HeldScan();
            if (held.Count == 0)
                return new { ok = false, reason = "empty_hands", note = "nothing to throw — grab something first (grab takes what's ~1m in front of your face)" };
            bool thrown = (bool)RunOnMainThread(() =>
            {
                try { _grabber.TryActivate(); return true; } catch (Exception e) { Logger.LogWarning("throw: " + e.Message); return false; }
            });
            try { Thread.Sleep(400); } catch (Exception) { }
            bool stopped = (bool)RunOnMainThread(() =>
            {
                try { _grabber.TryStopActivate(); return true; } catch (Exception) { return false; }
            });
            var names = new List<string>();
            foreach (var h in held) names.Add(h.Name + " (" + h.Kind + ")");
            bool hasPerson = false;
            foreach (var h in held) if (h.Victim != null) hasPerson = true;
            var res = new Dictionary<string, object>
            {
                ["ok"] = thrown && stopped,
                ["activated"] = string.Join(", ", names.ToArray()),
                ["note"] = "thrown/used in the direction your VIEW points (set it with look(yaw/pitch) first); jumping right before adds range; keep held things away from the mail/sell machine's intake",
            };
            if (hasPerson)
            {
                res["warning"] = "you threw/activated while holding a PERSON — that hurls their body; only okay when they asked to be thrown";
                HardReleaseAnyoneLeft();
            }
            return res;
        }

        // After a throw, anything that stayed in the grip (weapons stay held!) shouldn't
        // keep a person trapped — release remaining person-grabs.
        private void HardReleaseAnyoneLeft()
        {
            try { Thread.Sleep(500); } catch (Exception) { }
            foreach (var h in HeldScan())
                if (h.Victim != null) HardReleaseVictim(h.Victim);
        }

        private object ToolStatus()
        {
            return BuildPerception(false);
        }
    }
}
