# BiomeContainmentAnalyzer 核心结构与「雷管爆破方案」扩展点报告

分析对象：`D:\personal tasks\modding\_research_analyzer`（Terraria Biome Containment Analyzer，v2.0.0，`net8.0`）。
本文所有标识符均来自实际源码，行号对应当前工作树。

---

## 0. 全局命名空间与工程布局（先看这个，后面全部依赖它）

| 位置 | 命名空间 | 可见性 |
| --- | --- | --- |
| `src\BiomeContainmentAnalyzer\Program.cs` | `BiomeContainmentPlanner.VanillaAnalyzer` | `internal static class Program` |
| `src\BiomeContainmentAnalyzer\VanillaWorldReader.cs` | `BiomeContainmentPlanner.VanillaAnalyzer` | `internal static class VanillaWorldReader` |
| `src\BiomeContainmentAnalyzer\VanillaWorldScan.cs` | `BiomeContainmentPlanner.VanillaAnalyzer` | `internal sealed record VanillaWorldMetadata` / `internal sealed record VanillaWorldScan` |
| `src\BiomeContainmentAnalyzer\HtmlMapWriter.cs` | `BiomeContainmentPlanner.VanillaAnalyzer` | `internal static class HtmlMapWriter` |
| `src\BiomeContainmentAnalyzer\Core\*.cs` | `BiomeContainmentPlanner.Core` | 多数 `public`，少数 `internal`（见下） |

⚠️ 注意：**目录名是 `BiomeContainmentAnalyzer`，但根命名空间是 `BiomeContainmentPlanner.VanillaAnalyzer`**（`csproj` 的 `<RootNamespace>`）。写新代码时不要按目录名推命名空间。

`BiomeContainmentAnalyzer.csproj`：

```xml
<OutputType>Exe</OutputType>
<TargetFramework>net8.0</TargetFramework>
<ImplicitUsings>enable</ImplicitUsings>
<Nullable>enable</Nullable>
<AssemblyName>BiomeContainmentAnalyzer</AssemblyName>
<RootNamespace>BiomeContainmentPlanner.VanillaAnalyzer</RootNamespace>
```

---

## 1. 物块数据模型：`VanillaWorldReader` 跑完之后内存里究竟有什么

### 1.1 结论一句话

**整张物块网格没有保留。** 读取是按列流式进行的，每个物块只被翻译成 3 个布尔量（可感染 / 邪恶 / 神圣）+ 1 个植物标记，然后立刻丢弃；唯一被计数并留存的是 `ColumnSummary[]`（按 X 的邪恶/神圣计数与 Y 极值）、`InfectableTerrainRun` 列表和并查集组件。**block id、wall id、液体、坡度、actuated 全部在 `ReadTileRun` 之后即被丢弃，且没有任何地方保存原始数组。**

### 1.2 两个「扫描结果」类型（`VanillaWorldScan.cs`）

```csharp
internal sealed record VanillaWorldMetadata(
    uint FileVersion, string Title, string Seed, int WorldId,
    int Width, int Height, int SpawnX, int SpawnY,
    double WorldSurface, double RockLayer, int DungeonX,
    bool IsCrimson, bool HardMode, bool RemixWorld, bool SkyblockWorld, bool DualDungeonsWorld)
{
    public bool UsesStandardHardmodeV => !RemixWorld && !SkyblockWorld && !DualDungeonsWorld;
}

internal sealed record VanillaWorldScan(
    VanillaWorldMetadata Metadata,
    ColumnSummary[] Columns,
    InfectableTerrainRange InfectableTerrain,
    IReadOnlyList<InfectableTerrainRun> InfectableRuns,
    BiomeOverview Overview,
    SpreadContainmentAnalysis Containment,
    long TileSectionStart,
    long TileSectionEnd);
```

`VanillaWorldScan` 就是 `Read()` 的返回值，也是 `Program` 与 `HtmlMapWriter` 之间传递的唯一载体。

### 1.3 唯一的「逐格」残留：`ColumnSummary`（`Core\ColumnSummary.cs`）

```csharp
internal struct ColumnSummary           // 注意：internal，且在 Core 命名空间
{
    public int EvilCount;               // 该列邪恶物块总数
    public int HallowCount;             // 该列神圣物块总数
    public int EvilMinY;                // 无邪恶时为 worldHeight
    public int EvilMaxY;                // 无邪恶时为 -1
    public int HallowMinY;
    public int HallowMaxY;

    public void Reset(int worldHeight);
    public void AddEvil(int y);
    public void AddEvilRun(int minY, int maxY);
    public void AddHallow(int y);
    public void AddHallowRun(int minY, int maxY);
}
```

`scan.Columns[x]` 只告诉你「第 x 列有多少个邪恶块、Y 范围是多少」，**无法回答 (x,y) 是不是石块、是不是空腔、是不是水**。

### 1.4 原始物块的唯一存活窗口：`ReadColumns` 主循环

`VanillaWorldReader.ReadColumns(...)`（`VanillaWorldReader.cs:320-389`）是唯一能看到每个物块类型的地方：

```csharp
while (y < height)
{
    TileRun tile = ReadTileRun(reader, version, frameImportant);
    int maxY = checked(y + tile.Repetitions);

    if (tile.Active)
    {
        int runLength = tile.Repetitions + 1;
        bool isInfectable = HardmodeConversionTiles.IsConvertible(tile.Type);
        bool isEvil       = EvilTiles.Contains(tile.Type);
        bool isHallow     = HallowTiles.Contains(tile.Type);
        if (isInfectable || isEvil || isHallow)
        {
            infectableTerrain.AddClassifiedRun(
                x, y, maxY,
                isInfectable ? runLength : 0,
                isEvil       ? runLength : 0,
                isHallow     ? runLength : 0,
                HardmodeConversionTiles.SupportsPotentialInfectionPlantGrowth(tile.Type));
        }

        if (isEvil)   { columns[x].AddEvilRun(y, maxY);   overview.AddRun(x, y, maxY, BiomeOverviewFlags.Evil); }
        if (isHallow) { columns[x].AddHallowRun(y, maxY); overview.AddRun(x, y, maxY, BiomeOverviewFlags.Hallow); }
    }

    y = maxY + 1;
}
infectableTerrain.CompleteColumn(x);
```

**要新增「雷管方案」，这里就是唯一的注入点**：无论把原始 tile 存进一个新网格，还是就地计算爆破方案，都必须在 `ReadTileRun` 的消费点完成，因为出了这个 `while` 就再也没有类型信息。

### 1.5 `ReadTileRun` 实际解码了什么、丢弃了什么

```csharp
private readonly record struct TileRun(bool Active, ushort Type, int Repetitions);
```

`ReadTileRun`（`VanillaWorldReader.cs:391-443`）的逐位解码与丢弃清单：

