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

        // Training-data recorder file ([DataRecord].Enabled) — same idiom/fallback
        // as ReportFilePath; null when no path could be derived (recording stays off).
        internal static string TurnLogFilePath()
        {
            try
            {
                string data = Application.dataPath;
                if (!string.IsNullOrEmpty(data))
                    return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(data), "BepInEx", "config", "kkllmnpc_turns.jsonl");
            }
            catch (Exception) { }
            try
            {
                string asm = System.IO.Path.GetDirectoryName(typeof(NPCInstance).Assembly.Location);
                if (!string.IsNullOrEmpty(asm)) return System.IO.Path.Combine(asm, "kkllmnpc_turns.jsonl");
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
                if (!hit.HasValue) return new { ok = false, reason = "unknown_place", tried = name, note = "try `cat places` — nests/beds/farm plots and watering cans are listed map-wide — or ls for nearby things" };

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

            // Nest gate: a nest physically can't be used until egg >5ml AND energy >1
            // (OvipositionSpot.CanUse). Two distinct refusals — the model used to read
            // a "belly not full" note while carrying a poured guest-egg (real blocker:
            // energy) and decided the check was broken, then tried play stations to
            // "make eggs". Name the actual blocker, name the fix, and warn that
            // waiting/stimulation create neither.
            string destCls = ClassifyUsable(name).ToLowerInvariant();
            if (destCls == "nest")
            {
                float vol = 0f;
                try { vol = GetEggVolume(_kobold); } catch (Exception) { }
                if (vol <= 5f)
                    return new { ok = false, reason = "belly_not_full", name = _navTargetName, id = hasId ? p.S("id") : (object)null, dist = F(fullDist), eggs = F(vol), note = "you carry " + F(vol) + "ml egg — a nest needs >5ml; egg reagent comes ONLY from eating/swallowing, never from play stations or waiting. Do NOT seek a nest until needs.eggs says READY_TO_LAY — play, explore or keep company instead" };
                float lay = LayEnergy(_kobold);
                if (lay <= 1f)
                    return new { ok = false, reason = "out_of_energy", name = _navTargetName, id = hasId ? p.S("id") : (object)null, dist = F(fullDist), eggs = F(vol), note = "you carry " + F(vol) + "ml egg — that IS enough — but your ENERGY is " + F(lay) + " and a nest lays only above 1: eat food (or bed-rest) first, then return. Nothing else raises energy — stimulation and sitting in stations don't, ever" };
            }
            float stopAt = Mathf.Max(0f, fullDist - at);
            // No instant yaw snap here: it wrote the facing directly, and a model
            // re-issuing go_to every think tick whipped the whole ragdoll 90-180°
            // per tick ("movement like a fly bumping into things"). Waypoint
            // pursuit turns the body toward the route smoothly on its own.
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
                        return new { ok = true, to = _navTargetName ?? "position", id = hasId ? p.S("id") : (object)null, dist = F(fullDist), at = F(at), note = "continuing the current route — it course-corrects on its own as it walks; no need to re-issue go_to" };
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
                    return new { ok = true, to = _navTargetName ?? "position", id = hasId ? p.S("id") : (object)null, dist = F(fullDist), at = F(at), note = kind + dnote + "; go_to keeps walking and course-corrects on its own — only re-issue it for a NEW destination" };
                }
                // Fallback: direct steering toward the arrive point (previous behavior).
                lock (_stateLock) { _path = null; _pathIdx = 0; _pathGoalSet = false; _trailJumps = null; }
                _navTarget = new Vector3(arrive.x, 0, arrive.z);
                SetMove(1f, 0f, false, 0f, Mathf.Clamp(stopAt / 2f, 0.3f, 12f), p.B("run", false));
                if (mapCovers && NoRouteNotYetReported(arrive))
                    NavOutcome("no path to "
                        + (_navTargetName != null ? _navTargetName : "that place")
                        + " exists in the maps — steering by hand from here");
            }
            else { _navTarget = null; _path = null; _pathIdx = 0; _pathGoalSet = false; StopMove(); _blockedInfo = "already at " + name; }
            return new { ok = true, to = _navTargetName ?? "position", id = hasId ? p.S("id") : (object)null, dist = F(fullDist), at = F(at), note = "walking with obstacle steering; go_to course-corrects on its own — only re-issue it for a NEW destination" };
        }

        // Resolve a human-ish name ("bed", "toilet", "player", "door") to a world
        // position from what we can currently find around the map body.
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
            // Best matching place SCENE-WIDE (no perception radius): the key machines
            // (nests, beds, farm plots, watering cans) are usually FAR from spawn —
            // the old 60m OverlapSphere answered "unknown_place" for exactly them,
            // the user's "it can't navigate the scene well enough". StationList() =
            // every GenericUsable on the map; soil tiles + watering cans are not
            // GenericUsables and come from the key-places scan. Kind aliases let
            // `cd farm` / `cd water` / `cd can` / `cd nest` / `cd bed` match by KIND.
            try
            {
                object res = RunOnMainThread(() =>
                {
                    var origin = _kobold.transform.position;
                    string needleLow = needle;
                    // Kind aliases so a kind word resolves even when the object's
                    // mesh name doesn't contain it ("SoilTile" vs "farm plot").
                    bool wantsFarm = needleLow.Contains("farm") || needleLow.Contains("soil")
                        || needleLow.Contains("plot") || needleLow.Contains("field");
                    bool wantsWater = needleLow.Contains("water") || needleLow.Contains("watering")
                        || needleLow == "can" || needleLow.Contains("kettle");
                    Vector3 best = default(Vector3); bool found = false; float bestD = float.MaxValue;
                    foreach (var u in StationList())
                    {
                        if (u == null || u.transform == null) continue;
                        string uName = CleanName(u.name).ToLowerInvariant();
                        // TYPE-first: a kind word ("cd door/nest/food") must match the
                        // machine's own MonoBehaviour types, not just its mesh name.
                        string uCls = (ClassifyByType(u.gameObject) ?? ClassifyUsable(uName)).ToLowerInvariant();
                        bool matched = uName.Contains(needleLow) || uCls.Contains(needleLow) || needleLow.Contains(uCls);
                        if (!matched) continue;
                        float d = Vector3.Distance(u.transform.position, origin);
                        if (d < bestD) { bestD = d; best = u.transform.position; found = true; }
                    }
                    // Farm plots (SoilTile) — "farm"/"soil"/"plot".
                    foreach (var t in SoilTileList())
                    {
                        if (t == null || t.transform == null) continue;
                        if (!wantsFarm && !CleanName(t.name).ToLowerInvariant().Contains(needleLow)) continue;
                        float d = Vector3.Distance(t.transform.position, origin);
                        if (d < bestD) { bestD = d; best = t.transform.position; found = true; }
                    }
                    // Watering cans — "water"/"watering"/"can".
                    foreach (var w in WateringCanList())
                    {
                        if (w == null || w.transform == null) continue;
                        if (!wantsWater && !CleanName(w.name).ToLowerInvariant().Contains(needleLow)) continue;
                        float d = Vector3.Distance(w.transform.position, origin);
                        if (d < bestD) { bestD = d; best = w.transform.position; found = true; }
                    }
                    return found ? (object)best : null;
                }, 5000);
                if (res != null) return (Vector3)res;
            }
            catch (Exception) { }
            // `place:` facts with numbers — "place: mailbox at x=12.3 z=45.6" — are
            // real navigation targets. The coords habit is TAUGHT only where the
            // world map covers the scene (anchor teach), but resolution works
            // anywhere: distances beyond local reach still answer too_far honestly.
            try
            {
                Vector3? factHit = PlaceFactPosition(needle);
                if (factHit.HasValue) return factHit;
            }
            catch (Exception) { }
            return null;
        }

        // The model's remembered Vector3s: facts written as
        //   place: <name> at x=<x> z=<z>
        // (also accepts  pos:  and  x=…, z=…  shapes). Name match is substring-either-
        // way on the fact's name part; the numbers are meters. Null when none matches.
        private Vector3? PlaceFactPosition(string needle)
        {
            try
            {
                if (string.IsNullOrEmpty(needle)) return null;
                List<string> facts;
                lock (_facts)
                {
                    facts = new List<string>(_facts.Count);
                    foreach (var f in _facts) facts.Add(f.Text);
                }
                foreach (var f in facts)
                {
                    if (f == null) continue;
                    string low = f.ToLowerInvariant();
                    if (!(low.StartsWith("place:") || low.StartsWith("pos:") || low.StartsWith("location:")))
                        continue;
                    int xi = low.IndexOf("x=");
                    int zi = low.IndexOf("z=");
                    if (xi < 0 || zi < 0 || zi < xi) continue;
                    // The name part between the prefix and the numbers.
                    int nameEnd = low.IndexOf("at");
                    string namePart = nameEnd > 0 ? low.Substring(0, nameEnd) : low;
                    namePart = namePart.Substring(Mathf.Min(namePart.IndexOf(':') + 1, namePart.Length)).Trim();
                    if (namePart.Length == 0) continue;
                    bool match = namePart.Contains(needle) || needle.Contains(namePart);
                    if (!match) continue;
                    string xs = NumberAfter(low, xi + 2);
                    string zs = NumberAfter(low, zi + 2);
                    float x, z;
                    if (!TryParseFloat(xs, out x) || !TryParseFloat(zs, out z)) continue;
                    if (Mathf.Abs(x) > 4000f || Mathf.Abs(z) > 4000f) continue; // sane world meters only
                    return new Vector3(x, 0f, z);
                }
            }
            catch (Exception) { }
            return null;
        }

        private static string NumberAfter(string s, int start)
        {
            var sb = new StringBuilder();
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsDigit(c) || c == '-' || c == '.' || c == ',') sb.Append(c == ',' ? '.' : c);
                else if (sb.Length > 0) break;
            }
            return sb.ToString();
        }

        private static bool TryParseFloat(string s, out float v)
        {
            v = 0f;
            if (string.IsNullOrEmpty(s)) return false;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        // Store a fact in long-term memory ("bed upstairs", "player is friendly").
        // Deliberately store a fact (long-term memory, 24-item ring) the model reads
        // every tick. Junk classes (viewer-relative text, goals-in-facts, momentary
        // self/body states, transient object states) are refused with a teaching
        // note describing where that info lives LIVE instead.
        private object ToolRemember(JsonObj p)
        {
            string fact = p.S("mem", "");
            if (string.IsNullOrWhiteSpace(fact)) return new { ok = false, reason = "empty_mem" };
            fact = Sanitize(fact);
            string junk = TextUtil.JunkFactReason(fact);
            if (junk != null)
                return new { ok = false, reason = junk, note = TextUtil.JunkFactNote(junk) };
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

        // Look AT something and report what's hit — the concrete "look at" tool.
        // Aim at a partner/object by the `id` from `nearby`/survey, or by a world
        // heading; return the live position, distance, facing, and what the ray
        // landed on. If the ray lands on a real partner or usable, also let
        // bystanders know (the `[looked]` event).
        private object ToolLookAt(JsonObj p)
        {
            if (!IsAlive(_kobold) || !IsAlive(_head)) return new { ok = false, reason = "no_body" };

            // Resolve the aim. `id` from nearby/survey wins; else a heading_deg.
            float yaw = _yawDeg; float pitch = _pitchDeg; bool idGiven = false;
            try
            {
                if (p.Has("id"))
                {
                    idGiven = true;
                    int id = p.Has("id") ? (int)Math.Round(p.F("id")) : -1;
                    Transform tf = (id >= 0) ? FindTargetById(id) : null;
                    if (tf != null)
                    {
                        Vector3 dir = tf.position - _head.position;
                        if (dir.sqrMagnitude > 1e-6f)
                        {
                            yaw = Mathf.Repeat(Mathf.Atan2(dir.x, dir.z) * Consts.Rad2Deg, 360f);
                            pitch = Mathf.Clamp01(Mathf.Atan2(dir.y, Mathf.Max(0.01f, dir.magnitude))) * 2f - 45f;
                        }
                    }
                }
                if (p.Has("heading_deg")) yaw = p.F("heading_deg");
            }
            catch (Exception) { }
            // Clamp pitch into the valid range the camera/animator can face.
            pitch = Mathf.Clamp(pitch, -89f, 89f);
            yaw = Mathf.Repeat(yaw, 360f);

            // Turn the body to look (triggers a fresh image next tick).
            lock (_stateLock)
            {
                _yawDeg = MoveAngleTowards(_yawDeg, yaw, 60f);
                _pitchDeg = MoveAngleTowards(_pitchDeg, pitch, 40f);
            }
            _lastBigTurnTime = Time.unscaledTime;

            // Fire the ray. On a hit, classify what was landed on.
            float range = Mathf.Clamp(p.F("range", _cfgRayRange != null ? _cfgRayRange.Value : 25f), 2f, 100f);
            var hitResult = (Dictionary<string, object>)RunOnMainThread(() =>
            {
                var res = new Dictionary<string, object>();
                Vector3 dir = Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
                var org = _head.position;
                RaycastHit hit;
                bool hitIt = Physics.Raycast(org, dir, out hit, range, ~0, QueryTriggerInteraction.Ignore) && !IsOwnCollider(hit.collider);
                res["yaw"] = F(_yawDeg);
                res["pitch"] = F(_pitchDeg);
                if (!hitIt)
                {
                    res["hit"] = null;
                }
                else
                {
                    string name = "", kind = "geometry"; int tid = -1;
                    bool partner = false;
                    try
                    {
                        var kb = hit.collider.GetComponentInParent<Kobold>();
                        if (kb != null)
                        {
                            kind = IsPlayerKobold(kb) ? "player" : "kobold";
                            name = kb.name;
                            tid = TargetIdFor(kb.transform, kb.name);
                            partner = true;
                        }
                        else
                        {
                            var us = hit.collider.GetComponent<GenericUsable>() ?? hit.collider.GetComponentInParent<GenericUsable>();
                            if (us != null)
                            {
                                string cn = CleanName(us.name);
                                name = cn;
                                kind = ClassifyUsable(cn);
                                tid = TargetIdFor(us.transform, cn);
                            }
                        }
                    }
                    catch (Exception) { }
                    Vector3 center = hit.point;
                    RayHitInfo rhi = null;
                    try
                    {
                        rhi = GetRayHitInfo(hit.collider);
                        if (rhi != null && rhi.HasRender) center = rhi.Center;
                    }
                    catch (Exception) { }
                    res["hit"] = new
                    {
                        hit = true,
                        name = name,
                        kind = kind,
                        id = tid,
                        distance = F(hit.distance),
                        x = F(center.x), y = F(center.y), z = F(center.z),
                        facing = F(hit.collider.transform.eulerAngles.y),
                        width = F(rhi != null ? rhi.Size.x : 0f),
                        height = F(rhi != null ? rhi.Size.y : 0f),
                        depth = F(rhi != null ? rhi.Size.z : 0f),
                        partner = partner,
                    };
                }
                return res;
            }, 8000) as Dictionary<string, object>;

            // If we looked at a live partner, let bystanders see who's looking at
            // whom — the [looked] event (bystander awareness, like the swallow
            // narration's spectator line).
            if (hitResult != null && hitResult.ContainsKey("hit") && hitResult["hit"] != null)
            {
                var hitDict = hitResult["hit"] as Dictionary<string, object>;
                bool partner = (hitDict != null && hitDict.ContainsKey("partner")) ? Convert.ToBoolean(hitDict["partner"]) : false;
                if (partner) BroadcastLooked(hitDict);
            }

            return new { ok = true, id_gave_target = idGiven, aim = hitResult };
        }

        // Bystander awareness: announce to everyone (except the looker) that they
        // were looking at someone — the `[looked]` event. Cooldown-spaced so a
        // model sweeping the room doesn't spam.
        private void BroadcastLooked(Dictionary<string, object> hitDict)
        {
            try
            {
                if (Time.unscaledTime - _lookedCooldownT < 3f) return;
                _lookedCooldownT = Time.unscaledTime;
                string name = hitDict.ContainsKey("name") ? hitDict["name"] as string : "";
                string kind = hitDict.ContainsKey("kind") ? hitDict["kind"] as string : "";
                if (string.IsNullOrEmpty(name)) return;
                string ev = "[looked] you were looking at " + (kind == "player" ? "the PLAYER" : name)
                    + " — if anyone can see you, they noticed.";
                try { Plugin.FanoutWorldEventExcept(ev, this, null); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        // Legacy alias: jump is now a parameter of walk (the model-facing tool set is
        // go_to + walk + stop — one way to travel, one way to nudge).
        private object ToolJump()
        {
            // If locked in an animation station, jumping is how players get out —
            // do that first so "jump" behaves the way the model/player expects.
            if (IsInAnimationStation()) return ToolExitStation();
            // A jump PUSHES into the facing: a hop in place was useless for ledge
            // hops and fence clears. speed 0.7 keeps a forward burst for the arc.
            return ToolWalk(new JsonObj(new Dictionary<string, object>
            {
                ["jump"] = true,
                ["speed"] = 0.7f,
                ["duration"] = 0.5f,
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
            // WATER-TOOL ROUTE (agents kept "using" the bucket/can — the drink path
            // empties the water and leaves nothing to spray). A water weapon is never
            // used: HELD → route to `water` (spray the nearest plant); ON THE GROUND →
            // route to grab (pick it up first). Contextual, with the teach attached.
            try
            {
                string whint = "";
                try { whint = (p.S("name", p.S("target", "")) ?? "").Trim().ToLowerInvariant(); } catch (Exception) { }
                bool heldWater = false; double wid = -1;
                try { foreach (var h in HeldScan()) if (h != null && (h.Kind == "water bucket" || h.Kind == "watering can")) { heldWater = true; break; } } catch (Exception) { }
                Transform waterTf = null;
                bool idGiven = p.Has("id");
                if (idGiven)
                {
                    try { wid = p.DB("id", -1); } catch (Exception) { }
                    var tf = wid >= 0 ? FindTargetById((int)wid) : null;
                    if (tf != null)
                    {
                        try
                        {
                            var bucketC = tf.GetComponentInParent<BucketWeapon>();
                            if (bucketC != null) waterTf = bucketC.transform;
                            if (waterTf == null)
                            {
                                var canC = tf.GetComponentInParent<WateringCanWeapon>();
                                if (canC != null) waterTf = canC.transform;
                            }
                        }
                        catch (Exception) { }
                    }
                }
                bool hintWater = whint.Contains("bucket") || whint.Contains("watering can") || whint.Contains("water can");
                if (heldWater || waterTf != null || (hintWater && !heldWater && !idGiven))
                {
                    if (heldWater) return ToolWater(p);
                    // On the ground: grab it first (the tool fires only when in reach).
                    return new { ok = false, reason = "water_tool",
                        note = "that is the WATER TOOL, not a drink — using it would drink the water away; `get` it into your hands, face the planted tile, then `water` (or aim-and-throw for a long spray)" };
                }
            }
            catch (Exception) { }
            // PLANTABLE-IN-HAND SOIL ROUTE: `use` with a seed/egg held and a soil
            // intent (target named soil/dirt/plot/farm — or no usable target at all)
            // goes through the plant macro: it walks ONTO the plot, looks DOWN at it
            // (the plant happens where the item floats), and VERIFIES the tile took.
            try
            {
                var heldSeed = FindHeldPlantableSeed();
                if (heldSeed != null)
                {
                    string hint = (p.S("name", p.S("target", "")) ?? "").Trim().ToLowerInvariant();
                    // NOTE: an EMPTY hint does NOT route — `use` with a bare seed in
                    // hand still means "interact what's in front" (a station); planting
                    // asks for the explicit words (or the item's own name / an id of
                    // the plot) so station uses are never hijacked.
                    bool soilIntent = hint.Length > 0
                        && (hint.IndexOf("soil", StringComparison.Ordinal) >= 0
                        || hint.IndexOf("dirt", StringComparison.Ordinal) >= 0
                        || hint.IndexOf("plot", StringComparison.Ordinal) >= 0
                        || hint.IndexOf("farm", StringComparison.Ordinal) >= 0)
                        || (heldSeed.name ?? "").IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (soilIntent)
                    {
                        try { return PlantWithPositioning(heldSeed.transform, () => heldSeed, HeldItemWord(heldSeed.name)); }
                        catch (Exception e) { Logger.LogWarning("use plant route: " + e.Message); }
                    }
                }
            }
            catch (Exception) { }
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

                // Nest gate (again, at use-time): an unusable lay can't be ground — but
                // the refusal names the real blocker (egg vs energy), like go_to above.
                if (ClassifyUsable(CleanName(target.name)).ToLowerInvariant() == "nest")
                {
                    float vol = 0f;
                    try { vol = GetEggVolume(_kobold); } catch (Exception) { }
                    if (vol <= 5f)
                        return new { ok = false, reason = "belly_not_full", name = CleanName(target.name), dist = F(dist), eggs = F(vol),
                            note = "you carry " + F(vol) + "ml egg — a nest needs >5ml; egg reagent comes ONLY from eating/swallowing, never from play stations or waiting. Stop seeking nests until needs.eggs says READY_TO_LAY" };
                    float lay = LayEnergy(_kobold);
                    if (lay <= 1f)
                        return new { ok = false, reason = "out_of_energy", name = CleanName(target.name), dist = F(dist), eggs = F(vol),
                            note = "you carry " + F(vol) + "ml egg — that IS enough — but your ENERGY is " + F(lay) + " and a nest lays only above 1: eat food (or bed-rest) first, then return. Nothing else raises energy — stimulation and sitting in stations don't, ever" };
                }
                if (!target.CanUse(_kobold))
                {
                    // MAILBOX FIRST: CanUse=false on a mailbox means the box is EMPTY
                    // (no mail inside) — it is never busy or occupied. The generic
                    // "maybe busy" hint had agents camping an empty mailbox forever.
                    if (target is MailboxUsable)
                    {
                        string mailnote;
                        try
                        {
                            mailnote = ObjectiveManager.HasMail() && ObjectiveManager.GetCurrentObjective() == null
                                ? "the box is ready — mail IS waiting; if this keeps failing, step closer and try again"
                                : "the mailbox is EMPTY — no mail inside right now. That's what CanUse=false means here; it is never busy. Nothing to fetch: when the next letter arrives the space-beam above it glows and 'quest' shows mail waiting — come back then.";
                        }
                        catch (Exception) { mailnote = "the mailbox has no mail to collect right now — that is what CanUse=false means here; it is never busy"; }
                        return new { ok = false, reason = "mail_empty", name = CleanName(target.name), dist = F(dist), note = mailnote };
                    }
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
                // REACHED-AND-SEEN gate: interact only when actually near enough AND
                // with a clear line from the body. Pathfinding used to declare arrival
                // adjacent THROUGH a wall and the game's mount animation then dragged
                // the body through the building — the "noclip lerp".
                if (dist > InteractRange + 1.2f)
                    return new { ok = false, reason = "too_far", name = CleanName(target.name), dist = F(dist),
                        note = "that target is " + F(dist) + "m away — far past interaction reach: go_to (cd " + CleanName(target.name) + " or cd id:N from 'nearby') to approach it, then use again" };
                bool clear_;
                try
                {
                    // Bodies never block (the ray's own parent chain includes ours);
                    // the target's own machine colliders are rejected so "can I see it"
                    // isn't answered by "yes, I'm touching it".
                    Transform atf = target.transform;
                    clear_ = WorldMap.SegmentClear(target.transform.position, _kobold.transform.position,
                        c =>
                        {
                            if (c == null) return true;
                            try
                            {
                                if (c.GetComponentInParent<Kobold>() != null) return true;
                                Transform t = c.transform;
                                for (int i = 0; t != null && i < 3; i++)
                                { if (t == atf) return true; t = t.parent; }
                            }
                            catch (Exception) { return true; }
                            return false;
                        }, WorldMap.BodyHalfWidthM);
                }
                catch (Exception) { clear_ = true; } // LOS utility unavailable — never block use on it
                if (!clear_)
                    return new { ok = false, reason = "wall_between", name = CleanName(target.name), dist = F(dist),
                        note = "something SOLID sits between you and it (a wall or closed door?) — you can't use it from here; walk around to its side first: cd to it and follow the route, or step until you can see it in your view. Never use things through walls" };
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
                        // Touch-bubble LOS: a usable on the far side of a thin wall is
                        // inside the 2.6m bubble but NOT usable — selecting it used to
                        // let the mount animation drag the body through the wall.
                        Transform atf2 = u2.transform;
                        bool seesIt;
                        try
                        {
                            seesIt = WorldMap.SegmentClear(u2.transform.position, pos,
                                cc =>
                                {
                                    if (cc == null) return true;
                                    try
                                    {
                                        if (cc.GetComponentInParent<Kobold>() != null) return true;
                                        Transform t = cc.transform;
                                        for (int i = 0; t != null && i < 3; i++)
                                        { if (t == atf2) return true; t = t.parent; }
                                    }
                                    catch (Exception) { return true; }
                                    return false;
                                }, WorldMap.BodyHalfWidthM);
                        }
                        catch (Exception) { seesIt = true; }
                        if (!seesIt) continue;
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
            // AIM FIRST (the grab cone follows the camera view; small items on the
            // floor were never in it because the eye stayed level). Optional hint:
            // name or id targets a specific thing; with no hint the nearest
            // grabbable gets the aim. Main thread (scene reads).
            string hint = p.S("name", p.S("target", "")).Trim();
            string aimed = null;
            try
            {
                RunOnMainThread(() =>
                {
                    try
                    {
                        Vector3 eye = _head != null ? _head.position : _kobold.transform.position + Vector3.up * 0.6f;
                        Vector3 best = Vector3.zero; string bestName = null;
                        float bestD = 3.2f * 3.2f;
                        var hits = new Collider[32];
                        int n = Physics.OverlapSphereNonAlloc(_kobold.transform.position + Vector3.up * 0.5f, 3.2f, hits, ~0, QueryTriggerInteraction.Ignore);
                        // "id:N" / "N" hints resolve through the target registry —
                        // the model read the id from nearby, honor it exactly.
                        Transform hintedTf = null;
                        string idHint = hint;
                        if (idHint.StartsWith("id:", StringComparison.OrdinalIgnoreCase)) idHint = idHint.Substring(3).Trim();
                        int hidNum;
                        if (int.TryParse(idHint, out hidNum) && hidNum >= 0)
                        {
                            var tf = FindTargetById(hidNum);
                            if (tf != null) { hintedTf = tf; hint = CleanName(tf.name); }
                        }
                        for (int i = 0; i < n; i++)
                        {
                            var c = hits[i];
                            if (c == null || IsOwnCollider(c)) continue;
                            // A KOBOLD IS NEVER A GRAB TARGET — player or sibling NPC.
                            // Grabbing a person is not an item pickup; they can object.
                            // The model sees them via nearby/people instead. Grab only
                            // physical items (IGrabbable) from here on.
                            var kb = c.GetComponentInParent<Kobold>();
                            if (kb != null && kb != _kobold) continue;
                            Transform tf = c.transform;
                            string nm = CleanName(tf.name);
                            if (c.GetComponentInParent<IGrabbable>() != null)
                            {
                                if (!string.IsNullOrEmpty(hint)
                                    && nm.IndexOf(hint, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                float d = (tf.position - eye).sqrMagnitude;
                                if (bestName == null || d < bestD)
                                {
                                    bestD = d; best = tf.position; bestName = nm;
                                }
                            }
                        }
                        if (hintedTf != null && bestName == null)
                        {
                            best = hintedTf.position; bestName = CleanName(hintedTf.name);
                        }
                        // Nothing matched: keep the old grab (aim unchanged) when a
                        // HINT was given (aiming away would be wrong); else sweep:
                        // aim ahead-and-down, the classic floor-item miss.
                        if (bestName == null && string.IsNullOrEmpty(hint))
                        {
                            best = eye + Quaternion.Euler(0, _yawDeg, 0) * (Vector3.forward * 0.8f) + Vector3.down * 0.7f;
                            bestName = "(floor ahead)";
                        }
                        if (bestName != null)
                        {
                            Vector3 to = best - eye;
                            Vector3 flat = to; flat.y = 0;
                            float yaw = Mathf.Atan2(flat.x, flat.z) * Consts.Rad2Deg;
                            float pitch = Mathf.Clamp(Mathf.Atan2(-to.y, Mathf.Max(0.25f, flat.magnitude)) * Consts.Rad2Deg, -70f, 70f);
                            lock (_stateLock) { _yawDeg = Mathf.Repeat(yaw, 360f); _pitchDeg = pitch; }
                            _lastBigTurnTime = Time.unscaledTime;
                            aimed = bestName;
                        }
                    }
                    catch (Exception e) { Logger.LogWarning("grab aim: " + e.Message); }
                    return true;
                });
                if (aimed != null) { try { Thread.Sleep(260); } catch (Exception) { } } // let the eye swing
            }
            catch (Exception) { }
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
                ["aimed_at"] = aimed ?? "",
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
            // Captured INSIDE the marshalled try-drop (main thread): did our drop
            // turn into a force-feed swallow? The result must tell the truth —
            // "dropped" would lie when the release became a shove-into-a-mouth.
            string[] forceFed = null;
            bool droppedOk = (bool)RunOnMainThread(() =>
            {
                string[] forceFedTmp = null;
                try { _grabber.TryDrop(); forceFedTmp = SwallowManager.SwallowsBegunBy(_kobold); return true; }
                catch (Exception e) { Logger.LogWarning("drop: " + e.Message); return false; }
                finally { forceFed = forceFedTmp; }
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
            if (forceFed != null && forceFed.Length > 0)
            {
                res["force_fed"] = string.Join(", ", forceFed);
                if (stillHeld.Count == 0)
                    res["note"] = "you dropped a person onto another kobold's head — the receiver SWALLOWED them whole (force-feeding). No egg/rebirth is visible yet; the fate of the one you fed follows SwallowEffect — check your next [event] lines";
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
