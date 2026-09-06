// 3D layered A* pathfinding on a local walkability grid. Physics queries must
// run on the main thread, so FindPath() is called via RunOnMainThread from
// ToolGoTo. Instead of a flat floor plane, every grid cell is sampled with a
// downward ray for up to MaxLayers DISTINCT floor elevations (stairs, ramps,
// stacked floors, basements/attics). A* connects two adjacent cells only when
// the target floor is not higher than the current one by more than ClimbStep —
// climbing above that is forbidden, while dropping DOWN any distance is allowed
// (walking off a ledge is fine and non-damaging). Up-steps cost a little extra
// so the search prefers to stay on one level. Waypoints carry each cell's real
// ground height, so paths genuinely rise, fall and cross floors with the level.
// If the exact goal cell is unwalkable, a small near-miss ring is searched for
// the nearest passable cell so the path still ends at the goal's front door.
// Everything is bounded by a node budget: too big / too far / no route => null,
// and ToolGoTo falls back to direct straight-line steering.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KKLLMNPC
{
    internal partial class NPCInstance
    {
        // Reusable collider buffer for non-allocating overlap checks.
        internal Collider[] _pathColliderBuf = new Collider[32];

        // Build a grid over the start->goal window and run layered A*. Returns a
        // list of world-space waypoints (each at its floor's real height, the
        // final one being the goal or its nearest passable landing), or null.
        // Main-thread only.
        private List<Vector3> FindPath(Vector3 start, Vector3 goal)
        {
            if (!IsAlive(_kobold)) return null;
            _pathDoorBlocked = false;
            float cell = Mathf.Max(0.25f, _cfgPathCell.Value);
            float span = Mathf.Max(4f, _cfgPathSpan.Value);
            int cap = Mathf.Max(200, _cfgPathCap.Value);

            float refY = start.y;

            // Window centered on the segment, expanded by `span`.
            float hw = Mathf.Abs(goal.x - start.x) * 0.5f + span;
            float hd = Mathf.Abs(goal.z - start.z) * 0.5f + span;
            float minX = (start.x + goal.x) * 0.5f - hw;
            float minZ = (start.z + goal.z) * 0.5f - hd;

            int cols = Mathf.Max(2, Mathf.CeilToInt(hw * 2f / cell));
            int rows = Mathf.Max(2, Mathf.CeilToInt(hd * 2f / cell));
            // Respect the node budget by inflating the cell size.
            while ((long)cols * rows > cap)
            {
                cell *= 1.15f;
                cols = Mathf.Max(2, Mathf.CeilToInt(hw * 2f / cell));
                rows = Mathf.Max(2, Mathf.CeilToInt(hd * 2f / cell));
            }

            int sx = Mathf.Clamp(Mathf.FloorToInt((start.x - minX) / cell), 0, cols - 1);
            int szz = Mathf.Clamp(Mathf.FloorToInt((start.z - minZ) / cell), 0, rows - 1);
            int gx = Mathf.Clamp(Mathf.FloorToInt((goal.x - minX) / cell), 0, cols - 1);
            int gz = Mathf.Clamp(Mathf.FloorToInt((goal.z - minZ) / cell), 0, rows - 1);

            var grid = new PathGridState(cols, rows, cell, minX, minZ, refY, this);

            // Start: nearest real floor under the body, or seed a synthetic layer
            // at the exact body height if none matches (own colliders are skipped
            // by the sampler so a cramped/near-wall spawn still grounds).
            grid.SampleCell(sx, szz);
            int sl = grid.LayerNear(grid.Ci(sx, szz), refY);
            if (sl < 0) sl = grid.ForceLayer(sx, szz, refY);
            if (sl < 0) { Logger.LogInfo("path: null — start cell " + sx + "," + szz + " has no floor"); return null; }

            // Goal: exact cell on the floor nearest our elevation, else a landed
            // near-miss cell a few steps out (so a goal jammed against geometry
            // still routes us to the closest passable approach).
            int gc, gl;
            Vector3 goalPoint;
            if (!ResolveGoal(goal, grid, gx, gz, refY, out gc, out gl, out goalPoint))
            {
                Logger.LogInfo("path: null — no floor in goal cell or its 6-cell ring (" + gx + "," + gz + ")");
                return null;
            }

            var a = new Astar(cols, rows, gc, gl, grid);
            a.Expand(sx, szz, sl);
            _pathDoorBlocked = a.CrossedDoor;
            int[] nodes;
            if (!a.Build(out nodes))
            {
                Logger.LogInfo("path: null — A* expanded " + a.ExpandedCount + " nodes, no route to goal cell");
                return null;
            }
            if (nodes.Length < 1) return null;

            var way = new List<Vector3>();
            way.Add(start); // start the path at the actual body position
            for (int i = 1; i < nodes.Length; i++)
            {
                int ci = nodes[i] / PathGridState.MaxLayers;
                int li = nodes[i] % PathGridState.MaxLayers;
                way.Add(grid.CellPos(ci, li));
            }
            if (way.Count == 1) way.Add(goalPoint);
            else way[way.Count - 1] = goalPoint;

            // String-pull smoothing: try to jump straight from each kept waypoint
            // to the furthest visible one, dropping intermediate posts. The ray
            // rides 0.75m above each waypoint's own ground height, so it stays
            // clear through ramps/staircases.
            var outWay = new List<Vector3>();
            int i2 = 0;
            int n2 = way.Count;
            outWay.Add(way[0]);
            while (i2 < n2 - 1)
            {
                int j = n2 - 1;
                for (; j > i2 + 1; j--)
                    if (RayClear(way[i2], way[j])) break;
                outWay.Add(way[j]);
                i2 = j;
            }
            if (outWay.Count >= 2 && outWay[outWay.Count - 1] == outWay[outWay.Count - 2])
                outWay.RemoveAt(outWay.Count - 1);
            return outWay.Count >= 2 ? outWay : null;
        }

        // Chest-height line-of-sight between two waypoints, each orbited at its own
        // floor height + 0.75m so sloped/ramped segments stay above the ground.
        private bool RayClear(Vector3 a, Vector3 b)
        {
            Vector3 p = new Vector3(a.x, a.y + 0.75f, a.z);
            Vector3 q = new Vector3(b.x, b.y + 0.75f, b.z);
            Vector3 dir = q - p;
            float dist = dir.magnitude;
            if (dist < 0.01f) return true;
            RaycastHit h;
            return !Physics.Raycast(p, dir / dist, out h, dist, ~0, QueryTriggerInteraction.Ignore)
                || IsOwnCollider(h.collider);
        }

        // Pick the goal cell/layer. Prefers the exact cell on the floor closest to
        // our elevation; otherwise scans a ring out to 6 cells for the nearest
        // passable landing. Returns the final approach point (projected goal, or
        // the landed cell center for a near-miss).
        private bool ResolveGoal(Vector3 goal, PathGridState grid, int gx, int gz, float refY,
            out int gc, out int gl, out Vector3 goalPoint)
        {
            gc = -1; gl = -1; goalPoint = Vector3.zero;
            grid.SampleCell(gx, gz);
            int l = grid.LayerNear(grid.Ci(gx, gz), refY);
            if (l >= 0)
            {
                gc = grid.Ci(gx, gz);
                gl = l;
                goalPoint = new Vector3(goal.x, grid.H(gc, l), goal.z);
                return true;
            }
            int bestCi = -1, bestL = -1;
            float bestDh = 1e12f;
            for (int r = 1; r <= 6 && bestCi < 0; r++)
            {
                // Ring cells: top/bottom rows, left/right columns.
                for (int d = -r; d <= r; d++)
                {
                    int c1, c2, c3, c4; int l1, l2, l3, l4;
                    bool b1 = NearCell(grid, gx + d, gz - r, refY, out c1, out l1);
                    bool b2 = NearCell(grid, gx + d, gz + r, refY, out c2, out l2);
                    bool b3 = NearCell(grid, gx - r, gz + d, refY, out c3, out l3);
                    bool b4 = NearCell(grid, gx + r, gz + d, refY, out c4, out l4);
                    if (b1 && AbsDiff(grid.H(c1, l1) - refY) < bestDh) { bestDh = AbsDiff(grid.H(c1, l1) - refY); bestCi = c1; bestL = l1; }
                    if (b2 && AbsDiff(grid.H(c2, l2) - refY) < bestDh) { bestDh = AbsDiff(grid.H(c2, l2) - refY); bestCi = c2; bestL = l2; }
                    if (b3 && AbsDiff(grid.H(c3, l3) - refY) < bestDh) { bestDh = AbsDiff(grid.H(c3, l3) - refY); bestCi = c3; bestL = l3; }
                    if (b4 && AbsDiff(grid.H(c4, l4) - refY) < bestDh) { bestDh = AbsDiff(grid.H(c4, l4) - refY); bestCi = c4; bestL = l4; }
                }
            }
            if (bestCi < 0) return false;
            gc = bestCi; gl = bestL;
            goalPoint = grid.CellPos(gc, gl);
            return true;
        }

        private bool NearCell(PathGridState grid, int cx, int cz, float refY, out int ci, out int l)
        {
            ci = 0; l = -1;
            if (cx < 0 || cz < 0 || cx >= grid.Cols() || cz >= grid.Rows()) return false;
            int c = grid.Ci(cx, cz);
            grid.SampleCell(c);
            l = grid.LayerNear(c, refY);
            if (l < 0) return false;
            ci = c;
            return true;
        }

        private static float AbsDiff(float v) { return v < 0f ? -v : v; }
    }

    // Layered walkability grid (physics hops are memoized per cell). Each cell
    // caches up to MaxLayers distinct floor elevations with their clearance flag,
    // discovered by one downward multi-hit ray per cell.
    internal class PathGridState
    {
        public const int MaxLayers = 4;
        public const float ClimbStep = 0.5f;
        // Cell clearance: 1 = open floor, 2 = solid obstacle, 3 = a CLOSED door
        // here (walkable in the planner but strongly penalized — the body opens it
        // on arrival via Movement.MaybeOpenDoorAhead). An open door's panel is not
        // in the way, so its cell reads as plain 1.
        public const byte OkClear = 1;
        public const byte OkWall = 2;
        public const byte OkDoor = 3;
        private static bool Passable(byte b) { return b == OkClear || b == OkDoor; }

        private int _cols, _rows;
        private float _cell, _minX, _minZ, _refY;
        private NPCInstance _npc;
        private float[] _h;   // floor height per node (ci*MaxLayers+l); -999 invalid
        private byte[] _ok;   // per node: 0 unsampled / 1 clear / 2 blocked / 3 door
        private byte[] _nlay; // number of sampled layers per cell
        private RaycastHit[] _rayHits = new RaycastHit[128];
        private readonly List<float> _tmpYs = new List<float>();
        private readonly List<float> _layHs = new List<float>();

        public PathGridState(int cols, int rows, float cell, float minX, float minZ, float refY, NPCInstance npc)
        {
            _cols = cols; _rows = rows; _cell = cell; _minX = minX; _minZ = minZ; _refY = refY; _npc = npc;
            int N = cols * rows * MaxLayers;
            _h = new float[N];
            _ok = new byte[N];
            _nlay = new byte[cols * rows];
            for (int i = 0; i < N; i++) _h[i] = -999f;
        }

        public int Cols() { return _cols; }
        public int Rows() { return _rows; }
        public int Ci(int x, int z) { return z * _cols + x; }
        public int CellX(int ci) { return ci % _cols; }
        public int CellZ(int ci) { return ci / _cols; }
        public int Node(int ci, int l) { return ci * MaxLayers + l; }
        public int LayerCount(int ci) { return _nlay[ci]; }
        public float H(int ci, int l) { return _h[Node(ci, l)]; }

        public Vector3 CellPos(int ci, int l)
        {
            return new Vector3(_minX + (CellX(ci) + 0.5f) * _cell, H(ci, l), _minZ + (CellZ(ci) + 0.5f) * _cell);
        }

        // Discover the cell's floor elevations by one downward multi-hit ray.
        // Own colliders, triggers, doors and near-vertical surfaces are skipped;
        // surviving hits are clustered (gaps <= ClimbStep) into layers. Each layer
        // is then chest-clearance checked at its own height.
        public void SampleCell(int ci)
        {
            int x = CellX(ci), z = CellZ(ci);
            SampleCell(x, z);
        }

        public void SampleCell(int x, int z)
        {
            int ci = Ci(x, z);
            if (_nlay[ci] != 0) return; // already sampled
            float cx = _minX + (x + 0.5f) * _cell;
            float cz = _minZ + (z + 0.5f) * _cell;

            int nh = Physics.RaycastNonAlloc(new Vector3(cx, _refY + 12f, cz), Vector3.down,
                _rayHits, 20f, ~0, QueryTriggerInteraction.Ignore);
            _tmpYs.Clear();
            for (int i = 0; i < nh; i++)
            {
                RaycastHit h = _rayHits[i];
                if (h.collider == null) continue;
                if (_npc.IsOwnCollider(h.collider)) continue;
                if (h.normal.y < 0.35f) continue; // wall / near-vertical side, not a floor
                if (h.point.y < _refY - 8f || h.point.y > _refY + 12f) continue;
                var u = h.collider.GetComponent<GenericUsable>() ?? h.collider.GetComponentInParent<GenericUsable>();
                if (u != null && NPCInstance.ClassifyUsable(NPCInstance.CleanName(u.name)).Contains("door")) continue;
                _tmpYs.Add(h.point.y);
            }

            _tmpYs.Sort();
            _layHs.Clear();
            for (int i = 0; i < _tmpYs.Count; i++)
            {
                float y = _tmpYs[i];
                if (_layHs.Count > 0 && y - _layHs[_layHs.Count - 1] <= ClimbStep)
                    _layHs[_layHs.Count - 1] = y; // topmost surface of the merged cluster
                else if (_layHs.Count < MaxLayers)
                    _layHs.Add(y);
            }
            _tmpYs.Clear();
            int nl = _layHs.Count;
            if (nl == 0) return;

            for (int l = 0; l < nl; l++)
            {
                float fh = _layHs[l];
                int b = Node(ci, l);
                _h[b] = fh;
                int n = Physics.OverlapSphereNonAlloc(new Vector3(cx, fh + 0.75f, cz), 0.32f,
                    _npc._pathColliderBuf, ~0, QueryTriggerInteraction.Ignore);
                bool clear = true;
                bool doorOnly = false;
                for (int k = 0; k < n && clear; k++)
                {
                    var c = _npc._pathColliderBuf[k];
                    if (c == null || _npc.IsOwnCollider(c)) continue;
                    var u = c.GetComponent<GenericUsable>() ?? c.GetComponentInParent<GenericUsable>();
                    if (u != null && NPCInstance.ClassifyUsable(NPCInstance.CleanName(u.name)).Contains("door"))
                    {
                        // Closed door panel actually inside this cell. Not a hard
                        // wall: remember it so the layer is crossable-at-a-cost.
                        doorOnly = true;
                        continue;
                    }
                    clear = false;
                }
                _ok[b] = clear
                    ? (doorOnly ? OkDoor : OkClear)
                    : OkWall;
            }
            _nlay[ci] = (byte)nl;
        }

        // Seed an extra layer at an explicit height (for the start cell). No-op if
        // a layer already exists within ClimbStep. Returns the layer index or -1.
        public int ForceLayer(int x, int z, float y)
        {
            int ci = Ci(x, z);
            SampleCell(ci);
            for (int l = 0; l < _nlay[ci]; l++)
                if (Abs(_h[Node(ci, l)] - y) <= ClimbStep) return l;
            if (_nlay[ci] >= MaxLayers) return -1;
            int b = Node(ci, _nlay[ci]);
            _h[b] = y; _ok[b] = OkClear;
            _nlay[ci] += 1;
            return _nlay[ci] - 1;
        }

        // Walkable layer closest in height to `y` (elevation tie-break), or -1.
        public int LayerNear(int ci, float y)
        {
            SampleCell(ci);
            int best = -1; float bd = 1e9f;
            for (int l = 0; l < _nlay[ci]; l++)
            {
                int b = Node(ci, l);
                if (!Passable(_ok[b])) continue;
                float d = Abs(_h[b] - y);
                if (d < bd) { bd = d; best = l; }
            }
            return best;
        }

        // Layer in a neighboring cell reachable from a given floor: walkable and
        // not more than ClimbStep ABOVE us (dropping down is always allowed).
        // Picks the closest-in-height layer. Returns index or -1.
        public int BestLayer(int ci, float floorA)
        {
            SampleCell(ci);
            int best = -1; float bd = 1e9f;
            for (int l = 0; l < _nlay[ci]; l++)
            {
                int b = Node(ci, l);
                if (!Passable(_ok[b])) continue; // closed doors count as walkable here
                float dy = _h[b] - floorA;
                if (dy > ClimbStep + 0.001f) continue; // too tall to step up
                float d = Abs(dy);
                if (d < bd) { bd = d; best = l; }
            }
            return best;
        }

        public bool IsDoorNode(int node)
        {
            return _ok[node] == OkDoor;
        }

        public bool LayerOk(int ci, int l)
        {
            int b = Node(ci, l);
            return l >= 0 && l < _nlay[ci] && Passable(_ok[b]);
        }

        private static float Abs(float v) { return v < 0f ? -v : v; }
    }

    // Layered A* over the grid (pure CPU, no physics). Node = cell*MaxLayers +
    // layer. Vertical rule lives in PathGridState.BestLayer; here we additionally
    // prevent diagonal corner-cuts, add a small cost for stepping upward so the
    // path prefers a level route, and charge a heavy toll for walking THROUGH a
    // closed door cell — a detour of a few cells is always preferred, but when a
    // closed door is the only way through, the path still crosses it (the body
    // opens it on arrival).
    internal class Astar
    {
        private const int DoorCrossCost = 550; // ~5.5 cells; cheaper than a long detour
        private int _cols, _rows;
        private int _goalCi, _goalL;
        private PathGridState _grid;
        private int[] _gScore;
        private int[] _cameFrom;
        private bool[] _closed;
        private List<float> _heapF = new List<float>(1024);
        private List<int> _heapIdx = new List<int>(1024);

        public Astar(int cols, int rows, int goalCi, int goalL, PathGridState grid)
        {
            _cols = cols; _rows = rows; _goalCi = goalCi; _goalL = goalL; _grid = grid;
            int N = cols * rows * PathGridState.MaxLayers;
            _gScore = new int[N]; _cameFrom = new int[N]; _closed = new bool[N];
            for (int i = 0; i < N; i++) { _gScore[i] = int.MaxValue; _cameFrom[i] = -1; }
        }

        private void HeapPush(int idx, float f)
        {
            _heapF.Add(f); _heapIdx.Add(idx);
            int k = _heapF.Count - 1;
            while (k > 0)
            {
                int p = (k - 1) / 2;
                if (_heapF[p] <= _heapF[k]) break;
                float tf = _heapF[p]; _heapF[p] = _heapF[k]; _heapF[k] = tf;
                int ti = _heapIdx[p]; _heapIdx[p] = _heapIdx[k]; _heapIdx[k] = ti;
                k = p;
            }
        }

        private int HeapPop()
        {
            if (_heapF.Count == 0) return -1;
            int top = _heapIdx[0];
            int last = _heapF.Count - 1;
            if (last == 0) { _heapF.RemoveAt(0); _heapIdx.RemoveAt(0); return top; }
            _heapF[0] = _heapF[last]; _heapIdx[0] = _heapIdx[last];
            _heapF.RemoveAt(last); _heapIdx.RemoveAt(last);
            int k = 0;
            while (true)
            {
                int l = k * 2 + 1, r = l + 1, m = k;
                if (l < _heapF.Count && _heapF[l] < _heapF[m]) m = l;
                if (r < _heapF.Count && _heapF[r] < _heapF[m]) m = r;
                if (m == k) break;
                float tf = _heapF[m]; _heapF[m] = _heapF[k]; _heapF[k] = tf;
                int ti = _heapIdx[m]; _heapIdx[m] = _heapIdx[k]; _heapIdx[k] = ti;
                k = m;
            }
            return top;
        }

        private float H(int x, int z) // octile distance in cost units (1 per cell, diag ~1.41)
        {
            int dx = Mathf.Abs(x - _grid.CellX(_goalCi)), dz = Mathf.Abs(z - _grid.CellZ(_goalCi));
            return (Mathf.Min(dx, dz) * 1.41421356f + Mathf.Abs(dx - dz)) * 100f;
        }

        public int ExpandedCount = 0;
        public bool CrossedDoor = false;

        public void Expand(int sx, int sz, int sl)
        {
            int startNode = _grid.Node(_grid.Ci(sx, sz), sl);
            int goalNode = _grid.Node(_goalCi, _goalL);
            _gScore[startNode] = 0;
            HeapPush(startNode, H(sx, sz));

            int N = _cols * _rows * PathGridState.MaxLayers;
            int nodeBudget = Math.Max(2048, N);
            nodeBudget = Math.Min(N * 2, nodeBudget);
            int expanded = 0;
            ExpandedCount = 0;
            CrossedDoor = false;

            int[] dxs = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] dzs = { 0, 0, 1, -1, 1, -1, 1, -1 };
            float[] dcost = { 1f, 1f, 1f, 1f, 1.41421356f, 1.41421356f, 1.41421356f, 1.41421356f };

            while (_heapF.Count > 0 && expanded < nodeBudget)
            {
                int cur = HeapPop();
                if (_closed[cur]) continue;
                _closed[cur] = true;
                expanded++;
                if (cur == goalNode) break;

                int ci = cur / PathGridState.MaxLayers;
                int l = cur % PathGridState.MaxLayers;
                int cx = ci % _cols, cz = ci / _cols;
                float floorA = _grid.H(ci, l);

                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + dxs[d], nz = cz + dzs[d];
                    if (nx < 0 || nz < 0 || nx >= _cols || nz >= _rows) continue;
                    // No cutting corners on diagonals: both cardinals must be
                    // reachable from this floor first.
                    if (dxs[d] != 0 && dzs[d] != 0)
                    {
                        if (_grid.BestLayer(_grid.Ci(cx + dxs[d], cz), floorA) < 0) continue;
                        if (_grid.BestLayer(_grid.Ci(cx, cz + dzs[d]), floorA) < 0) continue;
                    }
                    int nbci = _grid.Ci(nx, nz);
                    int l2 = _grid.BestLayer(nbci, floorA);
                    if (l2 < 0) continue;
                    int nid = _grid.Node(nbci, l2);
                    if (_closed[nid]) continue;
                    float dy = _grid.H(nbci, l2) - floorA;
                    int add = (int)Math.Round(dcost[d] * 100f)
                              + (int)Math.Round(Mathf.Max(0f, dy) * 20f);
                    if (_grid.IsDoorNode(nid))
                    {
                        add += DoorCrossCost;
                        CrossedDoor = true;
                    }
                    int ng = _gScore[cur] + add;
                    if (ng < _gScore[nid])
                    {
                        _gScore[nid] = ng;
                        _cameFrom[nid] = cur;
                        HeapPush(nid, ng + H(nx, nz));
                    }
                }
            }
            ExpandedCount = expanded;
        }

        // Reconstruct the path node chain (start -> goal), or null.
        public bool Build(out int[] nodes)
        {
            nodes = null;
            int goalNode = _grid.Node(_goalCi, _goalL);
            if (_gScore[goalNode] == int.MaxValue) return false;
            var rev = new List<int>();
            int c = goalNode;
            while (c != -1 && rev.Count < 100000) { rev.Add(c); c = _cameFrom[c]; }
            rev.Reverse();
            nodes = rev.ToArray();
            return true;
        }
    }
}