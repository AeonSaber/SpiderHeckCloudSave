# SpiderHeck CloudSave helper, started by the mod as the game exits.
#
# Waits for the game to close, then turns cloud save on or off, moves it to another folder, or
# restores a backup, as the plan file says. Every change follows the same rules:
#   - back up first: a full copy of every folder it is about to change, in Backups\<run>\, with
#     backup-info.txt saying what each copy is and where it came from;
#   - copy and check before removing anything, and never leave a folder half-deleted: the old
#     folder is renamed aside, the link (or restored copy) goes in, and only then is the old
#     folder deleted;
#   - never delete anything inside a cloud folder, because other PCs may use it (replaced cloud
#     data is renamed, not removed);
#   - leave each folder either fully changed or as it was.
# Writes a log to Runs\<run>.log and a summary to last-run.txt for the mod to show.

param([Parameter(Mandatory = $true)][string]$PlanPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$plan = Get-Content -LiteralPath $PlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
$rootResults = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]
$backupInfo = New-Object System.Collections.Generic.List[string]
$fatal = $null
$ok = $true

function Write-Log([string]$Message) {
    $line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $Message
    Add-Content -LiteralPath $plan.LogPath -Value $line -Encoding UTF8
}

function Get-FullPath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
}

function Test-Junction([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $false }
    return $item.PSIsContainer -and (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) -and
        ($item.LinkType -eq 'Junction' -or $item.LinkType -eq 'SymbolicLink')
}

function Get-JunctionTarget([string]$Path) {
    $target = (Get-Item -LiteralPath $Path -Force).Target
    if ($target -is [array]) { $target = $target[0] }
    return [string]$target
}

# File count and total bytes under a folder. Refuses folders that contain links, so a copy can
# never follow a link somewhere else. Cloud placeholder files and folders (OneDrive, Google
# Drive) are counted like ordinary ones.
function Measure-Tree([string]$Path) {
    $count = 0
    $bytes = [int64]0
    if (-not (Test-Path -LiteralPath $Path)) {
        return New-Object psobject -Property @{ Count = 0; Bytes = [int64]0 }
    }
    $stack = New-Object System.Collections.Stack
    $stack.Push((New-Object System.IO.DirectoryInfo -ArgumentList (Get-FullPath $Path)))
    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()
        foreach ($entry in $dir.EnumerateFileSystemInfos()) {
            if ($entry -is [System.IO.DirectoryInfo]) {
                if ((($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) -and (Test-Junction $entry.FullName)) {
                    throw "'$($entry.FullName)' is a link inside the folder, and CloudSave does not move folders that contain links"
                }
                $stack.Push($entry)
            } else {
                $count++
                $bytes += $entry.Length
            }
        }
    }
    return New-Object psobject -Property @{ Count = $count; Bytes = $bytes }
}

# Copies a folder into an empty or missing destination and checks the copy by file count and
# size. Returns the source's count and size.
function Copy-Tree([string]$From, [string]$To) {
    $source = Measure-Tree $From
    if ((Measure-Tree $To).Count -gt 0) { throw "'$To' is not empty" }
    New-Item -ItemType Directory -Force -Path $To | Out-Null
    $output = & robocopy.exe $From $To /E /COPY:DAT /DCOPY:T /R:2 /W:1 /NP /NFL /NDL /NJH /NJS 2>&1
    $code = $LASTEXITCODE
    if ($code -ge 8) {
        throw "copying '$From' to '$To' failed (robocopy exit code $code): $(($output | Out-String).Trim())"
    }
    $copy = Measure-Tree $To
    if ($copy.Count -ne $source.Count -or $copy.Bytes -ne $source.Bytes) {
        throw "the copy of '$From' does not match it: $($source.Count) files and $($source.Bytes) bytes, but the copy has $($copy.Count) and $($copy.Bytes)"
    }
    return $source
}

# Folders copied out of OneDrive arrive read-only, and Windows refuses to delete read-only
# folders ("Access denied"). That stopped 0.2.0 halfway through deleting this PC's copy.
function Clear-ReadOnly([string]$Path) {
    $readOnly = [System.IO.FileAttributes]::ReadOnly
    $items = @(Get-Item -LiteralPath $Path -Force) + @(Get-ChildItem -LiteralPath $Path -Recurse -Force)
    foreach ($item in $items) {
        if (($item.Attributes -band $readOnly) -ne 0) {
            $item.Attributes = $item.Attributes -band (-bnot $readOnly)
        }
    }
}

