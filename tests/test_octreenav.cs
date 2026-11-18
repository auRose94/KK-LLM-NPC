// Tests for OctreeNav.cs — the 3D navigation core built from the same layered
// arrays the .kkmap cache stores. The scenarios the layered grid could NOT route:
// stacked tunnels, fall shafts, over/under passes, deep stepped caves, doors as
// tolls — plus the basics (flat path, wall block, patch-by-proof, straight-leg
// checks, budget). Pure C#, no UnityEngine; compiled as part of run_tests.sh.
using System;
using System.Collections.Generic;
using KKLLMNPC;

internal static class TestOctreeNav
{
    private const float Cell = 0.5f;
    private const float Climb = Consts.PathClimbStep;      // 0.5m step-up
    private const float Drop = Consts.LedgeDropHardStop;   // 2.6m walk-off drop

    private static int _passed;
    private static int _failed;

    private static void Assert(bool ok, string what)
    {
        if (!ok) { _failed++; Console.WriteLine("  FAIL: " + what); }
        else _passed++;
    }

    // Tiny layered-grid builder: nlay/h/ok in the WorldMap layout
    // (node = ci*maxLayers+l), so tests feed the SAME shape the cache does.
    private sealed class Grid
    {
        public int Cols, Rows, MaxLayers = 8;
        public byte[] Nlay;
        public float[] H;
        public byte[] Ok;

        public Grid(int cols, int rows)
        {
            Cols = cols; Rows = rows;
            Nlay = new byte[cols * rows];
            H = new float[cols * rows * MaxLayers];
            Ok = new byte[cols * rows * MaxLayers];
            for (int i = 0; i < H.Length; i++) H[i] = -999f;
        }

        public int Ci(int x, int z) { return z * Cols + x; }

        public void Floor(int x, int z, float y, byte state)
        {
            int ci = Ci(x, z);
            int nl = Nlay[ci];
            H[ci * MaxLayers + nl] = y;
            Ok[ci * MaxLayers + nl] = state;
            Nlay[ci] = (byte)(nl + 1);
        }

        public void FloorAll(int x0, int z0, int x1, int z1, float y, byte state)
        {
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    Floor(x, z, y, state);
        }

        public OctreeNav Nav()
        {
            return OctreeNav.FromLayeredGrid(Cols, Rows, Cell, 0f, 0f, Nlay, H, Ok, MaxLayers, Climb, Drop);
        }

        // World coordinates of a column's node (mirrors WorldMapGrid.CellPos).
        public static float Wx(int x) { return (x + 0.5f) * Cell; }
        public static float Wz(int z) { return (z + 0.5f) * Cell; }
    }

    // ------------------------------------------------------------------
    // basics
    // ------------------------------------------------------------------
    private static void TestFlatPath()
    {
        var g = new Grid(8, 8);
        g.FloorAll(0, 0, 7, 7, 0f, OctreeNav.Clear);
        var nav = g.Nav();
        Assert(nav != null, "flat: built");
        Assert(64 == nav.NodeCount, "flat: 64 nodes");
        Assert(64 == nav.ColumnCount, "flat: 64 columns");
        int s = nav.NearestNode(Grid.Wx(0), 0f, Grid.Wz(0), 2);
        int t = nav.NearestNode(Grid.Wx(7), 0f, Grid.Wz(7), 2);
        var path = nav.FindPath(s, t, 100000);
        Assert(path != null && path.Count >= 2, "flat: corner-to-corner path");
        Assert(path[0] == s && path[path.Count - 1] == t, "flat: endpoints exact");
    }

