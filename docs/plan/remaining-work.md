# 收尾计划（进行中）

目标见会话 goal（A\* 寻路 / 保护建筑加固 / 封堵清单 / 真机脚手板 / 收尾发版）。
这份文件是跨轮次的工作记忆：每完成一项就把结论写回这里，不要重复劳动。

## 已完成（v0.1.1-alpha）

- 物块可处理性判定 `src/ZhaDai.Core/World/TileCatalog.cs`：Blast / Dig / Blocked，闸门抄自
  `Player.GetPickaxeDamage`（Player.cs:54570-54648）与 `WorldGen.CanKillTile`（WorldGen.cs:62724-62831）；
  68 项 `tileNoFail`、398 项 `tileFrameImportant` 由源码生成。
- 规划器保护摆位：`ProtectionLevel`（strict/structures/none）+ 躲不开的封带格改挖；
  实测 `草剑挥打`：none 10,347 发 / 0 挖；strict 10,535 发 / 78 挖（65% 镐力）。
- 执行器接管前盘点（雷管/镐力/封堵物块）+ `DigFence` 挖掘阶段；`Item.pick` 进反射成员表。
- 检查项 103，`tools/verify-all.ps1` 8/8，发行 v0.1.1-alpha。

## 待做 1：A\* 寻路（最高优先）

现状：`src/ZhaDai.Automation/BlastExecutor.cs` 的 `WalkTowards` 是朝目标直走、`TryPlanRetreat` 是受限 BFS。

设计：
- 新增 `src/ZhaDai.Automation/TilePathfinder.cs`（netstandard2.0，不依赖 Core）：
  - A\*，8 邻接；对角移动要求两个正交邻居都可通行，避免穿角。
  - 代价：空气 1；固体 = `DigCost`（默认 12）且该物块必须挖得动（用下面的镐力闸门表）；
    岩浆 = 不可通行；水 = 2（可选游泳）；向上要跳 = 1.5；下落 = 1 + 落差惩罚。
  - 节点上限（默认 4000）与窗口半径（默认 64），超限返回「找不到」，调用方降级为旧行为。
  - 世界读取全部走 `IGameBridge`（`IsSolid`/`TileType`/`LiquidKind`），保证可测。
- `ExecutorOptions`：`DigCost`、`PathNodeLimit`、`PathWindowRadius`、`PathRecomputeTicks`。
- 在 `TilePathfinder` 内加最小镐力闸门表（26 挖不动；211→200；226/237→210；111/223→150；
  108/222→110；107/221→100；地牢砖 41/43/44/677/678/679→100；25/203/117/58/77→65；22/204/56→55；
  37→50；其余 0），用到 `IGameBridge.BestPickPower`。
- 接入点：`TickGoToStand`（走向站位）、`TickDigFence`（走向挖掘格）、`TryPlanRetreat`（撤离）。
  跟随方式：保留 `SetMovement(dx,dy,jump)`，每 `PathRecomputeTicks`（默认 30）重算一次，
  沿路径取下一个 waypoint；到不了的 waypoint 就挖。
- 测试（`tests/ZhaDai.Core.Tests`）：假世界里加一堵墙 → 新旧对比「挖穿格数更少」；
  岩浆池绕行；一条只有斜向通道的走廊能通过（旧直线走法会卡住）；找不到路时降级不崩。

## 待做 2：保护建筑加固

- 墙体：`TileGrid` 已有 `wall`（`WallAt`？没有就补）。规划器统计爆破范围内「非自然墙」格数
  （自然墙列表：1-16、22、40、54、62-70、187；其余算玩家墙），摆位时把玩家墙按 `IsPlayerBuilt`
  同级对待，并在 notes 里报出「炸掉的玩家墙格数」。
- 挖掘侧同样受限：`DigReason.Collateral` 的格子如果本身是结构物/玩家建材（不该发生，但要断言），
  改为 `Blocked` 并点名。
