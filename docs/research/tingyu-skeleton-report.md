# TingYu（听雨的声音）架构分析报告

> 分析对象：`D:\personal tasks\modding\听雨的声音`，版本 **1.0.2**（四个 `AssemblyInfo.cs` 均为 `1.0.2.0`）
> 目的：为「自动炸隔离带」新项目复用其骨架（IL 注入 + 反射门面 + 文件握手 + 自绘 WinForms + 离线校验）。
> 分析方式：只读。除本文件外未修改任何内容。

---

## 0. 一句话结论

这是一个**四工程、net48、无 .sln、手写 csproj** 的 Windows 桌面工具套件：

```
TingYu.Core      （库，AnyCPU）不引用游戏的纯算法层
TingYu.Plugin    （库，x86）   注入进 Terraria.exe 的接管层，全程反射访问游戏
TingYu.Patcher   （exe，AnyCPU）Mono.Cecil 注入/还原/离线成员核对
TingYu.Manager   （winexe，x86）可选自绘 WinForms 管理器 + 界面自检
```

核心设计信条（代码里反复出现）：
**把游戏本体当作一个「已核验哈希的外部契约」，用 Cecil 一次性写好钩子，用反射一次性绑定成员，之后每帧只做委托式调用；所有失败都必须落盘到日志文件，绝不冒泡进游戏的更新循环。**

---

## 1. 工具链

### 1.1 每工程精确配置

所有 csproj 都是 **旧式（`ToolsVersion="15.0"`，非 SDK-style）**，导入 `$(MSBuildToolsPath)\Microsoft.CSharp.targets`。

