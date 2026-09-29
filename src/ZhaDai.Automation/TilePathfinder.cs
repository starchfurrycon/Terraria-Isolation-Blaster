using System;
using System.Collections.Generic;

namespace ZhaDai.Automation
{
    /// <summary>Tuning for the path search. Costs are in tenths of a tile so the search stays integer.</summary>
    public sealed class PathOptions
    {
        /// <summary>Cost of walking one tile through air.</summary>
        public int StepCost { get; set; } = 10;

        /// <summary>Cost of mining one tile out of the way. Higher means the search prefers a detour.</summary>
        public int DigCost { get; set; } = 120;

        /// <summary>Extra cost for moving up, because a jump is slower than a walk.</summary>
        public int JumpCost { get; set; } = 6;

        /// <summary>Cost per tile of a voluntary drop.</summary>
        public int FallCost { get; set; } = 4;

        /// <summary>Never plan a drop deeper than this; deeper ones are treated as walls.</summary>
        public int MaxFallTiles { get; set; } = 16;

        /// <summary>Cost of moving through water; honey is a little worse, lava is impassable.</summary>
        public int WaterCost { get; set; } = 18;

        /// <summary>Honey is slower to move through than water.</summary>
        public int HoneyCost { get; set; } = 24;

        /// <summary>How far around the start and goal the search may explore.</summary>
        public int WindowRadius { get; set; } = 96;

        /// <summary>Give up after this many expanded nodes and let the caller fall back.</summary>
        public int NodeLimit { get; set; } = 20000;
    }

    /// <summary>The outcome of a search, including why it failed when it did.</summary>
    public sealed class PathResult
    {
        /// <summary>Waypoints from the tile after the start up to and including the goal.</summary>
        public List<(int X, int Y)> Waypoints { get; } = new List<(int X, int Y)>();

        /// <summary>Tiles along the path that have to be mined.</summary>
        public int DigTiles { get; set; }

        /// <summary>Total cost in tenths of a tile; 0 when no path was found.</summary>
        public int Cost { get; set; }

        /// <summary>Explored nodes, for the log line that explains an expensive search.</summary>
        public int Expanded { get; set; }

        public bool Found { get; set; }

        public string Failure { get; set; } = string.Empty;
    }

    /// <summary>
    /// A* over the live tile grid, used for every kind of walking the executor does: to a stand
    /// position, to a tile that has to be dug, and out of a blast radius.
    ///
    /// The point is that "walk straight at the target and mine whatever is in the way" is not a route,
    /// it is a direction. Underground that means chewing a tunnel through hardmode ore when a two tile
    /// detour around it was free, and in the middle of a blast countdown it means walking into a wall
    /// and dying next to your own Dynamite. The search charges for digging, refuses to plan through
    /// lava, and knows that falling is allowed but not free — so the cheap answer it returns is usually
    /// a path a player would actually take.
    ///
    /// Nothing here touches Terraria types: the whole world is read through <see cref="IGameBridge"/>,
    /// which is why the fake world in the test suite can exercise it.
    /// </summary>
    public sealed class TilePathfinder
    {
        private readonly IGameBridge game;
        private readonly PathOptions options;
        private readonly int pickPower;

        public TilePathfinder(IGameBridge game, PathOptions options)
        {
            this.game = game ?? throw new ArgumentNullException(nameof(game));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            pickPower = game.BestPickPower;
        }

        /// <summary>
        /// Minimum pick power that can damage a tile at all, mirroring the gates in
        /// <c>Player.GetPickaxeDamage</c>. Zero means any pickaxe manages; a huge number means never, so
        /// the search treats it as a wall instead of planning a tunnel through it.
        /// </summary>
        public static int RequiredPickPower(int type)
        {
            switch (type)
            {
                case 0:
                    return 0;
                case 26:
                    return int.MaxValue;
                case 211:
                    return 200;
                case 226:
                case 237:
                    return 210;
                case 111:
                case 223:
                    return 150;
                case 108:
                case 222:
                    return 110;
                case 107:
                case 221:
                case 41:
                case 43:
                case 44:
                case 677:
                case 678:
                case 679:
                    return 100;
                case 25:
                case 203:
                case 117:
                case 58:
                case 77:
                    return 65;
                case 22:
                case 204:
                case 56:
                    return 55;
                case 37:
                    return 50;
                default:
                    return 0;
            }
        }

        /// <summary>True when the player's pickaxe can eventually remove the tile.</summary>
        public bool CanDig(int x, int y)
        {
            if (!game.IsSolid(x, y))
            {
                return false;
            }

            int required = RequiredPickPower(game.TileType(x, y));
            return required != int.MaxValue && pickPower >= required;
        }

