#Requires -Version 7.0
<#
.SYNOPSIS
    Local CI gate: UI install, UI check beside the build, then build, test
    (without integration tests) and format check.

.DESCRIPTION
    Installs the dashboard UI, then runs the same three .NET stages as the GitHub
    Actions CI workflow on SqlHarness.sln - build -warnaserror (which restores
    implicitly), test, and format --verify-no-changes - stopping at the first
    failure. Excludes integration tests and does not touch ~/.sqlharness.

    Output is written for an agent first: every stage runs with its output in a
    log file, so a green stage prints one line and a red stage prints the stage,
    the exit code, a short tail and the log path. Use -Stream to watch a run.

    The stage lines report in the order the stages finish, not the order they
    start. The UI check needs no .NET artifacts, so it runs in the background
    beside the build and is joined before the test stage, which is the
    memory-hungry one on a box with 8 logical cores; it therefore prints its line
    after the build it overlapped. ui-install stays a foreground stage so its
    skip reason is reported before anything starts.

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
        [string]$SkipReason,

        # Report the stage and hand the result back instead of stopping the gate. Used
        # where a stage overlaps another, so both results exist before either of them
        # decides what the gate does.
        [switch]$ReportOnly
    )

    $script:stageCount++
    $result = Invoke-GateStage -Name $Name -LogDirectory $LogDirectory -Action $Action -Skipped:$Skipped -SkipReason $SkipReason

    Write-Host (Get-StageLine -Result $result)
    if ($Stream -and -not $result.Skipped -and (Test-Path -LiteralPath $result.LogPath)) {
        Get-Content -LiteralPath $result.LogPath | ForEach-Object { Write-Host $_ }
    }

    if ($ReportOnly) { return $result }
    if ($result.ExitCode -eq 0) { return }

    Stop-Gate -Name $Name -Result $result
}

# Named separately from Add-Stage so the overlap site can report both stages and only
# then decide. The function always exits; a caller that wants to keep going must catch
# the results itself.
function Stop-Gate {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)]$Result
    )

    $failure = Get-StageFailureText -Result $Result -TailLines 40
    if ($failure) { Write-Host $failure }
    Write-Host (Get-GateSummaryLine -Gate 'verify' -StageCount $script:stageCount -Seconds $total.Elapsed.TotalSeconds -FailureStage $Name -FailureExitCode $Result.ExitCode)
    exit $Result.ExitCode
}

# npm ci deletes and reinstalls node_modules, which cost 64s on every run. The
# install runs only when node_modules is missing or the lockfile changed.
. (Join-Path $PSScriptRoot 'ui-install.ps1')
$uiState = Get-UiInstallState -UiDirectory (Join-Path $repoRoot 'src\SqlHarness.Dashboard\ui')
Add-Stage -Name 'ui-install' -Skipped:$uiState.Installed -SkipReason $uiState.Reason -Action {
    $install = Install-UiDependencies -UiDirectory (Join-Path $repoRoot 'src\SqlHarness.Dashboard\ui')
    if ($install.ExitCode -ne 0) { exit $install.ExitCode }
}

# ui-check needs no .NET artifacts, so it runs beside the build instead of in front of
# it. WorkingDirectory is the repository root so the --prefix argument means the same
# thing here as it does when the gate is launched from the root.
$uiCheck = Start-BackgroundGateStage -Name 'ui-check' -LogDirectory $LogDirectory -FilePath 'npm' `
    -Arguments @('run', 'check', '--prefix', 'src/SqlHarness.Dashboard/ui') -WorkingDirectory $repoRoot
$script:stageCount++
$buildResult = Add-Stage -Name 'build' -ReportOnly -Action { dotnet build SqlHarness.sln -warnaserror }
$uiCheckResult = Complete-BackgroundGateStage -Name 'ui-check' -LogDirectory $LogDirectory -Process $uiCheck -Stream:$Stream

# Both stages are reported before either stops the gate, so a UI failure is never
# hidden behind a build failure. The build's code wins: it is the stage that ran first.
if ($buildResult.ExitCode -ne 0) { Stop-Gate -Name 'build' -Result $buildResult }
if ($uiCheckResult.ExitCode -ne 0) { Stop-Gate -Name 'ui-check' -Result $uiCheckResult }

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