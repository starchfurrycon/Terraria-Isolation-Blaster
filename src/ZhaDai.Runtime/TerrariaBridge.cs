using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using ZhaDai.Automation;

namespace ZhaDai.Runtime
{
    /// <summary>
    /// Implements <see cref="IGameBridge"/> against the live game through reflection only.
    ///
    /// Everything is in tiles, one tile being 16 pixels, because the plan is in tiles. The bridge
    /// never caches world state between frames: the plan was computed from a save that the player may
    /// have changed since, so every decision reads the tiles as they are right now.
    /// </summary>
    internal sealed class TerrariaBridge : IGameBridge
    {
        private const float PixelsPerTile = GameIds.PixelsPerTile;
        private const int TombstoneTile = GameIds.Tombstone;
        private const int PickPower = 200;

        private readonly GameReflection reflection;
        private readonly List<string> log = new List<string>();

        private object[] players;
        private object[] npcs;
        private bool[] solid;
        private bool[] solidTop;
        private object player;
        private Array tiles;
        private long frame;
        private bool keepUsingItem;

        internal TerrariaBridge(GameReflection reflection)
        {
            this.reflection = reflection;
        }

        /// <summary>Lines the executor asked to be logged, drained by the host into runtime.log.</summary>
        public IList<string> Messages
        {
            get { return log; }
        }

        public int TileWidth
        {
            get { return tiles == null ? 0 : tiles.GetLength(0); }
        }

        public int TileHeight
        {
            get { return tiles == null ? 0 : tiles.GetLength(1); }
        }

        public long Tick
        {
            get
            {
                if (reflection.MainGameUpdateCount != null)
                {
                    return Convert.ToInt64(reflection.MainGameUpdateCount.GetValue(null, null));
                }

                return frame;
            }
        }

        public double PlayerX
        {
            get { return CenterX() / PixelsPerTile; }
        }

        public double PlayerY
        {
            get { return CenterY() / PixelsPerTile; }
        }

        public double PlayerVelocityX
        {
            get
            {
                object velocity = PlayerField(reflection.EntityVelocity);
                return velocity == null ? 0d : reflection.VectorX(velocity) / PixelsPerTile;
            }
        }

        public double PlayerVelocityY
        {
            get
            {
                object velocity = PlayerField(reflection.EntityVelocity);
                return velocity == null ? 0d : reflection.VectorY(velocity) / PixelsPerTile;
            }
        }

        public int PlayerLife
        {
            get { return ReadInt(reflection.PlayerStatLife, 100); }
        }

        public int PlayerLifeMax
        {
            get { return ReadInt(reflection.PlayerStatLifeMax, 100); }
        }

        public bool PlayerDead
        {
            get
            {
                if (player == null)
                {
                    return true;
                }

                if (ReadBool(reflection.PlayerDead, false))
                {
                    return true;
                }

                return reflection.PlayerActive != null && !ReadBool(reflection.PlayerActive, true);
            }
        }

        public bool PlayerImmune
        {
            get { return ReadBool(reflection.PlayerImmune, false); }
        }

        public int PlayerLiquidKind
        {
            get
            {
                if (ReadBool(reflection.PlayerLavaWet, false))
                {
                    return 2;
                }

                if (ReadBool(reflection.PlayerHoneyWet, false))
                {
                    return 3;
                }

                return ReadBool(reflection.PlayerWet, false) ? 1 : 0;
            }
        }

        public bool PlayerLavaImmune
        {
            get { return ReadBool(reflection.PlayerLavaImmune, false); }
        }

        /// <summary>
        /// Drowning is only modelled when the game actually exposes breath; without it the executor
        /// has no way to see the danger coming, so it simply does not pretend to.
        /// </summary>
        public bool PlayerCanDrown
        {
            get { return reflection.PlayerBreath != null && reflection.PlayerBreathMax != null; }
        }

        public int PlayerBreath
        {
            get { return ReadInt(reflection.PlayerBreath, 200); }
        }

        public int PlayerBreathMax
        {
            get { return ReadInt(reflection.PlayerBreathMax, 200); }
        }

        public double NearestHostileDistance
        {
            get
            {
                if (npcs == null || reflection.NpcCenter == null)
                {
                    return double.MaxValue / 2d;
                }

                double centreX = CenterX();
                double centreY = CenterY();
                double best = double.MaxValue / 2d;

                for (int i = 0; i < npcs.Length; i++)
                {
                    object npc = npcs[i];
                    if (npc == null)
                    {
                        continue;
                    }

                    if (reflection.NpcActive != null && !Convert.ToBoolean(reflection.NpcActive.GetValue(npc)))
                    {
                        continue;
                    }

                    // friendly defaults to false when the field is absent, so anything unknown counts
                    // as a threat. That makes the executor wait, which is the safe direction to fail.
                    if (reflection.NpcFriendly != null && Convert.ToBoolean(reflection.NpcFriendly.GetValue(npc)))
                    {
                        continue;
                    }

                    if (reflection.NpcLife != null && Convert.ToInt32(reflection.NpcLife.GetValue(npc)) <= 0)
                    {
                        continue;
                    }

                    object centre = reflection.NpcCenter.GetValue(npc, null);
                    if (centre == null)
                    {
                        continue;
                    }

                    double dx = reflection.VectorX(centre) - centreX;
                    double dy = reflection.VectorY(centre) - centreY;
                    double distance = Math.Sqrt((dx * dx) + (dy * dy)) / PixelsPerTile;
                    if (distance < best)
                    {
                        best = distance;
                    }
                }

                return best;
            }
        }