- 结构和玩家建材周围加 1 格缓冲（`protectionBuffer`），避免爆炸擦到贴墙的家具。
- 测试：合成世界（已有 `PlanProtectionTests.cs`）加墙与缓冲两条。

## 待做 3：不可感染物块封堵清单

- 规划器：对每个「带草前沿物块下方是空气且竖井出口通到感染侧」的位置产出 `PlugOrder(x,y,item)`，
  材料选不可感染块（默认木材 30 / 物品 9），把 `PlugTiles`、`RequiredPlugBlocks`、`PlugItemId`
  写进 summary 与施工文件（`#PLUG x y item=`）。
- 执行器：`PlaceBlock` 进 `IGameBridge`（运行时用 `Player.PlaceThing`/`ItemCheck` 路径，反射成员表补条目），
  在雷管阶段前放置；`RequiredBlocks`/`RequiredBlockItem` 盘点随之生效。
- 测试：合成世界断根场景（vine-reach=0 时给出封堵清单）。

## 待做 4：真机检验脚手板（用户已同意方案）

用户已确认：可以用后台、必须独立存档目录、不得影响既有存档。
- `tools/live-check.ps1`：
  1. 把指定 `.wld` 复制到 `%TEMP%\zhadai-live\<名字>\Worlds\`，并复制 `Players\`（角色）。
  2. `Start-Process Terraria.exe -ArgumentList "-savedirectory","<副本目录>"`（先确认 1.4.5.8 支持该参数：
     用 Cecil/IL 找 `Main` 里的命令行解析，搜 `-savedirectory` 字符串）。
  3. 写入副本目录外层的 `ZhaDai\run.cfg`（`enabled=0`），装好插件（`zhaodai-patcher install --game=<游戏目录>`，
     它会自动备份；结束时 `restore`）。
  4. 只轮询 `status.txt`/`runtime.log`，记录「接管前盘点」「前 N 发」「死亡次数」「自伤次数」。
  5. 结束：`restore` 插件、删副本目录；原 `Documents\My Games\Terraria` 全程只读。
- 需要用户配合的点：读档与把 `enabled` 改成 1（不注入键鼠），或者用户明确同意我最小化窗口后自动读档。

## 收尾

- `tools/verify-all.ps1` 加一步「寻路对比」；更新 README/CHANGELOG；提交推送；发 v0.1.2-alpha。

## 已完成（本轮）：A* 寻路（2026-09-29）

- 新增 `src/ZhaDai.Automation/TilePathfinder.cs`：8 邻接 A*（二叉堆，netstandard2.0 手写），
  代价=空气 10 / 挖一格 120 / 跳跃 +6 / 水 18 / 蜂蜜 24；岩浆禁行（有岩浆免疫才放行）；
  对角要两个正交邻格都通（防穿角）；下落是合法走法但落点必须是空气（早期版本允许落进实心块，
  结果 A* 会「挖地板」当地道，实测多挖 66 格）；窗口 96 格、节点上限 20000，超限/无路则降级。
- 镐力闸门进 Automation：`TilePathfinder.RequiredPickPower`（祭坛不可挖、神庙砖 210、叶绿 200、
  精金 150、秘银 110、钴矿/地牢砖 100、黑檀石 65…），挖不动的物块是墙不是隧道。
- `WalkTowards` 换成「A* 路线 + 跟随」（走位、走向挖掘格、撤离跟随三处共用），每 30 tick 重算；
  找不到路时退回原来的直线走法，绝不卡死。状态新增 `RoutesPlanned`/`RouteRestarts`/`RouteDigs`/`LastRouteNote`。
- 撤离 BFS：起点不再落在玩家脚下的地板里；挖一格按 8 步计（原来先比步数，会沿地板挖过去）；
  岩浆与挖不动的岩石都禁行。
- 新增 `tests/ZhaDai.Core.Tests/PathfinderTests.cs`：墙有缺口时 0 挖通过、整条通道是岩浆时不硬闯、
  黑曜石皮放行、神庙砖在 100%% 镐力下当墙而 210%% 下当隧道、完全封死时只挖最薄一层、
  执行器端到端「绕过去 + 炸完 + 不死」。
- 执行器计数语义修正：同一发因死亡重丢记 `Retries`，`Fired` 仍按「发数」计，不再被重试灌水。

下一步：待做 2 保护建筑加固（墙 + 缓冲）→ 待做 3 封堵清单 → 待做 4 真机脚手板。

## 已完成（第 2 轮）：保护建筑加固（墙体 + 缓冲 + 统计）

- 读档不再跳过墙体字节：`TileRun` 带 `Wall`，`TileGrid` 用稀疏表只存「非世界生成」的墙
  （大地图两千万格，真有人建的墙也就几万格）。`SetWall`/`HasBuiltWallAt`/`BuiltWallHistogram`。
- 自然墙表从反编译源码**生成**：`WallID` 里名字带 `Unsafe` 的全是 WorldGen 生成的墙，加上
  `WallID.Conversion` 的九个家族与 `CaveWall`(170)/`CaveWall2`(171)，共 128 个 id。
  其余（Wood 4、GrayBrick 5、Glass 21、Planked 27…）按玩家自建算。
- 用真存档核对过（`--walls` 诊断入口）：第一版只取 Conversion 表，误判 **2,031,645 格**为玩家墙
  （15 MudUnsafe 85.8 万、9 粉地牢 18 万、180 花岗岩 15.4 万…全是世界生成的）；改成 Unsafe 表后
  只剩 **48,401 格**（34 砂岩砖金字塔、27 木板、5 灰砖…），量级回到正常。
- 摆位把墙当成结构级保护（有两个值：1 玩家建材、2 结构/墙），并加 `ProtectionBuffer`（默认 1 格，
  0..3）。缓冲**只长在墙与玩家建材周围**：一开始长在所有 frame-important 格上，78 格改挖直接变成
  28,063 格 / 463 分钟——树和洞穴装饰也是 frame-important，那样等于整条带没法炸。
- 统计与报告：JSON 增加 `builtWallTilesInBlast`/`builtWallTilesInWorld`/`builtWallHistogram`，
  文本报告多一行「墙体也一起保护：… 落在爆破范围内的玩家墙 N 格」。CLI 加 `--protect-buffer=<0..3>`。
- 实测 草剑挥打：strict（含 1 格缓冲）**10,535 发 / 78 格改挖**，与加墙保护前完全一致，
  落在爆破范围内的玩家墙 **0 格**；none 对照 10,345 发 / 0 挖。也就是说这张图上墙保护是免费的
  （带子没穿过别人的建筑），但真穿过去时会挡住并改挖。
- 测试新增：严格保护下玩家墙 0 格被炸、不保护时同一堵墙会被炸开、宁愿多挖不炸墙、如实报出世界墙数、
  1 格缓冲不比不留缓冲更省、改挖清单无负数镐击。

下一步：待做 3 封堵清单 → 待做 4 真机脚手板 → 收尾。

## 已完成（第 3 轮）：封堵清单（防藤蔓绕过封带）

- 模型：`InfectionModel.CanPlantBridge` 与 `SpreadWorkspace` 都认识封堵块——被封堵的藤蔓根不再算
  向下通路，所以「用一块木头换掉 13 格竖井」之后复核仍然全部封住（真跑传播图，不是嘴上说）。
- 规划器：｀PlugOrderｘ 清单（藤蔓根正下方那格 + 物品 id），`PlugVines` 默认开、`PlugItemId` 默认 9（木材），
  审议的锚点判定改成用距离图（原来用 fence mask，而下方那行的 mask 在那时还没写）。
- 输出：施工文件 `#PLUG x y item=9` 与头部 `plugs=`/`plug-item=`；JSON `plugTiles`/
  `requiredPlugBlocks`/`plugItemId`/`vineCurtainTilesSaved`；文本报告一行「封堵：N 格（物品 9，需备 M 个…）」。
