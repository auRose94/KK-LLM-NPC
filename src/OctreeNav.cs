// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// OctreeNav — the volumetric navigation core: a sparse 3D voxel column map with
// true-3D A* over floor nodes, built directly from the sampled/cached grid data.
//
// Why: the layered grid (WorldMapGrid) flattens each column into a small numbered
// layer list and only transitions floors INSIDE one column — fine for a house,
// blind for stacked cave tunnels, shafts you drop through, over/under passes, and
// floor stacks past the layer cap. This module keeps the SAME sampled data (it is
// built FROM the grid arrays — including straight from a cached .kkmap, so the
// on-disk cache becomes the 3D map with no re-sampling) but navigates it
// volumetrically:
//   * every sampled surface becomes a floor voxel (exact height kept for
//     waypoints) in a per-column Y-sorted sparse set — the structure is adaptive
//     in Y exactly like an octree's sparse branches: a column over an open field
//     holds one node, a shaft column holds a stack,
//   * horizontal moves pick the walkable surface in the NEIGHBOR column within
//     [−MaxDrop, +ClimbStep] of the current floor (binary search) — the edge the
//     same-column-only layered graph could never express,
//   * same-column DOWN edges model fall-through holes/shafts (≤ MaxDrop),
//   * wall-sampled voxels go into a packed-key blocking set so cross-floor
//     straight-leg checks see the furniture/ceilings that failed clearance.
//
// A mixed-resolution Morton octree was deliberately NOT built on top: we sample
// surfaces, not solid interiors, so uniform-interior merging has almost nothing
// to compress — the column map is already the sparse form of one.
//
// Pure C#: no UnityEngine, no BepInEx — compiled into the headless test suite
// (tests/test_octreenav.cs) exactly like PathCore. Coordinates are world floats
// at the edges, voxel ints inside. Immutable after Build except PatchWalkable
// (main thread, single writer — same contract as the grid's patch path); A*
// scratch is visit-stamped and guarded by the query lock.
using System;
using System.Collections.Generic;

namespace KKLLMNPC
{
    internal sealed class OctreeNav
    {
        // Node states — the grid's byte states verbatim so patches mean the same.
        public const byte Clear = 1;
        public const byte Wall = 2;
        public const byte Door = 3;

        // ---- geometry ----
        private readonly float _cell, _minX, _minZ;
        private readonly int _cols, _rows;
        private readonly int _climbV, _dropV;   // climb/drop windows in voxel units

        // ---- floor-node store, grouped by column, Y-sorted ----
        private readonly int[] _colStart;       // ci → first node or -1 (grid cell index space)
        private readonly int[] _colLen;
        private readonly float[] _ny;           // exact surface height (waypoints)
        private readonly int[] _nyq;            // voxel Y (topology)
        private readonly byte[] _nst;           // Clear / Wall / Door
        private readonly int[] _nodeCol;        // node → ci (O(1) NodePos / column edges)
        private readonly int _nodes;

        // ---- blocking voxels (wall samples) for straight-leg checks ----
        private readonly HashSet<long> _walls;

        // ---- A* scratch (visit-stamped; guarded by _qlock) ----
        private readonly object _qlock = new object();
        private readonly float[] _g;
        private readonly int[] _from;
        private readonly int[] _stamp;
        private readonly byte[] _closed;
        private readonly int[] _heap;
        private readonly float[] _heapF;
        private readonly int[] _heapIdx;
        private readonly int[] _heapIdxEpoch;
        private int _epoch;

        public int NodeCount { get { return _nodes; } }
        public int ColumnCount { get; private set; }
        public float Cell { get { return _cell; } }

        private OctreeNav(int cols, int rows, float cell, float minX, float minZ,
            float climbStep, float maxDrop, int nodeCount)
        {
            _cols = cols; _rows = rows; _cell = cell; _minX = minX; _minZ = minZ;
            _climbV = Math.Max(1, (int)Math.Ceiling(climbStep / cell));
            _dropV = Math.Max(1, (int)Math.Ceiling(maxDrop / cell));
            _colStart = new int[cols * rows];
            for (int i = 0; i < _colStart.Length; i++) _colStart[i] = -1;
            _colLen = new int[cols * rows];
            _nodes = nodeCount;
            _ny = new float[nodeCount];
            _nyq = new int[nodeCount];
            _nst = new byte[nodeCount];
            _nodeCol = new int[nodeCount];
            _walls = new HashSet<long>();
            _g = new float[nodeCount];
            _from = new int[nodeCount];
            _stamp = new int[nodeCount];
            _closed = new byte[nodeCount];
            _heap = new int[nodeCount + 1];
            _heapF = new float[nodeCount + 1];
            _heapIdx = new int[nodeCount];
            _heapIdxEpoch = new int[nodeCount];
        }

