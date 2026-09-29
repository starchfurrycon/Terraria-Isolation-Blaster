# 把一次接管的 runtime.log / status.txt 读成一份中文小结，只读，不写游戏目录。
#
# 用法：
#   pwsh -File tools/analyze-run.ps1                                  # 默认看 D:\zhadai-test\game\ZhaDai
#   pwsh -File tools/analyze-run.ps1 -RunDir "D:\zhadai-test\game\ZhaDai"
#   pwsh -File tools/analyze-run.ps1 -RunDir <目录> -Tail 40          # 附上日志最后 40 行
#
# 只读，绝不修改副本或存档；结论全部来自 status.txt 的结构化字段与 runtime.log 的原始行。

[CmdletBinding()]
param(
    [string]$RunDir = 'D:\zhadai-test\game\ZhaDai',
    [int]$Tail = 0
)

$ErrorActionPreference = 'Stop'
$statusPath = Join-Path $RunDir 'status.txt'
$logPath = Join-Path $RunDir 'runtime.log'

function Say([string]$text, [string]$color = 'Gray') { Write-Host $text -ForegroundColor $color }

if (-not (Test-Path -LiteralPath $statusPath) -and -not (Test-Path -LiteralPath $logPath)) {
    Say "这里没有 runtime.log / status.txt：$RunDir" 'Yellow'
    Say "插件只在进入世界后初始化；如果刚进过世界还是没有文件，说明插件没加载（确认用的是注入过的 exe）。" 'Yellow'
    exit 2
}

function Read-Status([string]$path) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $path)) { return $map }
    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        $at = $line.IndexOf('=')
        if ($at -gt 0) { $map[$line.Substring(0, $at)] = $line.Substring($at + 1) }
    }

    return $map
}

$status = Read-Status $statusPath
function Field([string]$name) { if ($status.ContainsKey($name)) { $status[$name] } else { '—' } }
function Number([string]$name) { $value = Field $name; if ($value -match '^\d+$') { [int]$value } else { 0 } }

Say ""
Say "=== 炸带 接管小结 ===" 'White'
Say "目录：$RunDir"
Say "时间：$(Field 'timestamp')"

Say ""
Say "--- 环境 ---" 'White'
$reflection = Field 'reflection'
Say "反射自检：$reflection$(if ($reflection -eq 'ok') { '（插件工作正常）' } else { '（插件没起来，下面数字不可信）' })"
Say "世界尺寸：$(Field 'world')    施工文件：$(Field 'plan')"
Say "按键开关：enabled=$(Field 'enabled')"
Say "玩家：生命 $(Field 'life')    位置 $(Field 'position')"

$chargeIndex = Field 'charge'
$fired = Number 'fired'
$skipped = Number 'skipped'
$deaths = Number 'deaths'
$chargeCount = 0
if ($chargeIndex -match '^(\d+)/(\d+)$') { $chargeCount = [int]$Matches[2] }

Say ""
Say "--- 进度 ---" 'White'
Say "状态：$(Field 'state')    当前：$(Field 'message')"
Say "施工文件 $chargeCount 发；已放 $fired 发，跳过 $skipped 发"
Say "死亡 $deaths 次；被自己的雷管炸到 $(Field 'blastHits') 次"
Say "墓碑清理：$(Field 'gravestones') 格"

Say ""
Say "--- 怎么走的 ---" 'White'
Say "A* 规划 $(Field 'routes') 次，重规划 $(Field 'routeRestarts') 次；路线自挖 $(Field 'routeDigs') 格"
Say "路线说明：$(Field 'routeNote')"
Say "镐子进度（完成/放弃）：$(Field 'digsDone')"
Say "封堵（放好/跳过/本来就有）：$(Field 'plugs')"
Say "危险等待（敌怪贴脸、岩浆、深水）：$(Field 'hazardWaits') 次"
Say "重试（死过一次又重新放的那一发）：$(Field 'retries')"

# 枚举名换成中文，免得回传时两边猜。字典里没有的原样打印。
$reasonText = @{
    'NoDynamite' = '背包里没有雷管'
    'NoRetreatRoom' = '这一发没有安全的撤离空间'
    'ExplosivesInBlast' = '爆区里有炸药类物块'
    'LavaInBlastWithoutImmunity' = '爆开会放出岩浆且没有免疫'
    'StandingInLava' = '正踩在岩浆里'
    'HostileTooClose' = '敌怪贴脸'
    'AboutToDrown' = '快溺水了'
    'ManualStop' = '手动停止'
    'NotEnoughSupplies' = '材料不够，拒绝接管'
}

$skips = $status.Keys | Where-Object { $_ -like 'skip.*' }
if ($skips) {
    Say ""
    Say "--- 跳过原因 ---" 'White'
    foreach ($key in ($skips | Sort-Object { -[int]$status[$_] })) {
        $name = $key.Substring(5)
        $text = if ($reasonText.ContainsKey($name)) { $reasonText[$name] } else { $name }
        Say ("  {0,-24} {1} 次  ({2})" -f $name, $status[$key], $text)
    }
}

if (Test-Path -LiteralPath $logPath) {
    $log = [System.IO.File]::ReadAllLines($logPath)
    $info = New-Object System.Collections.Generic.List[string]
    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($line in $log) {
        if ($line -match '拒绝接管|接管前盘点|第 \d+ 次死亡|吃自伤|没能到位|异常|停止接管|材料不够|复核|状态文件写不了') {
            $info.Add($line)
        }

        if ($line -match '异常|失败|错误|不能|崩溃') { $problems.Add($line) }
    }

    Say ""
    Say "--- 日志要点（$($log.Count) 行，摘出 $($info.Count) 条）---" 'White'
    foreach ($line in ($info | Select-Object -Last 25)) { Say "  $line" }

    if ($problems.Count -gt 0) {
        Say ""
        Say "--- 可疑行（$($problems.Count) 条）---" 'Yellow'
        foreach ($line in ($problems | Select-Object -Last 10)) { Say "  $line" 'Yellow' }
    }

    if ($Tail -gt 0) {
        Say ""
        Say "--- 日志最后 $Tail 行 ---" 'White'
        foreach ($line in ($log | Select-Object -Last $Tail)) { Say "  $line" }
    }
}

Say ""
Say "把上面这段（或整个 runtime.log、status.txt）回传即可。" 'Green'
