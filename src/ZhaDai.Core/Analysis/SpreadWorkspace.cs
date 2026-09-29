using ZhaDai.Core.World;

namespace ZhaDai.Core.Analysis;

/// <summary>Result of a bounded flood over the spread graph.</summary>
public readonly record struct FloodResult(int VisitedCount, bool Escaped, TileRect VisitedBounds)
{
    public static FloodResult Empty { get; } = new(0, false, new TileRect(0, 0, -1, -1));
}

/// <summary>A group of seeds that form one infection front and therefore get one fence.</summary>
public sealed record SeedCluster(
    int Sequence,
    IReadOnlyList<int> Seeds,
    bool HasEvil,
    bool HasHallow,
    TileRect Bounds)
{
    public int SeedCount => Seeds.Count;

    public bool IsMixed => HasEvil && HasHallow;
}

/// <summary>
/// Reusable scratch state for walking the spread graph: a Chebyshev distance field and a bounded
/// node flood. Everything is region-scoped so that a small infection front never costs a
/// world-sized traversal.
/// </summary>
public sealed class SpreadWorkspace
{
    /// <summary>Distance values at or above this are treated as unreachable.</summary>
    public const ushort Unreachable = ushort.MaxValue;

    private readonly InfectionModel model;
    private readonly TileGrid tiles;
    private readonly int width;
    private readonly int height;
    private readonly ushort[] distance;
    private readonly byte[] visited;
    private readonly List<int> touched = [];

    /// <summary>
    /// How far a downward vine from a plant supporting tile can carry the infection. The vector is
    /// confirmed in vanilla: <c>WorldGen.CheckVines</c> retypes a vine to match the tile above it, so a
    /// vine hanging under corrupt grass becomes <c>TileID.CorruptVines</c> (636), and
    /// <c>TileID.Sets.SpreadsCorruption</c> contains 636, so every vine tile spreads on its own.
    /// The depth itself is a growth process rather than a constant, so this is the conservative
    /// figure the sibling analyzer used and it can be raised or lowered by the caller.
    /// </summary>
    public int VineReach { get; set; } = InfectionModel.VineDownwardReach;

    /// <summary>
    /// Tiles the plan intends to fill with an inert block. A plug under a plant stops the vine at its
    /// source, so the flood must not treat that column as a route; without this the verification would
    /// reject a plan whose curtains were replaced by plugs.
    /// </summary>
    public IReadOnlySet<int>? Plugged { get; set; }

    public SpreadWorkspace(InfectionModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        this.model = model;
        tiles = model.Tiles;
        width = tiles.Width;
        height = tiles.Height;
        distance = new ushort[tiles.TileCount];
        Array.Fill(distance, Unreachable);
        visited = new byte[tiles.TileCount];
    }

    /// <summary>
    /// Exact Chebyshev distance to the nearest seed, computed with a two-pass chamfer over the
    /// supplied region. The region must extend at least <paramref name="cap"/> tiles past every
    /// seed so that the field is correct where it matters.
    /// </summary>
    public void ComputeChebyshevDistance(IReadOnlyList<int> seeds, TileRect region, int cap)
    {
        TileRect area = region.Clamp(width, height);
        if (area.IsEmpty)
        {
            return;
        }

        int x0 = area.MinX;
        int y0 = area.MinY;
        int rw = area.Width;
        int rh = area.Height;

        foreach (int seed in seeds)
        {
            distance[seed] = 0;
        }

        // Forward pass.
        for (int y = y0; y <= area.MaxY; y++)
        {
            int row = y * width;
            for (int x = x0; x <= area.MaxX; x++)
            {
                int index = row + x;
                int best = distance[index];
                if (best == 0)
                {
                    continue;
                }

                int candidate = Unreachable;
                if (x > x0)
                {
                    candidate = Math.Min(candidate, distance[index - 1] + 1);
                }

                if (y > y0)
                {
                    candidate = Math.Min(candidate, distance[index - width] + 1);
                    if (x > x0)
                    {
                        candidate = Math.Min(candidate, distance[index - width - 1] + 1);
                    }

                    if (x < area.MaxX)
                    {
                        candidate = Math.Min(candidate, distance[index - width + 1] + 1);
                    }
                }

                if (candidate < best)
                {
                    distance[index] = (ushort)Math.Min(candidate, cap);
                }
            }
        }

        // Backward pass.
        for (int y = area.MaxY; y >= y0; y--)
        {
            int row = y * width;
            for (int x = area.MaxX; x >= x0; x--)
            {
                int index = row + x;
                int best = distance[index];
                if (best == 0)
                {
                    continue;
                }

                int candidate = Unreachable;
                if (x < area.MaxX)
                {
                    candidate = Math.Min(candidate, distance[index + 1] + 1);
                }

                if (y < area.MaxY)
                {
                    candidate = Math.Min(candidate, distance[index + width] + 1);
                    if (x < area.MaxX)
                    {
                        candidate = Math.Min(candidate, distance[index + width + 1] + 1);
                    }

                    if (x > x0)
                    {
                        candidate = Math.Min(candidate, distance[index + width - 1] + 1);
                    }
                }

                if (candidate < best)
                {
                    distance[index] = (ushort)Math.Min(candidate, cap);
                }
            }
        }
    }