        // ------------------------------------------------------------------
        // build — from the SAME arrays the .kkmap cache stores
        // ------------------------------------------------------------------
        // nlay[ci] = layer count; h[ci*maxLayers+l] / ok[...] = the grid layout.
        // Wall layers join as blocking voxels (unreachable as nodes).
        public static OctreeNav FromLayeredGrid(int cols, int rows, float cell, float minX, float minZ,
            byte[] nlay, float[] h, byte[] ok, int maxLayers, float climbStep, float maxDrop)
        {
            if (cols < 2 || rows < 2 || cell <= 0f || nlay == null || h == null || ok == null || maxLayers < 1) return null;
            int cells = cols * rows;

            // First pass: exact node count (drop unsampled -999 slots defensively).
            int count = 0;
            for (int ci = 0; ci < cells; ci++)
            {
                int nl = Math.Min(nlay[ci], (byte)maxLayers);
                for (int l = 0; l < nl; l++)
                    if (h[ci * maxLayers + l] > -900f) count++;
            }
            if (count == 0) return null;
            var nav = new OctreeNav(cols, rows, cell, minX, minZ, climbStep, maxDrop, count);

            // Second pass: fill per column, Y-sorted by insertion (the sampler emits
            // height-ordered layers; insertion sort only guards odd cache loads).
            int idx = 0;
            int columns = 0;
            for (int ci = 0; ci < cells; ci++)
            {
                int nl = Math.Min(nlay[ci], (byte)maxLayers);
                if (nl <= 0) continue;
                int cx = ci % cols, cz = ci / cols;
                int start = idx;
                for (int l = 0; l < nl; l++)
                {
                    float y = h[ci * maxLayers + l];
                    if (y <= -900f) continue;
                    byte st = ok[ci * maxLayers + l];
                    // insert keeping the column sorted by height
                    int j = idx;
                    while (j > start && nav._nyq[j - 1] > VoxelY(y, cell))
                    {
                        nav._ny[j] = nav._ny[j - 1]; nav._nyq[j] = nav._nyq[j - 1];
                        nav._nst[j] = nav._nst[j - 1]; nav._nodeCol[j] = nav._nodeCol[j - 1];
                        j--;
                    }
                    nav._ny[j] = y;
                    nav._nyq[j] = VoxelY(y, cell);
                    nav._nst[j] = st;
                    nav._nodeCol[j] = ci;
                    if (st == Wall) nav._walls.Add(Pack(cx, WallVoxelY(y, cell), cz));
                    idx++;
                }
                int len = idx - start;
                if (len > 0) { nav._colStart[ci] = start; nav._colLen[ci] = len; columns++; }
            }
            nav.ColumnCount = columns;
            return nav;
        }

        internal static int VoxelY(float worldY, float cell) { return (int)Math.Floor(worldY / cell); }

        // Wall voxels live at CHEST height above their surface: legs are checked
        // (and the physics rays cast) at Consts.PathRayHeightOffset, so the
        // blocking voxel must sit where the body would collide, not at the floor.
        internal static int WallVoxelY(float surfaceY, float cell)
        {
            return VoxelY(surfaceY + Consts.PathRayHeightOffset, cell);
        }

        // 21 bits per axis packed into one long — max ±1M voxels per axis, far past
        // any scene (0.5m cells → a 500km world).
        internal static long Pack(int x, int y, int z)
        {
            return (((long)x & 0x1FFFFFL) << 42) | (((long)y & 0x1FFFFFL) << 21) | ((long)z & 0x1FFFFFL);
        }

        public int VoxX(float x) { return (int)Math.Floor((x - _minX) / _cell); }
        public int VoxZ(float z) { return (int)Math.Floor((z - _minZ) / _cell); }

        public void NodePos(int node, out float x, out float y, out float z)
        {
            int ci = _nodeCol[node];
            y = _ny[node];
            x = _minX + ((ci % _cols) + 0.5f) * _cell;
            z = _minZ + ((ci / _cols) + 0.5f) * _cell;
        }

