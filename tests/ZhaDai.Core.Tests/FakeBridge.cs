using System;
using System.Collections.Generic;
using ZhaDai.Automation;

namespace ZhaDai.Core.Tests;

/// <summary>
/// A tiny game stand-in: a flat floor, gravity, Dynamite that clears tiles on a fuse and hurts a
/// player standing inside the self damage box, death and respawn, and tombstones left behind. It
/// exists so the executor's survival logic can be tested without launching Terraria.
/// </summary>
internal sealed class FakeBridge : IGameBridge
{
    private const int FloorY = 20;
    private const int Radius = 7;

    private readonly bool[] solid;
    private readonly ushort[] types;
    private readonly byte[] liquids;
    private readonly List<double[]> projectiles = [];
    private readonly List<string> logs = [];

    private double velocityY;
    private long deadSince = -1;
    private bool jumpWasHeld;
    private int held;
    private int jumpTicks = 24;
    private int pendingDx;
    private int pendingDy;
    private bool pendingJump;

    public FakeBridge(int width = 400, int height = 60)
    {
        TileWidth = width;
        TileHeight = height;
        solid = new bool[width * height];
        types = new ushort[width * height];
        liquids = new byte[width * height];
        for (int y = FloorY; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                solid[(y * width) + x] = true;
                types[(y * width) + x] = 1;
            }
        }

