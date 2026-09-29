namespace ZhaDai.Automation
{
    /// <summary>
    /// The handful of game ids the runtime needs. They live here, next to the executor, because the
    /// runtime is compiled for .NET Framework and cannot reference the planner assembly; the test
    /// suite asserts that these agree with the planner's own table.
    /// </summary>
    public static class GameIds
    {
        /// <summary><c>ItemID.Dynamite</c>.</summary>
        public const int Dynamite = 167;

        /// <summary><c>ProjectileID.Dynamite</c>; the planner uses it to count live charges.</summary>
        public const int DynamiteProjectile = 29;

        /// <summary><c>TileID.Tombstones</c>: every gravestone style shares this id.</summary>
        public const int Tombstone = 85;

        /// <summary>Pixels in one tile.</summary>
        public const float PixelsPerTile = 16f;
    }
}
