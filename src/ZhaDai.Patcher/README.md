# ZhaDai.Patcher

「自动炸隔离带」的**可逆 IL 注入工具**。它把三个 `call` 指令写进 `Terraria.exe`，让
`ZhaDai.Runtime.dll` 能在原版更新循环的固定位置被调用；同时提供备份、清单、原子替换与还原，
保证随时可以退回逐字节一致的原版。

```
zhaodai-patcher verify      --terraria <Terraria.exe> [--plugin <ZhaDai.Runtime.dll>]
zhaodai-patcher patch-copy  --terraria <Terraria.exe> --out <copy.exe> --plugin <ZhaDai.Runtime.dll>
zhaodai-patcher install     --terraria <Terraria.exe> --plugin <ZhaDai.Runtime.dll> [--dry-run]
zhaodai-patcher restore     --terraria <Terraria.exe>
zhaodai-patcher status      --terraria <Terraria.exe>
```

`--terraria` 可以省略：先看环境变量 `ZHAODAI_TERRARIA`，再走 Steam 注册表与
`libraryfolders.vdf`，最后是几个常见安装路径。**不做全盘搜索。**

---

## 1. 给 `ZhaDai.Runtime` 的契约（这是本文件最重要的一节）

运行时插件要满足下面的形状，才能被注入的 `call` 指令绑定上。所有钩子都是
**`public static`、无参**，放在 **`ZhaDai.Runtime.Hooks`** 上：

| # | 钩子 | 精确签名 | 注入位置 | 语义 |
| --- | --- | --- | --- | --- |
| A1 | `BeforePlayerUpdate` | `public static void BeforePlayerUpdate()` | `Terraria.Main::UpdateWorld_Players()` 的**第 0 条指令** | 帧开始。此时 `Main.player[]` 还没更新。 |
| A2 | `AfterPlayerUpdate` | `public static void AfterPlayerUpdate()` | `Terraria.Player::Update(System.Int32)` **最后一个 `ret` 之前** | 帧结束（正常出口）。 |
| A3 | `BeforeItemCheck` | `public static bool BeforeItemCheck()` | `Terraria.Player::ItemCheck_PlayInstruments(Terraria.Item)` 的**第 0 条指令** | 返回 `true` = 已接管，**原版方法体整体跳过**；返回 `false` = 走原版。 |

A3 的 IL 形状（注入后方法开头）：

```
call    bool ZhaDai.Runtime.Hooks::BeforeItemCheck()
brfalse <原版第一条指令>
ret
<原版方法体...>
```

### 硬约束

1. **程序集名与文件名**：插件程序集名必须是 `ZhaDai.Runtime`，构建产物是
   `ZhaDai.Runtime.dll`。注入时按**插件自身的程序集名**建立 `AssemblyNameReference`
   （不写死版本号，插件的 `AssemblyVersion` 是什么就用什么）。
2. **必须与 `Terraria.exe` 同级**。CLR 探测程序集只看**宿主目录**与 GAC，子目录不在其列；
   放到子目录会换来 `FileNotFoundException` + 游戏退出码 `0xE0434352`。这条在参考实现里
   是最贵的一课，不要重复。
3. **`x86`**：Terraria 是 32 位进程，插件必须能以 32 位加载（`AnyCPU` 也可以，但显式
   `x86` 能防止有人在 64 位进程里引用它）。
4. **钩子里绝不能抛异常**。异常冒泡进游戏的更新循环会直接崩游戏。每个钩子都自己
   `try/catch`，失败落盘并自我停用。
5. **不要给自己加返回值**。A1/A2 是 `void`，多一个返回值就会导致注入被拒（签名校验不通过）。

### 可选的「自描述成员清单」（强烈建议）

如果运行时提供一个下面这个类型，`verify` 会**自动**多核对一批游戏成员，不需要改 Patcher：

