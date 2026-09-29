using System;
using System.Diagnostics;
using ZhaDai.Automation;

namespace ZhaDai.Core.Tests;

/// <summary>
/// Times the A* search the executor uses for walking, over a big synthetic world.
///
/// The first real-machine run stuttered about twice a second while the character stood still: the search was
/// recomputed every <c>PathRecomputeTicks</c> and each one is a synchronous hitch on the game thread. This
/// tool exists so the window and node limit are chosen from a measurement instead of a guess, and so a future
/// change that makes the search ten times dearer shows up here rather than in someone's game.
/// </summary>
internal static class PathBench
{
    public static void Run()
    {
        Console.WriteLine("A* 寻路开销（真机卡顿就是它的单次耗时）");
        Console.WriteLine("  窗口半径  节点上限   单次毫秒   找到路径   要挖格数");

        const int width = 4000;
        const int height = 400;
        FakeBridge bridge = new FakeBridge(width, height);
        CarveTerrain(bridge, width, height);

        foreach (int window in new[] { 32, 48, 64, 96 })
        {
            foreach (int limit in new[] { 4000, 8000, 20000 })
            {
                PathOptions options = new PathOptions { WindowRadius = window, NodeLimit = limit };
                TilePathfinder finder = new TilePathfinder(bridge, options);

                // Warm up once so JIT time is not billed to the first measurement.
                finder.FindPath(100, 20, 140, 20);

                const int runs = 20;
                Stopwatch watch = Stopwatch.StartNew();
                PathResult? last = null;
                for (int i = 0; i < runs; i++)
                {
                    last = finder.FindPath(100, 20, 100 + 200 + i, 20);
                }

                watch.Stop();
                double perSearch = watch.Elapsed.TotalMilliseconds / runs;
                bool found = last != null && last.Found;
                int digs = last == null ? 0 : last.DigTiles;
                Console.WriteLine($"  {window,8}  {limit,8}  {perSearch,8:0.00}  {found,8}  {digs,8}");

                // The expensive case, and the one the first real-machine run hit: the goal is far outside the
                // search window, so the search can never succeed and expands until it runs out of nodes or
                // area -- every single recompute.
                Stopwatch doomed = Stopwatch.StartNew();
                for (int i = 0; i < runs; i++)
                {
                    finder.FindPath(100, 20, 100 + 3000, 20);
                }

                doomed.Stop();
                double perDoomed = doomed.Elapsed.TotalMilliseconds / runs;
                Console.WriteLine($"  {window,8}  {limit,8}  {perDoomed,8:0.00}  （目标在窗口外，必然失败）");
            }
        }
    }

    /// <summary>Surface plus scattered rock, so the search has to look around rather than run in a line.</summary>
    private static void CarveTerrain(FakeBridge bridge, int width, int height)
    {
        int surface = height / 3;
        bridge.FillSolid(0, surface, width - 1, height - 1);
        Random random = new Random(20260929);
        for (int x = 0; x < width; x++)
        {
            int bump = random.Next(0, 3);
            for (int y = surface - bump; y < surface; y++)
            {
                bridge.FillSolid(x, y, x, y);
            }
        }

        for (int i = 0; i < width / 4; i++)
        {
            int x = random.Next(1, width - 2);
            int y = random.Next(surface - 12, surface - 1);
            bridge.FillSolid(x, y, x, y + 1);
        }
    }
}