| 位/字段 | 源码位置 | 是否保留 |
| --- | --- | --- |
| `header1 & 0x02` → `active` | `:410` | ✅ 保留为 `TileRun.Active` |
| `header1 & 0x20` → type 宽度（1 或 2 字节） | `:414-416` | ✅ 保留为 `TileRun.Type`（`ushort`） |
| frame（`framed` 时） | `:417-418` `Skip(reader, 4)` | ❌ 丢弃 |
| 物块漆色 `header3 & 0x08` | `:419` `Skip(reader, 1)` | ❌ 丢弃 |
| **wall**（`header1 & 0x04` 低位墙 + `header3 & 0x40` 高位墙） | `:422-426`、`:429` | ❌ **完全丢弃**（wall id 从未被读出） |
| liquid 液量（`header1 & 0x18`） | `:428` `Skip(reader, 1)` | ❌ **丢弃**（液量读了但没用，液体类型位本身也没解析） |
| **slope**（`header2 & 0x04` / `header2 & 0x08`） | — | ❌ 丢弃；`header2` 只被用来判 `header3` 是否存在（`:397-400`），坡位从未读取 |
| **actuated**（`header2 & 0x02`）、actuation 状态（`header3 & 0x04`） | — | ❌ 丢弃 |
| RLE 重复数（`header1 >> 6`） | `:431-436` | ✅ 保留为 `TileRun.Repetitions`（**这是游程长度减一**，代表 1 个物块 + N 次重复） |

`header3 & 0x01`（coating / 隐形）在 `:403-406` 只多读 1 个 header 字节，无 payload。

> 关键含义：现有代码**已经拥有了读取 slope / wall / liquid 所需的位判定知识**（`header2`、`header3` 的位含义在文件读取路径上出现），但**没有把它们记录到任何字段**。如果爆破方案需要「坡度是否可站立」「是否有墙」「是否有水」这类判断，必须扩展 `TileRun` 并在 `ReadTileRun` 里补读这些位——**不会破坏现有的 section 位置校验**，因为这些字节本来就在被 `Skip`。⚠️ 但要注意：任何 **新增的** `reader.ReadByte()` 都必须与原 `Skip` 的长度严格一致，否则 `stream.Position != sections[2]` 的校验（`:89-94`）会立刻抛 `InvalidDataException`。

### 1.6 如何在 (x,y) 查一个物块

**当前代码里没有任何 API 可以做这件事。** `scan.Columns[x]` 只能给邪恶/神圣的计数与 Y 极值。要在 (x,y) 查询 block id / wall id / liquid / slope / actuated / active，必须新增一个网格类型，例如：

```csharp
// 建议新增于 Core 或 VanillaAnalyzer 命名空间
internal sealed class TileGrid
{
    public int Width { get; }
    public int Height { get; }
    // 建议 ushort[] Tiles / ushort[] Walls / byte[] Liquid / byte[] Slope+flags
    public ushort BlockId(int x, int y);
    public ushort WallId(int x, int y);
    public bool   IsActive(int x, int y);
    public int    LiquidAmount(int x, int y);
    public byte   Slope(int x, int y);
    public bool   IsActuated(int x, int y);
}
```

推荐实现方式：在 `ReadColumns` 内用 `ushort[] tiles = new ushort[width*height]` 之类的大数组接住 `tile.Type`（按 run 填充），并把该数组挂到 `VanillaWorldScan` 上（新增一个 record 位置参数或新增属性）。大世界 8400×2400 的 `ushort[]` 约 40 MB，`ushort[] tiles + ushort[] walls + byte[] liquid + byte[] flags` 合计约 100 MB——**可接受，但必须确认这是有意的内存开销**。

### 1.7 唯一保留的「低分辨率地形」：`BiomeOverview`（8×8 格）

`Core\BiomeOverview.cs`：

```csharp
[Flags] public enum BiomeOverviewFlags : byte { None = 0, Evil = 1, Hallow = 2 }

public readonly record struct BiomeOverviewRun(int CellY, int StartCellX, int Length, BiomeOverviewFlags Flags);

public sealed record BiomeOverview(int CellSize, int WidthInCells, int HeightInCells, byte[] Cells)
{
    public IReadOnlyList<BiomeOverviewRun> BuildHorizontalRuns();
}

internal sealed class BiomeOverviewBuilder
{
    public const int DefaultCellSize = 8;
    public BiomeOverviewBuilder(int worldWidth, int worldHeight, int cellSize = DefaultCellSize);
    public void AddRun(int x, int minY, int maxY, BiomeOverviewFlags flags);
    public BiomeOverview Build();
}
```

它是 `byte[widthInCells * heightInCells]`，**只有 1=Evil、2=Hallow 两个位**，没有任何普通地形信息。`AddRun` 只按 `x / cellSize` 累加 cellX（`:84`），所以它**只用于显示**，不能拿来做几何规划。

---

## 2. 世界元数据：尺寸与深度分层

`VanillaWorldMetadata`（见 §1.2）里与尺寸/坐标相关的**精确属性名**：

- `int Width` — 世界宽（物块数，X 上界）
- `int Height` — 世界高（物块数，Y 上界）
- `int SpawnX` / `int SpawnY` — 出生点物块坐标
- `double WorldSurface` — 地表 Y（浮点）
- `double RockLayer` — 岩石层起始 Y（浮点）
- `int DungeonX` — 地牢 X（用于判断 V 臂起点在左还是右：`world.DungeonX < world.Width / 2`）
- `uint FileVersion` — 世界格式版本
- `bool HardMode` / `bool IsCrimson` / `bool RemixWorld` / `bool SkyblockWorld` / `bool DualDungeonsWorld`
- `bool UsesStandardHardmodeV` — 计算属性

解析处（`ReadHeader`）：

```csharp
int height = reader.ReadInt32();
int width  = reader.ReadInt32();     // 注意：文件中先 height 后 width
if (width is < 100 or > 20_000 || height is < 100 or > 5_000)
    throw new InvalidDataException($"Invalid world dimensions: {width}x{height}.");
...
double worldSurface = reader.ReadDouble();
double rockLayer    = reader.ReadDouble();
...
bool isCrimson = reader.ReadBoolean();
...
bool hardMode = reader.ReadBoolean();
```

即 `Width ∈ [100, 20000]`，`Height ∈ [100, 5000]`。

坐标换算工具（`Core\DepthCoordinate.cs`，`public`）：

```csharp
public enum DepthLayer : byte { Space, Surface, Underground, Caverns, Underworld }
public readonly record struct DepthReading(int Feet, DepthLayer Layer);

public static class DepthCoordinate
{
    private const float NormalPlayerHeightPixels = 42f;
    public static DepthReading FromTileY(
        int tileY, int worldWidth, int worldHeight, double worldSurface, double rockLayer);
}
```

X→英尺换算在 `Program`：`internal static int ToFeet(int tileX, int worldWidth) => tileX * 2 - worldWidth;`

---

## 3. 感染模型

### 3.1 邪恶 / 神圣物块 id 集合（`VanillaWorldReader.cs:11-20`）

这两个 `HashSet<ushort>` 是 `private static readonly`，**硬编码在 `VanillaWorldReader` 内，没有对外暴露**：

