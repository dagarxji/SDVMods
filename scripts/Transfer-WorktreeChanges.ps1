<#
.SYNOPSIS
    Transfers changes from another git worktree into the current branch's working directory,
    without merging.

.DESCRIPTION
    This repo is commonly checked out as multiple git worktrees (e.g. one per agent/session
    under a sibling *.worktrees folder). This script lists the other worktrees attached to the
    repository, lets you pick one from an interactive menu, computes the full diff of that
    worktree relative to its merge-base with the current branch - including any uncommitted or
    untracked changes still sitting in that worktree - and applies that diff to the current
    working directory as a plain patch.

    Nothing is merged: no branch merge, no merge commit. The result is just the file changes
    from the source worktree, applied on top of whatever is currently checked out here, left
    unstaged (unless -Stage is passed) so you can review before committing.

.PARAMETER SourcePath
    Optional. Path to the worktree to transfer changes from. If omitted, an interactive menu of
    the other known worktrees is shown.

.PARAMETER Stage
    Optional switch. If set, the applied changes are also staged (git apply --index).

.EXAMPLE
    ./scripts/Transfer-WorktreeChanges.ps1
    Shows an interactive menu of other worktrees and applies the chosen one's changes here.

.EXAMPLE
    ./scripts/Transfer-WorktreeChanges.ps1 -SourcePath 'C:\Users\me\Desktop\SDVMods.worktrees\some-branch'
    Applies changes from the given worktree without prompting for a selection.
#>
[CmdletBinding()]
param(
    [string]$SourcePath,
    [switch]$Stage
)

$ErrorActionPreference = 'Stop'

function Get-NormalizedPath {
    param([Parameter(Mandatory)][string]$Path)
    return ($Path -replace '/', '\').TrimEnd('\')
}

function Write-GitDiffToFile {
    # Writes git's raw stdout bytes straight to a file. Piping through PowerShell
    # (Out-File/Set-Content) re-splits output into lines and rewrites it with CRLF and a BOM,
    # which corrupts LF-based patches - this copies the stream unmodified instead.
    param(
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string[]]$GitArgs,
        [Parameter(Mandatory)][string]$OutFile,
        [switch]$Append
    )
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = 'git'
    $psi.WorkingDirectory = $WorkingDirectory
    # Windows PowerShell 5.1's ProcessStartInfo.ArgumentList is unavailable (null), so build a
    # properly quoted argument string instead.
    $psi.Arguments = ($GitArgs | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }) -join ' '
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $mode = if ($Append) { [System.IO.FileMode]::Append } else { [System.IO.FileMode]::Create }
    $fileStream = [System.IO.File]::Open($OutFile, $mode, [System.IO.FileAccess]::Write)
    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
        $proc.StandardOutput.BaseStream.CopyTo($fileStream)
        $proc.WaitForExit()
        return $proc.ExitCode
    } finally {
        $fileStream.Close()
    }
}

function Get-Worktrees {
    $raw = git worktree list --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'Failed to list git worktrees.' }

    $worktrees = @()
    $current = $null
    foreach ($line in $raw) {
        if ($line -like 'worktree *') {
            if ($current) { $worktrees += [pscustomobject]$current }
            $current = @{ Path = Get-NormalizedPath ($line.Substring(9).Trim()); Branch = $null; Head = $null }
        } elseif ($line -like 'branch *') {
            $current.Branch = $line.Substring(7).Trim() -replace '^refs/heads/', ''
        } elseif ($line -like 'HEAD *') {
            $current.Head = $line.Substring(5).Trim()
        } elseif ($line -eq 'detached') {
            $current.Branch = '(detached)'
        }
    }
    if ($current) { $worktrees += [pscustomobject]$current }
    return $worktrees
}

function Show-ArrowMenu {
    param(
        [Parameter(Mandatory)][string[]]$Options,
        [string]$Title = 'Select an option'
    )

    $selected = 0
    $originalVisible = [Console]::CursorVisible
    [Console]::CursorVisible = $false
    try {
        while ($true) {
            Clear-Host
            Write-Host $Title -ForegroundColor Cyan
            Write-Host 'Use Up/Down arrows (or number keys) and Enter to choose, Esc to cancel.' -ForegroundColor DarkGray
            Write-Host ''
            for ($i = 0; $i -lt $Options.Count; $i++) {
                $prefix = if ($i -eq $selected) { '>' } else { ' ' }
                $label = '{0} [{1}] {2}' -f $prefix, ($i + 1), $Options[$i]
                if ($i -eq $selected) {
                    Write-Host $label -ForegroundColor Black -BackgroundColor White
                } else {
                    Write-Host $label
                }
            }

            $key = [Console]::ReadKey($true)
            switch ($key.Key) {
                'UpArrow'   { $selected = ($selected - 1 + $Options.Count) % $Options.Count }
                'DownArrow' { $selected = ($selected + 1) % $Options.Count }
                'Enter'     { return $selected }
                'Escape'    { return -1 }
                default {
                    if ($key.KeyChar -match '^[1-9]$') {
                        $idx = [int]"$($key.KeyChar)" - 1
                        if ($idx -ge 0 -and $idx -lt $Options.Count) { return $idx }
                    }
                }
            }
        }
    } finally {
        [Console]::CursorVisible = $originalVisible
        Clear-Host
    }
}