- 执行器：新状态 `PlugFence`，放在**最后一发雷管响完之后**（放爆破范围里会被下一发炸掉）；那格已是石头就
  跳过；放不下去记 `PlugsSkipped` 并明说这一格藤蔓根可能还在；接管前盘点把封堵方块算进去，不够拒绝接管。
- 运行时：`PlaceBlock` 用 `WorldGen.PlaceTile(forced)` + 扣库存；`Item.createTile` 与
  `WorldGen.PlaceTile` 两条反射成员对真实 exe 核对通过（共 60 项、必需缺失 0），已标成必需项。
- 实测 草剑挥打：雷管 10,535 → **9,452 发**，封带 30,499 → 25,531 格，改挖 78 → 4 格，
  顶掉 38,727 格竖井，复核仍全部封住；封堵需求 2,979 格 / 需备 2,989 个木材。
- 测试：新建 `PlugTests.cs` 13 项（规划器给清单、清单与汇总一致、封堵后复核仍封住、比竖井省、
  备料足够、默认木材、封堵格都在会长植物的物块下方、施工文件往返、执行器真的放下两块、没方块时拒绝接管）；
  检查项 122 → 141，verify-all.ps1 8/8。

下一步：待做 4 真机脚手板（`tools/live-check.ps1`：复制存档到独立目录、`-savedirectory` 启动、
只读 runtime.log/status.txt），然后收尾发 v0.1.2-alpha。

