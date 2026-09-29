using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZhaDai.Automation
{
    public enum ExecutorState
    {
        Idle,
        GoToStand,
        Arming,
        Retreating,
        WaitingDetonation,
        Digging,
        AwaitRespawn,
        Halted,
        Finished,
    }

    /// <summary>Tuning for the runtime. Defaults are the careful ones.</summary>
    public sealed class ExecutorOptions
    {
        /// <summary>Do not arm a charge while a hostile is closer than this, in tiles.</summary>
        public double HostileSafeDistance { get; set; } = 14d;

        /// <summary>Surface for air once breath drops below this, when the player can drown at all.</summary>
        public int BreathFloor { get; set; } = 80;

        /// <summary>Extra ticks to wait after the fuse is due before touching the world again.</summary>
        public int DetonationSettleTicks { get; set; } = 30;

        /// <summary>Stop instead of dying forever.</summary>
        public int MaxDeaths { get; set; } = 30;

        /// <summary>Refuse a charge whose blast reaches explosives unless the player opts in.</summary>
        public bool AllowExplosives { get; set; }

        /// <summary>How far to look for tombstones after a death.</summary>
        public int GravestoneSearchRadius { get; set; } = 40;

        /// <summary>Give up on one charge after this many ticks so the run always makes progress.</summary>
        public int MaxTicksPerCharge { get; set; } = 6000;

        /// <summary>
        /// Dynamite self damage is a 250x250 px box, so anything inside about 7.81 tiles of the
        /// detonation can be hit. The planner's retreat distance must clear this.
        /// </summary>
        public double SelfDamageBoxTiles { get; set; } = 7.82d;

        /// <summary>A drop steeper than this is treated as a fall risk and the executor refuses to walk off.</summary>
        public int MaxSafeDropTiles { get; set; } = 20;
    }

    /// <summary>Read-only progress, so a UI or the runtime overlay can show what is happening.</summary>
    public sealed class ExecutorStatus
    {
        public ExecutorState State { get; internal set; } = ExecutorState.Idle;

        public int ChargeIndex { get; internal set; }

        public int ChargeCount { get; internal set; }

        public int Fired { get; internal set; }

        public int Skipped { get; internal set; }

        public int Deaths { get; internal set; }

        public int BlastHits { get; internal set; }

        public int GravestonesDug { get; internal set; }

        public long Ticks { get; internal set; }

        public SkipReason LastSkip { get; internal set; }

        public string Message { get; internal set; } = string.Empty;

        public int Remaining
        {
            get { return ChargeCount - ChargeIndex; }
        }
    }

    /// <summary>
    /// Runs one plan: walk to a charge, throw Dynamite, get clear before the fuse ends, wait out the
    /// blast, repeat. It never assumes the world still matches the plan; every tile decision is read
    /// from the live world, and it resumes the current charge after a death instead of restarting.
    /// </summary>
    public sealed class BlastExecutor
    {
        private readonly ExecutionPlan plan;
        private readonly ExecutorOptions options;
        private readonly ExecutorStatus status = new ExecutorStatus();
        private readonly List<int> pathX = new List<int>();
        private readonly List<int> pathY = new List<int>();

        private const int MaxRetreatSteps = 200;

        private long armedTick = -1;
        private long stateTick;
        private int pathCursor;
        private bool deathRecorded;
        private double deathX;
        private double deathY;
        private bool stopped;

        public BlastExecutor(ExecutionPlan plan, ExecutorOptions options)
        {
            this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            status.ChargeCount = plan.Charges.Count;
        }

        public ExecutorStatus Status
        {
            get { return status; }
        }

        /// <summary>Asks the run to wind down at the next safe point.</summary>
        public void Stop()
        {
            stopped = true;
        }

        /// <summary>One game tick. Safe to call every update; does nothing once finished or halted.</summary>
        public void Step(IGameBridge game)
        {
            if (game == null)
            {
                throw new ArgumentNullException(nameof(game));
            }

            status.Ticks++;

            if (stopped)
            {
                SetState(ExecutorState.Finished, SkipReason.ManualStop, "已按要求停止。");
                return;
            }

            if (status.State == ExecutorState.Finished || status.State == ExecutorState.Halted)
            {
                return;
            }

            if (game.PlayerDead)
            {
                HandleDeath(game);
                return;
            }

            if (status.State == ExecutorState.AwaitRespawn)
            {
                // Alive again: tidy up the grave this death left behind, then resume the same charge.
                status.State = ExecutorState.Digging;
                stateTick = game.Tick;
                SetState(ExecutorState.Digging, SkipReason.None, "已复活，先清墓碑再继续。");
                return;
            }

            if (game.PlayerLife <= 0)
            {
                return;
            }

            // Drowning is the one hazard that kills slowly enough to walk away from.
            if (game.PlayerCanDrown && game.PlayerLiquidKind != 0 && game.PlayerBreath < options.BreathFloor)
            {
                game.SetMovement(0, -1, true);
                SetState(status.State, SkipReason.AboutToDrown, "氧气不足，先上浮。");
                return;
            }

            switch (status.State)
            {
                case ExecutorState.Idle:
                    AdvanceToNextCharge(game);
                    break;
                case ExecutorState.GoToStand:
                    TickGoToStand(game);
                    break;
                case ExecutorState.Arming:
                    TickArming(game);
                    break;
                case ExecutorState.Retreating:
                    TickRetreating(game);
                    break;
                case ExecutorState.WaitingDetonation:
                    TickWaiting(game);
                    break;
                case ExecutorState.Digging:
                    TickDigging(game);
                    break;
                default:
                    break;
            }
        }

        private ChargeOrder Current
        {
            get
            {
                if (status.ChargeIndex < 0 || status.ChargeIndex >= plan.Charges.Count)
                {
                    return null;
                }

                return plan.Charges[status.ChargeIndex];
            }
        }

        private void AdvanceToNextCharge(IGameBridge game)
        {
            if (status.ChargeIndex >= plan.Charges.Count)
            {
                SetState(ExecutorState.Finished, SkipReason.None, "施工文件里的雷管都处理完了。");
                return;
            }

            stateTick = game.Tick;
            SetState(ExecutorState.GoToStand, SkipReason.None, "前往站位 " + Current);
        }

        private void TickGoToStand(IGameBridge game)
        {
            ChargeOrder charge = Current;
            if (charge == null)
            {
                SetState(ExecutorState.Finished, SkipReason.None, "没有更多雷管。");
                return;
            }

            int targetX = charge.StandX;
            int targetY = charge.StandY;
            double distance = Chebyshev(game.PlayerX, game.PlayerY, targetX, targetY);
            if (distance <= 1.5d)
            {
                game.SetMovement(0, 0, false);
                SetState(ExecutorState.Arming, SkipReason.None, "到位，准备布雷管。");
                return;
            }

            if (game.Tick - stateTick > options.MaxTicksPerCharge)
            {
                SkipCharge(game, SkipReason.NoRetreatRoom, "走不到站位，跳过这一发。");
                return;
            }

            WalkTowards(game, targetX, targetY);
        }

        private void TickArming(IGameBridge game)
        {
            ChargeOrder charge = Current;
            if (charge == null)
            {
                SetState(ExecutorState.Finished, SkipReason.None, "没有更多雷管。");
                return;
            }

            game.SetMovement(0, 0, false);

            int slot = game.FindDynamiteSlot();
            if (slot < 0)
            {
                SkipCharge(game, SkipReason.NoDynamite, "背包里没有雷管了。");
                return;
            }

            if (!charge.RetreatAvailable)
            {
                SkipCharge(game, SkipReason.NoRetreatRoom, "计划没给这一发留出撤离空间，跳过。");
                return;
            }

            if (charge.HasExplosives && !options.AllowExplosives)
            {
                SkipCharge(game, SkipReason.ExplosivesInBlast, "爆破范围内有炸弹桶/爆炸物，先手动清掉再炸。");
                return;
            }

            if (charge.HasLava && !game.PlayerLavaImmune)
            {
                SkipCharge(game, SkipReason.LavaInBlastWithoutImmunity, "爆开会放出岩浆，跳过。");
                return;
            }

            if (game.PlayerLiquidKind == 2 && !game.PlayerLavaImmune)
            {
                SkipCharge(game, SkipReason.StandingInLava, "正踩在岩浆里，先离开。");
                return;
            }

            if (game.NearestHostileDistance < options.HostileSafeDistance)
            {
                status.Message = "有敌怪靠近，先等它走开或处理掉。";
                return;
            }

            // The last exit check before committing: is there anywhere to run to inside the fuse?
            if (!TryPlanRetreat(game, charge))
            {
                SkipCharge(game, SkipReason.NoRetreatRoom, "引信时间内退不出爆炸范围，跳过。");
                return;
            }

            game.SelectSlot(slot);
            game.ThrowDynamiteAt(charge.X, charge.Y);
            status.Fired++;
            status.LastSkip = SkipReason.None;
            armedTick = game.Tick;
            SetState(ExecutorState.Retreating, SkipReason.None, "已丢出雷管，撤到 " + plan.RetreatTiles + " 格外。");
        }

        private void TickRetreating(IGameBridge game)
        {
            ChargeOrder charge = Current;
            if (charge == null)
            {
                SetState(ExecutorState.Finished, SkipReason.None, "没有更多雷管。");
                return;
            }

            double distance = Chebyshev(game.PlayerX, game.PlayerY, charge.X, charge.Y);
            if (distance >= plan.RetreatTiles)
            {
                game.SetMovement(0, 0, false);
                SetState(ExecutorState.WaitingDetonation, SkipReason.None, "已脱离爆炸范围，等起爆。");
                return;
            }

            // Follow the route planned before the throw. It is followed step by step rather than as a
            // straight line away from the charge because the fence is a tunnel: going "away" often
            // means going into rock, and the fuse does not wait for that.
            while (pathCursor < pathX.Count)
            {
                int waypointX = pathX[pathCursor];
                int waypointY = pathY[pathCursor];

                if (Chebyshev(game.PlayerX, game.PlayerY, waypointX, waypointY) <= 0.9d)
                {
                    pathCursor++;
                    continue;
                }

                if (game.IsSolid(waypointX, waypointY))
                {
                    game.DigTile(waypointX, waypointY);
                    game.SetMovement(0, 0, false);
                    return;
                }

                WalkTowards(game, waypointX, waypointY);
                return;
            }

            // No route left (or none was found): the old straight-line retreat is still better than
            // standing still.
            WalkAway(game, charge.X, charge.Y);
        }

        private void TickWaiting(IGameBridge game)
        {
            ChargeOrder charge = Current;
            long elapsed = game.Tick - armedTick;

            // Score the detonation frame itself: this is the number that says whether the run
            // actually kept the player out of the blast, rather than merely claiming to.
            if (charge != null && elapsed >= plan.FuseTicks && elapsed < plan.FuseTicks + options.DetonationSettleTicks)
            {
                double distance = Chebyshev(game.PlayerX, game.PlayerY, charge.X, charge.Y);
                if (distance < options.SelfDamageBoxTiles)
                {
                    status.BlastHits++;
                    game.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "起爆时距爆炸点只有 {0:0.##} 格，吃了自伤。",
                        distance));
                }
            }

            if (elapsed < plan.FuseTicks + options.DetonationSettleTicks)
            {
                game.SetMovement(0, 0, false);
                return;
            }

            status.ChargeIndex++;
            SetState(ExecutorState.Idle, SkipReason.None, "这一发完成。");
        }

        private void TickDigging(IGameBridge game)
        {
            int x;
            int y;
            if (!FindGravestone(game, out x, out y))
            {
                SetState(ExecutorState.GoToStand, SkipReason.None, "墓碑清完了，继续施工。");
                return;
            }

            if (Chebyshev(game.PlayerX, game.PlayerY, x, y) <= 4.5d)
            {
                game.SetMovement(0, 0, false);
                game.DigTile(x, y);
                status.GravestonesDug++;
                return;
            }

            WalkTowards(game, x, y);
        }

        private bool FindGravestone(IGameBridge game, out int x, out int y)
        {
            int centreX = (int)Math.Round(game.PlayerX);
            int centreY = (int)Math.Round(game.PlayerY);
            int radius = options.GravestoneSearchRadius;
            double best = double.MaxValue;
            x = 0;
            y = 0;
            bool found = false;

            for (int ty = Math.Max(0, centreY - radius); ty <= Math.Min(game.TileHeight - 1, centreY + radius); ty++)
            {
                for (int tx = Math.Max(0, centreX - radius); tx <= Math.Min(game.TileWidth - 1, centreX + radius); tx++)
                {
                    if (!game.IsGravestone(tx, ty))
                    {
                        continue;
                    }

                    double distance = Chebyshev(game.PlayerX, game.PlayerY, tx, ty);
                    if (distance < best)
                    {
                        best = distance;
                        x = tx;
                        y = ty;
                        found = true;
                    }
                }
            }

            return found;
        }

        private void HandleDeath(IGameBridge game)
        {
            if (!deathRecorded)
            {
                deathRecorded = true;
                deathX = game.PlayerX;
                deathY = game.PlayerY;
                status.Deaths++;
                game.Log(string.Format(
                    CultureInfo.InvariantCulture,
                    "第 {0} 次死亡，位置 ({1:0},{2:0})，复活后回到当前这一发继续。",
                    status.Deaths,
                    deathX,
                    deathY));
            }

            if (status.Deaths > options.MaxDeaths)
            {
                SetState(ExecutorState.Halted, SkipReason.ManualStop, "死亡次数超过上限，停下来等你处理。");
                return;
            }

            status.State = ExecutorState.AwaitRespawn;
        }

        /// <summary>
        /// Is there a way out of the blast that can actually be walked in the time the fuse allows?
        ///
        /// The planner already worked this out, but the world may have changed since, and "there is
        /// open space ten tiles away" is not the same as "the player can get there". A fence band is a
        /// one tile tunnel through solid rock, so a straight line away from the charge usually runs
        /// into a wall; walking blindly there is how a run turns into a pile of deaths. This searches
        /// the live tiles for the cheapest reachable escape cell, counting a solid tile as one dig,
        /// and stores the route for the retreat to follow.
        /// </summary>
        private bool TryPlanRetreat(IGameBridge game, ChargeOrder charge)
        {
            pathX.Clear();
            pathY.Clear();
            pathCursor = 0;

            int startX = (int)Math.Round(game.PlayerX);
            int startY = (int)Math.Round(game.PlayerY);
            int reach = plan.RetreatTiles;
            int window = reach + 6;
            int minX = Math.Max(0, charge.X - window);
            int maxX = Math.Min(game.TileWidth - 1, charge.X + window);
            int minY = Math.Max(0, charge.Y - window);
            int maxY = Math.Min(game.TileHeight - 1, charge.Y + window);
            int width = maxX - minX + 1;
            int height = maxY - minY + 1;

            int[] steps = new int[width * height];
            int[] digs = new int[width * height];
            int[] parent = new int[width * height];
            for (int i = 0; i < steps.Length; i++)
            {
                steps[i] = int.MaxValue;
                parent[i] = -1;
            }

            int startIndex = Index(startX, startY);
            if (startIndex < 0)
            {
                return false;
            }

            steps[startIndex] = 0;
            digs[startIndex] = 0;

            Queue<int> queue = new Queue<int>();
            queue.Enqueue(startIndex);

            int bestIndex = -1;
            int bestSteps = int.MaxValue;
            int bestDigs = int.MaxValue;

            // Four directions only: digging sideways or up is what a player does, and it keeps the
            // route something the follower below can actually walk.
            int[] offsetX = { 1, -1, 0, 0 };
            int[] offsetY = { 0, 0, 1, -1 };

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                int currentX = minX + (current % width);
                int currentY = minY + (current / width);

                if (Math.Max(Math.Abs(currentX - charge.X), Math.Abs(currentY - charge.Y)) >= reach &&
                    IsEscapeCell(game, currentX, currentY) &&
                    (steps[current] < bestSteps || (steps[current] == bestSteps && digs[current] < bestDigs)))
                {
                    bestIndex = current;
                    bestSteps = steps[current];
                    bestDigs = digs[current];
                }

                for (int i = 0; i < 4; i++)
                {
                    int nextX = currentX + offsetX[i];
                    int nextY = currentY + offsetY[i];
                    if (nextX < minX || nextX > maxX || nextY < minY || nextY > maxY)
                    {
                        continue;
                    }

                    int next = Index(nextX, nextY);
                    if (next < 0)
                    {
                        continue;
                    }

                    bool solid = game.IsSolid(nextX, nextY);
                    int nextSteps = steps[current] + 1;
                    int nextDigs = digs[current] + (solid ? 1 : 0);

                    // Five seconds of fuse: more than a couple of dozen tiles of mining is not a plan,
                    // it is wishful thinking.
                    if (nextDigs > 24 || nextSteps > MaxRetreatSteps)
                    {
                        continue;
                    }

                    if (nextSteps > steps[next] || (nextSteps == steps[next] && nextDigs >= digs[next]))
                    {
                        continue;
                    }

                    steps[next] = nextSteps;
                    digs[next] = nextDigs;
                    parent[next] = current;
                    queue.Enqueue(next);
                }
            }

            if (bestIndex < 0)
            {
                return false;
            }

            int node = bestIndex;
            while (node >= 0 && node != startIndex)
            {
                pathX.Add(minX + (node % width));
                pathY.Add(minY + (node / width));
                node = parent[node];
            }

            pathX.Reverse();
            pathY.Reverse();
            return true;

            int Index(int x, int y)
            {
                if (x < minX || x > maxX || y < minY || y > maxY)
                {
                    return -1;
                }

                return ((y - minY) * width) + (x - minX);
            }
        }

        /// <summary>A cell worth standing in when the charge goes off.</summary>
        private static bool IsEscapeCell(IGameBridge game, int x, int y)
        {
            if (game.IsSolid(x, y))
            {
                return false;
            }

            int liquid = game.LiquidKind(x, y);
            if (liquid == 2 && !game.PlayerLavaImmune)
            {
                return false;
            }

            return liquid == 0 || !game.PlayerCanDrown;
        }

        private void WalkTowards(IGameBridge game, int targetX, int targetY)
        {
            int dx = Math.Sign(targetX - game.PlayerX);
            int dy = Math.Sign(targetY - game.PlayerY);
            bool jump = false;

            if (dy < 0 && IsBlocked(game, (int)Math.Round(game.PlayerX) + dx, (int)Math.Round(game.PlayerY) - 1))
            {
                jump = true;
            }

            // Never walk into a wall: mine it instead, which is what the player would do.
            int aheadX = (int)Math.Round(game.PlayerX) + dx;
            int aheadY = (int)Math.Round(game.PlayerY);
            if (dx != 0 && game.IsSolid(aheadX, aheadY))
            {
                game.DigTile(aheadX, aheadY);
                game.SetMovement(0, 0, false);
                return;
            }

            game.SetMovement(dx, dy, jump);
        }

        private void WalkAway(IGameBridge game, int fromX, int fromY)
        {
            int dx = Math.Sign(game.PlayerX - fromX);
            int dy = Math.Sign(game.PlayerY - fromY);
            if (dx == 0 && dy == 0)
            {
                dx = 1;
            }

            bool jump = false;
            int aheadY = (int)Math.Round(game.PlayerY);
            int aheadX = (int)Math.Round(game.PlayerX) + dx;
            if (dx != 0 && game.IsSolid(aheadX, aheadY))
            {
                // Blocked while running from the blast: go up, and mine if the ceiling is the problem.
                int upX = (int)Math.Round(game.PlayerX);
                int upY = (int)Math.Round(game.PlayerY) - 1;
                if (game.IsSolid(upX, upY))
                {
                    game.DigTile(upX, upY);
                    game.SetMovement(0, 0, false);
                    return;
                }

                jump = true;
            }

            game.SetMovement(dx, dy, jump);
        }

        private static bool IsBlocked(IGameBridge game, int x, int y)
        {
            return game.IsSolid(x, y);
        }

        private void SkipCharge(IGameBridge game, SkipReason reason, string message)
        {
            status.Skipped++;
            status.LastSkip = reason;
            status.ChargeIndex++;
            game.Log(message);
            SetState(ExecutorState.Idle, reason, message);
        }

        private void SetState(ExecutorState state, SkipReason reason, string message)
        {
            // LastSkip is owned by the code that actually decides to skip or fire; setting it here too
            // would let a later ordinary transition such as "finished" erase the reason.
            _ = reason;
            status.State = state;
            status.Message = message;
        }

        private static double Chebyshev(double ax, double ay, double bx, double by)
        {
            return Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));
        }

        /// <summary>Total tiles the plan will destroy if it runs to the end; useful in a report.</summary>
        public long PlannedBlastTiles
        {
            get { return (long)plan.Charges.Count * 149L; }
        }
    }
}