```csharp
private static readonly HashSet<ushort> EvilTiles = new()
{
    23, 24, 25, 32, 112, 163, 199, 200, 201, 203, 205, 234,
    352, 398, 399, 400, 401, 636, 661, 662
};

private static readonly HashSet<ushort> HallowTiles = new()
{
    109, 110, 113, 115, 116, 117, 164, 402, 403, 492
};
```

### 3.2 可感染地形（"infectable terrain"）与植物生长

`Core\InfectableTerrainTracker.cs:641-656`：

```csharp
internal static class HardmodeConversionTiles
{
    // Exact union of the vanilla source tile types handled by GERunner's Hallow,
    // Corruption, and Crimson conversion branches in Terraria 1.4.4 and 1.4.5.
    public static bool IsConvertible(ushort type)
    {
        return type is 1 or 2 or 23 or 25 or 53 or 60 or 109 or 112 or 116 or 117
            or 123 or 161 or 163 or 164 or 199 or 200 or 203 or 225 or 230 or 234
            or 396 or 397 or 661 or 662;
    }

    public static bool SupportsPotentialInfectionPlantGrowth(ushort type)
    {
        return type is 2 or 23 or 60 or 109 or 199 or 661 or 662;
    }
}
```

因此「可感染地形」= `IsConvertible(type) == true` 的**活跃物块**（一个物块可以同时是「可感染」且「邪恶/神圣」，`AddClassifiedRun` 的三个计数是独立的）。

### 3.3 传播闭包（spread closure）与数据结构

传播闭包由 `InfectableTerrainTracker`（`internal sealed class`，`Core\InfectableTerrainTracker.cs`）**在读取过程中增量维护**，核心是并查集：`private readonly List<Node> nodes`，`Find(int)` / `Union(int,int,bool)`。

对外（`public`）的结果类型：

```csharp
public readonly record struct InfectableTerrainRange(int MinY, int MaxY, int TileCount, int ComponentCount);
public readonly record struct InfectableTerrainRun(int X, int MinY, int MaxY);

public sealed record InfectableTerrainAnalysis(
    InfectableTerrainRange Range,
    IReadOnlyList<InfectableTerrainRun> SubstantialRuns);

[Flags] public enum InfectionKind : byte { None = 0, Evil = 1, Hallow = 2 }

public sealed record SpreadClosureComponent(
    int Sequence,
    InfectionKind Kind,
    int MinX, int MaxX, int MinY, int MaxY,
    int InfectableTileCount,
    int EvilTileCount,
    int HallowTileCount,
    double WorldWidthPercent,
    double InfectableTerrainPercent,
    bool UsesPotentialPlantBridge,
    bool IsWithinLimit);

public sealed record SpreadContainmentAnalysis(
    bool IsContained,
    double MaximumWorldWidthPercent,
    double MaximumInfectableTerrainPercent,
    int TotalInfectableTileCount,
    int RawClosureComponentCount,
    IReadOnlyList<SpreadClosureComponent> Components)
{
    public bool HasMixedComponent => Components.Any(c => c.Kind == (InfectionKind.Evil | InfectionKind.Hallow));
}
```

对外 API：

```csharp
public InfectableTerrainTracker(int worldWidth, int worldHeight, double worldSurface);
public void AddRun(int x, int minY, int maxY, int tileCount);
public void AddClassifiedRun(int x, int minY, int maxY,
    int infectableTileCount, int evilTileCount, int hallowTileCount, bool supportsPotentialPlantGrowth);
public void CompleteColumn(int x);                        // 必须按 X 递增调用
public InfectableTerrainRange FindSubstantialRange();
public InfectableTerrainAnalysis AnalyzeSubstantialTerrain();
public SpreadContainmentAnalysis AnalyzeContainment(
    double maximumWorldWidthPercent = 30d, double maximumInfectableTerrainPercent = 35d);
```

`SpreadClosureComponent` 的边界是**真实并查集组件的包围盒**（含 `NearbyClosureReportingGap = 18` 的显示归组），并不是「隔离后应当保留的区域」——隔离带本身由 `ContainmentAnalysis` / `ContainmentBand` 给出（§4）。

### 3.4 三格传播距离的建模

`InfectableTerrainTracker` 顶部常量：

```csharp
private const int SpreadLinkDistance = 3;                 // 原版普通传播：每轴 3 格
private const int MaximumPlantHorizontalDistance = 4;     // 荆棘/植物水平最大 4 格
private const int NearbyClosureReportingGap = 18;         // 仅用于报告归组
private const int VineDownwardReach = 13;                 // 向下藤蔓
private const int PlantUpwardReach = 4;                   // 向上植物
```

普通连接的实现是「只跟最近 3 列比较」+ 竖直间隙判定：

```csharp
// CompleteColumn(int x)
int availablePreviousColumns = Math.Min(SpreadLinkDistance, x);
for (int distance = 1; distance <= availablePreviousColumns; distance++)
    ConnectColumns(current, recentColumnRuns[(x - distance) % recentColumnRuns.Length]);

// ConnectColumns(List<int> current, List<int> previous)
while (previousStart < previous.Count &&
       segments[previous[previousStart]].MaxY < currentRun.MinY - SpreadLinkDistance)
    previousStart++;
for (int i = previousStart; i < previous.Count; i++)
{
    RunSegment previousRun = segments[previous[i]];
    if (previousRun.MinY > currentRun.MaxY + SpreadLinkDistance) break;
    Union(currentIndex, previousIndex, usedPotentialPlantBridge: false);
}
```

即：两段竖直游程在 X 距离 ≤3 且 Y 间隙 ≤3 时连通。

### 3.5 植物 / 藤蔓桥接的建模

`ConnectPotentialPlants` 与 `ConnectPlantToSegments`：

```csharp
bool thornReach = horizontalDistance <= MaximumPlantHorizontalDistance   // 4
    && target.MaxY >= source.MinY - PlantUpwardReach                     // 4
    && target.MinY <= source.MaxY + 2;

bool vineReach = horizontalDistance <= SpreadLinkDistance                // 3
    && target.MaxY >= source.MinY - SpreadLinkDistance                   // 3
    && target.MinY <= source.MaxY + VineDownwardReach;                    // 13

if (thornReach || vineReach)
    Union(source.NodeIndex, targetIndex, usedPotentialPlantBridge: true);
```

`supportsPotentialPlantGrowth` 来自 `HardmodeConversionTiles.SupportsPotentialInfectionPlantGrowth(tile.Type)`（草/腐化草等 7 种），通过 `recentPlantRuns` / `recentColumnRuns` 两个环形桶做「最近 4 列」的双向匹配。`usesPotentialPlantBridge` 会传播到组件（`Union` 中 `UsesPotentialPlantBridge = usedPotentialPlantBridge || ...`），最终出现在 `SpreadClosureComponent.UsesPotentialPlantBridge`。

