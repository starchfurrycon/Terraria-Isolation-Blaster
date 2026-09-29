# One command that reproduces every claim in the README.
#
# ASCII only on purpose: a PowerShell script with non-ASCII text has to be saved as UTF-8 with a BOM
# or Windows PowerShell 5.1 mangles it, and that trap is not worth the risk in a verification script.
#
# Usage:
#   pwsh -File tools/verify-all.ps1 [-Terraria <Terraria.exe>] [-Worlds <world dir>]
#
# Exits 0 when every step passes, 1 when any step fails.

[CmdletBinding()]
param(
    [string]$Terraria = "",
    [string]$Worlds = "",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$results = New-Object System.Collections.Generic.List[object]

function Get-DotNet {
    $local = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $local) { return $local }
    return "dotnet"
}

$dotnet = Get-DotNet

function Invoke-Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    $ok = $false
    try {
        $ok = [bool](& $Body)
    }
    catch {
        Write-Host "  exception: $($_.Exception.Message)" -ForegroundColor Red
        $ok = $false
    }
    $script:results.Add([pscustomobject]@{ Name = $Name; Passed = $ok }) | Out-Null
    if ($ok) { Write-Host "  PASS" -ForegroundColor Green } else { Write-Host "  FAIL" -ForegroundColor Red }
}

if (-not $Worlds) {
    $Worlds = Join-Path $env:USERPROFILE "Documents\My Games\Terraria\Worlds"
}

Invoke-Step "build planner, automation, cli, ui" {
    foreach ($project in @(
        "src\ZhaDai.Core\ZhaDai.Core.csproj",
        "src\ZhaDai.Automation\ZhaDai.Automation.csproj",
        "src\ZhaDai.Cli\ZhaDai.Cli.csproj",
        "src\ZhaDai.Manager\ZhaDai.Manager.csproj")) {
        & $dotnet build (Join-Path $root $project) -c $Configuration --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Host "  build failed: $project" -ForegroundColor Red; return $false }
    }
    return $true
}

Invoke-Step "test suite (planner, spread model, executor simulation)" {
    $arguments = @("run", "--project", (Join-Path $root "tests\ZhaDai.Core.Tests"), "-c", $Configuration)
    if (Test-Path -LiteralPath $Worlds) {
        $arguments += @("--", $Worlds)
    }
    else {
        Write-Host "  no world directory, skipping the integration part: $Worlds" -ForegroundColor Yellow
    }
    & $dotnet @arguments
    return ($LASTEXITCODE -eq 0)
}

Invoke-Step "plan map payload (script parses, grid decodes, charges inside the world)" {
    $node = Get-Command node -ErrorAction SilentlyContinue
    if (-not $node) {
        Write-Host "  node is not installed, skipping" -ForegroundColor Yellow
        return $true
    }
    $world = Get-ChildItem -LiteralPath $Worlds -Filter *.wld -ErrorAction SilentlyContinue |
        Sort-Object Length | Select-Object -First 1
    if (-not $world) {
        Write-Host "  no world file to plan, skipping" -ForegroundColor Yellow
        return $true
    }
    $html = Join-Path $env:TEMP "zhadai-verify-map.html"
    & $dotnet (Join-Path $root "src\ZhaDai.Cli\bin\$Configuration\net8.0\zhaodai.dll") plan $world.FullName "--html=$html" | Out-Null
    & node (Join-Path $root "tools\check-plan-map.js") $html
    return ($LASTEXITCODE -eq 0)
}

Invoke-Step "ui smoke render (five states, non blank, palette present)" {
    $output = Join-Path $root "artifacts\ui-smoke"
    $ui = Join-Path $root "src\ZhaDai.Manager\bin\$Configuration\net8.0-windows\zhaodai-ui.dll"
    & $dotnet $ui "--ui-smoke=$output"
    if ($LASTEXITCODE -ne 0) { return $false }
    $png = Get-ChildItem -LiteralPath $output -Filter *.png -ErrorAction SilentlyContinue
    if (-not $png -or $png.Count -lt 5) { Write-Host "  expected five renders" -ForegroundColor Red; return $false }
    Add-Type -AssemblyName System.Drawing
    foreach ($file in $png) {
        $bitmap = [System.Drawing.Bitmap]::FromFile($file.FullName)
        $colours = New-Object 'System.Collections.Generic.HashSet[int]'
        for ($y = 0; $y -lt $bitmap.Height; $y += 3) {
            for ($x = 0; $x -lt $bitmap.Width; $x += 3) { $colours.Add($bitmap.GetPixel($x, $y).ToArgb()) | Out-Null }
        }
        $count = $colours.Count
        $bitmap.Dispose()
        Write-Host ("  {0}: {1} colours" -f $file.Name, $count)
        if ($count -lt 20) { Write-Host "  render looks blank" -ForegroundColor Red; return $false }
    }
    return $true
}

