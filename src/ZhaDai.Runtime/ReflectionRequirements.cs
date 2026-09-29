using System.Collections.Generic;

namespace ZhaDai.Runtime
{
    /// <summary>How a game member is reached. Field and property are different reflection paths.</summary>
    public enum MemberKind
    {
        Field,
        Property,
        Method
    }

    /// <summary>
    /// Every Terraria member the runtime touches, declared in exactly one place.
    ///
    /// The patcher's offline verifier reads this list straight out of the built plugin and checks each
    /// entry against the real Terraria.exe metadata, so "the plugin compiles" and "the names exist in
    /// this build of the game" cannot drift apart. Inherited members need
    /// <c>FlattenHierarchy</c>: <c>Player.position</c>, <c>Player.Center</c> and <c>Entity.whoAmI</c>
    /// all live on <c>Terraria.Entity</c>, so looking only at <c>Player</c> would wrongly report them
    /// missing.
    /// </summary>
    public static class ReflectionRequirements
    {
        public sealed class Requirement
        {
            public string Type;
            public string Member;
            public MemberKind Kind;
            public bool Required;
            public string[] Parameters;
            public string Note;

            public string Display
            {
                get { return Type + "." + Member + "（" + KindName(Kind) + "）"; }
            }

            public static string KindName(MemberKind kind)
            {
                switch (kind)
                {
                    case MemberKind.Field: return "字段";
                    case MemberKind.Property: return "属性";
                    default: return "方法";
                }
            }
        }

        public static IReadOnlyList<Requirement> All
        {
            get { return Requirements; }
        }

        private static readonly Requirement[] Requirements =
        {
            // ---------------------------------------------------------------- Main（静态）
            Field("Terraria.Main", "player", true, "全部玩家"),
            Field("Terraria.Main", "npc", true, "全部 NPC"),
            Field("Terraria.Main", "myPlayer", true, "本地玩家下标"),
            Field("Terraria.Main", "tile", true, "物块网格"),
            Field("Terraria.Main", "screenPosition", true, "世界与屏幕坐标换算"),
            Field("Terraria.Main", "mouseX", false, "瞄准用的鼠标 X"),
            Field("Terraria.Main", "mouseY", false, "瞄准用的鼠标 Y"),
            Field("Terraria.Main", "tileSolid", true, "哪些物块是实心的"),
            Field("Terraria.Main", "keyState", false, "按键状态（按键接管用；窗口失焦时游戏自己会清空它）"),
        Field("Terraria.Main", "tileSolidTop", false, "平台这类可以从下方穿过、上面能站的物块"),
            Field("Terraria.Main", "tileNoFail", false, "不可破坏的物块（地牢砖通关前）"),
            Property("Terraria.Main", "GameUpdateCount", false, "逻辑帧计数，用于热键节流"),

            // ---------------------------------------------------------------- Player（含继承来的）
            Field("Terraria.Player", "active", true, "玩家是否激活"),
            Field("Terraria.Player", "dead", true, "是否死亡"),
            Field("Terraria.Player", "immune", false, "是否处于无敌帧"),
            Field("Terraria.Player", "statLife", true, "当前生命"),
            Field("Terraria.Player", "statLifeMax", true, "生命上限"),
            Field("Terraria.Player", "breath", false, "剩余氧气"),
            Field("Terraria.Player", "breathMax", false, "氧气上限"),
            Field("Terraria.Player", "wet", false, "是否碰到水"),
            Field("Terraria.Player", "lavaWet", false, "是否碰到岩浆"),
            Field("Terraria.Player", "honeyWet", false, "是否碰到蜂蜜"),
            Field("Terraria.Player", "lavaImmune", false, "是否免疫岩浆"),
            Field("Terraria.Player", "controlLeft", true, "向左"),
            Field("Terraria.Player", "controlRight", true, "向右"),
            Field("Terraria.Player", "controlUp", true, "向上"),
            Field("Terraria.Player", "controlDown", true, "向下"),
            Field("Terraria.Player", "controlJump", true, "跳跃"),
            Field("Terraria.Player", "controlUseItem", true, "使用手持物品"),
            Field("Terraria.Player", "ownedProjectileCounts", false, "自己拥有的弹幕数量，用来判断雷管有没有丢出去"),
            Field("Terraria.Player", "inventory", true, "物品栏"),
            Field("Terraria.Player", "selectedItemState", true, "选中的物品栏格"),
            Property("Terraria.Player", "Center", true, "玩家中心（定义在基类 Entity 上）"),
            Field("Terraria.Entity", "position", true, "位置（定义在基类上）"),
            Field("Terraria.Entity", "velocity", false, "速度（定义在基类上）"),
            Field("Terraria.Entity", "width", false, "碰撞盒宽（定义在基类上）"),
            Field("Terraria.Entity", "height", false, "碰撞盒高（定义在基类上）"),
            Field("Terraria.Entity", "whoAmI", false, "下标（定义在基类上）"),
            Method("Terraria.Player", "PickTile", true, "挖物块", "System.Int32", "System.Int32", "System.Int32", "System.Int32"),

            // ---------------------------------------------------------------- 选中的格子
            Field("Terraria.Player+SelectedItemState", "selected", true, "当前选中的格索引"),

            // ---------------------------------------------------------------- Item
            Field("Terraria.Item", "type", true, "物品 ID"),
            Field("Terraria.Item", "stack", true, "堆叠数量"),
            Field("Terraria.Item", "pick", true, "镐力（镐子）、0（非镐子）"),
        Field("Terraria.Item", "createTile", true, "物块物品对应的物块 id（封堵用）"),
        Method("Terraria.WorldGen", "PlaceTile", true, "放物块（封堵用）"),

            // ---------------------------------------------------------------- NPC
            Field("Terraria.NPC", "active", true, "NPC 是否激活"),
            Field("Terraria.NPC", "friendly", false, "友方则不是威胁"),
            Field("Terraria.NPC", "life", false, "剩余生命"),
            Field("Terraria.NPC", "damage", false, "接触伤害"),
            Property("Terraria.NPC", "Center", false, "NPC 中心（定义在基类 Entity 上）"),

            // ---------------------------------------------------------------- Tile
            Field("Terraria.Tile", "type", true, "物块类型"),
            Field("Terraria.Tile", "liquid", true, "液体量"),
            Field("Terraria.Tile", "wall", false, "墙壁类型"),
            Method("Terraria.Tile", "active", true, "物块是否激活"),
            // Tile.actuated is deliberately NOT declared: this build keeps it non-public, so the offline
            // verifier (which only sees public members) would report a false gap. The bridge still tries
            // to bind it at run time and degrades safely when it cannot.
            Method("Terraria.Tile", "lava", false, "这格的液体是不是岩浆"),
            Method("Terraria.Tile", "honey", false, "这格的液体是不是蜂蜜"),
        };

        private static Requirement Field(string type, string member, bool required, string note)
        {
            return new Requirement
            {
                Type = type,
                Member = member,
                Kind = MemberKind.Field,
                Required = required,
                Parameters = new string[0],
                Note = note
            };
        }

        private static Requirement Property(string type, string member, bool required, string note)
        {
            return new Requirement
            {
                Type = type,
                Member = member,
                Kind = MemberKind.Property,
                Required = required,
                Parameters = new string[0],
                Note = note
            };
        }

        private static Requirement Method(string type, string member, bool required, string note, params string[] parameters)
        {
            return new Requirement
            {
                Type = type,
                Member = member,
                Kind = MemberKind.Method,
                Required = required,
                Parameters = parameters ?? new string[0],
                Note = note
            };
        }
    }
}
