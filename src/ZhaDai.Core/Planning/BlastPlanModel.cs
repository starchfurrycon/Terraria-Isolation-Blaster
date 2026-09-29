using ZhaDai.Core.Analysis;
using ZhaDai.Core.World;

namespace ZhaDai.Core.Planning;

/// <summary>Tuning knobs for the planner. Defaults are the conservative choices.</summary>
public sealed record BlastPlanOptions
{
    /// <summary>
    /// Chebyshev thickness of the cleared band around an infection front, in tiles. Six matches the
    /// documented vanilla rule that a six-tile empty gap blocks ordinary spread, and it also covers
    /// the four-tile plant/thorn bridge. The planner refuses anything below
    /// <see cref="MinimumClearance"/> because a thinner band would be reachable by the ordinary
    /// three-tile spread.
    /// </summary>
    public int Clearance { get; init; } = 6;

    public const int MinimumClearance = InfectionModel.SpreadReach + 1;

    /// <summary>
    /// Distance at which two infection fronts share one fence. The default of three merges only
    /// fronts that the ordinary spread could join anyway, which keeps the isolated range as small
    /// as possible; raising it saves charges at the cost of enclosing more of the world.
    /// </summary>
    public int MergeLinkDistance { get; init; } = InfectionModel.SpreadReach;

    /// <summary>Dynamite blast radius in tiles: <c>Projectile.Kill_ExplodeTiles</c> uses 7 for projectile 29.</summary>
    public int BlastRadius { get; init; } = 7;

    /// <summary>
    /// Ticks between throwing Dynamite and its detonation. Confirmed as 300 (5 s): the value is not
    /// in <c>Projectile.SetDefaults</c> but overwritten in <c>Projectile.NewProjectile</c> for
    /// projectile 29, and <c>AI_016_Bombs</c> kills it once <c>timeLeft &lt;= 3</c>.
    /// </summary>
    public int DynamiteFuseTicks { get; init; } = 300;

    /// <summary>
    /// Extra tiles beyond the blast radius the player must clear before a charge detonates. Dynamite
    /// self damage is 250 inside a 250x250 px box (a square of +-125 px, about 7.8 tiles, unrelated to
    /// the round tile radius), so the default 7 + 3 = 10 tile retreat clears both the box and the
    /// player's own hitbox.
    /// </summary>
    public int RetreatMarginTiles { get; init; } = 3;

    /// <summary>
    /// How far a corrupt vine can carry the infection down its own column. The vector is real in
    /// vanilla (<c>WorldGen.CheckVines</c> retypes a vine to match the tile above, so grass that is
    /// corrupt grows <c>CorruptVines</c> 636, which is itself a spread source), but the depth is a
    /// growth process rather than a constant. Zero turns the vine path off and produces the cheapest
    /// band that is still sealed against the ordinary three tile spread.
    /// </summary>
    public int VineReach { get; init; } = InfectionModel.VineDownwardReach;

    /// <summary>Extra rectangles to treat as infection, e.g. the analyzer's predicted pre-hardmode V bands.</summary>
    public IReadOnlyList<TileRect> ExtraSeedRects { get; init; } = [];

    /// <summary>
    /// How much collateral damage a charge is allowed to do. Dynamite is indiscriminate: it removes
    /// everything in a seven tile disc, not just the fence tiles it was aimed at, so a placement that
    /// seals better by flattening somebody's house has to be refused on purpose.
    /// </summary>
    public ProtectionLevel Protection { get; init; } = ProtectionLevel.Strict;

    /// <summary>
    /// Replace the vine curtains with a single inert block under each plant that can grow one. One block
    /// beats blasting a column as deep as a vine can reach, but it only works if the player has the blocks
    /// and is willing to walk them in, so it can be turned off.
    /// </summary>
    public bool PlugVines { get; init; } = true;

    /// <summary>Item id used as the plug. Wood is the cheapest thing everybody has by the hundred.</summary>
    public int PlugItemId { get; init; } = 9;

    /// <summary>
    /// Extra tiles of margin around anything protected. One tile keeps a blast from clipping the block
    /// next to a chest, which is enough to stop the contents being thrown around; zero restores the old
    /// tight fit. Raising it past three buys little and costs a lot of charges, so it is capped there.
    /// </summary>
    public int ProtectionBuffer { get; init; } = 1;

    /// <summary>
    /// Pick power the plan assumes when deciding whether a tile that survives dynamite can be dug out
    /// instead. The default of 100 is the Molten Pickaxe, the best a pre-hardmode character can have;
    /// raise it to 210 (Picksaw) to plan for the endgame, lower it to 65 (Nightmare Pickaxe) for a run
    /// that intends to fight the wall of flesh with a mid-game tool.
    /// </summary>
    public int PickPower { get; init; } = TileCatalog.PreHardmodePickPower;

    /// <summary>Refuse to emit a plan with more separate fences than this; a sign the world is far from contained.</summary>
    public int MaxSections { get; init; } = 4000;

    public void Validate()
    {
        if (Clearance < MinimumClearance)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Clearance),
                Clearance,
                $"Clearance must be at least {MinimumClearance}: a thinner band is reachable by the ordinary " +
                $"{InfectionModel.SpreadReach}-tile spread.");
        }

        if (ProtectionBuffer < 0 || ProtectionBuffer > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProtectionBuffer),
                ProtectionBuffer,
                "Protection buffer must be between 0 and 3 tiles.");
        }

        if (BlastRadius < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(BlastRadius), BlastRadius, "Blast radius must be at least 2.");
        }

        if (MergeLinkDistance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MergeLinkDistance), MergeLinkDistance, "Merge distance cannot be negative.");
        }

        if (MaxSections < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSections), MaxSections, "MaxSections must be positive.");
        }

        if (VineReach < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(VineReach), VineReach, "VineReach cannot be negative.");
        }

        if (PickPower < 1 || PickPower > TileCatalog.StrongestPickPower)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PickPower),
                PickPower,
                $"Pick power must be between 1 and {TileCatalog.StrongestPickPower} (the Picksaw, the strongest " +
                "gate any tile asks for).");
        }
    }
}

