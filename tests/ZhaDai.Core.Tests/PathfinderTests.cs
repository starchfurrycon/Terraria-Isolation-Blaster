using System;
using System.Collections.Generic;
using ZhaDai.Automation;

namespace ZhaDai.Core.Tests;

/// <summary>
/// The routing tests. The claim being checked is not "A* exists" but "the route is better than walking
/// straight at the target": fewer tiles mined, no routes through lava, and no plan through rock the
/// pickaxe cannot break.
/// </summary>
internal static class PathfinderTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("寻路（A*）");

        WallDetour(check);
        LavaIsNotARoute(check);
        UnbreakableRockIsAWall(check);
        DigsThroughWhenThereIsNoWayAround(check);
        ExecutorUsesTheRoute(check);
    }

    /// <summary>A wall with a gap two tiles above: the route goes through the gap and mines nothing.</summary>
    private static void WallDetour(Action<bool, string> check)
    {
        FakeBridge bridge = new();
        bridge.FillSolid(60, 0, 60, 18);

        TilePathfinder finder = new(bridge, new PathOptions());
        PathResult path = finder.FindPath(40, 19, 80, 19);

        check(path.Found, $"墙上有缺口时找得到路（{path.Failure}）");
        check(path.DigTiles == 0, $"绕过去而不是挖穿（要挖 {path.DigTiles} 格）");
        check(
            path.Waypoints.Exists(point => point.X == 60),
            "路线确实穿过了 x=60 那一列（在墙下方留出的通道里）");
    }

    /// <summary>Lava across the whole corridor: the route must not step in it.</summary>
    private static void LavaIsNotARoute(Action<bool, string> check)
    {
        FakeBridge bridge = new();
        bridge.FillSolid(60, 0, 60, 18);
        bridge.FillLiquid(58, 0, 62, 18, 2);

        TilePathfinder finder = new(bridge, new PathOptions());
        PathResult path = finder.FindPath(40, 19, 80, 19);

        if (path.Found)
        {
            bool wet = false;
            foreach ((int x, int y) in path.Waypoints)
            {
                if (bridge.LiquidKind(x, y) == 2)
                {
                    wet = true;
                    break;
                }
            }

            check(!wet, "路线一格子岩浆都不踩");
        }
        else
        {
            check(true, "整条通道都是岩浆时宁可不通也不硬闯（" + path.Failure + "）");
        }

        FakeBridge immune = new();
        immune.FillSolid(60, 0, 60, 18);
        immune.FillLiquid(58, 0, 62, 18, 2);
        immune.PlayerLavaImmune = true;
        TilePathfinder fireproof = new(immune, new PathOptions());
        PathResult through = fireproof.FindPath(40, 19, 80, 19);
        check(through.Found, "有岩浆免疫（黑曜石皮/岩浆鲨）时允许规划穿过去");
    }

    /// <summary>神庙砖 (226) needs a 210% pickaxe: with a 100% pick it is a wall, not a tunnel.</summary>
    private static void UnbreakableRockIsAWall(Action<bool, string> check)
    {
        FakeBridge bridge = new();
        bridge.FillType(0, 10, 399, 10, 226);

        TilePathfinder finder = new(bridge, new PathOptions());
        PathResult blocked = finder.FindPath(40, 19, 40, 9);

        // The slab spans the world at y=10, but the player can still fall around it only by leaving the
        // window, so the search must not claim a route that mines through it.
        if (blocked.Found)
        {
            check(
                blocked.Waypoints.TrueForAll(point => bridge.TileType(point.X, point.Y) != 226),
                "镐力不够时不会规划挖穿神庙砖");
        }
        else
        {
            check(true, "镐力不够时如实报告没有通路：" + blocked.Failure);
        }

        FakeBridge strong = new();
        strong.FillType(0, 10, 399, 10, 226);
        strong.BestPickPower = 210;
        TilePathfinder picksaw = new(strong, new PathOptions());
        PathResult mined = picksaw.FindPath(40, 19, 40, 9);
        check(
            mined.Found && mined.Waypoints.Exists(point => strong.TileType(point.X, point.Y) == 226),
            "换上锯镐后同一堵墙就变成可挖的隧道");
    }

    /// <summary>No gap anywhere: digging is the answer, and the search should take the short one.</summary>
    private static void DigsThroughWhenThereIsNoWayAround(Action<bool, string> check)
    {
        FakeBridge bridge = new();
        bridge.FillSolid(60, 0, 60, 59);

        TilePathfinder finder = new(bridge, new PathOptions());
        PathResult path = finder.FindPath(40, 19, 80, 19);

        check(path.Found, $"完全封死时仍然给出挖穿方案（{path.Failure}）");
        check(path.DigTiles >= 1 && path.DigTiles <= 3, $"只挖最薄的一层（要挖 {path.DigTiles} 格）");
    }

    /// <summary>
    /// The end to end claim: with a detour available the executor arrives without mining anything,
    /// where the straight-line walker would have chewed straight through the wall.
    /// </summary>
    private static void ExecutorUsesTheRoute(Action<bool, string> check)
    {
        FakeBridge bridge = new();
        bridge.FillSolid(60, 0, 60, 18);

        ExecutionPlan plan = new ExecutionPlan { Width = 400, Height = 60, BlastRadius = 7, FuseTicks = 300, RetreatTiles = 10 };
        plan.Charges.Add(new ChargeOrder
        {
            Order = 1,
            X = 80,
            Y = 19,
            Section = 1,
            StandX = 80,
            StandY = 19,
            RetreatAvailable = true,
        });

        BlastExecutor executor = new(plan, new ExecutorOptions());
        int ticks = 0;
        while (ticks < 20000 && executor.Status.State != ExecutorState.Finished && executor.Status.State != ExecutorState.Halted)
        {
            executor.Step(bridge);
            bridge.Advance();
            ticks++;
        }

        check(executor.Status.RoutesPlanned > 0, $"执行器确实用了 A* 路线（规划 {executor.Status.RoutesPlanned} 次）");
        check(
            executor.Status.RouteDigs == 0,
            $"有缺口时路线上一格都不用挖（路线挖了 {executor.Status.RouteDigs} 格，{executor.Status.LastRouteNote}）");
        check(executor.Status.Fired == 1, $"绕过去以后照常完成这一发（{executor.Status.Fired} 发）");
        check(executor.Status.Deaths == 0, $"绕路没有把自己绕死（死亡 {executor.Status.Deaths}）");
    }
}