        public void Refresh()
        {
            players = reflection.MainPlayers == null ? null : reflection.MainPlayers.GetValue(null) as object[];
            npcs = reflection.MainNpcs == null ? null : reflection.MainNpcs.GetValue(null) as object[];
            solid = reflection.MainTileSolid == null ? null : reflection.MainTileSolid.GetValue(null) as bool[];
            solidTop = reflection.MainTileSolidTop == null ? null : reflection.MainTileSolidTop.GetValue(null) as bool[];
            tiles = reflection.MainTiles == null ? null : reflection.MainTiles.GetValue(null) as Array;

            int index = reflection.MainMyPlayer == null ? 0 : Convert.ToInt32(reflection.MainMyPlayer.GetValue(null));
            if (players != null && index >= 0 && index < players.Length)
            {
                player = players[index];
            }

            if (player != null && reflection.Vector2 == null)
            {
                reflection.BindVector2(reflection.PlayerCenter.GetValue(player, null));
            }
        }

        public void AdvanceFrame()
        {
            frame++;
        }

        public bool IsSolid(int x, int y)
        {
            object tile = TileAt(x, y);
            if (tile == null)
            {
                // Outside the world counts as solid: it stops movement instead of walking off the map.
                return true;
            }

            if (!Active(tile) || IsActuated(tile))
            {
                return false;
            }

            int type = TypeOf(tile);
            if (solid == null || type < 0 || type >= solid.Length)
            {
                return true;
            }

            if (!solid[type])
            {
                return false;
            }

            // Platforms are solid from above only; treating them as solid would block walking, while
            // treating them as air only risks a fall, and the executor already refuses long drops.
            if (solidTop != null && type < solidTop.Length && solidTop[type])
            {
                return false;
            }

            return true;
        }

        public int TileType(int x, int y)
        {
            object tile = TileAt(x, y);
            return tile == null || !Active(tile) ? 0 : TypeOf(tile);
        }

        public int LiquidKind(int x, int y)
        {
            object tile = TileAt(x, y);
            if (tile == null || reflection.TileLiquid == null)
            {
                return 0;
            }

            if (Convert.ToInt32(reflection.TileLiquid.GetValue(tile)) <= 0)
            {
                return 0;
            }

            if (reflection.TileLava != null && Convert.ToBoolean(reflection.TileLava.Invoke(tile, null)))
            {
                return 2;
            }

            if (reflection.TileHoney != null && Convert.ToBoolean(reflection.TileHoney.Invoke(tile, null)))
            {
                return 3;
            }

            return 1;
        }

        public bool IsGravestone(int x, int y)
        {
            return TileType(x, y) == TombstoneTile;
        }

        public int FindDynamiteSlot()
        {
            object[] inventory = PlayerField(reflection.PlayerInventory) as object[];
            if (inventory == null || reflection.ItemType == null)
            {
                return -1;
            }

            for (int i = 0; i < inventory.Length && i < 59; i++)
            {
                object item = inventory[i];
                if (item == null)
                {
                    continue;
                }

                if (Convert.ToInt32(reflection.ItemType.GetValue(item)) != GameIds.Dynamite)
                {
                    continue;
                }

                if (reflection.ItemStack != null && Convert.ToInt32(reflection.ItemStack.GetValue(item)) <= 0)
                {
                    continue;
                }

                return i;
            }

            return -1;
        }

        /// <summary>
        /// Total count of an item across the inventory. The supply audit uses this instead of the slot
        /// search so a run that needs more Dynamite than fits in one stack is still measured honestly.
        /// </summary>
        public int CountItems(int itemId)
        {
            object[] inventory = PlayerField(reflection.PlayerInventory) as object[];
            if (inventory == null || reflection.ItemType == null)
            {
                return 0;
            }

            int total = 0;
            for (int i = 0; i < inventory.Length; i++)
            {
                object item = inventory[i];
                if (item == null)
                {
                    continue;
                }

                if (Convert.ToInt32(reflection.ItemType.GetValue(item)) != itemId)
                {
                    continue;
                }

                total += reflection.ItemStack != null ? Convert.ToInt32(reflection.ItemStack.GetValue(item)) : 1;
            }

            return total;
        }