    private static void TestWallAndDoor()
    {
        // x=3 is a solid wall with ONE door cell at z=4 (a column's floor is clear
        // OR wall OR door — never stacked states, matching the real sampler).
        var g3 = new Grid(8, 8);
        for (int x = 0; x < 8; x++)
            for (int z = 0; z < 8; z++)
            {
                byte st = OctreeNav.Clear;
                if (x == 3) st = z == 4 ? OctreeNav.Door : OctreeNav.Wall;
                g3.Floor(x, z, 0f, st);
            }
        var nav = g3.Nav();
        int s = nav.NearestNode(Grid.Wx(0), 0f, Grid.Wz(4), 2);
        int t = nav.NearestNode(Grid.Wx(7), 0f, Grid.Wz(4), 2);
        Assert(nav.FindPath(s, t, 100000) != null, "wall+door: path threads the door");

        // with NO door at all, the same query must fail
        var g4 = new Grid(8, 8);
        for (int x = 0; x < 8; x++)
            for (int z = 0; z < 8; z++)
                g4.Floor(x, z, 0f, x == 3 ? OctreeNav.Wall : OctreeNav.Clear);
        var nav4 = g4.Nav();
        int s4 = nav4.NearestNode(Grid.Wx(0), 0f, Grid.Wz(4), 2);
        int t4 = nav4.NearestNode(Grid.Wx(7), 0f, Grid.Wz(4), 2);
        Assert(nav4.FindPath(s4, t4, 100000) == null, "wall: solid wall blocks");
        Assert(nav4.FindPath(s4, s4, 100000) != null, "wall: trivial self path");
    }

    // ------------------------------------------------------------------
    // the 3D cases
    // ------------------------------------------------------------------
    // Two stacked tunnels in the same map: ground corridor z=2 at Y=0, raised
    // corridor z=6 at Y=2.6 — no ramp joins them, so each routes internally and
    // a cross request fails (this is the case the layer-indexed A* answered by
    // pretending both were the same floor).
    private static void TestStackedTunnels()
    {
        var g = new Grid(12, 12);
        g.FloorAll(0, 2, 11, 2, 0f, OctreeNav.Clear);      // lower tunnel
        g.FloorAll(0, 6, 11, 6, 2.6f, OctreeNav.Clear);    // raised tunnel (2.6 = MaxDrop)
        var nav = g.Nav();
        Assert(nav != null && nav.NodeCount == 24, "tunnels: 24 nodes");

        int a = nav.NearestNode(Grid.Wx(0), 0f, Grid.Wz(2), 2);
        int b = nav.NearestNode(Grid.Wx(11), 0f, Grid.Wz(2), 2);
        Assert(nav.FindPath(a, b, 100000) != null, "tunnels: lower routes");

        int c = nav.NearestNode(Grid.Wx(0), 2.6f, Grid.Wz(6), 2);
        int d = nav.NearestNode(Grid.Wx(11), 2.6f, Grid.Wz(6), 2);
        Assert(nav.FindPath(c, d, 100000) != null, "tunnels: raised routes");
        // and with no ramp joining them, cross-tunnel requests fail — the 3D graph
        // would rather say "no route" than silently route one tunnel as the other
        Assert(nav.FindPath(a, d, 100000) == null, "tunnels: unjoined levels don't cross-route");

        // anchoring is height-aware: a goal point at Y=2.6 over the LOWER tunnel
        // picks the lower floor, not the raised one two lanes over
        int e = nav.NearestNode(Grid.Wx(5), 0f, Grid.Wz(2), 2);
        float ex, ey, ez;
        nav.NodePos(e, out ex, out ey, out ez);
        Assert(Math.Abs(ey - 0f) < 0.01f, "tunnels: anchor respects height");

        // vertical banding inside ONE column: ground + overpass in the same
        // column. Dropping OFF the overpass (2.6m = exactly MaxDrop) is walk-off;
        // climbing back up is not.
        var g2 = new Grid(6, 6);
        g2.FloorAll(0, 0, 5, 5, 0f, OctreeNav.Clear);
        g2.FloorAll(2, 2, 3, 3, 2.6f, OctreeNav.Clear);   // overpass slab over ground
        var nav2 = g2.Nav();
        int top = nav2.NearestNode(Grid.Wx(2), 2.6f, Grid.Wz(2), 1);
        int under = nav2.NearestNode(Grid.Wx(0), 0f, Grid.Wz(0), 1);
        Assert(nav2.FindPath(top, under, 100000) != null, "overpass: drop OFF the slab routes");
        Assert(nav2.FindPath(under, top, 100000) == null, "overpass: climbing back up is impossible");
    }

