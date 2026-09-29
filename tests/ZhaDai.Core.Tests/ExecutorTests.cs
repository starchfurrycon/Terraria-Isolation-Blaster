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
        RetreatsThroughRock(check);
        FenceDigs(check);
        RespectsTheWorld(check);
        RemovalClassification(check);
        ExecutionFileRoundTrip(check);
    }

    /// <summary>
    /// The dangerous case: the charge sits in a pocket where every straight line away from it runs
    /// into rock. The executor has to plan a route before throwing, not walk into the wall, because
    /// the fuse does not wait. This is what stops a run from becoming a pile of deaths.
    /// </summary>
    private static void RetreatsThroughRock(Action<bool, string> check)
    {
        FakeBridge bridge = new();
        bridge.FillSolid(56, 10, 56, 19);
        bridge.FillSolid(64, 10, 64, 19);
        bridge.FillSolid(56, 10, 64, 10);
        bridge.Teleport(60, 19);

        BlastExecutor executor = new(MakePlan(1, standOffset: 0), new ExecutorOptions());
        Drive(executor, bridge, 3000);

        check(
            executor.Status.Fired == 1 && executor.Status.Deaths == 0 && executor.Status.BlastHits == 0,
            $"被岩石围住时也能退出去（开火 {executor.Status.Fired}、死亡 {executor.Status.Deaths}、被炸 {executor.Status.BlastHits}）");
        check(bridge.DigCount > 0, $"为了逃出去确实挖了岩石（挖了 {bridge.DigCount} 格）");
        check(executor.Status.Skipped == 0, $"没有白白跳过这一发（跳过 {executor.Status.Skipped}）");
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
        check(
            executor.Status.Jumps <= 20 && bridge.MaxJumpHoldTicks >= 3,
            $"跳得不多且是按住跳的（跳 {executor.Status.Jumps} 次、最长按住 {bridge.MaxJumpHoldTicks} tick、按住共 {bridge.JumpRequestTicks} tick）");
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
            broke.Status.Fired == 0 && broke.Status.LastSkip == SkipReason.NotEnoughSupplies,
            $"没有雷管时直接拒绝接管、一发都不点（实际 {broke.Status.LastSkip}，开了 {broke.Status.Fired} 发）");
        check(
            broke.Status.State == ExecutorState.Halted && broke.Status.AuditMessage.Contains("雷管"),
            $"拒绝接管时说明了缺什么（{broke.Status.AuditMessage}）");
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

    /// <summary>
    /// The pickaxe half of the plan: the audit refuses a run whose pickaxe is below the planned power,
    /// and a run that does have the pickaxe walks to each dig target and finishes it before the first
    /// charge is armed.
    /// </summary>
    private static void FenceDigs(Action<bool, string> check)
    {
        ExecutionPlan plan = MakePlan(1, standOffset: 10);
        plan.PickPower = 100;
        plan.Digs.Add(new ZhaDai.Automation.DigOrder { X = 45, Y = 19, Type = 107, Hits = 3, Reason = "blastimmune" });
        plan.Digs.Add(new ZhaDai.Automation.DigOrder { X = 50, Y = 19, Type = 226, Hits = 0, Reason = "blocked" });

        FakeBridge weak = new() { BestPickPower = 35 };
        BlastExecutor refused = new(plan, new ExecutorOptions());
        Drive(refused, weak, 2000);
        check(
            refused.Status.State == ExecutorState.Halted &&
            refused.Status.LastSkip == SkipReason.NotEnoughSupplies &&
            refused.Status.Fired == 0 &&
            refused.Status.AuditMessage.Contains("镐力"),
            $"镐力不够时拒绝接管（{refused.Status.AuditMessage}）");

        FakeBridge strong = new();
        strong.FillSolid(45, 19, 45, 19);
        strong.FillSolid(50, 19, 50, 19);
        BlastExecutor executor = new(plan, new ExecutorOptions());
        Drive(executor, strong, 40000);
        check(
            executor.Status.DigsDone == 1 && executor.Status.DigsSkipped == 0,
            $"该挖的封带格被挖掉了（挖成 {executor.Status.DigsDone}，跳过 {executor.Status.DigsSkipped}）");
        check(
            !strong.IsSolid(45, 19),
            "挖过的那一格在世界里真的没了");
        check(
            strong.IsSolid(50, 19) && executor.Status.Fired == 1,
            $"炸不掉的那格只记录不执行（是否还在：{strong.IsSolid(50, 19)}），雷管照常施工（{executor.Status.Fired} 发）");
        check(
            executor.Status.AuditMessage.Contains("雷管") && executor.Status.AuditMessage.Contains("镐力"),
            $"盘点结论写进了状态（{executor.Status.AuditMessage}）");
    }

    /// <summary>
    /// Every tile the planner might have to route around, checked against the transcribed vanilla
    /// tables: what dynamite leaves, what a pickaxe of a given power can still take, and what neither
    /// tool ever removes.
    /// </summary>
    private static void RemovalClassification(Action<bool, string> check)
    {
        check(TileCatalog.IsProtectedStructure(21), "箱子算结构物，默认要保护");
        check(TileCatalog.IsProtectedStructure(10), "门算结构物");
        check(!TileCatalog.IsProtectedStructure(32), "腐化荆棘不算结构物：它自己就是传播源，必须清掉");
        check(!TileCatalog.IsProtectedStructure(52), "藤蔓不算结构物");
        check(!TileCatalog.IsProtectedStructure(3), "普通草叶不算结构物：到处都是，保护它等于全图改挖");
        check(TileCatalog.IsPlayerBuilt(30), "木板算玩家建材");
        check(!TileCatalog.IsPlayerBuilt(1), "石头是天然地形，随便炸");
        check(!TileCatalog.IsPlayerBuilt(107), "钴矿是天然矿石，不是玩家盖的");

        check(TileCatalog.MinPickPower(25) == 65, $"黑檀石要 65% 镐力（实际 {TileCatalog.MinPickPower(25)}）");
        check(TileCatalog.MinPickPower(226) == 210, $"神庙砖要 210% 镐力（实际 {TileCatalog.MinPickPower(226)}）");
        check(
            TileCatalog.DigHits(226, 100) == 0 && TileCatalog.DigHits(226, 210) > 0,
            "100% 镐子挖不动神庙砖，210% 的锯镐可以");
        check(
            TileCatalog.DigHits(211, 150) == 0 && TileCatalog.DigHits(211, 200) > 0,
            "叶绿矿要 200% 镐力，150% 挖不动");
        check(TileCatalog.DigHits(3, 35) == 1, $"野草在 tileNoFail 里，一镐就没（实际 {TileCatalog.DigHits(3, 35)} 镐）");
        check(TileCatalog.DigHits(2, 35) == 3, $"草皮按镐力算：35% 镐子要 3 下（实际 {TileCatalog.DigHits(2, 35)}）");
        check(TileCatalog.DigHits(2, 100) == 1, "100% 镐子一镐铲掉草皮");
        check(
            TileCatalog.Classify(26, hardMode: true, downedGolemBoss: true, getGoodWorld: false, pickPower: 210) == RemovalMethod.Blocked,
            "祭坛不是镐子的目标，也不是雷管的目标：只能报告");
        check(
            TileCatalog.Classify(107, hardMode: true, downedGolemBoss: false, getGoodWorld: false, pickPower: 100) == RemovalMethod.Dig,
            "钴矿雷管炸不掉，但 100% 镐子挖得动：归镐子");
        check(
            TileCatalog.Classify(107, hardMode: true, downedGolemBoss: false, getGoodWorld: false, pickPower: 65) == RemovalMethod.Blocked,
            "镐力不够时钴矿两样都处理不掉，必须报出来");
        check(
            TileCatalog.Classify(1, hardMode: false, downedGolemBoss: false, getGoodWorld: false, pickPower: 35) == RemovalMethod.Blast,
            "普通石头交给雷管");
    }

    /// <summary>
    /// Scratch harness for the walking layer: runs the clean scenario and prints where the player actually is
    /// every so often, so a stall can be read instead of guessed at. Enabled with --walkdebug.
    /// </summary>
    public static void DebugWalk()
    {
        FakeBridge bridge = new();
        BlastExecutor executor = new(MakePlan(3, standOffset: 10), new ExecutorOptions());
        Console.WriteLine($"起点 ({bridge.PlayerX:0.0},{bridge.PlayerY:0.0}) 目标站位 50/90/130 起点行 19 地面行 20");
        for (int tick = 1; tick <= 6000; tick++)
        {
            executor.Step(bridge);
            bridge.Advance();
            if (tick % 10 == 0 && tick > 580 && tick < 700 || tick % 500 == 0)
            {
                Console.WriteLine(
                    $"t={tick,5} 状态={executor.Status.State,-10} 位置=({bridge.PlayerX,6:0.00},{bridge.PlayerY,6:0.00}) " +
                    $"请求dx={bridge.LastRequestedDX,2} dy={bridge.LastRequestedDY,2} 速度Y={bridge.PlayerVelocityY,6:0.00} " +
                    $"站地={bridge.PlayerGrounded,-5} 跳={executor.Status.Jumps} 挖={bridge.DigCount} " +
                    $"路线={executor.Status.LastRouteNote} | 分支[{executor.Status.WalkNote}] 跳过={executor.Status.Skipped}/{executor.Status.LastSkip} {executor.Status.Message}");
            }

            if (tick == 1520)
            {
                int cx = (int)Math.Round(bridge.PlayerX);
                int cy = (int)Math.Round(bridge.PlayerY);
                Console.WriteLine($"卡住点地形（我={cx},{cy}，列 {cx - 4}..{cx + 6}）：");
                for (int y = cy - 4; y <= cy + 4; y++)
                {
                    string row = "    ";
                    for (int x = cx - 4; x <= cx + 6; x++)
                    {
                        row += bridge.IsSolid(x, y) ? '#' : '.';
                    }

                    Console.WriteLine(row + "   y=" + y);
                }
            }

            if (executor.Status.State == ExecutorState.Finished || executor.Status.State == ExecutorState.Halted)
            {
                Console.WriteLine($"结束于 t={tick} 状态={executor.Status.State}");
                break;
            }
        }

        foreach (string line in bridge.Logs)
        {
            Console.WriteLine("  日志 " + line);
        }
    }

    /// <summary>
    /// The three things the second real-machine report was about: mining that behaves like mining, a walk
    /// that does not remodel the world, and getting out of a pocket instead of standing in it for ever.
    /// </summary>
    private static void RespectsTheWorld(Action<bool, string> check)
    {
        // 1. A charge the plan flagged as reaching a structure is left alone. The plan moves charges away from
        //    builds, so this only fires when the world changed after planning -- which is exactly the case the
        //    report describes.
        ExecutionPlan protectedPlan = MakePlan(1, standOffset: 10);
        protectedPlan.Charges[0].HasProtected = true;
        FakeBridge protectedWorld = new();
        BlastExecutor protectedRun = new(protectedPlan, new ExecutorOptions());
        Drive(protectedRun, protectedWorld, 3000);
        check(
            protectedRun.Status.LastSkip == SkipReason.StructuresInBlast && protectedRun.Status.Fired == 0,
            $"爆破范围里有玩家结构的那一发不炸（跳过原因 {protectedRun.Status.LastSkip}，放了 {protectedRun.Status.Fired} 发）");

        ExecutionPlan allowedPlan = MakePlan(1, standOffset: 10);
        allowedPlan.Charges[0].HasProtected = true;
        BlastExecutor allowedRun = new(allowedPlan, new ExecutorOptions { ProtectStructures = false });
        Drive(allowedRun, new FakeBridge(), 3000);
        check(
            allowedRun.Status.Fired == 1,
            $"显式关掉保护后照常施工（放了 {allowedRun.Status.Fired} 发）");

        // 2. A way on that would mean cutting through somebody's wall is refused rather than mined. Before this,
        //    the walker happily dug through the wall and the trench showed up in the player's base. The stand
        //    point sits behind a tall wall, so the only route in is through it.
        ExecutionPlan walledPlan = MakePlan(1, standOffset: 10);
        FakeBridge walledWorld = new();
        walledWorld.FillSolid(49, 10, 49, 20);
        for (int y = 10; y <= 20; y++)
        {
            walledWorld.SetWall(49, y, 4);
        }

        ExecutorOptions walledOptions = new();
        BlastExecutor walledRun = new(walledPlan, walledOptions);
        Drive(walledRun, walledWorld, 8000);
        check(
            walledRun.Status.RouteDigs == 0 && walledRun.Status.EscapeDigs == 0 && walledRun.Status.Fired == 0 &&
            walledRun.Status.LastSkip == SkipReason.NoUndamagingRoute,
            $"只有挖穿自建墙才过得去时不挖，改为一发不打并说明（路线挖 {walledRun.Status.RouteDigs}，脱困挖 {walledRun.Status.EscapeDigs}，放了 {walledRun.Status.Fired} 发，原因 {walledRun.Status.LastSkip}）");
        check(
            walledWorld.DugTiles.Count == 0,
            $"自建墙一格都没少（挖了 {walledWorld.DugTiles.Count} 格）");
        check(
            System.Linq.Enumerable.Any(walledWorld.Logs, m => m.Contains("困住")) ||
            System.Linq.Enumerable.Any(walledWorld.Logs, m => m.Contains("手动")),
            $"日志里写清了为什么放弃（{string.Join(" | ", walledWorld.Logs)}）");

        // 3. Mining takes more than one tick, because the game swings at the pickaxe's own use time. The old
        //    frame operation deleted a tile per frame, which is what "frame operation" felt like in game.
        ExecutionPlan slowPlan = MakePlan(1, standOffset: 10);
        slowPlan.PickPower = 100;
        slowPlan.Digs.Add(new ZhaDai.Automation.DigOrder { X = 45, Y = 19, Type = 107, Hits = 3, Reason = "blastimmune" });
        FakeBridge slowWorld = new() { BestPickPower = 100 };
        slowWorld.FillSolid(45, 19, 45, 19);
        BlastExecutor slowRun = new(slowPlan, new ExecutorOptions());
        Drive(slowRun, slowWorld, 40000);
        check(
            slowWorld.DigCount > 1 && !slowWorld.IsSolid(45, 19),
            $"挖一格要挥好几镐，而不是一帧删掉（挥了 {slowWorld.DigCount} 镐）");

        // 4. Trapped: the same tall wall, but plain rock, so the emergency dig is allowed and the walker mines
        //    one tile through it instead of standing there. Standing there is what the report saw.
        ExecutionPlan pocketPlan = MakePlan(1, standOffset: 10);
        FakeBridge pocketWorld = new();
        pocketWorld.FillSolid(49, 10, 49, 20);
        ExecutorOptions pocketOptions = new();
        BlastExecutor pocketRun = new(pocketPlan, pocketOptions);
        int pocketTicks = Drive(pocketRun, pocketWorld, 20000);
        check(
            pocketRun.Status.State == ExecutorState.Finished && pocketRun.Status.Fired == 1,
            $"被普通岩石关住时自己挖出来把活干完（状态 {pocketRun.Status.State}，放了 {pocketRun.Status.Fired} 发，{pocketTicks} tick，挖了 {pocketWorld.DugTiles.Count} 格）");
        check(
            pocketRun.Status.EscapeDigs <= pocketOptions.EscapeDigTiles,
            $"脱困那几步是有限的（脱困挖 {pocketRun.Status.EscapeDigs} 格，上限 {pocketOptions.EscapeDigTiles}）");
        check(
            walledRun.Status.EscapeDigs == 0 && pocketRun.Status.RouteDigs + pocketRun.Status.EscapeDigs > 0,
            $"地面上的墙不挖、只挖头顶有岩层的路（路线挖 {pocketRun.Status.RouteDigs}，脱困挖 {pocketRun.Status.EscapeDigs}）");
    }

    private static ExecutionPlan MakePlan(int count, int standOffset)    {
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
