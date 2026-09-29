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
    public int CountItems(int itemId) => itemId == GameIds.Dynamite ? DynamiteCount : 0;

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
        if (solid[index] && BestPickPower < TilePathfinder.RequiredPickPower(types[index]))
        {
            // Unbreakable with this pickaxe: the real game would just make the swing do nothing.
            return;
        }

        if (!solid[index])
        {
            if (wasGravestone)
            {
                DigCount++;
            }

            return;
        }

        solid[index] = false;
        types[index] = 0;
        DugTiles.Add((x, y));
        DigCount++;
    }

    public void SetMovement(int dx, int dy, bool jump)
    {
        pendingDx = dx;
        pendingDy = dy;
        pendingJump = jump;
        LastRequestedDX = dx;
        LastRequestedDY = dy;
    }

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

    /// <summary>The executor's movement intent is applied here, after the fact, with simple physics.</summary>
    private void ApplyMovement(int dx, int dy, bool jump)
    {
        if (PlayerDead)
        {
            return;
        }

        if (jump)
        {
            velocityY = -0.25d;
        }

        if (dx != 0)
        {
            double nextX = PlayerX + (Math.Sign(dx) * Speed);
            if (!IsSolid((int)Math.Round(nextX), (int)Math.Round(PlayerY)))
            {
                PlayerX = nextX;
                PlayerVelocityX = Math.Sign(dx) * Speed;
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

        velocityY += 0.02d;
        if (velocityY > 0.3d)
        {
            velocityY = 0.3d;
        }

        double nextY = PlayerY + velocityY;
        if (velocityY > 0 && IsSolid((int)Math.Round(PlayerX), (int)Math.Round(nextY) + 1))
        {
            PlayerY = Math.Floor(nextY);
            velocityY = 0;
        }
        else if (velocityY < 0 && IsSolid((int)Math.Round(PlayerX), (int)Math.Round(nextY)))
        {
            velocityY = 0;
        }
        else
        {
            PlayerY = nextY;
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
