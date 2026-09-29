using System;
using System.Collections.Generic;
using System.Reflection;

namespace ZhaDai.Runtime
{
    /// <summary>
    /// Resolves the game's types and members once, by name, and remembers what was not found.
    ///
    /// Nothing here is allowed to guess: if a member needed for a safety decision is missing, the
    /// bridge refuses to run instead of assuming. An executor that is wrong about what is solid, or
    /// about where lava is, is worse than one that does not start.
    /// </summary>
    internal sealed class GameReflection
    {
        private const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        private readonly List<string> problems = new List<string>();
        private readonly List<string> notes = new List<string>();

        private GameReflection()
        {
        }

        public Type Main { get; private set; }

        public Type Player { get; private set; }

        public Type Npc { get; private set; }

        public Type Tile { get; private set; }

        public Type Item { get; private set; }

        public Type SelectedItemState { get; private set; }

        public FieldInfo MainPlayers { get; private set; }

        public FieldInfo MainNpcs { get; private set; }

        public FieldInfo MainMyPlayer { get; private set; }

        public FieldInfo MainTiles { get; private set; }

        public FieldInfo MainScreenPosition { get; private set; }

        public FieldInfo MainMouseX { get; private set; }

        public FieldInfo MainMouseY { get; private set; }

        public FieldInfo MainTileSolid { get; private set; }

        public FieldInfo MainTileSolidTop { get; private set; }

        public PropertyInfo MainGameUpdateCount { get; private set; }

        public FieldInfo PlayerActive { get; private set; }

        public FieldInfo PlayerDead { get; private set; }

        public FieldInfo PlayerImmune { get; private set; }

        public FieldInfo PlayerStatLife { get; private set; }

        public FieldInfo PlayerStatLifeMax { get; private set; }

        public FieldInfo PlayerBreath { get; private set; }

        public FieldInfo PlayerBreathMax { get; private set; }

        public FieldInfo PlayerWet { get; private set; }

        public FieldInfo PlayerLavaWet { get; private set; }

        public FieldInfo PlayerHoneyWet { get; private set; }

        public FieldInfo PlayerLavaImmune { get; private set; }

        public FieldInfo PlayerControlLeft { get; private set; }

        public FieldInfo PlayerControlRight { get; private set; }

        public FieldInfo PlayerControlUp { get; private set; }

        public FieldInfo PlayerControlDown { get; private set; }

        public FieldInfo PlayerControlJump { get; private set; }

        public FieldInfo PlayerControlUseItem { get; private set; }

        public FieldInfo PlayerOwnedProjectileCounts { get; private set; }

        public FieldInfo PlayerInventory { get; private set; }

        public FieldInfo PlayerSelectedItemState { get; private set; }

        public PropertyInfo PlayerCenter { get; private set; }

        public FieldInfo EntityPosition { get; private set; }

        public FieldInfo EntityVelocity { get; private set; }

        public FieldInfo EntityWhoAmI { get; private set; }

        public MethodInfo PlayerPickTile { get; private set; }

        public FieldInfo SelectedIndex { get; private set; }

        public FieldInfo ItemType { get; private set; }

        public FieldInfo ItemStack { get; private set; }

        /// <summary>Item.pick: the pickaxe power of an item, 0 for everything that is not a pickaxe.</summary>
        public FieldInfo ItemPick { get; private set; }

        public FieldInfo NpcActive { get; private set; }

        public FieldInfo NpcFriendly { get; private set; }

        public FieldInfo NpcLife { get; private set; }

        public FieldInfo NpcDamage { get; private set; }

        public PropertyInfo NpcCenter { get; private set; }

        public FieldInfo TileType { get; private set; }

        public FieldInfo TileLiquid { get; private set; }

        public MethodInfo TileActive { get; private set; }

        public MethodInfo TileActuated { get; private set; }

        public FieldInfo TileActuatedField { get; private set; }

        public MethodInfo TileLava { get; private set; }

        public MethodInfo TileHoney { get; private set; }

        public Type Vector2 { get; private set; }

        public FieldInfo Vector2X { get; private set; }

        public FieldInfo Vector2Y { get; private set; }

        /// <summary>Missing members that make automation unsafe; empty means the bridge may run.</summary>
        public IList<string> Problems
        {
            get { return problems; }
        }

        /// <summary>Missing optional members: a feature is degraded, nothing is unsafe.</summary>
        public IList<string> Notes
        {
            get { return notes; }
        }

        public bool IsUsable
        {
            get { return problems.Count == 0; }
        }

        /// <summary>True when a world is loaded and the tile grid exists.</summary>
        public bool HasWorld
        {
            get { return MainTiles != null && MainTiles.GetValue(null) != null; }
        }

