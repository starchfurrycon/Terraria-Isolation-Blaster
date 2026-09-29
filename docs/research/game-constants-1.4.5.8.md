# 泰拉瑞亚 1.4.5.8 反编译源码权威常量提取（自动炸隔离带 规划器用）

- 源码树：`D:\personal tasks\modding\听雨的声音\_recon\terraria-src\`
- 下文所有 `file:line` 均相对该源码树根目录。
- 判定规则：凡本次未在源码中实际读到的内容一律写「**未找到**」；仅凭文件名/常识推断的内容写「**不确定**」。

---

## 0. 规划器最需要的数字速查表

| 项目 | 数值 | 依据 |
|---|---|---|
| `ItemID.Dynamite` | **167** | `Terraria.ID\ItemID.cs:1880` |
| `ItemID.Bomb` / `ItemID.StickyBomb` | **166** / **235** | `ItemID.cs:1878` / `ItemID.cs:2016` |
| `ItemID.Explosives` / `ItemID.StickyDynamite` | **580** / **2896** | `ItemID.cs:2706` / `ItemID.cs:7338` |
| 雷管(167) 发射的弹幕 | `ProjectileID.Dynamite` = **29** | `Item.cs:3425` `shoot = 29;` + `ProjectileID.cs:510` |
| **雷管引信时长** | **300 tick = 5 秒** | `Projectile.cs:10996-10999` |
| 炸弹(166) 引信 | 180 tick（3 秒） | `Projectile.cs:10976-10979` |
| Explosives(580) 引信 | 300 tick | `Projectile.cs:10996-10999`（type 108 在 `10996` 分支）|
| **雷管爆炸半径（瓦片）** | **7** | `Projectile.cs:79045-79048` `num6 = 7;` |
| 爆炸形状 | **圆（欧氏距离）**，判定 `sqrt(dx²+dy²) < radius` | `Projectile.cs:80467-80472` |
| 炸弹爆炸半径 | 4 | `Projectile.cs:79041-79044` |
| Explosives(580) 爆炸半径 | **10** | `Projectile.cs:79066-79069` |
| TNT 桶（物品 5327 / 瓦片 654） 爆炸半径 | **10** | `Projectile.cs:79070-79073` |
| 超级炸弹(1086/1087) 半径 | 9（且可炸困难矿） | `Projectile.cs:79049-79053` |
| **雷管对玩家自伤** | **250 伤害 / 击退 10 / 判定框 250×250 px**（≈±7.81 tile） | `Projectile.cs:47652-47656` |
| 自伤公式 | `Main.DamageVar(250, -luck)` ⇒ **250 × [0.85, 1.15] 随机**（±15%）；下限 1 | `Projectile.cs:15124`；`Main.cs:67147-67173`；`Player.cs:38681-38684` |
| 受伤后无敌帧 | **40 tick**（长无敌 80；PVP 8） | `Player.cs:38689-38693` |
| **爆炸免疫瓦片** | 见 §A5（核心：`Main.tileDungeon`、`TileID.Sets.BasicChest`、`wall == 350`、祭坛 26、Lihzahrd 226/237 等） | `Projectile.cs:80389-80458` |
| **感染硬扩散 reach** | **±3（Chebyshev）** | `WorldGen.cs:70252-70253`、`70341-70342`、`70432-70433` |
| 草地互相感染 reach | **±1（3×3）** | `WorldGen.cs:75240-75242` |
| 墙扩散 reach | **±2** | `WorldGen.cs:75505-75506` |
| **建议隔离带空距 N** | **≥4 格**（硬扩散单跳 3；若隔离带内全为空气则任意窄皆可阻断） | 推导见 §B11 |
| 熔岩伤害 | **80 / 次**（remix 200），周期 = 无敌帧冷却；附加 420 tick 着火 | `Player.cs:27899-27936` |
| 岩浆耐受 | `lavaMax`（Lava Charm +420），`lavaTime` 消耗 | `Player.cs:15307-15310`、`27899-27947` |
| 溺水 | `breath=200`，每 **7 tick** −1，呼吸耗尽后每 7 tick **2 伤害** | `Player.cs:1519-1521`、`23450-23470`、`3855-3868` |
| 摔落伤害 | 阈值 **25 tile**（+`extraFall`），`(落差−25)×10` 伤害 | `Player.cs:25511-25574` |
| **墓碑/坟墓瓦片** | 全部是 `TileID.Tombstones` = **85**，2×2 | `TileID.cs:607`、`TileObjectData.cs:3948-3949` |
| 复活计时 | **600 tick（10 秒）**，专家 ×1.5 = 900 | `Player.cs:39378-39411` |

---

## A. 雷管 / Dynamite

### A1. 物品 ID 与中文名

| 名称 | 常量 | 值 | 依据 |
|---|---|---|---|
| Bomb | `ItemID.Bomb` | 166 | `Terraria.ID\ItemID.cs:1878` |
| Dynamite（中文「雷管」） | `ItemID.Dynamite` | **167** | `ItemID.cs:1880` |
| StickyBomb | `ItemID.StickyBomb` | 235 | `ItemID.cs:2016` |
| Explosives | `ItemID.Explosives` | 580 | `ItemID.cs:2706` |
| StickyDynamite | `ItemID.StickyDynamite` | 2896 | `ItemID.cs:7338` |
| **SuperBomb** | `ItemID.SuperBomb` | **5594** | `ItemID.cs:12734` |
| **TNTBarrel** | `ItemID.TNTBarrel` | **5327** | `ItemID.cs:12200` |
| BouncyBomb / BouncyDynamite | | 3115 / 3547 | `ItemID.cs:7776` / `ItemID.cs:8640` |
| BombFish / ScarabBomb | | 3196 / 4423 | `ItemID.cs:7938` / `ItemID.cs:10392` |
| Wet/Lava/Honey/Dry Bomb | | 4824 / 4825 / 4826 / 4827 | `ItemID.cs:11194-11200` |
| DirtBomb | | 4908 | `ItemID.cs:11362` |

「雷管」确实是**投掷式带引信爆炸物**，依据 `Terraria\Item.cs:3422-3436`：

```csharp
case 167:
    useStyle = 1;
    shootSpeed = 4f;
    shoot = 29;            // → ProjectileID.Dynamite
    consumable = true;
    UseSound = SoundID.Item1;
    useAnimation = 40;
    useTime = 40;
    noUseGraphic = true;
    noMelee = true;
```

弹幕 `ProjectileID.Dynamite = 29`（`Terraria.ID\ProjectileID.cs:510`），走 `aiStyle = 16`（炸弹 AI），带引信——判据是它被登记进 `ProjectileID.Sets.IsABombWithFuse`：

```csharp
// Terraria.ID\ProjectileID.cs:277
public static bool[] IsABombWithFuse = Factory.CreateBoolSet(28, 37, 516, 519, 910, 911,
    1086, 1087, 906, 905, 904, 903, 773, 1077, 75, 102, 681, 470, 29, 637);
