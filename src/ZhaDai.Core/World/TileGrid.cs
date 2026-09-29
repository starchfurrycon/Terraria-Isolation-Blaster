using System.Runtime.CompilerServices;

namespace ZhaDai.Core.World;

/// <summary>Which liquid a tile holds. Mirrors the two-bit liquid header in the world file.</summary>
public enum LiquidKind : byte
{
    None = 0,
    Water = 1,
    Lava = 2,
    Honey = 3,
}

/// <summary>
/// A full world tile grid kept in memory. Every cell costs 4 bytes
/// (<c>ushort</c> type + 1 byte flags + 1 byte liquid amount), so a large world
/// (8400x2400) is about 80 MB.
/// </summary>
/// <remarks>
/// The upstream analyzer streams a world and throws the tiles away after each run. The blast
/// planner cannot work that way: the fence geometry, the blast-immunity check, and the hazard
/// scan all need random access to arbitrary tiles. This grid is therefore deliberately retained.
/// </remarks>
public sealed class TileGrid
{
    private const byte FlagActive = 0x01;
    private const byte FlagActuated = 0x02;
    private const byte FlagHasLiquid = 0x04;

    // Bits 3..5 hold the slope (0..4 in the file format). Bits 6..7 hold the liquid kind minus
    // one, so a tile with no liquid leaves them zero.
    private const byte SlopeShift = 3;
    private const byte SlopeMask = 0x07;
    private const byte LiquidKindShift = 6;
    private const byte LiquidKindMask = 0x03;

    private readonly ushort[] types;
    private readonly byte[] flags;
    private readonly byte[] liquids;

    public TileGrid(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "World dimensions must be positive.");
        }

        Width = width;
        Height = height;
        TileCount = width * height;
        types = new ushort[TileCount];
        flags = new byte[TileCount];
        liquids = new byte[TileCount];
    }

    public int Width { get; }

    public int Height { get; }

    public int TileCount { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Index(int x, int y) => (y * Width) + x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Active(int x, int y) => (flags[(y * Width) + x] & FlagActive) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ActiveAt(int index) => (flags[index] & FlagActive) != 0;

    /// <summary>Tile type at the cell, or 0 when the cell is empty.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort Type(int x, int y)
    {
        int index = (y * Width) + x;
        return (flags[index] & FlagActive) != 0 ? types[index] : (ushort)0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort TypeAt(int index) => (flags[index] & FlagActive) != 0 ? types[index] : (ushort)0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ActuatedAt(int index) => (flags[index] & FlagActuated) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int SlopeAt(int index) => (flags[index] >> SlopeShift) & SlopeMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LiquidKind LiquidAt(int index) =>
        (flags[index] & FlagHasLiquid) != 0
            ? (LiquidKind)(((flags[index] >> LiquidKindShift) & LiquidKindMask) + 1)
            : LiquidKind.None;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte LiquidAmountAt(int index) => liquids[index];

    public bool HasLiquid(int x, int y)
    {
        int index = (y * Width) + x;
        return (flags[index] & FlagHasLiquid) != 0 && liquids[index] > 0;
    }

    /// <summary>True when the cell physically blocks a walking or falling player.</summary>
    public bool IsSolid(int x, int y)
    {
        int index = (y * Width) + x;
        if ((flags[index] & FlagActive) == 0)
        {
            return false;
        }

        // An actuated tile is drawn but does not collide.
        return (flags[index] & FlagActuated) == 0;
    }

    internal void SetTile(int index, ushort type, bool active, bool actuated, int slope, LiquidKind liquid, byte liquidAmount)
    {
        types[index] = type;
        byte value = 0;
        if (active)
        {
            value |= FlagActive;
        }

        if (actuated)
        {
            value |= FlagActuated;
        }

        value |= (byte)((slope & SlopeMask) << SlopeShift);
        if (liquidAmount > 0 && liquid != LiquidKind.None)
        {
            value |= FlagHasLiquid;
            value |= (byte)(((byte)(liquid - 1) & LiquidKindMask) << LiquidKindShift);
            liquids[index] = liquidAmount;
        }
        else
        {
            liquids[index] = 0;
        }

        flags[index] = value;
    }

    /// <summary>
    /// Writes one cell. Exposed so tests and tools can build worlds in memory without going through
    /// the file reader.
    /// </summary>
    public void Set(
        int x,
        int y,
        ushort type,
        bool active = true,
        bool actuated = false,
        int slope = 0,
        LiquidKind liquid = LiquidKind.None,
        byte liquidAmount = 0)
    {
        if (!InBounds(x, y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"({x},{y}) is outside {Width}x{Height}.");
        }

        SetTile((y * Width) + x, type, active, actuated, slope, liquid, liquidAmount);
    }

    /// <summary>Fills a rectangle with one tile type.</summary>
    public void FillRect(int minX, int minY, int maxX, int maxY, ushort type, bool active = true)
    {
        for (int y = Math.Max(0, minY); y <= Math.Min(Height - 1, maxY); y++)
        {
            for (int x = Math.Max(0, minX); x <= Math.Min(Width - 1, maxX); x++)
            {
                SetTile((y * Width) + x, type, active, actuated: false, slope: 0, LiquidKind.None, 0);
            }
        }
    }

    /// <summary>Clears a cell to empty air, which is what blasting it leaves behind.</summary>
    /// <summary>
    /// Walls that are not worldgen's own, keyed by tile index. Stored as a sparse map instead of one
    /// array per tile: a large world has twenty million tiles and perhaps a few thousand walls anybody
    /// built, so the useful set is tiny and the array would be forty megabytes of zeroes.
    /// </summary>
    private readonly Dictionary<int, ushort> builtWalls = [];

    /// <summary>Records a wall, keeping only the ones that are not worldgen's own.</summary>
    public void SetWall(int index, ushort wall)
    {
        if (TileCatalog.IsNaturalWall(wall))
        {
            return;
        }

        builtWalls[index] = wall;
    }

    /// <summary>The wall at a tile, or 0 when it is worldgen's own or absent.</summary>
    public ushort BuiltWallAt(int index) => builtWalls.TryGetValue(index, out ushort wall) ? wall : (ushort)0;

    /// <summary>True when somebody built a wall here, so blasting it would damage a building.</summary>
    public bool HasBuiltWallAt(int index) => builtWalls.ContainsKey(index);

    /// <summary>How many tiles carry a wall a player built. Reported so the numbers are auditable.</summary>
    public int BuiltWallCount => builtWalls.Count;

    /// <summary>Wall id to tile count, so a plan can show what it decided to protect and why.</summary>
    public IReadOnlyDictionary<ushort, int> BuiltWallHistogram()
    {
        Dictionary<ushort, int> histogram = [];
        foreach (ushort wall in builtWalls.Values)
        {
            histogram[wall] = histogram.TryGetValue(wall, out int count) ? count + 1 : 1;
        }

        return histogram;
    }

    public void Clear(int index) => SetTile(index, 0, active: false, actuated: false, slope: 0, LiquidKind.None, 0);

    public int Count => TileCount;
}
