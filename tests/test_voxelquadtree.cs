// Tests for VoxelQuadtree (quadtree compression over the walkability bitmap).
// Pure C# — no Unity deps.
// Build: mcs -target:exe -out:/tmp/t_vq.exe tests/test_voxelquadtree.cs src/VoxelQuadtree.cs && mono /tmp/t_vq.exe
using System;

static class VoxelQuadtreeTests
{
    static int _passed, _failed;

    static void Assert(bool cond, string name)
    {
        if (cond) { _passed++; Console.WriteLine("  PASS: " + name); }
        else { _failed++; Console.WriteLine("  FAIL: " + name); }
    }

    static bool[] Map(string[] rows)
    {
        int cols = rows[0].Length;
        var w = new bool[rows.Length * cols];
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < cols; c++)
                w[r * cols + c] = rows[r][c] != '#'; // '#': blocked, '.'/'o': open
        return w;
    }

    static void Main()
    {
        Console.WriteLine("VoxelQuadtree tests");
        BuildBasics();
        StraightRuns();
        BlockedRuns();
        MixedRegionDrilldown();
        SegmentVsRun();
        Console.WriteLine(_passed + " passed, " + _failed + " failed");
        Environment.Exit(_failed);
    }

    // A fully-open map is one uniform root; a fully-blocked one too.
    static void BuildBasics()
    {
        var open = VoxelQuadtree.Build(new bool[16 * 16], 16, 16); // all false = blocked? No: walkable=false everywhere
        Assert(open.State(0, 0, open.LevelCount - 1) == VoxelQuadtree.Blocked, "all-blocked root");
        var allOpen = new bool[16 * 16];
        for (int i = 0; i < allOpen.Length; i++) allOpen[i] = true;
        var q2 = VoxelQuadtree.Build(allOpen, 16, 16);
        Assert(q2.State(0, 0, q2.LevelCount - 1) == VoxelQuadtree.Open, "all-open root");
        Assert(q2.OpenRun(0.5f, 0.5f, 15.5f, 15.5f) > 0f, "run crosses open map");
        // Non-square padding: an 8x3 map must not read outside the sampled rows.
        var w = new bool[8 * 3];
        for (int i = 0; i < w.Length; i++) w[i] = true;
        var q3 = VoxelQuadtree.Build(w, 8, 3);
        Assert(q3.IsOpen(0, 0) && q3.IsOpen(7, 2), "in-bounds open");
        Assert(!q3.IsOpen(0, 3), "beyond rows = blocked");
        Assert(!q3.IsOpen(8, 0), "beyond cols = blocked");
    }

    // Open field: the whole diagonal run is provably open, end to end.
    static void StraightRuns()
    {
        var rows = new[]
        {
            "........",
            "........",
            "........",
            "........",
            "........",
            "........",
            "........",
            "........",
        };
        var q = VoxelQuadtree.Build(Map(rows), 8, 8);
        float run = q.OpenRun(0.5f, 0.5f, 7.5f, 7.5f);
        Assert(run > 9f, "full diagonal open (" + run.ToString("0.00") + " cells)");
        float run2 = q.OpenRun(0.5f, 0.5f, 7.5f, 0.5f);
        Assert(run2 >= 6.5f, "full horizontal open");
    }

    // A wall across the path cuts the run exactly at (or before) the wall.
    static void BlockedRuns()
    {
        var rows = new[]
        {
            "........",
            "........",
            "........",
            "....####",
            "........",
            "........",
            "........",
            "........",
        };
        var q = VoxelQuadtree.Build(Map(rows), 8, 8);
        // East run along the wall row (z=3): open x0..3, wall at x>=4 → cut ≈4.
        float cut = q.OpenRun(0.5f, 3.5f, 7.5f, 3.5f);
        Assert(cut <= 4.0f && cut >= 3.0f, "wall cuts east run at x≈4 (got " + cut.ToString("0.00") + ")");
        // Along z=2 the run passes the wall ROW (not on it) → full.
        float around = q.OpenRun(0.5f, 2.5f, 7.5f, 2.5f);
        Assert(around >= 6.5f, "row north of the wall is open");
    }

    // Mixed regions (door frame in a wall) drill to fine leaves: a 1-cell gap reads.
    static void MixedRegionDrilldown()
    {
        var rows = new[]
        {
            "........",
            "........",
            "####.###",  // wall row with a one-cell gap at x=4 (a door slot)
            "........",
            "........",
        };
        var q = VoxelQuadtree.Build(Map(rows), 8, 5);
        Assert(q.IsOpen(4, 2) && !q.IsOpen(3, 2) && !q.IsOpen(5, 2), "gap cell open, frame cells blocked");
        // Vertical crossing through the gap passes; the same crossing off-gap fails.
        Assert(q.SegmentOpen(4.5f, 1.5f, 4.5f, 3.5f), "through the gap fine");
        Assert(!q.SegmentOpen(2.5f, 1.5f, 2.5f, 3.5f), "through the wall fails");
        // OpenRun vertical through the gap runs the full strip; off-gap cuts at the wall.
        float inGap = q.OpenRun(4.5f, 1.5f, 4.5f, 3.5f);
        Assert(inGap >= 1.9f, "run through the gap (got " + inGap.ToString("0.00") + ")");
        float offGap = q.OpenRun(2.5f, 1.5f, 2.5f, 3.5f);
        Assert(offGap <= 0.5f + 1e-3f, "run off-gap cuts at the wall (got " + offGap.ToString("0.00") + ")");
    }

    // OpenRun never outlives SegmentOpen: if the fine segment is blocked, the run
    // must cut at or before the blockage.
    static void SegmentVsRun()
    {
        var rows = new[]
        {
            "........",
            "..#.....",
            "........",
            "........",
            "..#.....",
            "........",
            "........",
            "........",
        };
        var q = VoxelQuadtree.Build(Map(rows), 8, 8);
        for (int i = 0; i < 40; i++)
        {
            float x0 = (i * 13 % 70) / 10f + 0.5f;
            float z0 = (i * 7 % 60) / 10f + 0.5f;
            float x1 = 7.5f - i * 0.15f, z1 = 7.2f - i * 0.1f;
            if (x1 < 0) x1 = 0.5f; if (z1 < 0) z1 = 0.5f;
            bool segOpen = q.SegmentOpen(x0, z0, x1, z1);
            float run = q.OpenRun(x0, z0, x1, z1);
            float segLen = (float)Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
            if (segOpen)
                Assert(run >= segLen - 1.01f, "open segment has a long run (" + run.ToString("0.0") + "/" + segLen.ToString("0.0") + ")");
            else
                Assert(run < segLen, "blocked segment shortens the run");
        }
        // Landing-cell truth: a strip whose ENDPOINT clips a wall runs only to the cut.
        var cut = VoxelQuadtree.Build(Map(new[] { "........", "..#.....", "........", "........", "..#.....", "........", "........", "........" }), 8, 8);
        bool tipOpen = cut.SegmentOpen(2.8f, 4.5f, 2.9f, 4.15f);
        Assert(!tipOpen, "endpoint clipping the wall cell is not open");
        float tipRun = cut.OpenRun(2.8f, 4.5f, 2.9f, 4.15f);
        Assert(tipRun < 1.36f, "landing clip cuts the run (got " + tipRun.ToString("0.00") + ")");

        Console.WriteLine("  (segment/run consistency probed across 40 diagonal segments)");
    }
}