        /// <summary>Best pickaxe power in the inventory; 0 when there is no pickaxe at all.</summary>
        public int BestPickPower
        {
            get
            {
                object[] inventory = PlayerField(reflection.PlayerInventory) as object[];
                if (inventory == null || reflection.ItemPick == null)
                {
                    return 0;
                }

                int best = 0;
                for (int i = 0; i < inventory.Length; i++)
                {
                    object item = inventory[i];
                    if (item == null)
                    {
                        continue;
                    }

                    int pick = Convert.ToInt32(reflection.ItemPick.GetValue(item));
                    if (pick > best)
                    {
                        best = pick;
                    }
                }

                return best;
            }
        }

        public void SelectSlot(int slot)        {
            if (player == null || reflection.PlayerSelectedItemState == null || reflection.SelectedIndex == null)
            {
                return;
            }

            object state = reflection.PlayerSelectedItemState.GetValue(player);
            if (state == null)
            {
                return;
            }

            reflection.SelectedIndex.SetValue(state, slot);
            reflection.PlayerSelectedItemState.SetValue(player, state);
        }

        /// <summary>
        /// Aims at the target tile by writing the mouse position for this frame and holding the use
        /// button. The game's own item check then throws the dynamite, exactly as a player would.
        /// </summary>
        public void ThrowDynamiteAt(int tileX, int tileY)
        {
            if (player == null || reflection.PlayerControlUseItem == null)
            {
                return;
            }

            if (reflection.MainScreenPosition != null && reflection.MainMouseX != null && reflection.MainMouseY != null)
            {
                object screen = reflection.MainScreenPosition.GetValue(null);
                if (screen != null)
                {
                    int mouseX = (int)Math.Round(((tileX * PixelsPerTile) + (PixelsPerTile / 2f)) - reflection.VectorX(screen));
                    int mouseY = (int)Math.Round(((tileY * PixelsPerTile) + (PixelsPerTile / 2f)) - reflection.VectorY(screen));
                    reflection.MainMouseX.SetValue(null, mouseX);
                    reflection.MainMouseY.SetValue(null, mouseY);
                }
            }

            reflection.PlayerControlUseItem.SetValue(player, true);
            keepUsingItem = true;
        }

        public void DigTile(int x, int y)
        {
            if (player == null || reflection.PlayerPickTile == null)
            {
                return;
            }

            reflection.PlayerPickTile.Invoke(player, new object[] { x, y, PickPower, 1 });
        }

        public void SetMovement(int dx, int dy, bool jump)
        {
            if (player == null)
            {
                return;
            }

            Set(reflection.PlayerControlLeft, dx < 0);
            Set(reflection.PlayerControlRight, dx > 0);
            Set(reflection.PlayerControlUp, dy < 0);
            Set(reflection.PlayerControlDown, dy > 0);
            Set(reflection.PlayerControlJump, jump && dy <= 0);

            // The use button is held for exactly the frame the throw happens in; releasing it here
            // stops the executor from emptying the whole stack at one spot.
            if (!keepUsingItem)
            {
                Set(reflection.PlayerControlUseItem, false);
            }

            keepUsingItem = false;
        }

        public void Log(string message)
        {
            if (log.Count > 400)
            {
                log.RemoveRange(0, 200);
            }

            log.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + message);
        }

        private object TileAt(int x, int y)
        {
            if (tiles == null || x < 0 || y < 0 || x >= tiles.GetLength(0) || y >= tiles.GetLength(1))
            {
                return null;
            }

            return tiles.GetValue(x, y);
        }

        private int TypeOf(object tile)
        {
            return Convert.ToInt32(reflection.TileType.GetValue(tile));
        }

        private bool Active(object tile)
        {
            return Convert.ToBoolean(reflection.TileActive.Invoke(tile, null));
        }

        private bool IsActuated(object tile)
        {
            if (reflection.TileActuatedField != null)
            {
                return Convert.ToBoolean(reflection.TileActuatedField.GetValue(tile));
            }

            if (reflection.TileActuated == null)
            {
                return false;
            }

            return Convert.ToBoolean(reflection.TileActuated.Invoke(tile, null));
        }

        private object PlayerField(FieldInfo field)
        {
            return field == null || player == null ? null : field.GetValue(player);
        }

        private float CenterX()
        {
            object centre = player == null ? null : reflection.PlayerCenter.GetValue(player, null);
            return centre == null ? 0f : reflection.VectorX(centre);
        }

        private float CenterY()
        {
            object centre = player == null ? null : reflection.PlayerCenter.GetValue(player, null);
            return centre == null ? 0f : reflection.VectorY(centre);
        }

        private int ReadInt(FieldInfo field, int fallback)
        {
            object value = PlayerField(field);
            return value == null ? fallback : Convert.ToInt32(value);
        }

        private bool ReadBool(FieldInfo field, bool fallback)
        {
            object value = PlayerField(field);
            return value == null ? fallback : Convert.ToBoolean(value);
        }

        private void Set(FieldInfo field, bool value)
        {
            if (field != null && player != null)
            {
                field.SetValue(player, value);
            }
        }
    }
}
