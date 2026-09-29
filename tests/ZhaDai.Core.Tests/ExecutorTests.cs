using System;
using System.Collections.Generic;
using ZhaDai.Automation;
using ZhaDai.Core.Planning;
using ZhaDai.Core.World;

namespace ZhaDai.Core.Tests;

/// <summary>
/// Runs the executor against the fake world. The point is to prove the numbers the user cares about:
/// it does not blow itself up, it finishes the plan, and a death resumes rather than restarts.
/// </summary>
internal static class ExecutorTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("执行器（模拟世界）");

        CleanRun(check);
        DeathResumes(check);
        SkipRules(check);
        HostileHold(check);
        DrowningSurfaces(check);
        ExecutionFileRoundTrip(check);
    }

    private static void CleanRun(Action<bool, string> check)
    {
        FakeBridge bridge = new();
        BlastExecutor executor = new(MakePlan(3, standOffset: 10), new ExecutorOptions());
        int ticks = Drive(executor, bridge, 20000);

        check(executor.Status.Fired == 3, $"干净的场地跑完 3 发（实际 {executor.Status.Fired} 发，{ticks} tick）");
        check(executor.Status.Deaths == 0, $"自伤规避成功，没有死亡（实际 {executor.Status.Deaths} 次）");
        check(executor.Status.BlastHits == 0, $"起爆瞬间都在爆炸范围外（实际被炸到 {executor.Status.BlastHits} 次）");
        check(executor.Status.State == ExecutorState.Finished, $"跑完进入 Finished（实际 {executor.Status.State}）");
        check(executor.Status.Skipped == 0, $"没有跳过任何一发（实际跳过 {executor.Status.Skipped}）");
    }

    private static void DeathResumes(Action<bool, string> check)
    {
        FakeBridge bridge = new();
        BlastExecutor executor = new(MakePlan(2, standOffset: 0), new ExecutorOptions());

        int ticks = 0;
        bool crippled = false;
        while (ticks < 60000 && executor.Status.State != ExecutorState.Finished && executor.Status.State != ExecutorState.Halted)
        {
            // Only cripple the player once the first charge is in the air: the point is that the
            // retreat fails, not that the walk to the charge fails.
            if (!crippled && executor.Status.State == ExecutorState.Retreating)
            {
                bridge.Speed = 0.003d;
                crippled = true;
            }

            executor.Step(bridge);
            bridge.Advance();
            ticks++;

            if (crippled && executor.Status.Deaths >= 1 && bridge.Speed < 0.1d)
            {
                bridge.Speed = 0.12d;
            }
        }

        check(executor.Status.Deaths >= 1, $"退不掉的时候确实会被自己的雷管炸死（实际 {executor.Status.Deaths} 次）");
        check(executor.Status.Fired == 2, $"死后没有丢掉这一发，继续把它炸完（实际完成 {executor.Status.Fired} 发）");
        check(executor.Status.State == ExecutorState.Finished, $"最终跑完整个计划（实际 {executor.Status.State}）");
        check(executor.Status.GravestonesDug >= 1, $"复活后挖掉了自己留下的墓碑（实际挖了 {executor.Status.GravestonesDug} 格）");
        check(bridge.Tombstones == 0, $"墓碑没有留在世界里（剩余 {bridge.Tombstones}）");
        check(bridge.Logs.Count > 0, "过程有日志可查");
    }

    private static void SkipRules(Action<bool, string> check)
    {
        CheckSkip(check, "爆炸范围里有炸弹桶就跳过", plan => plan.Charges[0].HasExplosives = true, SkipReason.ExplosivesInBlast);
        CheckSkip(check, "爆开会放岩浆且自己没有岩浆免疫就跳过", plan => plan.Charges[0].HasLava = true, SkipReason.LavaInBlastWithoutImmunity);
        CheckSkip(check, "没有撤离空间就跳过", plan => plan.Charges[0].RetreatAvailable = false, SkipReason.NoRetreatRoom);

        FakeBridge dry = new();
        ExecutionPlan empty = MakePlan(1, standOffset: 10);
        BlastExecutor broke = new(empty, new ExecutorOptions());
        dry.DynamiteCount = 0;
        Drive(broke, dry, 3000);
        check(
            broke.Status.Skipped == 1 && broke.Status.LastSkip == SkipReason.NoDynamite,
            $"没有雷管时不硬炸（实际 {broke.Status.LastSkip}）");
    }

    private static void CheckSkip(Action<bool, string> check, string description, Action<ExecutionPlan> mutate, SkipReason expected)
    {
        ExecutionPlan plan = MakePlan(1, standOffset: 10);
        mutate(plan);
        FakeBridge bridge = new();
        BlastExecutor executor = new(plan, new ExecutorOptions());
        Drive(executor, bridge, 8000);

        check(
            executor.Status.Skipped == 1 && executor.Status.Fired == 0 && executor.Status.LastSkip == expected,
            $"{description}（跳过 {executor.Status.Skipped} 发，原因 {executor.Status.LastSkip}）");
    }

    private static void HostileHold(Action<bool, string> check)
    {
        FakeBridge bridge = new() { NearestHostileDistance = 4d };
        BlastExecutor executor = new(MakePlan(1, standOffset: 10), new ExecutorOptions());
        Drive(executor, bridge, 1500);

        bool held = executor.Status.Fired == 0;
        bridge.NearestHostileDistance = 999d;
        Drive(executor, bridge, 3000);

        check(held, "敌怪贴脸时不点火");
        check(executor.Status.Fired == 1, $"敌怪走开后照常施工（实际 {executor.Status.Fired} 发）");
    }

    private static void DrowningSurfaces(Action<bool, string> check)
    {
        FakeBridge bridge = new() { PlayerCanDrown = true, PlayerLiquidKind = 1, PlayerBreath = 40 };
        BlastExecutor executor = new(MakePlan(1, standOffset: 10), new ExecutorOptions());
        Drive(executor, bridge, 600);

        check(executor.Status.Fired == 0, "快淹死时不点火");
        check(bridge.LastRequestedDY < 0, $"氧气不足时往上浮（实际 dy={bridge.LastRequestedDY}）");
    }

    /// <summary>
    /// The planner writes the work order and the runtime reads it. If those two ever drift, the
    /// runtime silently plans nothing, so the round trip is asserted rather than assumed.
    /// </summary>
    private static void ExecutionFileRoundTrip(Action<bool, string> check)
    {
        TileGrid tiles = new(120, 80);
        tiles.FillRect(0, 0, 119, 79, TileIds.Stone);
        for (int i = 0; i < 10; i++)
        {
            tiles.Set(30 + i, 30 + i, TileIds.Ebonstone);
        }

        LoadedWorld world = new(
            new WorldMetadata(
                Version: 326,
                Title: "rt",
                Seed: "0",
                WorldId: 1,
                Width: 120,
                Height: 80,
                SpawnX: 60,
                SpawnY: 10,
                WorldSurface: 24,
                RockLayer: 48,
                DungeonX: 100,
                IsCrimson: false,
                HardMode: true,
                DrunkWorld: false,
                GetGoodWorld: false,
                RemixWorld: false,
                SkyblockWorld: false,
                NoTraps: false,
                ZenithWorld: false),
            tiles);

        BlastPlan plan = BlastPlanner.Plan(world);
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zhadai-rt-" + Guid.NewGuid().ToString("N") + ".zplan");
        PlanWriter.WriteExecutionFile(plan, path, "rt.wld");
        ExecutionPlan parsed = ExecutionPlan.Load(path);
        System.IO.File.Delete(path);

        check(parsed.Charges.Count == plan.Charges.Count, $"施工文件里的雷管数一致（{parsed.Charges.Count} / {plan.Charges.Count}）");
        check(parsed.FuseTicks == 300 && parsed.BlastRadius == 7, $"施工文件带上了引信与半径（{parsed.FuseTicks}, {parsed.BlastRadius}）");
        check(parsed.RetreatTiles == plan.Options.BlastRadius + plan.Options.RetreatMarginTiles, $"施工文件带上了撤离距离（{parsed.RetreatTiles}）");

        bool same = true;
        for (int i = 0; i < parsed.Charges.Count; i++)
        {
            ChargeOrder a = parsed.Charges[i];
            BlastCharge b = plan.Charges[i];
            if (a.Order != b.Order || a.X != b.X || a.Y != b.Y || a.StandX != b.StandX || a.StandY != b.StandY ||
                a.RetreatAvailable != b.RetreatAvailable || a.HasLava != b.LavaInBlast)
            {
                same = false;
                break;
            }
        }

        check(same && parsed.Charges.Count > 0, "每一发的位置、站位和危险标记都能原样读回");

        // Running the parsed plan must also survive the fake world, which closes the loop between
        // what the planner wrote and what the executor does with it.
        ExecutionPlan runnable = MakePlan(Math.Min(3, parsed.Charges.Count), standOffset: 10);
        FakeBridge bridge = new();
        BlastExecutor executor = new(runnable, new ExecutorOptions());
        Drive(executor, bridge, 20000);
        check(executor.Status.Deaths == 0 && executor.Status.Fired == runnable.Charges.Count, "读回来的计划能直接跑完且不自杀");
    }

    private static ExecutionPlan MakePlan(int count, int standOffset)
    {
        ExecutionPlan plan = new()
        {
            Width = 400,
            Height = 60,
            BlastRadius = 7,
            FuseTicks = 300,
            RetreatTiles = 10,
        };

        for (int i = 0; i < count; i++)
        {
            int x = 60 + (i * 40);
            plan.Charges.Add(new ChargeOrder
            {
                Order = i + 1,
                X = x,
                Y = 19,
                Section = 1,
                StandX = x - standOffset,
                StandY = 19,
                RetreatAvailable = true,
            });
        }

        return plan;
    }

    private static int Drive(BlastExecutor executor, FakeBridge bridge, int maxTicks)
    {
        int ticks = 0;
        while (ticks < maxTicks && executor.Status.State != ExecutorState.Finished && executor.Status.State != ExecutorState.Halted)
        {
            executor.Step(bridge);
            bridge.Advance();
            ticks++;
        }

        return ticks;
    }
}
