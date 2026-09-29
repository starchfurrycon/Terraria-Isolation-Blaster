# 真机检验脚手板（ZhaDai live check）
#
# 目的：在**不动用户既有存档**的前提下，把插件装进真实泰拉瑞亚、让游戏用一份存档副本跑一次，
# 然后只读 ZhaDai\runtime.log 与 ZhaDai\status.txt 收集证据，最后把游戏还原成原版。
#
# 安全约定（写进代码，不靠记性）：
#   1. 存档副本：把 %USERPROFILE%\Documents\My Games\Terraria 整个复制到独立目录，游戏用
#      -savedirectory 指向副本。原目录只读，命令里出现的每一个写入路径都要先经过 Assert-OutsideOriginalSave。
#   2. 二进制：注入前先算 SHA256 记下来，结束（包括异常）一律 restore 并核对哈希。
#   3. 不抢桌面：启动后用 Win32 ShowWindow 收成最小化；插件瞄准靠 Main.mouseX/mouseY 字段与
#      Player.control* 字段，不需要真实鼠标键盘。
#   4. 只读证据：runtime.log / status.txt 只读，不写。
#
# 用法：
#   pwsh -File tools/live-check.ps1 -DryRun                    # 只打印计划，什么都不做
#   pwsh -File tools/live-check.ps1 -Stage plugin              # 装插件 + 启动 + 看反射自检 + 还原
#   pwsh -File tools/live-check.ps1 -Stage takeover            # 再额外写 run.cfg 驱动接管
#   pwsh -File tools/live-check.ps1 -Stage plugin -KeepInstall # 保留注入（调试用，务必手动还原）

[CmdletBinding()]
param(
    [ValidateSet('plugin', 'takeover')]
    [string]$Stage = 'plugin',

    [string]$World = '草剑挥打',

    # 接管跑多久就收工（秒）。真机检验不需要炸完整张图。
    [int]$TakeoverSeconds = 180,

    # 施工文件里最多保留多少发雷管；真机检验用小样本，不整图开炸。
    [int]$MaxCharges = 40,

    [switch]$DryRun,
    [switch]$KeepInstall,
    [string]$Terraria = 'D:\Program Files (x86)\Steam\steamapps\common\Terraria',
    [string]$WorkRoot = (Join-Path $env:TEMP 'zhadai-live')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $Terraria 'Terraria.exe'
$patcher = Join-Path $root 'src\ZhaDai.Patcher\bin\Release\net48\ZhaDai.Patcher.exe'
$pluginDll = Join-Path $root 'src\ZhaDai.Runtime\bin\Release\net48\ZhaDai.Runtime.dll'
$cli = Join-Path $root 'src\ZhaDai.Cli\bin\Release\net8.0\zhaodai.dll'
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$originalSave = Join-Path $env:USERPROFILE 'Documents\My Games\Terraria'
$originalHash = '960A03BFF6050CF7BE16DFC1A7B19E10FC2C4F8F835A6A3B135A50DD9E6BA2F3'

$saveCopy = Join-Path $WorkRoot 'save'
$runDir = Join-Path $Terraria 'ZhaDai'
$runtimeLog = Join-Path $runDir 'runtime.log'
$statusFile = Join-Path $runDir 'status.txt'
$configFile = Join-Path $runDir 'run.cfg'

function Say([string]$message, [string]$color = 'Gray') {
    Write-Host $message -ForegroundColor $color
}

function Assert-OutsideOriginalSave([string]$path) {
    $full = [System.IO.Path]::GetFullPath($path)
    $guard = [System.IO.Path]::GetFullPath($originalSave)
    if ($full.StartsWith($guard, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝写入用户既有存档目录：$full"
    }
}

function Invoke-Step([string]$name, [scriptblock]$body) {
    Say "  $name ..." 'DarkGray'
    $result = & $body
    if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) {
        throw "$name 失败（退出码 $LASTEXITCODE）"
    }

    return $result
}

function Copy-SaveTree([string]$target) {
    # 只复制存档真正需要的东西；tModLoader 那棵树和录像没必要搬。
    Assert-OutsideOriginalSave $target
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }

    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($name in @('Worlds', 'Players')) {
        $from = Join-Path $originalSave $name
        if (-not (Test-Path -LiteralPath $from)) {
            continue
        }

        Copy-Item -LiteralPath $from -Destination (Join-Path $target $name) -Recurse -Force
    }

    # 配置文件只复制纯文本的；其余交给游戏自己重建。
    foreach ($name in @('config.json', 'input profiles.json', 'favorites.json')) {
        $from = Join-Path $originalSave $name
        if (Test-Path -LiteralPath $from) {
            Copy-Item -LiteralPath $from -Destination (Join-Path $target $name) -Force
        }
    }
}

