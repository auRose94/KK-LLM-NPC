// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Shared full-scene walkability map, used by ALL agents in the loaded scene.
//
// The per-query local grid (Pathfinding.cs) only covers the start->goal window, so a
// station 40m away across stairs can never be routed. This builds ONE layered walkability
// grid for the whole playable scene, sampled once, shared by every NPCInstance, and
// cached to a file (BepInEx/config/kkllmnpc_maps/<scene>.kkmap) keyed by scene name +
// format version, so the next session (and every other agent/DLL on this machine) loads
// it in milliseconds instead of re-sampling.
//
// Build runs INCREMENTALLY on the main thread (a few dozen cells per frame) so the game
// keeps running while it maps — a big map takes minutes, never a frozen frame. Physics
// sampling (downward multi-hit ray + chest clearance sphere) mirrors PathGridState so
// the two planners agree on what is walkable; dynamic kobolds are treated as
// non-blocking (they move), closed door panels mark a "door" node (crossable at a toll,
// opened on arrival).
//
// Queries (FindPath) are pure CPU A* over the sampled grid — any thread may call them;
// the string-pull smoothing raycasts must run on the main thread, so callers marshal
// FindPathSmoothed through RunOnMainThread (or call it directly from FixedUpdate).
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Photon.Pun;

namespace KKLLMNPC
{
    // Pre-sampled layered grid. Same node layout as PathGridState (cell*MaxLayers+l),
    // but read-only: sampled once at build time, then shared across all agents.
    internal sealed class WorldMapGrid : ILayerGrid
    {
        private readonly int _cols, _rows, _maxLayers;
        private readonly float _cell, _minX, _minZ;
        private readonly byte[] _nlay;  // per cell: sampled layer count
        private readonly float[] _h;   // per node: floor height (-999 = none)
        private readonly byte[] _ok;   // per node: 0 none / 1 clear / 2 wall / 3 door

        public WorldMapGrid(int cols, int rows, float cell, float minX, float minZ, int maxLayers,
            byte[] nlay, float[] h, byte[] ok)
        {
            _cols = cols; _rows = rows; _cell = cell; _minX = minX; _minZ = minZ; _maxLayers = maxLayers;
            _nlay = nlay; _h = h; _ok = ok;
        }

        public int Cols() { return _cols; }
        public int Rows() { return _rows; }
        public int MaxLayerCount() { return _maxLayers; }
        public int CellX(int ci) { return ci % _cols; }
        public int CellZ(int ci) { return ci / _cols; }
        public int Ci(int x, int z) { return z * _cols + x; }
        public int Node(int ci, int l) { return ci * _maxLayers + l; }
        public float H(int ci, int l) { return _h[Node(ci, l)]; }
        public bool IsDoorNode(int node) { return _ok[node] == 3; }

        public Vector3 CellPos(int ci, int l)
        {
            return new Vector3(_minX + (CellX(ci) + 0.5f) * _cell, H(ci, l), _minZ + (CellZ(ci) + 0.5f) * _cell);
        }

        // Walkable layer closest in height to y (any distance), or -1.
        public int LayerNear(int ci, float y)
        {
            int best = -1; float bd = 1e9f;
            for (int l = 0; l < _nlay[ci]; l++)
            {
                int b = Node(ci, l);
                if (_ok[b] != 1 && _ok[b] != 3) continue;
                float d = Mathf.Abs(_h[b] - y);
                if (d < bd) { bd = d; best = l; }
            }
            return best;
        }

        // Neighboring layer reachable from floorA: walkable, at most ClimbStep above,
        // and no bigger a drop than MaxDrop (the body hard-stops on larger falls).
        // Closest-in-height wins.
        public int BestLayer(int ci, float floorA)
        {
            int best = -1; float bd = 1e9f;
            for (int l = 0; l < _nlay[ci]; l++)
            {
                int b = Node(ci, l);
                if (_ok[b] != 1 && _ok[b] != 3) continue;
                float dy = _h[b] - floorA;
                if (dy > PathGridState.ClimbStep + 0.001f) continue;
                if (dy < -PathGridState.MaxDrop) continue;
                float d = Mathf.Abs(dy);
                if (d < bd) { bd = d; best = l; }
            }
            return best;
        }
    }