        /// <summary>
        /// Finds a route from the player's tile to the goal. Returns an empty result with a reason when
        /// there is none inside the search window, which the caller turns into the old fallback.
        /// </summary>
        public PathResult FindPath(int startX, int startY, int goalX, int goalY)
        {
            PathResult result = new PathResult();
            if (!InBounds(startX, startY) || !InBounds(goalX, goalY))
            {
                result.Failure = "起点或终点在世界外";
                return result;
            }

            if (startX == goalX && startY == goalY)
            {
                result.Found = true;
                return result;
            }

            int minX = Math.Max(0, Math.Min(startX, goalX) - options.WindowRadius);
            int maxX = Math.Min(game.TileWidth - 1, Math.Max(startX, goalX) + options.WindowRadius);
            int minY = Math.Max(0, Math.Min(startY, goalY) - options.WindowRadius);
            int maxY = Math.Min(game.TileHeight - 1, Math.Max(startY, goalY) + options.WindowRadius);
            int width = maxX - minX + 1;
            int height = maxY - minY + 1;
            int count = width * height;

            int[] gScore = new int[count];
            int[] cameFrom = new int[count];
            byte[] state = new byte[count];
            for (int i = 0; i < count; i++)
            {
                gScore[i] = int.MaxValue;
                cameFrom[i] = -1;
            }

            Heap heap = new Heap(count);
            int start = Index(startX, startY, minX, minY, width);
            int goal = Index(goalX, goalY, minX, minY, width);
            gScore[start] = 0;
            heap.Push(start, Heuristic(startX, startY, goalX, goalY));

            int[] stepX = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] stepY = { 0, 0, 1, -1, 1, -1, 1, -1 };

            while (heap.Count > 0)
            {
                int node = heap.Pop(out int fScore);
                if (state[node] == 2)
                {
                    continue;
                }

                state[node] = 2;
                result.Expanded++;

                if (node == goal)
                {
                    BuildResult(result, cameFrom, node, minX, minY, width, start);
                    return result;
                }

                if (result.Expanded > options.NodeLimit)
                {
                    result.Failure = "搜索节点超过上限（" + options.NodeLimit + "）";
                    return result;
                }

                int x = minX + (node % width);
                int y = minY + (node / width);

                for (int direction = 0; direction < stepX.Length; direction++)
                {
                    int dx = stepX[direction];
                    int dy = stepY[direction];
                    int nx = x + dx;
                    int ny = y + dy;
                    if (nx < minX || nx > maxX || ny < minY || ny > maxY)
                    {
                        continue;
                    }

                    if (dx != 0 && dy != 0)
                    {
                        // Do not cut corners: a diagonal move needs both orthogonal neighbours open,
                        // otherwise the player clips the corner of the wall and stops dead.
                        if (!Passable(x + dx, y) || !Passable(x, y + dy))
                        {
                            continue;
                        }
                    }

                    int stepCost;
                    if (!TryEnter(nx, ny, out stepCost, out bool dig))
                    {
                        continue;
                    }

                    int candidate = gScore[node] + stepCost;
                    int neighbour = Index(nx, ny, minX, minY, width);
                    if (candidate >= gScore[neighbour])
                    {
                        continue;
                    }

                    gScore[neighbour] = candidate;
                    cameFrom[neighbour] = node;
                    state[neighbour] = 1;
                    heap.Push(neighbour, candidate + Heuristic(nx, ny, goalX, goalY));
                }

                // Dropping down a shaft is a move too, and it is often the only way into a cave the
                // band runs through. Deeper drops cost more and very deep ones are refused outright.
                for (int drop = 2; drop <= options.MaxFallTiles; drop++)
                {
                    int nx = x;
                    int ny = y + drop;
                    if (ny > maxY)
                    {
                        break;
                    }

                    // Blocked on the way down: no falling through rock.
                    if (!Passable(x, y + drop - 1))
                    {
                        break;
                    }

                    // The tile the player would end up in has to be open. Landing "into" a solid tile is
                    // not a drop, it is mining the floor, and charging it as a cheap move made the search
                    // cheerfully tunnel along under the ground.
                    if (!Passable(nx, ny))
                    {
                        continue;
                    }

                    int candidate = gScore[node] + options.StepCost + (drop * options.FallCost);
                    int neighbour = Index(nx, ny, minX, minY, width);
                    if (candidate >= gScore[neighbour])
                    {
                        continue;
                    }

                    gScore[neighbour] = candidate;
                    cameFrom[neighbour] = node;
                    state[neighbour] = 1;
                    heap.Push(neighbour, candidate + Heuristic(nx, ny, goalX, goalY));
                }
            }