        public static GameReflection Resolve(Assembly game)
        {
            GameReflection reflection = new GameReflection();

            reflection.Main = reflection.RequireType(game, "Terraria.Main");
            reflection.Player = reflection.RequireType(game, "Terraria.Player");
            reflection.Npc = reflection.RequireType(game, "Terraria.NPC");
            reflection.Tile = reflection.RequireType(game, "Terraria.Tile");
            reflection.Item = reflection.RequireType(game, "Terraria.Item");
            reflection.SelectedItemState = reflection.OptionalType(game, "Terraria.Player+SelectedItemState",
                "拿不到选中的物品栏格，就无法切换手上的物品。");

            reflection.MainPlayers = reflection.RequireField(reflection.Main, "player");
            reflection.MainNpcs = reflection.OptionalField(reflection.Main, "npc", "没有 NPC 列表就不做敌怪规避。");
            reflection.MainMyPlayer = reflection.OptionalField(reflection.Main, "myPlayer", "没有本地玩家下标就固定用 0 号玩家。");
            reflection.MainTiles = reflection.RequireField(reflection.Main, "tile");
            reflection.MainScreenPosition = reflection.OptionalField(reflection.Main, "screenPosition", "拿不到屏幕位置就无法瞄准。");
            reflection.MainMouseX = reflection.OptionalField(reflection.Main, "mouseX", "拿不到鼠标坐标就无法瞄准。");
            reflection.MainMouseY = reflection.OptionalField(reflection.Main, "mouseY", "拿不到鼠标坐标就无法瞄准。");
            reflection.MainTileSolid = reflection.RequireField(reflection.Main, "tileSolid");
            reflection.MainTileSolidTop = reflection.OptionalField(reflection.Main, "tileSolidTop", "平台会被当成实心。");
            reflection.MainGameUpdateCount = reflection.OptionalProperty(reflection.Main, "GameUpdateCount", "没有帧计数就用内部计数。");

            reflection.PlayerActive = reflection.OptionalField(reflection.Player, "active", "拿不到 active 就只按 dead 判断。");
            reflection.PlayerDead = reflection.RequireField(reflection.Player, "dead");
            reflection.PlayerImmune = reflection.OptionalField(reflection.Player, "immune", "拿不到无敌帧状态就无法判断起爆帧安全。");
            reflection.PlayerStatLife = reflection.OptionalField(reflection.Player, "statLife", "拿不到生命就只按 dead 判断。");
            reflection.PlayerStatLifeMax = reflection.OptionalField(reflection.Player, "statLifeMax", "拿不到生命上限。");
            reflection.PlayerBreath = reflection.OptionalField(reflection.Player, "breath", "拿不到氧气就不做溺水规避。");
            reflection.PlayerBreathMax = reflection.OptionalField(reflection.Player, "breathMax", "拿不到氧气上限。");
            reflection.PlayerWet = reflection.OptionalField(reflection.Player, "wet", "拿不到入水状态就不做溺水规避。");
            reflection.PlayerLavaWet = reflection.OptionalField(reflection.Player, "lavaWet", "拿不到入岩浆状态。");
            reflection.PlayerHoneyWet = reflection.OptionalField(reflection.Player, "honeyWet", "拿不到入蜂蜜状态。");
            reflection.PlayerLavaImmune = reflection.OptionalField(reflection.Player, "lavaImmune", "拿不到岩浆免疫就不敢在岩浆附近施工。");
            reflection.PlayerControlLeft = reflection.RequireField(reflection.Player, "controlLeft");
            reflection.PlayerControlRight = reflection.RequireField(reflection.Player, "controlRight");
            reflection.PlayerControlUp = reflection.RequireField(reflection.Player, "controlUp");
            reflection.PlayerControlDown = reflection.RequireField(reflection.Player, "controlDown");
            reflection.PlayerControlJump = reflection.RequireField(reflection.Player, "controlJump");
            reflection.PlayerControlUseItem = reflection.RequireField(reflection.Player, "controlUseItem");
            reflection.PlayerOwnedProjectileCounts = reflection.OptionalField(reflection.Player, "ownedProjectileCounts", "拿不到自己的弹幕数就无法确认雷管到底丢没丢出去。");
            reflection.PlayerInventory = reflection.RequireField(reflection.Player, "inventory");
            reflection.PlayerSelectedItemState = reflection.OptionalField(reflection.Player, "selectedItemState", "拿不到选中格就无法换手。");
            reflection.PlayerCenter = reflection.RequireProperty(reflection.Player, "Center");
            reflection.EntityPosition = reflection.RequireField(reflection.Player, "position");
            reflection.EntityVelocity = reflection.OptionalField(reflection.Player, "velocity", "拿不到速度。");
            reflection.EntityWhoAmI = reflection.OptionalField(reflection.Player, "whoAmI", "拿不到玩家下标。");
            reflection.PlayerPickTile = reflection.RequireMethod(reflection.Player, "PickTile",
                new[] { typeof(int), typeof(int), typeof(int), typeof(int) });

            reflection.SelectedIndex = reflection.OptionalField(reflection.SelectedItemState, "selected", "拿不到选中的格索引。");
            reflection.ItemType = reflection.RequireField(reflection.Item, "type");
            reflection.ItemStack = reflection.OptionalField(reflection.Item, "stack", "拿不到堆叠数量就无法判断雷管还剩几发。");
            reflection.ItemPick = reflection.OptionalField(reflection.Item, "pick", "拿不到镐力就无法在开工前盘点镐子够不够。");

            reflection.NpcActive = reflection.OptionalField(reflection.Npc, "active", "拿不到 NPC 激活状态就不做敌怪规避。");
            reflection.NpcFriendly = reflection.OptionalField(reflection.Npc, "friendly", "拿不到友方标记就把 NPC 一律当威胁。");
            reflection.NpcLife = reflection.OptionalField(reflection.Npc, "life", "拿不到 NPC 生命。");
            reflection.NpcDamage = reflection.OptionalField(reflection.Npc, "damage", "拿不到 NPC 伤害。");
            reflection.NpcCenter = reflection.OptionalProperty(reflection.Npc, "Center", "拿不到 NPC 位置就不做敌怪规避。");

            reflection.TileType = reflection.RequireField(reflection.Tile, "type");
            reflection.TileLiquid = reflection.RequireField(reflection.Tile, "liquid");
            reflection.TileActive = reflection.RequireMethod(reflection.Tile, "active", Type.EmptyTypes);

            // Actuated tiles are not exploded by dynamite and are walked through, so this one matters.
            // The shape differs between builds (field here), so both are looked for.
            reflection.TileActuatedField = reflection.Tile == null ? null : reflection.Tile.GetField("actuated", Any);
            reflection.TileActuated = reflection.TileActuatedField == null
                ? reflection.OptionalMethod(reflection.Tile, "actuated", Type.EmptyTypes, "拿不到虚化状态，可能把虚化物块当成能炸掉。")
                : null;
            if (reflection.TileActuatedField == null && reflection.TileActuated == null)
            {
                reflection.notes.Add("缺少虚化标记 Terraria.Tile.actuated：虚化的物块会被当成普通实心物块，走位会偏保守。");
            }
            reflection.TileLava = reflection.OptionalMethod(reflection.Tile, "lava", Type.EmptyTypes, "拿不到液体种类就不敢在液体附近施工。");
            reflection.TileHoney = reflection.OptionalMethod(reflection.Tile, "honey", Type.EmptyTypes, "拿不到蜂蜜标记。");

            return reflection;
        }

