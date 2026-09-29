using System.Text;
using ZhaDai.Core.Analysis;
using ZhaDai.Core.Planning;
using ZhaDai.Core.World;

namespace ZhaDai.Core.Tests;

/// <summary>
/// Console assertion runner. It deliberately avoids a test framework so the same binary works in CI
/// and on a bare machine, matching the sibling analyzer project.
/// </summary>
internal static class Program
{
    private static int failures;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        RunIdTests();
        RunBlastOffsetTests();
        RunSpreadTests();
        RunPlannerTests();
        ExecutorTests.Run(Check);
        PlanProtectionTests.Run(Check);
        RunWorldIntegrationTests(args);

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("全部通过。");
            return 0;
        }

        Console.WriteLine($"{failures} 项失败。");
        return 1;
    }

    internal static void Check(bool condition, string description)
    {
        if (condition)
        {
            Console.WriteLine("  通过  " + description);
            return;
        }

        failures++;
        Console.WriteLine("  失败  " + description);
    }

    /// <summary>
    /// The whole plan rests on this: if any tile the fence has to clear were immune to Dynamite,
    /// the band would keep a hole no matter where the charges go.
    /// </summary>
    private static void RunIdTests()
    {
        Console.WriteLine("物块分类");
        bool anyImmune = false;
        List<string> offenders = [];

        foreach (bool hardMode in new[] { false, true })
        {
            foreach (bool getGood in new[] { false, true })
            {
                foreach (bool golem in new[] { false, true })
                {
                    foreach (ushort type in TileIds.AllInfectionNodeTypes())
                    {
                        if (TileIds.IsDynamiteImmune(type, hardMode, golem, getGood))
                        {
                            anyImmune = true;
                            offenders.Add($"{type}(hard={hardMode},good={getGood},golem={golem})");
                        }
                    }
                }
            }
        }

        Check(!anyImmune, "没有任何感染物块是雷管炸不掉的" + (anyImmune ? " —— 反例：" + string.Join(",", offenders) : string.Empty));

        Check(TileIds.IsEvil(23) && TileIds.IsEvil(199) && TileIds.IsHallow(109), "腐化/猩红/神圣 id 归类正确");
        Check(!TileIds.IsHallow(23) && !TileIds.IsEvil(109), "邪恶与神圣不互相包含");
        Check(TileIds.IsInfectionNode(1) && TileIds.IsInfectionNode(2) && TileIds.IsInfectionNode(25), "石头/土/黑檀石属于传播图的节点");
        Check(!TileIds.IsInfectionNode(0), "空物块不是节点");
        Check(TileIds.IsDynamiteImmune(41, true, true, false), "地牢砖炸不掉");
        Check(TileIds.IsDynamiteImmune(107, true, true, false), "困难模式矿石炸不掉");
        Check(TileIds.IsTrap(137) && TileIds.IsTrap(443) && TileIds.IsTrap(48) && TileIds.IsTrap(210), "陷阱 id 归类正确");
        Check(TileIds.IsGravestone(85), "墓碑 id 归类正确");
    }

    private static void RunBlastOffsetTests()
    {
        Console.WriteLine("爆破几何");
        // Projectile.ExplodeTiles destroys a tile when dx^2 + dy^2 < radius^2 with radius 7.
        int radius = 7;
        int limit = radius * radius;
        Check(6 * 6 < limit, "半径 7 时轴向第 6 格会被炸掉");
        Check(7 * 7 >= limit, "半径 7 时轴向第 7 格不会被炸掉");
        Check((4 * 4) + (4 * 4) < limit, "对角 (4,4) 会被炸掉");
        Check((5 * 5) + (5 * 5) >= limit, "对角 (5,5) 不会被炸掉");
    }

    private static void RunSpreadTests()
    {
        Console.WriteLine("传播图");
        Check(InfectionModel.SpreadReach == 3, "普通传播距离是 3");
        Check(InfectionModel.VineDownwardReach == 13, "向下藤蔓距离是 13");

        // A corrupt tile three tiles from stone is inside the same spread closure; four is not,
        // unless something can bridge the gap.
        LoadedWorld three = FlatWorld(60, 40);
        three.Tiles.Set(10, 20, TileIds.CorruptGrass);
        three.Tiles.Set(13, 20, TileIds.Stone);
        Check(Connected(three) == 2, "相隔 3 格的石头仍在同一闭包");

        LoadedWorld six = FlatWorld(60, 40);
        six.Tiles.Set(10, 20, TileIds.CorruptGrass);
        six.Tiles.Set(16, 20, TileIds.Stone);
        Check(Connected(six) == 1, "相隔 6 格的石头不在同一闭包（六格空隙有效）");

        LoadedWorld four = FlatWorld(60, 40);
        four.Tiles.Set(10, 20, TileIds.CorruptGrass);
        four.Tiles.Set(14, 20, TileIds.Stone);
        Check(Connected(four) == 2, "草可以靠植物桥接 4 格，所以 4 格仍连通");

        LoadedWorld vine = FlatWorld(60, 40);
        vine.Tiles.Set(10, 20, TileIds.CorruptGrass);
        vine.Tiles.Set(10, 33, TileIds.Stone);
        Check(Connected(vine) == 2, "向下藤蔓可以桥接 13 格");

        LoadedWorld farVine = FlatWorld(60, 40);
        farVine.Tiles.Set(10, 20, TileIds.CorruptGrass);
        farVine.Tiles.Set(10, 34, TileIds.Stone);
        Check(Connected(farVine) == 1, "向下藤蔓够不到 14 格");
    }

    private static int Connected(LoadedWorld world)
    {
        InfectionModel model = InfectionModel.Build(world);
        SpreadWorkspace workspace = new(model);
        return workspace.Flood([.. model.EnumerateSeeds()], null, new TileRect(0, 0, world.Tiles.Width - 1, world.Tiles.Height - 1)).VisitedCount;
    }

    private static void RunPlannerTests()
    {
        Console.WriteLine("规划器");
        LoadedWorld world = FlatWorld(120, 80, solid: true);
        // A diagonal strip of corruption, the shape that a straight vertical fence handles badly.
        for (int i = 0; i < 12; i++)
        {
            world.Tiles.Set(20 + i, 30 + i, TileIds.Ebonstone);
        }

        BlastPlan plan = BlastPlanner.Plan(world);
        Check(plan.Summary.Charges > 0, "斜向感染源会产出雷管");
        Check(plan.Summary.AllSectionsSealed, "合成世界的计划通过了泛洪复核");
        Check(plan.Summary.BlastImmuneNodesInFence == 0, "封带里没有炸不掉的感染物块");
        Check(plan.Summary.EnclosedSeedTiles == plan.Summary.EvilSeeds, "封带把全部感染源留在带内");
        Check(plan.Charges.All(c => c.CoverTiles > 0), "每一发雷管都确实覆盖了封带物块");
        Check(plan.Charges.Skip(1).Any(c => c.RetreatAvailable), "除首发以外的雷管都有已炸开的撤离点");
        Check(plan.Sections.Count == 1, "连成一片的斜带只算一段");

        // The band must hug the diagonal, not wrap it in an axis aligned box.
        FenceSection section = plan.Sections[0];
        int spanX = section.Bounds.Width;
        int spanY = section.Bounds.Height;
        Check(spanX >= 12 && spanY >= 12, "斜带的包围盒确实沿对角线展开");
        Check(section.Charges > 0, "斜带一段里有雷管");

        BlastPlan again = BlastPlanner.Plan(world);
        Check(again.Summary.Charges == plan.Summary.Charges && again.Summary.FenceTiles == plan.Summary.FenceTiles,
            "同一存档两次规划结果一致");

        // Merging two nearby fronts must not cost more charges than fencing them apart.
        LoadedWorld pair = FlatWorld(200, 120, solid: true);
        pair.Tiles.Set(40, 60, TileIds.Ebonstone);
        pair.Tiles.Set(150, 60, TileIds.Ebonstone);
        BlastPlan apart = BlastPlanner.Plan(pair, new BlastPlanOptions { MergeLinkDistance = 3 });
        BlastPlan merged = BlastPlanner.Plan(pair, new BlastPlanOptions { MergeLinkDistance = 200 });
        Check(apart.Sections.Count == 2, "相距很远的两处感染源默认分开封");
        Check(merged.Sections.Count == 1, "调大合并距离后并成一段");
        Check(merged.Summary.FenceTiles <= apart.Summary.FenceTiles, "合并后的封带物块数不多于分开封");
        Check(apart.Summary.EnclosedSeedTiles <= merged.Summary.EnclosedSeedTiles, "分开封时留在带内的感染源不多于合并时");
    }

    /// <summary>
    /// Integration pass: real saves are the only evidence that the reader's byte layout is right.
    /// The paths come from the command line or from TERRARIA_WORLDS; without them the pass is skipped.
    /// </summary>
    private static void RunWorldIntegrationTests(string[] args)
    {
        string? directory = args.FirstOrDefault(a => !a.StartsWith('-'))
            ?? Environment.GetEnvironmentVariable("TERRARIA_WORLDS");
        if (directory is null || !Directory.Exists(directory))
        {
            Console.WriteLine("真实存档集成测试：已跳过（未给出存档目录）");
            return;
        }

        string[] worlds = [.. Directory.EnumerateFiles(directory, "*.wld").OrderBy(p => p)];
        if (worlds.Length == 0)
        {
            Console.WriteLine("真实存档集成测试：已跳过（目录里没有 .wld）");
            return;
        }

        Console.WriteLine("真实存档集成测试");
        foreach (string path in worlds)
        {
            LoadedWorld world = WorldFileReader.Read(path);
            Check(world.Metadata.Width > 0 && world.Metadata.Height > 0, $"{Path.GetFileName(path)} 读出了合法的世界尺寸");
            Check(world.Tiles.TypeAt((world.Metadata.SpawnY * world.Tiles.Width) + world.Metadata.SpawnX) != ushort.MaxValue,
                $"{Path.GetFileName(path)} 出生点可寻址");

            BlastPlan plan = BlastPlanner.Plan(world);
            Console.WriteLine(
                $"        {Path.GetFileName(path)}：隔离段 {plan.Summary.Sections}、" +
                $"封带 {plan.Summary.FenceTiles} 格、雷管 {plan.Summary.Charges} 发、" +
                $"封住 {plan.Summary.EnclosedSeedTiles} 格、复核 {(plan.Summary.AllSectionsSealed ? "全封住" : "有未封住")}");
        }
    }

    private static LoadedWorld FlatWorld(int width, int height, bool solid = false)
    {
        TileGrid tiles = new(width, height);

        // A band test needs something for the band to cut through: in an all air world the infection
        // graph has no other node to reach and no blasting would be needed at all. Spread tests want
        // the opposite, an empty world where only the two tiles under test can be connected.
        if (solid)
        {
            tiles.FillRect(0, 0, width - 1, height - 1, TileIds.Stone);
        }

        WorldMetadata metadata = new(
            Version: 326,
            Title: "test",
            Seed: "0",
            WorldId: 1,
            Width: width,
            Height: height,
            SpawnX: width / 2,
            SpawnY: 10,
            WorldSurface: height * 0.3,
            RockLayer: height * 0.6,
            DungeonX: width - 20,
            IsCrimson: false,
            HardMode: true,
            DrunkWorld: false,
            GetGoodWorld: false,
            RemixWorld: false,
            SkyblockWorld: false,
            NoTraps: false,
            ZenithWorld: false);
        return new LoadedWorld(metadata, tiles);
    }
}
