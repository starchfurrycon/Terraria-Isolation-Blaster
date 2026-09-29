# 本文件以「UTF-8 带 BOM」保存：Windows PowerShell 5.1 读无 BOM 的 .ps1 会按 ANSI 解码，中文会变乱码。
<#
.SYNOPSIS
    构建 ZhaDai.Patcher（以及 src\ZhaDai.Patcher\testdata 下的测试用替身插件）。

.DESCRIPTION
    本机 .NET SDK 不在 PATH 上（机器全局 dotnet 是 3.0.101，构建不了现代 TFM），
    但 net48 目标用 VS2022 的 MSBuild 完全没问题，所以本脚本以 MSBuild 为主路径。

.NOTES
    退出码：0 = 成功，1 = 缺少 MSBuild / 缺少 Mono.Cecil，其它 = MSBuild 的退出码。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Tests
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$patcherProject = Join-Path $repoRoot 'src\ZhaDai.Patcher\ZhaDai.Patcher.csproj'
$stubProject = Join-Path $repoRoot 'src\ZhaDai.Patcher\testdata\ZhaDai.Runtime\ZhaDai.Runtime.csproj'
$libDirectory = Join-Path $repoRoot 'src\ZhaDai.Patcher\lib'

function Find-MSBuild {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe')
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }

    $command = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
            Select-Object -First 1
        if ($found) { return $found }
    }
    return $null
}

$msbuild = Find-MSBuild
if (-not $msbuild) {
    Write-Error '找不到 MSBuild.exe。请安装 Visual Studio 2022（含 .NET 桌面开发工作负载）。'
    exit 1
}
Write-Host "MSBuild: $msbuild"

# Mono.Cecil 是唯一的外部依赖，且刻意不走 NuGet（见 README「依赖」一节）。
$requiredLibs = @('Mono.Cecil.dll', 'Mono.Cecil.Rocks.dll', 'Mono.Cecil.Pdb.dll', 'Mono.Cecil.Mdb.dll')
$missingLibs = @($requiredLibs | Where-Object { -not (Test-Path -LiteralPath (Join-Path $libDirectory $_)) })
if ($missingLibs.Count -gt 0) {
    Write-Error ("缺少 $libDirectory 下的：{0}。把 Mono.Cecil 0.11.6 的四个 DLL 放进去再重试。" -f ($missingLibs -join ', '))
    exit 1
}

function Invoke-Build([string]$project, [string]$platform) {
    Write-Host "构建: $project ($Configuration / $platform)"
    & $msbuild $project /nologo /v:m /p:Configuration=$Configuration /p:Platform=$platform
    if ($LASTEXITCODE -ne 0) {
        Write-Error "$project 构建失败（exit $LASTEXITCODE）"
        exit $LASTEXITCODE
    }
}

Invoke-Build $patcherProject 'AnyCPU'

if ($Tests) {
    Invoke-Build $stubProject 'AnyCPU'
}

$outputDirectory = Join-Path $repoRoot "src\ZhaDai.Patcher\bin\$Configuration\net48"
Write-Host ''
Write-Host "产物目录: $outputDirectory"
Get-ChildItem -LiteralPath $outputDirectory -File |
    Where-Object { $_.Extension -in @('.exe', '.dll') } |
    Select-Object Name, Length |
    Format-Table -AutoSize | Out-String -Width 120 | Write-Host
Write-Host '构建完成。'
exit 0
