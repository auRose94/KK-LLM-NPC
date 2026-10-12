// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// VoxelQuadtree — a quadtree compression over the sampled walkability bitmap.
// Pure C# (no Unity/BepInEx deps) so it is unit-testable:
//   mcs -target:exe -out:/tmp/t_vq.exe tests/test_voxelquadtree.cs src/VoxelQuadtree.cs && mono /tmp/t_vq.exe
//
// Why: the fine world-map grid IS the path data (0.5 m cells), but open-field paths
// zigzag post by post and every clearance query walked fine cells one at a time.
// The quadtree collapses UNIFORM regions (all-open or all-blocked blocks) one level
// up, so:
//   * a segment through open field is checked against a handful of coarse nodes,
//   * uniform-BLOCKED coarse nodes reject instantly (walls, fences, furniture rows),
//   * mixed regions (door frames, clutter) drill to the fine leaves — the exact
//     0.5 m truth the sampler recorded, so compression loses nothing that matters.
// The straighten pass uses <see cref="OpenRun"/> to fuse zigzag legs into long
// planned runs; segments re-verify per landing cell so boundary math stays safe.
//
// Layout: sizes padded to a power of two; one flat byte array per level;
// level k cell extent = size >> k; state 0 = mixed, 1 = open, 2 = blocked.
// All coordinates are in CELL units (not meters).
using System;
using System.Collections.Generic;

internal sealed class VoxelQuadtree
{
    public const byte Mixed = 0;
    public const byte Open = 1;
    public const byte Blocked = 2;

    private readonly int _cols, _rows, _size; // _size: power of two
    private readonly byte[][] _levels;        // levels[0] = fine; each is (size>>k)^2

    public int Cols { get { return _cols; } }
    public int Rows { get { return _rows; } }
    public int Size { get { return _size; } }
    public int LevelCount { get { return _levels.Length; } }

    private VoxelQuadtree(int cols, int rows, int size, byte[][] levels)
    {
        _cols = cols; _rows = rows; _size = size; _levels = levels;
    }

    public static VoxelQuadtree Build(bool[] walkable, int cols, int rows)
    {
        if (walkable == null) throw new ArgumentNullException("walkable");
        if (cols <= 0 || rows <= 0) throw new ArgumentOutOfRangeException("cols/rows");
        int size = 1;
        while (size < cols || size < rows) size <<= 1;

        var levels = new List<byte[]>();
        int n = size;
        byte[] fine = new byte[n * n];
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
                fine[z * n + x] = (x < cols && z < rows && walkable[z * cols + x]) ? Open : Blocked;
        levels.Add(fine);

        while (n > 1)
        {
            byte[] lower = levels[levels.Count - 1];
            byte[] upper = new byte[(n >> 1) * (n >> 1)];
            for (int z = 0; z < (n >> 1); z++)
                for (int x = 0; x < (n >> 1); x++)
                {
                    byte a = lower[(z * 2) * n + (x * 2)];
                    byte b = lower[(z * 2) * n + (x * 2 + 1)];
                    byte c = lower[(z * 2 + 1) * n + (x * 2)];
                    byte d = lower[(z * 2 + 1) * n + (x * 2 + 1)];
                    upper[z * (n >> 1) + x] = (a == b && b == c && c == d) ? a : Mixed;
                }
            levels.Add(upper);
            n >>= 1;
        }
        return new VoxelQuadtree(cols, rows, size, levels.ToArray());
    }

    // Grid-truth lookup (level 0). Outside the sampled area = blocked.
    public bool IsOpen(int x, int z)
    {
        if (x < 0 || z < 0 || x >= _cols || z >= _rows) return false;
        return _levels[0][z * _size + x] == Open;
    }

    // State of the level-k cell covering (x, z). k=0 is the fine lookup.
    public byte State(int x, int z, int level)
    {
        if (level < 0 || level >= _levels.Length) return Mixed;
        int n = _size >> level;
        if (n < 1) return Mixed;
        int cx = x >> level, cz = z >> level;
        return _levels[level][cz * n + cx];
    }

    // Segment walkability at FULL leaf resolution. Defined THROUGH OpenRun: a
    // half-cell stepped sampler optimistically misses diagonal corner-clips of
    // blocked cells (observed: a straight leg grazing a wall pixel read open while
    // the slab walk rightly refused it); OpenRun's slab math is the exact one, so
    // both answers come from the same walk. Pessimistic by construction.
    public bool SegmentOpen(float x0, float z0, float x1, float z1)
    {
        float dx = x1 - x0, dz = z1 - z0;
        float dist = (float)System.Math.Sqrt(dx * dx + dz * dz);
        if (dist <= 0f) return IsOpen((int)x0, (int)z0);
        return OpenRun(x0, z0, x1, z1) >= dist - 1e-3f;
    }