    // One build for one scene. State machine: Idle -> Building -> Ready | Failed.
    // Tick() samples a bounded batch of cells per call (main thread only).
    internal sealed class WorldMapBuild
    {
        public string Scene;
        public float MinX, MinZ, Cell;
        public int Cols, Rows, MaxLayers;
        public int Cells;
        public bool Ready;
        public bool Failed;
        public string FailReason;
        public float Progress; // 0..1
        public string Source;  // "file" | "built"
        public float BuiltUtc;

        private readonly byte[] _nlay;
        private readonly float[] _h;
        private readonly byte[] _ok;
        private int _nextCell;
        private readonly Collider[] _colBuf = new Collider[32];
        private readonly RaycastHit[] _rayBuf = new RaycastHit[128];
        private readonly List<float> _ys = new List<float>();
        private readonly List<float> _layers = new List<float>();

        public WorldMapBuild(string scene, float minX, float minZ, float cell, int cols, int rows, int maxLayers)
        {
            Scene = scene; MinX = minX; MinZ = minZ; Cell = cell;
            Cols = cols; Rows = rows; MaxLayers = maxLayers;
            Cells = cols * rows;
            _nlay = new byte[Cells];
            _h = new float[Cells * maxLayers];
            _ok = new byte[Cells * maxLayers];
            for (int i = 0; i < _h.Length; i++) _h[i] = -999f;
        }

        // Rehydrate from the file arrays (already cell-major, same layout).
        public WorldMapBuild(string scene, float minX, float minZ, float cell, int cols, int rows, int maxLayers,
            byte[] nlay, float[] h, byte[] ok, float builtUtc)
            : this(scene, minX, minZ, cell, cols, rows, maxLayers)
        {
            Array.Copy(nlay, _nlay, nlay.Length);
            Array.Copy(h, _h, h.Length);
            Array.Copy(ok, _ok, ok.Length);
            Ready = true;
            BuiltUtc = builtUtc;
        }

        public WorldMapGrid ToGrid()
        {
            return new WorldMapGrid(Cols, Rows, Cell, MinX, MinZ, MaxLayers, _nlay, _h, _ok);
        }

        // Array accessors for the file serializer.
        public byte NlayAt(int ci) { return _nlay[ci]; }
        public float HeightAt(int ci, int l) { return _h[ci * MaxLayers + l]; }
        public byte StateAt(int ci, int l) { return _ok[ci * MaxLayers + l]; }

        // Sample one cell's floor layers + clearance. Mirrors PathGridState.SampleCell
        // (same door/normal/kobold rules) but with a GLOBAL reference height so layers
        // are stable across the whole scene and between agents. Main thread only.
        private void SampleCell(int ci, float sampleFromY)
        {
            if (_nlay[ci] != 0) return;
            int x = ci % Cols, z = ci / Cols;
            float cx = MinX + (x + 0.5f) * Cell;
            float cz = MinZ + (z + 0.5f) * Cell;

            int nh = Physics.RaycastNonAlloc(new Vector3(cx, sampleFromY, cz), Vector3.down,
                _rayBuf, 60f, ~0, QueryTriggerInteraction.Ignore);
            _ys.Clear();
            for (int i = 0; i < nh; i++)
            {
                RaycastHit hit = _rayBuf[i];
                if (hit.collider == null) continue;
                if (hit.normal.y < 0.35f) continue; // wall / near-vertical side
                // Dynamic kobolds move — never treat a body as floor or furniture.
                if (hit.collider.GetComponentInParent<Kobold>() != null) continue;
                // Closed door panels are not floors.
                var u = hit.collider.GetComponent<GenericUsable>() ?? hit.collider.GetComponentInParent<GenericUsable>();
                if (u != null && NPCInstance.ClassifyUsable(NPCInstance.CleanName(u.name)).Contains("door")) continue;
                _ys.Add(hit.point.y);
            }

            _ys.Sort();
            _layers.Clear();
            for (int i = 0; i < _ys.Count; i++)
            {
                float y = _ys[i];
                // Surfaces within one step's height merge into a single layer.
                if (_layers.Count > 0 && y - _layers[_layers.Count - 1] <= Consts.PathClimbStep)
                    _layers[_layers.Count - 1] = y;
                else if (_layers.Count < MaxLayers)
                    _layers.Add(y);
            }
            int nl = _layers.Count;
            if (nl == 0) return;

            for (int l = 0; l < nl; l++)
            {
                float fh = _layers[l];
                int b = ci * MaxLayers + l;
                _h[b] = fh;
                int n = Physics.OverlapSphereNonAlloc(new Vector3(cx, fh + Consts.PathRayHeightOffset, cz), 0.32f, _colBuf, ~0, QueryTriggerInteraction.Ignore);
                bool clear = true;
                bool doorOnly = false;
                for (int k = 0; k < n && clear; k++)
                {
                    Collider c = _colBuf[k];
                    if (c == null) continue;
                    if (c.GetComponentInParent<Kobold>() != null) continue; // bodies move
                    var cu = c.GetComponent<GenericUsable>() ?? c.GetComponentInParent<GenericUsable>();
                    if (cu != null && NPCInstance.ClassifyUsable(NPCInstance.CleanName(cu.name)).Contains("door"))
                    { doorOnly = true; continue; }
                    clear = false;
                }
                _ok[b] = clear ? (byte)(doorOnly ? 3 : 1) : (byte)2;
            }
            _nlay[ci] = (byte)nl;
        }