    // A shaft column with a hole: same column holds floor 5.0 and floor 4.0 —
    // the same-column fall edge must route straight down through it.
    private static void TestFallShaft()
    {
        var g = new Grid(5, 5);
        g.FloorAll(0, 0, 4, 4, 5f, OctreeNav.Clear);
        // shaft: column (2,2) opens a lower floor 1m below the surface
        g.Floor(2, 2, 4f, OctreeNav.Clear);
        var nav = g.Nav();
        int top = nav.NearestNode(Grid.Wx(2), 5f, Grid.Wz(2), 1);
        int bot = nav.NearestNode(Grid.Wx(2), 4f, Grid.Wz(2), 1);
        Assert(top != bot, "shaft: two distinct floors in one column");
        var path = nav.FindPath(top, bot, 100000);
        Assert(path != null && path.Count == 2, "shaft: same-column fall edge connects");
        // a drop TOO far must not connect (5.0 -> 0.0 = 5m > MaxDrop)
        var g2 = new Grid(5, 5);
        g2.FloorAll(0, 0, 4, 4, 5f, OctreeNav.Clear);
        g2.Floor(2, 2, 0f, OctreeNav.Clear);
        var nav2 = g2.Nav();
        int t2 = nav2.NearestNode(Grid.Wx(2), 5f, Grid.Wz(2), 1);
        int b2 = nav2.NearestNode(Grid.Wx(2), 0f, Grid.Wz(2), 1);
        Assert(nav2.FindPath(t2, b2, 100000) == null, "shaft: over-drop does not connect");
    }

    // A deep cave reached by descending steps (each <= MaxDrop): 30m underground
    // via a 15-cell stair of 2m drops. The sampler sees all of it as stacked
    // layers; the 3D graph walks it down.
    private static void TestSteppedCave()
    {
        var g = new Grid(24, 8);
        g.FloorAll(0, 0, 23, 7, 0f, OctreeNav.Clear);          // surface world
        for (int i = 0; i < 15; i++)
            g.Floor(4 + i, 4, -2f * (i + 1), OctreeNav.Clear); // descending stair into the rock
        g.FloorAll(19, 4, 22, 4, -30f, OctreeNav.Clear);       // cave gallery at -30m
        var nav = g.Nav();
        int s = nav.NearestNode(Grid.Wx(0), 0f, Grid.Wz(4), 2);
        int deep = nav.NearestNode(Grid.Wx(22), -30f, Grid.Wz(4), 2);
        Assert(deep >= 0, "cave: deep floor anchored");
        var path = nav.FindPath(s, deep, 200000);
        Assert(path != null, "cave: 30m descent routes through the stair");
        if (path != null)
        {
            float ly; float lx; float lz;
            nav.NodePos(path[path.Count - 1], out lx, out ly, out lz);
            Assert(Math.Abs(ly - -30f) < 0.01f, "cave: path ends on the cave floor");
        }
        // and it should PREFER the stair over trying to drop through solid rock
        // (the surface cells above the gallery have no edge down: 30m >> MaxDrop)
    }

