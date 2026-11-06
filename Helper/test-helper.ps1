# Runs cloudsave-helper.ps1 against dummy folders under .\run and checks what each case did.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File test-helper.ps1
# Prints PASS or FAIL per check and a total; exits 1 if anything failed.
param([string]$Helper = (Join-Path $PSScriptRoot 'cloudsave-helper.ps1'))
$ErrorActionPreference = 'Stop'

$base = Join-Path $PSScriptRoot 'run'
if (Test-Path $base) { cmd /c rmdir /s /q "$base" | Out-Null }
$work = Join-Path $base 'work'; $pc = Join-Path $base 'pc'; $cloudA = Join-Path $base 'cloudA'; $cloudB = Join-Path $base 'cloudB'
New-Item -ItemType Directory -Force -Path (Join-Path $work 'Runs'), (Join-Path $work 'Backups'), $cloudA, $cloudB | Out-Null

$roots = @(
    @{ Key = 'SaveData'; Name = 'Save data'; Local = (Join-Path $pc 'LocalLow\Studio\Game') },
    @{ Key = 'SilkConfig'; Name = 'Silk settings'; Local = (Join-Path $pc 'Game\Silk\Config') },
    @{ Key = 'SilkMods'; Name = 'Silk mods'; Local = (Join-Path $pc 'Game\Silk\Mods') }
)

function Set-ReadOnlyFolders([string]$Path) {
    Get-ChildItem -LiteralPath $Path -Recurse -Force -Directory | ForEach-Object { $_.Attributes = $_.Attributes -bor [IO.FileAttributes]::ReadOnly }
}
function Set-Text([string]$Text) { foreach ($r in $roots) { Set-Content (Join-Path $r.Local 'a.txt') "$Text $($r.Key)" } }

foreach ($r in $roots) {
    New-Item -ItemType Directory -Force -Path (Join-Path $r.Local 'sub\deeper') | Out-Null
    Set-Content (Join-Path $r.Local 'sub\deeper\b.bin') ('x' * 5000)
}
Set-Text 'pc'
$ro = Join-Path $roots[0].Local 'readonly.txt'; Set-Content $ro 'ro'; (Get-Item $ro).IsReadOnly = $true
foreach ($r in $roots) { Set-ReadOnlyFolders $r.Local }

$script:failures = 0
$script:n = 0
$script:stamps = @{}
function Check([string]$Name, [bool]$Condition) {
    if ($Condition) { "  PASS $Name" } else { "  FAIL $Name"; $script:failures++ }
}

