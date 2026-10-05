#Requires -Version 7.0
<#
.SYNOPSIS
    Runs the CI gate inside WSL on a case-sensitive Linux filesystem.

.DESCRIPTION
    Syncs the current branch into the disposable gate clone created by
    scripts/setup-linux-gate.ps1, then runs the same four stages CI runs:
    restore, build -warnaserror, test, and format --verify-no-changes.

    The test stage logs skip counts, which is the signal a Windows run cannot
    give. Stops at the first failing stage and exits with that stage's exit
    code, naming the stage.

    Fail-closed: this working tree is read and never written. The gate clone is
    the only thing modified. Never writes ~/.sqlharness and never connects to a
    database.

.NOTES
    Run scripts/setup-linux-gate.ps1 first. Override the distribution with
    -Distro.
#>
[CmdletBinding()]
param(
    [string]$Distro = 'Ubuntu'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$branch = (& git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) {
    throw 'Could not determine the current branch.'
}

$windowsRepo = (& wsl -d $Distro -- wslpath -a ($repoRoot -replace '\\', '/')).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($windowsRepo)) {
    throw "Could not translate the repository path '$repoRoot' into a WSL path."
}

$wslHome = (& wsl -d $Distro -- bash -lc 'printf %s "$HOME"').Trim()
$gateClone = "$wslHome/src/sqlharness-gate"

function Invoke-GateStage {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Lines,
        [string[]]$Arguments = @()
    )

    Write-Host "==> $Name"

    # PATH is exported here rather than sourced from ~/.bashrc: Ubuntu's stock
    # .bashrc returns early in a non-interactive shell, so an rc-file PATH would
    # silently not apply and the gate would fail for the wrong reason.
    $script = "set -euo pipefail`nexport PATH=`"`$HOME/.dotnet:`$PATH`"`n" + ($Lines -join "`n")

    # The block is materialised as an LF-only, BOM-less file instead of piped on
    # stdin: PowerShell injects CRLF into native-command stdin, and a trailing CR
    # corrupts the last command's arguments (it made `dotnet --version` fail in
    # scripts/setup-linux-gate.ps1). Windows separators are converted to forward
    # slashes before wslpath for the same class of reason: WSL parses a raw
    # backslash as a shell escape and eats it.
    $stageFile = New-TemporaryFile
    try {
        [System.IO.File]::WriteAllText(
            $stageFile.FullName,
            ($script -replace "`r`n", "`n"),
            [System.Text.UTF8Encoding]::new($false))
        $stageWslPath = (& wsl -d $Distro -- wslpath -a ($stageFile.FullName -replace '\\', '/')).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($stageWslPath)) {
            throw "Could not translate the stage script path into a WSL path."
        }

        & wsl -d $Distro -- bash $stageWslPath @Arguments
        $code = $LASTEXITCODE
    }
    finally {
        Remove-Item -LiteralPath $stageFile.FullName -ErrorAction SilentlyContinue
    }

    if ($code -ne 0) {
        Write-Host "verify-linux: stage '$Name' failed with exit code $code."
        exit $code
    }
}

Invoke-GateStage -Name 'sync' -Arguments @($gateClone, $windowsRepo, $branch) -Lines @(
    'cd "$1"',
    'git fetch "$2" "$3"',
    'git reset --hard FETCH_HEAD',
    'git clean -fd'
)

Invoke-GateStage -Name 'sdk' -Arguments @($gateClone) -Lines @(
    'cd "$1"',
    'dotnet --version'
)

Invoke-GateStage -Name 'restore' -Arguments @($gateClone) -Lines @(
    'cd "$1"',
    'dotnet restore SqlHarness.sln'
)

Invoke-GateStage -Name 'build' -Arguments @($gateClone) -Lines @(
    'cd "$1"',
    'dotnet build SqlHarness.sln --no-restore -warnaserror'
)

Invoke-GateStage -Name 'test' -Arguments @($gateClone) -Lines @(
    'cd "$1"',
    'dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName!~Integration" --logger "console;verbosity=normal"'
)

Invoke-GateStage -Name 'format' -Arguments @($gateClone) -Lines @(
    'cd "$1"',
    'dotnet format SqlHarness.sln --no-restore --verify-no-changes'
)

Write-Host 'verify-linux: OK'
