# 本文件以「UTF-8 带 BOM」保存：Windows PowerShell 5.1 读无 BOM 的 .ps1 会按 ANSI 解码，中文会变乱码。
<#
.SYNOPSIS
    ZhaDai.Patcher 的离线证据链：verify（缺插件必须失败 / 有替身插件必须通过）
    -> patch-copy 注入副本 -> 回读校验 -> status -> install --dry-run
    -> 真实 install 必须被拒绝（游戏在跑）。

.DESCRIPTION
    全程不启动游戏、不写入游戏目录。最后会核对原始 Terraria.exe 的 SHA-256 是否仍然等于
    960A03BFF6050CF7BE16DFC1A7B19E10FC2C4F8F835A6A3B135A50DD9E6BA2F3。

.NOTES
    退出码：0 = 全部符合预期；1 = 任一步不符合预期。
#>
[CmdletBinding()]
param(
    [string]$TerrariaExe,
    [string]$Configuration = 'Release',
    [string]$WorkDirectory,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$script:ExpectedOriginalSha256 = '960A03BFF6050CF7BE16DFC1A7B19E10FC2C4F8F835A6A3B135A50DD9E6BA2F3'
$repoRoot = Split-Path -Parent $PSScriptRoot
$script:Patcher = Join-Path $repoRoot "src\ZhaDai.Patcher\bin\$Configuration\net48\ZhaDai.Patcher.exe"
$script:StubPlugin = Join-Path $repoRoot "src\ZhaDai.Patcher\testdata\ZhaDai.Runtime\bin\$Configuration\net48\ZhaDai.Runtime.dll"
$script:Failures = New-Object System.Collections.Generic.List[string]

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host ('=' * 78)
    Write-Host $text
    Write-Host ('=' * 78)
}

# 原生程序往 stderr 写东西时，PowerShell 5.1 在 $ErrorActionPreference='Stop' 下会把它当成
# 终止性错误——那会让「故意让它失败」的步骤直接中断整个脚本。这里统一包一层。
function Invoke-Patcher([string[]]$PatcherArgs) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = & $script:Patcher @PatcherArgs 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    foreach ($line in $lines) { Write-Host ([string]$line) }
    return $exitCode
}

function Find-TerrariaExe([string]$explicit) {
    if ($explicit -and (Test-Path -LiteralPath $explicit)) { return (Get-Item -LiteralPath $explicit).FullName }
    if ($env:ZHAODAI_TERRARIA -and (Test-Path -LiteralPath $env:ZHAODAI_TERRARIA)) {
        return (Get-Item -LiteralPath $env:ZHAODAI_TERRARIA).FullName
    }
    foreach ($candidate in @(
            'D:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe',
            'C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe',
            'D:\SteamLibrary\steamapps\common\Terraria\Terraria.exe')) {
        if (Test-Path -LiteralPath $candidate) { return (Get-Item -LiteralPath $candidate).FullName }
    }
    return $null
}

if (-not $SkipBuild) {
    Write-Step '1/9 构建 ZhaDai.Patcher 与测试替身插件'
    & (Join-Path $PSScriptRoot 'patcher-build.ps1') -Configuration $Configuration -Tests
    if ($LASTEXITCODE -ne 0) { Write-Error '构建失败。'; exit 1 }
}

foreach ($required in @($script:Patcher, $script:StubPlugin)) {
    if (-not (Test-Path -LiteralPath $required)) {
        Write-Error "缺少产物 $required（先跑 tools\patcher-build.ps1 -Tests）。"
        exit 1
    }
}

$terraria = Find-TerrariaExe $TerrariaExe
if (-not $terraria) { Write-Error '找不到 Terraria.exe，请用 -TerrariaExe 指定。'; exit 1 }

if (-not $WorkDirectory) { $WorkDirectory = Join-Path $env:TEMP 'zhaodai-evidence' }
New-Item -ItemType Directory -Force -Path $WorkDirectory | Out-Null
$copy = Join-Path $WorkDirectory 'Terraria.zhaodai-copy.exe'

Write-Host "Terraria.exe : $terraria"
Write-Host "Patcher      : $script:Patcher"
Write-Host "替身插件     : $script:StubPlugin"
Write-Host "副本输出     : $copy"

$sizeBefore = (Get-Item -LiteralPath $terraria).Length
$originalBefore = (Get-FileHash -LiteralPath $terraria -Algorithm SHA256).Hash

Write-Step '2/9 verify（不带 --plugin：必须明确报「插件缺失」并以非 0 退出）'
$verifyWithoutExit = Invoke-Patcher @('verify', '--terraria', $terraria)
Write-Host "退出码: $verifyWithoutExit"
if ($verifyWithoutExit -eq 0) { $script:Failures.Add('verify 在没有插件时不该成功。') }

Write-Step '3/9 verify（带替身插件：必须全部通过，退出码 0）'
$verifyWithExit = Invoke-Patcher @('verify', '--terraria', $terraria, '--plugin', $script:StubPlugin)
Write-Host "退出码: $verifyWithExit"
if ($verifyWithExit -ne 0) { $script:Failures.Add("verify 带插件时应当成功，实际退出码 $verifyWithExit。") }