function Minimize-GameWindow([System.Diagnostics.Process]$process) {
    # 不抢桌面：窗口一出现就收起来。XNA 在窗口非激活时仍然会 Update（InactiveSleepTime 只是降频），
    # 所以最小化不会让世界停摆；这一点由 status.txt 的时间戳持续前进在下面直接验证。
    if (-not ('ZhaDai.Win32' -as [type])) {
        Add-Type -Namespace ZhaDai -Name Win32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
'@
    }

    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) {
            return $false
        }

        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            [ZhaDai.Win32]::ShowWindowAsync($process.MainWindowHandle, 6) | Out-Null  # SW_MINIMIZE
            return $true
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

function Wait-ForLog([string]$needle, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $runtimeLog) {
            $text = Get-Content -LiteralPath $runtimeLog -Raw -ErrorAction SilentlyContinue
            if ($text -and $text.Contains($needle)) {
                return $true
            }
        }

        Start-Sleep -Seconds 2
    }

    return $false
}

function Show-Evidence() {
    foreach ($path in @($runtimeLog, $statusFile)) {
        Say ""
        Say "=== $path ===" 'Cyan'
        if (-not (Test-Path -LiteralPath $path)) {
            Say "（还没有这个文件）" 'Yellow'
            continue
        }

        Get-Content -LiteralPath $path -Tail 30 | ForEach-Object { Say "  $_" }
    }
}

# ---------------------------------------------------------------------------

Say "ZhaDai 真机检验（stage=$Stage，dry=$DryRun）" 'White'
Say ""
Say "游戏：    $exe"
Say "存档原目录：$originalSave（本次全程只读）"
Say "存档副本：  $saveCopy"
Say "运行目录：  $runDir（runtime.log / status.txt / run.cfg）"
Say "接管时长：  $TakeoverSeconds 秒，最多 $MaxCharges 发雷管"
Say ""

if (-not (Test-Path -LiteralPath $exe)) { throw "找不到 Terraria.exe：$exe" }
if (-not (Test-Path -LiteralPath $patcher)) { throw "找不到 patcher：$patcher（先 MSBuild 构建）" }
if (-not (Test-Path -LiteralPath $pluginDll)) { throw "找不到插件：$pluginDll" }
if (-not (Test-Path -LiteralPath $cli)) { throw "找不到 CLI：$cli" }

$hashBefore = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
Say "Terraria.exe SHA256（前）：$hashBefore"
if ($hashBefore -ne $originalHash) {
    Say "警告：当前 Terraria.exe 与记录的原版哈希不一致，可能已经注入过。先 restore 再跑。" 'Yellow'
}

if ($DryRun) {
    Say ""
    Say "干跑模式，只列出会执行的命令：" 'White'
    Say "  1. 复制存档 $originalSave -> $saveCopy"
    Say "  2. & `"$patcher`" verify --terraria `"$exe`" --plugin `"$pluginDll`""
    Say "  3. & `"$patcher`" install --terraria `"$exe`" --plugin `"$pluginDll`""
    Say "  4. Start-Process `"$exe`" -ArgumentList '-savedirectory','`"$saveCopy`"'"
    Say "  5. 最小化窗口，等 runtime.log 出现「反射自检通过」"
    if ($Stage -eq 'takeover') {
        Say "  6. & `"$dotnet`" `"$cli`" plan <副本世界> --zplan ZhaDai\plan.zplan"
        Say "  7. 写 ZhaDai\run.cfg（enabled=1, plan=plan.zplan, maxdeaths=...）"
        Say "  8. 只读 runtime.log / status.txt，$TakeoverSeconds 秒"
    }

    Say "  9. 关游戏 -> & patcher restore -> 核对哈希仍是 $originalHash"
    Say ""
    Say "干跑结束，什么都没做。" 'Green'
    exit 0
}

