# 造一份可以放心乱玩的测试副本：游戏副本 + 存档副本 + 插件 + 施工文件 + 按键接管配置。
#
# 特点：
#   * 游戏本体复制到副本目录，插件只装进副本，Steam 里那份原版一动不动。
#   * 存档（Worlds/Players/配置）复制到副本的 save 目录，用 -savedirectory 指过去。
#   * 进世界后按 F10 开始接管，再按 F10 停止；死亡后自动继续，直到施工文件跑完或 maxdeaths 用完。
#   * 只写副本目录，脚本不碰用户既有存档（写入前有 Assert 拦截）。
#
# 用法：
#   pwsh -File tools/make-test-copy.ps1                     # 默认全套
#   pwsh -File tools/make-test-copy.ps1 -World 踽踽独行      # 换一张图
#   pwsh -File tools/make-test-copy.ps1 -SkipGameCopy        # 游戏副本已存在时只更新存档/插件/计划

[CmdletBinding()]
param(
    [string]$World = '草剑挥打',
    [string]$Root = 'D:\zhadai-test',
    [string]$Terraria = 'D:\Program Files (x86)\Steam\steamapps\common\Terraria',
    [string]$Hotkey = 'F10',
    [int]$MaxDeaths = 50,
    [int]$QuickCharges = 40,
    [switch]$SkipGameCopy,

    # 存档副本已存在时不要覆盖：你在副本里玩过的进度不该被我刷掉。
    [switch]$SkipSaveCopy
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$gameCopy = Join-Path $Root 'game'
$saveCopy = Join-Path $Root 'save'
$patcher = Join-Path $repo 'src\ZhaDai.Patcher\bin\Release\net48\ZhaDai.Patcher.exe'
$pluginDll = Join-Path $repo 'src\ZhaDai.Runtime\bin\Release\net48\ZhaDai.Runtime.dll'
$cli = Join-Path $repo 'src\ZhaDai.Cli\bin\Release\net8.0\zhaodai.dll'
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$originalSave = Join-Path $env:USERPROFILE 'Documents\My Games\Terraria'

function Say([string]$message, [string]$color = 'Gray') { Write-Host $message -ForegroundColor $color }

function Assert-InsideRoot([string]$path) {
    $full = [System.IO.Path]::GetFullPath($path)
    $guard = [System.IO.Path]::GetFullPath($Root)
    if (-not $full.StartsWith($guard, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝写到测试目录之外：$full"
    }

    $original = [System.IO.Path]::GetFullPath($originalSave)
    if ($full.StartsWith($original, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝写用户既有存档：$full"
    }
}

Say "造测试副本到 $Root" 'White'
foreach ($tool in @($patcher, $pluginDll, $cli)) {
    if (-not (Test-Path -LiteralPath $tool)) { throw "缺构建产物：$tool" }
}

Assert-InsideRoot (Join-Path $gameCopy 'x')
New-Item -ItemType Directory -Path $Root -Force | Out-Null

# 1) 游戏副本
if (-not $SkipGameCopy) {
    Say ""
    Say "[1/5] 复制游戏本体（约 800MB，只复制一次）" 'White'
    if (Test-Path -LiteralPath $gameCopy) {
        Say "  已存在，跳过（要重造就先删掉 $gameCopy）" 'Yellow'
    }
    else {
        # 只跳过真正没用的东西；Content 必须带着，不然贴图/音效全缺。
        $skip = @('Logs', 'ZhaDai')
        & robocopy $Terraria $gameCopy /E /NFL /NDL /NJH /NJS /NP /XF "*.tmp" /XD ($skip | ForEach-Object { Join-Path $Terraria $_ }) | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy 失败，退出码 $LASTEXITCODE" }
        Say "  完成：$([Math]::Round(((Get-ChildItem $gameCopy -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 0)) MB" 'DarkGray'
    }
}
else {
    Say ""
    Say "[1/5] 跳过游戏本体复制（-SkipGameCopy）" 'White'
}

$copyExe = Join-Path $gameCopy 'Terraria.exe'
if (-not (Test-Path -LiteralPath $copyExe)) { throw "副本里没有 Terraria.exe：$copyExe" }

# 2) 存档副本
Say ""
if ($SkipSaveCopy) {
    Say "[2/5] 跳过存档复制（-SkipSaveCopy），沿用副本里现有存档" 'White'
} else {
    Say "[2/5] 复制存档到 $saveCopy" 'White'
    Assert-InsideRoot $saveCopy
    if (Test-Path -LiteralPath $saveCopy) { Remove-Item -LiteralPath $saveCopy -Recurse -Force }
    New-Item -ItemType Directory -Path $saveCopy -Force | Out-Null
    foreach ($name in @('Worlds', 'Players')) {
        $from = Join-Path $originalSave $name
        if (Test-Path -LiteralPath $from) {
            Copy-Item -LiteralPath $from -Destination (Join-Path $saveCopy $name) -Recurse -Force
        }
    }
}

foreach ($name in @('config.json', 'input profiles.json', 'favorites.json')) {
    $from = Join-Path $originalSave $name
    if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from -Destination (Join-Path $saveCopy $name) -Force }
}

Say "  完成：$([Math]::Round(((Get-ChildItem $saveCopy -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 0)) MB" 'DarkGray'

# 3) 插件装进副本（副本游戏本来就不是原版，不需要还原）
Say ""
Say "[3/5] 把插件装进副本的 Terraria.exe" 'White'
& $patcher verify --terraria $copyExe --plugin $pluginDll | Out-Null
if ($LASTEXITCODE -ne 0) { throw "副本核对注入点失败" }
$status = & $patcher status --terraria $copyExe
if ($status -match '已注入') {
    Say "  副本已经是注入状态，先还原再装" 'DarkGray'
    & $patcher restore --terraria $copyExe | Out-Null
}

& $patcher install --terraria $copyExe --plugin $pluginDll | Out-Null
if ($LASTEXITCODE -ne 0) { throw "安装插件到副本失败" }
Say "  完成：副本哈希 $((Get-FileHash -LiteralPath $copyExe -Algorithm SHA256).Hash.Substring(0,16))…" 'DarkGray'

# 4) 施工文件（整图 + 小样本）
Say ""
Say "[4/5] 为副本世界算施工文件" 'White'
$copyWorld = Join-Path $saveCopy ("Worlds\$World.wld")
if (-not (Test-Path -LiteralPath $copyWorld)) { throw "副本里没有世界 $World" }

$runDir = Join-Path $gameCopy 'ZhaDai'
Assert-InsideRoot (Join-Path $runDir 'plan.zplan')
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$fullPlan = Join-Path $runDir 'plan.zplan'
& $dotnet $cli plan $copyWorld "--zplan=$fullPlan" | Select-String -Pattern '雷管：|封堵|镐子分担|复核' | ForEach-Object { Say "  $($_.Line)" }
if ($LASTEXITCODE -ne 0) { throw "规划失败" }

# 小样本：只留 N 发雷管，去掉封堵和改挖，用来看动作对不对，不指望它真把图封住。
# 按「离出生点近」挑，而不是按规划顺序挑：整图的第一发可能在两千格以外，真机上按了键要
# 走十分钟才看到第一个动作，没法判断接管到底对不对。
$lines = [System.IO.File]::ReadAllLines($fullPlan)
$spawnX = 0; $spawnY = 0
foreach ($line in $lines) {
    if ($line -match '^spawn=(\d+)\s+(\d+)') { $spawnX = [int]$Matches[1]; $spawnY = [int]$Matches[2]; break }
}

$allCharges = New-Object System.Collections.Generic.List[string]
foreach ($line in $lines) {
    if ($line.StartsWith('#CHARGE')) { $allCharges.Add($line) }
}

$near = $allCharges | Sort-Object {
    # #CHARGE <序号> <x> <y> sec=… stand=… retreat=… haz=…
    if ($_ -match '^#CHARGE\s+\d+\s+(-?\d+)\s+(-?\d+)') {
        [Math]::Max([Math]::Abs([int]$Matches[1] - $spawnX), [Math]::Abs([int]$Matches[2] - $spawnY))
    } else { 999999 }
} | Select-Object -First $QuickCharges

$quick = New-Object System.Collections.Generic.List[string]
$charges = 0
$kept = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($charge in $near) { [void]$kept.Add($charge); $charges++ }
foreach ($line in $lines) {
    if ($line.StartsWith('#CHARGE')) {
        if ($kept.Contains($line)) { $quick.Add($line) }
        continue
    }

    if ($line.StartsWith('#PLUG') -or $line.StartsWith('#DIG')) { continue }
    $quick.Add($line)
}

[System.IO.File]::WriteAllLines((Join-Path $runDir 'plan-quick.zplan'), $quick, (New-Object System.Text.UTF8Encoding($false)))
Say "  小样本：$charges 发雷管 -> plan-quick.zplan；整图 -> plan.zplan" 'DarkGray'

# 5) 按键接管配置
Say ""
Say "[5/5] 写 run.cfg（进世界后按 $Hotkey 接管）" 'White'
$cfg = @(
    # 括号不能省：@('a' + $x, 'b') 会被 PowerShell 读成 'a' + ($x, 'b')，数组被拼成一行。
    ('# 进世界后按 ' + $Hotkey + ' 开始接管，再按一次停止；死亡后自动继续。'),
    'enabled=0',
    'plan=plan-quick.zplan',
    'allowexplosives=1',
    ('maxdeaths=' + $MaxDeaths),
    'hostiledistance=14',
    ('hotkey=' + $Hotkey)
)
# 最后一道保险：拼接写错时数组会多出半截元素，这里直接按行数核对，宁可在生成时炸掉，
# 也不要在游戏里读到一个残缺的配置。
if ($cfg.Count -ne 7) { throw "run.cfg 生成出 $($cfg.Count) 行，应该 7 行：$($cfg -join ' | ')" }
$cfgPath = Join-Path $runDir 'run.cfg'
Assert-InsideRoot $cfgPath
[System.IO.File]::WriteAllLines($cfgPath, $cfg, (New-Object System.Text.UTF8Encoding($true)))
Say "  $cfgPath" 'DarkGray'

# Steam 的 appid 文件 + 一个双击就能用的启动器。副本不在 Steam 目录里，缺了 appid 文件游戏会一闪而过；
# 工作目录必须是副本目录，否则资源路径会找错。
Set-Content -LiteralPath (Join-Path $gameCopy 'steam_appid.txt') -Value '105600' -Encoding ASCII
$launcher = @"
@echo off
cd /d "%~dp0game"
start "" "%~dp0game\Terraria.exe" -savedirectory "%~dp0save"
"@
$launcherPath = Join-Path $Root 'start-test.cmd'
Assert-InsideRoot $launcherPath
[System.IO.File]::WriteAllText($launcherPath, $launcher, (New-Object System.Text.ASCIIEncoding))
$readme = @"
# 炸带 测试副本

- 游戏副本：$copyExe
- 存档副本：$saveCopy（Worlds / Players 都是复制品，随便炸）
- 施工文件：$fullPlan（整图）、$(Join-Path $runDir 'plan-quick.zplan')（$QuickCharges 发小样本）
- 运行日志：$(Join-Path $runDir 'runtime.log')
- 状态文件：$(Join-Path $runDir 'status.txt')

## 怎么测

1. 启动副本：双击 `start-test.cmd`（在 $Root 下）。它做的事就是
   `cd "$gameCopy"` 然后 `"$copyExe" -savedirectory "$saveCopy"`。
   注意两点：工作目录必须是副本目录、目录里要有 steam_appid.txt（脚本已经放好），否则副本会一闪而过。
   Steam 客户端保持开着即可。
2. 单人游戏 -> 选角色 -> 选世界 **$World** -> 进入世界。
3. **进世界前先看背包**：接管前会盘点，缺东西就直接拒绝接管（日志里写清楚缺多少）。
   小样本（$QuickCharges 发）需要：雷管 ≥ $($QuickCharges + 5) 个、一把镐力 ≥ 65% 的镐子、木头（物块 id 9）≥ 10 个。
   整图计划需要雷管 ≥ 2151 个、木头 ≥ 264 个；想一次跑完就带足两组雷管。
4. 进世界后按 **$Hotkey**：
   - runtime.log 里会出现「按下 $Hotkey：开始接管，共 N 发雷管」；
   - status.txt 每 60 帧刷新一次，能看到状态机、第几发、死亡次数、寻路说明。
5. 再按一次 **$Hotkey** 停止接管（当场停手，不会继续丢雷管）。
6. 想跑整图：把 run.cfg 里的 plan 改成 plan.zplan（可以热改，插件每秒重读一次）。

## 行为约定

- 有敌怪/坠落/溺水/岩浆/陷阱规避，雷管自伤靠撤退距离规避；死亡后等复活继续，最多 $MaxDeaths 次。
- 接管前会盘点：雷管、镐力、封堵方块不够就直接拒绝接管，日志里会写清楚缺多少。
- 本副本是复制品，游戏本体和存档都不是原版；原版 Steam 安装和你的存档全程没动。
"@
$readmePath = Join-Path $Root 'README-测试.md'
Assert-InsideRoot $readmePath
[System.IO.File]::WriteAllText($readmePath, $readme, (New-Object System.Text.UTF8Encoding($true)))

Say ""
Say "完成。怎么玩：" 'Green'
Say "  1. 运行：`"$copyExe`" -savedirectory `"$saveCopy`"" 'White'
Say "  2. 单人游戏 -> $World -> 进世界后按 $Hotkey" 'White'
Say "  3. 日志：$(Join-Path $runDir 'runtime.log')  状态：$(Join-Path $runDir 'status.txt')" 'White'
Say "  4. 说明写在 $readmePath" 'White'