```csharp
namespace ZhaDai.Runtime
{
    public static class ReflectionRequirements
    {
        public static IEnumerable All { get; }   // 每项要有：
        //   string  Type        —— 例如 "Terraria.Player"；嵌套类型写 "Outer+Inner"
        //   string  Member      —— 成员名；Kind = "Type" 时可留空
        //   object  Kind        —— "Type" / "Field" / "Property" / "Method"（ToString() 后比较）
        //   bool    Required    —— 必需 or 可选
        //   string[] Parameters —— Kind = "Method" 时的参数类型全名前缀
    }
}
```

这样「工具说全过、插件在游戏里却报反射自检失败」这类互相打架的情况就不会发生——同一份清单，
两边共用。`--require-plugin-requirements` 可以把它变成强制项。

---

## 2. 三个锚点怎么定位的

**按名字 + 签名定位，绝不写死 MetadataToken。** 具体规则：

| 锚点 | 候选方法名（依次尝试） | 签名要求 |
| --- | --- | --- |
| A1 | `UpdateWorld_Players`、`UpdateWorld_Players_Inner` | 无参数、有方法体 |
| A2 | `Update` | 唯一参数 `System.Int32`、有方法体 |
| A3 | `ItemCheck_PlayInstruments`、`ItemCheck_PlayInstruments_Inner` | 唯一参数 `Terraria.Item`、有方法体 |

每个锚点解析成功后会**打印它实际解析到的 MetadataToken**。找不到或匹配到多个时**直接失败**，
错误信息里带上「查了哪些名字、各自的结果、实际存在哪些重载」——不猜、不硬编码偏移。

在本机已核验的 1.4.5.8 上解析结果（`patch-copy` 注入后回读，token 会随重写而变，这里仅供参考）：

| 锚点 | 方法 | 原版 token |
| --- | --- | --- |
| A1 | `System.Void Terraria.Main::UpdateWorld_Players()` | `0x06000D2B` |
| A2 | `System.Void Terraria.Player::Update(System.Int32)` | `0x06000990` |
| A3 | `System.Void Terraria.Player::ItemCheck_PlayInstruments(Terraria.Item)` | `0x06000AE8` |

> 说明：Cecil 重写整个模块后，元数据 token 会重新编号（注入后的名字没变、签名没变，只是编号变了）。
> 因此工具在任何地方都**不缓存、不硬编码 token**，每次都重新按名字解析。

---

## 3. 注入实现要点

- **`AssemblyNameReference` 必须显式建立。** Cecil 0.11 不会把 `TypeReference.Scope` 烘进写出的
  元数据里。如果直接把插件模块里的方法 `ImportReference` 过去，写出的 TypeRef 会**静默地指向
  `Terraria` 自己**，钩子在运行时永远解析不到。所以流程是：先 `new AssemblyNameReference(...)`
  并 `target.AssemblyReferences.Add(...)`，再 `ImportReference(hook)`，让钩子的 TypeRef 作用域
  指向 `ZhaDai.Runtime`。`Validate()` 会复查这一点。
- **`WidenShortBranches` 是必需的。** Cecil 不会因为在中间插指令而把 `Br_S` 自动换成 `Br`，
  一旦距离超过 127 字节，短跳转编码就溢出，写出的 exe 直接非法。三个被改过的方法里所有
  `Br_S / Brfalse_S / Brtrue_S / Beq_S / Bge_S / Bge_Un_S / Bgt_S / Bgt_Un_S / Ble_S / Ble_Un_S /
  Blt_S / Blt_Un_S / Bne_Un_S / Leave_S` 全部换成远跳转，并把条数打进报告。
- **写完必须回读校验（`Validate`）。** 只信「写出去的文件里确实是这个布局」，断言：
  存在 `ZhaDai.Runtime` 程序集引用；A1 第 0 条是 `call BeforePlayerUpdate`；A2 最后一个 `ret`
  的前一条是 `call AfterPlayerUpdate`；A3 头部严格是 `call / brfalse / ret` **且 `brfalse` 的目标
  正好是原版方法体的第一条指令**；每个钩子调用的 TypeRef 作用域都是 `ZhaDai.Runtime`。