/// <summary>How much of the world a charge is allowed to break on its way to the fence.</summary>
public enum ProtectionLevel
{
    /// <summary>Never blast a structure or a crafted building material: dig those fence tiles instead.</summary>
    Strict,

    /// <summary>Protect chests, doors, furniture and platforms, but plain crafted blocks may be blasted.</summary>
    Structures,

    /// <summary>No protection: place charges for the lowest charge count and report what they hit.</summary>
    None,
}

/// <summary>Why a fence tile has to be dug rather than blown up.</summary>
public enum DigReason
{
    /// <summary>Dynamite leaves the tile standing (hardmode ore, dungeon brick, Lihzahrd brick, chest).</summary>
    BlastImmune,

    /// <summary>A charge would have to flatten something a player built to reach it.</summary>
    Collateral,

    /// <summary>The tile is a thorn or vine that the blast would remove, but nothing reached it.</summary>
    Uncovered,

    /// <summary>Neither tool can remove it with the assumed pick power; reported, never executed.</summary>
    Blocked,
}

/// <summary>A single tile the pickaxe has to remove, in the order the executor reaches it.</summary>
public sealed record DigOrder(int X, int Y, ushort Type, int Hits, DigReason Reason);

/// <summary>One Dynamite detonation.</summary>
public sealed record BlastCharge(
    int Order,
    int X,
    int Y,
    int SectionSequence,
    int CoverTiles,
    int SeedsDestroyed,
    bool LavaInBlast,
    bool WaterInBlast,
    bool TrapInBlast,
    bool GravestoneInBlast,
    bool ExplosivesInBlast,
    int StandX,
    int StandY,
    bool RetreatAvailable,
    int ProtectedTilesInBlast = 0,
    int PlayerBlocksInBlast = 0,
    int BuiltWallTilesInBlast = 0);

/// <summary>
/// A tile the plan wants filled with an inert block. Placed after the blasts, because a plug inside a
/// blast radius would simply be blown up again, and it only ever goes where the tile is air by then.
/// </summary>
public sealed record PlugOrder(int X, int Y, int ItemId);

/// <summary>One fence: the cleared band around a single infection front.</summary>
public sealed record FenceSection(
    int Sequence,
    bool HasEvil,
    bool HasHallow,
    int SeedTiles,
    int FenceTiles,
    int Charges,
    TileRect Bounds,
    double EnclosedWidthWorldPercent,
    double EnclosedInfectablePercent,
    bool WithinAnalyzerLimits,
    bool SealedByFloodVerification,
    int DigTiles = 0,
    int BlockedTiles = 0,
    int PlugTiles = 0);

/// <summary>Totals for the whole plan.</summary>
public sealed record BlastPlanSummary(
    int InfectionNodes,
    int EvilSeeds,
    int HallowSeeds,
    int Sections,
    int MixedSections,
    int FenceTiles,
    int Charges,
    int DynamiteStacks,
    int SeedsDestroyedByBlast,
    int EnclosedSeedTiles,
    int BlastImmuneNodesInFence,
    int LavaTilesInFence,
    int WaterTilesInFence,
    int TrapTilesInFence,
    int GravestoneTilesInFence,
    int ExplosivesTilesInFence,
    long BlastDestroyedTiles,
    int BlastImmuneTilesInBlast,
    bool AllSectionsSealed,
    int VineAnchorTiles,
    long EstimatedPlayerSeconds,
    int DigTiles = 0,
    int BlockedTiles = 0,
    int RequiredPickPower = 0,
    int ChargesWithCollateral = 0,
    int ProtectedTilesInBlast = 0,
    int PlayerBlocksInBlast = 0,
    int BuiltWallTilesInBlast = 0,
    int BuiltWallTilesProtected = 0,
    int BuiltWallTilesWorld = 0,
    int PlugTiles = 0,
    int RequiredPlugBlocks = 0,
    int PlugItemId = 0,
    int VineCurtainTilesSaved = 0,
    long EstimatedDigSeconds = 0);

/// <summary>
/// A downsampled picture of the world for the map: four flag bits per cell.
/// <c>1</c> infectable node, <c>2</c> evil seed, <c>4</c> hallow seed, <c>8</c> fence.
/// </summary>
public sealed record PlanOverview(int CellSize, int Cols, int Rows, byte[] Cells);

/// <summary>A complete, executable blasting plan.</summary>
public sealed record BlastPlan(
    WorldMetadata World,
    BlastPlanOptions Options,
    BlastPlanSummary Summary,
    IReadOnlyList<FenceSection> Sections,
    IReadOnlyList<BlastCharge> Charges,
    IReadOnlyList<string> Notes,
    PlanOverview? Overview = null,
    IReadOnlyList<DigOrder>? Digs = null,
    IReadOnlyList<PlugOrder>? Plugs = null)
{
    /// <summary>Tiles the pickaxe has to remove; empty when dynamite covers the whole fence.</summary>
    public IReadOnlyList<DigOrder> DigOrders => Digs ?? [];

    /// <summary>Tiles to fill with an inert block after the blasts; empty when curtains were dug instead.</summary>
    public IReadOnlyList<PlugOrder> PlugOrders => Plugs ?? [];
}
