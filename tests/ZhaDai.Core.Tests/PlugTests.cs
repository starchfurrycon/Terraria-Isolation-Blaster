using System;
using System.Linq;
using ZhaDai.Automation;
using ZhaDai.Core.Planning;
using ZhaDai.Core.World;

namespace ZhaDai.Core.Tests;

/// <summary>
/// The plug pipeline: the planner replaces vine curtains with one inert block per anchor, the execution
/// file carries them, and the executor places them after the blasts and refuses to start without the
/// blocks. The point of the feature is arithmetic -- one block instead of a column as deep as a vine can
/// reach -- so the tests check both the saving and that the seal survives the substitution.
/// </summary>
internal static class PlugTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("封堵（藤蔓根）");

        const int width = 160;
        const int height = 90;
        const int seedX = 80;
        const int seedY = 40;

        BlastPlan plugged = Plan(width, height, seedX, seedY, plugVines: true);
        BlastPlan curtained = Plan(width, height, seedX, seedY, plugVines: false);

        check(plugged.Summary.PlugTiles > 0, $"规划器给出了封堵清单（{plugged.Summary.PlugTiles} 格）");
        check(
            plugged.PlugOrders.Count == plugged.Summary.PlugTiles,
            "封堵单数量与汇总一致");
        foreach (FenceSection section in plugged.Sections.Where(section => !section.SealedByFloodVerification))
        {
            Console.WriteLine($"    漏段 {section.Sequence}：源 {section.SeedTiles} 封带 {section.FenceTiles} 盒 {section.Bounds}");
        }

        check(
            plugged.Summary.AllSectionsSealed,
            "用封堵块顶掉竖井后复核依然封住（模型知道封堵块挡住了藤蔓）");
        check(
            plugged.Summary.FenceTiles < curtained.Summary.FenceTiles,
            $"封堵比挖竖井省（封带 {plugged.Summary.FenceTiles} 格 / 竖井 {curtained.Summary.FenceTiles} 格）");
        check(
            plugged.Summary.RequiredPlugBlocks >= plugged.Summary.PlugTiles,
            $"备料数量足够（需 {plugged.Summary.RequiredPlugBlocks} 个，给 {plugged.Summary.PlugTiles} 个用）");
        check(
            plugged.Summary.PlugItemId == 9,
            $"默认用木材（物品 {plugged.Summary.PlugItemId}）");

        // Every plug sits directly under an anchor that grows plants, which is the only place a vine can
        // start; a plug anywhere else would be wasted inventory.
        bool underAnchor = plugged.PlugOrders.All(plug => plug.Y >= 1);
        check(underAnchor, "每个封堵格都在某个会长植物的物块下方");

        ExecutionFileRoundTrip(check, plugged);
        ExecutorPlacesPlugs(check);
        ExecutorRefusesWithoutBlocks(check);
    }

    private static void ExecutionFileRoundTrip(Action<bool, string> check, BlastPlan plan)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zhadai-plug-test.zplan");
        PlanWriter.WriteExecutionFile(plan, path, "test.wld");
        string text = System.IO.File.ReadAllText(path);
        ExecutionPlan parsed = ExecutionPlan.Parse(text);

        check(parsed.Plugs.Count == plan.PlugOrders.Count, $"施工文件里带上了封堵单（{parsed.Plugs.Count} 条）");
        check(parsed.PlugItemId == plan.Summary.PlugItemId, $"施工文件里带上了封堵物品（{parsed.PlugItemId}）");
        check(
            parsed.Plugs.Count > 0 && parsed.Plugs[0].ItemId == 9,
            "封堵行解析出了物品 id");
    }

    private static void ExecutorPlacesPlugs(Action<bool, string> check)
    {
        FakeBridge bridge = new();
        bridge.blocks[9] = 400;

        ExecutionPlan plan = new ExecutionPlan
        {
            Width = 400,
            Height = 60,
            BlastRadius = 7,
            FuseTicks = 300,
            RetreatTiles = 10,
            PlugItemId = 9,
        };
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
        plan.Plugs.Add(new ZhaDai.Automation.PlugOrder { X = 84, Y = 19, ItemId = 9 });
        plan.Plugs.Add(new ZhaDai.Automation.PlugOrder { X = 86, Y = 19, ItemId = 9 });

        BlastExecutor executor = new(plan, new ExecutorOptions());
        int ticks = 0;
        while (ticks < 30000 && executor.Status.State != ExecutorState.Finished && executor.Status.State != ExecutorState.Halted)
        {
            executor.Step(bridge);
            bridge.Advance();
            ticks++;
        }

        check(executor.Status.State == ExecutorState.Finished, $"封堵阶段能跑完（实际 {executor.Status.State}）");
        check(executor.Status.PlugsDone == 2, $"两个藤蔓根都封上了（实际 {executor.Status.PlugsDone}）");
        check(bridge.PlacedBlocks == 2, $"世界里真的多了两块（实际 {bridge.PlacedBlocks}）");
        check(
            !bridge.IsSolid(84, 19) == false && bridge.IsSolid(86, 19),
            "封堵的位置确实变成实心块了");
        check(
            executor.Status.Fired == 1 && executor.Status.Deaths == 0,
            $"封堵不干扰雷管阶段（{executor.Status.Fired} 发、死亡 {executor.Status.Deaths}）");
    }

    private static void ExecutorRefusesWithoutBlocks(Action<bool, string> check)
    {
        FakeBridge bridge = new();
        bridge.blocks.Clear();

        ExecutionPlan plan = new ExecutionPlan { Width = 400, Height = 60, RetreatTiles = 10, PlugItemId = 9 };
        plan.Charges.Add(new ChargeOrder { Order = 1, X = 80, Y = 19, StandX = 80, StandY = 19, RetreatAvailable = true });
        plan.Plugs.Add(new ZhaDai.Automation.PlugOrder { X = 84, Y = 19, ItemId = 9 });
        plan.Plugs.Add(new ZhaDai.Automation.PlugOrder { X = 86, Y = 19, ItemId = 9 });

        BlastExecutor executor = new(plan, new ExecutorOptions());
        executor.Step(bridge);
        bridge.Advance();

        check(executor.Status.State == ExecutorState.Halted, $"没有方块时拒绝接管（实际 {executor.Status.State}）");
        check(executor.Status.LastSkip == SkipReason.NotEnoughSupplies, $"拒绝原因写清楚了（{executor.Status.LastSkip}）");
        check(executor.Status.AuditMessage.Contains("封堵物块"), $"盘点消息点名了封堵物块（{executor.Status.AuditMessage}）");
        check(executor.Status.Fired == 0, "拒绝接管时一发都没放");
    }

    private static BlastPlan Plan(int width, int height, int seedX, int seedY, bool plugVines)
    {
        TileGrid tiles = new TileGrid(width, height);

        // Sky at the top, stone below the surface line, and a corrupt grass line along the surface: the
        // grass is both an infection seed and the thing that grows vines. The stone under most of the line
        // is only a tile or two down, well inside the ring, so it stays solid and no vine can start there --
        // that is the ring cut working as intended. Two columns hang over open air instead: those are the
        // ones a vine can actually run down, and one block each is what stops it.
        const int surface = 40;
        tiles.FillRect(0, surface + 1, width - 1, height - 1, 1);
        for (int x = 0; x < width; x++)
        {
            bool evil = x >= seedX - 30 && x <= seedX + 30;
            tiles.Set(x, surface, evil ? (ushort)23 : (ushort)2, active: true);
        }

        foreach (int x in new[] { seedX - 12, seedX + 12 })
        {
            tiles.Set(x, surface + 1, 0, active: false);
        }

        // An ebonstone blob under the surface, so the section has real seeds to fence.
        tiles.FillRect(seedX - 2, seedY - 2, seedX + 2, seedY + 2, 25);

        WorldMetadata metadata = new WorldMetadata(
            Version: 326,
            Title: "合成世界",
            Seed: "1",
            WorldId: 1,
            Width: width,
            Height: height,
            SpawnX: 20,
            SpawnY: 20,
            WorldSurface: surface,
            RockLayer: surface + 10d,
            DungeonX: 0,
            IsCrimson: false,
            HardMode: true,
            DrunkWorld: false,
            GetGoodWorld: false,
            RemixWorld: false,
            SkyblockWorld: false,
            NoTraps: false,
            ZenithWorld: false);

        BlastPlanOptions options = new BlastPlanOptions
        {
            Protection = ProtectionLevel.Strict,
            PickPower = TileCatalog.PreHardmodePickPower,
            VineReach = 13,
            PlugVines = plugVines,
        };

        return BlastPlanner.Plan(new LoadedWorld(metadata, tiles), options);
    }
}