    public void ResetDistance(TileRect region)
    {
        TileRect area = region.Clamp(width, height);
        if (area.IsEmpty)
        {
            return;
        }

        int rowBytes = width;
        for (int y = area.MinY; y <= area.MaxY; y++)
        {
            Array.Fill(distance, Unreachable, (y * rowBytes) + area.MinX, area.Width);
        }
    }

    public ushort DistanceAt(int index) => distance[index];

    public ushort DistanceAt(int x, int y) => distance[(y * width) + x];

    /// <summary>
    /// Floods the spread graph from <paramref name="seeds"/>, refusing to enter nodes listed in
    /// <paramref name="blocked"/>. Reports whether the flood reached the border of
    /// <paramref name="region"/>, which is exactly how a hole in a fence is detected.
    /// </summary>
    public FloodResult Flood(IReadOnlyList<int> seeds, byte[]? blocked, TileRect region)
    {
        TileRect area = region.Clamp(width, height);
        if (area.IsEmpty || seeds.Count == 0)
        {
            return FloodResult.Empty;
        }

        int visitedCount = 0;
        bool escaped = false;
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;

        Queue<int> queue = new();
        foreach (int seed in seeds)
        {
            if (!InArea(seed, area) || visited[seed] != 0)
            {
                continue;
            }

            visited[seed] = 1;
            touched.Add(seed);
            queue.Enqueue(seed);
        }

        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            visitedCount++;

            int x = index % width;
            int y = index / width;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;

            // Touching the region border means the flood found a way around the fence. Touching the
            // world border does not: the edge of the world is itself a barrier, so a front sitting
            // against it has nothing to escape through.
            if ((x == area.MinX && area.MinX > 0) ||
                (x == area.MaxX && area.MaxX < width - 1) ||
                (y == area.MinY && area.MinY > 0) ||
                (y == area.MaxY && area.MaxY < height - 1))
            {
                escaped = true;
            }

            EnqueueNeighbours(queue, index, x, y, blocked, area);
        }

        foreach (int index in touched)
        {
            visited[index] = 0;
        }

        touched.Clear();

