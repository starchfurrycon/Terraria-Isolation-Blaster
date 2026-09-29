namespace ZhaDai.Core.World;

/// <summary>
/// Vanilla tile classification used by the planner. Every number here is a literal from
/// Terraria 1.4.5.8 and is cited to the decompiled member that defines it, because a wrong id
/// silently produces a fence with a hole in it.
/// </summary>
/// <remarks>
/// The infection sets and the hardmode conversion set are ported verbatim from the
/// Terraria-Biome-Containment-Analyzer so that this tool's containment verdict cannot drift away
/// from the analyzer's. See <c>docs/research/analyzer-core-report.md</c>.
/// </remarks>
public static class TileIds
{
    // --- Terrain ---------------------------------------------------------------
    public const ushort Dirt = 0;
    public const ushort Stone = 1;
    public const ushort Grass = 2;
    public const ushort Plants = 3;
    public const ushort CorruptGrass = 23;
    public const ushort Ebonstone = 25;
    public const ushort CorruptThorns = 32;
    public const ushort Vines = 52;
    public const ushort Sand = 53;
    public const ushort Ash = 57;
    public const ushort Mud = 59;
    public const ushort JungleGrass = 60;
    public const ushort MushroomGrass = 70;
    public const ushort HallowedGrass = 109;
    public const ushort Ebonsand = 112;
    public const ushort Pearlsand = 116;
    public const ushort Pearlstone = 117;
    public const ushort CrimsonGrass = 199;
    public const ushort Crimstone = 203;
    public const ushort Slush = 224;
    public const ushort Crimsand = 234;
    public const ushort CrimsonThorns = 352;
    public const ushort Sandstone = 396;
    public const ushort HardenedSand = 397;

    // --- Hazards ---------------------------------------------------------------
    /// <summary><c>TileID.Traps = 137</c> covers the five Lihzahrd traps; the frame picks which.</summary>
    public const ushort Traps = 137;

    public const ushort Spikes = 48;
    public const ushort LandMine = 210;
    public const ushort Boulder = 138;
    public const ushort Explosives = 141;
    public const ushort GeyserTrap = 443;
    public const ushort TntBarrel = 654;

    /// <summary><c>TileID.Tombstones = 85</c>; every grave variant shares this id and differs by frame.</summary>
    public const ushort Tombstones = 85;

    // --- Blast immunity --------------------------------------------------------
    public const ushort Meteorite = 37;
    public const ushort Hellstone = 58;
    public const ushort LihzahrdBrick = 226;

    /// <summary>
    /// Exact infection set from the analyzer: corruption, crimson and their stone/sand/plant forms.
    /// </summary>
    private static readonly ushort[] EvilTiles =
    [
        23, 24, 25, 32, 112, 163, 199, 200, 201, 203, 205, 234,
        352, 398, 399, 400, 401, 636, 661, 662,
    ];

    /// <summary>Exact hallow infection set from the analyzer.</summary>
    private static readonly ushort[] HallowTiles =
    [
        109, 110, 113, 115, 116, 117, 164, 402, 403, 492,
    ];

    private static readonly ushort[] ConvertibleTiles =
    [
        1, 2, 23, 25, 53, 60, 109, 112, 116, 117, 123, 161, 163, 164, 199, 200,
        203, 225, 230, 234, 396, 397, 661, 662,
    ];

    /// <summary>
    /// Tiles that can host a grass plant, thorn or downward vine, i.e. the tiles that can bridge a
    /// gap the ordinary three-tile reach could not cross. From the analyzer.
    /// </summary>
    private static readonly ushort[] PlantSupportTiles = [2, 23, 60, 109, 199, 661, 662];

    /// <summary><c>Main.tileDungeon</c> is set for exactly these types (Main.cs:8173-8178).</summary>
    private static readonly ushort[] DungeonBrickTiles = [41, 43, 44, 677, 678, 679];

    /// <summary>
    /// Types that <c>Projectile.CanExplodeTile</c> (Projectile.cs:80399-80418) rejects
    /// unconditionally, plus the dungeon bricks and chest-like tiles rejected just above it.
    /// </summary>
    private static readonly ushort[] UnconditionallyBlastImmune =
    [
        26, 88, 121, 122, 150, 211, 226, 237, 248, 249, 250, 346, 470, 475, 504, 685, 686,
    ];

    /// <summary>Hardmode ores: <c>CanExplodeTile</c> returns <c>explodeHardmodeOres</c>, false for Dynamite.</summary>
    private static readonly ushort[] HardmodeOreTiles = [107, 108, 111, 221, 222, 223];

    private static readonly HashSet<ushort> EvilSet = [.. EvilTiles];

    private static readonly HashSet<ushort> HallowSet = [.. HallowTiles];

    private static readonly HashSet<ushort> ConvertibleSet = [.. ConvertibleTiles];

    private static readonly HashSet<ushort> PlantSupportSet = [.. PlantSupportTiles];

    private static readonly HashSet<ushort> DungeonBrickSet = [.. DungeonBrickTiles];

    private static readonly HashSet<ushort> UnconditionallyImmuneSet = [.. UnconditionallyBlastImmune];

    private static readonly HashSet<ushort> HardmodeOreSet = [.. HardmodeOreTiles];

    public static bool IsEvil(ushort type) => EvilSet.Contains(type);

    public static bool IsHallow(ushort type) => HallowSet.Contains(type);

    public static bool IsConvertible(ushort type) => ConvertibleSet.Contains(type);

    /// <summary>
    /// True when a tile participates in the infection spread graph: it either already carries an
    /// infection or vanilla can convert it into one.
    /// </summary>
    public static bool IsInfectionNode(ushort type) =>
        type != 0 && (ConvertibleSet.Contains(type) || EvilSet.Contains(type) || HallowSet.Contains(type));

    public static bool SupportsPlantGrowth(ushort type) => PlantSupportSet.Contains(type);

    public static bool IsDungeonBrick(ushort type) => DungeonBrickSet.Contains(type);

    public static bool IsTrap(ushort type) =>
        type is Traps or Spikes or LandMine or Boulder or GeyserTrap or TntBarrel;

    public static bool IsGravestone(ushort type) => type == Tombstones;

    /// <summary>
    /// Whether a Dynamite blast (projectile 29, radius 7) leaves this tile standing.
    /// </summary>
    /// <param name="hardMode">The world's hardmode flag; gates meteorite and hellstone.</param>
    /// <param name="downedGolemBoss">Gates Lihzahrd brick.</param>
    /// <param name="getGoodWorld">Gates spikes and (in-game) some other tiles.</param>
    public static bool IsDynamiteImmune(ushort type, bool hardMode, bool downedGolemBoss, bool getGoodWorld)
    {
        if (type == 0)
        {
            return false;
        }

        if (UnconditionallyImmuneSet.Contains(type) || DungeonBrickSet.Contains(type))
        {
            return true;
        }

        // Dynamite never passes explodeHardmodeOres, so ores always survive.
        if (HardmodeOreSet.Contains(type))
        {
            return true;
        }

        return type switch
        {
            Meteorite or Hellstone => !hardMode,
            // 77 is hellstone-adjacent in the Underworld; without hardmode it survives down there.
            77 => !hardMode,
            Spikes or 232 => getGoodWorld,
            LihzahrdBrick => !downedGolemBoss,
            _ => false,
        };
    }

    /// <summary>Every tile type that can take part in the spread graph, for exhaustive tests.</summary>
    public static IEnumerable<ushort> AllInfectionNodeTypes() =>
        ConvertibleTiles.Concat(EvilTiles).Concat(HallowTiles).Distinct();
}
