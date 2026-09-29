namespace ZhaDai.Core.World;

/// <summary>How a tile on the fence line can be got rid of.</summary>
public enum RemovalMethod
{
    /// <summary>Nothing to do: the tile is already gone or was never a node.</summary>
    None,

    /// <summary>Dynamite takes it out; this is what the charge list is for.</summary>
    Blast,

    /// <summary>Dynamite leaves it standing, so the pickaxe has to do it.</summary>
    Dig,

    /// <summary>Neither tool can remove it with the pick power assumed; needs a detour or better gear.</summary>
    Blocked,
}

/// <summary>
/// What each tool can actually do to a tile, and what must be left alone.
///
/// Two things are easy to get wrong and both matter here:
///
/// 1. **Dynamite is not the only tool.** Blast-immune does not mean unremovable: hardmode ores, dungeon
///    brick, chests, Lihzahrd brick and chlorophyte all survive a blast, and most of them come out with
///    a good enough pickaxe. A planner that only knows about dynamite either gives up on those tiles or
///    tries to blast them forever.
/// 2. **Some tiles come out with neither.** Chlorophyte needs 200% pick power, Lihzahrd brick 210%, and
///    a Demon Altar is not a mining target at all. Those are reported instead of quietly counted as
///    sealed.
///
/// The pick power gates below are transcribed from <c>Player.GetPickaxeDamage</c>
/// (Player.cs:54570-54648) and <c>WorldGen.CanKillTile</c> (WorldGen.cs:62724-62831); the soft and
/// frame-important tables are generated from the <c>Main</c> initialiser itself, not hand written.
/// </summary>
public static class TileCatalog
{
    /// <summary>Highest gate any tile asks for: Lihzahrd brick at 210, which is the Picksaw.</summary>
    public const int StrongestPickPower = 210;

    /// <summary>Pick power a fresh character starts with (copper pickaxe).</summary>
    public const int StartingPickPower = 35;

    /// <summary>Pick power that clears the whole pre-hardmode fence: Molten Pickaxe.</summary>
    public const int PreHardmodePickPower = 100;

    /// <summary>
    /// <c>Main.tileNoFail</c> (Main.cs): these break in one hit whatever the pickaxe, so they cost no
    /// mining time at all. Generated from the source, 68 entries.
    /// </summary>
    private static readonly ushort[] SoftTiles =
    [
        3, 4, 24, 32, 50, 51, 52, 61, 62, 69, 73, 74, 81, 82,
        83, 84, 110, 113, 115, 129, 162, 165, 184, 185, 186, 187, 192, 201,
        205, 227, 233, 254, 324, 330, 331, 332, 333, 352, 373, 374, 375, 382,
        384, 461, 481, 482, 483, 484, 485, 518, 519, 528, 529, 530, 549, 624,
        636, 637, 638, 654, 655, 656, 666, 697, 700, 701, 705, 709,
    ];

    /// <summary>
    /// <c>Main.tileFrameImportant</c> (Main.cs): tiles that are not plain blocks — chests, doors,
    /// furniture, platforms, signs, statues, sunflowers, altars. Destroying one of these is destroying
    /// something a player built or wants, so they are protected by default. Generated from the source,
    /// 398 entries.
    /// </summary>
    private static readonly ushort[] FrameImportantTiles =
    [
        3, 4, 5, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20,
        21, 24, 26, 27, 28, 29, 31, 33, 34, 35, 36, 42, 49, 50,
        55, 61, 71, 72, 73, 74, 77, 78, 79, 81, 82, 83, 84, 85,
        86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99,
        100, 101, 102, 103, 104, 105, 106, 110, 113, 114, 125, 126, 128, 129,
        132, 133, 134, 135, 136, 137, 138, 139, 141, 142, 143, 144, 149, 165,
        171, 172, 173, 174, 178, 184, 185, 186, 187, 201, 207, 209, 210, 212,
        215, 216, 217, 218, 219, 220, 227, 228, 231, 233, 235, 236, 237, 238,
        239, 240, 241, 242, 243, 244, 245, 246, 247, 254, 269, 270, 271, 275,
        276, 277, 278, 279, 280, 281, 282, 283, 285, 286, 287, 288, 289, 290,
        291, 292, 293, 294, 295, 296, 297, 298, 299, 300, 301, 302, 303, 304,
        305, 306, 307, 308, 309, 310, 314, 316, 317, 318, 319, 320, 323, 324,
        334, 335, 337, 338, 339, 349, 354, 355, 356, 358, 359, 360, 361, 362,
        363, 364, 372, 373, 374, 375, 376, 377, 378, 380, 386, 387, 388, 389,
        390, 391, 392, 393, 394, 395, 405, 406, 410, 411, 412, 413, 414, 419,
        420, 423, 424, 425, 427, 428, 429, 440, 441, 442, 443, 444, 445, 452,
        453, 454, 455, 456, 457, 461, 462, 463, 464, 465, 466, 467, 468, 469,
        470, 471, 475, 476, 480, 484, 485, 486, 487, 488, 489, 490, 491, 493,
        494, 497, 499, 505, 506, 509, 510, 511, 518, 519, 520, 521, 522, 523,
        524, 525, 526, 527, 529, 530, 531, 532, 533, 538, 542, 543, 544, 545,
        547, 548, 549, 550, 551, 552, 553, 554, 555, 556, 558, 559, 560, 564,
        565, 567, 568, 569, 570, 571, 572, 573, 579, 580, 581, 582, 583, 584,
        585, 586, 587, 588, 589, 590, 591, 592, 593, 594, 595, 596, 597, 598,
        599, 600, 601, 602, 603, 604, 605, 606, 607, 608, 609, 610, 611, 612,
        613, 614, 615, 616, 617, 619, 620, 621, 622, 623, 624, 629, 630, 631,
        632, 634, 637, 639, 640, 642, 643, 644, 645, 646, 653, 654, 656, 657,
        658, 660, 663, 664, 665, 695, 696, 698, 699, 700, 701, 702, 703, 704,
        705, 707, 709, 710, 711, 712, 713, 714, 715, 716, 720, 721, 723, 724,
        725, 726, 733, 751, 752, 753,
    ];