        // Sample `budget` cells. Returns false when the build is complete.
        public bool Tick(float sampleFromY, int budget)
        {
            if (Ready || Failed) return true;
            int done = 0;
            while (_nextCell < Cells && done < budget)
            {
                SampleCell(_nextCell, sampleFromY);
                _nextCell++;
                done++;
            }
            Progress = Mathf.Clamp01(Cells > 0 ? (float)_nextCell / (float)Cells : 1f);
            if (_nextCell >= Cells)
            {
                Ready = true;
                Progress = 1f;
            }
            return _nextCell >= Cells;
        }
    }

    // Static shared map for the loaded scene. All instances (and every agent) use the
    // same sampled grid + the same cached file, so pathfinding is uniform across NPCs
    // and the expensive sampling happens once per scene load, not per go_to.
    internal static class WorldMap
    {
        public const int FormatVersion = 1;
        private const string Magic = "KKMAP";

        private static WorldMapBuild _build;      // current scene's build (building or ready)
        private static WorldMapGrid _grid;        // read-only view once Ready
        private static float _sampleFromY = 100f;
        private static readonly object _gate = new object();

        // Config (read by the plugin when starting a build; defaults here keep the
        // class self-sufficient).
        internal static float CellSize = 1.0f;
        internal static float MaxSpan = 800f;
        internal static int MaxLayers = Consts.PathMaxLayers;
        internal static int NodeBudget = 2000000;
        internal static int CellsPerFrame = 48;

        internal static bool Ready { get { lock (_gate) return _build != null && _build.Ready; } }
        internal static bool Building { get { lock (_gate) return _build != null && !_build.Ready && !_build.Failed; } }
        internal static WorldMapBuild Current { get { lock (_gate) return _build; } }

        internal static string StatusText()
        {
            lock (_gate)
            {
                if (_build == null) return "map: idle";
                if (_build.Ready)
                    return "map: ready (" + Fm(_build.Cols * _build.Cell) + "x" + Fm(_build.Rows * _build.Cell) + "m, "
                        + _build.Cells.ToString("0.0\\k") + " cells, " + (_build.Source == "file" ? "cached" : "built this session") + ")";
                if (_build.Failed) return "map: unavailable (" + _build.FailReason + ")";
                return "map: building " + (int)(_build.Progress * 100f) + "% (" + _build.Cells.ToString("0.0\\k") + " cells total)";
            }
        }

        private static string Fm(float v) { return v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture); }

