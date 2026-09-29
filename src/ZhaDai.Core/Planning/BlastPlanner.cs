using ZhaDai.Core.Analysis;
using ZhaDai.Core.World;

namespace ZhaDai.Core.Planning;

/// <summary>
/// Builds a Dynamite blasting plan that isolates every infection front of a vanilla world behind a
/// cleared band.
/// </summary>
/// <remarks>
/// <para><b>Why a band around the front, and not a vertical strip.</b> A tile is sealed off as soon
/// as no tile it could convert is left reachable. So the cheapest correct fence is the set of
/// infectable tiles sitting within reach of the front: it hugs the front's outline, which for a
/// diagonal hardmode arm is a diagonal band, and it leaves the smallest possible range on the
/// infected side. A straight vertical strip fencing the same arm must also clear everything
/// between the strip and the arm, which is strictly more digging and strictly more enclosed world.
/// </para>
///
/// <para><b>Why the band is sealed.</b> Every edge of the modelled spread graph is at most
/// <see cref="InfectionModel.SpreadReach"/> tiles long, except a plant bridge (four tiles) and a
/// downward vine (thirteen tiles, same column). The band clears every node within
/// <see cref="BlastPlanOptions.Clearance"/> tiles of the front, and additionally clears the nodes a
/// vine could reach in the front's own column, so no edge leaves the front without landing on a
/// cleared tile. A flood over the real graph then verifies that, rather than trusting the argument.
/// </para>
/// </remarks>
public static class BlastPlanner
{
    public static BlastPlan Plan(LoadedWorld world, BlastPlanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        BlastPlanOptions opts = options ?? new BlastPlanOptions();
        opts.Validate();

        InfectionModel model = InfectionModel.Build(world, opts.ExtraSeedRects);
        TileGrid tiles = world.Tiles;
        int width = tiles.Width;
        int height = tiles.Height;
        int tileCount = tiles.TileCount;
        List<string> notes = [];

        if (!model.HasInfection)
        {
            notes.Add("这个世界存档里没有检出腐化/猩红或神圣物块，没有需要隔离的感染源。");
            return new BlastPlan(
                world.Metadata,
                opts,
                new BlastPlanSummary(
                    InfectionNodes: model.NodeCount,
                    EvilSeeds: 0,
                    HallowSeeds: 0,
                    Sections: 0,
                    MixedSections: 0,
                    FenceTiles: 0,
                    Charges: 0,
                    DynamiteStacks: 0,
                    SeedsDestroyedByBlast: 0,
                    EnclosedSeedTiles: 0,
                    BlastImmuneNodesInFence: 0,
                    LavaTilesInFence: 0,
                    WaterTilesInFence: 0,
                    TrapTilesInFence: 0,
                    GravestoneTilesInFence: 0,
                    ExplosivesTilesInFence: 0,
                    BlastDestroyedTiles: 0,
                    BlastImmuneTilesInBlast: 0,
                    AllSectionsSealed: true,
                    VineAnchorTiles: 0,
                    EstimatedPlayerSeconds: 0),
                [],
                [],
                notes);
        }

        SpreadWorkspace workspace = new(model) { VineReach = opts.VineReach };
        IReadOnlyList<SeedCluster> clusters = workspace.ClusterSeeds(opts.MergeLinkDistance);
        if (clusters.Count > opts.MaxSections)
        {
            throw new InvalidOperationException(
                $"计划会生成 {clusters.Count} 段隔离带，超过上限 {opts.MaxSections}。" +
                "这通常说明感染源极度分散：请提高 --merge-gap 先把它们并成更大的段，" +
                "或提高 --max-sections 明确接受这个规模。");
        }

        byte[] fence = new byte[tileCount];
        byte[] covered = new byte[tileCount];
        List<int> coveredTouched = [];
        byte[] coverCount = new byte[tileCount];
        List<int> coverTouched = [];
        byte[] blastMark = new byte[tileCount];
        List<int> blastTouched = [];
        int[] blastOffsets = BuildBlastOffsets(opts.BlastRadius);

        // What a charge would break beyond the fence: 0 harmless, 1 crafted block, 2 structure.
        byte[] collateral = BuildCollateralMap(tiles);

        List<FenceSection> sections = [];
        List<BlastCharge> charges = [];
        int mixedSections = 0;
        bool allSealed = true;
        int seedsDestroyedByBlast = 0;
        int enclosedSeedTiles = 0;
        int blastImmuneNodesInFence = 0;
        int lavaInFence = 0;
        int waterInFence = 0;
        int trapInFence = 0;
        int graveInFence = 0;
        int explosivesInFence = 0;
        long blastDestroyed = 0;
        int blastImmuneInBlast = 0;
        int vineAnchors = 0;
        FenceTally totals = new();

        foreach (SeedCluster cluster in clusters)
        {
            TileRect ringRegion = cluster.Bounds.Expand(opts.Clearance + 1).Clamp(width, height);
            TileRect workRegion = cluster.Bounds
                .Expand(opts.Clearance + opts.VineReach + 2)
                .Clamp(width, height);

            workspace.ComputeChebyshevDistance(cluster.Seeds, ringRegion, opts.Clearance + 1);

            int sectionFenceTiles = 0;
            for (int y = workRegion.MinY; y <= workRegion.MaxY; y++)
            {
                int row = y * width;
                for (int x = workRegion.MinX; x <= workRegion.MaxX; x++)
                {
                    int index = row + x;
                    if (model.NodeMask[index] == 0)
                    {
                        continue;
                    }

                    ushort distance = workspace.DistanceAt(index);

                    if (distance == 0)
                    {
                        // A front tile stays inside the band, so an earlier section having already
                        // cleared it must not stop this section from cutting the vine it can grow.
                        // A vine keeps its grass tile's column, so that whole column needs a curtain
                        // as deep as vanilla's vine parent search allows.
                        if (!TileIds.SupportsPlantGrowth(tiles.TypeAt(index)))
                        {
                            continue;
                        }

                        // Tiles that can actually anchor a vine: the tile below is already empty, or
                        // the band is about to empty it. These are the only ones worth flagging for
                        // the player, and destroying them removes the vine vector outright.
                        int below = index + width;
                        if (below < tileCount && (tiles.ActiveAt(below) is false || fence[below] != 0))
                        {
                            vineAnchors++;
                        }

                        for (int dy = 1; dy <= opts.VineReach; dy++)
                        {
                            int ny = y + dy;
                            if (ny >= height)
                            {
                                break;
                            }

                            int curtain = (ny * width) + x;
                            if (model.NodeMask[curtain] == 0 || fence[curtain] != 0)
                            {
                                continue;
                            }

                            fence[curtain] = 1;
                            sectionFenceTiles++;
                        }

                        continue;
                    }

                    if (distance > opts.Clearance || fence[index] != 0)
                    {
                        continue;
                    }

                    fence[index] = 1;
                    sectionFenceTiles++;
                }
            }

            // Classify every fence tile by what can actually remove it before the flood check runs. A
            // tile that neither dynamite nor the assumed pickaxe can take out is cleared from the fence
            // mask so the verification reports the leak it really is: leaving it in the mask would make
            // a segment look sealed while the tile sits there spreading.
            int sectionDig = 0;
            int sectionBlocked = 0;
            bool hardMode = model.World.Metadata.HardMode;
            bool getGoodWorld = model.World.Metadata.GetGoodWorld;
            for (int y = workRegion.MinY; y <= workRegion.MaxY; y++)
            {
                int row = y * width;
                for (int x = workRegion.MinX; x <= workRegion.MaxX; x++)
                {
                    int index = row + x;
                    if (fence[index] == 0)
                    {
                        continue;
                    }

                    ushort type = tiles.TypeAt(index);
                    if (type == 0)
                    {
                        fence[index] = 0;
                        continue;
                    }

                    RemovalMethod method = TileCatalog.Classify(type, hardMode, downedGolemBoss: false, getGoodWorld, opts.PickPower);
                    if (method == RemovalMethod.Blast)
                    {
                        continue;
                    }

                    if (method == RemovalMethod.Blocked)
                    {
                        sectionBlocked++;
                        totals.BlockedTiles++;
                        totals.RequiredPickPower = Math.Max(
                            totals.RequiredPickPower,
                            Math.Min(TileCatalog.MinPickPower(type), TileCatalog.StrongestPickPower));
                        totals.Digs.Add(new DigOrder(x, y, type, 0, DigReason.Blocked));
                        fence[index] = 0;
                        continue;
                    }

                    // Blast immune but diggable: no charge can help, so the pickaxe owns this tile and
                    // the charge search must not count it as covered.
                    int hits = TileCatalog.DigHits(type, opts.PickPower);
                    sectionDig++;
                    totals.DigTiles++;
                    totals.DigHits += hits;
                    totals.RequiredPickPower = Math.Max(totals.RequiredPickPower, TileCatalog.MinPickPower(type));
                    totals.Digs.Add(new DigOrder(x, y, type, hits, DigReason.BlastImmune));
                    if (covered[index] == 0)
                    {
                        covered[index] = 1;
                        coveredTouched.Add(index);
                    }
                }
            }

            workspace.ResetDistance(ringRegion);

            // Verify against the real graph instead of trusting the construction argument.
            FloodResult flood = workspace.Flood(cluster.Seeds, fence, workRegion);
            bool isSealed = !flood.Escaped;
            allSealed &= isSealed;

            List<BlastCharge> sectionCharges = PlaceCharges(
                cluster,
                fence,
                covered,
                coveredTouched,
                coverCount,
                coverTouched,
                blastOffsets,
                collateral,
                model,
                tiles,
                opts,
                workRegion,
                totals);

            TallyHazards(
                sectionCharges,
                model,
                tiles,
                blastOffsets,
                blastMark,
                blastTouched,
                ref blastDestroyed,
                ref blastImmuneInBlast,
                ref seedsDestroyedByBlast,
                ref lavaInFence,
                ref waterInFence,
                ref trapInFence,
                ref graveInFence,
                ref explosivesInFence,
                ref blastImmuneNodesInFence);

            double widthPercent = 100d * cluster.Bounds.Width / width;
            double infectablePercent = 100d * cluster.SeedCount / Math.Max(1, model.NodeCount);
            bool withinLimits = widthPercent <= 30d && infectablePercent <= 35d && !cluster.IsMixed;
            if (cluster.IsMixed)
            {
                mixedSections++;
            }

            enclosedSeedTiles += cluster.SeedCount;
            sections.Add(new FenceSection(
                Sequence: cluster.Sequence,
                HasEvil: cluster.HasEvil,
                HasHallow: cluster.HasHallow,
                SeedTiles: cluster.SeedCount,
                FenceTiles: sectionFenceTiles,
                Charges: sectionCharges.Count,
                Bounds: cluster.Bounds,
                EnclosedWidthWorldPercent: Math.Round(widthPercent, 3),
                EnclosedInfectablePercent: Math.Round(infectablePercent, 3),
                WithinAnalyzerLimits: withinLimits,
                SealedByFloodVerification: isSealed,
                DigTiles: sectionDig,
                BlockedTiles: sectionBlocked));

            charges.AddRange(sectionCharges);
        }

        AddNotes(
            notes,
            allSealed,
            mixedSections,
            blastImmuneNodesInFence,
            explosivesInFence,
            graveInFence,
            trapInFence,
            lavaInFence,
            waterInFence);

        BlastPlanSummary summary = new(
            InfectionNodes: model.NodeCount,
            EvilSeeds: model.EvilSeedCount,
            HallowSeeds: model.HallowSeedCount,
            Sections: sections.Count,
            MixedSections: mixedSections,
            FenceTiles: sections.Sum(s => s.FenceTiles),
            Charges: charges.Count,
            DynamiteStacks: (int)Math.Ceiling(charges.Count / 99d),
            SeedsDestroyedByBlast: seedsDestroyedByBlast,
            EnclosedSeedTiles: enclosedSeedTiles,
            BlastImmuneNodesInFence: blastImmuneNodesInFence,
            LavaTilesInFence: lavaInFence,
            WaterTilesInFence: waterInFence,
            TrapTilesInFence: trapInFence,
            GravestoneTilesInFence: graveInFence,
            ExplosivesTilesInFence: explosivesInFence,
            BlastDestroyedTiles: blastDestroyed,
            BlastImmuneTilesInBlast: blastImmuneInBlast,
            AllSectionsSealed: allSealed,
            VineAnchorTiles: vineAnchors,
            EstimatedPlayerSeconds: EstimateSeconds(charges.Count, opts),
            DigTiles: totals.DigTiles,
            BlockedTiles: totals.BlockedTiles,
            RequiredPickPower: totals.RequiredPickPower,
            ChargesWithCollateral: totals.ChargesWithCollateral,
            ProtectedTilesInBlast: totals.ProtectedInBlast,
            PlayerBlocksInBlast: totals.PlayerBlocksInBlast,
            EstimatedDigSeconds: EstimateDigSeconds(totals));

        if (vineAnchors > 0)
        {
            string tail = opts.VineReach > 0
                ? $"计划已按向下 {opts.VineReach} 格的藤蔓路径加挖了竖井封带；" +
                  "想彻底断根，最稳的是把这批带草物块本身炸掉或换成不可感染物块（只炸掉这些物块比整条竖井省得多）。"
                : "本次没有按藤蔓路径加挖竖井（--vine-reach=0），所以**这些物块下方的空气通道没有被封住**：" +
                  "只要它们还在、下方还是空的，藤蔓迟早会把感染带下去。要么把它们本身炸掉，" +
                  "要么用 --vine-reach 重算一次。";

            notes.Add(
                $"隔离带内侧有 {vineAnchors} 格带草的前沿物块，它们下方是空的，会顺着空气长成腐化/猩红藤蔓" +
                $"，而藤蔓本身就是传播源（原版 CheckVines 会把藤蔓染成对应种类）。{tail}");
        }
        else if (opts.VineReach > 0)
        {
            notes.Add($"没有发现会长藤蔓的带草前沿物块（按向下 {opts.VineReach} 格检查），藤蔓这条路这次不用额外处理。");
        }

        notes.Add(
            $"雷管的瓦片破坏半径是 {opts.BlastRadius} 格（实际到轴向第 6 格），自伤判定却是 " +
            $"250x250 像素的方框（±7.8 格），所以每一发都要在引信 {opts.DynamiteFuseTicks} tick " +
            $"（{opts.DynamiteFuseTicks / 60d:0.#} 秒）结束前退到 {opts.BlastRadius + opts.RetreatMarginTiles} 格外；" +
            "计划为每发标注了它之前已经炸开的撤离点。");

        AddRemovalNotes(notes, totals, opts);

        PlanOverview overview = BuildOverview(model, fence, opts.Clearance);
        return new BlastPlan(world.Metadata, opts, summary, sections, charges, notes, overview, totals.Digs);
    }