        TileRect bounds = visitedCount == 0 ? new TileRect(0, 0, -1, -1) : new TileRect(minX, minY, maxX, maxY);
        return new FloodResult(visitedCount, escaped, bounds);
    }

    /// <summary>
    /// Groups seeds that belong to one infection front. Two seeds join a cluster when they are no
    /// further apart than <paramref name="linkDistance"/> on each axis; raising that value trades
    /// enclosed area for fewer, shorter fences.
    /// </summary>
    public IReadOnlyList<SeedCluster> ClusterSeeds(int linkDistance)
    {
        if (linkDistance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(linkDistance));
        }

        List<int> seedList = [.. model.EnumerateSeeds()];
        List<SeedCluster> clusters = [];
        if (seedList.Count == 0)
        {
            return clusters;
        }

        int sequence = 0;
        Queue<int> queue = new();

        foreach (int start in seedList)
        {
            if (visited[start] != 0)
            {
                continue;
            }

            sequence++;
            visited[start] = 1;
            touched.Add(start);
            queue.Enqueue(start);

            List<int> members = [];
            bool hasEvil = false;
            bool hasHallow = false;
            TileRect bounds = new(start % width, start / width, start % width, start / width);

            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                members.Add(index);

                int x = index % width;
                int y = index / width;
                SeedKind kind = model.KindAt(index);
                hasEvil |= kind == SeedKind.Evil;
                hasHallow |= kind == SeedKind.Hallow;
                bounds = bounds.Union(new TileRect(x, y, x, y));

                for (int dy = -linkDistance; dy <= linkDistance; dy++)
                {
                    int ny = y + dy;
                    if ((uint)ny >= (uint)height)
                    {
                        continue;
                    }

                    for (int dx = -linkDistance; dx <= linkDistance; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        int nx = x + dx;
                        if ((uint)nx >= (uint)width)
                        {
                            continue;
                        }

                        int neighbour = (ny * width) + nx;
                        if (visited[neighbour] != 0 || model.KindAt(neighbour) == SeedKind.None)
                        {
                            continue;
                        }

                        visited[neighbour] = 1;
                        touched.Add(neighbour);
                        queue.Enqueue(neighbour);
                    }
                }
            }

            clusters.Add(new SeedCluster(sequence, members, hasEvil, hasHallow, bounds));
        }

        foreach (int index in touched)
        {
            visited[index] = 0;
        }

        touched.Clear();

        return clusters;
    }

    private void EnqueueNeighbours(Queue<int> queue, int index, int x, int y, byte[]? blocked, TileRect area)
    {
        int x0 = Math.Max(area.MinX, x - InfectionModel.SpreadReach);
        int x1 = Math.Min(area.MaxX, x + InfectionModel.SpreadReach);
        int y0 = Math.Max(area.MinY, y - InfectionModel.SpreadReach);
        int y1 = Math.Min(area.MaxY, y + InfectionModel.SpreadReach);

        for (int ny = y0; ny <= y1; ny++)
        {
            int row = ny * width;
            for (int nx = x0; nx <= x1; nx++)
            {
                int neighbour = row + nx;
                if (neighbour == index)
                {
                    continue;
                }

                TryEnqueue(queue, neighbour, blocked);
            }
        }

        if (!TileIds.SupportsPlantGrowth(tiles.TypeAt(index)))
        {
            return;
        }

        // A plant or thorn can cross up to four tiles horizontally or upward.
        for (int dx = -InfectionModel.PlantHorizontalReach; dx <= InfectionModel.PlantHorizontalReach; dx++)
        {
            if (dx != 0)
            {
                TryEnqueueAt(queue, x + dx, y, blocked, area);
            }
        }

        for (int dy = 1; dy <= InfectionModel.PlantUpwardReach; dy++)
        {
            for (int dx = -InfectionModel.PlantHorizontalReach; dx <= InfectionModel.PlantHorizontalReach; dx++)
            {
                TryEnqueueAt(queue, x + dx, y - dy, blocked, area);
            }
        }

        // A downward vine is anchored to its grass tile, so it keeps the same column -- unless the tile
        // directly below the plant is plugged, in which case the vine never starts.
        bool vinePlugged = Plugged != null && Plugged.Contains(index + width);
        if (!vinePlugged)
        {
            for (int dy = InfectionModel.SpreadReach + 1; dy <= VineReach; dy++)
            {
                TryEnqueueAt(queue, x, y + dy, blocked, area);
            }
        }
    }

    private void TryEnqueueAt(Queue<int> queue, int x, int y, byte[]? blocked, TileRect area)
    {
        if (x < area.MinX || x > area.MaxX || y < area.MinY || y > area.MaxY)
        {
            return;
        }

        TryEnqueue(queue, (y * width) + x, blocked);
    }

    private void TryEnqueue(Queue<int> queue, int neighbour, byte[]? blocked)
    {
        if (visited[neighbour] != 0 || model.NodeMask[neighbour] == 0)
        {
            return;
        }

        if (blocked is not null && blocked[neighbour] != 0)
        {
            return;
        }

        visited[neighbour] = 1;
        touched.Add(neighbour);
        queue.Enqueue(neighbour);
    }

    private bool InArea(int index, TileRect area)
    {
        int x = index % width;
        int y = index / width;
        return area.Contains(x, y);
    }
}