function Invoke-Plan([string]$Title, $Action, $CloudRoot, $When = 'Cloud', $GamePid = 999999, $Wait = 5, $RestoreFrom = '') {
    $script:n++
    $stamp = '20261106-1700{0:00}' -f $script:n
    $script:stamps[$script:n] = $stamp
    $planRoots = foreach ($r in $roots) {
        $cloud = ''
        if ($CloudRoot) { $cloud = Join-Path $CloudRoot ('SpiderHeck\' + $r.Key) }
        @{ Key = $r.Key; Name = $r.Name; Local = $r.Local; Cloud = $cloud }
    }
    $restorePath = ''
    if ($RestoreFrom) { $restorePath = Join-Path $work "Backups\$RestoreFrom" }
    $plan = @{ Action = $Action; GamePid = $GamePid; GameName = 'NoSuchGameProcess'; WaitSeconds = $Wait; Stamp = $stamp
        WorkDir = $work; BackupsRoot = (Join-Path $work 'Backups'); BackupDir = (Join-Path $work "Backups\$stamp")
        LogPath = (Join-Path $work "Runs\$stamp.log"); ResultPath = (Join-Path $work 'last-run.txt'); WhenBothExist = $When
        KeepBackups = 3; RestoreFrom = $restorePath; Roots = @($planRoots) }
    $planPath = Join-Path $work "Runs\$stamp.plan.json"
    $plan | ConvertTo-Json -Depth 5 | Set-Content $planPath -Encoding UTF8
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $Helper -PlanPath $planPath | Out-Null
    Write-Host "=== $script:n $Title"
    $result = Get-Content (Join-Path $work 'last-run.txt')
    $result | Where-Object { $_ -match '^(Error|Warning)=' -or $_ -match '=failed\|' } | ForEach-Object { Write-Host "    $_" }
    return $result
}

function Get-Ok($Result) { return ($Result -contains 'Ok=true') }
function Get-Link($r) { $i = Get-Item -LiteralPath $r.Local -Force -ErrorAction SilentlyContinue; if ($i -and $i.LinkType) { return [string]@($i.Target)[0] } return $null }
function Get-A($r) { return (Get-Content (Join-Path $r.Local 'a.txt') -ErrorAction SilentlyContinue) }
function Test-AllLinked($Root) { foreach ($r in $roots) { if ((Get-Link $r) -ne (Join-Path $Root "SpiderHeck\$($r.Key)")) { return $false } } return $true }
function Test-AllLocal { foreach ($r in $roots) { if ((Get-Link $r) -or -not (Test-Path $r.Local)) { return $false } } return $true }
function Test-AllText([string]$Text) { foreach ($r in $roots) { if ((Get-A $r) -ne "$Text $($r.Key)") { return $false } } return $true }
function Test-NoLeftovers { foreach ($r in $roots) { if (@(Get-ChildItem (Split-Path $r.Local) -Directory | Where-Object { $_.Name -like "$(Split-Path -Leaf $r.Local).*" }).Count -gt 0) { return $false } } return $true }
function Test-NoReadOnlyFolders { foreach ($r in $roots) { if (@(Get-ChildItem -LiteralPath $r.Local -Recurse -Force -Directory | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReadOnly }).Count -gt 0) { return $false } } return $true }
function Get-BackupText([int]$Run, $r) { return (Get-Content (Join-Path $work "Backups\$($script:stamps[$Run])\$($r.Key)\a.txt") -ErrorAction SilentlyContinue) }

$res = Invoke-Plan 'turn on -> cloudA (read-only folders on this PC)' 'Enable' $cloudA
Check 'ok' (Get-Ok $res)
Check 'all three linked to cloudA' (Test-AllLinked $cloudA)
Check 'contents arrived' (Test-AllText 'pc')
Check 'no leftover folders next to the originals' (Test-NoLeftovers)
Check 'backup holds the original, with backup-info.txt' (((Get-BackupText 1 $roots[0]) -eq 'pc SaveData') -and (Test-Path (Join-Path $work "Backups\$($script:stamps[1])\backup-info.txt")))
Check 'result names the backup folder' (@($res | Where-Object { $_ -like 'Backup=*' }).Count -eq 1)
foreach ($r in $roots) { Set-ReadOnlyFolders (Join-Path $cloudA "SpiderHeck\$($r.Key)") }   # the way OneDrive leaves them

$res = Invoke-Plan 'move cloudA -> cloudB (cloud folders read-only, as in the real 0.2.0 failure)' 'Switch' $cloudB
Check 'ok' (Get-Ok $res)
Check 'all three linked to cloudB' (Test-AllLinked $cloudB)
Check 'contents intact' (Test-AllText 'pc')
Check 'cloudA copy kept' (Test-Path (Join-Path $cloudA 'SpiderHeck\SaveData\a.txt'))
Check 'no leftover folders next to the originals' (Test-NoLeftovers)

$res = Invoke-Plan 'turn off' 'Disable' $null
Check 'ok' (Get-Ok $res)
Check 'all three back on this PC' (Test-AllLocal)
Check 'contents intact, read-only file restored' ((Test-AllText 'pc') -and (Test-Path $ro))
Check 'cloudB copy kept' (Test-Path (Join-Path $cloudB 'SpiderHeck\SaveData\a.txt'))
Check 'no read-only folders left on this PC' (Test-NoReadOnlyFolders)

Set-Text 'pc changed'
$res = Invoke-Plan 'turn on -> cloudA, which has data: keep the cloud copy' 'Enable' $cloudA 'Cloud'
Check 'ok' (Get-Ok $res)
Check 'linked to cloudA, showing the cloud copy' ((Test-AllLinked $cloudA) -and (Test-AllText 'pc'))
Check "this PC's copy is in the backup" ((Get-BackupText 4 $roots[0]) -eq 'pc changed SaveData')

$res = Invoke-Plan 'turn off' 'Disable' $null
Check 'ok' (Get-Ok $res)

Set-Text 'pc wins'
$res = Invoke-Plan "turn on -> cloudA, which has data: keep this PC's copy" 'Enable' $cloudA 'Local'
Check 'ok' (Get-Ok $res)
Check "linked to cloudA, showing this PC's copy" ((Test-AllLinked $cloudA) -and (Test-AllText 'pc wins'))
Check 'old cloud copy renamed aside, not deleted' (@(Get-ChildItem (Join-Path $cloudA 'SpiderHeck') -Directory | Where-Object Name -like '*.replaced-*').Count -eq 3)

$res = Invoke-Plan 'turn off while the game is still running (own process, 3 s wait)' 'Disable' $null 'Cloud' $PID 3
Check 'refused' (-not (Get-Ok $res))
Check 'still linked, nothing touched' ((Test-AllLinked $cloudA) -and (Test-AllText 'pc wins'))

$res = Invoke-Plan 'restore run 4 backup while linked to cloudA' 'Restore' $null 'Cloud' 999999 5 $script:stamps[4]
Check 'ok' (Get-Ok $res)
Check 'still linked, now showing the backup' ((Test-AllLinked $cloudA) -and (Test-AllText 'pc changed'))
Check 'replaced cloud copy kept aside in the cloud' (@(Get-ChildItem (Join-Path $cloudA 'SpiderHeck') -Directory | Where-Object Name -like '*.before-restore-*').Count -eq 3)
Check 'pre-restore state backed up' ((Get-BackupText 8 $roots[0]) -eq 'pc wins SaveData')

$res = Invoke-Plan 'turn off' 'Disable' $null
Check 'ok and local' ((Get-Ok $res) -and (Test-AllLocal) -and (Test-AllText 'pc changed'))

$res = Invoke-Plan 'restore run 6 backup while on this PC' 'Restore' $null 'Cloud' 999999 5 $script:stamps[6]
Check 'ok' (Get-Ok $res)
Check 'local, now showing the backup' ((Test-AllLocal) -and (Test-AllText 'pc wins'))
Check 'no leftover folders next to the originals' (Test-NoLeftovers)

$locked = [IO.File]::Open((Join-Path $roots[0].Local 'a.txt'), 'Open', 'Read', 'None')
try {
    $res = Invoke-Plan 'turn on -> cloudB with a save file held open by another program' 'Enable' $cloudB 'Local'
} finally { $locked.Close() }
Check 'save data failed' (@($res | Where-Object { $_ -like 'Root:SaveData=failed*' }).Count -eq 1)
Check 'save data left as it was' ((-not (Get-Link $roots[0])) -and ((Get-A $roots[0]) -eq 'pc wins SaveData'))
Check 'the other two moved' (((Get-Link $roots[1]) -eq (Join-Path $cloudB 'SpiderHeck\SilkConfig')) -and ((Get-Link $roots[2]) -eq (Join-Path $cloudB 'SpiderHeck\SilkMods')))

$res = Invoke-Plan 'turn off (two linked, one local)' 'Disable' $null
Check 'ok and all local' ((Get-Ok $res) -and (Test-AllLocal) -and (Test-AllText 'pc wins'))

New-Item -ItemType Junction -Path (Join-Path $roots[2].Local 'linked') -Value $cloudB | Out-Null
$res = Invoke-Plan 'turn on -> cloudB with a link inside Silk mods' 'Enable' $cloudB 'Local'
Check 'Silk mods refused' (@($res | Where-Object { $_ -like 'Root:SilkMods=failed*' }).Count -eq 1)
Check 'Silk mods left as it was' ((-not (Get-Link $roots[2])) -and ((Get-A $roots[2]) -eq 'pc wins SilkMods'))

Check 'lock file removed' (-not (Test-Path (Join-Path $work 'helper.lock')))
"backups kept: $((Get-ChildItem (Join-Path $work 'Backups') -Directory).Name -join ', ')"
if ($script:failures -eq 0) { 'ALL CHECKS PASSED' } else { "$($script:failures) CHECK(S) FAILED"; exit 1 }