        // ------------------------------------------------------------------
        // nearest walkable floor node to a world point, searching outward rings
        // of columns. Height distance counts double: anchoring a start/goal
        // cares about "my floor" more than "this column". -1 when nothing found.
        // ------------------------------------------------------------------
        public int NearestNode(float x, float y, float z, int ring)
        {
            int cx = VoxX(x), cz = VoxZ(z);
            int best = -1; float bd = float.MaxValue;
            int maxR = Math.Max(0, ring);
            for (int r = 0; r <= maxR; r++)
            {
                RingColumns(cx, cz, r, (nx, nz) =>
                {
                    int ci = nz * _cols + nx;
                    int start = _colStart[ci];
                    if (start < 0) return;
                    for (int i = 0; i < _colLen[ci]; i++)
                    {
                        int nd = start + i;
                        byte st = _nst[nd];
                        if (st != Clear && st != Door) continue;
                        float dxr = (_minX + ((ci % _cols) + 0.5f) * _cell) - x;
                        float dzr = (_minZ + ((ci / _cols) + 0.5f) * _cell) - z;
                        float dyr = _ny[nd] - y;
                        float d = dxr * dxr + dzr * dzr + 4f * dyr * dyr;
                        if (d < bd) { bd = d; best = nd; }
                    }
                });
            }
            return best;
        }

        private void RingColumns(int cx, int cz, int r, Action<int, int> cb)
        {
            if (r == 0) { if (InColBounds(cx, cz)) cb(cx, cz); return; }
            for (int d = -r; d <= r; d++)
            {
                if (InColBounds(cx + d, cz - r)) cb(cx + d, cz - r);
                if (InColBounds(cx + d, cz + r)) cb(cx + d, cz + r);
            }
            for (int d = -r + 1; d <= r - 1; d++)
            {
                if (InColBounds(cx - r, cz + d)) cb(cx - r, cz + d);
                if (InColBounds(cx + r, cz + d)) cb(cx + r, cz + d);
            }
        }

        private bool InColBounds(int x, int z) { return x >= 0 && z >= 0 && x < _cols && z < _rows; }