    /// <summary>Downsamples the world so the map does not need the whole tile grid.</summary>
    private static PlanOverview BuildOverview(InfectionModel model, byte[] fence, int clearance)
    {
        const int cellSize = 16;
        TileGrid tiles = model.Tiles;
        int cols = (tiles.Width + cellSize - 1) / cellSize;
        int rows = (tiles.Height + cellSize - 1) / cellSize;
        byte[] cells = new byte[cols * rows];
        byte[] node = model.NodeMask;
        byte[] seed = model.SeedMask;

        for (int row = 0; row < rows; row++)
        {
            int y0 = row * cellSize;
            int y1 = Math.Min(tiles.Height - 1, y0 + cellSize - 1);
            for (int col = 0; col < cols; col++)
            {
                int x0 = col * cellSize;
                int x1 = Math.Min(tiles.Width - 1, x0 + cellSize - 1);
                byte value = 0;

                for (int y = y0; y <= y1; y++)
                {
                    int baseIndex = y * tiles.Width;
                    for (int x = x0; x <= x1; x++)
                    {
                        int index = baseIndex + x;
                        if (fence[index] != 0)
                        {
                            value |= 8;
                        }

                        if (node[index] == 0)
                        {
                            continue;
                        }

                        value |= 1;
                        switch ((SeedKind)seed[index])
                        {
                            case SeedKind.Evil:
                                value |= 2;
                                break;
                            case SeedKind.Hallow:
                                value |= 4;
                                break;
                        }
                    }
                }

                cells[(row * cols) + col] = value;
            }
        }

        _ = clearance;
        return new PlanOverview(cellSize, cols, rows, cells);
    }