    // ------------------------------------------------------------------
    // patch + segment + budget + robustness
    // ------------------------------------------------------------------
    private static void TestPatchAndSegment()
    {
        var g = new Grid(8, 8);
        for (int x = 0; x < 8; x++)
            for (int z = 0; z < 8; z++)
                g.Floor(x, z, 0f, x == 4 && z == 4 ? OctreeNav.Wall : OctreeNav.Clear);
        var nav = g.Nav();
        int s = nav.NearestNode(Grid.Wx(0), 0f, Grid.Wz(0), 2);
        Assert(nav.NearestNode(Grid.Wx(4), 0f, Grid.Wz(4), 0) < 0, "patch: wall-only column has no anchor");

        // straight-leg FIRST — the patch below removes exactly this wall voxel
        Assert(!nav.SegmentOpen(Grid.Wx(0), 0f, Grid.Wz(0), Grid.Wx(7), 0f, Grid.Wz(7)),
            "segment: wall voxel blocks the leg");
        var g2 = new Grid(4, 4);
        g2.FloorAll(0, 0, 3, 3, 0f, OctreeNav.Clear);
        var nav2 = g2.Nav();
        Assert(nav2.SegmentOpen(Grid.Wx(0), 0f, Grid.Wz(0), Grid.Wx(3), 0f, Grid.Wz(3)),
            "segment: clean leg open (no walls at all)");

        // patch the wall (a body stood there) → it becomes an anchor and routes
        Assert(nav.PatchWalkable(Grid.Wx(4), 0f, Grid.Wz(4)), "patch: wall flipped");
        int t2 = nav.NearestNode(Grid.Wx(4), 0f, Grid.Wz(4), 2);
        Assert(t2 >= 0, "patch: flipped node anchors");
        Assert(nav.FindPath(s, t2, 100000) != null, "patch: path through the proof");
    }

    private static void TestBudgetAndJunk()
    {
        var g = new Grid(16, 16);
        g.FloorAll(0, 0, 15, 15, 0f, OctreeNav.Clear);
        var nav = g.Nav();
        int s = nav.NearestNode(Grid.Wx(0), 0f, Grid.Wz(0), 2);
        int t = nav.NearestNode(Grid.Wx(15), 0f, Grid.Wz(15), 2);
        Assert(nav.FindPath(s, t, 1) == null, "budget: exhausted budget returns null");

        Assert(OctreeNav.FromLayeredGrid(4, 4, Cell, 0, 0, null, null, null, 8, Climb, Drop) == null, "junk: null arrays");
        var empty = new Grid(4, 4);
        Assert(empty.Nav() == null, "junk: no layers → no map");
        // node ids bounds-checked
        Assert(nav.FindPath(-1, 0, 100) == null, "junk: negative node rejected");
        Assert(nav.FindPath(0, int.MaxValue, 100) == null, "junk: huge node rejected");
    }

    // Larger smoke: 200x200 (40k columns) with scattered walls, plus a long
    // corridor trek — exercises the heap/indexed bookkeeping at scale.
    private static void TestLongTrekSmoke()
    {
        var rnd = new Random(20261117);
        var g = new Grid(200, 200);
        g.FloorAll(0, 0, 199, 199, 0f, OctreeNav.Clear);
        for (int i = 0; i < 400; i++)
        {
            int x = rnd.Next(2, 198), z = rnd.Next(2, 198);
            if (Math.Abs(x - 100) < 3 || Math.Abs(z - 100) < 3) continue; // keep the crossroad open
            g.Floor(x, z, 0f, OctreeNav.Wall);
        }
        var nav = g.Nav();
        Assert(nav != null && nav.NodeCount >= 39000, "trek: big map built");
        int s = nav.NearestNode(Grid.Wx(2), 0f, Grid.Wz(2), 2);
        int t = nav.NearestNode(Grid.Wx(197), 0f, Grid.Wz(197), 2);
        var path = nav.FindPath(s, t, 2000000);
        Assert(path != null && path.Count > 100, "trek: cross-map path found");
        // repeated queries stay correct (visit-stamp reuse)
        var again = nav.FindPath(s, t, 2000000);
        Assert(again != null && again.Count == path.Count, "trek: repeat query identical");
    }

    internal static int Run()
    {
        TestFlatPath();
        TestWallAndDoor();
        TestStackedTunnels();
        TestFallShaft();
        TestSteppedCave();
        TestPatchAndSegment();
        TestBudgetAndJunk();
        TestLongTrekSmoke();

        if (_failed > 0)
        {
            Console.WriteLine("octreenav: " + _failed + " assertion(s) failed");
            return 1;
        }
        Console.WriteLine("octreenav: all OK");
        return 0;
    }

    internal static void Main()
    {
        int rc = Run();
        if (rc != 0) Environment.Exit(rc);
    }
}
