# 炸带管理器界面自检：用本机 .NET 8 SDK 构建 src\ZhaDai.Manager，然后跑 --ui-smoke 无头渲染。
#
# 本文件必须以「UTF-8 带 BOM」保存：Windows PowerShell 5.1 读没有 BOM 的 .ps1 会按 ANSI 解码，
# 下面的中文路径与提示会变成乱码。
#
#   powershell -ExecutionPolicy Bypass -File tools\manager-ui-smoke.ps1
#   powershell -ExecutionPolicy Bypass -File tools\manager-ui-smoke.ps1 -Configuration Debug -OutputDirectory artifacts\ui-smoke
#
# 退出码：0 = 构建成功且界面自检通过；2 = 构建失败或界面自检失败。

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts\ui-smoke',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src\ZhaDai.Manager\ZhaDai.Manager.csproj'

# .NET 8 SDK 不在 PATH 上（机器全局的 dotnet 是 3.0.101，构建不了 net8.0）。
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    Write-Error "找不到本机 .NET SDK：$dotnet（本仓库的 net8.0 目标不能用 PATH 上的 dotnet 构建）"
    exit 2
}

if (-not (Test-Path -LiteralPath $project)) {
    Write-Error "找不到工程：$project"
    exit 2
}

if (-not $SkipBuild) {
    Write-Host "构建 $project（$Configuration）…"
    & $dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error "构建失败（exit $LASTEXITCODE）"
        exit 2
    }
}

$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
} else {
    Join-Path $repositoryRoot $OutputDirectory
}

$executable = Join-Path $repositoryRoot "src\ZhaDai.Manager\bin\$Configuration\net8.0-windows\zhaodai-ui.exe"
if (-not (Test-Path -LiteralPath $executable)) {
    Write-Error "找不到构建产物：$executable"
    exit 2
}

if (Test-Path -LiteralPath $outputPath) {
    Get-ChildItem -LiteralPath $outputPath -Filter '*.png' -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

Write-Host "运行界面自检：$executable --ui-smoke=$outputPath"
& $executable "--ui-smoke=$outputPath" | Out-Host
$exitCode = $LASTEXITCODE

$images = @()
if (Test-Path -LiteralPath $outputPath) {
    $images = @(Get-ChildItem -LiteralPath $outputPath -Filter '*.png' | Sort-Object Name)
}

Write-Host ''
Write-Host "界面自检退出码：$exitCode"
Write-Host "输出目录：$outputPath"

if ($images.Count -eq 0) {
    Write-Error '没有生成任何 PNG。'
    exit 2
}

Write-Host '生成的 PNG：'
foreach ($image in $images) {
    Write-Host ("  {0}  {1} 字节" -f $image.FullName, $image.Length)
}

$report = Join-Path $outputPath 'ui-smoke.txt'
if (Test-Path -LiteralPath $report) {
    Write-Host ''
    Write-Host "自检报告：$report"
}

exit $exitCode