        // ------------------------------------------------------------------
        // A* across 3D floor nodes
        // ------------------------------------------------------------------
        // 8-connected horizontal edges (neighbor column's best walkable surface
        // within [−drop, +climb] of the current floor) + same-column fall edges.
        // Returns node ids start→goal, or null. Thread-safe (query lock).
        public List<int> FindPath(int start, int goal, int expandBudget)
        {
            if (start < 0 || goal < 0 || start >= _nodes || goal >= _nodes) return null;
            if ((_nst[start] != Clear && _nst[start] != Door)
                || (_nst[goal] != Clear && _nst[goal] != Door)) return null;
            if (start == goal) return new List<int> { start };
            lock (_qlock)
            {
                float gx, gy, gz;
                NodePos(goal, out gx, out gy, out gz);
                int epoch = ++_epoch;
                int heapN = 0;
                _g[start] = 0f; _from[start] = -1; _stamp[start] = epoch; _closed[start] = 0;
                HeapAddOrImprove(start, gx, gy, gz, ref heapN, epoch);
                int expanded = 0;

                while (heapN > 0)
                {
                    int cur = HeapPop(ref heapN);
                    if (_stamp[cur] == epoch && _closed[cur] != 0) continue;
                    _closed[cur] = 1;
                    if (cur == goal) return Rebuild(goal, epoch);
                    if (++expanded > expandBudget) return null;

                    int ci = _nodeCol[cur];
                    int cxq = ci % _cols, czq = ci / _cols;
                    int cyq = _nyq[cur];

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            int nx = cxq + dx, nz = czq + dz;
                            if (!InColBounds(nx, nz)) continue;
                            int cand = BestInColumn(nz * _cols + nx, cyq);
                            if (cand < 0) continue;
                            float step = (dx != 0 && dz != 0) ? 1.4142136f : 1f;
                            float cost = step + 0.55f * Math.Abs(_nyq[cand] - cyq)
                                + (_nst[cand] == Door ? 1.5f : 0f);
                            Relax(cur, cand, cost, epoch, gx, gy, gz, ref heapN);
                        }
                    }

                    // same-column fall edge: drop through a hole/shaft straight DOWN.
                    // Columns sort ascending by height, so the node BELOW is cur−1 —
                    // never cur+1 (that's the node above; climbing it is a job for
                    // ramps, which reach it via horizontal edges).
                    int local = cur - _colStart[ci];
                    if (local - 1 >= 0)
                    {
                        int below = cur - 1;
                        byte st = _nst[below];
                        if ((st == Clear || st == Door) && cyq - _nyq[below] <= _dropV)
                            Relax(cur, below, 0.35f + (st == Door ? 1.5f : 0f), epoch, gx, gy, gz, ref heapN);
                    }
                }
                return null;
            }
        }

        // The walkable surface in column ci closest in height to yq, inside the
        // [−drop, +climb] window — binary search + narrow scan.
        private int BestInColumn(int ci, int yq)
        {
            int start = _colStart[ci];
            if (start < 0) return -1;
            int len = _colLen[ci];
            int lo = 0, hi = len - 1, first = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_nyq[start + mid] >= yq - _dropV) { first = mid; hi = mid - 1; }
                else lo = mid + 1;
            }
            if (first < 0) return -1;
            int best = -1, bd = int.MaxValue;
            for (int i = first; i < len && _nyq[start + i] <= yq + _climbV; i++)
            {
                byte st = _nst[start + i];
                if (st != Clear && st != Door) continue;
                int d = Math.Abs(_nyq[start + i] - yq);
                if (d < bd) { bd = d; best = start + i; }
            }
            return best;
        }

        private void Relax(int from, int to, float cost, int epoch, float gx, float gy, float gz, ref int heapN)
        {
            float ng = _g[from] + cost;
            if (_stamp[to] == epoch && (_closed[to] != 0 || _g[to] <= ng + 1e-6f)) return;
            _g[to] = ng; _from[to] = from; _stamp[to] = epoch; _closed[to] = 0;
            HeapAddOrImprove(to, gx, gy, gz, ref heapN, epoch);
        }

        private const float HScale = 1.001f; // admissible-ish tie-break toward the goal

        // Indexed decrease-key heap: each node occupies at most one slot, so the
        // heap can never exceed the node count and no pushed improvement is lost
        // (a lazy-duplicate heap could overflow and silently drop a live node).
        // _heapIdxEpoch (declared with the scratch fields) guards the per-node
        // slot against stale queries.
        private float FOf(int node, float gx, float gy, float gz)
        {
            float x, y, z;
            NodePos(node, out x, out y, out z);
            float dx = x - gx, dy = y - gy, dz = z - gz;
            return _g[node] + HScale * (float)Math.Sqrt(dx * dx + dy * dy + dz * dz) / _cell;
        }

        private void HeapAddOrImprove(int node, float gx, float gy, float gz, ref int heapN, int epoch)
        {
            float f = FOf(node, gx, gy, gz);
            int i;
            if (_heapIdxEpoch[node] == epoch && _heapIdx[node] >= 0)
            {
                i = _heapIdx[node];
                if (f >= _heapF[i]) return; // stale relax (shouldn't happen; cheap guard)
                _heapF[i] = f;
            }
            else
            {
                i = heapN++;
                _heap[i] = node; _heapF[i] = f;
                _heapIdx[node] = i; _heapIdxEpoch[node] = epoch;
            }
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_heapF[p] <= _heapF[i]) break;
                HeapSwap(i, p);
                i = p;
            }
        }

        private int HeapPop(ref int heapN)
        {
            int top = _heap[0];
            _heapIdx[top] = -1;
            heapN--;
            if (heapN > 0)
            {
                _heap[0] = _heap[heapN]; _heapF[0] = _heapF[heapN];
                _heapIdx[_heap[0]] = 0;
                int i = 0;
                while (true)
                {
                    int l = i * 2 + 1, r = l + 1, m = i;
                    if (l < heapN && _heapF[l] < _heapF[m]) m = l;
                    if (r < heapN && _heapF[r] < _heapF[m]) m = r;
                    if (m == i) break;
                    HeapSwap(i, m);
                    i = m;
                }
            }
            return top;
        }

        private void HeapSwap(int a, int b)
        {
            int tn = _heap[a]; _heap[a] = _heap[b]; _heap[b] = tn;
            float tf = _heapF[a]; _heapF[a] = _heapF[b]; _heapF[b] = tf;
            _heapIdx[_heap[a]] = a;
            _heapIdx[_heap[b]] = b;
        }

        private List<int> Rebuild(int goal, int epoch)
        {
            var path = new List<int>(64);
            int cur = goal;
            while (cur >= 0 && _stamp[cur] == epoch)
            {
                path.Add(cur);
                int prev = _from[cur];
                if (prev < 0) break;
                cur = prev;
            }
            path.Reverse();
            return path.Count >= 1 ? path : null;
        }

        // ------------------------------------------------------------------
        // straight-leg check — march the segment against the wall-voxel set
        // ------------------------------------------------------------------
        // True when the segment (world coords, chest-height offset applied)
        // passes no wall voxel NEAR ENOUGH TO MATTER. A sample landing in a wall
        // voxel only vetoes the leg when the voxel's CENTER is within one cell
        // of the segment — matching the ±body-half-width ray lanes' semantics.
        // The strict any-touch veto (pre-2026-11-18) blocked legs grazing voxel
        // edges half a meter off the line, and every false veto split a leg —
        // the A* staircase survived and kobolds walked zig-zag. Coarse by
        // design: the cheap pre-check before the physics raycasts (SegmentClear
        // stays the final truth). Unlike the per-layer quadtree check this one
        // crosses floors — a leg from a ramp onto an overpass is validated
        // against the walls of BOTH levels.
        // Veto radius = body half width + margin (mirrors the ±lateral ray lanes
        // and the simplifier's deviation bound — one notion of "near the line").
        // A wall voxel whose center is farther than this never splits a leg.
        internal const float SegmentVetoRadiusM = 0.45f;

        public bool SegmentOpen(float x0, float y0, float z0, float x1, float y1, float z1)
        {
            return SegmentOpen(x0, y0, z0, x1, y1, z1, SegmentVetoRadiusM);
        }

        // BODY-SIZE-AWARE (2026-11-21): the veto radius follows the requesting
        // body — tiny kobolds get narrow lanes (squeeze through cracks), large
        // ones wide lanes (route around gaps they can't fit). WorldMap threads
        // its per-body radius in; the const remains the default for compat.
        public bool SegmentOpen(float x0, float y0, float z0, float x1, float y1, float z1, float vetoRadiusM)
        {
            if (_walls.Count == 0) return true;
            float dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
            float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-4f) return true;
            int steps = (int)Math.Ceiling(len / (_cell * 0.5f));
            float sx = dx / steps, sy = dy / steps, sz = dz / steps;
            for (int i = 0; i <= steps; i++)
            {
                float x = x0 + sx * i, y = y0 + sy * i + 0.75f, z = z0 + sz * i;
                int vx = VoxX(x), vy = VoxelY(y, _cell), vz = VoxZ(z);
                if (!_walls.Contains(Pack(vx, vy, vz))) continue;
                // Near-enough-to-matter: distance from the wall voxel's center to
                // the SEGMENT (3D — the line can slope between floors), not just
                // "the sample touched the voxel's cell".
                float cx = _minX + (vx + 0.5f) * _cell;
                float cy = (vy + 0.5f) * _cell;
                float cz = _minZ + (vz + 0.5f) * _cell;
                float veto = vetoRadiusM > 0.05f ? vetoRadiusM : 0.05f;
                if (PointSegmentDist(cx, cy, cz, x0, y0 + 0.75f, z0, x1, y1 + 0.75f, z1) <= veto)
                    return false;
            }
            return true;
        }

        // Distance from point p to segment ab (3D). Pure.
        private static float PointSegmentDist(float px, float py, float pz,
            float ax, float ay, float az, float bx, float by, float bz)
        {
            float abx = bx - ax, aby = by - ay, abz = bz - az;
            float apx = px - ax, apy = py - ay, apz = pz - az;
            float denom = abx * abx + aby * aby + abz * abz;
            float t = denom > 1e-9f ? (apx * abx + apy * aby + apz * abz) / denom : 0f;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            float qx = ax + abx * t - px, qy = ay + aby * t - py, qz = az + abz * t - pz;
            return (float)Math.Sqrt(qx * qx + qy * qy + qz * qz);
        }

        // ------------------------------------------------------------------
        // patching — a body proved a "wall" verdict wrong by standing on it
        // ------------------------------------------------------------------
        public bool PatchWalkable(float x, float y, float z)
        {
            int cx = VoxX(x), cz = VoxZ(z);
            if (!InColBounds(cx, cz)) return false;
            int ci = cz * _cols + cx;
            int start = _colStart[ci];
            if (start < 0) return false;
            bool patched = false;
            for (int i = 0; i < _colLen[ci]; i++)
            {
                int nd = start + i;
                if (_nst[nd] == Wall && Math.Abs(_ny[nd] - y) < 1.2f)
                {
                    _nst[nd] = Clear;
                    _walls.Remove(Pack(cx, WallVoxelY(_ny[nd], _cell), cz));
                    patched = true;
                }
            }
            return patched;
        }
    }
}
