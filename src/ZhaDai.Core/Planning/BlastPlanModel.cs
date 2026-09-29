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
    }
}

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
    bool RetreatAvailable);

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
    bool SealedByFloodVerification);

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
    long EstimatedPlayerSeconds);

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
    PlanOverview? Overview = null);
