# 本文件以「UTF-8 带 BOM」保存：Windows PowerShell 5.1 读无 BOM 的 .ps1 会按 ANSI 解码，中文会变乱码。
<#
.SYNOPSIS
    ZhaDai.Patcher 的离线证据链：verify（缺插件必须失败 / 有替身插件必须通过）
    -> patch-copy 注入副本 -> 回读校验 -> status -> install --dry-run。

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
$expectedOriginalSha256 = '960A03BFF6050CF7BE16DFC1A7B19E10FC2C4F8F835A6A3B135A50DD9E6BA2F3'
$repoRoot = Split-Path -Parent $PSScriptRoot
$patcher = Join-Path $repoRoot "src\ZhaDai.Patcher\bin\$Configuration\net48\ZhaDai.Patcher.exe"
$stubPlugin = Join-Path $repoRoot "src\ZhaDai.Patcher\testdata\ZhaDai.Runtime\bin\$Configuration\net48\ZhaDai.Runtime.dll"
$failures = New-Object System.Collections.Generic.List[string]

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host ('=' * 78)
    Write-Host $text
    Write-Host ('=' * 78)
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
    Write-Step '1/8 构建 ZhaDai.Patcher 与测试替身插件'
    & (Join-Path $PSScriptRoot 'patcher-build.ps1') -Configuration $Configuration -Tests
    if ($LASTEXITCODE -ne 0) { Write-Error '构建失败。'; exit 1 }
}

foreach ($required in @($patcher, $stubPlugin)) {
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
Write-Host "Patcher      : $patcher"
Write-Host "替身插件     : $stubPlugin"
Write-Host "副本输出     : $copy"

$originalBefore = (Get-FileHash -LiteralPath $terraria -Algorithm SHA256).Hash
$sizeBefore = (Get-Item -LiteralPath $terraria).Length

Write-Step '2/8 verify（不带 --plugin：必须明确报「插件缺失」并以非 0 退出）'
& $patcher verify --terraria $terraria
$verifyWithoutExit = $LASTEXITCODE
Write-Host "退出码: $verifyWithoutExit"
if ($verifyWithoutExit -eq 0) { $failures.Add('verify 在没有插件时不该成功。') }

Write-Step '3/8 verify（带替身插件：必须全部通过，退出码 0）'
& $patcher verify --terraria $terraria --plugin $stubPlugin
$verifyWithExit = $LASTEXITCODE
Write-Host "退出码: $verifyWithExit"
if ($verifyWithExit -ne 0) { $failures.Add("verify 带插件时应当成功，实际退出码 $verifyWithExit。") }

Write-Step '4/8 patch-copy（注入副本，绝不碰游戏本体）'
if (Test-Path -LiteralPath $copy) { Remove-Item -LiteralPath $copy -Force }
& $patcher patch-copy --terraria $terraria --out $copy --plugin $stubPlugin
$patchExit = $LASTEXITCODE
Write-Host "退出码: $patchExit"
if ($patchExit -ne 0) { $failures.Add("patch-copy 应当成功，实际退出码 $patchExit。") }

Write-Step '5/8 证明副本真的被改了：大小与 SHA-256 对比'
$originalAfter = (Get-FileHash -LiteralPath $terraria -Algorithm SHA256).Hash
$sizeAfter = (Get-Item -LiteralPath $terraria).Length
$copySize = (Get-Item -LiteralPath $copy).Length
$copyHash = (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash

"{0,-22}{1,12}  {2}" -f '原版 Terraria.exe', $sizeBefore, $originalBefore | Write-Host
"{0,-22}{1,12}  {2}" -f '注入副本 copy.exe', $copySize, $copyHash | Write-Host

if ($originalBefore -ne $originalAfter) { $failures.Add('原始 Terraria.exe 的哈希发生了变化！') }
if ($originalAfter -ne $expectedOriginalSha256) {
    $failures.Add("原始 Terraria.exe 的哈希不是已核验值：$originalAfter")
}
if ($copyHash -eq $originalBefore) { $failures.Add('注入副本与原版哈希相同，说明没有真正改动。') }
if ($copySize -le $sizeBefore) { $failures.Add('注入副本没有变大，可疑。') }

Write-Step '6/8 patch-copy --verify-only（重新打开副本，确认钩子调用真的在里面）'
& $patcher patch-copy --out $copy --verify-only
$verifyOnlyExit = $LASTEXITCODE
Write-Host "退出码: $verifyOnlyExit"
if ($verifyOnlyExit -ne 0) { $failures.Add("--verify-only 应当成功，实际退出码 $verifyOnlyExit。") }

Write-Step '7/8 status（游戏本体必须是「原版（受支持）」）'
& $patcher status --terraria $terraria
$statusExit = $LASTEXITCODE
Write-Host "退出码: $statusExit"

Write-Step '8/8 install --dry-run（完整走一遍，但不写任何文件）'
& $patcher install --terraria $terraria --plugin $stubPlugin --dry-run
$dryRunExit = $LASTEXITCODE
Write-Host "退出码: $dryRunExit"
if ($dryRunExit -ne 0) { $failures.Add("install --dry-run 应当成功，实际退出码 $dryRunExit。") }

$gameDataDirectory = Join-Path (Split-Path -Parent $terraria) 'ZhaDai'
if (Test-Path -LiteralPath $gameDataDirectory) {
    $failures.Add("install --dry-run 竟然创建了 $gameDataDirectory。")
} else {
    Write-Host "已确认游戏目录下没有创建 ZhaDai\（演练没有落盘）。"
}

$finalOriginal = (Get-FileHash -LiteralPath $terraria -Algorithm SHA256).Hash
if ($finalOriginal -ne $expectedOriginalSha256) {
    $failures.Add("收尾核对失败：原始 Terraria.exe 哈希为 $finalOriginal")
}

Write-Step '汇总'
Write-Host "原始 Terraria.exe  大小 $sizeBefore -> $sizeAfter；SHA-256 $originalBefore -> $finalOriginal"
Write-Host "注入副本           大小 $copySize；SHA-256 $copyHash"
if ($failures.Count -eq 0) {
    Write-Host '全部证据链符合预期：原版未被改动，副本确实被注入。'
    exit 0
}
Write-Host "有 $($failures.Count) 项不符合预期："
foreach ($failure in $failures) { Write-Host "  ✗ $failure" }
exit 1
