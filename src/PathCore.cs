// Written by @auRose94 (https://github.com/auRose94) under MIT license.
// Pure C# pathfinding core — no UnityEngine dependency.
// Extracted from Pathfinding.cs / WorldMap.cs so it compiles as a standalone
// testable library (mcs -target:exe -out:/tmp/t_pf.exe tests/test_pathcore.cs src/PathCore.cs).
//
// Contents:
//   PathPolicy    — milestone replan decisions (pure function)
//   AstarSolver   — time-budgeted A* over bool[cols,rows] grids
//   AdaptiveCell  — adaptive cell sizing for whole-map coverage
//   AutoFloor     — auto floor clustering from Y samples
//
using System;
using System.Diagnostics;

namespace KKLLMNPC
{
    // ------------------------------------------------------------------
    // PathPolicy — milestone replan decisions
    //
    // Pure function: given the current navigation state, decide whether
    // to request a new path.  No timer-based replan — only events
    // (target moved, path exhausted, deviation, teleport) trigger it.
    // ------------------------------------------------------------------
    internal static class PathPolicy
    {
        // Default max path age in seconds.  After this the path is stale
        // even if the target hasn't moved.
        public const float DefaultMaxPathAge = 30f;

        // Thresholds (also exposed as config in Main.cs).
        public const float TargetMovedThreshold = 2f;   // meters
        public const float DeviationThreshold   = 0.8f; // meters
        public const float DeviationTimeWindow  = 0.75f;// seconds
        public const float TeleportDistance     = 5f;   // meters

        /// <summary>
        /// Decide whether to replan.  Returns true if ANY of the
        //  conditions below are met:
        //   1. target moved > 2 m from path goal
        //   2. path exhausted (pathRemaining == 0)
        //   3. deviation > 0.8 m AND timeSincePath > 0.75 s
        //   4. teleported (jump > 5 m)
        //   5. path age > maxPathAge
        //  Returns false if havePath is false (nothing to replan from).
        /// </summary>
        public static bool ShouldReplan(
            bool havePath,
            int pathRemaining,
            float distToGoal,
            bool targetMoved,
            float deviation,
            float timeSincePath,
            bool teleported,
            float maxPathAge)
        {
            if (!havePath) return false;

            // Path exhausted — we reached the last waypoint.
            if (pathRemaining <= 0) return true;

            // Path too old — stale even if nothing moved.
            if (maxPathAge > 0f && timeSincePath >= maxPathAge) return true;

            // Target moved beyond threshold from path goal.
            if (targetMoved && distToGoal > TargetMovedThreshold) return true;

            // Deviation persisting beyond the time window.
            if (deviation > DeviationThreshold && timeSincePath > DeviationTimeWindow) return true;

            // Teleported — body jumped somewhere unexpected.
            if (teleported) return true;

            return false;
        }

        // Overload with default maxPathAge.
        public static bool ShouldReplan(
            bool havePath,
            int pathRemaining,
            float distToGoal,
            bool targetMoved,
            float deviation,
            float timeSincePath,
            bool teleported)
        {
            return ShouldReplan(havePath, pathRemaining, distToGoal, targetMoved,
                deviation, timeSincePath, teleported, DefaultMaxPathAge);
        }
    }

    // ------------------------------------------------------------------
    // AstarSolver — time-budgeted A* over a bool[cols,rows] grid.
    //
    // The grid represents walkable (true) / unwalkable (false) cells.
    // This is a simplified 2D A* suitable for the per-query local grid
    // or the WorldMap (which can be projected to 2D per layer).
    //
    // The solver stops when either:
    //   - the goal is reached (returns the path),
    //   - the node expansion budget is exhausted (returns best partial path),
    //   - the time budget is exceeded (returns best partial path).
    // ------------------------------------------------------------------
    internal class AstarSolver
    {
        public const int DefaultNodeBudget = 20000;
        public const float DefaultTimeBudgetMs = 8f;

        private readonly bool[,] _grid;
        private readonly int _cols, _rows;
        private readonly int _sx, _sz, _gx, _gz;
        private readonly int _nodeBudget;
        private readonly float _timeBudgetMs;

        private int[] _gScore;
        private int[] _cameFrom;
        private bool[] _closed;
        private int _expanded;

        // Min-heap for the open set.
        private int[] _heapIdx;
        private float[] _heapF;
        private int _heapSize;

        public int ExpandedCount => _expanded;

