// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// PlayerTrail — the AI learns routes the way it learns from other players: by watching.
//
// Destiny-2-style "players proved it, so it's a path":
//   * Every watched body (host player here; NPC bodies patch from their own movement
//     loop) patches the shared walkability grid behind itself — a cell baked as "wall"
//     (a door closed at bake time, later-built geometry) becomes walkable the moment
//     someone actually stands there, and stays walkable because the patch lands in the
//     cached .kkmap too.
//   * The host player's actual walked path is SAMPLED (positions + jump flags),
//     persisted per scene to BepInEx/config/kkllmnpc_maps/trail_<scene>.txt, and offered
//     to go_to/follow as a route when the A* grid has no answer (too_far / gaps /
//     parkour): the NPC then follows the player's demonstrated route, jumps included,
//     by replaying each flagged point as a hop burst.
//   * Route narration ("the player went east ~9m, hopping, through the TopDoor") fans
//     out to every instance's world-event queue — how an NPC understands the player
//     left a room and HOW, with no facts and no memory: it watched.
//
// All of this is live observation in shared (non-fact) state, refreshed from the scene.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KKLLMNPC
{
    internal static class PlayerTrail
    {
        // ------------------------------------------------------------------
        // snapshot of the host player's live observable state (no facts involved)
        // ------------------------------------------------------------------
        internal sealed class Snapshot
        {
            public bool Has;
            public float SampleTime;
            public Vector3 Pos;
            public float Vel;         // horizontal speed m/s
            public float Heading;     // world yaw deg (0 = +Z = north)
            public bool InStation;
            public string StationName, StationKind;
            public Transform StationTf; // the station they're in / standing next to
            public bool Ragdolled;
            public List<NPCInstance.HeldInfo> Held;
        }
        internal static volatile Snapshot Snap = new Snapshot();

        private sealed class TrailPoint
        {
            public Vector3 Pos;
            public bool Jump;
            public float T; // sample time; -1 = loaded from file (never expires in-session)
        }

        private static readonly List<TrailPoint> _pts = new List<TrailPoint>(4096);
        private static readonly object _lock = new object();
        private static bool _dirty;
        private static float _lastSample = -99f, _lastSave;
        private static Vector3 _lastPt;
        private static Vector3 _legAnchor;      // where the unreported "leg" began
        private static bool _legJumped;
        private static bool _legInit;
        private static string _scene = "";
        private static string _lastDoorName;

        private const float SampleInterval = 0.4f;
        private const float MinPointSpacing = 0.5f;
        private const int TrailCap = 4000;
        private const float LegReportDistance = 9f;
        private const float TrailFreshSeconds = 300f;
        // Persistence throttling: file writes are MAIN-THREAD POISON on network drives
        // (the game may run on a share — an SMB stall inside Update is a "freeze").
        // Write on a background thread only, and only after real progress.
        private const float SaveMinInterval = 30f;
        private const int SaveMinNewSamples = 120;
        private static int _samplesAtLastSave;
        private static int _writeInFlight; // 1 = a thread-pool write is queued/running

        // Main-thread poll from the plugin (Update, next to WorldMap.Tick).
        internal static void Tick(LLMNPCPlugin plugin)
        {
            try
            {
                if (plugin == null || !plugin._mainReady || !plugin.Running) return;
                string scene;
                try { scene = SceneManager.GetActiveScene().name; } catch (Exception) { scene = ""; }
                if (scene != _scene)
                {
                    if (_scene.Length > 0) Save(_scene);
                    _scene = scene;
                    Load(scene);
                    _legInit = false;
                    _lastDoorName = null;
                    _lastSample = -99f;
                }
                if (scene.Length == 0) return;

                Kobold k = null;
                try { if (PlayerPossession.TryGetPlayerInstance(out var pp)) k = pp.kobold; } catch (Exception) { }
                if (k == null || !IsAlive(k)) { Snap = new Snapshot(); return; }

                // Walkproof: the player stood here, therefore it's passable (and it
                // persists into the cached map) — the "room blocked at bake time but
                // reachable later" link.
                try { WorldMap.PatchWalkable(k.transform.position); } catch (Exception) { }

                if (Time.unscaledTime - _lastSample < SampleInterval) return;
                _lastSample = Time.unscaledTime;

                var snap = new Snapshot
                {
                    Has = true,
                    SampleTime = Time.unscaledTime,
                    Pos = k.transform.position,
                    Ragdolled = IsRagdolled(k),
                };
                try
                {
                    var mover = k.GetComponent<KoboldCharacterController>();
                    if (mover != null && mover.body != null)
                    {
                        Vector3 v = mover.body.velocity;
                        snap.Vel = new Vector3(v.x, 0f, v.z).magnitude;
                        snap.Heading = mover.body.rotation.eulerAngles.y;
                    }
                }
                catch (Exception) { }
                try
                {
                    var anim = k.GetComponentInChildren<CharacterControllerAnimator>(true);
                    snap.InStation = anim != null && anim.IsAnimating();
                }
                catch (Exception) { }
                try { DescribeStationAt(k, snap); } catch (Exception) { }
                snap.Held = NPCInstance.HeldScanFor(k);
                Snap = snap;

                bool jumpedNow = false;
                try { if (k.body != null) jumpedNow = k.body.velocity.y > 2.4f; } catch (Exception) { }
                SampleTrail(snap, jumpedNow);
                ReportLeg(plugin);
                if (_dirty && Time.unscaledTime - _lastSave > SaveMinInterval
                    && Count() - _samplesAtLastSave >= SaveMinNewSamples)
                    Save(_scene);
            }
            catch (Exception) { }
        }

        private static bool IsAlive(UnityEngine.Object o) => o != null;

        private static bool IsRagdolled(Kobold k)
        {
            try { var rd = k.GetRagdoller(); return rd != null && rd.ragdolled; } catch (Exception) { return false; }
        }

        // Station wording for the activity line: closest known-kind usable in reach —
        // scanned whether they're USING it (3m) or just standing by it (2m), with the
        // transform kept so an NPC can target "the station I'm in/by".
        private static void DescribeStationAt(Kobold k, Snapshot s)
        {
            s.StationName = "";
            s.StationKind = "";
            s.StationTf = null;
            float radius = s.InStation ? 3f : 2f;
            var buf = new Collider[32];
            int n;
            try { n = Physics.OverlapSphereNonAlloc(k.transform.position, radius, buf, ~0, QueryTriggerInteraction.Collide); }
            catch (Exception) { return; }
            float bestD = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var c = buf[i];
                if (c == null) continue;
                GenericUsable u = null;
                try { u = c.GetComponent<GenericUsable>() ?? c.GetComponentInParent<GenericUsable>(); } catch (Exception) { continue; }
                if (u == null) continue;
                float d = Vector3.Distance(u.transform.position, k.transform.position);
                if (d >= bestD) continue;
                bestD = d;
                s.StationKind = NPCInstance.ClassifyUsable(NPCInstance.CleanName(u.name));
                s.StationName = NPCInstance.CleanName(u.name);
                s.StationTf = u.transform;
            }
        }

        // ------------------------------------------------------------------
        // sampling the player's actual walked path
        // ------------------------------------------------------------------
        private static void SampleTrail(Snapshot s, bool jumpedNow)
        {
            lock (_lock)
            {
                if (!_legInit)
                {
                    _legAnchor = s.Pos;
                    _legJumped = false;
                    _legInit = true;
                    _lastPt = s.Pos;
                    AddPoint(s.Pos, false, s.SampleTime);
                    return;
                }
                float horiz = Vector2.Distance(new Vector2(_lastPt.x, _lastPt.z), new Vector2(s.Pos.x, s.Pos.z));
                if (horiz >= MinPointSpacing || (jumpedNow && horiz >= 0.3f))
                {
                    AddPoint(s.Pos, jumpedNow, s.SampleTime);
                    _lastPt = s.Pos;
                    if (jumpedNow) _legJumped = true;
                }
            }
        }

        private static void AddPoint(Vector3 p, bool jump, float t)
        {
            try { NoteDoorNear(p); } catch (Exception) { }
            _pts.Add(new TrailPoint { Pos = p, Jump = jump, T = t });
            if (_pts.Count >= TrailCap) _pts.RemoveRange(0, 600); // keep recent history
            _dirty = true;
        }

        // A door usable within ~1.6m of the walked point is the "how they left" cue.
        private static void NoteDoorNear(Vector3 pos)
        {
            var buf = new Collider[24];
            int n = Physics.OverlapSphereNonAlloc(pos, 1.6f, buf, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var c = buf[i];
                if (c == null) continue;
                GenericUsable u = null;
                try { u = c.GetComponent<GenericUsable>() ?? c.GetComponentInParent<GenericUsable>(); } catch (Exception) { continue; }
                if (u == null) continue;
                if (NPCInstance.ClassifyUsable(NPCInstance.CleanName(u.name)) == "door")
                {
                    _lastDoorName = NPCInstance.CleanName(u.name);
                    return;
                }
            }
        }

        // ------------------------------------------------------------------
        // route narration: how the player is moving, fanned to every instance
        // ------------------------------------------------------------------
        private static void ReportLeg(LLMNPCPlugin plugin)
        {
            string ev;
            lock (_lock)
            {
                if (!_legInit) return;
                Vector3 delta = _lastPt - _legAnchor;
                if (delta.magnitude < LegReportDistance) return;
                ev = "ROUTE WATCH: your player is " + (_legJumped ? "hopping/parkouring" : "walking") + " "
                    + Compass.NameOfOffset(delta.x, delta.z) + " about " + M(delta.magnitude) + "m "
                    + (_lastDoorName != null ? "(through the " + _lastDoorName + ") " : "")
                    + "— they demonstrated this route; if go_to can't route there itself, it copies their trail (jumps included).";
                _legAnchor = _lastPt;
                _legJumped = false;
                _lastDoorName = null;
            }
            try { plugin.FanoutWorldEvent(ev); } catch (Exception) { }
        }

        private static string M(float v)
        {
            return v.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------
        // routing: copy the player's demonstrated path between two anchors
        // ------------------------------------------------------------------
        // Waypoints from the trail point nearest `start` to the one nearest `goal`
        // (walked order). A flagged point means: JUMP to reach it — the follower
        // replays it as a hop burst. Callers prepend start / append goal themselves.
        internal static bool TryPath(Vector3 start, Vector3 goal, out List<Vector3> pts, out List<bool> jumps)
        {
            pts = null; jumps = null;
            TrailPoint[] copy;
            lock (_lock)
            {
                if (_pts.Count < 6) return false;
                copy = _pts.ToArray();
            }
            int a = NearestIndex(copy, start, 3f);
            int b = NearestIndex(copy, goal, 4f);
            if (a < 0 || b < 0 || Math.Abs(a - b) < 2) return false;

            int dir = a <= b ? 1 : -1;
            var outp = new List<Vector3>();
            var outj = new List<bool>();
            Vector3 lastKept = copy[a].Pos;
            bool first = true;
            for (int i = a; i != b + dir; i += dir)
            {
                var p = copy[i].Pos;
                if (first || (p - lastKept).sqrMagnitude > 1.2f * 1.2f)
                {
                    outp.Add(p);
                    outj.Add(copy[i].Jump);
                    lastKept = p;
                    first = false;
                }
                if (outp.Count > 400) return false;
            }
            if (outp.Count < 2) return false;
            pts = outp;
            jumps = outj;
            return true;
        }

        private static int NearestIndex(TrailPoint[] pts, Vector3 p, float maxDist)
        {
            int best = -1;
            float bd = maxDist * maxDist;
            float now = Time.unscaledTime;
            for (int i = pts.Length - 1; i >= 0; i--)
            {
                var pt = pts[i];
                if (pt.T > 0f && now - pt.T > TrailFreshSeconds) break; // samples are chronological
                float d = (pt.Pos - p).sqrMagnitude;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        // ------------------------------------------------------------------
        // persistence (one trail file per scene)
        // ------------------------------------------------------------------
        private static string TrailPathFor(string scene)
        {
            try
            {
                string data = Application.dataPath;
                if (!string.IsNullOrEmpty(data))
                {
                    string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(data), "BepInEx", "config", "kkllmnpc_maps");
                    return System.IO.Path.Combine(dir, "trail_" + FileSafe(scene) + ".txt");
                }
            }
            catch (Exception) { }
            return null;
        }

        private static string FileSafe(string s)
        {
            var sb = new StringBuilder((s ?? "scene").Length);
            foreach (char c in s ?? "scene") sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        private static void Save(string scene)
        {
            _lastSave = Time.unscaledTime;
            lock (_lock) _dirty = false;
            string file = TrailPathFor(scene);
            if (file == null) return;

            // Snapshot the points ON the calling (main) thread — never copy there —
            // then do ALL file I/O on the thread pool. Unity main-thread file writes
            // on a share stall the frame (freeze) on network hiccups; the serialize
            // cost is a few ms, so only the WriteAllText/Delete/Move trip goes async.
            TrailPoint[] copy;
            lock (_lock) copy = _pts.ToArray();
            _samplesAtLastSave = copy.Length;

            if (System.Threading.Interlocked.CompareExchange(ref _writeInFlight, 1, 0) != 0)
                return; // a previous write still pending — it will supersede on the next gate
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    var sb = new StringBuilder(copy.Length * 32 + 64);
                    sb.Append("KKTRAIL1\n").Append(scene).Append('\n');
                    foreach (var p in copy)
                        sb.Append(p.Pos.x.ToString("0.###", ci)).Append(' ')
                          .Append(p.Pos.y.ToString("0.###", ci)).Append(' ')
                          .Append(p.Pos.z.ToString("0.###", ci)).Append(' ')
                          .Append(p.Jump ? "1" : "0").Append('\n');
                    string tmp = file + ".tmp";
                    System.IO.File.WriteAllText(tmp, sb.ToString());
                    if (System.IO.File.Exists(file)) try { System.IO.File.Delete(file); } catch (Exception) { }
                    System.IO.File.Move(tmp, file);
                }
                catch (Exception) { }
                finally { System.Threading.Interlocked.Exchange(ref _writeInFlight, 0); }
            });
        }

        private static void Load(string scene)
        {
            lock (_lock) { _pts.Clear(); _dirty = false; }
            string file = TrailPathFor(scene);
            if (file == null || !System.IO.File.Exists(file)) return;
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var line in System.IO.File.ReadAllLines(file))
                {
                    string l = line.Trim();
                    if (l.Length == 0 || l.StartsWith("KKTRAIL1", StringComparison.Ordinal) || l == scene) continue;
                    var tok = l.Split(' ');
                    if (tok.Length < 4) continue;
                    float x, y, z;
                    if (!float.TryParse(tok[0], System.Globalization.NumberStyles.Float, ci, out x)) continue;
                    if (!float.TryParse(tok[1], System.Globalization.NumberStyles.Float, ci, out y)) continue;
                    if (!float.TryParse(tok[2], System.Globalization.NumberStyles.Float, ci, out z)) continue;
                    lock (_lock)
                    {
                        _pts.Add(new TrailPoint { Pos = new Vector3(x, y, z), Jump = tok[3] == "1", T = -1f });
                        if (_pts.Count >= TrailCap) break;
                    }
                }
                lock (_lock) if (_pts.Count > 0) _lastPt = _pts[_pts.Count - 1].Pos;
                LLMNPCPlugin.Log?.LogInfo("KKLLMNPC: player trail loaded for '" + scene + "' (" + Count() + " samples)");
            }
            catch (Exception) { }
        }

        internal static int Count() { lock (_lock) return _pts.Count; }
    }
}