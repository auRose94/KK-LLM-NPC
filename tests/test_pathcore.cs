// Unit tests for PathCore.cs — pure C#, no UnityEngine.
// To run: mcs -target:exe -out:/tmp/t_pf.exe tests/test_pathcore.cs src/PathCore.cs && mono /tmp/t_pf.exe
using System;
using KKLLMNPC;

namespace KKLLMNPC.Tests
{
    public static class PathCoreTests
    {
        private static int _passed = 0;
        private static int _failed = 0;

        private static void Assert(bool condition, string msg)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("  PASS: " + msg);
            }
            else
            {
                _failed++;
                Console.WriteLine("  FAIL: " + msg);
            }
        }

        public static void Main()
        {
            Console.WriteLine("=== PathCore Unit Tests ===\n");

            TestShouldReplan();
            TestAstarSolver();
            TestAdaptiveCell();
            TestAutoFloor();

            Console.WriteLine("\n=== Results: " + _passed + " passed, " + _failed + " failed ===");
            Environment.Exit(_failed > 0 ? 1 : 0);
        }

        // ------------------------------------------------------------------
        // PathPolicy.ShouldReplan tests (≥ 12 cases)
        // ------------------------------------------------------------------
        static void TestShouldReplan()
        {
            Console.WriteLine("--- PathPolicy.ShouldReplan ---");

            // Case 1: No path — should not replan.
            Assert(!PathPolicy.ShouldReplan(false, 5, 3f, false, 0f, 0f, false),
                "no path → false");

            // Case 2: Path exhausted (pathRemaining == 0).
            Assert(PathPolicy.ShouldReplan(true, 0, 3f, false, 0f, 0f, false),
                "path exhausted → true");

            // Case 3: Target moved > 2m from goal.
            Assert(PathPolicy.ShouldReplan(true, 5, 3.5f, true, 0f, 0f, false),
                "target moved 3.5m → true");

            // Case 4: Target moved, but within threshold.
            Assert(!PathPolicy.ShouldReplan(true, 5, 1.5f, true, 0f, 0f, false),
                "target moved 1.5m (within threshold) → false");

            // Case 5: Deviation > 0.8m AND timeSincePath > 0.75s.
            Assert(PathPolicy.ShouldReplan(true, 5, 0.5f, false, 1.0f, 1.0f, false),
                "deviation 1.0m, time 1.0s → true");

            // Case 6: Deviation > 0.8m but time < 0.75s (too soon).
            Assert(!PathPolicy.ShouldReplan(true, 5, 0.5f, false, 1.0f, 0.5f, false),
                "deviation 1.0m, time 0.5s (too soon) → false");

            // Case 7: Deviation < 0.8m (even with enough time).
            Assert(!PathPolicy.ShouldReplan(true, 5, 0.5f, false, 0.5f, 1.0f, false),
                "deviation 0.5m (below threshold) → false");

            // Case 8: Teleported.
            Assert(PathPolicy.ShouldReplan(true, 5, 0.5f, false, 0f, 0f, true),
                "teleported → true");

            // Case 9: Path age exceeded (default 30s).
            Assert(PathPolicy.ShouldReplan(true, 5, 0.5f, false, 0f, 35f, false),
                "path age 35s (default max 30s) → true");

            // Case 10: Path age within limit.
            Assert(!PathPolicy.ShouldReplan(true, 5, 0.5f, false, 0f, 20f, false),
                "path age 20s (within limit) → false");

            // Case 11: Custom maxPathAge.
            Assert(PathPolicy.ShouldReplan(true, 5, 0.5f, false, 0f, 15f, false, 10f),
                "path age 15s with max 10s → true");

            // Case 12: All conditions false — no replan needed.
            Assert(!PathPolicy.ShouldReplan(true, 5, 0.5f, false, 0f, 1f, false),
                "all conditions false → false");

            // Case 13: Target moved exactly at threshold (2.0m).
            Assert(!PathPolicy.ShouldReplan(true, 5, 2.0f, true, 0f, 0f, false),
                "target moved exactly 2.0m (not > 2) → false");

            // Case 14: Target moved just above threshold.
            Assert(PathPolicy.ShouldReplan(true, 5, 2.01f, true, 0f, 0f, false),
                "target moved 2.01m → true");

            Console.WriteLine();
        }

        // ------------------------------------------------------------------
        // AstarSolver tests
        // ------------------------------------------------------------------
        static void TestAstarSolver()
        {
            Console.WriteLine("--- AstarSolver ---");

            // Test 1: Straight line, no obstacles.
            {
                bool[,] grid = new bool[10, 10];
                for (int x = 0; x < 10; x++)
                    for (int z = 0; z < 10; z++)
                        grid[x, z] = true;
                var solver = new AstarSolver(grid, 10, 10, 0, 0, 9, 9);
                int[] path = solver.Solve();
                Assert(path != null && path.Length >= 4,
                    "straight line path found (len=" + (path != null ? path.Length / 2 : 0) + ")");
            }

            // Test 2: Obstacle in the way — should route around.
            {
                bool[,] grid = new bool[10, 10];
                for (int x = 0; x < 10; x++)
                    for (int z = 0; z < 10; z++)
                        grid[x, z] = true;
                // Block a vertical wall in the middle.
                for (int z = 2; z < 8; z++)
                    grid[5, z] = false;
                var solver = new AstarSolver(grid, 10, 10, 0, 0, 9, 9);
                int[] path = solver.Solve();
                Assert(path != null && path.Length >= 4,
                    "obstacle routed around (len=" + (path != null ? path.Length / 2 : 0) + ")");
            }

            // Test 3: No route — fully enclosed area.
            {
                bool[,] grid = new bool[10, 10];
                // All walkable except a ring around start.
                for (int x = 0; x < 10; x++)
                    for (int z = 0; z < 10; z++)
                        grid[x, z] = true;
                // Close off the start area.
                grid[1, 0] = false; grid[1, 1] = false; grid[0, 1] = false;
                // Make the goal unreachable by blocking the only exit.
                for (int x = 0; x < 10; x++) grid[x, 5] = false;
                var solver = new AstarSolver(grid, 10, 10, 0, 0, 9, 9);
                int[] path = solver.Solve();
                Assert(path == null,
                    "no route → null (goal behind wall)");
            }

            // Test 4: Time budget exceeded — should return partial path.
            {
                bool[,] grid = new bool[100, 100];
                for (int x = 0; x < 100; x++)
                    for (int z = 0; z < 100; z++)
                        grid[x, z] = true;
                var solver = new AstarSolver(grid, 100, 100, 0, 0, 99, 99,
                    nodeBudget: 1000000, timeBudgetMs: 0.001f); // tiny budget
                int[] path = solver.Solve();
                Assert(path != null && path.Length >= 2,
                    "time budget exceeded → partial path (len=" + (path != null ? path.Length / 2 : 0) + ", expanded=" + solver.ExpandedCount + ")");
            }

            // Test 5: Node budget exceeded — should return partial path.
            {
                bool[,] grid = new bool[100, 100];
                for (int x = 0; x < 100; x++)
                    for (int z = 0; z < 100; z++)
                        grid[x, z] = true;
                var solver = new AstarSolver(grid, 100, 100, 0, 0, 99, 99,
                    nodeBudget: 100, timeBudgetMs: 10000f); // generous time, tight budget
                int[] path = solver.Solve();
                Assert(path != null && path.Length >= 2,
                    "node budget exceeded → partial path (expanded=" + solver.ExpandedCount + ")");
            }

            // Test 6: Start == Goal.
            {
                bool[,] grid = new bool[5, 5];
                for (int x = 0; x < 5; x++)
                    for (int z = 0; z < 5; z++)
                        grid[x, z] = true;
                var solver = new AstarSolver(grid, 5, 5, 2, 2, 2, 2);
                int[] path = solver.Solve();
                Assert(path != null && path.Length == 2,
                    "start == goal → single node (len=" + (path != null ? path.Length : 0) + ")");
            }

            Console.WriteLine();
        }

        // ------------------------------------------------------------------
        // AdaptiveCell tests
        // ------------------------------------------------------------------
        static void TestAdaptiveCell()
        {
            Console.WriteLine("--- AdaptiveCell ---");

            // Test 1: Small span → cell clamped to 0.5 minimum.
            {
                var (cell, cols, rows) = AdaptiveCell.Compute(10f, 10f);
                Assert(cell >= 0.5f && cell <= 4f,
                    "small span cell in range [0.5, 4.0] (cell=" + cell + ")");
                Assert(cols >= 2 && rows >= 2,
                    "small span cols/rows >= 2 (cols=" + cols + ", rows=" + rows + ")");
            }

            // Test 2: Large span (2000m) → adaptive cell.
            {
                var (cell, cols, rows) = AdaptiveCell.Compute(2000f, 2000f);
                Assert(cell >= 0.5f && cell <= 4f,
                    "2000m span cell in range (cell=" + cell + ")");
                Assert((long)cols * rows <= AdaptiveCell.MaxCells,
                    "2000m span within max cells (cols=" + cols + ", rows=" + rows + ", total=" + (cols * rows) + ")");
            }

            // Test 3: Rectangular map.
            {
                var (cell, cols, rows) = AdaptiveCell.Compute(100f, 50f);
                Assert(cols > rows,
                    "rectangular map cols > rows (cols=" + cols + ", rows=" + rows + ")");
            }

            // Test 4: Very large map → cell inflation kicks in.
            {
                var (cell, cols, rows) = AdaptiveCell.Compute(5000f, 5000f);
                Assert((long)cols * rows <= AdaptiveCell.MaxCells,
                    "5000m span within max cells (cols=" + cols + ", rows=" + rows + ", total=" + (cols * rows) + ")");
            }

            Console.WriteLine();
        }

        // ------------------------------------------------------------------
        // AutoFloor tests
        // ------------------------------------------------------------------
        static void TestAutoFloor()
        {
            Console.WriteLine("--- AutoFloor ---");

            // Test 1: Single cluster — all values within gap.
            {
                float[] samples = { 1.0f, 1.1f, 0.9f, 1.2f, 1.05f };
                float[] layers = AutoFloor.Cluster(samples);
                Assert(layers.Length == 1,
                    "single cluster → 1 layer (got " + layers.Length + ")");
            }

            // Test 2: Two distinct floors.
            {
                float[] samples = { 1.0f, 1.1f, 1.05f, 5.0f, 5.2f, 4.8f };
                float[] layers = AutoFloor.Cluster(samples);
                Assert(layers.Length == 2,
                    "two floors → 2 layers (got " + layers.Length + ")");
            }

            // Test 3: Three floors with gap threshold.
            {
                float[] samples = { 0.0f, 3.0f, 6.0f };
                float[] layers = AutoFloor.Cluster(samples, gapThreshold: 1.5f);
                Assert(layers.Length == 3,
                    "three floors with gap 1.5 → 3 layers (got " + layers.Length + ")");
            }

            // Test 4: Max layers cap.
            {
                float[] samples = new float[20];
                for (int i = 0; i < 20; i++) samples[i] = (float)i * 2f;
                float[] layers = AutoFloor.Cluster(samples, maxLayers: 5);
                Assert(layers.Length == 5,
                    "max layers 5 cap → 5 layers (got " + layers.Length + ")");
            }

            // Test 5: Empty samples.
            {
                float[] layers = AutoFloor.Cluster(new float[0]);
                Assert(layers.Length == 0,
                    "empty samples → 0 layers");
            }

            // Test 6: Null samples.
            {
                float[] layers = AutoFloor.Cluster(null);
                Assert(layers.Length == 0,
                    "null samples → 0 layers");
            }

            // Test 7: Gap threshold = 0 (every distinct value is a layer).
            {
                float[] samples = { 1f, 2f, 3f };
                float[] layers = AutoFloor.Cluster(samples, gapThreshold: 0f);
                Assert(layers.Length == 3,
                    "gap=0 → 3 layers (got " + layers.Length + ")");
            }

            Console.WriteLine();
        }
    }
}
