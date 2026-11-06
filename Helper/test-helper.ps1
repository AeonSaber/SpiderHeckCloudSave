# Runs cloudsave-helper.ps1 against dummy folders under .\run and prints what each case did.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File .\test-helper.ps1
param([string]$Helper = (Join-Path $PSScriptRoot "cloudsave-helper.ps1"))
$ErrorActionPreference = 'Stop'
$base = Join-Path $PSScriptRoot 'run'
if (Test-Path $base) { cmd /c rmdir /s /q "$base" | Out-Null }
$work = Join-Path $base 'work'; $pc = Join-Path $base 'pc'; $cloudA = Join-Path $base 'cloudA'; $cloudB = Join-Path $base 'cloudB'
New-Item -ItemType Directory -Force -Path (Join-Path $work 'Runs'), (Join-Path $work 'Backups'), $cloudA, $cloudB | Out-Null
$roots = @(
    @{ Key = 'SaveData'; Name = 'Save data'; Local = (Join-Path $pc 'LocalLow\Studio\Game') },
    @{ Key = 'SilkConfig'; Name = 'Silk configs'; Local = (Join-Path $pc 'Game\Silk\Config') },
    @{ Key = 'SilkMods'; Name = 'Silk mods'; Local = (Join-Path $pc 'Game\Silk\Mods') }
)
foreach ($r in $roots) {
    New-Item -ItemType Directory -Force -Path (Join-Path $r.Local 'sub\deeper') | Out-Null
    Set-Content (Join-Path $r.Local 'a.txt') "pc $($r.Key)"
    Set-Content (Join-Path $r.Local 'sub\deeper\b.bin') ('x' * 5000)
}
$ro = Join-Path $roots[0].Local 'readonly.txt'; Set-Content $ro 'ro'; (Get-Item $ro).IsReadOnly = $true

$script:n = 0
function Invoke-Plan($Action, $CloudRoot, $When = 'Cloud', $GamePid = 999999, $Wait = 5) {
    $script:n++
    $stamp = '20261106-1600{0:00}' -f $script:n
    $planRoots = foreach ($r in $roots) {
        $cloud = ''
        if ($CloudRoot) { $cloud = Join-Path $CloudRoot ('SpiderHeck\' + $r.Key) }
        @{ Key = $r.Key; Name = $r.Name; Local = $r.Local; Cloud = $cloud }
    }
    $plan = @{ Action = $Action; GamePid = $GamePid; GameName = 'NoSuchGameProcess'; WaitSeconds = $Wait; Stamp = $stamp
        WorkDir = $work; BackupsRoot = (Join-Path $work 'Backups'); BackupDir = (Join-Path $work "Backups\$stamp")
        LogPath = (Join-Path $work "Runs\$stamp.log"); ResultPath = (Join-Path $work 'last-run.txt'); WhenBothExist = $When
        KeepBackups = 3; Roots = @($planRoots) }
    $planPath = Join-Path $work "Runs\$stamp.plan.json"
    $plan | ConvertTo-Json -Depth 5 | Set-Content $planPath -Encoding UTF8
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $Helper -PlanPath $planPath | Out-Null
    return (Get-Content (Join-Path $work 'last-run.txt'))
}
function State($r) {
    $i = Get-Item -LiteralPath $r.Local -Force -ErrorAction SilentlyContinue
    if (-not $i) { return 'MISSING' }
    $t = ''; if ($i.LinkType) { $t = " -> $(@($i.Target)[0])" }
    $a = Get-Content (Join-Path $r.Local 'a.txt') -ErrorAction SilentlyContinue
    return "$($i.LinkType)$t | a.txt=$a"
}
function Show($title, $res) {
    "=== $title"
    $res | Where-Object { $_ -match '^(Ok|Error|Root:)' }
    foreach ($r in $roots) { "  $($r.Key): $(State $r)" }
}

Show '1 Enable -> cloudA' (Invoke-Plan 'Enable' $cloudA)
Show '2 Switch -> cloudB' (Invoke-Plan 'Switch' $cloudB)
"  cloudA kept: $(Test-Path (Join-Path $cloudA 'SpiderHeck\SaveData\a.txt'))"
Show '3 Disable' (Invoke-Plan 'Disable' $null)
"  cloudB kept: $(Test-Path (Join-Path $cloudB 'SpiderHeck\SaveData\a.txt'))  readonly restored: $(Test-Path $ro)"
foreach ($r in $roots) { Set-Content (Join-Path $r.Local 'a.txt') "pc changed $($r.Key)" }
Show '4 Enable -> cloudA, both exist, keep Cloud' (Invoke-Plan 'Enable' $cloudA 'Cloud')
Show '5 Disable' (Invoke-Plan 'Disable' $null)
foreach ($r in $roots) { Set-Content (Join-Path $r.Local 'a.txt') "pc wins $($r.Key)" }
Show '6 Enable -> cloudA, both exist, keep Local' (Invoke-Plan 'Enable' $cloudA 'Local')
"  renamed aside: $((Get-ChildItem (Join-Path $cloudA 'SpiderHeck') -Directory | Where-Object Name -like '*.replaced-*').Name -join ', ')"
Show '7 Disable while the game is still running (own pid, 3s wait)' (Invoke-Plan 'Disable' $null 'Cloud' $PID 3)
Show '8 Disable' (Invoke-Plan 'Disable' $null)
New-Item -ItemType Junction -Path (Join-Path $roots[2].Local 'linked') -Value $cloudB | Out-Null
Show '9 Enable -> cloudB with a link inside Silk mods' (Invoke-Plan 'Enable' $cloudB 'Local')
"=== backups kept: $((Get-ChildItem (Join-Path $work 'Backups') -Directory).Name -join ', ')"
"=== lock left behind: $(Test-Path (Join-Path $work 'helper.lock'))"