    /// <summary>
    /// Plants, thorns and vines that are themselves infection vectors: they are frame-important or soft,
    /// but they must still be removable, because they carry the infection across a gap by themselves.
    /// </summary>
    private static readonly ushort[] InfectionVectors =
    [
        24, 32, 52, 69, 110, 113, 115, 201, 205, 352, 636, 655,
    ];

    /// <summary>
    /// Natural terrain that is not convertible and therefore never a fence node, but is still fine to
    /// blast: clay, mud, cactus, silt, snow, ice, slush, clouds, granite, marble. Ids resolved from
    /// <c>TileID</c> by name rather than guessed.
    /// </summary>
    private static readonly ushort[] NaturalNonConvertible =
    [
        40, 59, 80, 123, 147, 161, 189, 196, 224, 367, 368,
    ];

    /// <summary>
    /// Building materials a player crafts: planks, bricks, glass, slime and bone blocks. Blasting these
    /// means blasting something somebody built, so under the default protection level the planner digs
    /// those fence tiles instead of blowing them up. Kept to materials that are crafted rather than
    /// generated, because a false positive costs a slower plan while a false negative costs a hole in
    /// somebody's house.
    /// </summary>
    private static readonly ushort[] CraftedBuildingBlocks =
    [
        30, 38, 39, 54, 148, 151, 157, 158, 159, 193, 194, 208, 229, 253, 311, 321, 322, 472,
    ];

    private static readonly HashSet<ushort> SoftSet = [.. SoftTiles];

    private static readonly HashSet<ushort> FrameImportantSet = [.. FrameImportantTiles];

    private static readonly HashSet<ushort> InfectionVectorSet = [.. InfectionVectors];

    private static readonly HashSet<ushort> NaturalSet = [.. NaturalNonConvertible];

    private static readonly HashSet<ushort> CraftedSet = [.. CraftedBuildingBlocks];

    /// <summary>Breaks in one hit with any pickaxe.</summary>
    public static bool IsSoft(ushort type) => SoftSet.Contains(type);

    /// <summary>
    /// A player-visible structure: furniture, container, door, platform, sign, altar and so on.
    /// Infection vectors are excluded because thorns and vines have to go for the seal to hold, and soft
    /// tiles are excluded because <c>tileNoFail</c> covers the plants, cobwebs and torches that grow all
    /// over a world — treating every cave plant as somebody's building would turn the whole plan into
    /// pickaxe work for no reason.
    /// </summary>
    public static bool IsProtectedStructure(ushort type) =>
        FrameImportantSet.Contains(type) && !SoftSet.Contains(type) && !InfectionVectorSet.Contains(type);

    /// <summary>Thorns, vines and infected plants: frame-important or not, these must be removed.</summary>
    public static bool IsInfectionVector(ushort type) => InfectionVectorSet.Contains(type);

    /// <summary>
    /// Terrain that occurs naturally and is safe to blow up: convertible terrain plus the natural
    /// non-convertible materials.
    /// </summary>
    public static bool IsNaturalTerrain(ushort type) =>
        type == 0 || TileIds.IsConvertible(type) || TileIds.IsEvil(type) || TileIds.IsHallow(type) ||
        NaturalSet.Contains(type) || InfectionVectorSet.Contains(type);