    private static void AddNotes(
        List<string> notes,
        bool allSealed,
        int mixedSections,
        int blastImmuneNodesInFence,
        int explosivesInFence,
        int graveInFence,
        int trapInFence,
        int lavaInFence,
        int waterInFence)
    {
        if (!allSealed)
        {
            notes.Add("有隔离带的泛洪复核没能封住，已如实标出：这些段不构成完整隔离带，不要按它施工。");
        }

        if (mixedSections > 0)
        {
            notes.Add(
                $"{mixedSections} 段隔离带里同时含腐化/猩红与神圣感染源。它们在封带之前就已经连成同一片传播闭包，" +
                "挖隔离带无法让二者互不接触，只能把它们一起封起来。");
        }

        if (blastImmuneNodesInFence > 0)
        {
            notes.Add(
                $"隔离带里有 {blastImmuneNodesInFence} 个雷管炸不掉的感染物块。这说明铲除模型与游戏常量不一致，" +
                "必须先用镐子挖掉，否则封带会留洞。");
        }

        if (explosivesInFence > 0)
        {
            notes.Add($"隔离带覆盖了 {explosivesInFence} 格爆炸物/炸弹桶：雷管会引爆它们，施工前先手动清掉。");
        }

        if (graveInFence > 0)
        {
            notes.Add($"隔离带覆盖了 {graveInFence} 格墓碑物块；雷管能直接炸掉，不必单独挖。");
        }

        if (trapInFence > 0)
        {
            notes.Add($"隔离带覆盖了 {trapInFence} 格陷阱物块：施工前先拆掉或断线，别在雷管作业半径里踩响。");
        }

        if (lavaInFence > 0 || waterInFence > 0)
        {
            notes.Add(
                $"隔离带穿过液体：岩浆 {lavaInFence} 格、水 {waterInFence} 格。炸开会放液，" +
                "下游或下方是岩浆池时要先处理，否则施工途中会被烧到或淹到。");
        }
    }