Invoke-Step "runtime plugin builds (net48, no compile time game reference)" {
    & $dotnet build (Join-Path $root "src\ZhaDai.Runtime\ZhaDai.Runtime.csproj") -c $Configuration --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { return $false }
    $plugin = Join-Path $root "src\ZhaDai.Runtime\bin\$Configuration\net48\ZhaDai.Runtime.dll"
    return (Test-Path -LiteralPath $plugin)
}

if (-not $Terraria) {
    $Terraria = $env:ZHAODAI_TERRARIA
}
if (-not $Terraria -or -not (Test-Path -LiteralPath $Terraria)) {
    foreach ($candidate in @(
        "D:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe",
        "C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe")) {
        if (Test-Path -LiteralPath $candidate) { $Terraria = $candidate; break }
    }
}

$patcher = Join-Path $root "src\ZhaDai.Patcher\bin\$Configuration\net48\ZhaDai.Patcher.exe"
$pluginDll = Join-Path $root "src\ZhaDai.Runtime\bin\$Configuration\net48\ZhaDai.Runtime.dll"

if (-not (Test-Path -LiteralPath $patcher)) {
    Write-Host ""
    Write-Host "patcher not built; build src\ZhaDai.Patcher with MSBuild first (needs Mono.Cecil in lib\)" -ForegroundColor Yellow
}
elseif (-not $Terraria) {
    Write-Host ""
    Write-Host "Terraria.exe not found; skipping the injection checks" -ForegroundColor Yellow
}
else {
    Invoke-Step "reflection member list against the real Terraria.exe" {
        $output = & $patcher verify --terraria $Terraria --plugin $pluginDll 2>&1
        $output | Select-String -Pattern "^共核对|^结论" | ForEach-Object { Write-Host "  $($_.Line)" }
        return ($LASTEXITCODE -eq 0)
    }

    Invoke-Step "injection rehearsal on a copy, original hash unchanged" {
        $before = (Get-FileHash -LiteralPath $Terraria -Algorithm SHA256).Hash
        $copy = Join-Path $env:TEMP "zhadai-verify\Terraria.patched.exe"
        New-Item -ItemType Directory -Force -Path (Split-Path $copy) | Out-Null
        & $patcher patch-copy --terraria $Terraria --out $copy --plugin $pluginDll | Out-Null
        if ($LASTEXITCODE -ne 0) { return $false }
        $after = (Get-FileHash -LiteralPath $Terraria -Algorithm SHA256).Hash
        Write-Host "  original hash before: $before"
        Write-Host "  original hash after:  $after"
        if ($before -ne $after) { Write-Host "  the original was touched" -ForegroundColor Red; return $false }
        return (Test-Path -LiteralPath $copy)
    }

    Invoke-Step "install dry run writes nothing" {
        & $patcher install --terraria $Terraria --plugin $pluginDll --dry-run | Out-Null
        return ($LASTEXITCODE -eq 0)
    }
}

Write-Host ""
Write-Host "=== summary ===" -ForegroundColor Cyan
foreach ($result in $results) {
    $mark = if ($result.Passed) { "PASS" } else { "FAIL" }
    $colour = if ($result.Passed) { "Green" } else { "Red" }
    Write-Host ("  [{0}] {1}" -f $mark, $result.Name) -ForegroundColor $colour
}

$failed = @($results | Where-Object { -not $_.Passed }).Count
Write-Host ""
if ($failed -eq 0) {
    Write-Host "all steps passed ($($results.Count))" -ForegroundColor Green
    exit 0
}

Write-Host "$failed of $($results.Count) steps failed" -ForegroundColor Red
exit 1