Write-Step '4/9 patch-copy（注入副本，绝不碰游戏本体）'
if (Test-Path -LiteralPath $copy) { Remove-Item -LiteralPath $copy -Force }
$patchExit = Invoke-Patcher @('patch-copy', '--terraria', $terraria, '--out', $copy, '--plugin', $script:StubPlugin)
Write-Host "退出码: $patchExit"
if ($patchExit -ne 0) { $script:Failures.Add("patch-copy 应当成功，实际退出码 $patchExit。") }

Write-Step '5/9 证明副本真的被改了：大小与 SHA-256 对比'
$originalAfter = (Get-FileHash -LiteralPath $terraria -Algorithm SHA256).Hash
$sizeAfter = (Get-Item -LiteralPath $terraria).Length
$copySize = (Get-Item -LiteralPath $copy).Length
$copyHash = (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash

"{0,-22}{1,12}  {2}" -f '原版 Terraria.exe', $sizeBefore, $originalBefore | Write-Host
"{0,-22}{1,12}  {2}" -f '注入副本 copy.exe', $copySize, $copyHash | Write-Host

if ($originalBefore -ne $originalAfter) { $script:Failures.Add('原始 Terraria.exe 的哈希发生了变化！') }
if ($originalAfter -ne $script:ExpectedOriginalSha256) {
    $script:Failures.Add("原始 Terraria.exe 的哈希不是已核验值：$originalAfter")
}
if ($copyHash -eq $originalBefore) { $script:Failures.Add('注入副本与原版哈希相同，说明没有真正改动。') }
if ($copySize -eq $sizeBefore) { $script:Failures.Add('注入副本大小与原版完全相同，可疑。') }

Write-Step '6/9 patch-copy --verify-only（重新打开副本，确认钩子调用真的在里面）'
$verifyOnlyExit = Invoke-Patcher @('patch-copy', '--out', $copy, '--verify-only')
Write-Host "退出码: $verifyOnlyExit"
if ($verifyOnlyExit -ne 0) { $script:Failures.Add("--verify-only 应当成功，实际退出码 $verifyOnlyExit。") }

Write-Step '7/9 status（游戏本体必须是「原版（受支持）」）'
$statusExit = Invoke-Patcher @('status', '--terraria', $terraria)
Write-Host "退出码: $statusExit"

$gameDataDirectory = Join-Path (Split-Path -Parent $terraria) 'ZhaDai'
if (Test-Path -LiteralPath $gameDataDirectory) {
    $script:Failures.Add("游戏目录下已经存在 $gameDataDirectory，本脚本不允许在真实游戏目录里写入。")
}

Write-Step '8/9 install --dry-run（完整走一遍，但不写任何文件）'
$dryRunExit = Invoke-Patcher @('install', '--terraria', $terraria, '--plugin', $script:StubPlugin, '--dry-run')
Write-Host "退出码: $dryRunExit"
if ($dryRunExit -ne 0) { $script:Failures.Add("install --dry-run 应当成功，实际退出码 $dryRunExit。") }

if (Test-Path -LiteralPath $gameDataDirectory) {
    $script:Failures.Add("install --dry-run 竟然创建了 $gameDataDirectory。")
} else {
    Write-Host '已确认游戏目录下没有创建 ZhaDai\（演练没有落盘）。'
}

Write-Step '9/9 真实 install 必须被拒绝（游戏正在运行 / 未加 --dry-run）'
$realInstallExit = Invoke-Patcher @('install', '--terraria', $terraria, '--plugin', $script:StubPlugin)
Write-Host "退出码: $realInstallExit"
if ($realInstallExit -eq 0) {
    $script:Failures.Add('真实 install 竟然成功了——这是绝不允许的（本脚本不得修改游戏目录）。')
} elseif (-not (Test-Path -LiteralPath $gameDataDirectory)) {
    Write-Host '已确认真安装被拒绝，且游戏目录下仍然没有 ZhaDai\。'
}

$finalOriginal = (Get-FileHash -LiteralPath $terraria -Algorithm SHA256).Hash
if ($finalOriginal -ne $script:ExpectedOriginalSha256) {
    $script:Failures.Add("收尾核对失败：原始 Terraria.exe 哈希为 $finalOriginal")
}

Write-Step '汇总'
Write-Host "原始 Terraria.exe  大小 $sizeBefore -> $sizeAfter；SHA-256 $originalBefore -> $finalOriginal"
Write-Host "注入副本           大小 $copySize；SHA-256 $copyHash"
if ($script:Failures.Count -eq 0) {
    Write-Host '全部证据链符合预期：原版未被改动，副本确实被注入。'
    exit 0
}
Write-Host "有 $($script:Failures.Count) 项不符合预期："
foreach ($failure in $script:Failures) { Write-Host "  x $failure" }
exit 1