    /// <summary>
    /// A crafted building material: planks, bricks, glass, slime or bone block. Vanilla does not record
    /// who placed a tile, so this is a material list rather than a real provenance check — it catches the
    /// wood-and-brick houses players actually build, and unknown ids stay blastable so the plan does not
    /// grind to a halt on terrain the list has never heard of.
    /// </summary>
    public static bool IsPlayerBuilt(ushort type) => CraftedSet.Contains(type);

    /// <summary>
    /// Minimum pick power that can damage the tile at all, from the gate chain in
    /// <c>GetPickaxeDamage</c>. Zero means any pickaxe works. <see cref="int.MaxValue"/> means no
    /// pickaxe ever will (a Demon Altar is not a mining target).
    /// </summary>
    public static int MinPickPower(ushort type, bool belowSurface = true, bool inOuterBands = true)
    {
        if (type == 0 || IsSoft(type))
        {
            return 0;
        }

        // 26 is the Demon Altar: a pickaxe never removes it, only a Pwnhammer in hardmode does.
        if (type == 26)
        {
            return int.MaxValue;
        }

        if (type == 211)
        {
            return 200;
        }

        if (type is 226 or 237)
        {
            return 210;
        }

        if (type is 111 or 223)
        {
            return 150;
        }

        if (type is 108 or 222)
        {
            return 110;
        }

        if (type is 107 or 221)
        {
            return 100;
        }

        if (TileIds.IsDungeonBrick(type) && belowSurface && inOuterBands)
        {
            return 100;
        }

        if (type is 25 or 203 or 117)
        {
            return 65;
        }

        if (type == 58 || type == 77)
        {
            return 65;
        }

        if (type is 22 or 204 or 56)
        {
            return 55;
        }

        if (type == 37)
        {
            return 50;
        }

        return 0;
    }

    /// <summary>
    /// Hits a pickaxe of the given power needs to break the tile, mirroring the damage arithmetic in
    /// <c>GetPickaxeDamage</c> (which returns damage per swing against a threshold of 100).
    /// Zero means it can never be broken, which is the answer that matters.
    /// </summary>
    public static int DigHits(ushort type, int pickPower, bool belowSurface = true, bool inOuterBands = true)
    {
        if (type == 0)
        {
            return 0;
        }

        if (pickPower < MinPickPower(type, belowSurface, inOuterBands))
        {
            return 0;
        }

        if (IsSoft(type))
        {
            return 1;
        }

        int damage;
        if (TileIds.IsDungeonBrick(type) || type is 58 or 25 or 117 or 203)
        {
            damage = pickPower / 2;
        }
        else if (type is 48 or 232)
        {
            damage = pickPower * 2;
        }
        else if (type == 226)
        {
            damage = pickPower / 4;
        }
        else if (type is 107 or 221)
        {
            damage = pickPower / 2;
        }
        else if (type is 108 or 222)
        {
            damage = pickPower / 3;
        }
        else if (type is 111 or 223)
        {
            damage = pickPower / 4;
        }
        else if (type == 211)
        {
            damage = pickPower / 5;
        }
        else
        {
            damage = pickPower;
        }

        if (type is 147 or 0 or 40 or 53 or 57 or 59 or 123 or 224 or 397)
        {
            damage += pickPower;
        }

        if (damage <= 0)
        {
            return 0;
        }

        return Math.Max(1, (100 + damage - 1) / damage);
    }

    /// <summary>
    /// The one question the planner asks: can this fence tile be removed at all, and with which tool?
    /// </summary>
    public static RemovalMethod Classify(
        ushort type,
        bool hardMode,
        bool downedGolemBoss,
        bool getGoodWorld,
        int pickPower)
    {
        if (type == 0)
        {
            return RemovalMethod.None;
        }

        bool blastable = !TileIds.IsDynamiteImmune(type, hardMode, downedGolemBoss, getGoodWorld);
        bool diggable = DigHits(type, pickPower) > 0;

        if (blastable)
        {
            // Blasting is the default because swing time is the scarce resource, but a tile that both
            // tools can handle is still worth knowing about: it can be dug instead when a charge would
            // damage something nearby.
            return RemovalMethod.Blast;
        }

        return diggable ? RemovalMethod.Dig : RemovalMethod.Blocked;
    }

    /// <summary>
    /// Runs every tile type through <see cref="Classify"/> so the test suite can assert the shape of the
    /// table rather than trusting a handful of hand-picked examples.
    /// </summary>
    public static IEnumerable<ushort> AllKnownTypes() => SoftSet
        .Concat(FrameImportantSet)
        .Concat(InfectionVectorSet)
        .Concat(NaturalSet)
        .Concat(TileIds.AllInfectionNodeTypes())
        .Distinct()
        .OrderBy(type => type);
}