# Deletes a real folder (only ever after its contents were copied and checked), or just the link
# if the path is a link.
function Remove-Tree([string]$Path) {
    if (Test-Junction $Path) {
        [System.IO.Directory]::Delete($Path, $false)
        return
    }
    Clear-ReadOnly $Path
    [System.IO.Directory]::Delete($Path, $true)
}

# Renames a folder next to itself (one atomic step: it either fully happens or not at all).
function Set-Aside([string]$Path, [string]$Suffix) {
    $aside = "$Path.$Suffix"
    $n = 2
    while (Test-Path -LiteralPath $aside) { $aside = "$Path.$Suffix-$n"; $n++ }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
        $item.Attributes = $item.Attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)
    }
    [System.IO.Directory]::Move($Path, $aside)
    return $aside
}

# Deleting a folder that was set aside is tidying up, not part of the change: if it fails, the
# change still stands and the folder is reported so it can be deleted by hand.
function Remove-Leftover([string]$Path) {
    try {
        Remove-Tree $Path
    } catch {
        $reason = ($_.Exception.Message -replace '[\r\n]+', ' ')
        Write-Log "  could not delete the old folder $Path ($reason); it is safe to delete by hand"
        $warnings.Add("The old folder $Path could not be deleted; it is safe to delete by hand.")
    }
}

function Backup-Folder($Root, [string]$From, [string]$Name, [string]$What) {
    $destination = Join-Path $plan.BackupDir $Name
    $saved = Copy-Tree $From $destination
    $backupInfo.Add("Folder:$Name=$($Root.Name)|$What|$From|$($saved.Count)|$($saved.Bytes)")
    Write-Log "  backed up $($saved.Count) files from $From to $destination"
    return $saved
}

# This PC's folder -> the cloud folder, leaving a link behind.
function Enable-Root($Root, [string]$CloudPath, [bool]$AlreadyBackedUp) {
    $local = Get-FullPath $Root.Local
    $cloud = Get-FullPath $CloudPath

    if (Test-Junction $local) {
        $current = Get-FullPath (Get-JunctionTarget $local)
        if ($current -eq $cloud) { return "already linked to $cloud" }
        throw "it is already linked to '$current'"
    }

    if (-not (Test-Path -LiteralPath $local)) {
        New-Item -ItemType Directory -Force -Path $cloud | Out-Null
        New-Junction $local $cloud
        return "linked to $cloud (there was no folder on this PC)"
    }

    if (-not $AlreadyBackedUp) {
        Backup-Folder $Root $local $Root.Key 'this PC' | Out-Null
    }
    $saved = Measure-Tree $local

    $cloudFiles = (Measure-Tree $cloud).Count
    if ($cloudFiles -gt 0 -and $plan.WhenBothExist -ne 'Local') {
        $outcome = "linked to $cloud, which already had $cloudFiles files (kept); this PC's $($saved.Count) files are in the backup"
    } else {
        $aside = $null
        if ($cloudFiles -gt 0) {
            Backup-Folder $Root $cloud ($Root.Key + '-replaced-cloud') 'the cloud folder (replaced)' | Out-Null
            $aside = Set-Aside $cloud "replaced-$($plan.Stamp)"
            Write-Log "  the cloud copy that was there was renamed to $aside"
        }
        try {
            Copy-Tree $local $cloud | Out-Null
        } catch {
            $reason = $_.Exception.Message
            if (Test-Path -LiteralPath $cloud) { Remove-Tree $cloud } # only the partial copy just made
            if ($null -ne $aside) { [System.IO.Directory]::Move($aside, $cloud) }
            throw "could not copy this PC's folder to $cloud ($reason); nothing was changed"
        }
        $outcome = "moved $($saved.Count) files to $cloud"
    }

    $old = Set-Aside $local "cloudsave-old-$($plan.Stamp)"
    try {
        New-Junction $local $cloud
    } catch {
        $reason = $_.Exception.Message
        if (Test-Path -LiteralPath $local) { Remove-Tree $local }
        [System.IO.Directory]::Move($old, $local)
        throw "could not link '$local' ($reason); this PC's folder was put back"
    }
    Remove-Leftover $old
    return $outcome
}

