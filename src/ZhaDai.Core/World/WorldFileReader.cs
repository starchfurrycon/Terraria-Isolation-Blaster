using System.Text;

namespace ZhaDai.Core.World;

/// <summary>World header fields the planner cares about.</summary>
public sealed record WorldMetadata(
    uint Version,
    string Title,
    string Seed,
    int WorldId,
    int Width,
    int Height,
    int SpawnX,
    int SpawnY,
    double WorldSurface,
    double RockLayer,
    int DungeonX,
    bool IsCrimson,
    bool HardMode,
    bool DrunkWorld,
    bool GetGoodWorld,
    bool RemixWorld,
    bool SkyblockWorld,
    bool NoTraps,
    bool ZenithWorld)
{
    public int WorldSurfaceY => (int)WorldSurface;

    public int RockLayerY => (int)RockLayer;

    public int UnderworldY => Height - 200;
}

/// <summary>A world read into memory: header plus every tile.</summary>
public sealed record LoadedWorld(WorldMetadata Metadata, TileGrid Tiles);

/// <summary>
/// Reads a vanilla Terraria <c>.wld</c> file.
/// </summary>
/// <remarks>
/// The section/version layout is ported from Terraria-Biome-Containment-Analyzer's
/// <c>VanillaWorldReader</c> (formats 269-326, through 1.4.5.8) so that a world file accepted
/// there is accepted here byte for byte, except that this reader keeps the tiles instead of
/// folding them into per-column counters. The file is opened read-only and shared, so it never
/// blocks Terraria's own save or backup replacement.
/// </remarks>
public static class WorldFileReader
{
    public const uint MinimumSupportedVersion = 269;
    public const uint MaximumSupportedVersion = 326;