## 第 4 轮：重复计费 bug 修掉，雷管 9452 -> 2177

- 症状：草剑挥打 9452 发雷管，封带只有 25531 格 => 每发只算 2.7 格，而单发实际能覆盖 40~120 格。
- 根因：`covered` 覆盖标记每处理完一个「感染段」就被清空，而封带掩码是全局共享的、每段的作业区又超出自己
  的前沿很远，于是同一批封带格被相邻段落反复「重新发现」并重复埋药。逐段雷管求和 = 总数，说明重复发生在段间。
- 修法：`PlaceCharges` 结束时不再清 `covered`，整张图每格封带只买一次单。实测 **9452 -> 2177 发**，
  雷管原始命中 141 万格 -> 32.6 万格（重叠 55.2 倍 -> 12.8 倍），复核仍然全部封住。
- 顺带查明「是不是把感染物块全炸了」：不是。封带 25531 格 = 前沿感染物块 6042 + 干净物块 19489；
  全世界感染物块 97250 格，进封带的只有 6%。但感染侧那 6042 格其实可以不清（隔离只需切干净那一侧）：
  试过之后雷管再降到 1361，可惜泛洪复核会报「有段未封住」（藤蔓/竖井那套的 belowWillClear 还假设感染侧被清空），
  所以**没有**当默认，记为下一步。
- 新增可核查的指标（JSON + 文本）：`fenceSeedTiles`/`blastTileHits`/`blastDestroyedTiles` 与
  `chargesCovering*` 直方图，报告里一行「封带构成：… 每格封带被炸 N 次」。
- 检查项 141 通过、verify-all.ps1 8/8。
- 真机：脚本 tools/live-check.ps1 造好（复制存档到独立目录 + -savedirectory + 最小化 + 只读日志 + 自动还原）。
  第一次跑证实注入后的 Terraria.exe 能正常启动到主界面（窗口响应、1.2GB 内存、哈希 275D…），但插件只在
  「已进世界」的钩子（Main.UpdateWorld_Players / Player.Update）里初始化，所以停在主菜单时没有 runtime.log。
  用户决定自己做实测，脚本留给用户/后续用。

## 第 4 轮（续）：按键接管 + 测试副本