    private static long EstimateSeconds(int charges, BlastPlanOptions opts)
    {
        // One charge costs the fuse plus walking in and back out along the band.
        double perCharge = (opts.DynamiteFuseTicks / 60d) + 6d;
        return (long)Math.Ceiling(charges * perCharge);
    }

    /// <summary>
    /// Pickaxe time for the tiles dynamite does not handle. A swing is about a third of a second and
    /// the player also has to walk between tiles, so the estimate is hits plus travel and is meant to
    /// answer "is digging this cheaper than blasting it", not to be a stopwatch.
    /// </summary>
    private static long EstimateDigSeconds(FenceTally totals)
    {
        double seconds = (totals.DigHits * 0.35) + (totals.DigTiles * 0.5);
        return (long)Math.Ceiling(seconds);
    }

    /// <summary>
    /// Explains the split between what dynamite removes and what the pickaxe has to, and refuses to
    /// pretend a fence is finished when a tile comes out with neither tool.
    /// </summary>
    private static void AddRemovalNotes(List<string> notes, FenceTally totals, BlastPlanOptions opts)
    {
        int blocked = totals.Digs.Count(dig => dig.Reason == DigReason.Blocked);
        int collateral = totals.Digs.Count(dig => dig.Reason == DigReason.Collateral);
        int immune = totals.Digs.Count(dig => dig.Reason == DigReason.BlastImmune);

        if (totals.DigTiles > 0)
        {
            string why = string.Join(
                "、",
                new[]
                {
                    immune > 0 ? $"{immune} 格雷管炸不掉" : null,
                    collateral > 0 ? $"{collateral} 格炸过去会打到建筑" : null,
                }.Where(part => part is not null));

            notes.Add(
                $"封带里有 {totals.DigTiles} 格要靠镐子（{why}），按当前设定的 " +
                $"{opts.PickPower}% 镐力估算 {totals.DigHits} 次挥镐、约 {EstimateDigSeconds(totals) / 60d:0.#} 分钟。" +
                $"这批格子需要镐力至少 {totals.RequiredPickPower}%" +
                (totals.RequiredPickPower > opts.PickPower
                    ? "，比当前设定更高：换把更好的镐子，或者把这些格子留给下一趟。"
                    : "。"));
        }

        if (blocked > 0)
        {
            string sample = string.Join(
                "；",
                totals.Digs
                    .Where(dig => dig.Reason == DigReason.Blocked)
                    .Take(5)
                    .Select(dig => $"({dig.X},{dig.Y}) 物块 {dig.Type}"));

            notes.Add(
                $"**有 {blocked} 格镐子和雷管都处理不掉**（前几格：{sample}）。这些格子已经按「炸不掉」从封带里剔除，" +
                "所以对应的隔离段复核为未封住：要么绕开它们改线，要么换镐力更高的工具再算一次。" +
                "祭坛、神庙砖（需要 210% 镐力）、叶绿矿（200%）是常见原因。");
        }

        if (totals.ChargesWithCollateral > 0)
        {
            notes.Add(
                $"有 {totals.ChargesWithCollateral} 发雷管的爆破范围会打到 {totals.ProtectedInBlast} 格家具/容器/门这类结构、" +
                $"{totals.PlayerBlocksInBlast} 格玩家建材（木板、砖、玻璃等）。当前保护级别是 {DescribeProtection(opts.Protection)}：" +
                "严格级别下这些摆位根本不会被采用，所以出现这个数字说明附近所有位置都躲不开，请人工看一眼这几发再施工。");
        }
        else if (opts.Protection != ProtectionLevel.None)
        {
            notes.Add(
                $"按 {DescribeProtection(opts.Protection)} 保护级别摆位：每一发雷管的爆破范围都避开了结构物与玩家建材，" +
                "炸不到房子、箱子、门、家具和平台的格子上（原版没有记录物块是谁放的，" +
                "这里是按材质表判断的，木板/灰砖/玻璃这类建材会被当成玩家建筑）。");
        }

        if (totals.DigTiles == 0 && blocked == 0)
        {
            notes.Add("这条隔离带全部由雷管完成，不需要额外挥镐（走到爆破点的通道另算）。");
        }
    }

