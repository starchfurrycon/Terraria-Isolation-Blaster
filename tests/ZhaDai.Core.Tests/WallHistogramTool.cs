using System;
using System.Globalization;
using System.IO;
using System.Linq;
using ZhaDai.Core.Planning;
using ZhaDai.Core.World;

namespace ZhaDai.Core.Tests;

/// <summary>
/// A one off diagnostic used while wiring walls into the planner: it prints the wall id histogram of a
/// world so the "is this wall worldgen's or somebody's" table can be checked against a real save instead
/// of guessed at. Run it with <c>--walls &lt;world.wld&gt;</c>.
/// </summary>
internal static class WallHistogramTool
{
    public static void Run(string worldPath)
    {
        LoadedWorld world = WorldFileReader.Read(worldPath);
        Console.WriteLine($"世界 {world.Metadata.Title}：{world.Metadata.Width}x{world.Metadata.Height}");
        Console.WriteLine($"墙体总数（非自然）：{world.Tiles.BuiltWallCount}");

        Console.WriteLine("前 20 个墙 id：");
        foreach (var entry in world.Tiles.BuiltWallHistogram().OrderByDescending(pair => pair.Value).Take(20))
        {
            Console.WriteLine(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "  wall {0,-6} {1,10} 格  自然表里吗={2}",
                    entry.Key,
                    entry.Value,
                    TileCatalog.IsNaturalWall(entry.Key)));
        }
    }
}