```

（`Projectile.cs:47786-47791` 用该集合播放 `SoundID.BombFuse` 循环引信音。）

### A2. 雷管弹幕的完整生命周期

**弹幕 ID / 值**：`ProjectileID.Dynamite = 29`（`ProjectileID.cs:510`）。

**默认属性**（`Terraria\Projectile.cs:1121-1128`）：

```csharp
else if (type == 29)
{
    width = 10;
    height = 10;
    aiStyle = 16;
    friendly = true;
    penetrate = -1;
}
```

**引信时长 = 300 tick**。它**不在** `SetDefaults` 里设置（`Projectile.SetDefaults` 的通用默认是 `timeLeft = 3600;`，`Projectile.cs:794`），而是在 `Projectile.NewProjectile` 内按 `Type` 覆写（`Projectile.cs:10996-10999`）：

```csharp
if (Type == 29 || Type == 470 || Type == 637)
{
    projectile.timeLeft = 300;
}
```

对照：炸弹 28/516/519/1077 → 180 tick（`Projectile.cs:10976-10991`）；超级炸弹 1086/1087 → 300（`Projectile.cs:10992-10995`）。

**AI 逻辑**（`Projectile.cs:47769` `AI_016_Bombs`，由 `Projectile.cs:25638-25641` 在 `aiStyle == 16` 时调用）：

- 粘性变体（37/397/470/519/773/911/1087）落地后 `velocity.X = 0f; velocity.Y = -0.2f;` 贴住（`Projectile.cs:47827-47881`）。
- 引信音效：`Projectile.cs:47786-47791`。
- 时间归零的收尾统一在 `Projectile.cs:48032-48043`：

```csharp
if (owner == Main.myPlayer && type == 1088 && (timeLeft <= 3 || velocity.Length() <= 0.01f))
{
    Kill();
}
else if (owner == Main.myPlayer && timeLeft <= 3)
{
    Kill();
}
```

**on kill**：`Projectile.Kill()`（`Projectile.cs:71758`）。`aiStyle == 16` 分支（`Projectile.cs:71786-71815`）：

```csharp
if (aiStyle == 16)
{
    bool flag3 = ai[0] != 1000f;
    if (flag3 && type != 1088)
    {
        PrepareBombToBlow();                 // 放大判定框 + 设伤害
        bool flag4 = friendly && !npcProj && !ProjectileID.Sets.RocketsSkipDamageForPlayers[type];
        bool flag5 = owner == Main.myPlayer || owner == 255 || Main.getGoodWorld;
        if (flag4 && flag5)
        {
            SelfHurtPlayers();               // 玩家自伤
        }
        Damage();                            // 对 NPC / 玩家造成伤害 + CutTiles()
    }
    ...
    if (flag3)
    {
        Kill_Bombs_DoUsualKillCode();        // 视效 + 爆炸粒子
    }
```

`PrepareBombToBlow()`（`Projectile.cs:47635`）对雷管：

```csharp
else if (type == 29 || type == 470 || type == 637)
{
    Resize(250, 250);
    damage = 250;
    knockBack = 10f;
}
```

**爆炸破坏瓦片的真正入口**：`Projectile.cs:78990-78998`

```csharp
if (owner == Main.myPlayer && Main.netMode != 0)
{
    NetMessage.SendData(29, -1, -1, null, key, vector.X, vector.Y);
}
if (flag2)
{
    Kill_ExplodeTiles();
}
active = false;
```

即**只有 owner 是本地玩家的弹幕实例**才会真正执行瓦片破坏（网络端由 `owner != Main.myPlayer` 直接 return，见 `Projectile.cs:79003-79006`）。

### A3. 雷管爆炸半径：**7 瓦片，圆形**

半径字面量在 `Projectile.Kill_ExplodeTiles()`（`Projectile.cs:79001`）内：

```csharp
// Projectile.cs:79033 — 进入爆炸破坏分支的弹幕类型白名单
if (type == 28 || type == 29 || type == 37 || type == 108 || type == 136 || ... || type == 1086 || type == 1087)
{
    int num6 = 3;                       // ← 默认半径
    bool explodeHardmodeOres = false;
    ...
    if (type == 29 || type == 470 || type == 637 || type == 796 || type == 797 || type == 798 || type == 809)
    {
        num6 = 7;                       // ← 雷管半径
    }
    ...
    Vector2 center2 = position;
    int num7 = num6;
    int num8 = num6;
    int num9  = (int)(center2.X / 16f - (float)num7);
    int num10 = (int)(center2.X / 16f + (float)num7);
    int num11 = (int)(center2.Y / 16f - (float)num8);
    int num12 = (int)(center2.Y / 16f + (float)num8);
    ...
    bool wallSplode = ShouldWallExplode(center2, num6, num9, num10, num11, num12);
    ExplodeTiles(center2, num6, num9, num10, num11, num12, wallSplode, explodeHardmodeOres);
}
```

`ExplodeTiles` 的几何判定（`Projectile.cs:80460-80472`）——**严格圆**：

```csharp
public void ExplodeTiles(Vector2 compareSpot, int radius, int minI, int maxI, int minJ, int maxJ, bool wallSplode, bool explodeHardmodeOres = false)
{
    AchievementsHelper.CurrentlyMining = true;
    for (int i = minI; i <= maxI; i++)
    {
        for (int j = minJ; j <= maxJ; j++)
        {
            float num  = Math.Abs((float)i - compareSpot.X / 16f);
            float num2 = Math.Abs((float)j - compareSpot.Y / 16f);
            if (!(Math.Sqrt(num * num + num2 * num2) < (double)radius))
            {
                continue;
            }
```

**结论**：
- 判定是 `sqrt(dx²+dy²) < radius`（严格小于），所以是**圆**，不是菱形、不是正方形。
- 半径字面量 **7**；扫描盒是 `[cx−7, cx+7] × [cy−7, cy+7]`（15×15），盒内被圆裁掉四角。
- 圆心是 `position`（雷管是 `center2 = position`，**不是** `Center`）。注意 `Projectile.position` 是左上角，而 `width=height=10`，因此圆心相对弹幕中心偏了 5px（≈0.31 tile）。`type == 716/718/773/1086/1087` 才用 `base.Center`（`Projectile.cs:79074-79078`）。
- **会被瓦片类型检查钳制**：圆内每格还要过 `CanExplodeTile`，见 §A5。

> 换算提示（规划器）：严格 `< 7` ⇒ 沿正轴向最远破坏到 `dx = 6`（因为 `7 < 7` 为假，`dx=7` 的轴向格不破坏）。斜向最大 `dx=dy=4`（`sqrt(32)=5.66 < 7`），`dx=dy=5` 时 `sqrt(50)=7.07 > 7` **不破坏**。所以实际破坏区域是半径 7 的圆盘但**轴向截到 6**。

### A4. Explosives / Bomb 半径对照

| 物品（ItemID） | 弹幕（ProjectileID） | 半径 | 引信 | 依据 |
|---|---|---|---|---|
| Bomb (166) | Bomb = 28 | **4** | 180 | `Projectile.cs:79041-79044`、`10976-10979` |
| StickyBomb (235) | StickyBomb = 37 | 4 | 180 | 同上（37 在 28 分支组） |
| BouncyBomb (3115) | 516 | 4 | 180 | `Projectile.cs:79041-79044` |
| BombFish (3196) | 519 | 4 | 180 | 同上 |
| **Dynamite (167)** | **29** | **7** | **300** | `Projectile.cs:79045-79048`、`10996-10999` |
| StickyDynamite (2896) | 470 | 7 | 300 | 同上 |
| BouncyDynamite (3547) | 637 | 7 | 300 | 同上 |
| DryBomb 等 4827 | 906 | 3 | 300（`IsABombWithFuse` 成员） | `Projectile.cs:79058-79061` |
| **Explosives (580)** | **108** | **10** | 300 | `Projectile.cs:79066-79069`、`10996-10999` |
| **SuperBomb (5594)** | 1086/1087 | 9（并 `explodeHardmodeOres = true`） | 300 | `Projectile.cs:79049-79053` |
| TNTBarrel (5327) | 1002 | 10 | 2 | `Projectile.cs:79070-79073`、`9530-9544` |
| BombSkeletronPrime | 102 | 4（`getGoodWorld` 时） | — | `Projectile.cs:79037-79040` |
| ScarabBomb | 773 | 3 | — | `Projectile.cs:79058-79061` |
| Celebration Mk2 火箭 | 716/780/781/782/783/804/863 | 3 | — | `Projectile.cs:79058-79061` |
| Celebration Mk2 大火箭 | 718 | 5 | — | `Projectile.cs:79062-79065` |

另有「裂纹砖」专用小半径爆炸：`num = 4` 分支（`Projectile.cs:79007-79032`，类型 30/517/397/588）调用 `ExplodeCrackedTiles`（`Projectile.cs:80511`），只破坏 `TileID.Sets.CrackedBricks = {481, 482, 483}`（`Terraria.ID\TileID.cs:183`）。

### A5. 爆炸免疫瓦片（完整清单）

守卫函数 `Projectile.CanExplodeTile(int x, int y, bool explodeHardmodeOres = false)`，`Projectile.cs:80389-80458` 全文：

```csharp
public bool CanExplodeTile(int x, int y, bool explodeHardmodeOres = false)
{
    if (Main.tileDungeon[Main.tile[x, y].type] || TileID.Sets.BasicChest[Main.tile[x, y].type])
    {
        return false;
    }
    if (Main.tile[x, y].wall == 350)
    {
        return false;
    }
    switch (Main.tile[x, y].type)
    {
    case 26: case 88: case 121: case 122: case 150: case 211: case 226:
    case 237: case 248: case 249: case 250: case 346: case 470: case 475:
    case 504: case 685: case 686:
        return false;
    case 107: case 108: case 111: case 221: case 222: case 223:
        return explodeHardmodeOres;
    case 37: case 58:
        if (!Main.hardMode) { return false; }
        break;
    case 77:
        if (!Main.hardMode && y >= Main.UnderworldLayer) { return false; }
        break;
    case 48: case 232:
        if (Main.getGoodWorld) { return false; }
        break;
    case 137:
        if (!NPC.downedGolemBoss)
        {
            int num = Main.tile[x, y].frameY / 18;
            if ((uint)(num - 1) <= 3u) { return false; }
        }
        break;
    }
    return true;
}
```

#### A5.1 无条件免疫（永远炸不掉）

| 表达式 | TileID 名称/值 | 依据 |
|---|---|---|
| `Main.tileDungeon[type]` | **BlueDungeonBrick = 41**、**GreenDungeonBrick = 43**、**PinkDungeonBrick = 44**、**AncientBlueBrick = 677**、**AncientGreenBrick = 678**、**AncientPinkBrick = 679** | `Terraria\Main.cs:1475`（数组声明）、`Main.cs:8173-8178`（赋值）；名称见 `TileID.cs:519/523/525/1791/1793/1795` |
| `TileID.Sets.BasicChest[type]` | **Chest = 21**、**Chest2 = 467** | `TileID.cs:315` `public static bool[] BasicChest = Factory.CreateBoolSet(21, 467);` |
| `wall == 350` | **UnbreakableBlockWall = 350** | `Terraria.ID\WallID.cs:769` |
| `case 26` | **DemonAltar = 26**（恶魔祭坛 / 猩红祭坛同型） | `TileID.cs:489` |
| `case 88` | **Dressers = 88**（梳妆台） | `TileID.cs:613` |
| `case 121` | **CobaltBrick = 121** | `TileID.cs:679` |
| `case 122` | **MythrilBrick = 122** | `TileID.cs:681` |
| `case 150` | **AdamantiteBeam = 150** | `TileID.cs:737` |
| `case 211` | **Chlorophyte = 211**（叶绿矿） | `TileID.cs:859` |
| `case 226` | **LihzahrdBrick = 226**（蜥蜴神庙砖） | `TileID.cs:889` |
| `case 237` | **LihzahrdAltar = 237**（蜥蜴祭坛） | `TileID.cs:911` |
| `case 248` | **PalladiumColumn = 248** | `TileID.cs:933` |
| `case 249` | **BubblegumBlock = 249** | `TileID.cs:935` |
| `case 250` | **Titanstone = 250** | `TileID.cs:937` |
| `case 346` | **ChlorophyteBrick = 346** | `TileID.cs:1129` |
| `case 470` | **DisplayDoll = 470**（人偶展示架） | `TileID.cs:1377` |
| `case 475` | **HatRack = 475**（衣帽架） | `TileID.cs:1387` |
| `case 504` | **MysticSnakeRope = 504** | `TileID.cs:1445` |
| `case 685` | **AncientCobaltBrick = 685** | `TileID.cs:1807` |
| `case 686` | **AncientMythrilBrick = 686** | `TileID.cs:1809` |

#### A5.2 条件免疫 / 条件可炸

| 分支 | 语义 | 名称 |
|---|---|---|
| `case 107/108/111/221/222/223 → return explodeHardmodeOres;` | **困难模式矿石默认炸不掉**，只有 `explodeHardmodeOres == true`（超级炸弹 1086/1087）才可炸 | Cobalt = 107、Mythril = 108、Adamantite = 111、Palladium = 221、Orichalcum = 222、Titanium = 223（`TileID.cs:651/653/659/879/881/883`） |
| `case 37/58: if (!Main.hardMode) return false;` | **非困难模式**下陨石/地狱石炸不掉 | Meteorite = 37（`TileID.cs:511`）、Hellstone = 58（`TileID.cs:553`） |
| `case 77: if (!Main.hardMode && y >= Main.UnderworldLayer) return false;` | 非困难模式且在地狱层，地狱熔炉炸不掉 | Hellforge = 77（`TileID.cs:591`） |
| `case 48/232: if (Main.getGoodWorld) return false;` | 仅在「Good World（1.4 秘密种子）」下尖刺免疫 | Spikes = 48（`TileID.cs:533`）、WoodenSpikes = 232（`TileID.cs:901`） |
| `case 137: if (!NPC.downedGolemBoss) { frameY/18 ∈ {1,2,3,4} → return false; }` | 未击败石巨人时，**部分朝向的陷阱**炸不掉 | Traps = 137（`TileID.cs:711`） |

**重要**：Dynamite（29）不带 `explodeHardmodeOres`，所以**所有困难模式矿石都炸不掉**（只能用半径 7 的爆炸清掉周围普通方块）。

#### A5.3 墙壁爆炸（`wallSplode`）

`ShouldWallExplode`（`Projectile.cs:80364-80381`）：只要半径圆内**存在任意一格 `wall == 0`** 就返回 `true`；然后 `ExplodeTiles` 在每格成功破坏后，对该格 3×3 邻域杀墙（`Projectile.cs:80487-80505`）：

```csharp
if (!(wallSplode && flag)) { continue; }
for (int k = i - 1; k <= i + 1; k++)
{
    for (int l = j - 1; l <= j + 1; l++)
    {
        Tile tile2 = Main.tile[k, l];
        if (tile2 != null && tile2.wall != 0 && tile2.wall != 350)
        {
            WorldGen.KillWall(k, l);
        }
    }
}
```

即：**雷管会摧毁墙体**（`WallID`），但**不会摧毁 `WallID.UnbreakableBlockWall = 350`**（`WallID.cs:769`）。注意 `CanExplodeTile` 也对 `wall == 350` 的格子直接返回 `false`（即「不可破坏墙」保护其所在格）。

### A6. 玩家自伤

**会伤害玩家。** 入口 `Projectile.cs:71791-71797`：`friendly && !npcProj && !RocketsSkipDamageForPlayers[type]` 且 `owner == Main.myPlayer || owner == 255 || Main.getGoodWorld` ⇒ `SelfHurtPlayers()`。

`SelfHurtPlayers()`（`Projectile.cs:15110-15150`）：

```csharp
private void SelfHurtPlayers()
{
    Player localPlayer = Main.LocalPlayer;
    if (!localPlayer.active || localPlayer.dead || localPlayer.immune || (ownerHitCheck && !CanHitWithMeleeWeapon(localPlayer)) || !Colliding(base.Hitbox, localPlayer.Hitbox))
    {
        return;
    }
    direction = ((!(localPlayer.Center.X < base.Center.X)) ? 1 : (-1));
    int value = damage;                                    // ← 雷管此时 damage = 250
    int? num = ProjectileID.Sets.FixedSelfDamageForPlayers[type];
    if (num.HasValue) { value = num.Value; }
    int num2 = Main.DamageVar(value, 0f - localPlayer.luck);
    if (localPlayer.deadMansSweater && trap)
    {
        num2 = (int)((float)num2 * 0.5f);
        num2 = Math.Min(num2, 300);
    }
    bool flag = !hostile && type != 949;
    int playerIndex = (flag ? owner : (-1));
    if (ProjectileID.Sets.IsAGravestone[type])
    {
        playerIndex = (int)ai[0];
    }
    bool dodgeable = IsDamageDodgeable();
    PlayerDeathReason damageSource = PlayerDeathReason.ByProjectile(playerIndex, whoAmI);
    if (localPlayer.Hurt(damageSource, num2, direction, flag, quiet: false, Crit: false, -1, dodgeable) > 0.0 && !localPlayer.dead)
    {
        StatusPlayer(localPlayer);
    }
```

- **伤害值**：`damage`，由 `PrepareBombToBlow()` 设为 **250**（雷管；`Projectile.cs:47655`）。
- `ProjectileID.Sets.FixedSelfDamageForPlayers`（`Terraria.ID\ProjectileID.cs:193-273`）里**没有 29 / 470 / 637**（该表只覆盖 133-144、776-801、779/783、1117 等火箭类，值 60 或 120）。⇒ 雷管**不套用固定自伤**，直接用 250。
- **判定半径**：用 `Colliding(base.Hitbox, localPlayer.Hitbox)`，而 `PrepareBombToBlow()` 已把判定框 `Resize(250, 250)`（`Projectile.cs:47654`）。
  - `Resize` 以中心为基准（`Projectile.cs:80136-80142` 的等价写法：先移动到中心，改宽高，再移回）。
  - 因此自伤判定是**以爆炸点为心、5120 边长的轴对齐正方形**。玩家判定框 40×56（默认，见 §D19），故实际受影响范围约 **±（125 + 20）= 145 px ≈ 9.1 tile（水平）/ ±（125 + 28）≈ 9.6 tile（垂直）**。
- **计算方式**：非固定、非距离衰减，而是**定值 250 + 全局 ±15% 随机**：
  `Main.DamageVar(250, -luck)`（`Main.cs:67147-67173`）⇒ `250 × (1 + rand(−15..15)/100)`，返回 `Math.Round`。
  负 luck 会让随机取值偏向更**高**（`Main.cs:67165-67171`）。
- **无距离衰减**：源码中没有任何按距离缩放的项（只有 `Main.DamageVar` 的 ±15%）。
- 额外：若 `localPlayer.deadMansSweater && trap` 会减半并封顶 300（`Projectile.cs:15125-15129`），但雷管 `trap == false`，**不适用**。
- 防护：`localPlayer.immune` 为真则**完全不受伤**（`Projectile.cs:15113` 提前 return）。所以只要在爆炸帧处于无敌帧内就安全（见 §C18）。

### A7. 是否摧毁物品 / 墓碑 / 墙 / 通电与斜坡瓦片

| 问题 | 结论 | 依据 |
|---|---|---|
| 摧毁**地面掉落物**（`Main.item`）？ | **不摧毁**。全树 `Projectile.cs` 内无对 `Main.item[]` 的爆炸销毁逻辑 | `Projectile.cs` 内 `Item.NewItem` 仅 6 处（`:33543`、`:42392`、`:76478`、`:76482`、`:76515`、`:76634`），均为掉落生成，非销毁 |
| 摧毁**墓碑/墓石**（TileID 85）？ | **会摧毁**。85 不在 `CanExplodeTile` 的任何免疫分支里，因此走 `WorldGen.KillTile(i, j)` 正常破坏（挖矿时 85 还有减半惩罚：`Player.cs:54577`） | `Projectile.cs:80389-80458`、`80480` |
| 摧毁**墙**（`WallID`）？ | **会**，3×3 邻域杀墙，但跳过 wall 350 | `Projectile.cs:80487-80505` |
| 摧毁**通电（actuated）瓦片**？ | **不会**。`ExplodeTiles` 先要求 `tile.active()`（`Projectile.cs:80475`），`Tile.active()` 定义是 `(sTileHeader & 0x20) == 32`（`Terraria\Tile.cs:589-592`）；而「通电后未实体化」由 `Tile.nactive()` 判定：`(sTileHeader & 0x60) == 32`（`Terraria\Tile.cs:240-247`）。当瓦片被通电关闭时 `active()` 为 false ⇒ 直接跳过 | `Projectile.cs:80475`、`Tile.cs:589-592`、`Tile.cs:240-247` |
| 摧毁**斜坡（sloped）瓦片**？ | **会**。`ExplodeTiles` / `CanExplodeTile` 均**未检查** slope / `halfBrick`，只有 `active()` 前置条件 | `Projectile.cs:80460-80509`、`80389-80458` |
| 破坏**草/花**类可切瓦片？ | 会额外走 `Damage()` → `CutTiles()`（`Projectile.cs:12520-12523`），判定框是放大后的 250×250 | `Projectile.cs:12507-12536`、`15282` |

> 不确定：`CutTiles()` 内部对「可切瓦片」的具体白名单依赖 `Main.player[owner].GetTileCutIgnorance(...)`（`Projectile.cs:15290`），本次未逐条展开，若规划器需要精确到草/藤的具体 TileID 请另行确认。

---

## B. 感染扩散规则（决定所需空隙）

**说明**：本次未找到名为 `WorldGen.SpreadInfection` 的方法（**未找到**），也**未找到**单一的 `TileID.Sets.Spread` 集合。实际实现是 `WorldGen.hardUpdateWorld` 内的三个 `TileID.Sets.SpreadsCorruption/SpreadsCrimson/SpreadsHallow` 分支 + 草地专用 `UpdateWorld_GrassGrowth` + 墙专用 `SpreadGrassWalls`。`TileRunner`（`WorldGen.cs:77598`）全部 75 处调用都在世界生成代码里，与每 tick 扩散无关。

### B8. 扩散例程与 reach

**调用链**：

```
Terraria\Main.cs:18333                     WorldGen.UpdateWorld();
Terraria\WorldGen.cs:72043                 public static void UpdateWorld()
WorldGen.cs:72049                          hardModeWorldUpdates = Main.hardMode || (Main.remixWorld && Main.getGoodWorld && !Main.tenthAnniversaryWorld);
WorldGen.cs:72050-72055                    AllowedToSpreadInfections = true;   // 创造模式“停止生物群系蔓延”电力可置 false
WorldGen.cs:72056                          int wallDist = 3;
WorldGen.cs:72116-72123                    随机采样点循环
WorldGen.cs:72150                          UpdateWorld_OvergroundTile(num8, num9, wallDist);
WorldGen.cs:72161 / 72172                  UpdateWorld_UndergroundTile(..., wallDist);
WorldGen.cs:72614                          private static void UpdateWorld_OvergroundTile(int i, int j, int wallDist)
WorldGen.cs:72616-72619                    num = i - 1; num2 = i + 2; num3 = j - 1; num4 = j + 2;   // 3×3 窗口
WorldGen.cs:72803                          hardUpdateWorld(i, j);
WorldGen.cs:73814                          private static void UpdateWorld_UndergroundTile(...)   // 73816-73819 同 3×3
WorldGen.cs:73854                          hardUpdateWorld(i, j);
WorldGen.cs:72942 / 73882                  UpdateWorld_GrassGrowth(i, j, num, num2, num3, num4, underground: false/true);
WorldGen.cs:70140                          public static void hardUpdateWorld(int i, int j)
```

**硬门槛**：

```csharp
// WorldGen.cs:70142
if (!hardModeWorldUpdates || Main.tile[i, j].inActive())
{
    return;
}
...
// WorldGen.cs:70242
if ((NPC.downedPlantBoss && genRand.Next(2) != 0) || !AllowedToSpreadInfections)
{
    return;
}
```

⇒ 非困难模式完全不扩散；击败世纪之花后**只有 50% 的 tick** 会扩散。

**reach = ±3（Chebyshev）**，三派各一处（`WorldGen.cs:70246-70253` Corruption；`:70335-70342` Crimson；`:70424-70433` Hallow）：

```csharp
// WorldGen.cs:70246
if (type >= 0 && TileID.Sets.SpreadsCorruption[type])
{
    bool flag2 = true;
    while (flag2)
    {
        flag2 = false;
        int num11 = i + genRand.Next(-3, 4);      // ← ±3，含 3
        int num12 = j + genRand.Next(-3, 4);
        if (!InWorld(num11, num12, 10)) { continue; }
        if (nearbyChlorophyte(num11, num12)) { ChlorophyteDefense(num11, num12); }
        else
        {
            if (CountNearBlocksTypes(num11, num12, 2, 1, 27) > 0) { continue; }   // 27 = Sunflower 太阳花阻断
            if (Main.tile[num11, num12].type == 2 || Main.tile[num11, num12].type == 477)
            {
                if (genRand.Next(2) == 0) { flag2 = true; }        // 50% 继续
                Convert(num11, num12, 1, tiles: true, walls: false);
            }
            ...
```

**不是** `for (i=x-3; i<=x+3; i++)` 双重扫描，而是「在源格 ±3 方框内随机取一格 + 50% 概率 `while` 重采样」，目标始终相对**原始源格**。因此单 tick 内可清空整个 ±3 方框内所有可转换格（理论上；期望约 2 格）。

另外两条 reach：

| 路径 | reach | 依据 |
|---|---|---|
| 硬扩散（`hardUpdateWorld`，石/沙/冰/草等） | **±3** | `WorldGen.cs:70252-70253`、`70341-70342`、`70432-70433` |
| 草地互相感染（`UpdateWorld_GrassGrowth`） | **±1（3×3 窗口）** | `WorldGen.cs:75240-75242`：`for (int num19 = minI; num19 < maxI; num19++) for (int num20 = minJ; num20 < maxJ; num20++)`，而 `minI = i-1, maxI = i+2` ⇒ `i-1..i+1` |
| 墙扩散（`SpreadGrassWalls`） | **±2** | `WorldGen.cs:75505-75506`：`int num = i + genRand.Next(-2, 3);` |
| 带 `size` 的 `Convert` 重载（净化粉等，**非**每 tick 扩散） | `size` 方框 + 曼哈顿 `< 6` | `WorldGen.cs:55556-55560` |

### B9. 可感染瓦片与转换对

#### 源集合（`TileID.Sets`，逐行引用）

```csharp
// Terraria.ID\TileID.cs:333
public static bool[] Corrupt = Factory.CreateBoolSet(23, 661, 25, 112, 163, 398, 400, 636);
// Terraria.ID\TileID.cs:335
public static bool[] SpreadsCorruption = Factory.CreateBoolSet(23, 661, 25, 112, 398, 400, 163, 32, 636, 24);
// Terraria.ID\TileID.cs:341
public static bool[] Hallow = Factory.CreateBoolSet(109, 492, 117, 116, 164, 402, 403, 115);
// Terraria.ID\TileID.cs:343
public static bool[] SpreadsHallow = Factory.CreateBoolSet(109, 492, 117, 116, 402, 403, 164, 115, 110, 113);
// Terraria.ID\TileID.cs:351
public static bool[] Crimson = Factory.CreateBoolSet(199, 662, 203, 234, 200, 399, 401, 205);
// Terraria.ID\TileID.cs:353
public static bool[] SpreadsCrimson = Factory.CreateBoolSet(199, 662, 203, 234, 399, 401, 200, 352, 205, 201);
// Terraria.ID\TileID.cs:417
public static bool[] SpreadOverground = Factory.CreateBoolSet(2, 23, 661, 32, 60, 70, 109, 199, 662, 352, 477, 492, 633, 226);
// Terraria.ID\TileID.cs:419
public static bool[] SpreadUnderground = Factory.CreateBoolSet(23, 661, 109, 199, 662, 60, 70, 633, 226);
```

#### 目标白名单（硬扩散只碰这些）

- Corruption（`WorldGen.cs:70268-70331`）与 Crimson（`:70357-70420`）：`2 | 477`、`1 | Main.tileMoss[]`、`53`、`396`、`397`、`60`、`69`、`161`
- Hallow（`:70438-70492`）：`2`、`477`、`1 | moss`、`53`、`396`、`397`、`161`（**无 60、无 69**）

⇒ **117 Pearlstone / 109 HallowedGrass / 199 CrimsonGrass 等异派方块不会被另一派的硬扩散直接改写**（跨派互转走 §B10 的草地路径）。

#### 精确转换对（函数 `WorldGen.Convert(x, y, conversionType)`）

`conversionType`：**1 = Corruption，2 = Hallow，4 = Crimson**。

| 原瓦片 | Corruption (1) | Crimson (4) | Hallow (2) | 依据 |
|---|---|---|---|---|
| Stone 1 / moss | → **25 Ebonstone** | → **203 Crimstone** | → **117 Pearlstone** | `WorldGen.cs:55786-55788` / `55583+` / `55744+`；集合 `TileID.Sets.Conversion.Stone{1,25,117,203}`（`TileID.cs:26`） |
| Grass 2 | → **23 CorruptGrass** | → **199 CrimsonGrass** | → **109 HallowedGrass** | `TileID.cs:18` `Conversion.Grass{2,23,199,109,477,492}` |
| GolfGrass 477 | → 23 | → 199 | → **492** | `WorldGen.cs:55707-55709` |
| JungleGrass 60 | → **661** | → **662** | （不在白名单） | `TileID.cs:14` `Conversion.JungleGrass{60,661,662}` |
| Ice 161 | → **163 CorruptIce** | → **200 FleshIce** | → **164 HallowedIce** | `TileID.cs:28` `Conversion.Ice{161,163,164,200}` |
| Sand 53 | → **112 Ebonsand** | → **234 Crimsand** | → **116 Pearlsand** | `TileID.cs:30` `Conversion.Sand{53,112,116,234}` |
| HardenedSand 397 | → **398** | → **399** | → **402** | `TileID.cs:32` `Conversion.HardenedSand{397,398,402,399}` |
| Sandstone 396 | → **400** | → **401** | → **403** | `TileID.cs:34` `Conversion.Sandstone{396,400,403,401}` |
| Thorn | → **32 CorruptThorns** | → **352 CrimsonThorns** | 直接 `KillTile`（荆棘被清除） | `TileID.cs:36` `Conversion.Thorn{32,352,69,655}`；神圣见 `WorldGen.cs:55731-55738` |
| Mud 59（仅当四邻有 109） | — | — | → **0 Dirt**（泥变土） | `WorldGen.cs:55739-55742` |
| 墙 Grass | → wall **69** | → wall **81** | → wall **70** | `WorldGen.cs:55747-55777` / `55583+` / `55744+` |
| 墙 Stone | → wall **3** | → wall **83** | → wall **28** | 同上 |

#### 跨派草地互转（reach 1，无逐格随机门）

`WorldGen.cs:75239-75300`：源草 `23/199/109/492/661/662` 会把 3×3 内的 `2/477/109/492/23/199` 转成自己一派：

```csharp
// WorldGen.cs:75254
if (type2 == 0 || (num18 > -1 && type2 == 59) || ((num10 == 23 || num10 == 661 || num10 == 199 || num10 == 662) && (type2 == 2 || type2 == 109 || type2 == 477 || type2 == 492)))
{
    SpreadGrass(num19, num20, 0, grass, repeat: false, color3);
    ...
// WorldGen.cs:75294-75300
if ((num10 == 492 || num10 == 109) && AllowedToSpreadInfections)
{
    SpreadGrass(num19, num20, 23, 109, repeat: false, color3);
}
if ((num10 == 492 || num10 == 109) && AllowedToSpreadInfections)
{
    SpreadGrass(num19, num20, 199, 109, repeat: false, color3);
}
```

真正换草的落点在 `SpreadGrass`（`WorldGen.cs:75758`）的 `75818-75831`。

### B10. 植物 / 藤蔓 / 荆棘桥梁与「母格搜索」距离

| 对象 | 母格搜索距离 | 依据 |
|---|---|---|
| **植物**（3/24/61/71/110/113/201/637…） | **只查正下方 1 格** | `WorldGen.cs:81828` `PlantCheck`；`81852` `if (y + 1 < Main.maxTilesY && Main.tile[x, y + 1] != null && ...) down = Main.tile[x, y + 1].type;`；`81867` `if (!PlantCheck_IsBadTypeMatch(down, type)) return;` |
| **藤蔓**（52 Vines / 636 CorruptVines / 205 CrimsonVines / 115 HallowedVines） | **只查正上方 1 格** | `WorldGen.cs:86147` `CheckVines`；`86149` `Tile tile = Main.tile[i, j - 1];`；`86165` `flag = num == 23 || num == 636 || num == 661;`；`86243-86246` `if (flag5) KillTile(i, j);` |
| **荆棘**（32 CorruptThorns / 352 CrimsonThorns） | **半径 7 方框 + 菱形判据 `|dx|*2 + |dy| < 9`** | `WorldGen.cs:45885` `GrowSpike`；`45960-45970`：`int num5 = 7; for (int k = num2-num5; k < num2+num5; k++) for (int l = num3-num5; l < num3+num5; l++) if (Math.Abs(k-num2)*2 + Math.Abs(l-num3) < 9 && ...)`；触发 `72944`（地表）/`73884`（地下）`if ((type == 32 || type == 352) && genRand.Next(3) == 0)` |
| 腐化藤「向上找母格」 | **向上 10 格** | `WorldGen.cs:73289-73301`：`for (int num33 = j; num33 > j - 10; num33--) ... if (active && (type == 23 || type == 661) && !bottomSlope()) { flag8 = true; break; }` |

**关键结论**：植物与藤蔓**不会跨空隙「搭桥」**——它们各自只认紧邻 1 格的母格，所以只要母格那 1 格不是感染草/感染藤，植物就换不了型。荆棘的 7 格搜索是唯一较宽的，但需要 `|dx|*2 + |dy| < 9`，且 `Main.tile[k, l-1].type == spikeType`（母格正上方必须是同类荆棘）与 `liquid == 0`。

相关 TileID：`CorruptThorns = 32`（`TileID.cs:501`）、`Vines = 52`（`TileID.cs:541`）、`CrimsonThorns = 352`（`TileID.cs:1141`）、`CorruptVines = 636`（`TileID.cs:1709`）。注意 `TileID.Cobweb = 51`（`TileID.cs:539`）**不是**藤蔓。

### B11. 空气 / 水 / 邻接，以及阻断空隙 N

- **硬扩散不做邻接、不做视线检查**。它只随机取 `i±3, j±3` 的一格然后看 `type` 是否在白名单（`WorldGen.cs:70252-70331`）。因此中间隔着什么（空气 / 水 / 实体）**完全不影响**。
- **空气本身不会被转换**：`type == 0` 不在任何白名单里。感染是「**跳过**」空气，不是「经过」空气。
- **水不阻断**：白名单只判 `tile.type`，不检查 `liquid`。只有植物/海草生长才查 `liquid`（例如 `WorldGen.cs:75110`、`75159`）。

**阻断空隙 N（代码推出的结论）**

| 路径 | 阻断所需最小空距 N | 推导 |
|---|---|---|
| 硬扩散 `hardUpdateWorld` | **n ≥ 3**（中间 ≥3 格空/非白名单 ⇒ 距离 ≥4 > 采样上限 3） | 采样上限 Chebyshev 3；距离 = n + 1 |
| 草地互相感染 | **n ≥ 1** | 3×3 窗口（`WorldGen.cs:75240-75242`） |
| 墙扩散 | **n ≥ 2** | `genRand.Next(-2, 3)`（`WorldGen.cs:75505-75506`） |

**两跳（chain）问题 —— 规划器必须用这个数**：硬扩散能在感染区内生成新的「感染源」，再以新源为心做下一次 ±3 跳跃。因此单看「源→干净格距离 ≤3」是不够的：只要感染区外缘 `d ≤ 3` 处存在**可被感染的实体方块**（石 1、沙 53、硬化沙 397、砂岩 396、草 2/477、丛林草 60、冰 161），它下一 tick 就会成为新源，把界限再推进 3 格。

⇒ **规划器安全判据（推荐）**：

```
D = 干净区的第一个可感染实体方块（1/2/53/60/69/161/396/397/477 等）
    到 感染区任一感染源方块（SpreadsCorruption/Crimson/Hallow 集合，含 24/32/636/201/352/205/110/113/115）
    的最小欧氏距离（也可用 Chebyshev 保守估计）
要求 D >= 4 才安全（一次爆炸只能推进 3 格）
```

反过来算「需要炸多宽」：**隔离带宽度 = 3 格的可感染实体 + 1 格余量 = 4 格**；如果隔离带内全部挖成空气（无任何可感染方块），则硬扩散**不能越过**，因为它无法在空气里生成踏脚点，而它自己不会把空气转换成方块（`type == 0` 不在白名单）——此时**无论多窄都能阻断**。

> 不确定：`InWorld(num11, num12, 10)` 的前置（`WorldGen.cs:70254`）会跳过世界边缘 10 格，边缘区域不扩散；本条未逐点验证边界行为。

另外两个可用的**主动阻断手段**（源码有据）：
1. **向日葵**（`TileID.Sunflower = 27`，`TileID.cs:491`）：目标格周围 `CountNearBlocksTypes(..., 2, 1, 27) > 0` 就跳过（`WorldGen.cs:70264`），半径 2（5×5）。
2. **创造模式「停止生物群系蔓延」电力** 把 `AllowedToSpreadInfections` 置 false（`WorldGen.cs:72050-72055`）。

### B12 附：墙壁感染

`WorldGen.SpreadGrassWalls(int wallDist, int i, int j)`（`WorldGen.cs:75496`），调用点 `73085-73088`（地表）/ `74410-74413`（地下），前提 `AllowedToSpreadInfections`：

```csharp
// WorldGen.cs:75503
if (WallID.Sets.SpreadsCrimson[tile.wall] || (tile.active() && TileID.Sets.SpreadsCrimson[tile.type]))
...
// WorldGen.cs:75505-75507
int num  = i + genRand.Next(-2, 3);
int num2 = j + genRand.Next(-2, 3);
if (!InWorld(num, num2, 10) || Main.tile[num, num2].wall < 63 || Main.tile[num, num2].wall > 68) return;
```

墙体源集合：`WallID.Sets.SpreadsCorruption{69,217,220,3}`、`SpreadsCrimson{83,81,218,221}`、`SpreadsHallow{70,219,222,28}`（`Terraria.ID\WallID.cs:48/50/52`）。

---

## C. 执行器必须规避的危险

### C12. 熔岩（Lava）

**液体类型常量**：`Terraria\Liquid.cs` 里的 `Liquid` 是 **struct**，**没有** `Liquid.water/lava/honey/shimmer`（**未找到**）。真正的常量在 `Terraria.ID\LiquidID.cs:5-13`：

```csharp
public const short Water = 0;
public const short Lava = 1;
public const short Honey = 2;
public const short Shimmer = 3;
public static readonly short Count = 4;
```

**每格液体数据在 `Tile` 上**：`Terraria\Tile.cs:12` `public byte liquid;`（0..255，255 = 满格）；类型编码在位域：
- `Tile.cs:235-238` `public byte liquidType() => (byte)((bTileHeader & 0x60) >> 5);`
- `Tile.cs:345-348` `public bool lava() => (bTileHeader & 0x60) == 32;`
- `Tile.cs:362-365` `honey() == 64`；`Tile.cs:379-382` `shimmer() == 96`；`Tile.cs:396-399` `water() => liquidType() == 0`

实际岩浆伤害判定走的是 `Collision.LavaCollision`（`Terraria\Collision.cs:1667`，条件 `tile.liquid > 0 && tile.lava()`，见 `:1682`），而非直接读 `Liquid.type`：

```csharp
// Terraria\Player.cs:27893-27897
if (!shimmering)
{
    flag25 = Collision.LavaCollision(position, width, num82);
}
lavaWet = flag25;
```

**受伤逻辑**（`Player.cs:27899-27939`）：

```csharp
lavaImmune |= ashWoodBonus && lavaRose;
if (flag25 && !lavaImmune)
{
    flag26 = false;
    if (Main.myPlayer == i && hurtCooldowns[ImmunityCooldownID.Lava] <= 0)
    {
        if (lavaTime > 0)
        {
            lavaTime--;                       // 先消耗岩浆耐受
        }
        else
        {
            int num83 = 80;                   // ← 熔岩伤害 80
            int num84 = 420;                  // ← 着火 buff 时长 420 tick
            if (Main.remixWorld) { num83 = 200; num84 = 630; }
            if (ashWoodBonus)
            {
                if (Main.remixWorld) { num83 = 145; }
                num83 /= 2;                   // ashWoodBonus 减半
                num84 -= 210;
            }
            if (lavaRose)
            {
                num83 -= 45;                  // Obsidian Rose -45
                num84 -= 210;
            }
            double num85 = Hurt(PlayerDeathReason.ByOther(2), num83, 0, pvp: false, quiet: false, Crit: false, ImmunityCooldownID.Lava);
            if (num84 > 0 && num85 > 0.0)
            {
                AddBuff(24, num84);           // BuffID.OnFire = 24
            }
        }
    }
}
if (flag26 && lavaTime < lavaMax)
{
    lavaTime++;
}
```

| 项目 | 数值 | 依据 |
|---|---|---|
| 每次熔岩伤害 | **80**（remix 世界 200） | `Player.cs:27911-27917` |
| ashWoodBonus 时 | 40（remix 145/2 = 72） | `Player.cs:27918-27926` |
| lavaRose（黑曜石玫瑰）时 | 再 −45 | `Player.cs:27927-27931` |
| 着火 buff | `AddBuff(24, 420)`，即 `BuffID.OnFire = 24`，420 tick = 7 秒 | `Player.cs:27912`、`27935` |
| 触发节流 | `hurtCooldowns[ImmunityCooldownID.Lava] <= 0` ⇒ 受击后的无敌帧长度决定周期（默认 40 tick） | `Player.cs:27903`；`Player.cs:38689-38697` |
| 是否有专家/大师缩放 | **未找到（确认不存在）**：该分支内无 `Main.expertMode` / `GameDifficultyData` 判断 | `Player.cs:27899-27939` |
| 但难度**间接**影响实际扣血 | `Main.CalculateDamagePlayersTake`（`Main.cs:67196-67212`）：`num = Damage - Defense * 0.5`；`masterMode` → `Damage - Defense`；`expertMode` → `Damage - Defense * 0.75`；下限 1 | `Main.cs:67196-67212` |
| `GameDifficultyData.EnemyDamageMultiplier` | 0.5 / 1 / 2 / 3 / 5.333（Journey/Classic/Expert/Master/Legendary），**只**用于 `NPC.cs:7065,7071`、`Player.cs:25432`（石化摔伤）、`Conditions.cs:805`——**不含岩浆 / 溺水 / 坠落** | `GameDifficultyData.cs:71` |

**真实扣血速率**：80 伤害 ÷ 40 帧间隔 ≈ **120 DPS**（未计防御）。

**精确字段名**（全部在 `Terraria\Player.cs`）：

| 字段 | 声明 | 行号 |
|---|---|---|
| `lavaWet` | `public bool lavaWet;` | `Terraria\Entity.cs:34`（**注意：继承自 Entity**） |
| `lavaImmune` | `public bool lavaImmune;` | `Player.cs:2779` |
| `lavaTime` | `public int lavaTime;` | `Player.cs:1527` |
| `lavaMax` | `public int lavaMax;`（默认 0） | `Player.cs:1525` |
| `lavaCD` | `public int lavaCD;` | `Player.cs:1523`（**全树零引用 → 死字段**） |
| `lavaRose` | `public bool lavaRose;` | `Player.cs:1971`（声明）；赋值 `Player.cs:14574`、`14592`、`15301`、`15471`；每帧清零 `Player.cs:18742` |
| `ashWoodBonus` | `public bool ashWoodBonus;` | 每帧清零 `Player.cs:18455` |
| `fireWalk` | `public bool fireWalk;` | `Player.cs:1877`（声明） |
| `lavaVision` | `public bool lavaVision;` | `Player.cs:1531` |
| `lavaOpacity` | `public float lavaOpacity = 1f;` | `Player.cs:1533` |

**`lavaTime` / `lavaMax` 的准确语义**（`Player.cs:27940-27947`）：

```csharp
if (flag26 && lavaTime < lavaMax)     // flag26 = 本帧不在岩浆里
{
    lavaTime++;
}
if (lavaTime > lavaMax)
{
    lavaTime = lavaMax;
}
```

`lavaTime` 是「**脱离岩浆时累积的缓冲计时器**」，上限 `lavaMax`；入岩浆时先按 `lavaTime--` 消耗（`Player.cs:27905-27908`），耗尽后才开始吃伤害。默认 `lavaMax = 0` ⇒ 入岩浆**立即**受伤。重生时 `lavaTime = lavaMax`（`Player.cs:37889`）。

**`fireWalk` 不参与岩浆伤害**：它只用于免疫「灼热方块」（狱石等）——`Collision.cs:3243` `if (TileID.Sets.TouchDamageHot[type] && (player == null || !player.fireWalk))`。所以 `fireWalk` 不能防岩浆。

**免疫来源**：

| 来源 | 机制 | 依据 |
|---|---|---|
| Buff 1（Obsidian Skin 黑曜石皮） | `lavaImmune = true; fireWalk = true; buffImmune[24] = true;` | `Player.cs:9978-9983` |
| Buff 305（Lava Shark 坐骑） | `lavaImmune = true; lavaVision = true; fireWalk = true;` | `Player.cs:10008-10014` |
| **Lava Charm（ItemID 906）** | **不免疫**，只 `lavaMax += 420`（延长耐受时间） | `Player.cs:15307-15310` |
| Molten Charm（4038） | `lavaMax += 420` + `fireWalk = true` | `Player.cs:14569-14571`、`15307-15310` |
| Lava Skull（3999） | `lavaMax += 420` + `fireWalk = true` | `Player.cs:14594-14596`、`15307-15310` |
| Molten Skull Rose（4003） | `lavaMax += 420` + `fireWalk = true` + `lavaRose = true` | `Player.cs:14572-14575` |
| Lava Waders（908）/ 5000 | `waterWalk = true; fireWalk = true; lavaMax += 420; lavaRose = true;` | `Player.cs:15296-15302` |
| Obsidian Rose（1323） | 只 `lavaRose = true` | `Player.cs:15469` |

> **重要**：`lavaMax` 默认 0，每帧在 `Player.cs:18793` 清零后由饰品累加（`Player.cs:15307-15310`）。所以戴 Lava Charm 时 `lavaMax = 420` tick = 在熔岩里可白嫖 **7 秒**，之后才吃 80/次。

### C13. 溺水

**字段**（`Terraria\Player.cs`）：

| 字段 | 精确声明 | 行号 |
|---|---|---|
| `breath` | `public int breath = 200;` | `Player.cs:1521` |
| `breathMax` | `public int breathMax = 200;` | `Player.cs:1519` |
| `breathCD` | `public int breathCD;` | `Player.cs:1517` |
| `breathCDMax` | 属性，`get` 返回 **7**（Breathing Reed 时 ×2，Diving Helmet 时 ×6） | `Player.cs:3855-3869` |

**关键代码**（`Player.cs:23448-23481`）：

```csharp
if (flag)                                        // flag = 头部没入水中
{
    breathCD++;
    if (breathCD >= breathCDMax)                 // breathCDMax 默认 7
    {
        breathCD = 0;
        breath--;
        if (breath == 0)
        {
            SoundEngine.PlaySound(23);
        }
        if (breath <= 0)
        {
            lifeRegenTime = 0f;
            breath = 0;
            statLife -= 2;                        // ← 溺水伤害 2
            SetOrRequestSpectating(-1);
            if (statLife <= 0)
            {
                statLife = 0;
                KillMe(PlayerDeathReason.ByOther(1), 10.0, 0);
            }
        }
    }
}
else
{
    breath += 3;                                  // 出水每帧回 3
    if (breath > breathMax) { breath = breathMax; }
    breathCD = 0;
}
```

| 项目 | 数值 | 依据 |
|---|---|---|
| 起始 breath / breathMax | **200 / 200** | `Player.cs:1519-1521` |
| 水下消耗速率 | 每 **7 tick** −1 ⇒ 200 × 7 = **1400 tick ≈ 23.3 秒** | `Player.cs:23451`、`3855-3869` |
| 溺水伤害 | **2 / 每 7 tick**（未乘难度系数） | `Player.cs:23463` |
| 死亡 | `KillMe(ByOther(1), 10.0, 0)` | `Player.cs:23468` |
| 出水恢复 | **+3 / 帧** | `Player.cs:23475` |
| `gills`（鱼鳃） | `flag = Main.getGoodWorld && !flag;`（即 Good World 反转语义） | `Player.cs:23426-23429` |
| `accMerman` | 变人鱼并 `flag = false` | `Player.cs:23440-23447` |
| `accDivingHelm` | `breathCDMax *= 6`（⇒ 42 tick 才掉 1 点） | `Player.cs:3864-3867` |
| `hasBreathingReed` | `breathCDMax *= 2` | `Player.cs:3860-3863` |
| 是否有专家/大师缩放 | **未找到（确认不存在）**：`statLife -= 2` 是**直接扣血，绕过 `Hurt`**，因此不吃防御、不随难度变化 | `Player.cs:23448-23470` |

**触发条件 `flag` 的完整推导**（`Player.CheckDrowning`，`Player.cs:23391-23447`）：

```csharp
// Player.cs:23393
bool flag = Collision.DrownCollision(position, width, height, gravDir);
// Player.cs:23395
if (effectiveArmor.type == 250 || effectiveArmor.type == 4275)   // FishBowl 250 / GoldGoldfishBowl 4275
{
    flag = true;                                                  // 戴鱼缸 → 陆上也憋气
}
// Player.cs:23410 — 呼吸管探出水面
if (Main.tile[num, num3].liquid < 128) { ... flag = false; }
// Player.cs:23426
if (gills) { flag = Main.getGoodWorld && !flag; }                 // Buff 4 Gills，getGoodWorld 下反转
// Player.cs:23430
if (shimmering) { flag = false; }
// Player.cs:23434
if (mount.Active && mount.Type == 4) { flag = false; }            // 龟坐骑
// Player.cs:23440
if (accMerman) { if (flag) { merman = true; } flag = false; }      // 海神贝壳类
```

`Collision.DrownCollision`（`Terraria\Collision.cs:1395`）判定 `Collision.cs:1428`：`tile.liquid > 0 && !tile.lava() && !tile.shimmer() && ...` ⇒ **水与蜂蜜都会溺水，岩浆与微光不会**。

**相关物品 / Buff**：

| 效果 | 代码 |
|---|---|
| 海神贝壳 497 / MoonShell 861 / CelestialShell 3110 → `accMerman`（免溺水 + 变人鱼） | `Player.cs:15695-15702`、`15263-15272`、`15531-15540`；湿身 `AddBuff(34, 2)` `:25789-25796` |
| Buff 4 Gills（鳃药水）→ `gills = true` | `Player.cs:10059-10062` |
| 潜水头盔 268（头甲）/ DivingGear 394 / JellyfishDivingGear 1860 / ArcticDivingGear 1861 | `Player.cs:13363-13366`、`15327-15331`、`15364-15367`、`15377-15382` |
| 呼吸管 **186 BreathingReed**（`hasBreathingReed`） | `Player.cs:4136-4149` |
| 鱼缸 250 / 金鱼缸 4275 → 陆上也溺 | `Player.cs:23394-23398` |
| 蜂蜜 | `Player.cs:27968-27972` `AddBuff(48, 1800)` + `honeyWet = true`（会溺水） |
| 微光 | `Player.cs:27955-27966` `AddBuff(353, 60)`（不溺水；Buff 353 使 `frozen` 并重置 `fallStart`，`Player.cs:11887-11891`） |
| 专家模式雪地 | `Player.cs:28225-28227` `if (Main.expertMode && ZoneSnow && wet && !lavaWet && !honeyWet && !arcticDivingGear && environmentBuffImmunityTimer == 0) { AddBuff(46, 150); }`（Chilled） |

**未找到**：`Player.oxygenInWater` 类字段（`Player.cs` 内 `oxygen` 零匹配）。

### C14. 摔落伤害

**字段**：

| 字段 | 精确声明 | 行号 |
|---|---|---|
| `fallStart` | `public int fallStart;` | `Player.cs:2881` |
| `fallStart2` | `public int fallStart2;` | `Player.cs:2883` |
| `extraFall` | `public int extraFall;` | `Player.cs:2725` |
| `noFallDmg` | `public bool noFallDmg;` | `Player.cs:2765` |
| `hasWings` | `public bool hasWings;` | `Player.cs:1335` |
| `stoned` | `public bool stoned;` | `Player.cs:1025` |
| `maxFallSpeed` | `public float maxFallSpeed = 10f;` | `Player.cs:2517` |

**完整公式**（`Player.cs:25503-25593`）：

```csharp
if (i == Main.myPlayer)
{
    if (velocity.Y <= 0f)
    {
        fallStart2 = (int)(position.Y / 16f);
    }
    if (velocity.Y == 0f)                     // 落地帧
    {
        int num10 = 25;                       // ← 基础阈值 25 格
        num10 += extraFall;
        if (mount.Active)
        {
            num10 += mount.ExtraFall;
        }
        int num11 = (int)(position.Y / 16f) - fallStart;      // ← 实际落差（格）
        ...
        else if (((gravDir == 1f && num11 > num10) || (gravDir == -1f && num11 < -num10)) && !noFallDmg && !hasWings)
        {
            immune = false;
            int num17 = (int)((float)num11 * gravDir - (float)num10) * 10;    // ← (落差 − 阈值) × 10
            if (mount.Active)
            {
                num17 = (int)((float)num17 * mount.FallDamage);
            }
            if (num17 > 0)
            {
                Hurt(PlayerDeathReason.ByOther(0), num17, 0);
            }
        }
        fallStart = (int)(position.Y / 16f);
    }
    if (jump > 0 || rocketDelay > 0 || wet || slowFall || (double)num6 < 0.8 || tongued)
    {
        fallStart = (int)(position.Y / 16f);   // 这些状态持续重置落差起点
    }
}
```

| 项目 | 数值 | 依据 |
|---|---|---|
| 阈值（安全落差） | **25 格** + `extraFall` + 坐骑 `ExtraFall` | `Player.cs:25511-25516` |
| 伤害公式 | `(落差 − 阈值) × 10`（整数截断） | `Player.cs:25574` |
| 专家/大师缩放 | **未找到（确认不存在）**：`Player.cs:25571-25586` 内无 `Main.expertMode` / `GameDifficultyData`。唯一难度相关的是**石化初伤**：`Player.cs:25432` `int damage = (int)(20.0 * GameDifficultyData.EnemyDamageMultiplier.Sample(Main.Difficulty));`（Classic 20 / Expert 40 / Master 60 / Legendary ≈106） | `Player.cs:25432`、`25571-25586` |
| **无敌帧不能免摔伤** | 摔伤分支先执行 `immune = false;` 再调 `Hurt(...)` | `Player.cs:25573` |
| 绝对免疫 | `noFallDmg`（幸运马蹄铁等）或 `hasWings`（翅膀）⇒ 整个分支跳过 | `Player.cs:25571` |
| 云瓦片免伤 | `TileID.Sets.Clouds[tile.type] \|\| tile.type == 666 \|\| (tile.type == 19 && tile.frameY / 18 == 49)` ⇒ `num11 = 0` | `Player.cs:25544-25556` |
| 石化（`stoned`） | 阈值降为 **2 格**、每格 **20 点**：`(num11 * gravDir − 2) * 20`；且**不受 `noFallDmg` / `hasWings` 保护** | `Player.cs:25558-25569` |
| 飞行坐骑 / 矿车 | `num11 = 0` | `Player.cs:25518-25534` |
| 落差记录点 | `fallStart` 在落地（`Player.cs:25588`）、起跳/湿身/缓落/低重力/被舌拉（`25590-25593`）、水行走（`23749-23757`、`23785-23793`）、重力翻转（`26916-26943`）、绳索（`26516`）、钩爪（`22841`）、爬墙（`21842`-`22048`）、弹跳块（`35177`）、受伤击退（`38838`）、复活（`37754`、`38014-38015`）等时机重置；`fallStart2` 在 `velocity.Y <= 0f` 时**每帧**更新（`Player.cs:25505-25508`），供矿车轨道使用（`28329`） |
| **液体免摔伤** | 通过 `wet`：`Player.cs:25590-25593` 里 `wet` 每帧重置 `fallStart` ⇒ 落差无法累积。而 `Player.cs:27952` `Collision.WetCollision(position, width, height)` 只判断 `Main.tile[i,j].liquid > 0`（`Collision.cs:1618`，**不排除 lava**），随后 `Player.cs:28074 wet = true;` ⇒ **水 / 岩浆 / 蜂蜜 / 微光都免摔伤** |
| 高空低重力免摔伤 | `(double)num6 < 0.8` 也重置 `fallStart`（`num6` 为高度重力系数，`Player.cs:24878-24898`） | `Player.cs:25590` |
| **羽落药水免摔伤** | `slowFall` 在 `25590` 的重置列表内 ⇒ 羽落药水**完全免摔伤** | `Player.cs:25590`、`2783` |
| `extraFall` 来源 | 蛙腿 `+15`（`:19739-19743`）、月亮领主腿 `+10`（`:19744-19748`）、ItemID 3990/3994/3995/3996 各种气球靴 `+10`（`:14520-14565`） | — |
| `noFallDmg` 来源 | 158 LuckyHorseshoe（`:15157-15161`）、5331（`:14948-14956`）、1250/1251/1252（`:14957-14977`）、3250/3251/3252（`:14978-14998`）、396（`:15332-15337`）。**不含马蹄铁的跳跃气球只给 `jumpBoost`，不免摔伤** | — |

**规划器安全落差**：`落差 ≤ 25 格`（含 `extraFall` 时再放宽）。例：`extraFall = 10`（Lucky Horseshoe 类）时安全落差 = 35 格。

### C15. 陷阱

#### TileID（`Terraria.ID\TileID.cs`）

| 陷阱 | 常量 | 值 | 行号 |
|---|---|---|---|
| 尖刺 | `TileID.Spikes` | **48** | `TileID.cs:533` |
| 木刺 | `TileID.WoodenSpikes` | **232** | `TileID.cs:901` |
| 压力板（通用） | `TileID.PressurePlates` | **135** | `TileID.cs:707` |
| **陷阱（全部机关陷阱共用）** | `TileID.Traps` | **137** | `TileID.cs:711` |
| 巨石 | `TileID.Boulder` | **138** | `TileID.cs:713` |
| 地雷 | `TileID.LandMine` | **210** | `TileID.cs:857` |
| 加权压力板 | `TileID.WeightedPressurePlate` | **428** | `TileID.cs:1293` |
| 间歇泉 | `TileID.GeyserTrap` | **443** | `TileID.cs:1323` |
| 爆炸物（电路触发） | `TileID.Explosives` | **141** | `TileID.cs:719` |
| **地狱火 TNT 桶** | `TileID.TNTBarrel` | **654** | `TileID.cs:1745` |
| 蹦床巨石 | `TileID.BouncyBoulder` | 664 | `TileID.cs:1765` |
| 生命水晶巨石 | `TileID.LifeCrystalBoulder` | 665 | `TileID.cs:1767` |
| 彩虹巨石 | `TileID.RainbowBoulder` | 711 | `TileID.cs:1859` |
| 岩浆巨石 | `TileID.LavaBoulder` | 713 | `TileID.cs:1863` |
| 蜘蛛巨石 | `TileID.SpiderBoulder` | 714 | `TileID.cs:1865` |
| 尖刺块 | `TileID.SpikeBlock` | 745 | `TileID.cs:1927` |
| 巨石块 | `TileID.BoulderBlock` | 749 | `TileID.cs:1935` |
| 伤害尖刺块 | `TileID.DamagingSpikeBlock` | 750 | `TileID.cs:1937` |
| 巨石集合 | `TileID.Sets.Boulders` | `{138, 484, 664, 665, 711, 712, 713, 714, 715, 716}` | `TileID.cs:195` |

**「巨石是瓦片还是弹幕」**：**两者都有**。
- 自然生成的巨石是**瓦片** `TileID.Boulder = 138`（`TileID.cs:713`），被电线触发后消失并生成**弹幕**：`ProjectileID.Boulder = 99`（`ProjectileID.cs:650`）。
- 巨石雕像（`TileID.BoulderStatue = 531`，`TileID.cs:1499`）被电线触发也生成弹幕 99、伤害 70、击退 10（`Terraria\Wiring.cs:1998-2016`）：

```csharp
case 531:
{
    ...
    if (CheckMech(num90, num91, 900))
    {
        Vector2 vector2 = new Vector2(num90 + 1, num91) * 16f;
        vector2.Y += 28f;
        int num92 = 99;              // ProjectileID.Boulder
        int damage3 = 70;
        float knockBack3 = 10f;
        Projectile.NewProjectile(GetProjectileSource(num90, num91), (int)vector2.X, (int)vector2.Y, 0f, 0f, num92, damage3, knockBack3, Main.myPlayer);
    }
```

#### 各陷阱的判定方式与弹幕 ID

**机关陷阱全部是 `TileID.Traps = 137`，用 `frameY / 18` 区分样式**（`Terraria\Wiring.cs:1764-1985`）：

| `frameY/18` | 陷阱 | 弹幕 ID | 伤害 | 攻速 | 依据 |
|---|---|---|---|---|---|
| 0 | **飞镖陷阱 Dart Trap** | `ProjectileID.PoisonDartTrap` = **184** | 40 | 12 | `Wiring.cs:1790-1795`；`ProjectileID.cs:820` |
| 1 | （同 0 组，另一朝向） | 184 | 40 | 12 | `Wiring.cs:1790-1795` |
| 2 | （同 0 组） | 184 | 40 | 12 | 同上 |
| 5 | （同 0 组） | 184 | 40 | 12 | 同上 |
| 3 | **尖球陷阱 Spiky Ball Trap** | `ProjectileID.SpikyBallTrap` = **185** | 40 | 随机 | `Wiring.cs:1827-1856`；`ProjectileID.cs:822` |
| 4 | **长矛陷阱 Spear Trap** | `ProjectileID.SpearTrap` = **186** | 60 | 8 | `Wiring.cs:1859-1890`；`ProjectileID.cs:824` |
| −8 | **喷火陷阱 Flame Trap** | `ProjectileID.FlamethrowerTrap` = **187** | 40 | 5 | `Wiring.cs:1926-1940`；`ProjectileID.cs:826` |
| −6 | （同型，向下） | 186 | 60 | 8 | `Wiring.cs:1969-1979` |
| −9 | 加强飞镖 | 184 | 40 | 12 | `Wiring.cs:1910-1925` |
| −7 | 尖球 | 185 | 40 | 4 | `Wiring.cs:1942-1968` |
| −10 | 飞镖 | 98 | 20 | 12 | `Wiring.cs:1894-1909` |

> 补：`frameY/18 == 2` 与 `−8` 用的都是 `ProjectileID.FlamethrowerTrap = 187`（`ProjectileID.cs:826`），数值已确认，名称先前误判为未知——**已更正**。

其他关键弹幕 ID：

| 名称 | 常量 | 值 | 行号 |
|---|---|---|---|
| SpikyBall | `ProjectileID.SpikyBall` | 24 | `ProjectileID.cs:500` |
| PoisonDart（普通飞镖） | `ProjectileID.PoisonDart` | 98 | `ProjectileID.cs:648` |
| **Boulder（巨石）** | `ProjectileID.Boulder` | **99** | `ProjectileID.cs:650` |
| Landmine（地雷） | `ProjectileID.Landmine` | **164** | `ProjectileID.cs:780` |
| PoisonDartTrap | | 184 | `ProjectileID.cs:820` |
| SpikyBallTrap | | 185 | `ProjectileID.cs:822` |
| SpearTrap | | 186 | `ProjectileID.cs:824` |
| **FlamethrowerTrap（喷火陷阱）** | `ProjectileID.FlamethrowerTrap` | **187** | `ProjectileID.cs:826` |
| Spike | `ProjectileID.Spike` | 352 | `ProjectileID.cs:1156` |
| VenomDartTrap | | 980 | `ProjectileID.cs:2412` |
| **TNTBarrel** | `ProjectileID.TNTBarrel` | **1002** | `ProjectileID.cs:2456` |
| MiniBoulder | | 1005 | `ProjectileID.cs:2462` |
| BouncyBoulder | | 1013 | `ProjectileID.cs:2478` |
| LifeCrystalBoulder | | 1014 | `ProjectileID.cs:2480` |
| MoonBoulder | | 1021 | `ProjectileID.cs:2494` |
| RainbowBoulder | | 1047 | `ProjectileID.cs:2546` |
| LavaBoulder | | 1053 | `ProjectileID.cs:2558` |
| SpiderBoulder | | 1054 | `ProjectileID.cs:2560` |
| BoulderThatSpawnsPet | | 1057 | `ProjectileID.cs:2566` |
| GeyserTrap | `ProjectileID.GeyserTrap` | **654** | `ProjectileID.cs:1760` |

**地雷**（`TileID.LandMine = 210`）：`Wiring.cs:3088-3096`

```csharp
public static void ExplodeMine(int i, int j)
{
    if (Main.netMode != 1)
    {
        WorldGen.KillTile(i, j, fail: false, effectOnly: false, noItem: true);
        NetMessage.SendTileSquare(-1, i, j);
        Projectile.NewProjectile(GetProjectileSource(i, j), i * 16 + 8, j * 16 + 8, 0f, 0f, 164, 250, 10f, Main.myPlayer);
    }
}
```

**间歇泉**（`TileID.GeyserTrap = 443`）：`Wiring.cs:3098-3132`，弹幕 654、伤害 20、击退 2，冷却 `CheckMech(num2, j, 200)`。

**爆炸物**（`TileID.Explosives = 141`）：`Wiring.cs:2039-2042`

```csharp
case 141:
    WorldGen.KillTile(i, j, fail: false, effectOnly: false, noItem: true);
    NetMessage.SendTileSquare(-1, i, j);
    Projectile.NewProjectile(GetProjectileSource(i, j), i * 16 + 8, j * 16 + 8, 0f, 0f, 108, 500, 10f, Main.myPlayer);
    break;
```

**TNT 桶**（`TileID.TNTBarrel = 654` / `ProjectileID.TNTBarrel = 1002`）：`Wiring.cs` 内**没有** `case 654`（**未找到**）。弹幕 1002 的默认属性（`Projectile.cs:9530-9544`）本身就是**一次性爆炸判定框**：

```csharp
else if (type == 1002)
{
    width = 260;
    height = 260;
    aiStyle = 16;
    friendly = true;
    hostile = true;
    penetrate = -1;
    tileCollide = false;
    alpha = 255;
    timeLeft = 2;
    trap = true;
    usesIDStaticNPCImmunity = true;
    idStaticNPCHitCooldown = 15;
}
```

⇒ TNT 桶的**爆炸半径 10**（`Projectile.cs:79070-79073`）、对玩家自伤判定框 260×260、`trap = true`（会触发陷阱来源判定）；伤害减半（`Projectile.cs:12976-12979` `if (type == 1002) { dmg /= 2; }`）。

**如何识别陷阱瓦片 / 是否已触发**：

- 陷阱瓦片：`Main.tile[x, y].type == 137`（机关陷阱）/ `443`（间歇泉）/ `210`（地雷）/ `654`（TNT 桶）/ `141`（爆炸物）/ `48` / `232`（尖刺，非机关）。
- 480 机关陷阱的**朝向/样式**：`Main.tile[x, y].frameY / 18`（`Wiring.cs:1766`）与 `frameX / 18`（`Wiring.cs:1864`）。
- 是否已触发：源码内**未找到**「陷阱已触发」这一持久字段；机关陷阱是一次性触发（触发时移除瓦片或生成弹幕），间歇泉/飞镖等由 `Wiring.CheckMech`（`Wiring.cs:1778`、`1814`、`1860` 等）的**冷却计时**控制，冷却值见调用第二参数（飞镖 200、长矛 90、尖球 300、间歇泉 200）。
- 弹幕侧：`Projectile.trap`（`Projectile.cs:453` `public bool trap;`）标记「来自陷阱」，影响 `SelfHurtPlayers` 的 `deadMansSweater` 减伤与成就（`Projectile.cs:15142-15149`）。
- 压力板：`TileID.PressurePlates = 135`、`WeightedPressurePlate = 428`；`TileID.Frame`/`frameY` 表示按下状态（`Wiring.cs:296-310` 的 `Main.tile[i, j].frameY = 18` / `= 0` 即「开/关」）。
- 电线字段：`Main.tile[x, y].wire` / `wire2/3/4`（本次未逐条读出声明，**不确定**具体访问器名）；触发入口 `Wiring.TripWire`（`Wiring.cs:286`）、`Wiring.CheckMech`。

**巨石相关实例判定**（`Projectile.cs:79033` 分支里 780/781/782/804/783/863 都是半径 3，`796/797/798/809` 是半径 7）：

```csharp
if (type == 29 || type == 470 || type == 637 || type == 796 || type == 797 || type == 798 || type == 809)
{
    num6 = 7;
}
```

### C16. 敌对生物识别

**核心字段**（`Terraria\NPC.cs`）：

| 字段 | 精确声明 | 行号 |
|---|---|---|
| `active` | `public bool active;` | `NPC.cs:6012` |
| `damage` | `public int damage;` | `NPC.cs:6468` |
| `lifeMax` | `public int lifeMax;` | `NPC.cs:6488` |
| `immune` | `public int[] immune = new int[256];`（**按 buff 索引的 buff 免疫数组，不是无敌帧！**） | `NPC.cs:6446` |
| `boss` | `public bool boss;` | `NPC.cs:6520` |
| `townNPC` | `public bool townNPC;` | `NPC.cs:6544` |
| `friendly` | `public bool friendly;` | `NPC.cs:6570` |
| `chaseable` | `public bool chaseable = true;` | `NPC.cs:6268` |
| `dontTakeDamage` | `public bool dontTakeDamage;` | `NPC.cs:6532` |
| `immortal` | `public bool immortal;` | `NPC.cs:6266` |
| `SpawnedFromStatue` | `public bool SpawnedFromStatue;` | `NPC.cs:6068` |
| `noTileCollide` | `public bool noTileCollide;` | `NPC.cs:6514` |
| `realLife` | `public int realLife = -1;` | `NPC.cs:6168` |
| `netAlways` | `public bool netAlways;` | `NPC.cs:6154` |
| `CountsAsACritter` | **属性**：`get { if (lifeMax <= 5 && damage == 0 && type != 594) { return type != 686; } return false; }` | `NPC.cs:6911-6921` |

> **⚠ 常见误解**：`NPC.immune[]` 是「**buff 免疫表**」（长度 256，按 BuffID 索引），**不是**受击无敌帧。NPC 的受击无敌用 `immuneTime`/`immune` 其它成员（本次未细读，**不确定**）。

> **⚠ 另一误解**：`NPC.friendly` 对本任务的语义是「**不打玩家的友方 NPC**」，而 `NPC.townNPC` 才是「城镇 NPC」。两者都不是「是否敌对」的完整判据。

**推荐的「敌人」判据**（对齐游戏自身逻辑）：

游戏内部判定「可被玩家攻击的目标」用 `NPC.CanBeChasedBy`（`NPC.cs:91140-91151`）：

```csharp
public bool CanBeChasedBy(object attacker = null, bool ignoreDontTakeDamage = false)
{
    if (active && chaseable && lifeMax > 5 && (!dontTakeDamage || ignoreDontTakeDamage) && !friendly)
    {
        if (!DebugOptions.LetProjectilesAimAtTargetDummies)
        {
            return !immortal;
        }
        return true;
    }
    return false;
}
```

⇒ **简单回避策略推荐判据**：

```
is_enemy(npc) := npc.active
              && !npc.friendly
              && !npc.townNPC
              && !npc.CountsAsACritter        // NPC.cs:6911
              && npc.damage > 0
              && !npc.dontTakeDamage
              && !npc.SpawnedFromStatue
```

`NPCID.Sets` 谓词（`Terraria.ID\NPCID.cs`）：

| 集合 | 内容 | 行号 |
|---|---|---|
| `TownCritter` | 小镇生物（小动物变体）ID 列表 | `NPCID.cs:4844` |
| `CountsAsCritter` | 小动物 ID 列表（**注意：这是 NPCID.Sets 里的一个 bool[]，与 NPC.CountsAsACritter 属性不同**） | `NPCID.cs:4846` |
| `CritterThatCanTurnOnPlayers` | 会转而攻击玩家的小动物 | `NPCID.cs:4798` |
| `IsGoldCritter` | 金动物 | `NPCID.cs:4475` |
| `TakesDamageFromHostilesWithoutBeingFriendly` | 会被敌怪打的「非友方」 | `NPCID.cs:4800` |
| `BoundTownNPCs` | 被束缚的城镇 NPC | `NPCID.cs:4808` |

致命性过滤：`npc.damage > 0 && !npc.friendly` 是游戏自己在多处使用的写法（例：`NPC.cs:45564`、`48338`、`93576`）。

**生成抑制**（`Terraria\NPC.cs`）：

| 项目 | 数值/字段 | 依据 |
|---|---|---|
| `spawnRate`（默认） | `private static int defaultSpawnRate = 600;` | `NPC.cs:6190` |
| `maxSpawns`（默认） | `private static int defaultMaxSpawns = 5;` | `NPC.cs:6192` |
| 生成循环开关 | `private static bool noSpawnCycle = false;` | `NPC.cs:6186` |
| 生成入口 | `public static void SpawnNPC()`（`NPC.cs:80990`），首行 `if (noSpawnCycle) { noSpawnCycle = false; return; }`（`80992-80996`） | `NPC.cs:80990-80999` |
| 多玩家 | `maxSpawns = (int)(defaultMaxSpawns * (2.0 + 0.3 * numberOfActivePlayers))` | `NPC.cs:774`、`784` |
| 上下限 | `spawnRate` ≥ `defaultSpawnRate * 0.1`；`maxSpawns` ≤ `defaultMaxSpawns * 3` | `NPC.cs:750-756` |
| 内部生成器 | `public class Spawner`（`NPC.cs:144`），`new Spawner().SpawnNPC()` | `NPC.cs:144`、`80998` |

> 「城镇 NPC / 向日葵 / 和平蜡烛降低生成」的具体系数在 `Spawner.SpawnNPC`（`NPC.cs:144` 起）内，本次未逐行展开（**不确定**精确系数）。`NPC.cs:476-930` 是各生物群系的 `spawnRate`/`maxSpawns` 修正表。

### C17. 墓碑 / 墓石

**全部是同一个瓦片 `TileID.Tombstones = 85`**（`Terraria.ID\TileID.cs:607`），用**样式（style）**区分外观。物品侧有独立 ItemID（`ItemID.cs`）：

| 物品 | 常量 | ItemID | 行号 |
|---|---|---|---|
| Tombstone（墓碑） | `ItemID.Tombstone` | **321** | `ItemID.cs:2188` |
| Grave Marker | `ItemID.GraveMarker` | **1173** | `ItemID.cs:3892` |
| Cross Grave Marker | `ItemID.CrossGraveMarker` | **1174** | `ItemID.cs:3894` |
| Headstone | `ItemID.Headstone` | **1175** | `ItemID.cs:3896` |
| Obelisk | `ItemID.Obelisk` | **1177** | `ItemID.cs:3900` |

弹幕侧（玩家死亡时生成的「落墓碑」弹幕）：

| 弹幕 | ProjectileID | 值 | 行号 |
|---|---|---|---|
| Tombstone | `ProjectileID.Tombstone` | **43** | `ProjectileID.cs:538` |
| GraveMarker | | **201** | `ProjectileID.cs:854` |
| CrossGraveMarker | | **202** | `ProjectileID.cs:856` |
| Headstone | | **203** | `ProjectileID.cs:858` |
| Obelisk | | **205** | `ProjectileID.cs:862` |
| （527..531 一组奖励墓碑） | | 527-531 | `ProjectileID.cs` 未逐条读，**不确定** |

`ProjectileID.Sets.IsAGravestone`（`ProjectileID.cs:29`）：

```csharp
public static bool[] IsAGravestone = Factory.CreateBoolSet(false, 202, 201, 204, 43, 203, 205, 527, 528, 529, 530, 531);
```

**死亡时如何放置**（`Terraria\Player.cs:39263` → `Player.DropTombstone(...)`，`Player.cs:39426-39463`）：

```csharp
public void DropTombstone(long coinsOwned, NetworkText deathText, int hitDirection)
{
    if (Main.netMode != 1)
    {
        float num;
        for (num = (float)Main.rand.Next(-35, 36) * 0.1f; num < 2f && num > -2f; num += ...) { }
        int num2 = Main.rand.Next(6);
        if (coinsOwned <= 100000)
        {
            num2 = ((num2 != 0) ? (200 + num2) : 43);      // 201..205，或 43
        }
        else
        {
            num2 = Main.rand.Next(5);
            num2 += 527;                                   // 527..531（金币 > 100000 时的稀有墓碑）
        }
        ...
        int num5 = Projectile.NewProjectile(projectileSource_Misc, ..., num2, damage, num3, Main.myPlayer, num4);
        ...
        Main.projectile[num5].miscText = miscText;          // 墓志铭文本
    }
}
```

**落墓碑弹幕如何变成瓦片**（`Terraria\Projectile.cs:25655-25692`，`aiStyle == 17`）：

```csharp
if (owner != Main.myPlayer) { return; }
int num148 = (int)((base.position.X + (float)(width / 2)) / 16f);
int num149 = (int)((base.position.Y + (float)height - 4f) / 16f);
if (Main.tile[num148, num149] == null) { return; }
int style = 0;
if (type >= 201 && type <= 205)
{
    style = type - 200;                     // 201→1, 202→2, 203→3, 204→4, 205→5
}
if (type >= 527 && type <= 531)
{
    style = type - 527 + 6;                 // 527→6 ... 531→10
}
bool flag7 = false;
TileObject objectData = default(TileObject);
if (TileObject.CanPlace(num148, num149, 85, style, direction, out objectData))
{
    flag7 = TileObject.Place(objectData);
}
if (flag7)
{
    NetMessage.SendObjectPlacement(-1, num148, num149, objectData.type, objectData.style, ...);
    SoundEngine.PlaySound(0, num148 * 16, num149 * 16);
    int num150 = Sign.ReadSign(num148, num149);
    if (num150 >= 0)
    {
        Sign.TextSign(num150, miscText);     // 墓志铭写进 Sign
        ...
    }
    Kill();
}
```

**高度（执行器挖矿用）**：`TileObjectData` 里 tile 85 用 `Style2x2`（`Terraria.ObjectData\TileObjectData.cs:3948-3949`）：

```csharp
newTile.CopyFrom(Style2x2);
newTile.StyleHorizontal = true;
...
addTile(85);
```

`Style2x2` 由 `addBaseTile(out Style2x2)`（`TileObjectData.cs:3461`）建立，基底定义在 `TileObjectData.cs:3455-3460`：

```csharp
newTile.AnchorBottom = new AnchorData(AnchorType.SolidTile | AnchorType.SolidWithTop | AnchorType.Table | AnchorType.SolidSide, newTile.Width, 0);
newTile.UsesCustomCanPlace = true;
newTile.CoordinateHeights = new int[2] { 16, 16 };
newTile.CoordinateWidth = 16;
newTile.CoordinatePadding = 2;
newTile.LavaDeath = true;
```

⇒ **墓碑是 2 格宽 × 2 格高**（`CoordinateHeights` 两个 16 ⇒ 高 2 格；`Width` = 2 由 2×2 命名与 `CoordinateWidth` 16+padding 2 的两列决定）。**LavaDeath = true**⇒ 岩浆会摧毁它。

> 不确定：`addBaseTile` 的具体实现（`TileObjectData.cs:2001`）本次未逐行读，`Width == 2` 是从 `Style2x2` 命名 + `CoordinateHeights.Length == 2` 推断；若规划器需要严格保证，请再核对 `TileObjectData.cs:2001-2008`。

**死亡 / 复活字段**：

| 字段 | 精确声明 | 行号 |
|---|---|---|
| `dead` | `public bool dead;` | `Player.cs:1629` |
| `respawnTimer` | `public int respawnTimer;` | `Player.cs:1635` |
| `respawnTimerMax` | `public static readonly int respawnTimerMax = 3600;` | `Player.cs:1637` |
| `dead` 置 true | `Player.cs:39226`（在 `KillMe` 内） | `Player.cs:39226` |
| `respawnTimer` 设置 | `respawnTimer = GetRespawnTime(pvp);` | `Player.cs:39228` |
| 计数递减 | `respawnTimer = Utils.Clamp(respawnTimer - 1, 0, respawnTimerMax);` | `Player.cs:17179`、`17188` |

**默认复活计时**（`Player.cs:39378-39411`）：

```csharp
private int GetRespawnTime(bool pvp)
{
    int num = 600;                            // ← 默认 600 tick = 10 秒
    bool flag = false;
    if (Main.netMode != 0 && !pvp)
    {
        flag = AnyBossHindersRespawnTime();
    }
    if (flag) { num += 600; }                 // Boss 在场 +10 秒
    if (Main.expertMode) { num = (int)((double)num * 1.5); }   // 专家 ×1.5
    if (flag && Main.getGoodWorld && Main.netMode != 0) { ... num *= 2; }
    return num;
}
```

⇒ **单人普通模式：600 tick = 10 秒**；专家：900 tick = 15 秒；Boss 在场（多人）：+600。

### C18. 自伤与无敌帧

自伤细节见 §A6。**无敌帧字段**（`Terraria\Player.cs`）：

| 字段 | 精确声明 | 行号 |
|---|---|---|
| `immune` | `public bool immune;` | `Player.cs:1419` |
| `immuneNoBlink` | `public bool immuneNoBlink;` | `Player.cs:1421` |
| `immuneTime` | `public int immuneTime;` | `Player.cs:1423` |
| `immuneAlphaDirection` | `public int immuneAlphaDirection;` | `Player.cs:1425` |
| `immuneAlpha` | `public int immuneAlpha;` | `Player.cs:1427` |
| `hurtCooldowns` | `public int[] hurtCooldowns = new int[ImmunityCooldownID.Count];` | `Player.cs:3159` |

**受伤后无敌帧长度**（`Player.cs:38689-38698`）：

```csharp
int num10 = (pvp ? 8 : ((num2 != 1.0) ? (longInvince ? 80 : 40) : (longInvince ? 40 : 20)));
if (cooldownCounter == ImmunityCooldownID.General)
{
    immune = true;
    immuneTime = num10;
}
else if (hurtCooldowns[cooldownCounter] == 0 || flag2)
{
    hurtCooldowns[cooldownCounter] = num10;
}
```

| 场景 | 无敌帧 | 依据 |
|---|---|---|
| PVP | **8** | `Player.cs:38689` |
| **普通伤害（默认）** | **40 tick ≈ 0.67 秒** | 同上 |
| 伤害恰为 1（`num2 == 1.0`） | 20 | 同上 |
| `longInvince`（十字项链等） | **80**（伤害 1 时为 40） | 同上 |

**递减循环** `Player.UpdateImmunity()`（`Player.cs:18986`），由 `Update` 在 `Player.cs:25667` 调用，**每 tick 一次**：

```csharp
if (immune)                                          // 18988
{
    immuneTime--;                                    // 18990
    if (immuneTime <= 0) { immune = false; immuneNoBlink = false; }   // 18991-18994
    ...
}
else { immuneAlpha = 0; }
for (int i = 0; i < hurtCooldowns.Length; i++)       // 19017
{
    if (hurtCooldowns[i] > 0) { hurtCooldowns[i]--; } // 19019-19022
}
```

**给执行器的时序建议**（基于代码事实）：
- 雷管从抛出到爆炸 = **300 tick = 5 秒**（`Projectile.cs:10996-10999`）。
- 爆炸帧自伤要求 `!localPlayer.immune`（`Projectile.cs:15113`）。
- 只要在爆炸那一刻 `immune == true` 就**完全免伤**，无论距离多近。
- 伤害来源不同 ⇒ 冷却桶不同：`ImmunityCooldownID.General = -1`（`Terraria.ID\ImmunityCooldownID.cs:48`）、`ImmunityCooldownID.Lava` 为独立桶，`ImmunityCooldownID.Count = 6`（`ImmunityCooldownID.cs:62`）。所以熔岩无敌帧**不能**抵挡爆炸（爆炸用默认 `cooldownCounter = -1` ⇒ General 桶）。
- `Hurt` 的最小伤害钳制：`if (num2 < 1.0) { num2 = 1.0; }`（`Player.cs:38681-38684`）。

---

## D. 有用的玩家能力

### D19. 移动控制字段

**输入字段**（全部是 **public 字段**，非属性，声明在 `Terraria\Player.cs`）：

| 成员 | 精确声明 | 行号 |
|---|---|---|
| `controlLeft` | `public bool controlLeft;` | `Player.cs:1721` |
| `controlRight` | `public bool controlRight;` | `Player.cs:1723` |
| `controlUp` | `public bool controlUp;` | `Player.cs:1725` |
| `controlDown` | `public bool controlDown;` | `Player.cs:1727` |
| `controlJump` | `public bool controlJump;` | `Player.cs:1729` |
| `controlUseItem` | `public bool controlUseItem;` | `Player.cs:1731` |
| `controlUseTile` | `public bool controlUseTile;` | `Player.cs:1733` |
| `controlThrow` | `public bool controlThrow;` | `Player.cs:1735` |
| `controlInv` | `public bool controlInv;` | `Player.cs:1737` |
| `controlHook` | `public bool controlHook;` | `Player.cs:1739` |
| `controlTorch` | `public bool controlTorch;` | `Player.cs:1741` |
| `controlMap` | `public bool controlMap;` | `Player.cs:1743` |
| `controlSmart` | `public bool controlSmart;` | `Player.cs:1745` |
| `controlMount` | `public bool controlMount;` | `Player.cs:1747` |
| `releaseUseItem` | `public bool releaseUseItem;` | `Player.cs:1757` |
| `releaseHook` | `public bool releaseHook;` | `Player.cs:1763` |
| `controlQuickMana` | `public bool controlQuickMana;` | `Player.cs:1795` |
| `controlQuickHeal` | `public bool controlQuickHeal;` | `Player.cs:1797` |
| `lastItemUseAttemptSuccess` | `public bool lastItemUseAttemptSuccess;` | `Player.cs:1811` |
| `channel` | `public bool channel;` | `Player.cs:1879` |
| `dead` | `public bool dead;` | `Player.cs:1629` |
| `respawnTimer` | `public int respawnTimer;` | `Player.cs:1635` |
| `statLife` | `public int statLife = 100;` | `Player.cs:1927` |
| `statLifeMax2` | `public int statLifeMax2 = 100;` | `Player.cs:1925` |
| `statLifeMax` | `public int statLifeMax = 100;` | `Player.cs:1923` |
| `itemAnimation` | `public int itemAnimation;` | `Player.cs:3013` |
| `itemAnimationMax` | `public int itemAnimationMax;` | `Player.cs:3015` |
| `itemTime` | `public int itemTime;` | `Player.cs:3017` |
| `itemTimeMax` | `public int itemTimeMax;` | `Player.cs:3019` |
| `toolTime` | `public int toolTime;` | `Player.cs:3021` |
| `reuseDelay` | `public int reuseDelay;` | `Player.cs:1443` |
| `inventory` | `public Item[] inventory = new Item[59];` | `Player.cs:1575` |
| `hurtCooldowns` | `public int[] hurtCooldowns = new int[ImmunityCooldownID.Count];` | `Player.cs:3159` |
| `grappling` | `public int[] grappling = new int[20];` | `Player.cs:2739` |
| `mount` | `public Mount mount;` | `Player.cs:2125` |

**属性（只读表达式体，不可直接赋值）**：

| 成员 | 精确声明 | 行号 |
|---|---|---|
| `selectedItem` | `public int selectedItem => selectedItemState.Selected;` | `Player.cs:3851` |
| `HeldItem` | `public Item HeldItem => inventory[selectedItem];` | `Player.cs:3853` |
| `ItemTimeIsZero` | `public bool ItemTimeIsZero => itemTime == 0;` | `Player.cs:4064` |
| `ItemAnimationJustStarted` | `public bool ItemAnimationJustStarted => itemAnimation == itemAnimationMax - 1;` | `Player.cs:4066` |
| `breathCDMax` | 属性（见 §C13） | `Player.cs:3855-3869` |

**不在 Player.cs 内、而是继承自 `Terraria\Entity.cs`**（`Player.cs:45` → `public class Player : Entity, IFixLoadedData`）：

| 成员 | 精确声明 | 行号 |
|---|---|---|
| `whoAmI` | `public int whoAmI;` | `Entity.cs:8` |
| `position` | `public Vector2 position;` | `Entity.cs:10` |
| `velocity` | `public Vector2 velocity;` | `Entity.cs:12` |
| `direction` | `public int direction = 1;` | `Entity.cs:20` |
| `width` | `public int width;` | `Entity.cs:22` |
| `height` | `public int height;` | `Entity.cs:24` |
| `lavaWet` | `public bool lavaWet;` | `Entity.cs:34` |
| `wet` | `public bool wet;` | `Entity.cs:26` |
| `Center` | 属性 `{ get { return new Vector2(position.X + width/2f, position.Y + height/2f); } set { position = new Vector2(value.X - width/2f, value.Y - height/2f); } }` | `Entity.cs:50-60` |

**未找到**：`Player.grapple`、`ControlGrapple`。钩爪输入实际是 `PlayerInput.Triggers.JustPressed.Grapple`（`Player.cs:33799`）；状态用 `int[] grappling`（`Player.cs:2739`）与 `QuickGrapple()`（`Player.cs:6307`）。

### D20. 受伤 / 死亡 API

**`Player.Hurt`——只有一个重载**（`Player.cs:38497`）：

```csharp
public double Hurt(PlayerDeathReason damageSource, int Damage, int hitDirection, bool pvp = false, bool quiet = false, bool Crit = false, int cooldownCounter = -1, bool dodgeable = true)
```

- 返回 **`double`**：实际造成的伤害值，末尾 `Player.cs:38881` `return num2;`。
- 提前 `return 0.0` 的分支：`Player.cs:38501`（shimmer 闪避）、`:38505`（`creativeGodMode`）、`:38512`（免疫未过）、`:38519/:38524/:38529/:38534`（各类闪避饰品）。
- 最小伤害钳制：`Player.cs:38681-38684` `if (num2 < 1.0) { num2 = 1.0; }`。
- `cooldownCounter` 默认 `-1`；`ImmunityCooldownID.General = -1`（`Terraria.ID\ImmunityCooldownID.cs:48`）。三元短路使默认路径不会索引 `hurtCooldowns[-1]`（`Player.cs:38508`）。

**`Player.KillMe`——只有一个重载，且没有 `quiet` 参数**（`Player.cs:39122`）：

```csharp
public void KillMe(PlayerDeathReason damageSource, double dmg, int hitDirection, bool pvp = false)
```

- 入口守卫：`Player.cs:39124-39127` `if (creativeGodMode || dead) { return; }`。
- **未找到**带 `bool quiet` 的 `KillMe` 重载。
- 相关：`KillMeForGood()`（`Player.cs:39104`）、`KillMe_DustExplosion(...)`（`Player.cs:39346`）。

**`statLife` 的扣减与死亡**：

```csharp
// Player.cs:38687
statLife -= (int)num2;
...
// Player.cs:38842 / 38869-38876
if (statLife > 0)
{
    ...
}
else
{
    statLife = 0;                                        // Player.cs:38871
    if (whoAmI == Main.myPlayer)
    {
        KillMe(damageSource, num2, hitDirection, pvp);   // Player.cs:38874
    }
}
```

⇒ **`KillMe` 只在本地玩家（`whoAmI == Main.myPlayer`）时被调用**；远程玩家走网络消息。`dead = true` 在 `Player.cs:39226`（`KillMe` 内）。另有独立扣血路径 `HurtLifeRegen(int dmg)`（`Player.cs:19523-19526`）。

**检测「我受伤了」的推荐做法**：
- 每 tick 比较 `player.statLife` 的下降（最简单可靠）。
- 或读 `player.immune` / `player.immuneTime` 的跃变（受伤帧会 `immune = true; immuneTime = 40/80/…`，`Player.cs:38692-38693`）。
- 或 `player.dead` 跃变为 true。

### D21. 物品使用

**`Player.ItemCheck` 定义**：`Player.cs:42937` → `public void ItemCheck()`。
调用链：`Player.Update(int i)`（`Player.cs:24558`）→ `Player.cs:28509` `ItemCheckWrapped(i);`（`Player.cs:32051`）→ `Player.cs:32080` → `ItemCheck();`。

**「真正开始使用」的守卫**（`Player.cs:43080`）：

```csharp
if (controlUseItem && releaseUseItem && itemAnimation == 0 && item.useStyle != 0 && !selectedItemState.HasBufferedChange)
```

随后（`Player.cs:43090-43132`）：

```csharp
bool flag2 = ItemCheck_TryStartUse(item);                  // 43090；定义 Player.cs:52680
...
if (whoAmI == Main.myPlayer)                               // 本地玩家
{
    if (flag2 != lastItemUseAttemptSuccess)
    {
        lastItemUseAttemptSuccess = flag2;                 // 43095
    }
    flag2 &= lastItemUseAttemptSuccess;                    // 43101
}
...
if (!flag4 && flag2)
{
    ItemCheck_StartActualUse(item);                        // 43131
}
```

发射弹幕（`Player.cs:43917-43920`）：

```csharp
if (sItem.shoot > 0 && flag4)
{
    ItemCheck_Shoot(whoAmI, sItem, weaponDamage);
}
```

**计时字段语义（每 tick）**：

| 字段 | 含义 | 写入点 | 行号 |
|---|---|---|---|
| `itemAnimation` | 剩余动画帧；每 tick `itemAnimation--;` | `Player.cs:43161` | `3013` |
| `itemAnimationMax` | 本次动画总帧；由 `SetItemAnimation(frames)` 同时写两者 | `Player.cs:4450-4451` | `3015` |
| `itemTime` | 剩余使用冷却；每 tick `itemTime--`；由 `SetItemTime(...)` 写入 | `Player.cs:43181-43183`、`4413-4414` | `3017` |
| `itemTimeMax` | 本次冷却总帧 | `Player.cs:4414` | `3019` |
| `reuseDelay` | 多段攻击延迟 | `Player.cs:43010-43013`、`43166-43169` | `1443` |
| `HeldItem` | 属性：`inventory[selectedItem]` | — | `3853` |

**「本 tick 真的用出去了」最可靠的字段**：

1. **`Player.lastItemUseAttemptSuccess`**（`Player.cs:1811`）—— 唯一被网络同步的「使用尝试成功」标志，写于 `Player.cs:43095`（值来自 `ItemCheck_TryStartUse` 的返回），同步见 `Player.cs:43096` 的 `NetMessage.SendData(13, ...)`。
2. **`Player.ItemAnimationJustStarted`**（`Player.cs:4066`）—— 注意定义是 `itemAnimation == itemAnimationMax - 1`（**不是** `== itemAnimationMax`），因为同一 tick 稍后 `Player.cs:43161` 已执行 `itemAnimation--`。
3. **`Player.ownedProjectileCounts[]`**（`Player.cs:2991`，`public int[] ownedProjectileCounts = new int[ProjectileID.Count];`）—— 检测「我扔出了弹幕」，每 tick 填充于 `Player.cs:12296`，清零于 `Player.cs:12325-12327`。

⇒ **规划器推荐**：投掷雷管后，检查 `player.ownedProjectileCounts[29]` 是否增加（或直接扫描 `Main.projectile[i].active && type == 29 && owner == whoAmI`）。

**背包与快捷栏**：

- `public Item[] inventory = new Item[59];`（`Player.cs:1575`）：0..49 主背包，50..53 钱币，54..57 弹药，58 鼠标物品。
- `Item.type`（`Terraria\Item.cs:141` `public int type;`）、`Item.stack`（`Item.cs:157` `public int stack;`）、`Item.IsAir`（`Item.cs:386-396` 属性：`type > 0 ? stack <= 0 : true`）。
- **`Player.selectedItem` 是只读属性**（`Player.cs:3851`），值 0..9 为快捷栏。切换用 `selectedItemState.Select(slot)`（`Player.cs:485-517`，`:487-490 if (item < 10) { hotbar = item; }`）。
- 钳制逻辑：`SelectedItemState.Update()` 的 `Player.cs:535-538`（`if (selected >= 10 && player.HeldItem.IsAir) { selected = hotbar; }`）与 `ClampHotbarOffset(int Offset)`（`Player.cs:32148-32159`：`while (Offset > 9) Offset -= 10; while (Offset < 0) Offset += 10;`），调用点 `Player.cs:32047`。
- **未找到** `if (selectedItem > 9) selectedItem = 0` 这样的字面代码（因为 `selectedItem` 是只读属性，无法直接赋值）。

### D22. 挖矿

**签名**：

| 方法 | 精确签名 | 行号 |
|---|---|---|
| `PickTile` | `public void PickTile(int x, int y, int pickPower, int dealDamageAsIfBaseNumberIs = -1)` | `Player.cs:54423` |
| `PickWall` | `public void PickWall(int x, int y, int damage)` | `Player.cs:46348` |
| `PickTile_DetermineDamage` | `public void PickTile_DetermineDamage(int x, int y, int pickPower, Tile tileTarget, bool respectTransformingTiles, out int bufferIndex, out int damage)` | `Player.cs:54495` |
| `hitTile` | `public HitTile hitTile;` | `Player.cs:1657` |
| `CanPlayerSmashWall` | `public static bool CanPlayerSmashWall(int X, int Y)` | `Player.cs:46323` |

破坏判定是**累计伤害 ≥ 100**（`Player.cs:54436`）：

```csharp
if (hitTile.AddDamage(bufferIndex, damage) >= 100)
```

**`tileTargetX` / `tileTargetY` —— 是 `static` 字段**：

| 成员 | 精确声明 | 行号 |
|---|---|---|
| `tileTargetX` | `public static int tileTargetX;` | `Player.cs:2503` |
| `tileTargetY` | `public static int tileTargetY;` | `Player.cs:2505` |
| `tileRangeX` | `public static int tileRangeX = DefaultTileRangeX;` | `Player.cs:2495` |
| `tileRangeY` | `public static int tileRangeY = DefaultTileRangeY;` | `Player.cs:2497` |
| `DefaultTileRangeX` | `public static readonly int DefaultTileRangeX = 5;` | `Player.cs:2491` |
| `DefaultTileRangeY` | `public static readonly int DefaultTileRangeY = 3;` | `Player.cs:2493` |
| `lastTileRangeX` / `lastTileRangeY` | `public int lastTileRangeX;` / `public int lastTileRangeY;` | `Player.cs:2499` / `2501` |

**从鼠标换算（`Player.Update` 内，`Player.cs:25600-25620`）**：

```csharp
tileTargetX = (int)(((float)Main.mouseX + Main.screenPosition.X) / 16f);
tileTargetY = (int)(((float)Main.mouseY + Main.screenPosition.Y) / 16f);
if (gravDir == -1f)
{
    tileTargetY = (int)((Main.screenPosition.Y + (float)Main.screenHeight - (float)Main.mouseY) / 16f);
}
if (tileTargetX >= Main.maxTilesX - 5) { tileTargetX = Main.maxTilesX - 5; }
if (tileTargetY >= Main.maxTilesY - 5) { tileTargetY = Main.maxTilesY - 5; }
if (tileTargetX < 5) { tileTargetX = 5; }
if (tileTargetY < 5) { tileTargetY = 5; }
```

同一公式另有 3 处：`dropItemCheck()`（`Player.cs:5142-5147`）、`GetTargetTileWithReverseGravity(...)`（`Player.cs:52983-52991`）、`SmartSelectLookup_GetTargetTile(...)`（`Player.cs:17592-17600`）。智能光标强制覆盖在 `ForceSmartSelectCursor(bool state)`（`Player.cs:32119`，写于 `32132-32134`）。

**未找到** `Player.GetTileTarget`（全树 0 命中）。等价物就是上面两处私有方法。

`Main.mouseX` / `Main.mouseY`：`Terraria\Main.cs:599` `public static int mouseX;`、`Main.cs:601` `public static int mouseY;`；`Main.screenPosition`（`Main.cs:1725`）、`Main.screenHeight`（`Main.cs:1729`，默认 864）、`Main.myPlayer`（`Main.cs:1807`）。

**`itemAnimation` 如何 gate 挖矿**（`Player.ItemCheck_UseMiningTools`，`Player.cs:45987`）：

```csharp
// Player.cs:46029
if (toolTime == 0 && itemAnimation > 0 && controlUseItem)
{
    if (specialToolUsageSettings.UsageAction != null)
    {
        specialToolUsageSettings.UsageAction(this, sItem, tileTargetX, tileTargetY);
        return;
    }
    ItemCheck_UseMiningTools_ActuallyUseMiningTool(sItem, out canHitWalls, tileTargetX, tileTargetY);   // 46036
}
// Player.cs:46042
if (toolTime == 0 && itemAnimation > 0 && controlUseItem && canHitWalls)
{
    ItemCheck_UseMiningTools_TryFindingWallToHammer(out var wX, out var wY);
    ItemCheck_UseMiningTools_TryHittingWall(sItem, wX, wY);
}
```

最终 `PickTile` 调用点 `Player.cs:46179` → `PickTile(x, y, sItem.pick);`（入口 `ItemCheck_UseMiningTools_ActuallyUseMiningTool`，`Player.cs:46049`）。镐分支特殊设置冷却：`Player.cs:46183` → `itemTime = (int)((float)sItem.useTime * pickSpeed);`。`PickWall` 调用点 `Player.cs:46318`（门禁 `Player.cs:46315`）。

**范围校验**：

- 每 tick 在 `ResetEffects()` 里复位：`Player.cs:18945-18956`（`tileRangeX = DefaultTileRangeX;` … 旅行模式 `FarPlacementRangePower` 时 `tileRangeX *= 2; tileRangeX += 8;`）。其他修正：`Player.cs:13156-13157`、`15084-15085`。
- `TileReachCheckSettings`：`Terraria.DataStructures\TileReachCheckSettings.cs:6`（struct）；`Simple` 定义 `:16-20`（`TileRangeMultiplier = 1, TileReachLimit = 20`）；`GetRanges(out x, out y)` `:28-51`；`GetTileRegion(...)` `:53-62`。
- 玩家侧判定：`Player.IsInTileInteractionRange(int targetX, int targetY, TileReachCheckSettings settings, int TB = 0)`（`Player.cs:32278-32282`）。
- 挖矿路径实际用 `TileReachCheckSettings.Simple` + `sItem.tileBoost`：`Player.cs:46002`、`47391`、`53380` 等。

**墓碑（tile 85）挖矿惩罚**（`Player.cs:54577` 的三元链）：`tileTarget.type == 85` 时 `num = (!Main.getGoodWorld) ? (num + pickPower) : (num + pickPower / 4)`。

---

## 附录：本次「未找到 / 不确定」清单

**未找到**（全树 grep 0 命中或不适用）：
1. `WorldGen.SpreadInfection`、名为 `Spread Corruption` 的独立扩散方法。
2. 单一的 `TileID.Sets.Spread` 集合（实际是 `SpreadsCorruption`/`SpreadsCrimson`/`SpreadsHallow`）。
3. `Player.grapple`、`ControlGrapple`。
4. `Player.GetTileTarget`。
5. `KillMe(..., bool quiet)` 重载。
6. `if (selectedItem > 9) selectedItem = 0` 字面代码。
7. `Hurt` 的第二个重载。
8. `Terraria\Liquid.cs` 中的 `Liquid.water / Liquid.lava / Liquid.honey / Liquid.shimmer / Liquid.amount / Liquid.type`（`Liquid` 在本版是 struct，只有 `x / y / kill / delay` 四个实例字段，`Liquid.cs:45-51`；类型常量改在 `Terraria.ID\LiquidID.cs:5-13`）。
9. `Player.oxygenInWater` 类字段（`Player.cs` 内 `oxygen` 零匹配）。
10. `Wiring.cs` 中 `case 654`（TNT 桶）或 `TileID.TNTBarrel` 在 `Terraria\` 下的任何引用（只有 `WorldGen.cs:8836` 的生成函数与 ID 定义）。
10. ~~`ProjectileID` 中数值 `187` 的常量名~~ → **已确认**：`ProjectileID.FlamethrowerTrap = 187`（`ProjectileID.cs:826`），不再是未找到项。
11. 「陷阱瓦片已被触发」的持久布尔字段。
13. 岩浆 / 溺水 / 坠落的 expert-master 伤害倍率（**确认不存在**）。
14. `Player.lavaCD` 的任何引用（已声明 `Player.cs:1523` 但**全树零引用，死字段**）。
15. ~~`ProjectileID` 数值 187 的常量名~~ → 已在核对阶段确认为 `ProjectileID.FlamethrowerTrap`（`ProjectileID.cs:826`）。

**不确定**：
1. `TileObjectData.Style2x2` 的 `Width` 值（`TileObjectData.cs:2001` 的 `addBaseTile` 未逐行读）；墓碑 **2×2** 是从 `CoordinateHeights = new int[2]` 与 `Style2x2` 命名推断。
2. Hallow 转换分支中 `case 3` 的确切对应（`TileID.Sets.Conversion.Grass{2,23,199,109,477,492}` 与 `Hallow` 集合 `{109,492,117,116,164,402,403,115}`，`TileID.cs:18/341`），以及 `WorldGen.cs:55711` Situation 的自然语言归属。
3. `Spawner.SpawnNPC`（`NPC.cs:144` 起）内「城镇 NPC / 向日葵 / 和平蜡烛」对 `spawnRate`/`maxSpawns` 的精确系数。
4. 墙体感染 `WallID.Sets` 的墙值→名称完整映射（只确认了 `Spreads*` 集合与 `wall` 范围 63..68 的判定）。
5. 感染「草地-草地」路径实际每秒触发次数（取决于 `WorldGen.cs:72089-72092` 的采样率与 `GetWorldUpdateRate()`，`WorldGen.cs:72605` 返回 `min(Main.desiredWorldTilesUpdateRate, 24)`，FreezeTime 为 0）。
6. `Projectile.CutTiles()` 对「可切瓦片」的白名单（依赖 `Player.GetTileCutIgnorance(...)`，`Projectile.cs:15290`）。
7. `Player.cs:25590` 中 `num6` 的语义判定为「高度重力系数」（依据 `Player.cs:24878-24898` 的 `gravity *= num6`）：**高置信推断，非官方命名**，但数值/行为由代码确定。
8. `lavaTime` 的中文命名「岩浆安全缓冲计时器」为推断描述，数值行为由 `Player.cs:27900-27947` 完全确定。
9. `187` 是「喷火陷阱」的归属推断（依据 `Wiring.cs:1798` 攻速 5 与 `1936` 的负样式分组），名称未确认。
