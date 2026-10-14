#Requires -Version 7.0
<#
.SYNOPSIS
    Local CI gate: UI install and check, restore, build, test (without integration tests), and format check.

.DESCRIPTION
    Installs and checks the dashboard UI, then runs the same four .NET stages as
    the GitHub Actions CI workflow on SqlHarness.sln, stopping at the first
    failure. Excludes integration tests and does not touch ~/.sqlharness.

    Output is written for an agent first: every stage runs with its output in a
    log file, so a green stage prints one line and a red stage prints the stage,
    the exit code, a short tail and the log path. Use -Stream to watch a run.

    The format stage only checks the C# files the working tree changed; the full
    -solution check stays in CI. See scripts/format-changed.ps1.

.NOTES
    Requires the .NET SDK version pinned by global.json and the Node version
    pinned by .nvmrc (npm on PATH).
#>
[CmdletBinding()]
param(
    # Stream every stage instead of the compact per-stage summary.
    [switch]$Stream,

    # Where stage logs are written. Defaults to a directory under the temp path.
    [string]$LogDirectory
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'gate-output.ps1')

if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
    $LogDirectory = Join-Path ([System.IO.Path]::GetTempPath()) 'sqlharness-gate-logs\verify'
}

$script:stageCount = 0
$total = [System.Diagnostics.Stopwatch]::StartNew()

function Add-Stage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Action,
        [switch]$Skipped,
        [string]$SkipReason
    )

    $script:stageCount++
    $result = Invoke-GateStage -Name $Name -LogDirectory $LogDirectory -Action $Action -Skipped:$Skipped -SkipReason $SkipReason

    Write-Host (Get-StageLine -Result $result)
    if ($Stream -and -not $result.Skipped -and (Test-Path -LiteralPath $result.LogPath)) {
        Get-Content -LiteralPath $result.LogPath | ForEach-Object { Write-Host $_ }
    }

    if ($result.ExitCode -eq 0) { return }

    $failure = Get-StageFailureText -Result $result -TailLines 40
    if ($failure) { Write-Host $failure }
    Write-Host (Get-GateSummaryLine -Gate 'verify' -StageCount $script:stageCount -Seconds $total.Elapsed.TotalSeconds -FailureStage $Name -FailureExitCode $result.ExitCode)
    exit $result.ExitCode
}

Add-Stage -Name 'ui-install' -Action { npm ci --prefix src/SqlHarness.Dashboard/ui }
Add-Stage -Name 'ui-check' -Action { npm run check --prefix src/SqlHarness.Dashboard/ui }
Add-Stage -Name 'restore' -Action { dotnet restore SqlHarness.sln }
Add-Stage -Name 'build' -Action { dotnet build SqlHarness.sln --no-restore -warnaserror }
Add-Stage -Name 'test' -Action { dotnet test SqlHarness.sln --no-build --filter 'FullyQualifiedName!~Integration' }

# The list is resolved before the stage runs so an unchanged tree never pays for
# loading the MSBuild workspace inside dotnet format.
$changedFiles = @(& (Join-Path $PSScriptRoot 'format-changed.ps1') -ListOnly | Where-Object { $_ -match '\.cs$' })
Add-Stage -Name 'format' -Skipped:($changedFiles.Count -eq 0) -SkipReason '0 changed C# files' -Action {
    dotnet format SqlHarness.sln --no-restore --include $changedFiles --verify-no-changes
}

$total.Stop()
Write-Host (Get-GateSummaryLine -Gate 'verify' -StageCount $script:stageCount -Seconds $total.Elapsed.TotalSeconds)
exit 0