using ZhaDai.Core.World;

namespace ZhaDai.Core.Analysis;

/// <summary>Which infection a seed tile carries.</summary>
public enum SeedKind : byte
{
    None = 0,
    Evil = 1,
    Hallow = 2,
}

/// <summary>An inclusive tile rectangle.</summary>
public readonly record struct TileRect(int MinX, int MinY, int MaxX, int MaxY)
{
    public bool IsEmpty => MaxX < MinX || MaxY < MinY;

    public int Width => MaxX - MinX + 1;

    public int Height => MaxY - MinY + 1;

    public long Area => (long)Width * Height;

    public static TileRect FromPoints(int x0, int y0, int x1, int y1) =>
        new(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));

    public TileRect Expand(int margin) => new(MinX - margin, MinY - margin, MaxX + margin, MaxY + margin);

    public TileRect Clamp(int width, int height) => new(
        Math.Max(0, MinX),
        Math.Max(0, MinY),
        Math.Min(width - 1, MaxX),
        Math.Min(height - 1, MaxY));

    public bool Contains(int x, int y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

    public TileRect Union(TileRect other) => IsEmpty
        ? other
        : other.IsEmpty
            ? this
            : new(Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY), Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));
}

/// <summary>
/// The infection view of a world: which tiles can take part in the vanilla spread graph, which
/// tiles already carry an infection, and the connected components of the spread graph.
/// </summary>
/// <remarks>
/// <para>
/// The spread model is deliberately conservative and follows Terraria-Biome-Containment-Analyzer:
/// ordinary infection can select a target within three tiles on each axis (Chebyshev distance 3),
/// and a tile that can host grass can additionally bridge through a one-tile plant or thorn up to
/// four tiles horizontally or upward, or through a downward vine whose vanilla parent search is
/// limited to thirteen tiles.
/// </para>
/// <para>
/// Being conservative here is the safe direction: extra edges can only make the planner widen the
/// fence, never leave a hole.
/// </para>
/// </remarks>
public sealed class InfectionModel
{
    public const int SpreadReach = 3;
    public const int PlantHorizontalReach = 4;
    public const int PlantUpwardReach = 4;
    public const int VineDownwardReach = 13;

    private readonly byte[] node;
    private readonly byte[] seed;

    private InfectionModel(LoadedWorld world, byte[] node, byte[] seed, int nodeCount, int evilCount, int hallowCount)
    {
        World = world;
        Tiles = world.Tiles;
        this.node = node;
        this.seed = seed;
        NodeCount = nodeCount;
        EvilSeedCount = evilCount;
        HallowSeedCount = hallowCount;
    }

    public LoadedWorld World { get; }

    public TileGrid Tiles { get; }

    public int NodeCount { get; }

    public int EvilSeedCount { get; }

    public int HallowSeedCount { get; }

    public bool HasInfection => EvilSeedCount > 0 || HallowSeedCount > 0;

    public static InfectionModel Build(LoadedWorld world, IReadOnlyList<TileRect>? extraSeedRects = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        TileGrid tiles = world.Tiles;
        int count = tiles.TileCount;
        byte[] node = new byte[count];
        byte[] seed = new byte[count];
        int nodeCount = 0;
        int evilCount = 0;
        int hallowCount = 0;

        for (int index = 0; index < count; index++)
        {
            ushort type = tiles.TypeAt(index);
            if (type == 0)
            {
                continue;
            }

            if (!TileIds.IsInfectionNode(type))
            {
                continue;
            }

            node[index] = 1;
            nodeCount++;

            if (TileIds.IsEvil(type))
            {
                seed[index] = (byte)SeedKind.Evil;
                evilCount++;
            }
            else if (TileIds.IsHallow(type))
            {
                seed[index] = (byte)SeedKind.Hallow;
                hallowCount++;
            }
        }

        if (extraSeedRects is not null)
        {
            foreach (TileRect rect in extraSeedRects)
            {
                TileRect clamped = rect.Clamp(tiles.Width, tiles.Height);
                for (int y = clamped.MinY; y <= clamped.MaxY; y++)
                {
                    for (int x = clamped.MinX; x <= clamped.MaxX; x++)
                    {
                        int index = (y * tiles.Width) + x;
                        if (node[index] == 0)
                        {
                            node[index] = 1;
                            nodeCount++;
                        }

                        if (seed[index] == 0)
                        {
                            seed[index] = (byte)SeedKind.Evil;
                            evilCount++;
                        }
                    }
                }
            }
        }

        return new InfectionModel(world, node, seed, nodeCount, evilCount, hallowCount);
    }

    public bool IsNode(int index) => node[index] != 0;

    public bool IsSeed(int index) => seed[index] != 0;

    public SeedKind KindAt(int index) => (SeedKind)seed[index];

    public byte[] NodeMask => node;

    public byte[] SeedMask => seed;

    public IEnumerable<int> EnumerateSeeds()
    {
        for (int index = 0; index < seed.Length; index++)
        {
            if (seed[index] != 0)
            {
                yield return index;
            }
        }
    }

    /// <summary>
    /// Whether a tile that carries a plant can bridge to another node. The caller supplies the
    /// candidate target; source tiles that cannot host plants simply never bridge.
    /// </summary>
    public bool CanPlantBridge(int sourceIndex, int targetIndex, IReadOnlySet<int>? plugged = null)
    {
        if (node[sourceIndex] == 0 || node[targetIndex] == 0)
        {
            return false;
        }

        ushort type = Tiles.TypeAt(sourceIndex);
        if (!TileIds.SupportsPlantGrowth(type))
        {
            return false;
        }

        int sourceX = sourceIndex % Tiles.Width;
        int sourceY = sourceIndex / Tiles.Width;
        int targetX = targetIndex % Tiles.Width;
        int targetY = targetIndex / Tiles.Width;
        int dx = Math.Abs(targetX - sourceX);
        int dy = targetY - sourceY;

        if (dx == 0 && dy > 0 && dy <= VineDownwardReach)
        {
            // A block placed directly under the plant leaves the vine nowhere to start, so a plugged
            // column is not a route at all. That is the whole reason plugs are worth carrying: one inert
            // block replaces a curtain as deep as the vine can reach.
            if (plugged != null && plugged.Contains(sourceIndex + Tiles.Width))
            {
                return false;
            }

            return true; // downward vine
        }

        if (dx <= PlantHorizontalReach && dy == 0)
        {
            return true; // surface plant or thorn
        }

        return dy < 0 && -dy <= PlantUpwardReach && dx <= PlantHorizontalReach;
    }
}