        // Directory for map files: BepInEx/config/kkllmnpc_maps (fallback: plugin dir).
        private static string MapDir()
        {
            try
            {
                string data = Application.dataPath;
                if (!string.IsNullOrEmpty(data))
                {
                    string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(data), "BepInEx", "config", "kkllmnpc_maps");
                    return dir;
                }
            }
            catch (Exception) { }
            try
            {
                string asm = System.IO.Path.GetDirectoryName(typeof(WorldMap).Assembly.Location);
                if (!string.IsNullOrEmpty(asm)) return System.IO.Path.Combine(asm, "kkllmnpc_maps");
            }
            catch (Exception) { }
            return System.IO.Directory.GetCurrentDirectory();
        }

        private static string FileNameFor(string scene)
        {
            string s = scene ?? "scene";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString() + ".kkmap";
        }

        // Collect scene anchors: every usable (stations), every kobold, the player.
        // Main thread only (FindObjectsOfType). Returns false when nothing was found.
        private static bool CollectAnchors(List<Vector3> anchors)
        {
            int before = anchors.Count;
            try
            {
                foreach (var u in UnityEngine.Object.FindObjectsOfType<GenericUsable>())
                {
                    if (u != null && u.transform != null) anchors.Add(u.transform.position);
                }
            }
            catch (Exception) { }
            try
            {
                foreach (var k in UnityEngine.Object.FindObjectsOfType<Kobold>())
                {
                    if (k != null && k.transform != null) anchors.Add(k.transform.position);
                }
            }
            catch (Exception) { }
            try
            {
                if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null)
                    anchors.Add(pp.kobold.transform.position);
            }
            catch (Exception) { }
            return anchors.Count > before;
        }

        // Start (or keep) the map for the active scene. Idempotent: returns the existing
        // build when one already targets this scene. Main thread only.
        internal static void EnsureStarted(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return;
            lock (_gate)
            {
                if (_build != null && string.Equals(_build.Scene, scene, StringComparison.Ordinal))
                    return; // building or ready for this scene
                if (_build != null && !_build.Ready)
                {
                    // Scene changed mid-build — abort the stale build.
                    _build.Failed = true;
                    _build.FailReason = "scene changed";
                    _build = null;
                    _grid = null;
                }
                else
                {
                    // Different scene (or ready map for an old scene) — replace.
                    _build = null;
                    _grid = null;
                }

                var anchors = new List<Vector3>();
                if (!CollectAnchors(anchors))
                {
                    _build = new WorldMapBuild(scene, 0f, 0f, 1f, 0, 0, MaxLayers) { Failed = true, FailReason = "no anchors (scene empty?)" };
                    return;
                }

                // Bounds: encapsulate all anchors + margin, clamp to MaxSpan around
                // the centroid so a huge custom map still maps within the node budget.
                Bounds b = new Bounds(anchors[0], Vector3.zero);
                for (int i = 1; i < anchors.Count; i++) b.Encapsulate(anchors[i]);
                float margin = 15f;
                float minX = b.min.x - margin, maxX = b.max.x + margin;
                float minZ = b.min.z - margin, maxZ = b.max.z + margin;
                float cx = (minX + maxX) * 0.5f, cz = (minZ + maxZ) * 0.5f;
                if (maxX - minX > MaxSpan) { minX = cx - MaxSpan * 0.5f; maxX = cx + MaxSpan * 0.5f; }
                if (maxZ - minZ > MaxSpan) { minZ = cz - MaxSpan * 0.5f; maxZ = cz + MaxSpan * 0.5f; }

                // Cell size: start at the configured size, inflate until the node
                // budget holds (cell^2 * area * layers <= NodeBudget).
                float cell = Mathf.Clamp(CellSize, 0.25f, 4f);
                int cols = Mathf.Max(2, Mathf.CeilToInt((maxX - minX) / cell));
                int rows = Mathf.Max(2, Mathf.CeilToInt((maxZ - minZ) / cell));
                while ((long)cols * rows * MaxLayers > NodeBudget && cell < 4f)
                {
                    cell *= 1.2f;
                    cols = Mathf.Max(2, Mathf.CeilToInt((maxX - minX) / cell));
                    rows = Mathf.Max(2, Mathf.CeilToInt((maxZ - minZ) / cell));
                }

                // Try the cache FIRST — a previous session may have already built this.
                string dir = MapDir();
                string file = System.IO.Path.Combine(dir, FileNameFor(scene));
                if (TryLoad(file, scene, out WorldMapBuild loaded))
                {
                    _build = loaded;
                    _grid = _build.ToGrid();
                    LLMNPCPlugin.Log?.LogInfo("KKLLMNPC: world map loaded from cache: " + file
                        + " (" + _build.Cols + "x" + _build.Rows + " @ " + Fm(_build.Cell) + "m)");
                    return;
                }

                _build = new WorldMapBuild(scene, minX, minZ, cell, cols, rows, MaxLayers) { Source = "built" };
                _sampleFromY = b.max.y + 15f;
                LLMNPCPlugin.Log?.LogInfo("KKLLMNPC: world map build started: scene='" + scene
                    + "' " + cols + "x" + rows + " cells @" + Fm(cell) + "m (span "
                    + Fm(maxX - minX) + "x" + Fm(maxZ - minZ) + "m) — sampling in the background; agents use local pathfinding until ready.");
            }
        }

        // Advance the build by one frame's batch. Main thread. No-op when idle/done.
        internal static void Tick()
        {
            WorldMapBuild b;
            lock (_gate) b = _build;
            if (b == null || b.Ready || b.Failed)
            {
                if (b != null && b.Ready && _grid == null)
                {
                    lock (_gate) _grid = b.ToGrid();
                }
                return;
            }
            b.Tick(_sampleFromY, CellsPerFrame);
            if (b.Ready)
            {
                lock (_gate) _grid = b.ToGrid();
                try
                {
                    string dir = MapDir();
                    string file = System.IO.Path.Combine(dir, FileNameFor(b.Scene));
                    if (Save(dir, file, b))
                        LLMNPCPlugin.Log?.LogInfo("KKLLMNPC: world map saved: " + file
                            + " (" + b.Cols + "x" + b.Rows + " cells) — future sessions load this instantly.");
                }
                catch (Exception e)
                {
                    LLMNPCPlugin.Log?.LogWarning("KKLLMNPC: world map save failed: " + e.Message);
                }
            }
        }

        // Scene changed (or world reloaded): drop the stale build so EnsureStarted
        // rebuilds for the new scene. Main thread.
        // newScene == null forces a clear (left the world). Otherwise clears only
        // when the active scene changed.
        internal static void Invalidate(string newScene)
        {
            lock (_gate)
            {
                if (newScene == null || (_build != null && !string.Equals(_build.Scene, newScene, StringComparison.Ordinal)))
                {
                    if (_build != null) { _build.Failed = true; _build.FailReason = "scene changed"; }
                    _build = null;
                    _grid = null;
                }
            }
        }

        // ------------------------------------------------------------------
        // queries
        // ------------------------------------------------------------------
        internal static bool InBounds(Vector3 p)
        {
            WorldMapBuild b;
            lock (_gate) b = _build;
            if (b == null || !b.Ready) return false;
            return p.x >= b.MinX && p.x < b.MinX + b.Cols * b.Cell
                && p.z >= b.MinZ && p.z < b.MinZ + b.Rows * b.Cell;
        }

        // Walk the perimeter of the ring at radius r around (cx,cz), in cell steps.
        private static void RingCells(int cx, int cz, int r, System.Action<int, int> cb)
        {
            for (int d = -r; d <= r; d++)
            {
                cb(cx + d, cz - r);
                cb(cx + d, cz + r);
            }
            for (int d = -r + 1; d <= r - 1; d++)
            {
                cb(cx - r, cz + d);
                cb(cx + r, cz + d);
            }
        }

        // A* over the shared grid. Pure CPU — any thread. Returns null when the map
        // isn't ready, the points are outside it, or no route exists.
        private static List<Vector3> AstarQuery(Vector3 start, Vector3 goal, WorldMapGrid grid, WorldMapBuild b)
        {
            int sx = Mathf.Clamp(Mathf.FloorToInt((start.x - b.MinX) / b.Cell), 0, b.Cols - 1);
            int sz = Mathf.Clamp(Mathf.FloorToInt((start.z - b.MinZ) / b.Cell), 0, b.Rows - 1);
            int gx = Mathf.Clamp(Mathf.FloorToInt((goal.x - b.MinX) / b.Cell), 0, b.Cols - 1);
            int gz = Mathf.Clamp(Mathf.FloorToInt((goal.z - b.MinZ) / b.Cell), 0, b.Rows - 1);
            int sCi = sz * b.Cols + sx;
            int gCi = gz * b.Cols + gx;

            int sl = grid.LayerNear(sCi, start.y);
            if (sl < 0)
            {
                // Body might be on furniture/geometry the sampler merged differently —
                // try the ring of neighbors before giving up.
                bool found = false;
                for (int r = 1; r <= 2 && !found; r++)
                    RingCells(sx, sz, r, (nx, nz) =>
                    {
                        if (found) return;
                        if (nx < 0 || nz < 0 || nx >= b.Cols || nz >= b.Rows) return;
                        int l = grid.LayerNear(nz * b.Cols + nx, start.y);
                        if (l < 0) return;
                        sx = nx; sz = nz; sCi = nz * b.Cols + nx; sl = l; found = true;
                    });
                if (sl < 0) return null;
            }

            int gl = grid.LayerNear(gCi, goal.y);
            if (gl < 0)
            {
                // Near-miss ring: land at the nearest passable cell (the goal may sit
                // in water/geometry); the path ends at that approach point.
                int bestCi = -1, bestL = -1; float bdh = 1e12f;
                for (int r = 1; r <= 8 && bestCi < 0; r++)
                    RingCells(gx, gz, r, (nx, nz) =>
                    {
                        if (nx < 0 || nz < 0 || nx >= b.Cols || nz >= b.Rows) return;
                        int ci = nz * b.Cols + nx;
                        int l = grid.LayerNear(ci, goal.y);
                        if (l < 0) return;
                        float dh = Mathf.Abs(grid.H(ci, l) - goal.y);
                        if (dh < bdh) { bdh = dh; bestCi = ci; bestL = l; }
                    });
                if (bestCi < 0) return null;
                gCi = bestCi; gl = bestL;
            }

            var a = new Astar(b.Cols, b.Rows, gCi, gl, grid);
            a.Expand(sx, sz, sl);
            int[] nodes;
            if (!a.Build(out nodes) || nodes == null || nodes.Length < 1) return null;

            var way = new List<Vector3>(nodes.Length + 1);
            way.Add(start);
            for (int i = 1; i < nodes.Length; i++)
                way.Add(grid.CellPos(nodes[i] / grid.MaxLayerCount(), nodes[i] % grid.MaxLayerCount()));
            if (way.Count == 1) way.Add(goal);
            else way[way.Count - 1] = new Vector3(goal.x, grid.H(gCi, gl), goal.z);
            return way.Count >= 2 ? way : null;
        }

        // A* + string-pull in one shot. Main thread (smoothing raycasts).
        internal static List<Vector3> FindPathSmoothed(Vector3 start, Vector3 goal)
        {
            WorldMapBuild b; WorldMapGrid g;
            lock (_gate) { b = _build; g = _grid; }
            if (b == null || !b.Ready || g == null) return null;
            if (!InBounds(start) || !InBounds(goal)) return null;
            try
            {
                var way = AstarQuery(start, goal, g, b);
                if (way != null)
                {
                // Ray-clear smoothing (chest height above each post's own floor):
                // from each kept post, keep the FURTHEST still-visible post.
                var outWay = new List<Vector3>();
                int i = 0; int n = way.Count;
                outWay.Add(way[0]);
                while (i < n - 1)
                {
                    int j = n - 1;
                    for (; j > i + 1; j--)
                        if (RayClear(way[i], way[j])) break;
                    outWay.Add(way[j]);
                    i = j;
                }
                    if (outWay.Count >= 2 && outWay[outWay.Count - 1] == outWay[outWay.Count - 2])
                        outWay.RemoveAt(outWay.Count - 1);
                    return outWay.Count >= 2 ? outWay : null;
                }
                return null;
            }
            catch (Exception e)
            {
                LLMNPCPlugin.Log?.LogWarning("KKLLMNPC: world map query: " + e.Message);
                return null;
            }
        }

        private static bool RayClear(Vector3 a, Vector3 b)
        {
            Vector3 p = new Vector3(a.x, a.y + Consts.PathRayHeightOffset, a.z);
            Vector3 q = new Vector3(b.x, b.y + Consts.PathRayHeightOffset, b.z);
            Vector3 dir = q - p;
            float dist = dir.magnitude;
            if (dist < 0.01f) return true;
            RaycastHit h;
            return !Physics.Raycast(p, dir / dist, out h, dist, ~0, QueryTriggerInteraction.Ignore)
                || h.collider != null && h.collider.GetComponentInParent<Kobold>() != null;
        }

        // ------------------------------------------------------------------
        // file format (little-endian)
        // ------------------------------------------------------------------
        // magic(5) | u32 version | u16 sceneLen + utf8 scene
        // | f32 minX | f32 minZ | f32 cell | i32 cols | i32 rows | u32 maxLayers
        // | f64 builtUtc | u32 cellCount
        // | per cell: u8 nlay [ f32 height u8 state ]*nlay
        private static bool Save(string dir, string file, WorldMapBuild b)
        {
            try
            {
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                using (var ms = new System.IO.MemoryStream())
                using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, true))
                {
                    foreach (char c in Magic) w.Write(c);
                    w.Write(FormatVersion);
                    w.Write((ushort)b.Scene.Length);
                    w.Write(b.Scene);
                    w.Write(b.MinX); w.Write(b.MinZ); w.Write(b.Cell);
                    w.Write(b.Cols); w.Write(b.Rows);
                    w.Write((uint)b.MaxLayers);
                    w.Write((double)DateTime.UtcNow.Ticks);
                    w.Write((uint)b.Cells);
                    for (int ci = 0; ci < b.Cells; ci++)
                    {
                        byte nl = ReadNlay(b, ci);
                        w.Write(nl);
                        for (int l = 0; l < nl; l++)
                        {
                            w.Write(ReadH(b, ci, l));
                            w.Write(ReadOk(b, ci, l));
                        }
                    }
                    string tmp = file + ".tmp";
                    WriteAllBytesSafe(tmp, ms.ToArray());
                    if (System.IO.File.Exists(file)) try { System.IO.File.Delete(file); } catch (Exception) { }
                    System.IO.File.Move(tmp, file);
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        // The build's arrays are private; expose them for the serializer.
        private static byte ReadNlay(WorldMapBuild b, int ci) { lock (_gate) return b.NlayAt(ci); }
        private static float ReadH(WorldMapBuild b, int ci, int l) { lock (_gate) return b.HeightAt(ci, l); }
        private static byte ReadOk(WorldMapBuild b, int ci, int l) { lock (_gate) return b.StateAt(ci, l); }

        private static void WriteAllBytesSafe(string path, byte[] data)
        {
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                fs.Write(data, 0, data.Length);
        }

        private static bool TryLoad(string file, string scene, out WorldMapBuild loaded)
        {
            loaded = null;
            try
            {
                if (!System.IO.File.Exists(file)) return false;
                byte[] raw = System.IO.File.ReadAllBytes(file);
                using (var ms = new System.IO.MemoryStream(raw, false))
                using (var r = new System.IO.BinaryReader(ms, System.Text.Encoding.UTF8, true))
                {
                    var magic = new System.Text.StringBuilder(5);
                    for (int i = 0; i < 5; i++) magic.Append((char)r.ReadByte());
                    if (magic.ToString() != Magic) return false;
                    int version = r.ReadInt32();
                    if (version != FormatVersion)
                    {
                        LLMNPCPlugin.Log?.LogInfo("KKLLMNPC: world map cache outdated (v" + version + " < v" + FormatVersion + ") — rebuilding.");
                        return false;
                    }
                    int slen = r.ReadUInt16();
                    string sname = r.ReadString();
                    if (!string.Equals(sname, scene, StringComparison.Ordinal)) return false;
                    float minX = r.ReadSingle();
                    float minZ = r.ReadSingle();
                    float cell = r.ReadSingle();
                    int cols = r.ReadInt32();
                    int rows = r.ReadInt32();
                    int maxLayers = (int)r.ReadUInt32();
                    double builtUtc = r.ReadDouble();
                    uint cells = r.ReadUInt32();
                    if (cols < 2 || rows < 2 || cells == 0 || cells != (uint)(cols * rows) || maxLayers < 1 || maxLayers > 8)
                        return false;
                    var nlay = new byte[cells];
                    var h = new float[cells * maxLayers];
                    var ok = new byte[cells * maxLayers];
                    for (int i = 0; i < h.Length; i++) h[i] = -999f;
                    for (int ci = 0; ci < cells; ci++)
                    {
                        int nl = r.ReadByte();
                        if (nl > maxLayers) return false;
                        nlay[ci] = (byte)nl;
                        for (int l = 0; l < nl; l++)
                        {
                            h[ci * maxLayers + l] = r.ReadSingle();
                            ok[ci * maxLayers + l] = r.ReadByte();
                        }
                    }
                    loaded = new WorldMapBuild(sname, minX, minZ, cell, cols, rows, maxLayers,
                        nlay, h, ok, (float)builtUtc) { Source = "file" };
                    return true;
                }
            }
            catch (Exception) { loaded = null; return false; }
        }
    }
}
