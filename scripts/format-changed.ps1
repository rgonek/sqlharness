#Requires -Version 7.0
<#
.SYNOPSIS
    Checks (or rewrites) C# formatting for the files a change actually touched.

.DESCRIPTION
    `dotnet format` spends nearly all of its time loading the MSBuild workspace,
    not formatting code: a full-solution run took 129s here, the same run scoped
    to the projects that changed took 34s. This script resolves the changed C#
    files first and skips the tool entirely when there are none, so a formatting
    check is free on an unchanged tree.

    Two scopes:
      working-tree (default) - staged, unstaged and untracked C# files under the
                               given repository.
      branch                 - C# files the current branch changed relative to
                               the merge base with -BaseRef (origin/main), which
                               is what CI sees for a pull request. Uncommitted
                               local edits are deliberately excluded.

    The full-solution check is not removed: .github/workflows/ci.yml still runs
    `dotnet format --verify-no-changes` over everything on ubuntu-latest. This
    script is the fast local gate, not a replacement.

.NOTES
    Requires git and the .NET SDK pinned by global.json. Exits with the exit code
    of `dotnet format`; exits 0 when there is nothing to check.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,

    [ValidateSet('working-tree', 'branch')]
    [string]$Mode = 'working-tree',

    [string]$BaseRef = 'origin/main',

    # Rewrite the files instead of only checking them.
    [switch]$Fix,

    # Print the resolved file list and exit without running dotnet.
    [switch]$ListOnly
)

$ErrorActionPreference = 'Stop'

# One invocation per batch keeps the command line clear of the Windows length
# limit when a branch rewrites hundreds of files.
$BatchSize = 200

function Invoke-Git {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $output = & git -C $Directory @Arguments 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = @($output)
    }
}

function Get-WorkingTreeCandidates {
    param([Parameter(Mandatory)][string]$Directory)

    $sources = @(
        (Invoke-Git -Directory $Directory -Arguments @('diff', '--name-only', '--diff-filter=d', 'HEAD')),
        (Invoke-Git -Directory $Directory -Arguments @('diff', '--name-only', '--diff-filter=d', '--cached')),
        (Invoke-Git -Directory $Directory -Arguments @('ls-files', '--others', '--exclude-standard'))
    )

    return ($sources | ForEach-Object { $_.Output }) | Where-Object { $_ -is [string] -and $_.Trim() }
}

function Get-BranchCandidates {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$Reference
    )

    $mergeBase = Invoke-Git -Directory $Directory -Arguments @('merge-base', 'HEAD', $Reference)
    if ($mergeBase.ExitCode -ne 0) {
        Write-Host "format: skipped (no merge base with '$Reference')"
        return @()
    }

    $base = ($mergeBase.Output | Select-Object -First 1).Trim()
    $changed = Invoke-Git -Directory $Directory -Arguments @('diff', '--name-only', '--diff-filter=d', "$base..HEAD")
    if ($changed.ExitCode -ne 0) {
        Write-Host "format: skipped (could not diff '$base..HEAD')"
        return @()
    }

    return $changed.Output | Where-Object { $_ -is [string] -and $_.Trim() }
}

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot '.git'))) {
    Write-Host "format: skipped ('$RepoRoot' is not a git working tree)"
    exit 0
}

$candidates = switch ($Mode) {
    'branch' { Get-BranchCandidates -Directory $RepoRoot -Reference $BaseRef }
    default { Get-WorkingTreeCandidates -Directory $RepoRoot }
}

$files = @($candidates |
    ForEach-Object { $_.Trim().Replace('\', '/') } |
    Where-Object { $_ -match '\.cs$' -and $_ -notmatch '(^|/)(obj|bin)/' } |
    Sort-Object -Unique)

if ($files.Count -eq 0) {
    Write-Host 'format: skipped (0 changed C# files)'
    exit 0
}

Write-Host "format: $($files.Count) changed C# file(s) ($Mode)"
if ($ListOnly) {
    # Output, not Write-Host: the gates call this script in-process and pipe the
    # result straight into --include.
    $files | ForEach-Object { Write-Output $_ }
    exit 0
}

$exitCode = 0
foreach ($batch in 0..([Math]::Ceiling($files.Count / $BatchSize) - 1)) {
    $start = $batch * $BatchSize
    $slice = @($files[$start..([Math]::Min($start + $BatchSize, $files.Count) - 1)])

    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.Add('format')
    $arguments.Add('SqlHarness.sln')
    $arguments.Add('--no-restore')
    $arguments.Add('--verbosity')
    $arguments.Add('quiet')
    $arguments.Add('--include')
    $arguments.AddRange([string[]]$slice)
    if (-not $Fix) { $arguments.Add('--verify-no-changes') }

    Push-Location $RepoRoot
    try {
        & dotnet @arguments
        $code = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($code -ne 0 -and $exitCode -eq 0) { $exitCode = $code }
}

if ($exitCode -eq 0) {
    Write-Host 'format: OK'
}
else {
    Write-Host "format: FAILED (dotnet format exit $exitCode)"
}

exit $exitCode