# The cloud folder -> back onto this PC. The cloud copy is kept.
function Disable-Root($Root) {
    $local = Get-FullPath $Root.Local

    if (-not (Test-Junction $local)) {
        if (Test-Path -LiteralPath $local) { return 'already on this PC' }
        throw "'$local' is missing"
    }

    $cloud = Get-FullPath (Get-JunctionTarget $local)
    if (-not (Test-Path -LiteralPath $cloud)) {
        throw "it is linked to '$cloud', which is missing, so the link was left as it is"
    }

    $saved = Backup-Folder $Root $cloud $Root.Key 'the cloud folder'

    [System.IO.Directory]::Delete($local, $false) # removes only the link
    if (Test-Path -LiteralPath $local) { throw "could not remove the link '$local'" }

    $check = Measure-Tree $cloud
    if ($check.Count -ne $saved.Count -or $check.Bytes -ne $saved.Bytes) {
        New-Junction $local $cloud
        throw "the cloud folder changed while the link was removed, so the link was put back"
    }

    try {
        Copy-Tree $cloud $local | Out-Null
    } catch {
        $reason = $_.Exception.Message
        if (Test-Path -LiteralPath $local) { Remove-Tree $local }
        New-Junction $local $cloud
        throw "could not copy the files back ($reason); the link was put back"
    }
    Clear-ReadOnly $local
    return "copied $($saved.Count) files back to this PC; the cloud copy at $cloud was kept"
}

# A backup -> wherever the folder lives now (this PC, or the cloud folder it is linked to).
function Restore-Root($Root) {
    $source = $null
    foreach ($name in @($Root.Key, "$($Root.Key)-cloud", "$($Root.Key)-pc")) { # the last two are 0.2.0 names
        $candidate = Join-Path $plan.RestoreFrom $name
        if (Test-Path -LiteralPath $candidate) { $source = $candidate; break }
    }
    if ($null -eq $source) { return 'not in this backup, so it was left as it is' }

    $local = Get-FullPath $Root.Local
    $inCloud = Test-Junction $local
    if ($inCloud) { $target = Get-FullPath (Get-JunctionTarget $local) } else { $target = $local }
    $wanted = Measure-Tree $source

    $aside = $null
    if (Test-Path -LiteralPath $target) {
        Backup-Folder $Root $target $Root.Key 'the folder before the restore' | Out-Null
        $aside = Set-Aside $target "before-restore-$($plan.Stamp)"
    }

    try {
        Copy-Tree $source $target | Out-Null
    } catch {
        $reason = $_.Exception.Message
        if (Test-Path -LiteralPath $target) { Remove-Tree $target }
        if ($null -ne $aside) { [System.IO.Directory]::Move($aside, $target) }
        throw "could not restore the backup ($reason); nothing was changed"
    }
    Clear-ReadOnly $target

    if ($null -ne $aside) {
        if ($inCloud) {
            Write-Log "  the cloud copy that was there was renamed to $aside and kept (cloud copies are never deleted)"
        } else {
            Remove-Leftover $aside
        }
    }
    return "restored $($wanted.Count) files from $source to $target"
}

function New-Junction([string]$Link, [string]$Target) {
    $parent = Split-Path -Parent $Link
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    New-Item -ItemType Junction -Path $Link -Value $Target | Out-Null
    if (-not (Test-Junction $Link)) { throw "could not create the link '$Link'" }
    $actual = Get-JunctionTarget $Link
    if ((Get-FullPath $actual) -ne (Get-FullPath $Target)) {
        throw "the link '$Link' points to '$actual' instead of '$Target'"
    }
}

