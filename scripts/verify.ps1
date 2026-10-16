#Requires -Version 7.0
<#
.SYNOPSIS
    Local CI gate: UI install, UI check beside the build, then build, test
    (without integration tests) and format check.

.DESCRIPTION
    Installs the dashboard UI, then runs the same three .NET stages as the GitHub
    Actions CI workflow - build -warnaserror on SqlHarness.sln (which restores
    implicitly), test, and format --verify-no-changes - stopping at the first
    failure. Excludes integration tests and does not touch ~/.sqlharness.

    The two test projects are named explicitly and started at the same time, so the test
    stage costs the slower project rather than the sum of both; handing dotnet the whole
    solution instead runs them one after the other. Every project writes its own log, and
    the failure tail names the project that failed.

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

        # One command, run by this process and logged to <name>.log.
        [scriptblock]$Action,

        # Several commands started at the same time, one test process per entry, each
        # logged to <name>-<project>.log. Projects are listed, never discovered.
        [string[]]$Project,
        [string[]]$Arguments,
        [switch]$Skipped,
        [string]$SkipReason,

        # Report the stage and hand the result back instead of stopping the gate. Used
        # where a stage overlaps another, so both results exist before either of them
        # decides what the gate does.
        [switch]$ReportOnly
    )

    # Exactly one of the two ways to run something. A stage given neither would run nothing
    # and report green; one given both would silently run only the first. Both are bugs in
    # this script, and a bug here must not reach the summary line as a result.
    if (($null -eq $Action) -eq ($null -eq $Project)) {
        throw "stage '$Name' must have exactly one of -Action or -Project"
    }

    $script:stageCount++
    # The parallel stage returns its own exit code and the log of the project that failed.
    # Routing it through Invoke-GateStage's scriptblock instead would throw both away and
    # leave the stage reporting whatever LASTEXITCODE happened to hold.
    $result = if ($null -ne $Project) {
        Invoke-ParallelGateStage -Name $Name -LogDirectory $LogDirectory -Project $Project -Arguments $Arguments -Skipped:$Skipped -SkipReason $SkipReason
    }
    else {
        Invoke-GateStage -Name $Name -LogDirectory $LogDirectory -Action $Action -Skipped:$Skipped -SkipReason $SkipReason
    }

    Write-Host (Get-StageLine -Result $result)
    if ($Stream) {
        # A parallel stage has one log per project, and its LogPath names only the failing
        # one, so a green stage would stream nothing at all if this read LogPath alone.
        foreach ($path in (Get-StageLogPaths -Result $result)) {
            if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path | ForEach-Object { Write-Host $_ } }
        }
    }

    if ($result.ExitCode -ne 0) {
        # The stage reports its own tail here, once. Stop-Gate prints the summary alone,
        # so a caller that decides a failure ends the gate cannot print it a second time.
        $failure = Get-StageFailureText -Result $result -TailLines 40
        if ($failure) { Write-Host $failure }
    }

    if ($ReportOnly) { return $result }
    if ($result.ExitCode -eq 0) { return }

    Stop-Gate -Name $Name -Result $result
}

# The summary line and the exit code, and nothing else: each stage has already printed
# its own line and its own failure tail by the time anything calls this. The function
# always exits, so a caller that wants to keep going has to keep the results itself.
function Stop-Gate {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)]$Result
    )

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
$uiCheck = $null
try {
    $uiCheck = Start-BackgroundGateStage -Name 'ui-check' -LogDirectory $LogDirectory -FilePath 'npm' `
        -Arguments @('run', 'check', '--prefix', 'src/SqlHarness.Dashboard/ui') -WorkingDirectory $repoRoot
}
catch {
    $uiCheckStartFailure = $_.Exception.Message
}

if ($null -eq $uiCheck) {
    # An unresolvable or unstartable npm throws out of Start-, and dying on the spot
    # would leave the run with no ui-check line, no log and no summary at all - less
    # than a foreground stage reported. Report it through the stage path, and do not
    # attempt the build: compiling cannot fix a missing npm, and before the overlap the
    # gate stopped here too. Add-Stage ends the run, so the overlap below never runs
    # with a process that was never started.
    $script:stageCount++
    Add-Stage -Name 'ui-check' -Action ([scriptblock]::Create("throw '" + $uiCheckStartFailure.Replace("'", "''") + "'"))
}
else {
    $script:stageCount++
    $buildResult = Add-Stage -Name 'build' -ReportOnly -Action { dotnet build SqlHarness.sln -warnaserror }
    $uiCheckResult = Complete-BackgroundGateStage -Name 'ui-check' -LogDirectory $LogDirectory -Process $uiCheck -Stream:$Stream

    # Both stages are reported before either stops the gate, so a UI failure is never
    # hidden behind a build failure. The build's code wins: it is the stage that ran
    # first, and it is the code the gate returns.
    if ($buildResult.ExitCode -ne 0) { Stop-Gate -Name 'build' -Result $buildResult }
    if ($uiCheckResult.ExitCode -ne 0) { Stop-Gate -Name 'ui-check' -Result $uiCheckResult }
}

# The test projects are named here rather than handed to dotnet as a solution, because that
# is what lets them run at the same time. The list is explicit on purpose: GateParityTests
# reads SqlHarness.sln and fails when a project appears there that neither gate names, so
# adding a test project cannot quietly leave it unrun.
Add-Stage -Name 'test' -Project @(
    'tests/SqlHarness.Tests/SqlHarness.Tests.csproj'
    'tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj'
) -Arguments @('--no-build', '--filter', 'FullyQualifiedName!~Integration')

# The list is resolved before the stage runs so an unchanged tree never pays for
# loading the MSBuild workspace inside dotnet format.
$changedFiles = @(& (Join-Path $PSScriptRoot 'format-changed.ps1') -ListOnly | Where-Object { $_ -match '\.cs$' })
Add-Stage -Name 'format' -Skipped:($changedFiles.Count -eq 0) -SkipReason '0 changed C# files' -Action {
    dotnet format SqlHarness.sln --no-restore --include $changedFiles --verify-no-changes
}

$total.Stop()
Write-Host (Get-GateSummaryLine -Gate 'verify' -StageCount $script:stageCount -Seconds $total.Elapsed.TotalSeconds)
exit 0