| 工程 | OutputType | TargetFrameworkVersion | 输出目录 | PlatformTarget | LangVersion | 引用 |
| --- | --- | --- | --- | --- | --- | --- |
| `TingYu.Core` | `Library` | **v4.8** | `bin\$(Configuration)\net48\` | **AnyCPU** | `latest` | `System`, `System.Core` |
| `TingYu.Plugin` | `Library` | **v4.8** | `bin\$(Configuration)\net48\` | **x86** | `latest` | `System`, `System.Core` + ProjectReference → Core（**`<Private>false</Private>`**） |
| `TingYu.Patcher` | `Exe` | **v4.8** | `bin\$(Configuration)\net48\` | **AnyCPU** | `latest` | `System`, `System.Core`, `Mono.Cecil`（`HintPath ..\..\lib\Mono.Cecil.dll`，`Private=true`） |
| `TingYu.Manager` | `WinExe` | **v4.8** | `bin\$(Configuration)\net48\` | **x86** | **7.3** | `System`, `System.Core`, `System.Drawing`, `System.Windows.Forms`, `Microsoft.Xna.Framework`, `Microsoft.Xna.Framework.Graphics` + ProjectReference → Core、Patcher |

公共属性：`Deterministic=true`、`GenerateAssemblyInfo=false`、`DebugType=portable`、Release 时 `Optimize=true`。
每个工程自带手写 `Properties\AssemblyInfo.cs`（含 `AssemblyVersion`/`AssemblyFileVersion`）。

**没有 `.sln`**，`build.ps1` 逐个执行 MSBuild。

### 1.2 必需的编译器/SDK

* **Visual Studio 2022 MSBuild**（含「.NET 桌面开发」工作负载）+ **.NET Framework 4.8 开发者包（目标包）**。
* `build.ps1` 用 `Find-MSBuild` 按顺序探测：
  1. `$env:ProgramFiles\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe`
  2. `...\2022\Professional\...`
  3. `...\2022\Enterprise\...`
  4. `${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe`
  5. `${env:ProgramFiles(x86)}\...\2019\Community\...`
  6. 最后退回 `Get-Command msbuild`（PATH）
  找不到就 `throw '找不到 MSBuild.exe。请安装 Visual Studio 2022（含 .NET 桌面开发工作负载）。'`
* 本机实测：VS2022 Community 与 VS2019 BuildTools 都在；`net48 Release=533509`。（`dotnet` SDK 3.0.101 存在但**没有使用**——这套构建不走 `dotnet build`。）

### 1.3 外部程序集

| 程序集 | 来源 | 版本/说明 |
| --- | --- | --- |
| **Mono.Cecil** | 仓库内 `lib\Mono.Cecil.dll`（363,008 字节） | **0.11.6.0**（FileVersion/ProductVersion）；只被 Patcher 直接引用；因为 Manager 也引用 Patcher 且 `Private=true`，Cecil.dll 会被拷进 Manager 的 bin，再由 `build.ps1` 拷进载荷 |
| **XNA 4.0** | **GAC_32**：`%WINDIR%\Microsoft.NET\assembly\GAC_32` | 只被 **Manager** 引用，**用简单名引用**（无 HintPath）。`build.ps1` 的 `Assert-XnaPresent` 校验 4 个：`Microsoft.Xna.Framework`、`Microsoft.Xna.Framework.Game`、`Microsoft.Xna.Framework.Graphics`、`Microsoft.Xna.Framework.Xact` |
| 其他 | 无第三方 | Plugin / Core 完全不引用游戏程序集、也不引用 XNA |

> ⚠️ **`lib/` 在 `.gitignore` 里**（见 `.gitignore`：`bin/ obj/ lib/ build/ *.user *.suo .vs/ _recon/ tmp/`）。仓库里**没有** Mono.Cecil.dll 的版本来源说明，新项目必须自行提供该 DLL 并固定 0.11.6。

### 1.4 精确构建命令

```powershell
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -Configuration Release -Package
```

`build.ps1` 的真实动作：

```powershell
$projects = @(
    @{ Path = 'src\TingYu.Core\TingYu.Core.csproj';         Platform = 'AnyCPU' },
    @{ Path = 'src\TingYu.Plugin\TingYu.Plugin.csproj';     Platform = 'x86'    },
    @{ Path = 'src\TingYu.Patcher\TingYu.Patcher.csproj';   Platform = 'AnyCPU' },
    @{ Path = 'src\TingYu.Manager\TingYu.Manager.csproj';   Platform = 'x86'    }
)
foreach ($project in $projects) {
    & $msbuild $project.Path /nologo /v:m `
        /p:Configuration=$Configuration /p:Platform=$($project.Platform)
    if ($LASTEXITCODE -ne 0) { throw "$($project.Path) 构建失败（exit $LASTEXITCODE）" }
}
```

等价的单工程命令：

```
msbuild src\TingYu.Plugin\TingYu.Plugin.csproj /nologo /v:m /p:Configuration=Release /p:Platform=x86
```

产物布局：

```
build\<Config>\payload\        TingYu.Core.dll, TingYu.Plugin.dll, TingYu.Patcher.exe,
                               TingYu.Manager.exe (+ .exe.config), Mono.Cecil.dll,
                               install.ps1, restore.ps1
build\<Config>\release\        （-Package）payload 全部 + README.md/LICENSE/CHANGELOG.md
build\<Config>\TingYu-<Config>.zip
```

**构建不需要游戏**：`build.ps1` 里 `param([string]$TerrariaDir = 'D:\Program Files (x86)\Steam\steamapps\common\Terraria')` **声明了但从未使用**。构建只要求 MSBuild + net48 目标包 + GAC 里的 XNA 4.0。

---

## 2. Terraria 定位与版本锁定

### 2.1 定位（`TerrariaLocator.cs`）

`FindTerrariaExe()` 遍历 `Candidates()`，返回第一个 `File.Exists` 命中的；找不到返回 `null`（界面提示用户手填）。**明确不做全盘搜索**（注释：那要几分钟，还会把机器卡住）。

顺序：

1. 环境变量 **`TINGYU_TERRARIA`**（直接当 exe 完整路径用）
2. **Steam 注册表** `HKCU\Software\Valve\Steam` 的 `SteamPath`，**同时用 `RegistryView.Registry32` 和 `Registry64`** 两个视图读（`RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view)`），值里的 `/` 换成 `\`
   → `<SteamPath>\steamapps\common\Terraria\Terraria.exe`
3. 对每个 Steam 根，解析 `<root>\steamapps\libraryfolders.vdf`：只找以 `"path"` 开头的行，取第 2、3 个双引号之间的值，把 `\\` 还原成 `\`、`/` 换成 `\`
   → `<library>\steamapps\common\Terraria\Terraria.exe`
4. 硬编码兜底（**注意：不含 `_recon`、不含任何环境探测**）：
   ```
   C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe
   C:\Program Files\Steam\steamapps\common\Terraria\Terraria.exe
   D:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe
   D:\SteamLibrary\steamapps\common\Terraria\Terraria.exe
   E:\SteamLibrary\steamapps\common\Terraria\Terraria.exe
   ```
5. 常量 `public const string TerrariaAppId = "105600";` 存在但**定位逻辑没有用到**。

`LibraryFolders` 是 `public static IEnumerable<string>`，可单独测试（浅解析，不引 VDF 解析器）。

### 2.2 版本 + 哈希双重锁定（`InstallationService.cs`）

硬编码常量（**逐字引用**）：

```csharp
public const string SupportedVersion = "1.4.5.8";
public const string SupportedSha256  = "960A03BFF6050CF7BE16DFC1A7B19E10FC2C4F8F835A6A3B135A50DD9E6BA2F3";
public const string DataFolderName   = "TingYu";
private const string ManifestFileName = "tingyu-install.txt";
private const string BackupFileName   = "Terraria.exe.orig";
```

判定逻辑（必须**两个都**成立才允许注入）：

```csharp
var version = FileVersionInfo.GetVersionInfo(terrariaExe).FileVersion;
var hash = Sha256(terrariaExe);
...
var supported = version == SupportedVersion &&
                hash.Equals(SupportedSha256, StringComparison.OrdinalIgnoreCase);
return NewStatus(supported ? InstallState.CleanSupported : InstallState.CleanUnsupported, ...);
```

状态机 `InstallState`：`NotFound / CleanSupported / CleanUnsupported / Installed / InstalledButChanged / Invalid`；
`CanInstall => State == CleanSupported || State == Installed`。

* **已安装判定**：读 `<game>\TingYu\tingyu-install.txt`，若存在且 `Sha256(exe) == manifest.PatchedSha256` → `Installed`；不等 → `InstalledButChanged`（"Steam 或别的工具改动过，请先还原再重装"）。
* `Sha256()` 输出**大写十六进制**（`value.ToString("X2")`），比较用 `OrdinalIgnoreCase`。
* 清单内容（`WriteManifest`，UTF-8 无 BOM）：`originalSha256 / patchedSha256 / gameVersion / pluginVersion / installedUtc`。注释直言：「删掉这个文件等于放弃还原能力」。
* `EnsureGameClosed()`：`Process.GetProcessesByName("Terraria")`，有活的就抛「Terraria 正在运行，请先退出游戏再安装或还原。」
* 本机实测：游戏在 `D:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe`，`FileVersion=1.4.5.8`，`SHA256=960A03BF…A2F3` **与常量完全一致**；当前**未注入**（exe 内不含 `TingYu.Plugin`）；游戏目录下仅残留一个 `TingYu\config.txt`。

**接受的版本只有 1.4.5.8 一个**，且必须哈希匹配。README 原话：「1.4.5.8 的偏移量是反编译核过的，换一个构建就未必」「版本不符就拒绝写入」。

---

## 3. 注入机制（逐步）

### 3.1 四个锚点

`AssemblyPatcher` 的类注释给出全貌：

| 锚点 | 位置 | 作用 |
|---|---|---|
| **AF** | `Main.UpdateWorld_Players` 入口 | 接管帧开始：算本 tick 的光标目标与按键状态 |
| **AB** | 同方法末尾 `ret` 之前 | 接管帧结束（现行版本无需还原任何状态） |
| **MN** | `Player.ItemCheck_PlayInstruments` 入口 | 接管期间由接管方决定发哪个音，可跳过原版算音高的那段 |
| **CU** | `Player.Update` 里 `TriggersSet.CopyInto` 之后 | 原版这里加载入的 `PlayerInput`（含真实鼠标位置），接管方可在此覆盖 |

顺序契约：AF 一定先于 MN，AB 一定后于 MN——「发一个音需要两个 tick」的时序由此成立。

### 3.2 目标方法的选择（按签名，不按偏移）

```csharp
var main   = module.Types.Single(t => t.FullName == "Terraria.Main");
var player = module.Types.Single(t => t.FullName == "Terraria.Player");

var updateWorldPlayers = main.Methods.Single(m => m.Name == "UpdateWorld_Players" && m.Parameters.Count == 0);
var itemCheckPlayInstruments = player.Methods.Single(m => m.Name == "ItemCheck_PlayInstruments" && m.Parameters.Count == 1);
var update = player.Methods.Single(m =>
    m.Name == "Update" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32);
```

CU 的锚点用「同名调用点必须唯一」来定位，否则拒绝注入（不猜）：

```csharp
var copyInput = RequireUniqueCall(update, "Terraria.GameInput.TriggersSet", "CopyInto");
...
if (calls.Count != 1)
    throw new PatchException(method.FullName + " 里 " + declaringType + "." + name +
        " 的调用点应唯一，实际 " + calls.Count + " 处；拒绝猜测注入位置。");
```

### 3.3 核心注入例程（逐字摘录）

**主流程**：

```csharp
public void Patch(string sourceExe, string outputExe, string pluginDll)
{
    var source = Path.GetFullPath(sourceExe);
    var output = Path.GetFullPath(outputExe);
    var plugin = Path.GetFullPath(pluginDll);
    if (source.Equals(output, StringComparison.OrdinalIgnoreCase))
        throw new PatchException("注入输出不能覆盖输入文件，必须写到另一个路径。");
    if (!File.Exists(plugin)) throw new PatchException("缺少注入载荷：" + plugin);

    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(plugin));
    resolver.AddSearchDirectory(Path.GetDirectoryName(source));

    var readerParameters = new ReaderParameters
    {
        InMemory = true, ReadWrite = false,
        AssemblyResolver = resolver, ReadSymbols = false
    };

    using (var pluginModule = ModuleDefinition.ReadModule(plugin, new ReaderParameters { AssemblyResolver = resolver }))
    using (var module = ModuleDefinition.ReadModule(source, readerParameters))
    {
        if (module.AssemblyReferences.Any(r => r.Name == "TingYu.Plugin"))
            throw new PatchException("这个 Terraria.exe 已经注入过听雨的声音，拒绝重复注入。请先还原。");
        if (module.AssemblyReferences.Any(r => r.Name == "Chaite.Plugin"))
            throw new PatchException("检测到拆特的注入，请先还原拆特再安装听雨的声音。");

        var runtime = pluginModule.Types.FirstOrDefault(t => t.FullName == PluginRuntimeType);
        if (runtime == null) throw new PatchException("载荷里找不到 " + PluginRuntimeType + "。");

        var beginFrame     = Import(module, runtime, "BeginFrame");
        var endFrame       = Import(module, runtime, "EndFrame");
        var instrumentTick = Import(module, runtime, "InstrumentTick");
        var captureCursor  = Import(module, runtime, "CaptureCursorInput");
        ...
        InjectAtStart(updateWorldPlayers, new[] { Instruction.Create(OpCodes.Call, beginFrame) });
        InjectBeforeExit(updateWorldPlayers, new[] { Instruction.Create(OpCodes.Call, endFrame) });
        PatchInstrumentEntry(itemCheckPlayInstruments, instrumentTick);
        PatchCursorAfterInputCopy(update, captureCursor);

        WidenShortBranches(updateWorldPlayers);
        WidenShortBranches(itemCheckPlayInstruments);
        WidenShortBranches(update);

        module.Write(output, new WriterParameters { WriteSymbols = false });
    }

    Validate(output);
}
```

**MN 的 IL 形状**（把方法开头改成 `if (!Runtime.InstrumentTick()) { …原版方法体… } return;`）：

```csharp
private static void PatchInstrumentEntry(MethodDefinition method, MethodReference hook)
{
    var instructions = method.Body.Instructions;
    var processor = method.Body.GetILProcessor();

    // 顺序要紧：先把原首指令变成 ret，再在它前面插分支，
    // 这样「原版方法体」的入口就是原来的第一条指令，分支目标天然正确。
    var originalFirst = instructions[0];
    var ret = Instruction.Create(OpCodes.Ret);
    processor.InsertBefore(originalFirst, ret);

    var skip = Instruction.Create(OpCodes.Brfalse, originalFirst);
    processor.InsertBefore(ret, Instruction.Create(OpCodes.Call, hook));
    processor.InsertBefore(ret, skip);

    method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 1);
}
```

生成的实际指令序列：`call Runtime::InstrumentTick` → `brfalse <原版首指令>` → `ret` → `<原版方法体…>`。

**CU 的 IL 形状**（`ldarg.1` 把 `Player.Update(int i)` 的下标喂给钩子）：

```csharp
private static void PatchCursorAfterInputCopy(MethodDefinition update, MethodReference hook)
{
    var copyInput = RequireUniqueCall(update, "Terraria.GameInput.TriggersSet", "CopyInto");
    InjectAfter(update, copyInput, new[]
    {
        Instruction.Create(OpCodes.Ldarg_1),
        Instruction.Create(OpCodes.Call, hook)
    });
    update.Body.MaxStackSize = Math.Max(update.Body.MaxStackSize, 2);
}
```

**短跳转加宽**（Cecil 不会自动做，距离超过 127 字节就写不出合法 IL）：

```csharp
private static void WidenShortBranches(MethodDefinition method)
{
    // Cecil 不会因为在中间插入指令而自动把短跳转换成远跳转，
    // 距离一旦超过 127 字节，短跳转的编码就会溢出。
    foreach (var instruction in method.Body.Instructions)
    {
        switch (instruction.OpCode.Code)
        {
            case Code.Br_S:      instruction.OpCode = OpCodes.Br;      break;
            case Code.Brfalse_S: instruction.OpCode = OpCodes.Brfalse; break;
            case Code.Brtrue_S:  instruction.OpCode = OpCodes.Brtrue;  break;
            case Code.Beq_S:     instruction.OpCode = OpCodes.Beq;     break;
            /* … Bge/Bgt/Ble/Blt 的 _S 与 _Un_S、Bne_Un_S、Leave_S 同样处理 … */
            case Code.Leave_S:   instruction.OpCode = OpCodes.Leave;   break;
        }
    }
}
```

**写完再读回来核一遍**（`Validate`，静态方法，可被 `patch-copy` 单独调用）：断言程序集中出现 `TingYu.Plugin` 引用；`UpdateWorld_Players` 首条是 `call BeginFrame`；最后一条 `ret` 的前一条是 `call EndFrame`；`ItemCheck_PlayInstruments` 开头严格是 `call / brfalse / ret` 且 `head[1].Operand == head[3]`；`CopyInto` 之后紧跟 `ldarg.1` + `call CaptureCursorInput`。任何一项不符即抛 `PatchException`。

**导入方式**：Patcher 用 `module.ImportReference(MethodDefinition)`，目标方法在插件里按 **名字 + `IsStatic && IsPublic`** 唯一定位：

```csharp
private static MethodReference Import(ModuleDefinition module, TypeDefinition runtime, string name)
{
    var method = runtime.Methods.SingleOrDefault(m => m.Name == name && m.IsStatic && m.IsPublic);
    if (method == null) throw new PatchException("载荷缺少 " + runtime.FullName + "." + name + "。");
    return module.ImportReference(method);
}
```

四个名字同时硬编码在 `AssemblyPatcher.HookNames = { "BeginFrame", "EndFrame", "InstrumentTick", "CaptureCursorInput" }`，供核对工具使用。

### 3.4 插件程序集怎么被加载进 Terraria 的 AppDomain

**不是**手动 `Assembly.Load`，而是**让 CLR 自己解析**：注入后的 `Terraria.exe` 多了一条对 `TingYu.Plugin` 的 AssemblyRef，CLR 在启动/预热阶段（Terraria 会 `RuntimeHelpers.PrepareMethod` 预热自己程序集里的每个方法）必然要去解析它。

因此插件 DLL 的落位是硬约束——**必须与 `Terraria.exe` 同级，不能放子目录**：

```csharp
// Runtime.ResolveDataDirectory() 注释
// 插件 DLL 必须与 Terraria.exe 同级：Terraria 启动时会用
// `RuntimeHelpers.PrepareMethod` 预热自己程序集里的每个方法，注入点引用的
// TingYu.Plugin 因此会在 CLR 的程序集探测阶段被解析，而探测只看 exe 同级目录
// 与 GAC（子目录不在其列）。放在子目录里的那次尝试换来了一个
// FileNotFoundException 与 `0xE0434352` 退出码。
```

实际落位（`InstallationService.Install`）：

```csharp
var pluginTarget = Path.Combine(Path.GetDirectoryName(terrariaExe), "TingYu.Plugin.dll");
var coreTarget   = Path.Combine(Path.GetDirectoryName(terrariaExe), "TingYu.Core.dll");
File.Copy(pluginSource, pluginTarget, true);
File.Copy(coreSource,   coreTarget,   true);
```

数据目录才是子目录：`<game>\TingYu\`，放 `config.txt / status.txt / tingyu.log / boot.log / tingyu-install.txt / Terraria.exe.orig / sprites\`。

载荷必须齐全（缺一个都不装）：`RequiredPayload = { "TingYu.Plugin.dll", "TingYu.Core.dll" }`。

### 3.5 原字节的备份与还原

```csharp
var backup = BackupPath(terrariaExe);          // <game>\TingYu\Terraria.exe.orig
File.Copy(terrariaExe, backup, true);