> **对爆破方案最重要的结论**：现有模型把「传播可达性」抽象为**竖直游程之间的 3 格间隙判定**，不是逐格 BFS。若爆破方案要证明「炸出的沟槽确实切断传播」，最稳妥的做法是复用同一套 `spreadLink` 判定逻辑（间隙 ≥ `IsolationWidth`），或在新规划器里对沟槽两侧做同样的游程间隙检查。

---

## 4. 肉前 V 带预测：类型、成员与坐标系

### 4.1 承载类型：`ContainmentBand`（`Core\ContainmentBand.cs`，全部 `public`）

```csharp
public enum ContainmentKind : byte
{
    EvilRegion, EvilCurrent, HallowCurrent, HardmodeRisk, HardmodeLeftRisk, HardmodeRightRisk
}

public enum ContainmentShape : byte { Rectangle, VerticalShafts, SteppedShafts }

public readonly record struct ContainmentBand(
    ContainmentKind Kind,
    ContainmentShape Shape,
    int MinX, int MaxX, int MinY, int MaxY,
    int TileCount,
    bool IsPrediction,
    int Sequence);
```

**左/右臂就是两条 `ContainmentBand`**，通过 `Kind` 区分：

- `ContainmentKind.HardmodeLeftRisk` → `ContainmentShape.VerticalShafts`，`IsPrediction: true`，`Sequence: 1`
- `ContainmentKind.HardmodeRightRisk` → 同上

**坐标系：物块 X/Y（tile X/Y），不是深度、不是像素。** `MinX/MaxX/MinY/MaxY` 都是整数物块索引，`MinX < MaxX`、`MinY < MaxY`，两端**闭区间**（HTML 绘制时用 `MaxX - MinX + 1`）。

### 4.2 生成算法（`Core\ContainmentAnalysis.cs`）

```csharp
internal static class ContainmentAnalysis
{
    public const int IsolationWidth = 6;                       // 六格挖空即可阻断普通传播
    public const double PredictionConfidencePercent = 99.9d;
    private const int MergeGapColumns = 18;
    private const int MinimumRegionTiles = 24;
    private const int MaximumCurrentClustersPerKind = 126;
    private const double RandomWalkConfidenceCoefficient = 11.2d;

    public static List<ContainmentBand> Build(
        ColumnSummary[] columns,
        int worldWidth, int worldHeight,
        bool hardMode,
        bool standardVConversion,
        bool dungeonOnLeft,
        InfectableTerrainRange? infectableTerrain = null,
        IReadOnlyList<InfectableTerrainRun>? infectableRuns = null);
}
```

关键中间量（都在 `AddHardmodeArmRiskEnvelopes`，`:105-212`）：

```csharp
double scale = worldWidth / 4200d;
int maximumDiameter = Math.Max(1, (int)(249d * scale));
int maximumRadius   = (int)Math.Ceiling(maximumDiameter * 0.5d);

double leftStartMinimum  = dungeonOnLeft ? 0.200d : 0.300d;
double leftStartMaximum  = dungeonOnLeft ? 0.299d : 0.399d;
double rightStartMinimum = dungeonOnLeft ? 0.601d : 0.701d;
double rightStartMaximum = dungeonOnLeft ? 0.700d : 0.800d;

int terrainMinY = infectableTerrain?.MinY ?? 5;
int terrainMaxY = infectableTerrain?.MaxY ?? worldHeight - 6;
int firstY = Clamp(terrainMinY - IsolationWidth, 5, worldHeight - 6);
int lastY  = Clamp(terrainMaxY + IsolationWidth, firstY, worldHeight - 6);

OffsetEnvelope[] offsets = BuildHorizontalOffsetEnvelopesByY(worldHeight, maximumRadius);
```

私有辅助类型与成员（都 `private`，扩展时需要注意可见性）：

```csharp
private readonly record struct Cluster(int MinX, int MaxX, int MinY, int MaxY, int TileCount);
private readonly record struct OffsetEnvelope(int Minimum, int Maximum);

private static List<Cluster> FindClusters(ColumnSummary[] columns, bool hallow);
private static OffsetEnvelope[] BuildHorizontalOffsetEnvelopesByY(int worldHeight, int maximumRadius);
private static (int Minimum, int Maximum) FindHorizontalOffsetEnvelope(OffsetEnvelope[] offsets, int minY, int maxY);
private static (int MinX, int MaxX)? FindIntersectingTerrainEnvelope(
    IReadOnlyList<InfectableTerrainRun> runs, OffsetEnvelope[] offsets,
    int terrainMinY, int terrainMaxY, int startMinX, int startMaxX, int worldWidth, bool travelsRight);
private static int Clamp(int value, int minimum, int maximum);
```

> **对爆破方案的意义**：`ContainmentAnalysis.Build(...)` 返回的 `List<ContainmentBand>` 就是「要挖掉的隔离带」的**朴素矩形/竖直井**版本。雷管方案要做的事，本质是**用一串沿块状网格的雷管爆破点，逼近同一个边界，但只在真正能挖的地方炸**。这是一个可以直接复用的「目标边界」输入。`ContainmentShape.SteppedShafts` 枚举值目前没有任何代码生成它——看起来正是为「阶梯状斜向沟槽」预留的。

---

## 5. 输出面：`--json` 的精确 schema 与 `HtmlMapWriter` 的数据消费

### 5.1 `--json` schema（`Program.BuildJsonReport`，`Program.cs:113-181`）

匿名对象的属性名即 JSON 键名（`JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }`，因此枚举以**字符串**输出）：

```jsonc
{
  "File": "C:\\...\\world.wld",                  // Path.GetFullPath(worldPath)
  "World": {                                     // VanillaWorldMetadata 原样序列化
    "FileVersion": 326, "Title": "...", "Seed": "...", "WorldId": 0,
    "Width": 8400, "Height": 2400, "SpawnX": 0, "SpawnY": 0,
    "WorldSurface": 649.0, "RockLayer": 961.0, "DungeonX": 0,
    "IsCrimson": false, "HardMode": false,
    "RemixWorld": false, "SkyblockWorld": false, "DualDungeonsWorld": false,
    "UsesStandardHardmodeV": true
  },
  "IsolationWidthTiles": 6,                      // ContainmentAnalysis.IsolationWidth
  "ProtectedInfectableTerrain": {                 // InfectableTerrainRange
    "MinY": 408, "MaxY": 2219, "TileCount": 5197237, "ComponentCount": 71
  },
  "SubstantialInfectableRunCount": 12345,         // scan.InfectableRuns.Count
  "TileSectionValidated": true,                   // scan.TileSectionEnd > scan.TileSectionStart
  "Containment": {
    "IsContained": false,
    "HasMixedComponent": false,
    "MaximumWorldWidthPercent": 30.0,
    "MaximumInfectableTerrainPercent": 35.0,
    "TotalInfectableTileCount": 5197237,
    "RawClosureComponentCount": 42,
    "Components": [
      {
        "Sequence": 1,
        "Kind": "Evil",                          // JsonStringEnumConverter → "Evil" / "Hallow" / "Evil, Hallow"
        "MinX": 100, "MaxX": 200,
        "MinFeet": -8200, "MaxFeet": -8000,      // ToFeet(x, world.Width) = x*2 - width
        "MinY": 50, "MaxY": 90,
        "MinDepth": { "Feet": 284, "Layer": "Surface" },   // DepthCoordinate.FromTileY
        "MaxDepth": { "Feet": 682, "Layer": "Caverns" },
        "InfectableTileCount": 0, "EvilTileCount": 0, "HallowTileCount": 0,
        "WorldWidthPercent": 0, "InfectableTerrainPercent": 0,
        "UsesPotentialPlantBridge": false,
        "IsWithinLimit": true
      }
    ]
  },
  "Overview": {
    "CellSize": 8, "WidthInCells": 1050, "HeightInCells": 300,
    "Encoding": "base64 row-major bit flags: 1=evil, 2=Hallow",
    "CellsBase64": "AAAA..."
  },
  "Bands": [
    {
      "MinDepth": { "Feet": 284, "Layer": "Surface" },
      "MaxDepth": { "Feet": 682, "Layer": "Caverns" },
      "Kind": "HardmodeLeftRisk",                // ContainmentKind
      "Shape": "VerticalShafts",                 // ContainmentShape
      "Sequence": 1,
      "MinX": 2388, "MaxX": 5202,
      "MinFeet": -3624, "MaxFeet": -2014,
      "MinY": 402, "MaxY": 2225,
      "TileCount": 0,
      "IsPrediction": true
    }
  ]
}
```

