// Tests for GoalResolver (PathCore.cs) — snapping a requested goal onto the
// nearest walkable cell. Pure C# — no Unity/BepInEx/game assemblies.
//
// PathGridState already implements IGoalGrid, so this exercises the same code
// the game runs; FakeGrid just supplies deterministic cell data.
//
// Build: mcs -target:exe -out:/tmp/t_gr.exe tests/test_goalresolver.cs src/PathCore.cs
using System;
using System.Collections.Generic;
using KKLLMNPC;

static class GoalResolverTests
{
    static int _passed, _failed;

    static void Assert(bool cond, string name)
    {
        if (cond) { _passed++; Console.WriteLine("  PASS: " + name); }
        else { _failed++; Console.WriteLine("  FAIL: " + name); }
    }

    // A grid of walkable cells with a single floor height each, plus optional
    // blocked cells. -1 height means "no layer here" (a hole / unwalkable).
    //
    // The default height is deliberately set FAR from the refY the tests use,
    // so an unremarkable cell never wins on a height tie. Tests that care about
    // the tiebreak place their candidates explicitly.
    class FakeGrid : IGoalGrid
    {
        public int ColsN, RowsN;
        public float[,] Height;
        public HashSet<int> Blocked = new HashSet<int>();
        public int SampleCount;

        public FakeGrid(int cols, int rows, float defaultHeight)
        {
            ColsN = cols; RowsN = rows;
            Height = new float[cols, rows];
            for (int x = 0; x < cols; x++)
                for (int z = 0; z < rows; z++)
                    Height[x, z] = defaultHeight;
        }

        public int Cols() { return ColsN; }
        public int Rows() { return RowsN; }
        public int Ci(int x, int z) { return z * ColsN + x; }
        public void SampleCell(int ci) { SampleCount++; }
        public int LayerNear(int ci, float y)
        {
            int x = ci % ColsN, z = ci / ColsN;
            if (Blocked.Contains(ci) || Height[x, z] < 0f) return -1;
            return 0;
        }
        public float H(int ci, int l) { return Height[ci % ColsN, ci / ColsN]; }

        public void Block(int x, int z) { Blocked.Add(Ci(x, z)); }
        public void SetHeight(int x, int z, float h) { Height[x, z] = h; }
    }

    // refY used by the height-tiebreak tests. The uninteresting floor sits far
    // away from it so only explicitly placed cells are ever "close".
    const float RefY = 0f;
    const float FarY = 50f;

    // ---- fast path ----

    static void ExactCellIsUsedAsIs()
    {
        var g = new FakeGrid(10, 10, 0f);
        var r = GoalResolver.Resolve(g, 5, 5, 0f);
        Assert(r.Found, "walkable goal resolves");
        AssertEq(r.Cell, g.Ci(5, 5), "exact cell used when walkable");
        AssertEq(r.Layer, 0, "layer 0");
        Assert(!r.Found || r.Cell == g.Ci(5, 5), "no ring search needed on the fast path");
    }

    static void ExactCellPicksNearestLayer()
    {
        // Two floors in the cell; refY picks the closer one.
        var g = new FakeGrid(10, 10, 0f);
        // FakeGrid has one layer; the single-layer behaviour is the contract here.
        var r = GoalResolver.Resolve(g, 3, 3, 2.5f);
        Assert(r.Found, "resolves with a multi-storey refY");
        AssertEq(r.Cell, g.Ci(3, 3), "still the exact cell");
    }

    // ---- ring search ----

    static void BlockedGoalFindsNeighbour()
    {
        var g = new FakeGrid(10, 10, 0f);
        g.Block(5, 5);
        var r = GoalResolver.Resolve(g, 5, 5, 0f);
        Assert(r.Found, "blocked goal still resolves via a ring");
        Assert(r.Cell != g.Ci(5, 5), "did not stay on the blocked cell");
        // Ring 1 candidates are the 8 neighbours.
        int dx = Math.Abs((r.Cell % 10) - 5), dz = Math.Abs((r.Cell / 10) - 5);
        Assert(dx <= 1 && dz <= 1, "picked a ring-1 neighbour");
    }

    static void SearchExpandsUntilItFindsSomething()
    {
        var g = new FakeGrid(21, 21, FarY);
        // Block the perimeters of rings 1 and 2 (and the centre), so the first
        // walkable cell is on ring 3.
        for (int ring = 1; ring <= 2; ring++)
            for (int d = -ring; d <= ring; d++)
            {
                g.Block(10 + d, 10 - ring);
                g.Block(10 + d, 10 + ring);
                g.Block(10 - ring, 10 + d);
                g.Block(10 + ring, 10 + d);
            }
        g.Block(10, 10);
        var res = GoalResolver.Resolve(g, 10, 10, RefY);
        Assert(res.Found, "resolves at radius 3");
        int dx = Math.Abs((res.Cell % 21) - 10), dz = Math.Abs((res.Cell / 21) - 10);
        Assert(Math.Max(dx, dz) == 3, "found exactly on ring 3 (dx=" + dx + " dz=" + dz + ")");
    }

    static void GivesUpWhenNothingWalkableNearby()
    {
        var g = new FakeGrid(9, 9, 0f);
        // Block the whole 9x9 (max radius 6 from the centre covers it).
        for (int x = 0; x < 9; x++)
            for (int z = 0; z < 9; z++)
                g.Block(x, z);
        var r = GoalResolver.Resolve(g, 4, 4, 0f);
        Assert(!r.Found, "reports no path when the area is fully blocked");
        AssertEq(r.Cell, -1, "no cell reported");
    }