    public static LoadedWorld Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        uint version = reader.ReadUInt32();
        if (version is < MinimumSupportedVersion or > MaximumSupportedVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Terraria world format {version}. Supported formats: " +
                $"{MinimumSupportedVersion}-{MaximumSupportedVersion} (through Terraria 1.4.5.8). No data was changed.");
        }

        string signature = Encoding.ASCII.GetString(ReadExact(reader, 7));
        byte fileType = reader.ReadByte();
        if ((signature != "relogic" && signature != "xindong") || fileType != 2)
        {
            throw new InvalidDataException("The selected file is not a supported Terraria world file.");
        }

        _ = reader.ReadUInt32(); // file revision
        _ = reader.ReadUInt64(); // favorite and future file flags

        short sectionCount = reader.ReadInt16();
        if (sectionCount is < 3 or > 32)
        {
            throw new InvalidDataException($"Invalid section count: {sectionCount}.");
        }

        int[] sections = new int[sectionCount];
        for (int i = 0; i < sections.Length; i++)
        {
            sections[i] = reader.ReadInt32();
        }

        ValidateSections(sections, stream.Length);
        bool[] frameImportant = ReadPackedBooleans(reader);
        if (stream.Position != sections[0])
        {
            throw new InvalidDataException(
                $"World section header ended at {stream.Position}, expected {sections[0]}.");
        }

        WorldMetadata metadata = ReadHeader(reader, version, sections[1]);
        stream.Position = sections[1];

        TileGrid tiles = ReadTiles(reader, metadata, version, frameImportant);
        if (stream.Position != sections[2])
        {
            throw new InvalidDataException(
                $"Tile section ended at {stream.Position}, expected {sections[2]}. " +
                "The world format may have changed; no data was changed.");
        }

        return new LoadedWorld(metadata, tiles);
    }

    private static WorldMetadata ReadHeader(BinaryReader reader, uint version, int expectedEnd)
    {
        string title = reader.ReadString();
        string seed = version == 179 ? reader.ReadInt32().ToString() : reader.ReadString();

        _ = reader.ReadUInt64(); // world-generator version
        _ = ReadExact(reader, 16); // world GUID
        int worldId = reader.ReadInt32();
        Skip(reader, 16); // pixel-space world bounds

        // The file stores height before width.
        int height = reader.ReadInt32();
        int width = reader.ReadInt32();
        if (width is < 100 or > 20_000 || height is < 100 or > 5_000)
        {
            throw new InvalidDataException($"Invalid world dimensions: {width}x{height}.");
        }

        _ = reader.ReadInt32(); // game mode
        bool drunkWorld = reader.ReadBoolean();
        bool getGoodWorld = reader.ReadBoolean();
        _ = reader.ReadBoolean(); // tenth anniversary
        _ = reader.ReadBoolean(); // don't starve
        _ = reader.ReadBoolean(); // not the bees
        bool remixWorld = reader.ReadBoolean();
        bool noTraps = reader.ReadBoolean();
        bool zenithWorld = reader.ReadBoolean();
        bool skyblockWorld = version >= 302 && reader.ReadBoolean();

        Skip(reader, 8); // creation time
        if (version >= 284)
        {
            Skip(reader, 8); // last played
        }

        Skip(reader, 1); // moon type
        Skip(reader, 3 * 4); // tree X
        Skip(reader, 4 * 4); // tree styles
        Skip(reader, 3 * 4); // cave background X
        Skip(reader, 4 * 4); // cave background styles
        Skip(reader, 3 * 4); // ice, jungle and underworld styles
        int spawnX = reader.ReadInt32();
        int spawnY = reader.ReadInt32();
        double worldSurface = reader.ReadDouble();
        double rockLayer = reader.ReadDouble();
        Skip(reader, 8); // time
        if (!double.IsFinite(worldSurface) || !double.IsFinite(rockLayer) ||
            worldSurface <= 0 || rockLayer <= worldSurface || rockLayer >= height)
        {
            throw new InvalidDataException(
                $"Invalid world depth layers: surface={worldSurface}, rock={rockLayer}, height={height}.");
        }

        Skip(reader, 1 + 4 + 1 + 1); // daytime, moon phase, blood moon, eclipse
        int dungeonX = reader.ReadInt32();
        _ = reader.ReadInt32(); // dungeon Y
        bool isCrimson = reader.ReadBoolean();

        // Ten boss-progression flags through Golem, then King Slime from 1.4 on. The exact bit
        // order inside those ten bytes is not needed here: the only tile it gates (Lihzahrd
        // brick, 226) is not an infection node, so it can never be part of a fence.
        Skip(reader, 10);
        if (version >= 118)
        {
            Skip(reader, 1); // King Slime
        }

        Skip(reader, 7); // rescued NPC and invasion flags
        Skip(reader, 2); // shadow orb and meteor flags
        Skip(reader, 1); // shadow orb count
        Skip(reader, 4); // altar count
        bool hardMode = reader.ReadBoolean();

        if (version >= 257)
        {
            Skip(reader, 1); // Party of Doom
        }

        Skip(reader, 3 * 4 + 8); // invasion state
        if (version >= 118)
        {
            Skip(reader, 8); // slime rain
        }

        if (version >= 113)
        {
            Skip(reader, 1); // sundial cooldown
        }

        Skip(reader, 1 + 4 + 4); // rain
        Skip(reader, 3 * 4); // hardmode ore tiers
        Skip(reader, 8); // biome backgrounds
        Skip(reader, 4 + 2 + 4); // cloud and wind state

        int anglerCount = ReadCount(reader.ReadInt32(), 10_000, "angler completion list");
        for (int i = 0; i < anglerCount; i++)
        {
            _ = reader.ReadString();
        }

        if (version >= 99) Skip(reader, 1);
        if (version >= 101) Skip(reader, 4);
        if (version >= 104) Skip(reader, 1);
        if (version >= 129) Skip(reader, 1);
        if (version >= 201) Skip(reader, 1);
        if (version >= 107) Skip(reader, 4);
        if (version >= 108) Skip(reader, 4);

        int killedMobCount = ReadCount(reader.ReadInt16(), 65_535, "banner kill list");
        Skip(reader, killedMobCount * 4L);
        if (version >= 289)
        {
            int claimableBannerCount = ReadCount(reader.ReadInt16(), 65_535, "claimable banner list");
            Skip(reader, claimableBannerCount * 2L);
        }

        if (version >= 128) Skip(reader, 1);
        if (version >= 131) Skip(reader, 9);
        if (version >= 140) Skip(reader, 9);

        if (version >= 170)
        {
            Skip(reader, 2 + 4);
            int partyNpcCount = ReadCount(reader.ReadInt32(), 10_000, "party NPC list");
            Skip(reader, partyNpcCount * 4L);
        }

        if (version >= 174) Skip(reader, 1 + 4 + 4 + 4);
        if (version >= 178) Skip(reader, 4);
        if (version > 194) Skip(reader, 1);
        if (version >= 215) Skip(reader, 1);
        if (version > 195) Skip(reader, 3);
        if (version >= 204) Skip(reader, 1);
        if (version >= 207) Skip(reader, 4 + 3);
        if (version >= 211)
        {
            int treeTopCount = ReadCount(reader.ReadInt32(), 1_000, "tree-top style list");
            Skip(reader, treeTopCount * 4L);
        }

        if (version >= 212) Skip(reader, 2);
        if (version >= 216) Skip(reader, 4 * 4);
        if (version >= 217) Skip(reader, 3);
        if (version >= 223) Skip(reader, 2);
        if (version >= 240) Skip(reader, 1);
        if (version >= 250) Skip(reader, 1);
        if (version >= 251) Skip(reader, 8);
        if (version >= 259) Skip(reader, 1);
        if (version >= 260) Skip(reader, 1);
        if (version >= 261) Skip(reader, 7);
        if (version >= 264) Skip(reader, 2);
        if (version >= 287) Skip(reader, 2);
        if (version >= 288) Skip(reader, 1);
        if (version >= 296) Skip(reader, 1);
        if (version >= 291) Skip(reader, 8);
        if (version >= 297)
        {
            Skip(reader, 1); // team-based spawns flag
            int teamSpawnCount = reader.ReadByte();
            Skip(reader, teamSpawnCount * 4L);
        }

        bool dualDungeonsWorld = version >= 304 && reader.ReadBoolean();
        if (version >= 323) Skip(reader, 2); // lightning seed flags
        if (version is >= 299 and < 313) Skip(reader, 4); // removed manifest checksum
        if (version >= 299) _ = reader.ReadString(); // world manifest

        if (reader.BaseStream.Position > expectedEnd)
        {
            throw new InvalidDataException(
                $"World header overran its section at {reader.BaseStream.Position}; expected at most {expectedEnd}.");
        }

        // New fields may be appended to a known version without changing the tile encoding, so
        // seeking to the declared section end is safer than guessing at the remaining bytes.
        reader.BaseStream.Position = expectedEnd;

        _ = dualDungeonsWorld;
        return new WorldMetadata(
            version,
            title,
            seed,
            worldId,
            width,
            height,
            spawnX,
            spawnY,
            worldSurface,
            rockLayer,
            dungeonX,
            isCrimson,
            hardMode,
            drunkWorld,
            getGoodWorld,
            remixWorld,
            skyblockWorld,
            noTraps,
            zenithWorld);
    }

    private static TileGrid ReadTiles(BinaryReader reader, WorldMetadata metadata, uint version, bool[] frameImportant)
    {
        int width = metadata.Width;
        int height = metadata.Height;
        TileGrid tiles = new(width, height);

        for (int x = 0; x < width; x++)
        {
            int y = 0;
            while (y < height)
            {
                TileRun tile = ReadTileRun(reader, version, frameImportant);
                int maxY = checked(y + tile.Repetitions);
                if (maxY >= height)
                {
                    throw new InvalidDataException($"Tile RLE exceeded column {x} at row {y}.");
                }

                if (tile.Active || tile.HasLiquid)
                {
                    for (int row = y; row <= maxY; row++)
                    {
                        tiles.SetTile(
                            (row * width) + x,
                            tile.Active ? tile.Type : (ushort)0,
                            tile.Active,
                            tile.Actuated,
                            tile.Slope,
                            tile.Liquid,
                            tile.LiquidAmount);
                    }
                }

                y = maxY + 1;
            }
        }

        return tiles;
    }

    private static TileRun ReadTileRun(BinaryReader reader, uint version, bool[] frameImportant)
    {
        byte header1 = reader.ReadByte();
        byte header2 = 0;
        byte header3 = 0;

        if ((header1 & 0x01) != 0)
        {
            header2 = reader.ReadByte();
            if ((header2 & 0x01) != 0)
            {
                header3 = reader.ReadByte();
                if (version >= 269 && (header3 & 0x01) != 0)
                {
                    _ = reader.ReadByte(); // coating/invisibility header; it carries no payload bytes
                }
            }
        }

        bool active = (header1 & 0x02) != 0;
        ushort type = 0;
        if (active)
        {
            type = (header1 & 0x20) == 0
                ? reader.ReadByte()
                : reader.ReadUInt16();
            bool framed = type >= frameImportant.Length || frameImportant[type];
            if (framed)
            {
                Skip(reader, 4); // frame X and frame Y
            }

            if ((header3 & 0x08) != 0)
            {
                Skip(reader, 1); // tile paint
            }
        }

        if ((header1 & 0x04) != 0)
        {
            Skip(reader, 1); // low wall byte
            if ((header3 & 0x10) != 0)
            {
                Skip(reader, 1); // wall paint
            }
        }

        LiquidKind liquid = LiquidKind.None;
        byte liquidAmount = 0;
        if ((header1 & 0x18) != 0)
        {
            liquid = (LiquidKind)((header1 & 0x18) >> 3);
            liquidAmount = reader.ReadByte();
        }

        if (version >= 222 && (header3 & 0x40) != 0)
        {
            Skip(reader, 1); // high wall byte
        }

        int repetitions = (header1 >> 6) switch
        {
            0 => 0,
            1 => reader.ReadByte(),
            _ => reader.ReadInt16(),
        };
        if (repetitions < 0)
        {
            throw new InvalidDataException("Negative tile RLE count.");
        }

        bool actuated = (header2 & 0x02) != 0;
        int slope = (header2 >> 4) & 0x07;
        return new TileRun(active, type, repetitions, actuated, slope, liquid, liquidAmount);
    }

    private static bool[] ReadPackedBooleans(BinaryReader reader)
    {
        short bitCount = reader.ReadInt16();
        if (bitCount <= 0)
        {
            throw new InvalidDataException($"Invalid tile-frame bit count: {bitCount}.");
        }

        bool[] values = new bool[bitCount];
        for (int i = 0; i < bitCount; i += 8)
        {
            byte packed = reader.ReadByte();
            int count = Math.Min(8, bitCount - i);
            for (int bit = 0; bit < count; bit++)
            {
                values[i + bit] = (packed & (1 << bit)) != 0;
            }
        }

        return values;
    }

    private static void ValidateSections(int[] sections, long fileLength)
    {
        int previous = 0;
        for (int i = 0; i < sections.Length; i++)
        {
            int current = sections[i];
            if (current <= previous || current > fileLength)
            {
                throw new InvalidDataException($"Invalid section pointer {i}: {current}.");
            }

            previous = current;
        }
    }

    private static int ReadCount(int value, int maximum, string description)
    {
        if (value < 0 || value > maximum)
        {
            throw new InvalidDataException($"Invalid {description} count: {value}.");
        }

        return value;
    }

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        byte[] data = reader.ReadBytes(count);
        if (data.Length != count)
        {
            throw new EndOfStreamException();
        }

        return data;
    }

    private static void Skip(BinaryReader reader, long count)
    {
        long position = checked(reader.BaseStream.Position + count);
        if (count < 0 || position > reader.BaseStream.Length)
        {
            throw new EndOfStreamException();
        }

        reader.BaseStream.Position = position;
    }

    private readonly record struct TileRun(
        bool Active,
        ushort Type,
        int Repetitions,
        bool Actuated,
        int Slope,
        LiquidKind Liquid,
        byte LiquidAmount)
    {
        public bool HasLiquid => LiquidAmount > 0 && Liquid != LiquidKind.None;
    }
}