> 注意：`Bands[]` 与 `Containment.Components[]` 都是**匿名对象**，新增字段只需在 `BuildJsonReport` 的匿名对象里加一行。`BuildJsonReport` 是 `internal static object`，被 `HtmlMapWriter` 直接复用——**JSON 与 HTML 的 payload 是同一份对象，改一处两处都变**。

### 5.2 `HtmlMapWriter` 消费什么、画什么

```csharp
internal static class HtmlMapWriter
{
    public static void Write(string outputPath, string worldPath, VanillaWorldScan scan, IReadOnlyList<ContainmentBand> bands);
}
```

- 它**只接收 `VanillaWorldScan` + `List<ContainmentBand>`**，没有任何逐格地形。
- 第 28 行把 `Program.BuildJsonReport(...)` 的结果序列化后作为 `const report = {...}` 内联进 `<script>`（`{{payload}}` raw string 插值）。

生成的 HTML 里的 JS 结构（`HtmlMapWriter.cs:84-251`）：

| JS 标识符 | 行 | 作用 |
| --- | --- | --- |
| `const report` | 86 | 整个 JSON payload |
| `const world` / `const overview` | 87-88 | `report.World` / `report.Overview` |
| `cellsBinary` / `cells` | 89-91 | `atob` 解码 `CellsBase64` → `Uint8Array` |
| `const canvas/wrap/hover/ctx` | 93-96 | DOM 与 2D context |
| `const containment` / `const status` | 100-103 | 面板状态文字 |
| `componentBox` 循环 | 105-114 | 右侧闭包卡片列表 |
| `let dpr/scale/offsetX/offsetY/...` | 116-124 | 视图状态：`showBands`、`showClosures` |
| `function resize()` / `function fitWorld()` | 126-138 | 画布尺寸与世界适配 |
| `function worldRect(x, y, width, height)` | 140-142 | **物块坐标 → 设备像素**的唯一转换函数 |
| `function draw()` | 144-190 | 主绘制：先 `Bands`（`showBands`），再 8×8 overview cells，最后 `containment.Components` 虚线框（`showClosures`） |
| `function layerFor(tileY)` | 192-206 | 复刻 `DepthCoordinate.FromTileY` 的层判定（**C# 与 JS 双份逻辑**，改动时必须同步） |
| `function pointerWorld(event)` | 208-212 | 鼠标 → 物块坐标 |
| `mousemove` / `mousedown` / `mouseup` / `wheel` | 214-246 | 命中检测、拖动、滚轮缩放 |
| `fit` / `toggle-bands` / `toggle-closures` 按钮 | 247-249 | 视图开关 |

### 5.3 新 overlay 层（方案折线 + 雷管点）应该插在哪

**精确插入点：**

1. **HTML 面板**：在 `<h2>传播闭包 / Spread closures</h2>`（`:80`）附近新增 `<button id="toggle-blast">爆破方案 / Blast plan</button>` 与图例条目（`:74-79` 的 `.legend`）。
2. **JS 视图状态**：在 `let showBands = true; let showClosures = true;`（`:123-124`）旁加 `let showBlast = true;`。
3. **JS 绘制**：在 `function draw()`（`:144-190`）内、`if (showClosures) { ... }`（`:180-188`）之后、`ctx.restore()`（`:189`）之前插入新调用，例如 `if (showBlast) drawBlastPlan();`，并新增 `function drawBlastPlan()`——用现成的 `worldRect(...)`/`scale`/`offsetX` 做坐标映射，画 `report.BlastPlan` 的折线与雷管点。
4. **事件绑定**：在 `:249` 后加 `document.getElementById("toggle-blast").addEventListener("click", () => { showBlast = !showBlast; draw(); });`
5. **详细清单面板**（可选）：仿 `componentBox` 循环（`:105-114`）新增 `#blast` 容器与循环。
6. **悬停提示**（可选）：在 `mousemove`（`:214-233`）的 `hover.textContent` 拼接里加雷管序号。

> ⚠️ `HtmlMapWriter` 的 HTML 是 **C# raw string literal**（`$$"""` / `{{payload}}`）。JS 里的 `{` `}` 必须写成单花括号（只有 `{{...}}` 会被插值），**新增 JS 代码时不要误用 `{{`**，否则会被当作插值表达式而编译失败。

---

## 6. CLI 结构、参数解析与退出码

### 6.1 入口与退出码

```csharp
private static int Main(string[] args)
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    try
    {
        Options options = Options.Parse(args);
        if (options.ShowHelp) { PrintHelp(); return 0; }
        string worldPath = options.WorldPath ?? FindNewestVanillaWorld();
        if (!options.Watch) { AnalyzeAndPrint(worldPath, options); return 0; }
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; options.Cancelled = true; };
        Watch(worldPath, options);
        return 0;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
    {
        Console.Error.WriteLine("分析失败：" + exception.Message);
        return 1;
    }
}
```

**退出码实际只有 0 和 1**（没有「隔离未通过」的失败码——PASS/FAIL 只体现在文本/JSON 内容里）。⚠️ `catch` 的 when 过滤器**不含 `NotSupportedException`、`ArgumentOutOfRangeException`（它是 `ArgumentException` 的子类，OK）、`OverflowException`**——`ReadColumns` 里用了 `checked(...)`（`:340`），世界若畸形会抛 `OverflowException` 直接崩溃而不是返回 1。新代码若引入新的异常类型，要考虑是否加入过滤器。

另外 `.cmd` 脚本自己用 `exit /b 2` 表示「未传路径」（`scripts\*.cmd`）。