# --- Resolve current worktree/branch --------------------------------------------------------
$currentPath = (git rev-parse --show-toplevel)
if ($LASTEXITCODE -ne 0) { throw 'Not inside a git repository.' }
$currentPath = Get-NormalizedPath (Resolve-Path $currentPath).Path
$currentBranch = (git rev-parse --abbrev-ref HEAD).Trim()

# --- Resolve the source worktree to transfer changes from -----------------------------------
if ($SourcePath) {
    $resolvedSource = Get-NormalizedPath (Resolve-Path $SourcePath).Path
    $match = Get-Worktrees | Where-Object { $_.Path -eq $resolvedSource }
    if (-not $match) { throw "'$SourcePath' is not a registered git worktree of this repository." }
    $source = $match
} else {
    $candidates = @(Get-Worktrees | Where-Object { $_.Path -ne $currentPath })
    if ($candidates.Count -eq 0) {
        throw 'No other git worktrees found to transfer changes from.'
    }

    $labels = $candidates | ForEach-Object { '{0}  ({1})' -f $_.Branch, $_.Path }
    $choice = Show-ArrowMenu -Options $labels -Title "Transfer changes into '$currentBranch' from which worktree?"
    if ($choice -lt 0) {
        Write-Host 'Cancelled.' -ForegroundColor Yellow
        exit 0
    }
    $source = $candidates[$choice]
}

$sourcePath = $source.Path
Write-Host ''
Write-Host "Source worktree: $sourcePath (branch: $($source.Branch))" -ForegroundColor Cyan
Write-Host "Target branch:   $currentBranch ($currentPath)" -ForegroundColor Cyan

if ($sourcePath -eq $currentPath) {
    throw 'Source and target worktree are the same.'
}

# --- Compute merge base -----------------------------------------------------------------------
$mergeBase = (git merge-base HEAD $source.Head).Trim()
if ($LASTEXITCODE -ne 0 -or -not $mergeBase) {
    throw "Could not determine a common ancestor between HEAD and $($source.Head)."
}

# --- Build the combined patch (tracked + uncommitted + untracked changes) ---------------------
$patchPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "transfer-$([guid]::NewGuid()).patch")
$intentAdded = @()
$success = $false
try {
    Write-GitDiffToFile -WorkingDirectory $sourcePath -GitArgs @('diff', '--binary', $mergeBase) -OutFile $patchPath

    $untracked = @(git -C $sourcePath ls-files --others --exclude-standard)
    if ($untracked.Count -gt 0) {
        git -C $sourcePath add --intent-to-add -- $untracked
        $intentAdded = $untracked
        Write-GitDiffToFile -WorkingDirectory $sourcePath -GitArgs (@('diff', '--binary', $mergeBase, '--') + $untracked) -OutFile $patchPath -Append
    }

    if ((Get-Item $patchPath).Length -eq 0) {
        Write-Host "No differences found between '$currentBranch' and '$($source.Branch)'. Nothing to transfer." -ForegroundColor Yellow
        $success = $true
        exit 0
    }

    Write-Host ''
    Write-Host 'Changes to be applied:' -ForegroundColor Cyan
    git apply --stat $patchPath

    $confirm = Read-Host "Apply these changes to '$currentBranch'? (y/N)"
    if ($confirm -notmatch '^[Yy]') {
        Write-Host 'Cancelled.' -ForegroundColor Yellow
        $success = $true
        exit 0
    }

    $applyArgs = @('apply', '--3way')
    if ($Stage) { $applyArgs += '--index' }
    $applyArgs += $patchPath

    Push-Location $currentPath
    try {
        git @applyArgs
        if ($LASTEXITCODE -ne 0) {
            throw "git apply failed - resolve the reported conflicts manually, or inspect the saved patch at: $patchPath"
        }

        if (-not $Stage) {
            # --3way stages any paths it merges via the index, even without --index. Unstage
            # just the touched files so the result matches plain "unstaged working tree edits"
            # unless the caller explicitly asked to stage (-Stage).
            $touchedFiles = @(git apply --numstat $patchPath | ForEach-Object { ($_ -split "`t")[2] } | Where-Object { $_ })
            if ($touchedFiles.Count -gt 0) {
                git reset -- $touchedFiles | Out-Null
            }
        }
    } finally {
        Pop-Location
    }

    $success = $true
    Write-Host ''
    Write-Host "Changes from '$($source.Branch)' applied to '$currentBranch' (not committed)." -ForegroundColor Green
} finally {
    if ($intentAdded.Count -gt 0) {
        git -C $sourcePath reset -- $intentAdded | Out-Null
    }
    if ($success -and (Test-Path $patchPath)) {
        Remove-Item $patchPath -ErrorAction SilentlyContinue
    }
}