$installed = $false
$game = $null
try {
    Say ""
    Say "[1/6] 复制存档到独立目录" 'White'
    Invoke-Step '复制 Worlds/Players/配置' { Copy-SaveTree $saveCopy }
    Say "  副本大小：$([Math]::Round(((Get-ChildItem $saveCopy -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)) MB" 'DarkGray'

    Say ""
    Say "[2/6] 只读核对注入点" 'White'
    Invoke-Step 'patcher verify' { & $patcher verify --terraria $exe --plugin $pluginDll } | Out-Null
    Say "  核对通过" 'DarkGray'

    Say ""
    Say "[3/6] 安装插件（可逆注入）" 'White'
    Invoke-Step 'patcher install' { & $patcher install --terraria $exe --plugin $pluginDll } | Out-Null
    $installed = $true
    $hashPatched = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    Say "  注入后哈希：$hashPatched" 'DarkGray'

    Say ""
    Say "[4/6] 后台启动游戏（存档指向副本）" 'White'
    Invoke-Step 'patcher status' { & $patcher status --terraria $exe } | Out-Null
    $game = Start-Process -FilePath $exe -ArgumentList @('-savedirectory', $saveCopy) -PassThru
    Say "  进程 $($game.Id)，收窗口 ..." 'DarkGray'
    $ok = Minimize-GameWindow $game
    Say "  最小化：$(if ($ok) { '成功' } else { '没拿到窗口句柄（可能已退出）' })" 'DarkGray'

    Say ""
    Say "[5/6] 等插件在真实游戏里的反射自检" 'White'
    $selfCheck = Wait-ForLog '反射自检通过' 120
    if ($selfCheck) {
        Say "  runtime.log 里出现了「反射自检通过」——真实 Terraria.exe 里注入成功" 'Green'
    }
    else {
        Say "  没等到反射自检（120 秒）" 'Yellow'
    }

    if ($Stage -eq 'takeover' -and $selfCheck) {
        $copyWorld = Join-Path $saveCopy ("Worlds\" + $World + '.wld')
        if (-not (Test-Path -LiteralPath $copyWorld)) {
            Say "  副本里没有世界 $World，跳过接管" 'Yellow'
        }
        else {
            Say ""
            Say "[5b] 生成小样本施工文件" 'White'
            Assert-OutsideOriginalSave (Join-Path $runDir 'plan.zplan')
            New-Item -ItemType Directory -Path $runDir -Force | Out-Null
            & $dotnet $cli plan $copyWorld "--zplan=$(Join-Path $runDir 'plan.zplan')" | Select-String -Pattern '雷管：|封堵|复核' | ForEach-Object { Say "  $($_.Line)" }
            if ($LASTEXITCODE -ne 0) { throw "规划失败" }

            $cfg = @(
                'enabled=1',
                'plan=plan.zplan',
                'allowexplosives=1',
                'maxdeaths=3'
            )
            Assert-OutsideOriginalSave $configFile
            [System.IO.File]::WriteAllLines($configFile, $cfg, (New-Object System.Text.UTF8Encoding($false)))
            Say "  已写 run.cfg：$($cfg -join ' / ')" 'DarkGray'

            Say ""
            Say "[5c] 接管 $TakeoverSeconds 秒（只看日志，不碰键鼠）" 'White'
            $deadline = (Get-Date).AddSeconds($TakeoverSeconds)
            $ticks = @()
            while ((Get-Date) -lt $deadline) {
                if ($game.HasExited) { break }
                if (Test-Path -LiteralPath $statusFile) {
                    $ticks += (Get-Item -LiteralPath $statusFile).LastWriteTime
                }

                Start-Sleep -Seconds 5
            }

            if ($ticks.Count -ge 2) {
                Say "  status.txt 更新了 $($ticks.Count) 次，最后一次 $($ticks[-1].ToString('HH:mm:ss'))" 'Green'
            }
            else {
                Say "  status.txt 没有持续更新（窗口非激活时世界可能停摆，需要前台）" 'Yellow'
            }

            # 停手：把 enabled 关掉，插件下一轮 Poll 就会 Stop，不用杀进程。
            [System.IO.File]::WriteAllLines($configFile, @('enabled=0'), (New-Object System.Text.UTF8Encoding($false)))
            Start-Sleep -Seconds 3
        }
    }

    Say ""
    Say "[6/6] 收工：关游戏、还原二进制" 'White'
    if ($game -and -not $game.HasExited) {
        $game.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 5
        if (-not $game.HasExited) {
            Stop-Process -Id $game.Id -Force
        }

        Say "  游戏已退出" 'DarkGray'
    }

    if ($installed -and -not $KeepInstall) {
        Invoke-Step 'patcher restore' { & $patcher restore --terraria $exe } | Out-Null
        $installed = $false
    }

    Show-Evidence
}
finally {
    if ($game -and -not $game.HasExited) {
        Stop-Process -Id $game.Id -Force -ErrorAction SilentlyContinue
    }

    if ($installed -and -not $KeepInstall) {
        Say "异常退出，仍在还原二进制 ..." 'Yellow'
        & $patcher restore --terraria $exe 2>&1 | Out-Null
    }

    $hashAfter = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    Say ""
    Say "Terraria.exe SHA256（后）：$hashAfter"
    if ($hashAfter -eq $originalHash) {
        Say "二进制已还原为原版 ✓" 'Green'
    }
    else {
        Say "二进制与记录的原版哈希不一致！手动跑 restore。注意：哈希也会随 Steam 更新变化。" 'Red'
    }

    Say ""
    Say "存档原目录时间戳（应当没被碰过）：" 'White'
    Get-ChildItem -LiteralPath $originalSave -Directory | ForEach-Object {
        Say ("  {0,-16} {1}" -f $_.Name, $_.LastWriteTime)
    }
}