### 6.2 `Options.Parse`（`Program.cs:335-426`）

```csharp
private sealed class Options
{
    public string? WorldPath { get; private set; }
    public bool Watch { get; private set; }
    public bool Json { get; private set; }
    public bool Map { get; private set; }
    public string? MapFile { get; private set; }
    public double MaximumWorldWidthPercent { get; private set; } = 30d;
    public double MaximumInfectableTerrainPercent { get; private set; } = 35d;
    public bool ShowHelp { get; private set; }
    public bool Cancelled { get; set; }
}
```

解析是**手写的单层 `foreach`**，不是子命令体系。已识别的参数（大小写不敏感，用 `argument.ToLowerInvariant()`）：

| 参数 | 效果 |
| --- | --- |
| `--watch` | 轮询最近保存时间，2 秒一次重扫 |
| `--json` | 打印 JSON，**并且会 `return`，跳过文本报告**（但 `--map` 仍先生成） |
| `--map` | 生成 HTML 到默认路径 |
| `--map-file=<path>` | 隐含 `--map`，指定输出路径 |
| `--max-width-percent=<n>` | `(0,100]`，默认 30 |
| `--max-terrain-percent=<n>` | `(0,100]`，默认 35 |
| `--help` / `-h` / `/?` | 打印帮助 |
| 其他 `-` 开头 | `throw new ArgumentException($"未知参数：{argument}")` → exit 1 |
| 非 `-` 开头 | 作为 `WorldPath`；第二个非选项参数 → `throw new ArgumentException("只能指定一个世界文件。")` |

### 6.3 新 flag / 子命令怎么插

- **推荐：新 flag**，例如 `--blast-plan`（bool）或 `--blast-plan-file=<path>`（string，仿 `--map-file=` 走 `TryValue` 分支）。插入点：`Options.Parse` 的 `else if` 链（`:353-381`）与 `Options` 属性、`PrintHelp()`（`:321-333`）。
- **子命令**（`analyze` / `blast`）：当前**没有任何子命令基础设施**，`Parse` 会把第一个非 `-` 参数当世界路径。要加子命令必须新增「第一个非选项 token 若是已知命令名则作为命令」的分支——改动面比 flag 大。
- 生成流程插入点：`AnalyzeAndPrint`（`:77-111`）——`--map` 分支（`:94-102`）与 `--json` 分支（`:104-108`）都从这里分流；若新 flag 需要「即使不 `--map` 也生成文件」，在此新增分支。

---

## 7. 扩展点：新增 `BlastPlan`（从物块算出，并在 JSON 与 HTML 同时输出）

### 7.1 最小改动清单（文件 + 方法，按依赖顺序）

| # | 文件 | 成员 | 要做什么 |
| --- | --- | --- | --- |
| 1 | `src\BiomeContainmentAnalyzer\VanillaWorldReader.cs` | `private readonly record struct TileRun(bool Active, ushort Type, int Repetitions)`（`:514`） | 若需 wall/liquid/slope/actuated：扩展为 `(bool Active, ushort Type, ushort Wall, byte Liquid, byte Slope, bool Actuated, int Repetitions)` |
| 2 | 同上 | `private static TileRun ReadTileRun(BinaryReader, uint, bool[])`（`:391`） | 把当前的 `Skip(reader, 1)` / `Skip(reader, 4)` 换成真正读取并保留；**字节总长必须与原来完全一致**，否则 `sections[2]` 位置校验失败 |
| 3 | 同上 | `private static WorldTileAnalysis ReadColumns(...)`（`:320`） | 新增 `TileGrid`（或规划器）实例，在每个游程处喂入 `x, y, maxY, tile.Type, tile.Active` + 新增字段；**这是唯一能看到 (x,y) 物块的位置** |
| 4 | 同上 | `Read(...)`（`:22`）/ `WorldTileAnalysis`（`:516`）/ `VanillaWorldScan`（`VanillaWorldScan.cs:26`） | 把 tile 网格（或已算好的 `BlastPlan`）通过返回值传出去；建议给 `VanillaWorldScan` 增位参或属性 |
| 5 | **新增** `src\BiomeContainmentAnalyzer\Core\BlastPlan.cs`（或 `BlastPlanner.cs`） | `internal static class BlastPlanner` | 规划器：输入 `TileGrid`（或逐列回调）+ `IReadOnlyList<ContainmentBand>` + `ContainmentAnalysis.IsolationWidth`，输出 `BlastPlan` |
| 6 | **新增** 同上 | `public sealed record BlastCharge(int Sequence, int X, int Y, ...)`、`public sealed record BlastSegment(int X1, int Y1, int X2, int Y2, ...)`、`public sealed record BlastPlan(int ChargeCount, int TrenchTileCount, IReadOnlyList<BlastCharge>, IReadOnlyList<BlastSegment>, ...)` | 结果模型；`public` 便于测试直接构造 |
| 7 | `src\BiomeContainmentAnalyzer\Program.cs` | `internal static object BuildJsonReport(string, VanillaWorldScan, IReadOnlyList<ContainmentBand>)`（`:113`） | 在匿名对象里加 `BlastPlan = ...`（JSON 与 HTML 共用此对象，**一处改动同时生效**） |
| 8 | 同上 | `AnalyzeAndPrint`（`:77`） | 调用规划器，把结果放进 `scan` 或作为额外参数传给 `BuildJsonReport` / `HtmlMapWriter.Write` |
| 9 | 同上 | `PrintTextReport`（`:183`）、`PrintHelp`（`:321`）、`Options.Parse`（`:347`） | 文本报告段落、帮助文本、新 flag |
| 10 | `src\BiomeContainmentAnalyzer\HtmlMapWriter.cs` | `Write(...)`（`:15`）**签名可能需要增参**（若 `BlastPlan` 未挂进 `scan`） | 生成 HTML |
| 11 | 同上 | HTML 模板：面板按钮区（`:70-72`）、图例（`:74-79`）、`let showBands/...`（`:123-124`）、`function draw()`（`:144-190`）、新 `function drawBlastPlan()`、事件绑定（`:249`） | overlay 绘制 |
| 12 | `tests\BiomeContainmentAnalyzer.Tests\BiomeContainmentAnalyzer.Tests.csproj` | `<ItemGroup>`（`:10-19`） | 用 `<Compile Include="..\..\src\...\Core\BlastPlan.cs" Link="Shared\BlastPlan.cs" />` 把新文件显式加入测试工程 |
| 13 | `tests\BiomeContainmentAnalyzer.Tests\Program.cs` | `Main`（`:12`）+ 新 `TestXxx` 方法 | 注册并编写测试 |
| 14 | `README.md` | 中英文「What it shows」/「地图与隔离判定」段落 | 文档更新（可选但当前项目文档很完整，建议同步） |

### 7.2 会「对抗」这个改动的地方（务必提前设计）