var patched = Path.Combine(Path.GetDirectoryName(terrariaExe), "Terraria.exe.tingyu-new");
new AssemblyPatcher().Patch(terrariaExe, patched, pluginTarget);
File.Replace(patched, terrariaExe, null, true);        // 原子替换

var manifest = new InstallManifest {
    OriginalSha256 = Sha256(backup),
    PatchedSha256  = Sha256(terrariaExe),
    GameVersion    = before.GameVersion,
    PluginVersion  = ReadAssemblyVersion(pluginTarget),
    InstalledUtc   = DateTime.UtcNow.ToString("o")
};
WriteManifest(terrariaExe, manifest);
```

三条硬规矩（注释原文）：

1. **只认已核验的版本**——版本号与整份 exe 的 SHA-256 都对上才允许注入。
2. **装之前先备份原版**，并把原版哈希写进清单；还原时先核哈希再换回，避免把 Steam 更新过的东西当原来的东西盖回去。
3. **装之前确认游戏没在跑**（`EnsureGameClosed`）。

**重装（`Reinstall`）**：不叠加注入。从备份反推干净原版 → `File.Replace` 覆盖回去 → 重拷载荷 → 重新 `Patch` → 重写清单。备份丢失则 `throw new InvalidDataException("原版备份不见了，拒绝在已注入的文件上再注入。请用 Steam 校验文件完整性。")`。

**还原（`Restore`）**：

```csharp
if (!Sha256(backup).Equals(manifest.OriginalSha256, StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("原版备份的哈希与清单不符，拒绝用一个来路不明的备份覆盖游戏。");

var temporary = Path.Combine(Path.GetDirectoryName(terrariaExe), "Terraria.exe.tingyu-restore");
File.Copy(backup, temporary, true);
File.Replace(temporary, terrariaExe, null, true);

DeleteIfExists(<exe 同级>\TingYu.Plugin.dll);
DeleteIfExists(<exe 同级>\TingYu.Core.dll);
DeleteIfExists(ManifestPath);  DeleteIfExists(BackupPath);
// TingYu 目录若为空则删除（IOException 忽略）
```

CHANGELOG 1.0.1 记录了往返验证结论：**还原后 `Terraria.exe` 与原版逐字节一致**。

### 3.6 CLI 入口（Patcher）

`TingYu.Patcher [status|install|restore|verify|patch-copy] [--terraria <exe>] [--payload <目录>]`

* 默认命令 `status`；`--terraria` 缺省走 `TerrariaLocator.FindTerrariaExe()`，`--payload` 缺省 `AppDomain.CurrentDomain.BaseDirectory`。
* `verify` → `MemberVerifier.Run(exe, plugin)`；`patch-copy` → 只写 `--out`，不碰游戏。
* 退出码：`Installed/CleanSupported` → 0，其余 → 1；用法错误 → 2。

---

## 4. 钩子签名契约与反射引导

### 4.1 契约（这是新项目最该照抄的部分）

**注入点引用的全部是 `TingYu.Plugin.Runtime` 上的 public static 方法**，宿主类型名硬编码为 `AssemblyPatcher.PluginRuntimeType = "TingYu.Plugin.Runtime"`：

| 钩子 | 精确签名 | 注入位置 | 栈约定 |
| --- | --- | --- | --- |
| `BeginFrame` | `public static void BeginFrame()` | `Main.UpdateWorld_Players` 第 0 条 | 无参无返回，`call` |
| `EndFrame` | `public static void EndFrame()` | 同方法最后一个 `ret` 之前 | 无参无返回，`call` |
| `InstrumentTick` | `public static bool InstrumentTick()` | `Player.ItemCheck_PlayInstruments` 第 0 条 | 返回 `bool` 被 `brfalse` 消费；**true = 跳过原版** |
| `CaptureCursorInput` | `public static void CaptureCursorInput(int tick)` | `Player.Update` 的 `TriggersSet.CopyInto` 之后 | `ldarg.1` 传 `Player.Update(int i)` 的 `i` |

Runtime 注释明确「**静态状态而不是实例**：注入点没有宿主对象可挂，而且整个进程只可能有一份。」

```csharp
public static class Runtime
{
    /// <item>`BeginFrame()` —— `Main.UpdateWorld_Players` 入口</item>
    /// <item>`EndFrame()` —— 同方法出口前</item>
    /// <item>`InstrumentTick()` —— `Player.ItemCheck_PlayInstruments` 入口</item>
    /// <item>`CaptureCursorInput(int)` —— `Player.Update` 里 `TriggersSet.CopyInto` 之后</item>
```

### 4.2 插件如何「找到那个调用」

**它不需要找**。Patcher 在构建产物里写入 `MethodReference`，CLR 自动绑定；插件只负责提供同名同签名的 public static 方法。插件的 csproj **不含任何 Terraria 或 XNA 引用**——这是刻意的：编译期不依赖游戏，只有运行期反射。

### 4.3 反射引导代码（逐字）

**找到游戏程序集**（不靠 `Assembly.Load`，靠枚举当前 AppDomain）：

```csharp
private static Assembly FindTerrariaAssembly()
{
    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
    {
        try
        {
            if (assembly.GetType("Terraria.Main", false) != null) return assembly;
        }
        catch (Exception) { }
    }
    // 兜底：Terraria.exe 就是宿主进程的程序集。
    try
    {
        var entry = Assembly.GetEntryAssembly();
        if (entry != null && entry.GetType("Terraria.Main", false) != null) return entry;
    }
    catch (Exception) { }
    return null;
}
```

**一次性绑定全部成员**（构造函数里做，之后每 tick 只做委托式调用）：

```csharp
public GameFacade(Assembly game)
{
    if (game == null) throw new ArgumentNullException("game");
    GameAssembly = game;

    foreach (var requirement in ReflectionRequirements.All)
    {
        MemberInfo member;
        string problem;
        if (ReflectionRequirements.TryResolve(game, requirement, out member, out problem))
        {
            _resolved[Key(requirement.Type, requirement.Member)] = member;
        }
        else if (requirement.Required) { _missingRequired.Add(problem); }
        else                          { _missingOptional.Add(problem); }
    }
}
```

解析用的 BindingFlags（**FlattenHierarchy 不可省**）：

```csharp
const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                           BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
```

**单一真源（Single Source of Truth）**：成员清单只写在 `TingYu.Plugin.ReflectionRequirements.Requirements` 一处，运行时的 `GameFacade.Validate()` 与构建期的 `MemberVerifier` 读**同一份**。类注释记录了动机：

> 之前它们是两份各写各的列表，结果互相打架：工具说「全部通过」，插件却在游戏里报「反射自检失败」。

清单（35+ 项，字段/属性/方法三类，`Required` 布尔区分致命与否）覆盖：

* `Terraria.Main`：`player`(F,必), `npc`(F,必), `myPlayer`(F,必), `Camera`(F,必), `gameMenu`(F,必), `musicPitch`(F,必), `screenPosition`, `netMode`, `bloodMoon`, `eclipse`, `snowMoon`, `pumpkinMoon`, `invasionType`, `GameUpdateCount`(P), `screenWidth`, `screenHeight`
* `Terraria.Player`：`active`(必), `dead`(必), `ghost`, `inventory`(必), `selectedItemState`(必), `musicDist`, `Center`(P,必，**定义在 `Terraria.Entity`**), `musicNotes`
* `Terraria.Entity`：`whoAmI`(F)
* `Terraria.Player+SelectedItemState`：`selected`(F,必), `CanChangeSelectedItemImmediately`(P), `Select`(M,必, `System.Int32`)
* `Terraria.Item`：`type`(必), `stack`(必)
* `Terraria.NPC`：`active`(必), `boss`
* `Terraria.GameContent.Events.Sandstorm`：`Happening`
* 方法：`Terraria.Player.PlayGuitarChord(System.Single)`、`PlayDrums(System.Single)`、`Terraria.Audio.SoundEngine.PlaySound(System.Int32, Microsoft.Xna.Framework.Vector2, System.Int32, System.Single)`、`Terraria.NetMessage.SendData(System.Int32, System.Int32, System.Int32, Terraria.Localization.NetworkText, System.Int32, System.Single)`、`Terraria.GameContent.Achievements.AchievementsHelper.NotifyProgressionEvent(System.Int32)`

嵌套类型用反射写法 `Outer+Inner`：

```csharp
/// <summary>找类型。`Outer+Inner` 表示嵌套类型。</summary>
public static Type FindType(Assembly game, string name)
{
    var type = game.GetType(name, false);
    if (type != null) return type;

    var separator = name.IndexOf('+');
    if (separator < 0) return null;
    var outer = game.GetType(name.Substring(0, separator), false);
    if (outer == null) return null;
    return outer.GetNestedType(name.Substring(separator + 1),
        BindingFlags.Public | BindingFlags.NonPublic);
}
```

方法重载匹配规则：参数类型前缀逐一全名相等，**多出来的参数必须全是 `IsOptional`**（否则反射调用会因缺参失败）。

**绕开 XNA 的编译期依赖**：`Vector2Like` 只带两个 float，需要真类型时现场构造：

```csharp
public struct Vector2Like
{
    public float X, Y;
    public static Vector2Like From(object vector) { /* 反射读 X/Y 字段 */ }

    /// <summary>造一个真正的 XNA `Vector2`。调用原版方法时必须给真类型，不能给这个替身。</summary>
    public object ToGameVector()
    {
        var type = Type.GetType("Microsoft.Xna.Framework.Vector2, Microsoft.Xna.Framework", false);
        if (type == null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType("Microsoft.Xna.Framework.Vector2", false);
                if (type != null) break;
            }
        }
        if (type == null) return null;
        return Activator.CreateInstance(type, new object[] { X, Y });
    }
}
```

### 4.4 惰性初始化 + 全量自检（第一次钩子被调用时）

```
步骤 1/6 解析数据目录
步骤 2/6 读配置
步骤 3/6 开日志
步骤 4/6 找 Terraria 程序集
步骤 5/6 绑定反射
步骤 6/6 载入曲库
```

每一步都先写一条 `Bootstrap("…")`（落 `TingYu\boot.log`）。原因写在注释里：

> 这一行在所有初始化逻辑之前落盘。它的唯一职责是「证明钩子真的被调到了」：之前那次失败里一个字节的日志都没有，连插件有没有被加载都无法判断，只能靠反汇编去猜。

失败即 `_initFailed = true`，此后所有钩子直接返回、不再做事（保证游戏不会崩）。

---

## 5. Takeover 模式

### 5.1 结论先行（重要纠正）

**它完全不写输入，不伪造鼠标按键，不重放原版输入。** 具体来说，代码里**没有任何**对 `Main.player[i].controlLeft / controlRight / controlUp / controlDown / controlJump / controlUseItem / controlThrow`、`Main.mouseX/mouseY`、`Main.mouseLeft/mouseLeftRelease`、`PlayerInput`、`TriggersSet` 的写入。

这条路**试过并被彻底放弃**，原因写在 `Takeover` 类注释里：

> 曾经试过「把真实光标推到目标位置，让原版自己算音高」那条路。它有一个致命副作用：为了让原版认定「左键刚按下」，必须把 `Main.mouseLeft` 与 `Main.mouseLeftRelease` 都置为 true，而这两个位在 `Main.Draw` 里被界面消费——结果就是每一帧都在界面上点一次鼠标，玩家会看到一个凭空冒出来的界面。这条路已经彻底放弃。

现行方案：**直接复刻原版的发声代码**（`GameFacade.PlayNote`），音高按原版公式自己算。

### 5.2 每 tick 的时序

```
BeginFrame() : 检查是否该释放 → 问 Performance 要不要发音 → 要就 Strike()
EndFrame()   : 什么都不用还原（不碰光标与按键的直接好处）
```

`Runtime.BeginFrame()` 才是真正的 tick 驱动器：累加 `_tick` → 按 `Main.GameUpdateCount` 变化节流轮询热键 → 每 30 tick 读一次配置 → 处理待办换手 → `_takeover.BeginFrame()` → 每 20 tick 写一次 `status.txt`。整个函数体包在 `try/catch` 里，异常走 `Fault()`。

### 5.3 决定是否接管

```csharp
public string TryStart(TingYuConfig config)
{
    if (Active) return "已经在接管中。";
    if (!config.Enabled) return "接管已在界面上关闭。";
    var player = _game.LocalPlayer;
    if (player == null || !_game.LocalPlayerInWorld) return "当前不在游戏里。";
    // 先把「手上拿的」对齐到背包里的乐器 …
    // 界面点过某件乐器时以界面为准（config.PreferredInstrument，0 表示不覆盖）
    // 事件进行中拒绝；曲目无谱拒绝
    _performance = new Performance(score, instrument, baseMidi, config.JitterTicks, Environment.TickCount ^ score.Notes.Count);
    _performance.Start();
    ...
}
```

* 返回值契约：`null` = 成功；常量 `SwitchPending = "\u0000切换中"` = 正在换手、下 tick 重试；其他 = 拒绝原因（中文，直接给玩家看）。
* `Runtime.ServicePendingStart()` 逐 tick 重试，超过 `PendingStartTimeoutTicks = 60`（约 1 秒）放弃并提示「换乐器超时：手上正在使用别的物品，先松开鼠标再按一次接管键。」

### 5.4 热键

`Hotkey.cs` 用 Win32 `GetAsyncKeyState` **轮询**（不是 XNA `Keyboard.GetState`，后者失焦后读不到任何键），并做**前台判定**（`GetForegroundWindow` + `GetWindowThreadProcessId` 必须等于自己的 pid），避免玩家在别的程序里打字时误触发。

```csharp
public const int DefaultVirtualKey = 0x79; // F10
public bool PollPressed()
{
    var down = (GetAsyncKeyState(_virtualKey) & 0x8000) != 0;
    return Sample(IsGameForeground(), down);   // 纯边沿检测，便于离线测试
}
```

**默认 F10 而非 F8**：Terraria 1.4.5 本体把 F8 绑给了网络统计浮层。`DisplayName` 把 VK 渲染成 `F1..F24` / `A-Z` / `0-9` / `0xNN`。

### 5.5 写入游戏的完整字段清单

| 目标 | 成员 | 类型 | 说明 |
| --- | --- | --- | --- |
| 音高 | `Terraria.Main.musicPitch` | 静态 `float` 字段 | `GameFacade.MusicPitch` 的 setter，`Write(null, Find("Terraria.Main","musicPitch"), value)`；只有原版单音路径会读它 |
| 光标距离显示 | `Terraria.Player.musicDist` | 实例 `float` 字段 | **不影响发声**，只被原版用来画光标附近的音高指示 |
| 手上格子 | `Terraria.Player.selectedItemState` | 实例**结构体**字段 | 反射读副本 → 在副本上调 `SelectedItemState.Select(int)` → **`SetValue` 写回** |

`SelectSlot` 的两处「静默失效」注释：

```csharp
/// 1. `selectedItemState` 是**结构体**字段。反射 `GetValue` 拿到的是副本，
///    在副本上调用 `Select` 只改副本，所以必须 `SetValue` 写回，否则不报错也没效果。
/// 2. `Select` 的语义是**缓冲**：真正生效要等 `Player.Update` 里的
///    `selectedItemState.Update()`，而它又被「当前没在使用物品」挡着。
///    所以换手一定跨 tick，调用方必须重试。
```

并且每 tick 重新钉住槽位（`RefreshSelection`）：

```csharp
/// 为什么要重复做：`Player.Update` 里有若干分支会重算 `selectedItem`,
/// （比如使用物品时的自动切换）。只设置一次会出现
/// 「第一下弹了、后面全哑了」这种极难查的现象。
```

### 5.6 发声路径（复刻原版）

```csharp
public void PlayNote(object player, int itemId, float normalizedDistance, float musicPitch)
{
    var soundId = 0;
    switch (itemId)
    {
        case 508:  soundId = 26; MusicPitch = musicPitch; break;  // 竖琴 → SoundID.Item26
        case 507:  soundId = 35; MusicPitch = musicPitch; break;  // 铃铛 → SoundID.Item35
        case 1305: soundId = 47; MusicPitch = musicPitch; break;  // 吉他斧 → SoundID.Item47
        case 4372: case 4057: case 4715:                          // 常春藤/雨歌/星星吉他
            InvokeChord("PlayGuitarChord", player, normalizedDistance); break;
        case 4673: InvokeChord("PlayDrums", player, normalizedDistance); break;
        default: return;
    }

    if (soundId != 0)
    {
        PlaySound(soundId, PlayerCenter(player));    // SoundEngine.PlaySound(id, Vector2, 1, 0f)
        // 单音乐器同步的是 `musicPitch`，拨弦乐器同步的是归一化距离
        SendPitch(player, soundId == 26 || soundId == 35 ? musicPitch : normalizedDistance);
    }
    // AchievementsHelper.NotifyProgressionEvent(37)
}
```

* `PitchMessageType = 58`（`NetMessage.SendData(58, -1, -1, null, whoAmI, pitch)`）。
* `ProgressionEventInstrument = 37`。
* **音高公式**（与原版 `ItemCheck_PlayInstruments` 一致，含银行家舍入陷阱）：

```csharp
public static float PitchFromWorldDistance(float worldDistance, float smallerScaledAxis, int musicNotes)
{
    if (smallerScaledAxis <= 0f || musicNotes <= 0) return 0f;
    var normalized = worldDistance / (smallerScaledAxis / 2f);
    if (normalized > 1f) normalized = 1f;
    normalized = normalized * 2f - 1f;
    if (normalized < -1f) normalized = -1f;
    if (normalized > 1f) normalized = 1f;
    var steps = (float)Math.Round(normalized * musicNotes);   // 银行家舍入
    return steps / musicNotes;
}
```

* **屏幕短边**（1.4.5.8 的坑）：首选 `Main.Camera.SmallerScaledAxis`（属性，定义在 `Terraria.Graphics.Camera`），若 `<= 1f` 则回退 `min(Main.screenWidth, Main.screenHeight)`。

### 5.7 释放条件（每 tick 在 `BeginFrame` 里查）

`ReleaseReason` 枚举：`None, Finished, HotkeyPressed, PlayerDied, InstrumentLost, PlayerLeftWorld, WorldEvent, InputMoved, Disabled, Fault, DeviceLost`。

检查顺序：界面关闭接管 → 不在世界/回主菜单 → 角色死亡 → 手上不是目标乐器 → 世界事件。世界事件优先级：

```csharp
public string WorldEventReason()
{
    if (BossAlive)    return "Boss 出现";
    if (InvasionType > 0) return "入侵事件";
    if (PumpkinMoon)  return "南瓜月";
    if (SnowMoon)     return "霜月";
    if (Eclipse)      return "日食";
    if (BloodMoon)    return "血月";
    if (Sandstorm)    return "沙尘暴";
    return null;
}
```

`BossAlive` 遍历 `Main.npc` 数组找 `active && boss`。

### 5.8 跨进程握手（Manager ↔ Plugin）

两条**文件**通道，不用任何 IPC：

```
<game>\TingYu\config.txt    管理器写（含 request=play|stop|reload|idle），插件每 30 tick 读一次
<game>\TingYu\status.txt    插件每 20 tick 写一次，管理器每秒读一次
<game>\TingYu\tingyu.log    运行日志（GBK/ANSI）
<game>\TingYu\boot.log      初始化兜底日志（不依赖任何状态）
```

* 插件靠 `File.GetLastWriteTimeUtc(config.txt)` 变化触发重载（热键、曲库目录、曲目、verbose、request）。
* `request` 是**一次性**的：插件处理完把该行改回 `request=idle`（`ClearRequest`）。
* 管理器侧刻意**不复用**插件的 `TingYuConfig` 类：注释说明「让两个进程共享一个程序集会带来版本耦合——插件更新了，管理器就再也读不了旧配置。所以格式共享、类型各写一份」。

---

## 6. UI

### 6.1 关键纠正：**窗口不是无边框的**

`MainForm` 的构造函数**没有设置 `FormBorderStyle`**，也没有 `AutoScaleMode`、没有 DPI 感知设置、没有 `app.manifest`、没有 `SetHighDpiMode`（我已全树 grep，零命中）。所以它是一个**标准的可缩放窗口**（`FormBorderStyle.Sizable` 默认值），亮色主题来自**全自绘内容**，不是来自无边框窗体。

精确的 WinForms 设置：

```csharp
internal MainForm()
{
    Text = "听雨的声音";
    ClientSize = new Size(980, 640);
    MinimumSize = new Size(880, 560);
    StartPosition = FormStartPosition.CenterScreen;
    BackColor = UiTheme.Canvas;        // (250,250,251)
    ForeColor = UiTheme.TextColor;     // (32,36,44)
    Font = UiTheme.Body;               // 9pt
    DoubleBuffered = true;
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
             ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    KeyPreview = true;
    ...
}
```

`Program.cs` 启动序列：

```csharp
[STAThread]
private static int Main(string[] args)
{
    var smokeDirectory = Argument(args, "--ui-smoke");
    if (smokeDirectory != null) return UiSmokeTest.Run(smokeDirectory);
    if (HasFlag(args, "--where")) { Console.WriteLine(TerrariaLocator.FindTerrariaExe() ?? "(未找到)"); return 0; }

    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { ReportCrash(e.Exception); };
    AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) {
        ReportCrash(e.ExceptionObject as Exception);
    };
    Application.Run(new MainForm());
    return 0;
}
```

崩溃写 `%LOCALAPPDATA%\TingYu\manager-error.log` + MessageBox。

**界面上没有放任何 WinForms 控件**——全部在 `OnPaint` 里手绘：

```csharp
protected override void OnPaint(PaintEventArgs e)
{
    var g = e.Graphics;
    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    Render(g);
}
```

`Render(Graphics)` 与 `OnPaint` 刻意分离，唯一目的就是让自检能画到自己的位图上。命中测试是纯几何：`HitTest(Point)` 用 `InstrumentRects() / TrackRects() / ActionButtons() / PlayButtonBounds() / StopButtonBounds()` 重算矩形；`_hover` 驱动悬停；`OnMouseDown` 分发到 `install/restore/sprites/play/stop/transpose±/jitter±/releaseonevent/releaseonmousemove`。

布局是一个「顶栏 / 左栏乐器 / 中栏曲目 / 右栏参数 / 底栏」的固定网格，全部由 `ClientSize` 推导：

```csharp
internal Rectangle TopBar   { get { return new Rectangle(0, 0, ClientSize.Width, 48); } }
internal Rectangle Body     { get { return new Rectangle(UiTheme.Margin, 48,
                                        ClientSize.Width - UiTheme.Margin * 2,
                                        ClientSize.Height - 48 - 36 - UiTheme.Margin); } }
internal Rectangle LeftPanel  { get { return new Rectangle(Body.X, Body.Top, 248, Body.Height); } }
internal Rectangle RightPanel { get { return new Rectangle(Body.Right - 260, Body.Top, 260, Body.Height); } }
internal Rectangle CenterPanel { /* LeftPanel.Right + Gap … RightPanel.Left - Gap */ }
internal Rectangle BottomBar { get { return new Rectangle(UiTheme.Margin, ClientSize.Height - 30,
                                       ClientSize.Width - UiTheme.Margin * 2, 22); } }
```

### 6.2 `UiTheme.cs` 完整调色板（每个颜色常量 + hex）

| 常量 | `Color.FromArgb` | HEX | 用途 |
| --- | --- | --- | --- |
| `Canvas` | 250, 250, 251 | `#FAFAFB` | 窗口底色，最亮的一层 |
| `Surface` | 255, 255, 255 | `#FFFFFF` | 卡片/面板底色 |
| `Subtle` | 244, 245, 247 | `#F4F5F7` | 次级底色（输入框、列表条纹、悬停） |
| `Border` | 214, 218, 224 | `#D6DAE0` | 唯一的边框色 |
| `BorderStrong` | 64, 120, 200 | `#4078C8` | 选中态强调边框 |
| `TextColor` | 32, 36, 44 | `#20242C` | 主文字 |
| `TextMuted` | 122, 130, 142 | `#7A828E` | 次级文字（数值与状态） |
| `TextDisabled` | 178, 184, 194 | `#B2B8C2` | 不可用 |
| `Accent` | 64, 120, 200 | `#4078C8` | 强调色（选中条目与主按钮） |
| `AccentSoft` | 232, 240, 252 | `#E8F0FC` | 选中条目底色 |
| `Active` | 38, 150, 96 | `#269660` | 接管中（绿） |
| `Danger` | 198, 64, 64 | `#C64040` | 警告/失败（红） |

字体（静态初始化时从系统字体里挑，不自带字体文件）：

```csharp
private static string PickFamily()
{
    var wanted = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans SC", "Segoe UI" };
    foreach (var name in wanted)
        foreach (var family in FontFamily.Families)
            if (string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase))
                return family.Name;
    return FontFamily.GenericSansSerif.Name;
}

internal static readonly Font Body     = new Font(Family,  9f, FontStyle.Regular, GraphicsUnit.Point);
internal static readonly Font BodyBold = new Font(Family,  9f, FontStyle.Bold,    GraphicsUnit.Point);
internal static readonly Font Small    = new Font(Family,  8f, FontStyle.Regular, GraphicsUnit.Point);
internal static readonly Font Title    = new Font(Family, 11f, FontStyle.Bold,    GraphicsUnit.Point);
```

尺寸常量：`BorderWidth = 1`、`Padding = 12`、`Gap = 8`、`Margin = 10`、`RowHeight = 34`、`InstrumentCardWidth = 104`、`InstrumentCardHeight = 96`。

三个绘制原语（**整套界面所有分块都必须走这三个**）：

```csharp
internal static void Frame(Graphics graphics, Rectangle bounds, Color fill, Color border)
{
    using (var brush = new SolidBrush(fill)) graphics.FillRectangle(brush, bounds);
    // 描边画在矩形内侧，这样相邻面板的边框不会互相压掉半个像素。
    using (var pen = new Pen(border, BorderWidth))
    {
        var inset = new Rectangle(bounds.X, bounds.Y, bounds.Width - BorderWidth, bounds.Height - BorderWidth);
        graphics.DrawRectangle(pen, inset);
    }
}
```

`DrawText` 的两条硬约束（都被自检逼出来的）：

```csharp
/// 用 `Graphics.DrawString`（GDI+ 路径），不用 `TextRenderer`（GDI 路径）。
/// `TextRenderer` 在窗口上直接绘制时没问题，但一旦把窗口抄进离屏位图（自检用的 `DrawToBitmap`），
/// 就会出现难以预测的丢字 …
/// 这里必须**显式传入**一个 `StringFormat`。… 不带 `StringFormat` 的那个重载在离屏渲染下一律
/// 画不出字（0 像素）…
```

`DrawSprite` 用 `InterpolationMode.NearestNeighbor` + `PixelOffsetMode.Half` 保持 16×16 像素风。

### 6.3 `UiSmokeTest` 如何无头（headless）渲染截图

**CLI 旗标**：`TingYu.Manager.exe --ui-smoke[=<输出目录>]`
裸 `--ui-smoke` → 输出到 `Path.Combine(Path.GetTempPath(), "tingyu-ui-smoke")`。
另有 `TingYu.Manager.exe --where` 打印定位到的 `Terraria.exe`。
（`Program.Argument` 同时接受 `--ui-smoke=DIR` 与 `--ui-smoke DIR` 之外的裸形式；裸形式返回 `string.Empty`，`Run` 内部再补默认目录。）

**渲染方式**（刻意不用 `DrawToBitmap`）：

```csharp
using (var form = new MainForm())
{
    form.StartPosition = FormStartPosition.Manual;
    form.Location = new Point(-4000, -4000);   // 挪到屏幕外，避免闪窗
    form.Show();
    Application.DoEvents();

    foreach (var size in sizes)                // {980x640, 1180x720, 880x560}
    {
        form.ClientSize = size;
        form.PerformLayout();
        Application.DoEvents();

        var bitmap = Render(form);             // Bitmap + Graphics.FromImage + form.Render(g)
        var path = Path.Combine(outputDirectory, "ui-" + tag + ".png");
        bitmap.Save(path, ImageFormat.Png);
        ...
    }
    form.Close();
}
```

```csharp
private static Bitmap Render(MainForm form)
{
    var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height, PixelFormat.Format32bppArgb);
    using (var graphics = Graphics.FromImage(bitmap))
    {
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(UiTheme.Canvas);
        form.Render(graphics);
    }
    return bitmap;
}
```

**输出**：`ui-980x640.png`、`ui-1180x720.png`、`ui-880x560.png`、`ui-smoke.txt`；报告同时 `Console.WriteLine`。
退出码：通过 `0`，失败 `2`。

**断言**（阈值都留了余量）：

```csharp
Require(failures, tag, "画布几乎看不到（面板之间可能没有留缝）",  canvas * 200  >= total);
Require(failures, tag, "面板几乎看不到",                          surface * 100 >= total);
Require(failures, tag, "边框太少（分块可能没画出来）",             border * 1000 >= total);
Require(failures, tag, "强调色缺失（主按钮或选中态没画）",          accent > 200);
Require(failures, tag, "颜色数过少（可能是纯色块误渲染）",          colors.Count >= 8);
Require(failures, tag, "窗口边缘没有留白（面板糊满了整个窗口）",
        HasCanvasMargin(bitmap, UiTheme.Canvas, 6));                 // 只查左/右/下三边
var missing = MissingLabels(bitmap, labels);
Require(failures, tag, "以下位置没有画出任何内容：" + string.Join("、", missing.ToArray()),
        missing.Count == 0);
```

「这块区域该有东西」的清单**由界面自己给出**（`MainForm.ExpectedLabels()` 返回 `List<LabelCheck>`，坐标来自与绘制共用的 `TitleBounds/InstrumentRects/TrackTitleBounds/…`），而不是测试里另抄一份坐标：

> 这些方法存在的唯一理由是**坐标只能有一份**。之前自检里的期望区域是另抄一遍坐标并加上估算偏移，结果界面一改，自检就报出一堆并不存在的「缺失」，反而把真正的缺失淹没了。

「有内容」的判定是对**整个调色板**做容差 70 的近似匹配（`Palette = { TextColor, TextMuted, TextDisabled, Accent, AccentSoft, Active, Danger, Color.White }`），区域里至少 8 个命中像素才算非空白：

```csharp
/// 判定方式是**只认调色板里的文字色**，而不是「和背景色不同」——
/// 第一版按后者写，把文字本身也当成了背景，于是每个区域都判成空白，自检反过来报假错。
/// 第二版只认文字主色，又把灰阶的次要文字（例如禁用状态的按钮文字）判成缺失，仍是假报。
```

**现有产出的实测报告**（`build\Release\ui-smoke\ui-smoke.txt`）：三个尺寸各 176 种不同颜色、画布占比 6.0~7.8%、面板占比 80.9~85.7%、边框占比 2.3~3.1%、`预期标签 23 项 · 有内容的 23 项`，结论 `界面自检通过。`

### 6.4 图标走「从玩家自己的游戏里解码」

`SpriteStore` 不在仓库里存任何美术资源（那是 Re-Logic 的版权素材），而是在玩家机器上用 XNA 的 `ContentManager` 解 LZX 压缩的 XNB：

```csharp
internal static readonly SpriteDefinition[] All =
{
    new SpriteDefinition { Key = "harp",     AssetPath = "Images/Item_508"  },
    new SpriteDefinition { Key = "bell",     AssetPath = "Images/Item_507"  },
    new SpriteDefinition { Key = "axe",      AssetPath = "Images/Item_1305" },
    new SpriteDefinition { Key = "ivy",      AssetPath = "Images/Item_4372" },
    new SpriteDefinition { Key = "rainsong", AssetPath = "Images/Item_4057" },
    new SpriteDefinition { Key = "stellar",  AssetPath = "Images/Item_4715" }
};
```

要点：独立 **STA 线程**跑解码（XNA 需要窗口句柄）；`GraphicsProfile.HiDef`（Terraria 内容按 HiDef 编译，Reach 档读不了）；先写临时目录、全部成功再一次性发布（半个缓存比没有更糟）；XNA 纹理是**预乘 alpha**，写 PNG 前要反预乘；缓存位置 `<game>\TingYu\sprites\`，靠 `manifest.txt`（含 `FormatVersion`）判版本，版本不符即重建；读缓存用 `Image.FromStream` 后**必须拷出来**（Image 是惰性的会一直持有文件句柄）。

---

## 7. 无游戏测试

### 7.1 先澄清：**不存在 `TingYu.TestHost`，也不存在 `TingYu.Tools`**

`src\` 下只有 4 个工程（Core / Plugin / Patcher / Manager）。全树也只列到 `tools\` 下的 **7 个 PowerShell 脚本**（`build / deploy-test / e2e-test / launch-test / read-log / refresh-test-copy / smoke-test`）和 `tmp\probe\Probe.cs`。没有任何测试框架（无 NUnit/xUnit/MSTest），没有 `*Tests` 工程。

### 7.2 各个「测试」到底验什么，以及**能不能在没有游戏的机器上跑**

| # | 入口 | 验什么 | 需要游戏吗 |
| --- | --- | --- | --- |
| 1 | `TingYu.Patcher.exe verify --terraria <exe> --plugin <TingYu.Plugin.dll>` | **离线成员核对**：用 `Assembly.LoadFrom` 加载插件 DLL（纯托管、只依赖 .NET 基础库，安全），读出 `ReflectionRequirements.All`；再用 Cecil 打开 **Terraria.exe 的元数据**，逐项核对类型存在、字段 vs 属性、方法重载（参数类型前缀全名匹配 + 尾部参数必须 `IsOptional`），并**沿继承链**查找（`FindField/FindProperty/AllMethods` 都走 `ResolveBase`）。输出 `共核对 N 项成员（清单来自 TingYu.Plugin.dll）`。退出码 0 = 必需项全过，2 = 有问题，1 = 环境错误 | **需要 `Terraria.exe` 文件**（要读元数据），**不需要启动游戏** |
| 2 | `TingYu.Patcher.exe patch-copy --terraria <exe> --plugin <dll> --out <文件>` | **离线注入演练**：写出注入副本并跑 `AssemblyPatcher.Validate` 的全部布局断言。不碰游戏本体 | 需要 `Terraria.exe` 文件，不需要启动 |
| 3 | `TingYu.Patcher.exe status --terraria <exe>` | 版本 + SHA-256 + 安装状态 | 需要 `Terraria.exe` 文件 |
| 4 | `TingYu.Manager.exe --ui-smoke[=dir]` | **界面渲染自检**（见 §6.3）：3 个尺寸的位图 + 7 条断言 + 23 项标签区域非空白检查 | **不需要**（找不到游戏就画占位框；也不调用 XNA 解码路径） |
| 5 | `TingYu.Manager.exe --where` | 只打印定位结果，用于验证 `TerrariaLocator` | **不需要** |
| 6 | `TingYu.Patcher.exe install` / `restore` | 真实注入/还原（含 `EnsureGameClosed`） | **需要**游戏安装 + 未运行 |
| 7 | `tools\smoke-test.ps1` | 启动 `_recon\gametest\Terraria.exe`（带 `-autoplay`、`-logerrors`、`-logfile`、`-savedirectory`、`-playersave`、`-world`），轮询进程存活与 `tingyu.log` 行数，最后打印日志尾部 + `status.txt` + `TingYu\` 目录 + 游戏日志尾部。`-KeepAlive` 可留着进程 | **需要**：真实游戏 + 已注入的测试副本 + 指定存档 |
| 8 | `tools\e2e-test.ps1` | **无人值守端到端**：设置 `$env:TINGYU_AUTOPLAY`（`<trackId>[:秒数]` 自检模式，绕过键盘），写 `verbose=1` 配置，启动游戏，用 `SetForegroundWindow` + `SendKeys` 连发 3 个 `{ENTER}` 走完「主菜单→单人→选角色→选世界」，轮询 `status.txt` 的 `^active=1`，超时后杀进程并 dump 日志/状态。进程早退 → `exit 3` | **需要**：真实游戏 + 存档 + 可交互桌面（SendKeys 要前台窗口） |
| 9 | `tools\deploy-test.ps1` / `refresh-test-copy.ps1` / `launch-test.ps1` | 部署辅助：拷 `TingYu.Plugin.dll`/`TingYu.Core.dll` 到 `_recon\gametest`，跑 `patch-copy` 生成注入后的副本，写 config，启动游戏 | **需要**；且三者把源路径**硬编码**为 `D:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe` |
| 10 | `tools\read-log.ps1` | 读 `boot.log` / `tingyu.log` / `status.txt`，**显式** `[System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)` | 不需要（但没有运行过就没有日志） |
| 11 | `tmp\probe\Probe.cs`（编成 `Probe.exe`） | **纯 Core 算法探针**：对每首内置曲目 × 每件乐器，算 `Performance.SuggestBaseMidi`、八度折叠后的级数集合、用到的档位数、覆盖的音域，写 `probe-report.txt`。**完全不引用游戏，也不引用 Plugin/Patcher** | **不需要**。但它没有 csproj，是临时手编的（源码在 `tmp\`，而 `tmp/` 被 gitignore） |

### 7.3 明确回答「没有游戏时构建/测试能不能跑」

* **构建：能。** 只需 VS2022 MSBuild + .NET Framework 4.8 目标包 + GAC_32 里的 XNA 4.0。（`build.ps1` 的 `$TerrariaDir` 参数完全没用上。）
  * 唯一现实门槛是 XNA：Manager 引用了 XNA 4.0，缺了就连编译都过不去（`Assert-XnaPresent` 会先抛）。而 XNA 通常是装游戏时一起进的，或单独装 XNA Framework Redistributable 4.0。
* **UI 自检 / `--where`：能，完全不需要游戏。**
* **`verify` / `patch-copy` / `status`：需要机器上存在一个 `Terraria.exe` 文件**（读元数据/算哈希），但**不需要启动它**，也不需要存档。
* **smoke-test / e2e-test / deploy / launch：不能。** 必须有真实安装、已注入的副本、`.plr`/`.wld` 存档、可交互桌面，还会真的启动游戏并强杀进程。
* **`install` / `restore`：不能**（会真的改游戏文件，并且要求游戏没在运行）。

---

## 8. Gotchas（代码 / CHANGELOG / 注释里明确记录的坑）

### 8.1 编码

1. **PowerShell 5.1 的 `.ps1` 必须存成「UTF-8 带 BOM」**（脚本头部原文注释）：
   > 本文件必须以「UTF-8 带 BOM」保存（Windows PowerShell 5.1 读无 BOM 的 .ps1 会按 ANSI 解码）。
   > Windows PowerShell 5.1 读无 BOM 的 .ps1 会按 ANSI 解码，中文路径会变成乱码，`Start-Process` 直接失败。
   实测：`install.ps1`、`restore.ps1` 与全部 `tools\*.ps1` **都有 BOM**，只有 `tools\read-log.ps1` 没有（它是唯一的纯读取脚本）。本会话的 shell 正是 `PSVersion 5.1.26100.9444 / Desktop`，能直接复现 `Get-Content` 默认按 ANSI 读中文变乱码的现象。
2. **日志用系统 ANSI（中文 Windows 即 GBK），Config/Status/Manifest 用 UTF-8 无 BOM**——同一个数据目录里混了两种编码：

   ```csharp
   /// 日志编码用系统 ANSI 代码页（中文 Windows 即 GBK），不用 UTF-8。
   /// 原因很实际：这些日志是给人用记事本 / `type` / `Get-Content` 直接看的，
   /// 而 Windows PowerShell 5.1 的 `Get-Content` 默认按 ANSI 解码，
   /// 写 UTF-8 会让整份日志变成乱码。
   private static readonly Encoding LogEncoding = Encoding.Default;
   ```
   `boot.log` 同样优先 `Encoding.Default`，遇 `EncoderFallbackException` 再退 `new UTF8Encoding(false)`；连 `Encoding.Default` 本身抛异常都有 catch。
3. **`config.txt` 绝对不能带 BOM**：带 BOM 会让插件把第一行的键读成 `"\uFEFFenabled"`，于是 `enabled` 解析不到。写入端一律 `new UTF8Encoding(false)`。
4. **C# 源码里没有 BOM**（全部 `*.cs` 实测无 BOM），中文靠 UTF-8 本身。

### 8.2 PowerShell 的引号/路径陷阱

5. **`-LiteralPath` 不展开通配符**（CHANGELOG 1.0.1 记录的构建事故）：
   > 原先用了 `-LiteralPath` 而不展开通配符，发行目录只剩下文档、压缩包里没有程序。
   ```powershell
   # 这里必须用 -Path：-LiteralPath 不展开通配符，`$payload\*` 会被当成字面文件名
   # 而静默什么都不复制——发行目录于是只剩文档，压缩包里没有程序。
   Copy-Item -Path "$payload\*" -Destination $release -Recurse -Force
   ```
6. **参数传递用数组 + splat，不拼字符串**（`install.ps1`）：
   ```powershell
   $arguments = @('install', '--payload', $here)
   if ($TerrariaDir) { $arguments += @('--terraria', (Join-Path $TerrariaDir 'Terraria.exe')) }
   & $patcher @arguments
   ```
   带空格的游戏路径（`D:\Program Files (x86)\...`）因此不需要手写引号。
7. **`Get-Content` 读日志要显式给编码**（`read-log.ps1`）：
   ```powershell
   $lines = [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)
   ```
8. **关于 `git commit` 多行**：全树 grep（含 `*.md / *.ps1 / *.cs / *.txt`）**没有任何 `git` 相关记载**（也没有 `.git` 目录、没有 `.gitmessage`、没有 hook）；`.gitignore` 只忽略构建产物与 `_recon/ tmp/ lib/`。所以「git commit multiline」在这个项目里**没有对应证据**，不要把它当既有约定照抄。（CHANGELOG 里唯一的脚本类陷阱就是上面第 5 条。）

### 8.3 哈希 / 版本 / 状态

9. SHA-256 存**大写** hex，比较用 `OrdinalIgnoreCase`。
10. **还原前先核备份哈希**，不符就拒：
    > `原版备份的哈希与清单不符，拒绝用一个来路不明的备份覆盖游戏。`
11. **备份丢失就拒绝在已注入的文件上再注入**：`原版备份不见了，拒绝在已注入的文件上再注入。请用 Steam 校验文件完整性。`
12. **删掉 `tingyu-install.txt` 等于放弃还原能力**（清单里那句注释就是为此写的）。
13. `File.Replace(..., null, true)` 做原子替换；`File.Copy` 只用于备份与临时文件。
14. **重复注入防护**：`AssemblyReferences` 里出现 `TingYu.Plugin` → 「已经注入过，请先还原」；出现 `Chaite.Plugin` → 「检测到拆特的注入，请先还原拆特」。兄弟工具「拆特」用同一个套路，它的备份名是 `Terraria.exe.960A03BFF6050CF7.backup`（哈希前 16 位 + `.backup`），在 `_recon\gametest\Chaite\` 里可见。

### 8.4 x86 / x64 / XNA

15. **`TingYu.Plugin` 必须 x86**（csproj 注释）：
    > x86 是硬要求：这个 DLL 被注入进 Terraria.exe，而 Terraria 是 32 位进程，64 位的程序集加载不进去。AnyCPU 在 32 位宿主里也一样能跑，但明确成 x86 可以避免任何人把它当库在 64 位进程里引用。
16. **`TingYu.Manager` 必须 x86**（csproj 注释）：
    > x86 不是偏好，是硬约束。管理器要从玩家自己的 Terraria 安装里读乐器贴图，而那批 XNB 是 LZX 压缩的，机器上唯一能解压它的东西就是 XNA 自带的 ContentManager，而 XNA 只有 32 位程序集——64 位进程根本加载不了它。Terraria 本体也是 x86。
17. **XNA 只在 `GAC_32`**，**不在游戏目录**（`build.ps1` 注释）：
    > 这些程序集在 GAC 里（`C:\Windows\Microsoft.NET\assembly\GAC_32`），**不在游戏目录里**，所以 csproj 用简单名引用即可… 早先的版本假设 XNA 在游戏目录，那会导致构建直接失败。
18. XNA 的 `GraphicsDevice` 需要窗口句柄（哪怕窗口从不显示）、`GraphicsProfile.HiDef`、以及 STA 线程；无显示的机器上**无法首次提取贴图**（但界面会用占位框正常工作）。
19. XNA 纹理是**预乘 alpha**，写 PNG 要反预乘，否则每条软边发暗。

### 8.5 Cecil / IL

20. **必须手工加宽短跳转**：Cecil 不会因为插入指令而把 `Br_S` 自动换成 `Br`，超过 127 字节编码就溢出 → 写出非法 exe。见 §3.3 `WidenShortBranches`。
21. **`PatchInstrumentEntry` 的插入顺序不能变**：先插 `ret` 在原首指令前，再插 `call` + `brfalse` 在 `ret` 前，这样「原版方法体入口 = 原来的第一条指令」，`brfalse` 的分支目标天然正确。
22. **CU 锚点必须唯一**：`TriggersSet.CopyInto` 在 `Player.Update` 里的调用点多于/少于 1 处就拒绝注入，绝不猜位置。
23. **写完必须再读回来核**：`Validate` 不信「我以为我插进去了」，只信写出去的文件里的实际布局（连 `head[1].Operand == head[3]` 这种引用相等都查）。
24. 用 `InMemory = true` 读源 exe；`ReadSymbols = false` / `WriteSymbols = false`（Terraria.exe 无 PDB）。
25. `AssemblyResolver` 必须同时加**插件目录**和**源 exe 目录**，否则 Cecil 解析不了交叉引用。

### 8.6 反射

26. **字段与属性是两条完全不同的路**，写错只会**静默拿到 null**（注释原文：「照直觉当字段读只会静默拿到 null」）。核对器专门给出提示：`应声明为 Property` / `应声明为 Field`。
27. **继承成员必须 `FlattenHierarchy`**：`Player.Center` 与 `Entity.whoAmI` 定义在基类 `Terraria.Entity` 上；MemberVerifier 也必须沿继承链找（注释：「我正是因此白跑了一轮游戏测试」）。
28. **`selectedItemState` 是结构体字段**：反射 `GetValue` 给的是副本，改副本无效；必须 `SetValue` 写回。
29. **`SoundEngine.PlaySound` 有 4 个重载**，其中两个首参都是 `int`，必须靠完整参数类型表 `(Int32, Vector2, Int32, Single)` 才能唯一定位。
30. **`Sandstorm.Happening` 在 `Terraria.GameContent.Events.Sandstorm` 上，不在 `Main` 上**（照直觉写 `Main.sandstormHappening` 会永远读到 false）。
31. **反射调用最好一次解析、缓存 `MemberInfo`**：`GetField` 放进 60 Hz 循环里会让帧时间明显变长。
32. **`TargetInvocationException` 必须剥壳**，否则消息永远是套话「Exception has been thrown by the target of an invocation」。
33. **「读抛异常」与「值本来就是 0」必须能区分**：`GameFacade.LastReadError` / `LastReadException` 就是为此存在（注释：「我正是在这里丢掉了一次关键线索」）。

### 8.7 运行时行为

34. **`Main.Camera.SmallerScaledAxis` 在 1.4.5.8 运行时返回 0**（内部读 `GraphicsDevice.Viewport` 失败且异常被吞），导致归一化距离全被钳到 1，**整首曲子只有一个音高**，而节奏/音符数/日志全都正常。回退到 `min(screenWidth, screenHeight)`（缩放 1.0、平移 0 时与 `ScaledSize` 完全相等，实测 1066 == min(1706,1066)）。CHANGELOG 1.0.0 记录了这条 bug。
35. **`Math.Round` 是银行家舍入**：音高级数正好落在中点时偶数优先，调用方不应把距离放在中点。
36. **F8 与 Terraria 自己的网络统计浮层冲突** → 默认热键改 **F10 (VK 0x79)**；F10 在 1.4.5.8 默认按键表里是空的。
37. **`XNA Keyboard.GetState` 失焦后读不到键** → 用 `GetAsyncKeyState`；但必须加**前台 pid 判定**，否则在别的程序里打字会误触发。
38. **换手是缓冲动作**，一定跨 tick，且 `Player.Update` 的分支会重算 `selectedItem` → 必须每 tick 重新钉住，否则「第一下弹了、后面全哑了」。
39. **钩子里绝不能抛异常**：一次冒泡就可能让 Terraria 直接崩。四个钩子 + `Runtime` 各层全部 `try/catch` → 写日志 → 释放接管。
40. **初始化日志必须在初始化之前写**：`Bootstrap("钩子被调用，开始初始化")` 是「证明钩子真的被调到了」的唯一手段（之前失败时一个字节日志都没有，只能靠反汇编猜）。
41. 界面自检自身的两次假报（见 §6.3 引文）：把文字色当背景、区域坐标抄两份而漂移。
42. `install.ps1` / `restore.ps1` 在检测到运行中的 Terraria 时，会把**进程路径**一起打出来（注释：「机器上可能同时开着别的 Terraria 实例（例如其它工具的探针）」）。
43. 载荷放子目录 → `FileNotFoundException` + **游戏退出码 `0xE0434352`**（CLR 启动阶段探测只看 exe 同级 + GAC）。这条在 CHANGELOG、`Runtime`、`build.ps1`、`install.ps1` 四处重复，是最贵的一课。

---

## 9. 附属目录与脚本说明（按要求只列不展开）

### 9.1 `_recon\terraria-src\` —— **是反编译出来的 Terraria 源码**

* 结构：**1,652 个文件、约 38.8 MB**，按游戏命名空间分目录：`Terraria`(78)、`Terraria.GameContent`(91) 及其 40+ 子命名空间、`Terraria.DataStructures`(126)、`Terraria.ID`(61)、`Terraria.GameContent.UI.Elements`(74)、`Terraria.WorldBuilding`(45)、`Terraria.UI`(37)、`Terraria.Social.Steam`(19)、`Terraria.Social.WeGame`(26)、`Terraria.Testing.Cloning`(18) 等。
* 明确是**反编译产物**（不是官方源码）的证据：
  * 根目录有一个 **SDK-style** `Terraria.csproj`：`<OutputType>WinExe</OutputType>`、`<UseWindowsForms>True</UseWindowsForms>`、`<TargetFramework>net40</TargetFramework>`、`<PlatformTarget>x86</PlatformTarget>`、`<LangVersion>12.0</LangVersion>`、`<AllowUnsafeBlocks>True</AllowUnsafeBlocks>`、显式 `app.ico` / `app.manifest`；`<Compile Include=` 数量为 **0**（靠默认 glob），这正是 ILSpy「导出为工程」的产物形态。
  * 根目录同时导出/附带了游戏的第三方库（`Terraria.Libraries.*.dll`：ReLogic、Steamworks.NET、Newtonsoft.Json、MP3Sharp、NVorbis、Ionic.Zip、CsvHelper、RailSDK、SteelSeries）、本地化 JSON（en-US/zh-Hans/zh-Hant/ru-RU/ja-JP/ko-KR/… 各 6 个文件）、TSV（`…Sacrifices.tsv`、`…ResourcePacksDefaultInfo.tsv`）、`Microsoft.Xna.Framework.RuntimeProfile`、`nativefiledialog.cs`。
  * 未发现 `// ILSpy` / `// dnSpy` 之类的横幅注释（ILSpy 8+ 默认不写），但上述工程形态足以定论。
* 已核验注入点在这个源码树里存在：`Terraria\Main.cs`（2.2 MB）第 **18049** 行 `private void UpdateWorld_Players()`，第 17981 行调用它。
* 用途：**离线查证原版行为**（音高公式、发声分支、Sandstorm 位置等），是「不启动游戏就能核对」的基础。`_recon/` 在 `.gitignore` 里，不入库。

同目录下还有：`gametest\`（注入测试副本，含 `Terraria.exe`、`Content\`、`saves\`、`logs\`、`Chaite\`）、`patchtest\Terraria.patched.exe`、`Terraria.exe.from-unknown-source`，以及 4 个 Python 探针 `dbg.py`、`dbg2.py`、`xnb_probe.py`、`extract_sounds.py`（+ `Item_26.wav`、`__pycache__`）。

### 9.2 `tmp\probe\Probe.cs` 做什么

一个**临时的一次性算法探针**（`internal static class Probe`，`Main()`，68 行），只依赖 `TingYu.Core`，**不引用游戏、不引用 Plugin/Patcher**：

1. 打印 `NoteNames.MiddleC`（回退基准音）；
2. 对每首内置曲目（`BuiltInSongs.List()` → `BuiltInSongs.Find(id)`）：收集并排序所有 `note.Midi`，打印音符数、原始 MIDI 区间、音名区间、半音跨度；
3. 对**每一件乐器**（`InstrumentModel.All`）：用 `Performance.SuggestBaseMidi(score, instrument, fallback)` 求基准音，把每个音按 `±12` 做八度折叠，再 `instrument.NearestStepForMidi(...)` 求级数，统计**实际用到的不同级数**（`用到 N/13 级`）、发声音名区间、级数列表；
4. 全部结果写 `probe-report.txt`（UTF-8），整体包在 `try/catch` 里，异常时把异常类型/消息/堆栈/内层异常也写进报告。

用途：回答「内置三首歌究竟能用到几档音高」这个只能靠算、不能靠听的问题（CHANGELOG 1.0.2 里「实测内置三首曲子都从 3~5 档提升到用满 6 档」这个结论的数据来源就是它）。**没有 csproj**，是手编成 `tmp\probe\Probe.exe` 的；`tmp/` 被 gitignore。

---

## 10. 给「自动炸隔离带」的复用清单

### 可以直接照抄的部分（与「乐器」无关）

| 能力 | 来源文件 | 复用方式 |
| --- | --- | --- |
| 版本+哈希双重锁定 + 清单 + 备份/还原/重装状态机 | `TingYu.Patcher\InstallationService.cs` | 改 3 个常量（版本、哈希、数据目录名）与 `RequiredPayload` 即可 |
| 游戏定位（注册表→libraryfolders.vdf→兜底） | `TingYu.Patcher\TerrariaLocator.cs` | 原样；可换 appid/目录名 |
| Cecil 四个锚点的注入骨架（含短跳加宽、唯一调用点定位、写完回读校验） | `TingYu.Patcher\AssemblyPatcher.cs` | 换方法名与钩子签名；`Validate` 的结构必须一起改 |
| 单一真源的成员清单 + 反射解析 + 构建期离线核对 | `TingYu.Plugin\ReflectionRequirements.cs`、`GameFacade.cs`、`TingYu.Patcher\MemberVerifier.cs` | **强烈建议整块照抄**：这是「不启动游戏也能一次报全所有绑定错误」的关键 |
| 静态注入入口 + 惰性初始化 + boot.log 兜底 + 钩子异常隔离 | `TingYu.Plugin\Runtime.cs` | 换初始化步骤与状态字段 |
| 跨进程文件握手（config.txt/status.txt + mtime 触发 + 一次性 request） | `Runtime.cs` + `Manager\ConfigView.cs` | 原样；注意 UTF-8 无 BOM 与「两端各写一份类型」的取舍 |
| 全局热键轮询（GetAsyncKeyState + 前台 pid 判定 + 边沿检测） | `TingYu.Plugin\Hotkey.cs` | 原样，换默认 VK |
| 亮色自绘 WinForms（主题+绘制原语+完全自绘+坐标单一来源） | `Manager\UiTheme.cs`、`MainForm.cs` | 主题色可整体替换；`ExpectedLabels()` 模式照抄 |
| 离屏 UI 自检（`--ui-smoke` + 位图断言 + 调色板 ink 判定） | `Manager\UiSmokeTest.cs` | 原样；改断言阈值与标签清单 |
| GBK 日志 + 环形缓冲 + 定时 flush | `Plugin\Diagnostics.cs` | 原样 |
| 从游戏 Content 解码官方贴图（XNA ContentManager + 反预乘 + 清单版本） | `Manager\SpriteStore.cs` | 若新项目不需要游戏素材，可整块删掉（同时也就能去掉 XNA 依赖） |

### 必须改的部分

1. **四个锚点**：`UpdateWorld_Players` / `ItemCheck_PlayInstruments` / `Player.Update` 这套是为「在更新循环里插入一段受控行为」设计的。新项目若要在别处下手（例如物品使用、投射物更新），要先在 `_recon\terraria-src\` 里找**唯一且稳定可定位**的方法与调用点，并把定位规则写成「按签名 + `RequireUniqueCall` 唯一性」，绝不按 IL 偏移。
2. **版本锁定常量**：新项目若针对同一份 1.4.5.8，可沿用 `960A03BF…A2F3`；若针对别的构建（例如 tModLoader 版或更新版本），必须重新反编译核验并换哈希。**绝不能只比版本号**。
3. **`DataFolderName`**：现在硬编码为 `"TingYu"`，新项目要换成自己的名字，否则两个工具的 `config.txt`/`status.txt`/备份会互相覆盖。
4. **Chaite 检测**：`AssemblyPatcher` 里对 `Chaite.Plugin` 的互斥检测要换成新项目的互斥项。
5. **`Manager` 的 `--ui-smoke` 与 `Patcher` 的 `verify`/`patch-copy` 要一并保留**——这是这个项目能在没有人工点检的情况下推进的全部依据。
6. **去掉 XNA 依赖（可选但推荐）**：如果新项目不需要从游戏 Content 解码贴图，把 `SpriteStore.cs` 删掉，Manager 就能变成 AnyCPU、`build.ps1` 的 `Assert-XnaPresent` 也能去掉，构建门槛会显著降低。

### 三条必须内化的纪律

1. **只信写出去的文件。** 注入完必须重新读回来断言（`Validate`）；核对工具必须读插件 DLL 里的那份清单，而不是另写一份。
2. **拒绝猜测。** 定位不到唯一锚点就抛异常停下，宁可安装失败也不写出可能让游戏启动即崩的 exe。
3. **一切落盘。** 注入层的日志是唯一证据（进程崩了没有控制台），且初始化最早期的那一行必须不依赖任何状态就能写出去。