- 用户要求：不要自动进世界，改成进世界后**按键触发**接管；死亡后继续。
- 运行时：`run.cfg` 新增 `hotkey=F8`。按键读 `Main.keyState`（Main.cs:973；游戏只在窗口有焦点时填充，
  Main.cs:18607），经 `KeyboardState.GetPressedKeys()` 反射读键名，不引 XNA 编译期引用。边沿检测放在
  Host.Frame（A1 钩子，进世界后每帧）。带 hotkey 时 `enabled=` 只在第一遍生效、按下键后彻底交给按键，
  否则每秒重读配置会把刚开的接管关掉；按键时先 `Poll()` 一次，保证计划已载入。
- 反射成员：`Terraria.Main.keyState` 加进核对表，对真实 exe 核对 61 项、必需缺失 0。
- 死亡续跑已确认：`HandleDeath` 记死亡 -> `AwaitRespawn` -> 复活后回到当前这一发，超过 `maxdeaths` 才停。
- `tools/make-test-copy.ps1`：游戏本体（792MB robocopy）+ 存档（118MB）双副本，插件只装副本；
  生成 `start-test.cmd`（cd 到副本目录再启动）与 `steam_appid.txt`（否则副本一闪而过，实测过）、
  `plan.zplan`（整图 2177 发）与 `plan-quick.zplan`（40 发小样本）、`run.cfg`（hotkey=F8, maxdeaths=50）、
  `README-测试.md`。写入前有 Assert，绝不写用户既有存档。
- 实测：副本 exe 能正常启动到主界面（1184MB、窗口响应、日志 `Setting breakpad minidump AppID = 105600`）；
  插件只在进世界后初始化，所以主菜单没有 runtime.log —— 这也是没有自动进世界时的预期行为。
- 注意：用户当前自己那个实例跑的是**原版 exe**（未注入，我已还原），带 `-savedirectory D:\zhadai-test\save`；
  要测按键接管必须换成启动副本的 `start-test.cmd`。

下一步（还没做）：摆药贪心从「局部窗口」换成「整段最优」以继续压雷管数；感染侧留活口要先理顺藤蔓
`belowWillClear` 的假设。

## 第 5 轮：封带从「整球」改成「外圈」，并查清雷管为什么降不下去

**改动**

- 封带 = 距前沿 `clearance-fence-thickness+1 .. clearance` 的可感染物块（默认 4..6，三层环）。
  以前是 ≤6 的整球，把感染体本身也炸了 6,042 格。新增 `--fence-thickness`（默认 3）。
  实测：封带有 25,531 -> 23,958；**封堵方块 2,979 -> 254（−91%）**；进封带的感染物块 6,042 -> 5,688；
  泛洪复核仍「全部封住」。
- 厚度不是拍的：把 `--fence-thickness` 调到 1 或 2，泛洪复核立刻报「有段未封住」——原版一次传播跨 3 格，
  三层是能挡住的最小厚度，实测证据在手。
- `--clearance` 语义明确为「外沿离前沿几格」。实测 4 也封得住：雷管 2,146 -> 1,758（−18%），
  代价是施工期间留给蔓延的余量 6 -> 4 格；5 反而漏（距离场的形状），所以没有改成默认。
- 摆药换成覆盖贪心（对每个未覆盖格盖戳 -> 取覆盖最多的位置），保护判定改成预计算的「到达掩码」
  （一次字节判断），旧版只看分数最高的 8 个候选会把 4 格镐子活变成 1,027 格。

**为什么雷管降不下去（几何结论）**

- 每发雷管圆盘 149 格，封带里只有 11~13 格落在盘里，其余 91% 是带外地形；这是「用雷管切一条细带」的固有代价，
  不是重叠 bug。重叠倍数 13.1 = 149×2146/23958，就是盘面积/带密度，说明没有重复计费。
- 真正能减的是带本身的长度与厚度。厚度已到最小；长度由感染前沿的周长决定（全世界 97,250 格感染、
  前沿摊开约 822 段）。要再降只能做**最小顶点割**（在 3 格跨度图上求最小割，而不是照距离场切一圈），
  估计能再省 30~50%，工程量大，记为下一步。