1. **物块数据在分析后即被丢弃 —— 这是最大障碍。**
   `ReadColumns` 是流式的，`TileRun` 出了循环就没了。`VanillaWorldScan` 里只有 `ColumnSummary`（§1.3）和 8×8 的 `BiomeOverview`（§1.7），**两者都不足以回答「(x,y) 能不能挖」**。要么在读取时把 tile 网格留下来（大世界约 100 MB，见 §1.6），要么在读取过程中**就地**调用规划器并把 `BlastPlan` 留在 `VanillaWorldScan` 里（省内存，推荐）。
2. **`InfectableTerrainTracker` 的生命周期与作用域**：它是在 `ReadColumns` 里 `new` 的局部变量，`CompleteColumn(x)` 之后其内部 `nodes`/`segments` **不会被返回**（只有 `Range` 和 `SubstantialRuns` 被取出）。如果规划器想复用并查集连通性，必须新增导出方法，或在 `ReadColumns` 内提前算好。
3. **`ColumnSummary` 与 `ColumnSummary` 之外的内部可见性**：`ColumnSummary` 是 `internal struct`（`Core\ColumnSummary.cs:3`），`EvilTiles` / `HallowTiles` 是 `VanillaWorldReader` 的 `private static readonly`（`:11`、`:17`）。规划器若需要这些集合，要么把它们提到 `Core` 下的 `public static`（例如放进 `HardmodeConversionTiles` 旁边），要么把规划器写在同一个类/程序集内（同程序集没问题，测试工程靠 csproj 的 `<Compile Include>` 链接源码，也能访问 `internal`）。
4. **测试工程用「链接源码」而不是项目引用**（见 §8）：**新增的 `Core\*.cs` 文件不会自动进入测试工程**，必须手工加 `<Compile Include ... Link="Shared\..."/>`，否则测试里 `BlastPlanner` 找不到。
5. **`HtmlMapWriter.cs` 的 raw string literal 花括号规则**（§5.3 的告警）：JS 代码里写 `{{` 会被当成插值，编译失败。
6. **C#/JS 双份深度换算**：`DepthCoordinate.FromTileY`（C#）与 `layerFor`（JS）是两份实现。若雷管方案要在悬停里显示深度，不要引入第三份逻辑。
7. **`Main` 的异常过滤器**（§6.1）不含 `OverflowException`/`NotSupportedException`；新规划器若抛这些类型会变成未捕获崩溃（exit code 非 1，且没有中文错误信息）。
8. **只读承诺**：README / NOTICE 都声明「从不写入 .wld / .twld / 角色 / .map」。爆破方案只能生成**报告与地图**，绝不能写出任何游戏可加载文件。
9. **`ContainmentShape.SteppedShafts` 已被声明但从未被生成**——这是「允许斜向阶梯沟槽」最自然的落点，但也要注意 `Band` 目前是纯矩形（4 个 int），**斜向折线无法用 `ContainmentBand` 表达**，必须用新的多段线类型（例如 `BlastSegment`）。

---

## 8. 测试

### 8.1 怎么跑

测试工程**不是 xunit/nunit，而是一个 `OutputType=Exe` 的控制台程序，自带极简断言**：

```xml
<OutputType>Exe</OutputType>
<TargetFramework>net8.0</TargetFramework>
<ImplicitUsings>enable</ImplicitUsings>
<Nullable>enable</Nullable>
<AssemblyName>BiomeContainmentAnalyzer.Tests</AssemblyName>
```

它**不用 `<ProjectReference>`**，而是用 `<Compile Include="..\..\src\...\*.cs" Link="..."/>` 把被测源码链接进来（`BiomeContainmentAnalyzer.Tests.csproj:10-19`），当前链接了 8 个文件：`ColumnSummary.cs`、`ContainmentBand.cs`、`ContainmentAnalysis.cs`、`DepthCoordinate.cs`、`InfectableTerrainTracker.cs`、`BiomeOverview.cs`、`VanillaWorldScan.cs`、`VanillaWorldReader.cs`。

运行命令（README 与 CI 一致）：

```powershell
dotnet run --project .\tests\BiomeContainmentAnalyzer.Tests\BiomeContainmentAnalyzer.Tests.csproj -c Release
```

退出码：`Main` 返回 `failures == 0 ? 0 : 1`（`:31`）。输出形如 `PASS pre-Hardmode rectangles` / `FAIL ... : message` / 最后 `All tests passed.` 或 `N test(s) failed.`。

### 8.2 断言与框架

```csharp
private static void Run(string name, Action test);      // try/catch，失败累计到 failures 并写 stderr
private static void Equal<T>(T expected, T actual);     // EqualityComparer<T>.Default
private static void True(bool value, string message);
private static ColumnSummary[] EmptyColumns(int width, int height);
private static void AddEvil(ColumnSummary[] columns, int minX, int maxX, int minY, int maxY);
private static void AddHallow(ColumnSummary[] columns, int minX, int maxX, int minY, int maxY);
```

现有测试全部是**纯合成数据**（`EmptyColumns` + `AddEvilRun` + 直接构造 `InfectableTerrainTracker`），没有随机化、没有基准、没有 mock。

### 8.3 是否需要真实 `.wld`

**默认不需要。** 真实世界测试是可选的第 12 个用例：

```csharp
if (args.Length > 0)
{
    Run("Terraria 1.4.5.8 read-only integration", () => TestRealWorld(args[0]));
}
```

即通过 `dotnet run --project <tests.csproj> -c Release -- "C:\path\to\world.wld"` 传入。`TestRealWorld` 会：SHA256 前后比对证明**文件未被修改**、断言 `FileVersion == 326`、断言尺寸/深度层合法、断言 `TileSectionEnd > TileSectionStart`、断言至少有一个邪恶块、断言 overview 数组长度自洽、断言 `RawClosureComponentCount >= Components.Count`，并计时 `ContainmentAnalysis.Build`。**CI（build.yml / release.yml）不传参数，因此真实世界测试在 CI 中不运行。**

### 8.4 如何为新几何/规划组件加测试

1. 在 `BiomeContainmentAnalyzer.Tests.csproj` 的 `<ItemGroup>` 里加一行（**必须**，因为测试工程不引用项目）：
   ```xml
   <Compile Include="..\..\src\BiomeContainmentAnalyzer\Core\BlastPlan.cs" Link="Shared\BlastPlan.cs" />
   <Compile Include="..\..\src\BiomeContainmentAnalyzer\Core\BlastPlanner.cs" Link="Shared\BlastPlanner.cs" />
   ```
2. 在 `tests\...\Program.cs` 的 `Main`（`:12-23`）里加 `Run("blast plan on a synthetic trench", TestBlastPlan...);`
3. 写**确定性的合成几何测试**（与现有风格一致）：
   - 构造一个小网格（例如 64×64）与一个已知的感染包围盒，断言雷管点数、沟槽段端点、以及「沟槽确实把并查集图切断」（可用 `InfectableTerrainTracker` 模拟：把沟槽列标记为不可感染/不可连通，再 `AnalyzeContainment` 断言 `IsContained == true`）。
   - 断言 `IsolationWidth`（6 格）语义：间隙 < 6 仍连通、≥ 6 断开（参照现有 `TestSpreadClosureShaft`，它跳过了 `x is < 91 or > 96` 即 6 列空缺）。
   - 断言「不可挖掘地形被绕开」：构造一段 players 无法处理的物块（例如狱岩石/无法用雷管破坏的 id，具体 id 集合需在实现时定义），断言折线绕开它而不是穿过。
   - 断言 `BlastPlan` 与 `ContainmentBand` 的边界一致性（方案包络应覆盖对应 band 的 X 范围）。