        /// <summary>Reads a Vector2's components without referencing XNA at compile time.</summary>
        public void BindVector2(object sample)
        {
            if (sample == null || Vector2 != null)
            {
                return;
            }

            Vector2 = sample.GetType();
            Vector2X = Vector2.GetField("X", Any);
            Vector2Y = Vector2.GetField("Y", Any);
            if (Vector2X == null || Vector2Y == null)
            {
                problems.Add("向量类型 " + Vector2.FullName + " 上没有 X/Y 字段。");
                Vector2 = null;
            }
        }

        public float VectorX(object value)
        {
            return value == null ? 0f : Convert.ToSingle(Vector2X.GetValue(value));
        }

        public float VectorY(object value)
        {
            return value == null ? 0f : Convert.ToSingle(Vector2Y.GetValue(value));
        }

        private Type RequireType(Assembly game, string name)
        {
            Type type = game.GetType(name, false);
            if (type == null)
            {
                problems.Add("找不到类型 " + name + "。");
            }

            return type;
        }

        private Type OptionalType(Assembly game, string name, string note)
        {
            Type type = game.GetType(name, false);
            if (type == null)
            {
                notes.Add("缺少类型 " + name + "：" + note);
            }

            return type;
        }

        private FieldInfo RequireField(Type type, string name)
        {
            FieldInfo field = type == null ? null : type.GetField(name, Any);
            if (field == null)
            {
                problems.Add("找不到字段 " + type?.FullName + "." + name + "。");
            }

            return field;
        }

        private FieldInfo OptionalField(Type type, string name, string note)
        {
            FieldInfo field = type == null ? null : type.GetField(name, Any);
            if (field == null)
            {
                notes.Add("缺少字段 " + type?.FullName + "." + name + "：" + note);
            }

            return field;
        }

        private PropertyInfo RequireProperty(Type type, string name)
        {
            PropertyInfo property = type == null ? null : type.GetProperty(name, Any);
            if (property == null || !property.CanRead)
            {
                problems.Add("找不到可读属性 " + type?.FullName + "." + name + "。");
                return null;
            }

            return property;
        }

        private PropertyInfo OptionalProperty(Type type, string name, string note)
        {
            PropertyInfo property = type == null ? null : type.GetProperty(name, Any);
            if (property == null || !property.CanRead)
            {
                notes.Add("缺少可读属性 " + type?.FullName + "." + name + "：" + note);
                return null;
            }

            return property;
        }

        private MethodInfo RequireMethod(Type type, string name, Type[] parameters)
        {
            MethodInfo method = type == null ? null : type.GetMethod(name, Any, null, parameters, null);
            if (method == null)
            {
                problems.Add("找不到方法 " + type?.FullName + "." + name + "(" + parameters.Length + " 个参数)。");
            }

            return method;
        }

        private MethodInfo OptionalMethod(Type type, string name, Type[] parameters, string note)
        {
            MethodInfo method = type == null ? null : type.GetMethod(name, Any, null, parameters, null);
            if (method == null)
            {
                notes.Add("缺少方法 " + type?.FullName + "." + name + "：" + note);
            }

            return method;
        }
    }
}