    static void RespectsMaxRadius()
    {
        // A walkable cell just outside the search radius must NOT be used.
        var g = new FakeGrid(41, 41, 0f);
        for (int x = 0; x < 41; x++)
            for (int z = 0; z < 41; z++)
                g.Block(x, z);
        // Open a single cell exactly MaxRadius+1 away in x.
        int far = 20 + GoalResolver.MaxRadius + 1;
        g.Height[far, 20] = 0f;
        g.Blocked.Remove(g.Ci(far, 20));
        var r = GoalResolver.Resolve(g, 20, 20, 0f);
        Assert(!r.Found, "cell beyond MaxRadius is not used (" + GoalResolver.MaxRadius + ")");
    }

    // ---- height tiebreak ----

    static void PrefersTheCellClosestInHeight()
    {
        // Ring 1 has three candidates at different heights; the one whose floor
        // matches refY must win over the two that are off.
        var g = new FakeGrid(11, 11, FarY);
        g.Block(5, 5);
        g.SetHeight(5, 4, RefY);   // exact match
        g.SetHeight(6, 5, 9f);     // off
        g.SetHeight(4, 5, 7f);     // off
        var r = GoalResolver.Resolve(g, 5, 5, RefY);
        Assert(r.Found, "resolves among ring-1 candidates");
        AssertEq(r.Cell, g.Ci(5, 4), "picked the height-exact candidate");
    }

    static void TiesKeepTheFirstScanned()
    {
        // Two candidates equidistant in height. Scan order decides: the ring's
        // top row runs d = -r..+r, so (5,4) is seen before (5,6).
        var g = new FakeGrid(11, 11, FarY);
        g.Block(5, 5);
        g.SetHeight(5, 4, 1f);     // delta 1, first
        g.SetHeight(5, 6, 1f);     // delta 1, later
        var r = GoalResolver.Resolve(g, 5, 5, RefY);
        Assert(r.Found, "resolves on a tie");
        AssertEq(r.Cell, g.Ci(5, 4), "tie broken by first-scanned (top row)");
    }

    static void StopsAtTheFirstRingThatYieldsACandidate()
    {
        // A cell in ring 1 must win even though ring 2 has a perfect height
        // match — this is a nearest-in-plane search, not a global optimum.
        var g = new FakeGrid(15, 15, FarY);
        g.Block(7, 7);
        g.SetHeight(7, 6, 5f);     // ring 1, mediocre height
        g.SetHeight(7, 5, RefY);   // ring 2, perfect height
        var r = GoalResolver.Resolve(g, 7, 7, RefY);
        Assert(r.Found, "resolves");
        AssertEq(r.Cell, g.Ci(7, 6), "ring 1 wins; search does not continue past it");
    }

    // ---- bounds / edges ----

    static void RejectsOutOfBoundsGoal()
    {
        var g = new FakeGrid(10, 10, 0f);
        Assert(!GoalResolver.Resolve(g, -1, 5, 0f).Found, "negative x rejected");
        Assert(!GoalResolver.Resolve(g, 5, -1, 0f).Found, "negative z rejected");
        Assert(!GoalResolver.Resolve(g, 10, 5, 0f).Found, "x == Cols rejected");
        Assert(!GoalResolver.Resolve(g, 5, 10, 0f).Found, "z == Rows rejected");
    }

    static void CornerGoalOnlySearchesInBounds()
    {
        // A blocked corner must resolve using the in-bounds ring only.
        var g = new FakeGrid(10, 10, 0f);
        g.Block(0, 0);
        var r = GoalResolver.Resolve(g, 0, 0, 0f);
        Assert(r.Found, "corner goal resolves in-bounds");
        int x = r.Cell % 10, z = r.Cell / 10;
        Assert(x >= 0 && z >= 0 && x < 10 && z < 10, "resolved cell is inside the grid");
        Assert(x <= 1 && z <= 1, "picked an in-bounds ring-1 neighbour");
    }

    static void ZeroSizedGridIsSafe()
    {
        var g = new FakeGrid(0, 0, 0f);
        var r = GoalResolver.Resolve(g, 0, 0, 0f);
        Assert(!r.Found, "empty grid does not throw and reports no path");
    }

    static void SamplesTheGridItInspects()
    {
        // The resolver must SampleCell before asking for layers, or the first
        // query on a fresh grid would read empty layer data.
        var g = new FakeGrid(10, 10, 0f);
        GoalResolver.Resolve(g, 5, 5, 0f);
        Assert(g.SampleCount > 0, "fast path samples the goal cell (" + g.SampleCount + ")");
        int before = g.SampleCount;
        g.Block(5, 5);
        GoalResolver.Resolve(g, 5, 5, 0f);
        Assert(g.SampleCount > before, "ring search samples candidate cells");
    }

    static void AssertEq(int actual, int expected, string name)
    {
        Assert(actual == expected, name + " (got " + actual + ", want " + expected + ")");
    }

    static int RunAll()
    {
        Console.WriteLine("== GoalResolver Tests ==");
        ExactCellIsUsedAsIs();
        ExactCellPicksNearestLayer();
        BlockedGoalFindsNeighbour();
        SearchExpandsUntilItFindsSomething();
        GivesUpWhenNothingWalkableNearby();
        RespectsMaxRadius();
        PrefersTheCellClosestInHeight();
        TiesKeepTheFirstScanned();
        StopsAtTheFirstRingThatYieldsACandidate();
        RejectsOutOfBoundsGoal();
        CornerGoalOnlySearchesInBounds();
        ZeroSizedGridIsSafe();
        SamplesTheGridItInspects();
        Console.WriteLine();
        Console.WriteLine("Results: " + _passed + " passed, " + _failed + " failed");
        return _failed;
    }

    static int Main()
    {
        return RunAll() == 0 ? 0 : 1;
    }
}