    // How far along (x0,z0)→(x1,z1) the strip stays provably open, in CELL units
    // (≤ the input length; -1 when the very start is blocked). Every advance is
    // VERIFIED at its landing cell — coarse uniform-OPEN blocks may be skipped,
    // but the skip is never trusted past its boundary, and the endpoint's landing
    // cell is checked explicitly (a segment tip clipping a wall must cut the run,
    // or a straightened path would end inside geometry).
    public float OpenRun(float x0, float z0, float x1, float z1)
    {
        float dx = x1 - x0, dz = z1 - z0;
        float dist = (float)System.Math.Sqrt(dx * dx + dz * dz);
        if (dist <= 0f) return IsOpen((int)x0, (int)z0) ? 0f : -1f;
        float ndx = dx / dist, ndz = dz / dist;
        // Coarse level: deepest level whose cells still span ≥4 fine cells.
        int coarse = _levels.Length >= 3 ? _levels.Length - 3 : 0;
        if (!IsOpen((int)x0, (int)z0)) return -1f;
        float t = 0f;
        int guard = 0;
        while (guard++ < 4096)
        {
            int cx = ClampI((int)(x0 + ndx * t), 0, _cols - 1);
            int cz = ClampI((int)(z0 + ndz * t), 0, _rows - 1);
            byte st = State(cx, cz, coarse);
            if (st == Blocked) return t;
            float next;
            if (st == Open)
                next = SlabExit(x0, z0, ndx, ndz, t, cx >> coarse, cz >> coarse, coarse);
            else if (IsOpen(cx, cz))
                next = SlabExit(x0, z0, ndx, ndz, t, cx, cz, 0);
            else return t; // mixed region, fine leaf blocked here
            if (next - t < 0.05f) t += 0.5f; // degenerate/epsilon-crawl exit — step half a cell
                                              // (landings are re-verified, so exactness at the
                                              // boundary is not needed; without this the guard
                                              // burns on floating-point boundary crawls)
            else if (next >= dist)
            {
                // The skip/reach claims the whole strip — verify the landing cell.
                int lx = ClampI((int)(x0 + ndx * dist), 0, _cols - 1);
                int lz = ClampI((int)(z0 + ndz * dist), 0, _rows - 1);
                if (IsOpen(lx, lz)) return dist;
                return LandingCut(x0, z0, ndx, ndz, t, dist);
            }
            else t = next;
        }
        return dist;
    }

    // Fine-step the remaining strip for the exact cut when a skip overclaims.
    private float LandingCut(float x0, float z0, float ndx, float ndz, float from, float dist)
    {
        float s = from;
        while (s < dist)
        {
            float s2 = Math.Min(dist, s + 0.5f);
            int sx = ClampI((int)(x0 + ndx * s2), 0, _cols - 1);
            int sz = ClampI((int)(z0 + ndz * s2), 0, _rows - 1);
            if (!IsOpen(sx, sz)) return s < dist ? s : dist;
            s = s2;
        }
        return dist;
    }

    private static int ClampI(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

    // Absolute t where the ray exits the block with INDEX (cx, cz) at the given
    // level (cell-space corner = index << level). Deltas are RELATIVE to the entry
    // t and are added back; the index→corner shift and the relative→absolute add
    // were both missed in the first version (skips ran through walls / garbled).
    private static float SlabExit(float x0, float z0, float ndx, float ndz, float t, int cx, int cz, int level)
    {
        int ext = 1 << level;
        float loX = (float)(cx << level), hiX = loX + ext;
        float loZ = (float)(cz << level), hiZ = loZ + ext;
        float tX = float.PositiveInfinity, tZ = float.PositiveInfinity;
        if (ndx > 1e-6f) tX = (hiX - (x0 + ndx * t)) / ndx;
        else if (ndx < -1e-6f) tX = (loX - (x0 + ndx * t)) / ndx;
        if (ndz > 1e-6f) tZ = (hiZ - (z0 + ndz * t)) / ndz;
        else if (ndz < -1e-6f) tZ = (loZ - (z0 + ndz * t)) / ndz;
        float rel = Math.Min(tX, tZ);
        return (rel > 1e-6f && !float.IsInfinity(rel)) ? t + rel : t + 0.5f;
    }
}