        public AstarSolver(bool[,] grid, int cols, int rows, int sx, int sz, int gx, int gz,
            int? nodeBudget = null, float? timeBudgetMs = null)
        {
            _grid = grid; _cols = cols; _rows = rows;
            _sx = sx; _sz = sz; _gx = gx; _gz = gz;
            _nodeBudget = nodeBudget ?? DefaultNodeBudget;
            _timeBudgetMs = timeBudgetMs ?? DefaultTimeBudgetMs;

            int N = cols * rows;
            _gScore = new int[N];
            _cameFrom = new int[N];
            _closed = new bool[N];
            _heapIdx = new int[N + 16];
            _heapF = new float[N + 16];
            _heapSize = 0;
            _expanded = 0;

            for (int i = 0; i < N; i++)
            {
                _gScore[i] = int.MaxValue;
                _cameFrom[i] = -1;
            }
        }

        private void HeapPush(int idx, float f)
        {
            if (_heapSize >= _heapIdx.Length)
            {
                int newCap = _heapIdx.Length * 2;
                Array.Resize(ref _heapIdx, newCap);
                Array.Resize(ref _heapF, newCap);
            }
            _heapF[_heapSize] = f;
            _heapIdx[_heapSize] = idx;
            int k = _heapSize;
            _heapSize++;
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
            if (_heapSize == 0) return -1;
            int top = _heapIdx[0];
            _heapSize--;
            if (_heapSize == 0)
            {
                _heapIdx[0] = -1;
                _heapF[0] = 0f;
                return top;
            }
            _heapIdx[0] = _heapIdx[_heapSize];
            _heapF[0] = _heapF[_heapSize];
            int k = 0;
            while (true)
            {
                int l = k * 2 + 1, r = l + 1, m = k;
                if (l < _heapSize && _heapF[l] < _heapF[m]) m = l;
                if (r < _heapSize && _heapF[r] < _heapF[m]) m = r;
                if (m == k) break;
                float tf = _heapF[m]; _heapF[m] = _heapF[k]; _heapF[k] = tf;
                int ti = _heapIdx[m]; _heapIdx[m] = _heapIdx[k]; _heapIdx[k] = ti;
                k = m;
            }
            return top;
        }

        private float Heuristic(int x, int z)
        {
            int dx = Math.Abs(x - _gx);
            int dz = Math.Abs(z - _gz);
            return (Math.Min(dx, dz) * 1.41421356f + Math.Abs(dx - dz)) * 100f;
        }

        /// <summary>
        /// Run A* and return the path as a list of (x,z) pairs, or null if no path found.
        /// On budget overrun, returns the best partial path found so far (closest to goal).
        /// </summary>
        public int[] Solve()
        {
            var sw = Stopwatch.StartNew();
            int startNode = _sz * _cols + _sx;
            int goalNode = _gz * _cols + _gx;

            _gScore[startNode] = 0;
            HeapPush(startNode, Heuristic(_sx, _sz));

            int[] dxs = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] dzs = { 0, 0, 1, -1, 1, -1, 1, -1 };
            float[] dcost = { 1f, 1f, 1f, 1f, 1.41421356f, 1.41421356f, 1.41421356f, 1.41421356f };

            while (_heapSize > 0 && _expanded < _nodeBudget)
            {
                if (sw.ElapsedMilliseconds > _timeBudgetMs)
                {
                    // Time budget exceeded — return best partial path.
                    return PartialPath(goalNode);
                }

                int cur = HeapPop();
                if (cur == -1 || _closed[cur]) continue;
                _closed[cur] = true;
                _expanded++;

                if (cur == goalNode) break;

                int cx = cur % _cols, cz = cur / _cols;

                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + dxs[d], nz = cz + dzs[d];
                    if (nx < 0 || nz < 0 || nx >= _cols || nz >= _rows) continue;
                    if (!_grid[nx, nz]) continue; // unwalkable

                    // No cutting corners on diagonals.
                    if (dxs[d] != 0 && dzs[d] != 0)
                    {
                        if (!_grid[cx + dxs[d], cz]) continue;
                        if (!_grid[cx, cz + dzs[d]]) continue;
                    }

                    int nid = nz * _cols + nx;
                    if (_closed[nid]) continue;

                    int ng = _gScore[cur] + (int)Math.Round(dcost[d] * 100f);
                    if (ng < _gScore[nid])
                    {
                        _gScore[nid] = ng;
                        _cameFrom[nid] = cur;
                        HeapPush(nid, ng + Heuristic(nx, nz));
                    }
                }
            }

            // Reconstruct path.
            int goalNode2 = _gz * _cols + _gx;
            if (_gScore[goalNode2] == int.MaxValue) return null;

            var rev = new System.Collections.Generic.List<int>();
            int c = goalNode2;
            int maxSteps = _cols * _rows;
            while (c != -1 && rev.Count < maxSteps)
            {
                rev.Add(c);
                c = _cameFrom[c];
            }
            rev.Reverse();