function Remove-OldRuns {
    $keepAlso = ''
    if ($plan.Action -eq 'Restore') { $keepAlso = Split-Path -Leaf $plan.RestoreFrom }
    Get-ChildItem -LiteralPath $plan.BackupsRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d{8}-\d{6}$' -and $_.Name -ne $keepAlso } |
        Sort-Object Name -Descending |
        Select-Object -Skip ([int]$plan.KeepBackups) |
        ForEach-Object {
            Write-Log "Removing old backup $($_.FullName)"
            Remove-Leftover $_.FullName
        }
    Get-ChildItem -LiteralPath (Split-Path -Parent $plan.LogPath) -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d{8}-\d{6}\.(log|plan\.json)$' } |
        Sort-Object Name -Descending |
        Select-Object -Skip 40 |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

$lock = Join-Path $plan.WorkDir 'helper.lock'
Set-Content -LiteralPath $lock -Value $PID -Encoding ASCII
try {
    Write-Log "CloudSave helper: $($plan.Action). Waiting for $($plan.GameName) (process $($plan.GamePid)) to close."
    $deadline = (Get-Date).AddSeconds([int]$plan.WaitSeconds)
    while (Get-Process -Id ([int]$plan.GamePid) -ErrorAction SilentlyContinue) {
        if ((Get-Date) -gt $deadline) { throw "the game did not close within $($plan.WaitSeconds) seconds" }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2

    if ($plan.Action -eq 'Restore' -and -not (Test-Path -LiteralPath $plan.RestoreFrom)) {
        throw "the backup '$($plan.RestoreFrom)' does not exist"
    }

    foreach ($root in $plan.Roots) {
        if (Get-Process -Name $plan.GameName -ErrorAction SilentlyContinue) {
            $ok = $false
            Write-Log "$($root.Name): skipped, because the game was started again"
            $rootResults.Add("Root:$($root.Key)=skipped|the game was started again before this folder was done")
            continue
        }
        Write-Log "$($root.Name):"
        try {
            switch ($plan.Action) {
                'Enable' { $message = Enable-Root $root $root.Cloud $false }
                'Disable' { $message = Disable-Root $root }
                'Switch' {
                    $first = Disable-Root $root
                    Write-Log "  $first"
                    $message = Enable-Root $root $root.Cloud $true
                }
                'Restore' { $message = Restore-Root $root }
                default { throw "unknown action '$($plan.Action)'" }
            }
            Write-Log "  $message"
            $rootResults.Add("Root:$($root.Key)=ok|$message")
        } catch {
            $ok = $false
            $reason = ($_.Exception.Message -replace '[\r\n]+', ' ')
            Write-Log "  FAILED: $reason"
            $rootResults.Add("Root:$($root.Key)=failed|$reason")
        }
    }

    Remove-OldRuns
} catch {
    $ok = $false
    $fatal = ($_.Exception.Message -replace '[\r\n]+', ' ')
    Write-Log "FAILED: $fatal"
} finally {
    $hasBackup = Test-Path -LiteralPath $plan.BackupDir
    if ($hasBackup) {
        $info = New-Object System.Collections.Generic.List[string]
        $info.Add("Date=$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))")
        $info.Add("Action=$($plan.Action)")
        $info.Add('About=Copies of each folder as it was before this change. Restore one with "Restore this backup" in CloudSave''s menu, or copy a folder back by hand while the game is closed.')
        foreach ($line in $backupInfo) { $info.Add($line) }
        Set-Content -LiteralPath (Join-Path $plan.BackupDir 'backup-info.txt') -Value $info -Encoding UTF8
        Write-Log "Backup of the folders before this change: $($plan.BackupDir)"
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("Stamp=$($plan.Stamp)")
    $lines.Add("Action=$($plan.Action)")
    $lines.Add("Ok=$($ok.ToString().ToLowerInvariant())")
    $lines.Add("Log=$($plan.LogPath)")
    if ($hasBackup) { $lines.Add("Backup=$($plan.BackupDir)") }
    if ($null -ne $fatal) { $lines.Add("Error=$fatal") }
    foreach ($line in $rootResults) { $lines.Add($line) }
    foreach ($warning in $warnings) { $lines.Add("Warning=$warning") }
    $tmp = "$($plan.ResultPath).tmp"
    Set-Content -LiteralPath $tmp -Value $lines -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $plan.ResultPath -Force
    Remove-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue
    if ($ok) { Write-Log 'Finished: all done.' } else { Write-Log 'Finished with errors.' }
}
