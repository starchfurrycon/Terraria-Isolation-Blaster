using System;

namespace ZhaDai.Automation
{
    /// <summary>What the executor wants to know about the world. Coordinates are in tiles.</summary>
    public interface IGameBridge
    {
        int TileWidth { get; }

        int TileHeight { get; }

        /// <summary>Monotonic tick counter, one per game update.</summary>
        long Tick { get; }

        double PlayerX { get; }

        double PlayerY { get; }

        double PlayerVelocityX { get; }

        double PlayerVelocityY { get; }

        int PlayerLife { get; }

        int PlayerLifeMax { get; }

        bool PlayerDead { get; }

        /// <summary>True while the player cannot take damage, e.g. the post hit immunity frames.</summary>
        bool PlayerImmune { get; }

        /// <summary>Liquid kind the player's feet are in: 0 none, 1 water, 2 lava, 3 honey.</summary>
        int PlayerLiquidKind { get; }

        bool PlayerLavaImmune { get; }

        /// <summary>True when losing breath is possible at all, e.g. no diving gear and no gills.</summary>
        bool PlayerCanDrown { get; }

        int PlayerBreath { get; }

        int PlayerBreathMax { get; }

        /// <summary>Distance in tiles to the closest hostile that could actually reach the player, or a big number.</summary>
        double NearestHostileDistance { get; }

        /// <summary>True when the tile is active and blocks movement.</summary>
        bool IsSolid(int x, int y);

        /// <summary>Tile type id, 0 when empty.</summary>
        int TileType(int x, int y);

        /// <summary>Liquid kind in the tile: 0 none, 1 water, 2 lava, 3 honey.</summary>
        int LiquidKind(int x, int y);

        /// <summary>True for a tombstone tile, which the executor digs after a death.</summary>
        bool IsGravestone(int x, int y);

        /// <summary>Tile id of the Dynamite item stack, or -1 when the player has none.</summary>
        int FindDynamiteSlot();

        /// <summary>How many of an item the player is carrying, across the whole inventory.</summary>
        int CountItems(int itemId);

        /// <summary>
        /// Best pickaxe power in the inventory, 0 with no pickaxe. The plan's dig list is only
        /// executable if this reaches the pick power the plan was computed with, so the run checks it
        /// before the first swing instead of standing in front of unbreakable rock later.
        /// </summary>
        int BestPickPower { get; }

        /// <summary>Selects the hotbar slot before a throw.</summary>
        void SelectSlot(int slot);

        /// <summary>Throws Dynamite toward a tile position. The runtime impl uses the item use path.</summary>
        void ThrowDynamiteAt(int tileX, int tileY);

        /// <summary>Mines one tile, used for tombstones and for digging through on the way.</summary>
        void DigTile(int x, int y);

        /// <summary>
        /// Places one block of the given item into an empty tile, used for plugging vine anchors. Returns
        /// false when the placement did not happen -- no such item in the inventory, a tile already there,
        /// or nothing adjacent to build against -- so the caller can count it as a hole rather than assume
        /// the vine is stopped.
        /// </summary>
        bool PlaceBlock(int x, int y, int itemId);

        /// <summary>Sets the movement controls for this tick. dx and dy are -1, 0 or 1.</summary>
        void SetMovement(int dx, int dy, bool jump);

        void Log(string message);
    }

    /// <summary>Why a charge was not fired. Reported so the player can act on it instead of guessing.</summary>
    public enum SkipReason
    {
        None = 0,
        NoDynamite,
        NoRetreatRoom,
        ExplosivesInBlast,
        LavaInBlastWithoutImmunity,
        StandingInLava,
        HostileTooClose,
        AboutToDrown,
        ManualStop,

        /// <summary>The player is not carrying what the plan needs; nothing was fired.</summary>
        NotEnoughSupplies,
    }
}