    private static string DescribeProtection(ProtectionLevel level) => level switch
    {
        ProtectionLevel.Strict => "严格（结构和玩家建材都不炸，改挖）",
        ProtectionLevel.Structures => "结构优先（保护家具与容器，允许炸掉玩家自建的普通方块）",
        _ => "无保护（按最少雷管摆位，误伤只做统计）",
    };

    /// <summary>
    /// Every tile offset a blast reaches. <c>Projectile.ExplodeTiles</c> destroys a tile when
    /// <c>sqrt(dx^2 + dy^2) &lt; radius</c>, so the test uses the squared radius with a strict
    /// less-than: offset 6 along an axis is reachable, offset 7 is not.
    /// </summary>
    private static int[] BuildBlastOffsets(int radius)
    {
        List<int> offsets = [];
        int limit = radius * radius;
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if ((dx * dx) + (dy * dy) < limit)
                {
                    offsets.Add(PackOffset(dx, dy));
                }
            }
        }

        return [.. offsets];
    }

    private static int PackOffset(int dx, int dy) => ((dy & 0xFFFF) << 16) | (dx & 0xFFFF);

    /// <summary>
    /// Picks where to detonate so that the disc clears as many still-uncovered band tiles as
    /// possible, searching the window the covering charge has to live in. Ties go to the candidate
    /// nearest the seed tile, which keeps placement deterministic and keeps the charge next to the
    /// band it was found on.
    /// </summary>
    private static (int X, int Y, int Structures, int PlayerBlocks, bool Found) FindBestPlacement(
        int x,
        int y,
        byte[] fence,
        byte[] covered,
        int[] blastOffsets,
        byte[] collateral,
        int width,
        int height,
        int radius,
        ProtectionLevel protection)
    {
        int bestX = x;
        int bestY = y;
        int bestScore = -1;
        int bestDistance = int.MaxValue;
        int bestStructures = int.MaxValue;
        int bestPlayerBlocks = int.MaxValue;
        bool found = false;

        for (int cy = y - radius; cy <= y + radius; cy++)
        {
            if ((uint)cy >= (uint)height)
            {
                continue;
            }

            for (int cx = x - radius; cx <= x + radius; cx++)
            {
                if ((uint)cx >= (uint)width)
                {
                    continue;
                }

                int score = 0;
                int structures = 0;
                int playerBlocks = 0;
                foreach (int offset in blastOffsets)
                {
                    int nx = cx + OffsetX(offset);
                    int ny = cy + OffsetY(offset);
                    if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                    {
                        continue;
                    }

                    int neighbour = (ny * width) + nx;
                    if (fence[neighbour] != 0 && covered[neighbour] == 0)
                    {
                        score++;
                    }

                    switch (collateral[neighbour])
                    {
                        case 2:
                            structures++;
                            break;
                        case 1:
                            playerBlocks++;
                            break;
                    }
                }

                if (score == 0)
                {
                    continue;
                }

                int dx = cx - x;
                int dy = cy - y;
                int distance = (dx * dx) + (dy * dy);
                bool better;
                if (protection == ProtectionLevel.None)
                {
                    better = score > bestScore || (score == bestScore && distance < bestDistance);
                }
                else
                {
                    // Collateral first, coverage second: a charge that reaches one more fence tile by
                    // taking a wall out of somebody's house is not the better charge.
                    better = structures < bestStructures ||
                        (structures == bestStructures && playerBlocks < bestPlayerBlocks) ||
                        (structures == bestStructures && playerBlocks == bestPlayerBlocks &&
                            (score > bestScore || (score == bestScore && distance < bestDistance)));
                }

                if (better)
                {
                    bestScore = score;
                    bestDistance = distance;
                    bestStructures = structures;
                    bestPlayerBlocks = playerBlocks;
                    bestX = cx;
                    bestY = cy;
                    found = true;
                }
            }
        }

        return (bestX, bestY, bestStructures == int.MaxValue ? 0 : bestStructures, bestPlayerBlocks == int.MaxValue ? 0 : bestPlayerBlocks, found);
    }

    private static int OffsetX(int packed) => (short)(packed & 0xFFFF);

    private static int OffsetY(int packed) => (short)((packed >> 16) & 0xFFFF);

    private static List<BlastCharge> PlaceCharges(
        SeedCluster cluster,
        byte[] fence,
        byte[] covered,
        List<int> coveredTouched,
        byte[] coverCount,
        List<int> coverTouched,
        int[] blastOffsets,
        byte[] collateral,
        InfectionModel model,
        TileGrid tiles,
        BlastPlanOptions opts,
        TileRect workRegion,
        FenceTally totals)
    {
        List<(int X, int Y)> placed = [];
        Dictionary<(int X, int Y), (int Structures, int PlayerBlocks)> placementDamage = [];
        int width = tiles.Width;
        int height = tiles.Height;

        for (int y = workRegion.MinY; y <= workRegion.MaxY; y++)
        {
            int row = y * width;
            for (int x = workRegion.MinX; x <= workRegion.MaxX; x++)
            {
                int index = row + x;
                if (fence[index] == 0 || covered[index] != 0)
                {
                    continue;
                }

                // The first uncovered band tile only picks the neighbourhood to work in. Detonating
                // exactly on it wastes most of the disc: a band tile sits on the band's edge, so the
                // charge spends half its radius on the infection side. Searching the surrounding
                // window for the position that clears the most still-uncovered band tiles is what
                // turns a band into a few large bites instead of one charge per few tiles.
                (int bestX, int bestY, int structures, int playerBlocks, bool found) = FindBestPlacement(
                    x,
                    y,
                    fence,
                    covered,
                    blastOffsets,
                    collateral,
                    width,
                    height,
                    opts.BlastRadius,
                    opts.Protection);

                bool refuse = !found ||
                    (opts.Protection == ProtectionLevel.Strict && (structures > 0 || playerBlocks > 0)) ||
                    (opts.Protection == ProtectionLevel.Structures && structures > 0);

                if (refuse)
                {
                    // Either nothing in range clears this tile without wrecking something, or the best
                    // available placement does wreck something and the caller asked us not to. Both
                    // answers are the same: the pickaxe takes these tiles instead of dynamite. Digging
                    // costs time, blasting costs somebody's house, and time is the cheaper currency.
                    foreach (int offset in blastOffsets)
                    {
                        int nx = bestX + OffsetX(offset);
                        int ny = bestY + OffsetY(offset);
                        if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                        {
                            continue;
                        }

                        int neighbour = (ny * width) + nx;
                        if (fence[neighbour] == 0 || covered[neighbour] != 0)
                        {
                            continue;
                        }

                        covered[neighbour] = 1;
                        coveredTouched.Add(neighbour);
                        RegisterDig(tiles, neighbour, nx, ny, opts.PickPower, DigReason.Collateral, totals);
                    }

                    continue;
                }

                placed.Add((bestX, bestY));
                placementDamage[(bestX, bestY)] = (structures, playerBlocks);
                if (structures > 0 || playerBlocks > 0)
                {
                    totals.ChargesWithCollateral++;
                    totals.ProtectedInBlast += structures;
                    totals.PlayerBlocksInBlast += playerBlocks;
                }

                foreach (int offset in blastOffsets)
                {
                    int nx = bestX + OffsetX(offset);
                    int ny = bestY + OffsetY(offset);
                    if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                    {
                        continue;
                    }

                    int neighbour = (ny * width) + nx;
                    if (fence[neighbour] == 0 || covered[neighbour] != 0)
                    {
                        continue;
                    }

                    covered[neighbour] = 1;
                    coveredTouched.Add(neighbour);
                }
            }
        }

        foreach (int index in coveredTouched)
        {
            covered[index] = 0;
        }

        coveredTouched.Clear();

        if (placed.Count == 0)
        {
            return [];
        }

        // Second pass: drop charges whose every fence tile is also covered by some other charge.
        foreach ((int x, int y) in placed)
        {
            foreach (int offset in blastOffsets)
            {
                int nx = x + OffsetX(offset);
                int ny = y + OffsetY(offset);
                if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                {
                    continue;
                }

                int neighbour = (ny * width) + nx;
                if (fence[neighbour] == 0)
                {
                    continue;
                }

                if (coverCount[neighbour] == 0)
                {
                    coverTouched.Add(neighbour);
                }

                if (coverCount[neighbour] < byte.MaxValue)
                {
                    coverCount[neighbour]++;
                }
            }
        }

        List<(int X, int Y)> kept = [];
        foreach ((int x, int y) in placed)
        {
            bool essential = false;
            foreach (int offset in blastOffsets)
            {
                int nx = x + OffsetX(offset);
                int ny = y + OffsetY(offset);
                if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                {
                    continue;
                }

                int neighbour = (ny * width) + nx;
                if (fence[neighbour] != 0 && coverCount[neighbour] < 2)
                {
                    essential = true;
                    break;
                }
            }

            if (essential)
            {
                kept.Add((x, y));
            }
        }

        foreach (int index in coverTouched)
        {
            coverCount[index] = 0;
        }

        coverTouched.Clear();

        if (kept.Count == 0)
        {
            return [];
        }

        List<(int X, int Y)> ordered = OrderAlongBand(kept, model.World.Metadata.SpawnX, model.World.Metadata.SpawnY);
        List<BlastCharge> result = new(ordered.Count);

        for (int order = 0; order < ordered.Count; order++)
        {
            (int x, int y) = ordered[order];
            int coverTiles = 0;
            int seedsDestroyed = 0;
            bool lava = false;
            bool water = false;
            bool trap = false;
            bool grave = false;
            bool explosives = false;

            foreach (int offset in blastOffsets)
            {
                int nx = x + OffsetX(offset);
                int ny = y + OffsetY(offset);
                if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                {
                    continue;
                }

                int neighbour = (ny * width) + nx;
                if (fence[neighbour] != 0)
                {
                    coverTiles++;
                }

                if (model.IsSeed(neighbour))
                {
                    seedsDestroyed++;
                }

                ushort type = tiles.TypeAt(neighbour);
                if (TileIds.IsTrap(type))
                {
                    trap = true;
                }
                else if (TileIds.IsGravestone(type))
                {
                    grave = true;
                }
                else if (type == TileIds.Explosives || type == TileIds.TntBarrel)
                {
                    explosives = true;
                }

                switch (tiles.LiquidAt(neighbour))
                {
                    case LiquidKind.Lava:
                        lava = true;
                        break;
                    case LiquidKind.Water:
                        water = true;
                        break;
                }
            }

            (int standX, int standY, bool retreat) = FindRetreat(ordered, order, opts);
            placementDamage.TryGetValue((x, y), out (int Structures, int PlayerBlocks) damage);
            result.Add(new BlastCharge(
                Order: order + 1,
                X: x,
                Y: y,
                SectionSequence: cluster.Sequence,
                CoverTiles: coverTiles,
                SeedsDestroyed: seedsDestroyed,
                LavaInBlast: lava,
                WaterInBlast: water,
                TrapInBlast: trap,
                GravestoneInBlast: grave,
                ExplosivesInBlast: explosives,
                StandX: standX,
                StandY: standY,
                RetreatAvailable: retreat,
                ProtectedTilesInBlast: damage.Structures,
                PlayerBlocksInBlast: damage.PlayerBlocks));
        }

        return result;
    }

    /// <summary>
    /// Per-tile collateral class for every active tile: 0 worth nothing, 1 a crafted building block,
    /// 2 a structure (chest, door, furniture, platform, altar). Read once per plan because the charge
    /// search looks at the same tiles over and over.
    /// </summary>
    private static byte[] BuildCollateralMap(TileGrid tiles)
    {
        byte[] map = new byte[tiles.Count];
        for (int index = 0; index < map.Length; index++)
        {
            if (!tiles.ActiveAt(index))
            {
                continue;
            }

            ushort type = tiles.TypeAt(index);
            if (TileCatalog.IsProtectedStructure(type))
            {
                map[index] = 2;
            }
            else if (TileCatalog.IsPlayerBuilt(type))
            {
                map[index] = 1;
            }
        }

        return map;
    }

    /// <summary>Records a fence tile the pickaxe has to take out, whatever the reason.</summary>
    private static void RegisterDig(
        TileGrid tiles,
        int index,
        int x,
        int y,
        int pickPower,
        DigReason reason,
        FenceTally totals)
    {
        ushort type = tiles.TypeAt(index);
        int hits = TileCatalog.DigHits(type, pickPower);
        if (hits <= 0)
        {
            totals.BlockedTiles++;
            totals.RequiredPickPower = Math.Max(
                totals.RequiredPickPower,
                Math.Min(TileCatalog.MinPickPower(type), TileCatalog.StrongestPickPower));
            totals.Digs.Add(new DigOrder(x, y, type, 0, DigReason.Blocked));
            return;
        }

        totals.DigTiles++;
        totals.DigHits += hits;
        totals.RequiredPickPower = Math.Max(totals.RequiredPickPower, TileCatalog.MinPickPower(type));
        totals.Digs.Add(new DigOrder(x, y, type, hits, reason));
    }

    /// <summary>Running totals for the parts of the fence that dynamite does not handle.</summary>
    private sealed class FenceTally
    {
        public int DigTiles { get; set; }

        public int BlockedTiles { get; set; }

        public long DigHits { get; set; }

        public int RequiredPickPower { get; set; }

        public int ChargesWithCollateral { get; set; }

        public int ProtectedInBlast { get; set; }

        public int PlayerBlocksInBlast { get; set; }

        public List<DigOrder> Digs { get; } = [];
    }

    /// <summary>
    /// Where the player should be standing when this charge detonates: a point reached earlier in
    /// the same band, so the ground between it and the charge is already open.
    /// </summary>
    private static (int X, int Y, bool Available) FindRetreat(List<(int X, int Y)> ordered, int order, BlastPlanOptions opts)
    {
        int required = opts.BlastRadius + opts.RetreatMarginTiles;
        int requiredSquared = required * required;
        (int X, int Y) charge = ordered[order];

        for (int back = order - 1; back >= 0; back--)
        {
            (int x, int y) = ordered[back];
            int dx = x - charge.X;
            int dy = y - charge.Y;
            if ((dx * dx) + (dy * dy) >= requiredSquared)
            {
                return (x, y, true);
            }
        }

        return (charge.X, charge.Y, false);
    }

    /// <summary>
    /// Orders charges by nearest neighbour from the world spawn, so the player walks each band once
    /// and always retreats into ground an earlier charge already opened. A uniform grid keeps the
    /// search local: a fence with tens of thousands of charges must not cost a quadratic scan.
    /// </summary>
    private static List<(int X, int Y)> OrderAlongBand(List<(int X, int Y)> points, int entryX, int entryY)
    {
        if (points.Count <= 2)
        {
            return [.. points];
        }

        const int cellSize = 32;
        Dictionary<(int X, int Y), List<int>> grid = [];
        for (int i = 0; i < points.Count; i++)
        {
            (int cx, int cy) = (points[i].X / cellSize, points[i].Y / cellSize);
            if (!grid.TryGetValue((cx, cy), out List<int>? bucket))
            {
                bucket = [];
                grid[(cx, cy)] = bucket;
            }

            bucket.Add(i);
        }

        bool[] used = new bool[points.Count];
        List<(int X, int Y)> ordered = new(points.Count);

        int start = 0;
        long bestStart = long.MaxValue;
        for (int i = 0; i < points.Count; i++)
        {
            long dx = points[i].X - entryX;
            long dy = points[i].Y - entryY;
            long score = (dx * dx) + (dy * dy);
            if (score < bestStart)
            {
                bestStart = score;
                start = i;
            }
        }

        int current = start;
        for (int step = 0; step < points.Count; step++)
        {
            used[current] = true;
            ordered.Add(points[current]);

            int next = -1;
            long nextDistance = long.MaxValue;
            (int cx, int cy) = (points[current].X / cellSize, points[current].Y / cellSize);

            for (int ring = 0; ring <= 64; ring++)
            {
                if (next >= 0 && (long)ring * cellSize * (ring * cellSize) > nextDistance)
                {
                    break;
                }

                bool anyCell = false;
                for (int gx = cx - ring; gx <= cx + ring; gx++)
                {
                    for (int gy = cy - ring; gy <= cy + ring; gy++)
                    {
                        // Only the perimeter of the ring is new.
                        if (ring > 0 && Math.Abs(gx - cx) != ring && Math.Abs(gy - cy) != ring)
                        {
                            continue;
                        }

                        if (!grid.TryGetValue((gx, gy), out List<int>? bucket))
                        {
                            continue;
                        }

                        anyCell = true;
                        foreach (int i in bucket)
                        {
                            if (used[i])
                            {
                                continue;
                            }

                            long dx = points[i].X - points[current].X;
                            long dy = points[i].Y - points[current].Y;
                            long distance = (dx * dx) + (dy * dy);
                            if (distance < nextDistance)
                            {
                                nextDistance = distance;
                                next = i;
                            }
                        }
                    }
                }

                if (!anyCell && ring > 4)
                {
                    break;
                }
            }

            if (next < 0)
            {
                break;
            }

            current = next;
        }

        // A disconnected leftover (should not happen for a single band) is appended unchanged.
        if (ordered.Count < points.Count)
        {
            for (int i = 0; i < points.Count; i++)
            {
                if (!used[i])
                {
                    ordered.Add(points[i]);
                }
            }
        }

        return ordered;
    }

    private static void TallyHazards(
        List<BlastCharge> charges,
        InfectionModel model,
        TileGrid tiles,
        int[] blastOffsets,
        byte[] blastMark,
        List<int> blastTouched,
        ref long blastDestroyed,
        ref int blastImmuneInBlast,
        ref int seedsDestroyed,
        ref int lava,
        ref int water,
        ref int trap,
        ref int grave,
        ref int explosives,
        ref int immuneNodes)
    {
        int width = tiles.Width;
        int height = tiles.Height;

        foreach (BlastCharge charge in charges)
        {
            seedsDestroyed += charge.SeedsDestroyed;

            foreach (int offset in blastOffsets)
            {
                int nx = charge.X + OffsetX(offset);
                int ny = charge.Y + OffsetY(offset);
                if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                {
                    continue;
                }

                int index = (ny * width) + nx;
                if (blastMark[index] != 0)
                {
                    continue;
                }

                blastMark[index] = 1;
                blastTouched.Add(index);
                blastDestroyed++;

                ushort type = tiles.TypeAt(index);
                if (type != 0 &&
                    TileIds.IsDynamiteImmune(type, model.World.Metadata.HardMode, downedGolemBoss: false, model.World.Metadata.GetGoodWorld))
                {
                    blastImmuneInBlast++;
                    if (TileIds.IsInfectionNode(type))
                    {
                        immuneNodes++;
                    }
                }

                if (TileIds.IsTrap(type))
                {
                    trap++;
                }
                else if (TileIds.IsGravestone(type))
                {
                    grave++;
                }
                else if (type == TileIds.Explosives || type == TileIds.TntBarrel)
                {
                    explosives++;
                }

                switch (tiles.LiquidAt(index))
                {
                    case LiquidKind.Lava:
                        lava++;
                        break;
                    case LiquidKind.Water:
                        water++;
                        break;
                }
            }
        }

        foreach (int index in blastTouched)
        {
            blastMark[index] = 0;
        }

        blastTouched.Clear();
    }
}