            // Convert node indices to (x,z) pairs.
            int[] result = new int[rev.Count * 2];
            for (int i = 0; i < rev.Count; i++)
            {
                result[i * 2] = rev[i] % _cols;     // x
                result[i * 2 + 1] = rev[i] / _cols; // z
            }
            return result;
        }

        /// <summary>
        /// Return the node in the open set closest to the goal (best partial).
        /// </summary>
        private int[] PartialPath(int goalNode)
        {
            // Find the closed node closest to goal.
            int bestNode = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < _closed.Length; i++)
            {
                if (!_closed[i]) continue;
                int x = i % _cols, z = i / _cols;
                float d = (x - _gx) * (x - _gx) + (z - _gz) * (z - _gz);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestNode = i;
                }
            }
            if (bestNode == -1) return null;

            // Reconstruct partial path.
            var rev = new System.Collections.Generic.List<int>();
            int c = bestNode;
            int maxSteps = _cols * _rows;
            while (c != -1 && rev.Count < maxSteps)
            {
                rev.Add(c);
                c = _cameFrom[c];
            }
            rev.Reverse();

            int[] result = new int[rev.Count * 2];
            for (int i = 0; i < rev.Count; i++)
            {
                result[i * 2] = rev[i] % _cols;
                result[i * 2 + 1] = rev[i] / _cols;
            }
            return result;
        }
    }

    // ------------------------------------------------------------------
    // AdaptiveCell — adaptive cell sizing for whole-map coverage.
    //
    // Given a desired span (meters), compute an adaptive cell size:
    //   base = span / 1200
    //   clamp to [0.5, 4.0]
    //   enforce hard cell cap (~4M cells) by inflating if needed.
    // ------------------------------------------------------------------
    internal static class AdaptiveCell
    {
        public const float DefaultSpan = 2000f;
        public const float BaseDivisor = 1200f;
        public const float MinCell = 0.5f;
        public const float MaxCell = 4.0f;
        public const int MaxCells = 4_000_000;

        /// <summary>
        /// Compute adaptive cell size and resulting grid dimensions for a
        /// bounding box (width x depth).  Returns {cell, cols, rows}.
        /// </summary>
        public static (float cell, int cols, int rows) Compute(float width, float depth)
        {
            // Base cell size from span.
            float cell = width / BaseDivisor;
            cell = Math.Max(MinCell, Math.Min(MaxCell, cell));

            int cols = MathfCeilToInt(width / cell);
            int rows = MathfCeilToInt(depth / cell);
            cols = Math.Max(2, cols);
            rows = Math.Max(2, rows);

            // Enforce hard cell cap.
            while ((long)cols * rows > MaxCells && cell < MaxCell * 2f)
            {
                cell *= 1.2f;
                cols = MathfCeilToInt(width / cell);
                rows = MathfCeilToInt(depth / cell);
                cols = Math.Max(2, cols);
                rows = Math.Max(2, rows);
            }

            return (cell, cols, rows);
        }

        private static int MathfCeilToInt(float v)
        {
            int i = (int)v;
            return i < v ? i + 1 : i;
        }
    }

    // ------------------------------------------------------------------
    // AutoFloor — cluster occupied Y values into floor layers.
    //
    // Given a set of occupied Y samples, cluster them by merging
    // values within a gap threshold.  Each cluster center becomes
    // a floor layer.
    // ------------------------------------------------------------------
    internal static class AutoFloor
    {
        public const float DefaultGapThreshold = 1.5f;
        public const int MaxLayers = 16;

        /// <summary>
        /// Cluster Y samples into floor layers.  Returns sorted unique
        //  layer heights.
        /// </summary>
        public static float[] Cluster(float[] samples, float? gapThreshold = null, int? maxLayers = null)
        {
            if (samples == null || samples.Length == 0) return new float[0];

            float gap = gapThreshold ?? DefaultGapThreshold;
            int maxL = maxLayers ?? MaxLayers;

            // Sort and deduplicate.
            Array.Sort(samples);
            float[] unique = new float[samples.Length];
            int uCount = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                if (i == 0 || samples[i] != samples[i - 1])
                {
                    unique[uCount++] = samples[i];
                }
            }
            if (uCount == 0) return new float[0];

            // Cluster: merge consecutive values within gap.
            float[] layers = new float[Math.Min(uCount, maxL)];
            int lCount = 0;
            layers[0] = unique[0];
            lCount = 1;

            for (int i = 1; i < uCount; i++)
            {
                if (unique[i] - layers[lCount - 1] > gap)
                {
                    if (lCount >= maxL) break; // cap reached
                    layers[lCount++] = unique[i];
                }
                // else: within gap, merge (keep the higher value as the layer).
                else
                {
                    layers[lCount - 1] = unique[i];
                }
            }

            // Trim to actual count.
            if (lCount < layers.Length)
            {
                float[] result = new float[lCount];
                Array.Copy(layers, result, lCount);
                return result;
            }
            return layers;
        }
    }
}
