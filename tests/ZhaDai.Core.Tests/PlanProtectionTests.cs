using System;
using System.Collections.Generic;
using System.Linq;
using ZhaDai.Core.Planning;
using ZhaDai.Core.World;

namespace ZhaDai.Core.Tests;

/// <summary>
/// Checks the promise that dynamite does not flatten things people built. The world here is synthetic
/// on purpose: a real save cannot be arranged to put a chest on the fence line on demand.
/// </summary>
internal static class PlanProtectionTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("规划器（保护建筑 / 镐子分担）");

        const int width = 160;
        const int height = 80;
        const int seedX = 80;
        const int seedY = 40;

        BlastPlan strict = Plan(width, height, seedX, seedY, ProtectionLevel.Strict, chestOffset: 8);
        BlastPlan none = Plan(width, height, seedX, seedY, ProtectionLevel.None, chestOffset: 8);

        check(strict.Summary.AllSectionsSealed, "严格保护下这条带依然复核为封住");
        check(
            strict.Summary.ProtectedTilesInBlast == 0,
            $"严格保护下没有一发雷管会打到结构物（实际 {strict.Summary.ProtectedTilesInBlast} 格）");
        check(
            strict.Charges.All(charge => charge.ProtectedTilesInBlast == 0),
            "逐发检查也没有结构物在爆破范围里");
        check(
            none.Summary.ProtectedTilesInBlast > 0,
            $"不保护时确实会打到箱子（实际 {none.Summary.ProtectedTilesInBlast} 格），说明上面那条不是空话");
        // The chest is small enough to route around, so protection should show up as different charge
        // positions rather than as extra digging. The wood box case below is the one that has to dig.
        HashSet<(int X, int Y)> unprotected = new HashSet<(int X, int Y)>(none.Charges.Select(charge => (charge.X, charge.Y)));
        int moved = strict.Charges.Count(charge => !unprotected.Contains((charge.X, charge.Y)));
        check(moved > 0, $"严格保护下摆位换了位置去躲开箱子（换位 {moved} 发）");
        check(
            strict.Summary.FenceTiles == none.Summary.FenceTiles,
            $"保护只换摆位、不会少封带格（严格 {strict.Summary.FenceTiles} 格 / 不保护 {none.Summary.FenceTiles} 格）");

        // A band tile with crafted wood all around it: every charge that could reach it is inside the
        // wood, so there is no zero-collateral placement left and the tile has to be dug.
        BlastPlan wall = Plan(width, height, seedX, seedY, ProtectionLevel.Strict, chestOffset: -1, woodBox: true);
        BlastPlan wallNone = Plan(width, height, seedX, seedY, ProtectionLevel.None, chestOffset: -1, woodBox: true);
        check(
            wall.Summary.PlayerBlocksInBlast == 0,
            $"严格保护下玩家建材也不炸（实际 {wall.Summary.PlayerBlocksInBlast} 格）");
        check(
            wallNone.Summary.PlayerBlocksInBlast > 0,
            $"不保护时木板会被炸掉（实际 {wallNone.Summary.PlayerBlocksInBlast} 格）");
        check(
            wall.DigOrders.Any(dig => dig.Reason == DigReason.Collateral),
            "被建材围住的封带格改为镐子挖");
        check(
            wall.Summary.DigTiles > 0 && wall.Summary.RequiredPickPower <= 100,
            $"改挖的格子给了数量与镐力要求（{wall.Summary.DigTiles} 格、{wall.Summary.RequiredPickPower}%）");
        check(
            wall.Summary.EstimatedDigSeconds > 0,
            "改挖的时间也算进了施工预估");

        // Walls: the game never records who placed a wall, so the table decides. Wood (4) is craftable and
        // therefore somebody's; the worldgen walls are all named *Unsafe and stay blastable. Here a wood
        // wall runs straight through the band, and the only way to keep it is to mine those tiles.
        BlastPlan walled = Plan(width, height, seedX, seedY, ProtectionLevel.Strict, chestOffset: -1, wallRow: true);
        BlastPlan walledNone = Plan(width, height, seedX, seedY, ProtectionLevel.None, chestOffset: -1, wallRow: true);
        check(
            walled.Summary.BuiltWallTilesInBlast == 0,
            $"严格保护下玩家墙一格都没被炸到（实际 {walled.Summary.BuiltWallTilesInBlast} 格）");
        check(
            walledNone.Summary.BuiltWallTilesInBlast > 0,
            $"不保护时同一堵墙会被炸开（实际 {walledNone.Summary.BuiltWallTilesInBlast} 格）");
        check(
            walled.Summary.DigTiles > 0,
            $"宁愿多挖也不炸墙（改挖 {walled.Summary.DigTiles} 格）");
        check(
            walled.Summary.BuiltWallTilesWorld > 0,
            $"计划如实报出世界里有多少玩家墙（{walled.Summary.BuiltWallTilesWorld} 格）");

        BlastPlan tight = Plan(
            width,
            height,
            seedX,
            seedY,
            ProtectionLevel.Strict,
            chestOffset: -1,
            wallRow: true,
            protectionBuffer: 0);
        check(
            walled.Summary.DigTiles >= tight.Summary.DigTiles,
            $"1 格缓冲不会比不留缓冲更省（缓冲 {walled.Summary.DigTiles} 格 / 不留 {tight.Summary.DigTiles} 格）");
        check(
            walled.DigOrders.All(dig => dig.Hits >= 0),
            "改挖清单里没有负数镐击（处理不掉的格子走 Blocked，不算进挖掘）");
    }

    private static BlastPlan Plan(
        int width,
        int height,
        int seedX,
        int seedY,
        ProtectionLevel protection,
        int chestOffset,
        bool woodBox = false,
        bool wallRow = false,
        int protectionBuffer = 1,

        int pickPower = TileCatalog.PreHardmodePickPower)
    {
        TileGrid tiles = new TileGrid(width, height);
        tiles.FillRect(0, 0, width - 1, height - 1, 1);
        tiles.FillRect(seedX - 2, seedY - 2, seedX + 2, seedY + 2, 25);

        if (chestOffset > 0)
        {
            tiles.Set(seedX + chestOffset, seedY, 21, active: true);
        }

        if (wallRow)
        {
            // A wood wall straight through the band. Wall id 4 is WallID.Wood: craftable, so the planner
            // treats it as somebody's building, unlike the worldgen walls whose names all end in Unsafe.
            for (int x = seedX - 20; x <= seedX + 20; x++)
            {
                tiles.SetWall(tiles.Index(x, seedY), 4);
            }
        }

        if (woodBox)
        {
            // Crafted wood everywhere except one band tile, which stays a stone node at distance 6.
            tiles.FillRect(seedX + 1, seedY - 8, seedX + 20, seedY + 8, 30);
            tiles.Set(seedX + 6, seedY, 1, active: true);
        }

        WorldMetadata metadata = new WorldMetadata(
            Version: 326,
            Title: "合成世界",
            Seed: "1",
            WorldId: 1,
            Width: width,
            Height: height,
            SpawnX: 20,
            SpawnY: 20,
            WorldSurface: 20d,
            RockLayer: 40d,
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
            Protection = protection,
            ProtectionBuffer = protectionBuffer,
            PickPower = pickPower,
            VineReach = 0,
        };

        return BlastPlanner.Plan(new LoadedWorld(metadata, tiles), options);
    }
}
