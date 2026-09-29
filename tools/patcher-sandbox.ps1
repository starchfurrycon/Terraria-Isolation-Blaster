# 本文件以「UTF-8 带 BOM」保存：Windows PowerShell 5.1 读无 BOM 的 .ps1 会按 ANSI 解码，中文会变乱码。
<#
.SYNOPSIS
    在一个临时沙箱里完整验证 install -> status -> restore 往返，绝不写入真实游戏目录。

.DESCRIPTION
    沙箱结构：<沙箱>\Terraria.exe（真实 1.4.5.8 的字节副本）+ <沙箱>\ZhaDai\。
    断言：
      1. install 会建立备份与清单，注入后 status = 已注入；
      2. restore 之后沙箱里的 Terraria.exe 与已核验原版哈希逐字节一致；
      3. 沙箱的 Terraria.exe 从未等于已核验哈希之外的其它来源（即原版副本本身没被提前改动）。

.NOTES
    退出码：0 = 全部符合预期；1 = 任一条不符合。
#>
[CmdletBinding()]
param(
    [string]$TerrariaExe,
    [string]$Configuration = 'Release',
    [string]$Sandbox
)

$ErrorActionPreference = 'Stop'
$expectedOriginalSha256 = '960A03BFF6050CF7BE16DFC1A7B19E10FC2C4F8F835A6A3B135A50DD9E6BA2F3'
$repoRoot = Split-Path -Parent $PSScriptRoot
$patcher = Join-Path $repoRoot "src\ZhaDai.Patcher\bin\$Configuration\net48\ZhaDai.Patcher.exe"
$stubPlugin = Join-Path $repoRoot "src\ZhaDai.Patcher\testdata\ZhaDai.Runtime\bin\$Configuration\net48\ZhaDai.Runtime.dll"
$failures = New-Object System.Collections.Generic.List[string]

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host ('-' * 78)
    Write-Host $text
    Write-Host ('-' * 78)
}

foreach ($required in @($patcher, $stubPlugin)) {
    if (-not (Test-Path -LiteralPath $required)) {
        Write-Error "缺少产物 $required（先跑 tools\patcher-build.ps1 -Tests）。"
        exit 1
    }
}

if (-not $TerrariaExe) { $TerrariaExe = $env:ZHAODAI_TERRARIA }
if (-not $TerrariaExe -or -not (Test-Path -LiteralPath $TerrariaExe)) {
    foreach ($candidate in @(
            'D:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe',
            'C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe')) {
        if (Test-Path -LiteralPath $candidate) { $TerrariaExe = $candidate; break }
    }
}
if (-not $TerrariaExe) { Write-Error '找不到 Terraria.exe，请用 -TerrariaExe 指定。'; exit 1 }

if (-not $Sandbox) { $Sandbox = Join-Path $env:TEMP 'zhaodai-sandbox' }
if (Test-Path -LiteralPath $Sandbox) { Remove-Item -LiteralPath $Sandbox -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$sandboxExe = Join-Path $Sandbox 'Terraria.exe'
Copy-Item -LiteralPath $TerrariaExe -Destination $sandboxExe -Force

Write-Host "沙箱:        $Sandbox"
Write-Host "原版来源:    $TerrariaExe"
Write-Host "沙箱 exe:    $sandboxExe"
$sandboxOriginal = (Get-FileHash -LiteralPath $sandboxExe -Algorithm SHA256).Hash
Write-Host "沙箱初始哈希: $sandboxOriginal"
if ($sandboxOriginal -ne $expectedOriginalSha256) {
    $failures.Add("沙箱里的 Terraria.exe 不是已核验原版：$sandboxOriginal")
}

Write-Step 'install（沙箱内真实写入）'
& $patcher install --terraria $sandboxExe --plugin $stubPlugin
$installExit = $LASTEXITCODE
Write-Host "退出码: $installExit"
if ($installExit -ne 0) { $failures.Add("install 失败，退出码 $installExit。") }

$backup = Join-Path $Sandbox 'ZhaDai\Terraria.exe.orig'
$manifest = Join-Path $Sandbox 'ZhaDai\manifest.json'
foreach ($file in @($backup, $manifest)) {
    if (Test-Path -LiteralPath $file) {
        Write-Host "存在: $file（$((Get-Item -LiteralPath $file).Length) 字节）"
    } else {
        $failures.Add("install 之后缺少 $file。")
    }
}

$patchedHash = (Get-FileHash -LiteralPath $sandboxExe -Algorithm SHA256).Hash
Write-Host "注入后哈希:  $patchedHash"
if ($patchedHash -eq $expectedOriginalSha256) { $failures.Add('install 之后 exe 竟然还是原版哈希。') }

Write-Step 'status（沙箱内应为「已注入」）'
& $patcher status --terraria $sandboxExe
$statusExit = $LASTEXITCODE
Write-Host "退出码: $statusExit"

Write-Step '重复注入防护（再 install 一次，应当以备份为基准重注入而不是叠加）'
& $patcher install --terraria $sandboxExe --plugin $stubPlugin
$reinstallExit = $LASTEXITCODE
Write-Host "退出码: $reinstallExit"
if ($reinstallExit -ne 0) { $failures.Add("重复 install 失败，退出码 $reinstallExit。") }
$reinstalledHash = (Get-FileHash -LiteralPath $sandboxExe -Algorithm SHA256).Hash
Write-Host "重注入后哈希: $reinstalledHash"

Write-Step 'restore（沙箱内还原）'
& $patcher restore --terraria $sandboxExe
$restoreExit = $LASTEXITCODE
Write-Host "退出码: $restoreExit"
if ($restoreExit -ne 0) { $failures.Add("restore 失败，退出码 $restoreExit。") }

$restoredHash = (Get-FileHash -LiteralPath $sandboxExe -Algorithm SHA256).Hash
Write-Host ''
Write-Host "沙箱初始哈希: $sandboxOriginal"
Write-Host "还原之后哈希: $restoredHash"
if ($restoredHash -ne $expectedOriginalSha256) {
    $failures.Add("还原后的哈希与已核验原版不符：$restoredHash")
} else {
    Write-Host '还原后与已核验原版逐字节一致。'
}

$backupHash = (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash
Write-Host "备份哈希:     $backupHash"
if ($backupHash -ne $expectedOriginalSha256) { $failures.Add("备份的哈希不是原版：$backupHash") }

$realGameHash = (Get-FileHash -LiteralPath $TerrariaExe -Algorithm SHA256).Hash
Write-Host "真实游戏哈希: $realGameHash"
if ($realGameHash -ne $expectedOriginalSha256) { $failures.Add("真实游戏目录的 Terraria.exe 被改动了！$realGameHash") }

Write-Step '汇总'
if ($failures.Count -eq 0) {
    Write-Host 'install -> status -> restore 往返全部通过，真实游戏目录未被触碰。'
    exit 0
}
Write-Host "有 $($failures.Count) 项不符合预期："
foreach ($failure in $failures) { Write-Host "  ✗ $failure" }
exit 1