        PlayerX = 20;
        PlayerY = FloorY - 1;
    }

    public int TileWidth { get; }

    public int TileHeight { get; }

    public long Tick { get; private set; }

    public double PlayerX { get; private set; }

    public double PlayerY { get; private set; }

    public double PlayerVelocityX { get; private set; }

    public double PlayerVelocityY => velocityY;

    /// <summary>This bridge's player occupies a single row, so the floor is the row below it.</summary>
    public bool PlayerGrounded => !PlayerDead && IsSolid((int)Math.Round(PlayerX), (int)Math.Round(PlayerY) + 1);

    public int PlayerLife { get; private set; } = 100;

    public int PlayerLifeMax => 100;

    public bool PlayerDead { get; private set; }

    public bool PlayerImmune { get; set; }

    public int PlayerLiquidKind { get; set; }

    public bool PlayerLavaImmune { get; set; }

    public bool PlayerCanDrown { get; set; }

    public int PlayerBreath { get; set; } = 200;

    public int PlayerBreathMax => 200;

    public double NearestHostileDistance { get; set; } = 999d;

    /// <summary>Tiles per tick. Slowing this down is how a test makes the retreat fail.</summary>
    public double Speed { get; set; } = 0.12d;

    public int DynamiteCount { get; set; } = 99;

    public int RespawnDelay { get; set; } = 200;

    public int DigCount { get; private set; }

    public int Explosions { get; private set; }

    public int Tombstones => tombstones.Count;

    public int DamageTaken { get; private set; }

    public IReadOnlyList<string> Logs => logs;

    public bool IsSolid(int x, int y)
    {
        if (x < 0 || y < 0 || x >= TileWidth || y >= TileHeight)
        {
            return true;
        }

        return solid[(y * TileWidth) + x];
    }

    public int TileType(int x, int y) => IsSolid(x, y) ? types[(y * TileWidth) + x] : 0;

    public int LiquidKind(int x, int y)
    {
        if (x < 0 || y < 0 || x >= TileWidth || y >= TileHeight)
        {
            return 0;
        }

        return liquids[(y * TileWidth) + x];
    }

    /// <summary>Fills a region with liquid: 1 water, 2 lava, 3 honey. Used by the routing tests.</summary>
    public void FillLiquid(int minX, int minY, int maxX, int maxY, byte kind)
    {
        for (int y = Math.Max(0, minY); y <= Math.Min(TileHeight - 1, maxY); y++)
        {
            for (int x = Math.Max(0, minX); x <= Math.Min(TileWidth - 1, maxX); x++)
            {
                liquids[(y * TileWidth) + x] = kind;
            }
        }
    }

    /// <summary>Places solid tiles of a given type, so tests can build unbreakable rock.</summary>
    public void FillType(int minX, int minY, int maxX, int maxY, ushort type)
    {
        for (int y = Math.Max(0, minY); y <= Math.Min(TileHeight - 1, maxY); y++)
        {
            for (int x = Math.Max(0, minX); x <= Math.Min(TileWidth - 1, maxX); x++)
            {
                int index = (y * TileWidth) + x;
                solid[index] = true;
                types[index] = type;
            }
        }
    }

    public bool IsGravestone(int x, int y) => tombstones.Contains((y * TileWidth) + x);

    public int FindDynamiteSlot() => DynamiteCount > 0 ? 3 : -1;

    /// <summary>Fake inventory: only Dynamite is counted, everything else is absent.</summary>
    public int CountItems(int itemId)
    {
        if (itemId == GameIds.Dynamite)
        {
            return DynamiteCount;
        }

        return blocks.TryGetValue(itemId, out int count) ? count : 0;
    }

    /// <summary>Blocks available for plugging, by item id. Wood is 9.</summary>
    public readonly Dictionary<int, int> blocks = [];

    public int PlacedBlocks { get; private set; }

    public bool PlaceBlock(int x, int y, int itemId)
    {
        if (x < 0 || y < 0 || x >= TileWidth || y >= TileHeight)
        {
            return false;
        }

        int index = (y * TileWidth) + x;
        if (solid[index] || !blocks.TryGetValue(itemId, out int count) || count <= 0)
        {
            return false;
        }

        blocks[itemId] = count - 1;
        solid[index] = true;
        types[index] = (ushort)itemId;
        PlacedBlocks++;
        return true;
    }

    /// <summary>Fake pickaxe power; the tests set it to whatever the run should be audited against.</summary>
    public int BestPickPower { get; set; } = 100;

    public void SelectSlot(int slot)
    {
    }

    public void ThrowDynamiteAt(int tileX, int tileY)
    {
        DynamiteCount--;
        projectiles.Add([tileX, tileY, 300d]);
    }

    public void DigTile(int x, int y)
    {
        if (x < 0 || y < 0 || x >= TileWidth || y >= TileHeight)
        {
            return;
        }

        int index = (y * TileWidth) + x;
        bool wasGravestone = tombstones.Remove(index);
        DigCount++;
        if (!solid[index])
        {
            return;
        }

        if (BestPickPower < TilePathfinder.RequiredPickPower(types[index]))
        {
            // Unbreakable with this pickaxe: the real game would just make the swing do nothing.
            return;
        }

        // A tile needs more than one swing when the pickaxe is weaker than the tile's hardness. The real game
        // swings at the pickaxe's own use time; the executor only holds the button, so this is what makes the
        // difference between "mining" and "a frame operation that deletes terrain" visible in a test.
        int swings = Math.Max(1, TilePathfinder.RequiredPickPower(types[index]) / 25);
        swingsByTile.TryGetValue(index, out int already);
        already++;
        if (already < swings)
        {
            swingsByTile[index] = already;
            return;
        }

        swingsByTile.Remove(index);
        solid[index] = false;
        types[index] = 0;
        DugTiles.Add((x, y));
    }

    public void StopDigging()
    {
        Digging = false;
    }

    /// <summary>True between a DigTile call and StopDigging, so a test can see the swing being held.</summary>
    public bool Digging { get; private set; }

    /// <summary>Swings spent per tile, for the multi-hit hardness above.</summary>
    private readonly Dictionary<int, int> swingsByTile = [];

    public int TileWall(int x, int y)
    {
        if (x < 0 || y < 0 || x >= TileWidth || y >= TileHeight)
        {
            return 0;
        }

        return walls.TryGetValue((y * TileWidth) + x, out int wall) ? wall : 0;
    }

    /// <summary>Puts a wall behind a tile, i.e. marks it as part of somebody's building.</summary>
    public void SetWall(int x, int y, int wallId)
    {
        walls[(y * TileWidth) + x] = wallId;
    }

    private readonly Dictionary<int, int> walls = [];

    public void SetMovement(int dx, int dy, bool jump)
    {
        pendingDx = dx;
        pendingDy = dy;
        pendingJump = jump;
        LastRequestedDX = dx;
        LastRequestedDY = dy;

        // Jump statistics. A walker that flicks the button on and off every frame has a hold of one; a walker
        // that holds it for the whole rise has a hold of many, and the two look identical in a screenshot.
        if (jump)
        {
            JumpRequestTicks++;
            held++;
            if (held > MaxJumpHoldTicks)
            {
                MaxJumpHoldTicks = held;
            }
        }
        else
        {
            held = 0;
        }
    }

    /// <summary>Ticks the executor asked for a jump, and the longest unbroken hold of the whole run.</summary>
    public int JumpRequestTicks { get; private set; }

    public int MaxJumpHoldTicks { get; private set; }

    /// <summary>The most recent movement the executor asked for, kept for assertions.</summary>
    public int LastRequestedDX { get; private set; }

    public int LastRequestedDY { get; private set; }

    public void Log(string message) => logs.Add(message);

    /// <summary>Places the player without walking there, for tests that start inside a pocket.</summary>
    public void Teleport(double x, double y)
    {
        PlayerX = x;
        PlayerY = y;
        velocityY = 0;
        pendingDx = 0;
        pendingDy = 0;
        pendingJump = false;
    }

    /// <summary>Fills solid tiles, used to build walls and pockets around a charge.</summary>
    /// <summary>Every tile the executor actually removed, so a test can point at what it chewed.</summary>
    public readonly List<(int X, int Y)> DugTiles = [];

    public void FillSolid(int minX, int minY, int maxX, int maxY)
    {
        for (int y = Math.Max(0, minY); y <= Math.Min(TileHeight - 1, maxY); y++)
        {
            for (int x = Math.Max(0, minX); x <= Math.Min(TileWidth - 1, maxX); x++)
            {
                int index = (y * TileWidth) + x;
                solid[index] = true;
                types[index] = 1;
            }
        }
    }

    /// <summary>Called after every executor tick: applies the requested movement, then advances the world.</summary>
    public void Advance()
    {
        ApplyMovement(pendingDx, pendingDy, pendingJump);
        pendingDx = 0;
        pendingDy = 0;
        pendingJump = false;

        Tick++;

        if (PlayerDead)
        {
            if (Tick - deadSince >= RespawnDelay)
            {
                PlayerDead = false;
                PlayerLife = PlayerLifeMax;
                PlayerX = 20;
                PlayerY = FloorY - 1;
                velocityY = 0;
                deadSince = -1;
            }

            return;
        }

        if (PlayerCanDrown && PlayerLiquidKind != 0 && PlayerBreath <= 0)
        {
            PlayerLife -= 2;
            if (PlayerLife <= 0)
            {
                Kill();
                return;
            }
        }

        for (int i = projectiles.Count - 1; i >= 0; i--)
        {
            double[] projectile = projectiles[i];
            projectile[2] -= 1d;
            if (projectile[2] > 0d)
            {
                continue;
            }

            projectiles.RemoveAt(i);
            Explode((int)projectile[0], (int)projectile[1]);
        }
    }

    /// <summary>
    /// The executor's movement intent is applied here, after the fact, with simple physics.
    ///
    /// This deliberately copies the parts of the real game the walker has to live with: a step up of one or
    /// two tiles is climbed by walking (Collision.StepUp), a jump is an impulse that only fires on a fresh
    /// press while standing, and holding the button keeps the rise going rather than firing again. The first
    /// real-machine walk failed on exactly these three points, and a model without them cannot tell a working
    /// walker from a broken one.
    /// </summary>
    private void ApplyMovement(int dx, int dy, bool jump)
    {
        if (PlayerDead)
        {
            return;
        }

        if (dx != 0)
        {
            int sign = Math.Sign(dx);
            int px = (int)Math.Round(PlayerX);
            int py = (int)Math.Round(PlayerY);
            double nextX = PlayerX + (sign * Speed);
            int nx = (int)Math.Round(nextX);

            if (!IsSolid(nx, py))
            {
                PlayerX = nextX;
                PlayerVelocityX = sign * Speed;
            }
            else if (!IsSolid(nx, py - 1) && !IsSolid(nx, py - 2))
            {
                // One or two tiles of rise is a step, not a wall: the player walks up it. Two is generous,
                // but the walker is allowed to jump as well and this keeps the model from fighting it.
                PlayerX = nextX;
                PlayerY = py - 1;
                PlayerVelocityX = sign * Speed;
            }
            else
            {
                PlayerVelocityX = 0;
            }
        }
        else
        {
            PlayerVelocityX = 0;
        }

        int column = (int)Math.Round(PlayerX);
        bool onFloor = IsSolid(column, (int)Math.Round(PlayerY) + 1);

        if (jump && !jumpWasHeld && onFloor)
        {
            velocityY = -0.25d;
            jumpTicks = 0;
        }
        else if (jump && !onFloor && velocityY < 0 && jumpTicks < 24)
        {
            // Held while still rising: keep the rise at full speed. This is the variable-height jump.
            velocityY = -0.25d;
            jumpTicks++;
        }

        jumpWasHeld = jump;

        velocityY += 0.02d;
        if (velocityY > 0.3d)
        {
            velocityY = 0.3d;
        }

        double nextY = PlayerY + velocityY;
        if (velocityY > 0 && IsSolid(column, (int)Math.Round(nextY) + 1))
        {
            // Land on the row above the floor. Math.Floor here left the player one row short of the ground it
            // had just touched, so it never registered as standing and could never jump -- the stall that the
            // terrain dump showed as "player at y=21, floor at y=23".
            PlayerY = (int)Math.Round(nextY);
            velocityY = 0;
        }
        else if (velocityY < 0 && IsSolid(column, (int)Math.Round(nextY)))
        {
            velocityY = 0;
        }
        else
        {
            PlayerY = nextY;
        }

        // The real game pushes a player out of solid tiles. Without this the model leaves the player embedded
        // in the crater lip after a blast, where it is neither standing nor falling and the walker can never
        // make progress -- and there is nothing for the walker to fix there, the model itself is wrong.
        for (int guard = 0; guard < 6 && IsSolid(column, (int)Math.Round(PlayerY)); guard++)
        {
            PlayerY -= 1;
            velocityY = 0;
        }
    }

    private void Explode(int centreX, int centreY)
    {
        Explosions++;

        for (int dy = -Radius; dy <= Radius; dy++)
        {
            for (int dx = -Radius; dx <= Radius; dx++)
            {
                if (Math.Sqrt((dx * dx) + (dy * dy)) >= Radius)
                {
                    continue;
                }

                DigTile(centreX + dx, centreY + dy);
            }
        }

        // Dynamite self damage is a 250x250 px box, so about +-7.82 tiles, and it is not reduced by
        // distance. One hit is fatal for a 100 life player, which is what makes the test meaningful.
        double distance = Math.Max(Math.Abs(PlayerX - centreX), Math.Abs(PlayerY - centreY));
        if (distance < 7.82d && !PlayerImmune)
        {
            DamageTaken++;
            PlayerLife -= 250;
            if (PlayerLife <= 0)
            {
                Kill();
            }
        }
    }

    private void Kill()
    {
        PlayerDead = true;
        deadSince = Tick;
        tombstones.Add(((int)Math.Round(PlayerY) * TileWidth) + (int)Math.Round(PlayerX));
    }

    private readonly HashSet<int> tombstones = [];
}