- **拒绝叠加注入。** 目标里已经存在 `ZhaDai.Runtime` 引用，或者存在 `TingYu.Plugin` /
  `Chaite.Plugin` 引用，都直接拒绝安装。

---

## 4. 安装 / 还原 / 状态

三条硬规矩：

1. **只认已核验的构建**。版本号 `1.4.5.8` **且**整份 exe 的 SHA-256 等于
   `960A03BFF6050CF7BE16DFC1A7B19E10FC2C4F8F835A6A3B135A50DD9E6BA2F3`，才允许注入。
   只比版本号是不够的。
2. **写之前先备份，还原之前先核哈希**。备份在 `<游戏目录>\ZhaDai\Terraria.exe.orig`，
   **永不覆盖**；清单在 `<游戏目录>\ZhaDai\manifest.json`（UTF-8 无 BOM）。
   备份哈希与清单不符时拒绝用它覆盖游戏。
3. **游戏在跑就不动文件**。判定方式是「有没有一个 `Terraria` 进程的映像路径等于目标 exe」——
   精确到文件，既不会被无关的沙箱副本误挡，也不会漏掉真正占用文件的那个进程。

`install` 的步骤顺序（每一步都会打进报告）：

1. 核对 `--plugin` 与目标构建（版本 + SHA-256）；
2. **部署载荷**：把 `--plugin` 拷成 `<游戏目录>\ZhaDai.Runtime.dll`；
   ——必须在注入前做，因为回读校验会断言「CLR 将来要探测的那个路径上确实有这个文件」；
3. 注入到临时文件 `<游戏目录>\Terraria.exe.zhaodai-new` 并回读校验；
4. 备份原版到 `<游戏目录>\ZhaDai\Terraria.exe.orig`（**已存在就绝不覆盖**）；
   重装时改为「从已有备份反推干净原版再重新注入」，不叠加；
5. `File.Replace` 原子替换 `Terraria.exe`；
6. 写 `manifest.json`（UTF-8 无 BOM），再 `status` 回读一遍。

`restore` 只换回 `Terraria.exe`，**保留**备份、清单与已部署的 `ZhaDai.Runtime.dll`
（因此可以立刻再装一次，也不会误删别人放在游戏目录里的文件）。

`manifest.json` 的内容：
```json
{
  "tool": "zhaodai-patcher",
  "toolVersion": "1.0.0.0",
  "gameVersion": "1.4.5.8",
  "originalSha256": "960A03...A2F3",
  "patchedSha256": "7270...5350",
  "pluginVersion": "0.0.0.0",
  "pluginSha256": "9723...E25C",
  "installedUtc": "2026-09-29T00:46:06.8190361Z",
  "hooks": ["BeforePlayerUpdate", "AfterPlayerUpdate", "BeforeItemCheck"],
  "anchors": ["A1=System.Void Terraria.Main::UpdateWorld_Players() token=0x06000EB4", "..."]
}
```

`status` 的四种结论：

| 状态 | 判据 |
| --- | --- |
| 原版（受支持） | 无清单，版本与哈希都等于已核验值 |
| 原版（未验证的构建） | 无清单，版本或哈希对不上，且程序集里没有 ZhaDai 钩子 |
| 已注入 | 有清单，当前哈希 == 清单里的 `patchedSha256` |
| 已注入但被改动 | 有清单，但当前哈希既不是 `patchedSha256` 也不是 `originalSha256` |
| 半注入/清单缺失 | **没有清单，但程序集里已经能检测到 `ZhaDai.Runtime` 引用**——无法安全重装，提示用 Steam 校验文件完整性 |

`install --dry-run` 会把整套注入真的跑一遍（写到系统临时目录的 scratch 文件），并打印计划记录的
原版哈希与注入结果哈希，但**不创建、不修改游戏目录里的任何文件**。

---

## 5. 退出码

| 码 | 含义 |
| --- | --- |
| 0 | 成功 / 全部核对通过 |
| 1 | 错误（缺文件、IO 失败、操作被拒） |
| 2 | 核对失败（成员缺失、注入布局不符） |
| 3 | 用法错误 |
| 4 | 目标构建未验证 |