4. 建议再加一个**真实世界可选测试**：在 `TestRealWorld` 末尾对传入的 `.wld` 跑一次规划，断言不抛异常且耗时在阈值内（现有代码已用 `Stopwatch` 打点，风格可直接沿用）。

补充（构建告警，非错误）：`TestRealWorld` 里用了 `SHA256.HashData(...)`（`tests\...\Program.cs:328、330`），该静态方法在 **.NET 5+ 已标注过时（`SYSLIB0021`）**，在 `net8.0` 下构建会产生两条过时告警；本仓库**未开启 `TreatWarningsAsErrors`**，所以不影响构建与 CI。若新代码想避免告警，可改用 `SHA256.Create().ComputeHash(...)` 或 `IncrementalHash`。

---

## 9. 构建

### 9.1 工程目标与命令

两个工程都是 **`net8.0`**，`OutputType=Exe`，`ImplicitUsings`/`Nullable` 均开启，没有 `global.json`（`dotnet --info` 明确显示 `global.json file: Not found`），**没有 NuGet 依赖**（无 `PackageReference`，只需 SDK 内置的 `System.Text.Json`）。

```powershell
# 构建分析器
dotnet build .\src\BiomeContainmentAnalyzer\BiomeContainmentAnalyzer.csproj -c Release

# 构建 + 运行测试（控制台断言，exit 1 表示有失败）
dotnet run --project .\tests\BiomeContainmentAnalyzer.Tests\BiomeContainmentAnalyzer.Tests.csproj -c Release

# 带真实世界集成测试（可选）
dotnet run --project .\tests\BiomeContainmentAnalyzer.Tests\BiomeContainmentAnalyzer.Tests.csproj -c Release -- "C:\path\to\world.wld"
```

CI（`.github\workflows\build.yml`）：`windows-latest` + `ubuntu-latest`，`actions/setup-dotnet@v4` 安装 `dotnet-version: 8.0.x`，跑上述两条命令（不带世界参数）。
发布（`.github\workflows\release.yml`，`v*` tag 触发）：先 `dotnet run ... -c Release`（测试门禁），再

```powershell
dotnet publish src/BiomeContainmentAnalyzer/BiomeContainmentAnalyzer.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false `
  -o artifacts/package
```

然后把 4 个 `scripts\*.cmd` + `README.md` + `NOTICE.md` 打进 zip 并生成 SHA256。

### 9.2 本机 SDK 3.0.101 是否够用 —— **不够，明确不够**

实测本机环境（`dotnet --info`）：

```
.NET SDKs installed:
  3.0.101 [C:\Program Files\dotnet\sdk]

.NET runtimes installed:
  Microsoft.NETCore.App 3.0.1 / 3.1.32 / 6.0.35 / 8.0.3 / 8.0.8 / 8.0.11 / 9.0.3
Host Version: 9.0.3
global.json: Not found
```

即：**只有 3.0.101 一个 SDK，但有 8.0.x 与 9.0.3 的运行时**。实测构建失败（为不污染仓库，输出重定向到了 `%TEMP%`）：

```
$ dotnet build src\BiomeContainmentAnalyzer\BiomeContainmentAnalyzer.csproj -c Release
C:\Program Files\dotnet\sdk\3.0.101\Microsoft.Common.CurrentVersion.targets(1175,5):
  error MSB3644: 找不到 .NETFramework,Version=v8.0 的引用程序集。
  ... 的还原在 44.23 ms 内完成。
    0 个警告
    1 个错误
[exit=1]
```

原因：SDK 3.0.101 的 MSBuild 不认识 `net8.0` 这个 TFM，把它当成 .NET Framework 的 `v8.0`，于是去找 .NET Framework 引用程序集并失败。

**结论：**
- **`dotnet` SDK 3.0.101 不足以构建 `net8.0` 工程**，必须安装 **.NET 8 SDK（8.0.100 或更高，建议 8.0.x 最新）**。
- 仅安装 8.0 **运行时**不够——构建需要 SDK（含 MSBuild 与 targeting pack）。本机已有 `Microsoft.NETCore.App 8.0.3/8.0.8/8.0.11` 运行时，但它们只够**运行**已编译好的 `net8.0` 程序，不能编译。
- 安装路径：<https://dotnet.microsoft.com/download/dotnet/8.0>（Windows x64 Installer）。安装后 `dotnet --list-sdks` 应出现 `8.0.x`；工程没有 `global.json`，新 SDK 会自动被选中。
- **注意**：本机有 SDK 3.0.101 时，一旦装了 8.0 SDK，默认会选**最高版本**的 SDK；如果将来同时装了 9.0/10.0 SDK，为保持确定性建议加 `global.json` 钉住 `8.0.x`（当前仓库没有）。另外 `dotnet build` 会生成 `bin/`/`obj/`，两者都已在 `.gitignore` 中。
- 本次分析**未在仓库内构建**（输出重定向到 `%TEMP%` 并已清理，仓库中无 `bin`/`obj` 残留），符合「只读」要求。

---

## 10. 给「细粒度雷管方案」的一句话路线图

1. 在 `VanillaWorldReader.ReadColumns` 捕获 `tile.Type`（必要时扩展 `ReadTileRun`/`TileRun` 读取 slope/wall/liquid/actuated），构造 `TileGrid`。
2. 就地或读取后调用新 `Core\BlastPlanner.cs`，输入 `TileGrid` + `ContainmentAnalysis.Build(...)` 产出的 `List<ContainmentBand>` + `IsolationWidth = 6`，输出 `BlastPlan`（`BlastCharge[]` + `BlastSegment[]`，支持斜向阶梯），并保证「凡沟槽阻断处，两侧游程的 3 格传播/植物桥接判定不再连通」。
3. 把 `BlastPlan` 挂进 `VanillaWorldScan`（省内存）或在 `AnalyzeAndPrint` 里生成后传入。
4. `Program.BuildJsonReport` 加一个 `BlastPlan` 键（JSON 与 HTML payload 同时生效）；`PrintTextReport` 加中文清单；`Options.Parse` + `PrintHelp` 加 `--blast-plan`。
5. `HtmlMapWriter` 加 `drawBlastPlan()` + `#toggle-blast` 按钮 + `showBlast` 状态。
6. `tests\...\BiomeContainmentAnalyzer.Tests.csproj` 链接新源码文件，在 `Main` 里注册合成几何测试与「沟槽确实断开并查集」的测试。
7. 装 .NET 8 SDK 后再跑 `dotnet build` / `dotnet run --project tests...`。