            result.Failure = "窗口内没有通路";
            return result;
        }

        /// <summary>True when the tile can be occupied without removing it.</summary>
        private bool Passable(int x, int y)
        {
            if (x < 0 || y < 0 || x >= game.TileWidth || y >= game.TileHeight)
            {
                return false;
            }

            if (game.IsSolid(x, y))
            {
                return false;
            }

            return game.LiquidKind(x, y) != 2;
        }

        /// <summary>
        /// Cost of moving into a tile, or false when that is impossible: lava, out of reach rock, or
        /// solid rock the pickaxe cannot break.
        /// </summary>
        private bool TryEnter(int x, int y, out int cost, out bool dig)
        {
            cost = 0;
            dig = false;
            if (x < 0 || y < 0 || x >= game.TileWidth || y >= game.TileHeight)
            {
                return false;
            }

            int liquid = game.LiquidKind(x, y);
            if (liquid == 2 && !game.PlayerLavaImmune)
            {
                // Walking into lava on the way to a charge is a death with extra steps.
                return false;
            }

            if (game.IsSolid(x, y))
            {
                if (!CanDig(x, y))
                {
                    return false;
                }

                dig = true;
                cost = options.DigCost;
            }
            else
            {
                cost = options.StepCost;
            }

            if (liquid == 1)
            {
                cost += options.WaterCost;
            }
            else if (liquid == 3)
            {
                cost += options.HoneyCost;
            }

            return true;
        }

        private void BuildResult(
            PathResult result,
            int[] cameFrom,
            int node,
            int minX,
            int minY,
            int width,
            int start)
        {
            List<(int X, int Y)> reversed = new List<(int X, int Y)>();
            int current = node;
            while (current != start && current >= 0)
            {
                int x = minX + (current % width);
                int y = minY + (current / width);
                reversed.Add((x, y));
                if (game.IsSolid(x, y) && CanDig(x, y))
                {
                    result.DigTiles++;
                }

                current = cameFrom[current];
            }

            reversed.Reverse();
            result.Waypoints.AddRange(reversed);
            result.Cost = result.Waypoints.Count;
            result.Found = true;
        }

        private bool InBounds(int x, int y)
        {
            return x >= 0 && y >= 0 && x < game.TileWidth && y < game.TileHeight;
        }

        private static int Index(int x, int y, int minX, int minY, int width)
        {
            return ((y - minY) * width) + (x - minX);
        }

        /// <summary>Octile distance: admissible for eight way movement costing at least a tenth each.</summary>
        private static int Heuristic(int x, int y, int goalX, int goalY)
        {
            int dx = Math.Abs(goalX - x);
            int dy = Math.Abs(goalY - y);
            int diagonal = Math.Min(dx, dy);
            int straight = Math.Max(dx, dy) - diagonal;
            return (diagonal * 14) + (straight * 10);
        }

        /// <summary>
        /// A binary heap keyed by f-score. netstandard2.0 has no PriorityQueue, and a sorted list would
        /// make a 20k node search quadratic.
        /// </summary>
        private sealed class Heap
        {
            private readonly int[] nodes;
            private readonly int[] scores;
            private int count;

            public Heap(int capacity)
            {
                nodes = new int[capacity];
                scores = new int[capacity];
            }

            public int Count
            {
                get { return count; }
            }

            public void Push(int node, int score)
            {
                if (count == nodes.Length)
                {
                    return;
                }

                int index = count++;
                nodes[index] = node;
                scores[index] = score;
                while (index > 0)
                {
                    int parent = (index - 1) / 2;
                    if (scores[parent] <= scores[index])
                    {
                        break;
                    }

                    Swap(parent, index);
                    index = parent;
                }
            }

            public int Pop(out int score)
            {
                int node = nodes[0];
                score = scores[0];
                count--;
                if (count > 0)
                {
                    nodes[0] = nodes[count];
                    scores[0] = scores[count];
                    int index = 0;
                    while (true)
                    {
                        int left = (index * 2) + 1;
                        int right = left + 1;
                        int smallest = index;
                        if (left < count && scores[left] < scores[smallest])
                        {
                            smallest = left;
                        }

                        if (right < count && scores[right] < scores[smallest])
                        {
                            smallest = right;
                        }

                        if (smallest == index)
                        {
                            break;
                        }

                        Swap(index, smallest);
                        index = smallest;
                    }
                }

                return node;
            }

            private void Swap(int a, int b)
            {
                int node = nodes[a];
                nodes[a] = nodes[b];
                nodes[b] = node;
                int score = scores[a];
                scores[a] = scores[b];
                scores[b] = score;
            }
        }
    }
}