---

## 6. 构建

本机 .NET SDK 不在 PATH 上（全局是 3.0.101，构建不了现代 TFM），**用 VS2022 的 MSBuild**：

```powershell
powershell -ExecutionPolicy Bypass -File tools\patcher-build.ps1 -Configuration Release -Tests
```

`-Tests` 会额外构建 `src\ZhaDai.Patcher\testdata\ZhaDai.Runtime`（测试用替身插件）。
产物：`src\ZhaDai.Patcher\bin\Release\net48\ZhaDai.Patcher.exe`。

### 依赖

只依赖 **Mono.Cecil 0.11.6**，刻意**不走 NuGet**，四个 DLL 放在
`src\ZhaDai.Patcher\lib\`（`Mono.Cecil.dll` / `Mono.Cecil.Rocks.dll` / `Mono.Cecil.Pdb.dll` /
`Mono.Cecil.Mdb.dll`），csproj 用 `<HintPath>lib\Mono.Cecil.dll</HintPath>` 引用。
这四个 DLL 是二进制，不入库；缺失时 `patcher-build.ps1` 会明确报错。
本仓库的 `.gitignore` 目前**没有** `lib/` 一行（`src\ZhaDai.Patcher\lib\` 里的 DLL 因此会被 git 看到），
需要由仓库维护者补上；本工具只允许改自己目录下的文件，没有代改。

---

## 7. 目录结构

```
src\ZhaDai.Patcher\
  ZhaDai.Patcher.csproj      旧式 csproj，net48 / AnyCPU / LangVersion 7.3
  AnchorResolver.cs          按名字+签名解析三个锚点，报告 MetadataToken
  HookContract.cs            钩子契约的唯一真源（名字、签名、注入位置）
  AssemblyPatcher.cs         注入 + 短跳加宽 + 写完回读校验
  MemberVerifier.cs          verify 命令：离线解析锚点、钩子、游戏成员
  InstallationService.cs     install / restore / status / 清单 / 备份 / 原子替换
  TerrariaLocator.cs         定位 Terraria.exe
  PatchException.cs          注入失败异常
  Program.cs                 命令行入口（简体中文输出）
  Properties\AssemblyInfo.cs
  lib\                       Mono.Cecil 0.11.6 的四个 DLL
  testdata\ZhaDai.Runtime\   测试用替身插件（net48），只提供钩子签名
```

配套脚本（均在 `tools\`）：

```
tools\patcher-build.ps1     构建 ZhaDai.Patcher（-Tests 时连替身插件一起构建）
tools\patcher-verify.ps1    离线证据链：verify（缺插件/有插件）-> patch-copy -> 哈希对比 -> --verify-only -> status -> install --dry-run
tools\patcher-sandbox.ps1   在临时沙箱里跑 install -> status -> 重装 -> restore 往返，证明还原后端与已核验原版逐字节一致
```

> PowerShell 脚本必须以 **UTF-8 带 BOM** 保存：Windows PowerShell 5.1 读无 BOM 的 `.ps1`
> 会按 ANSI 解码，中文会变乱码。

---

## 8. 已知限制 / 未做的事

- **没有启动过游戏。** `patch-copy` 与沙箱 `install` 都只做到「IL 布局正确 + 引用能解析」，
  真正的运行期验证需要进世界实测（尤其 A3 的 `true` 分支会跳过整段原版物品检查逻辑）。
- 校验是 **Cecil 级的布局断言**，不是完整的 IL 栈平衡证明。不过注入的都是 `call` 一条指令，
  A3 分支形状也被逐条核对，且所有短跳都已加宽，风险集中在「钩子自身行为」而不是「IL 非法」。
- 只对 **1.4.5.8 / `960A03BF…A2F3`** 这一个构建开了写入许可；其它构建只能 `verify`，不能注入。
- 钩子参数表刻意保持**无参**，跨边界需要的信息（player 下标、tick 号）由运行时自己从游戏反射取。
  如果将来确实要传参，必须同步改 `HookContract`、`Validate` 与本节表格。
