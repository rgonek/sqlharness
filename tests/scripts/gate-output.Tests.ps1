# Pester 3.4-compatible unit tests for gate-output.ps1
# Pure helpers over throwaway log files; never runs a build, a test or dotnet.

$ErrorActionPreference = 'Stop'

$script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$script:GateOutput = Join-Path $script:RepoRoot 'scripts\gate-output.ps1'
$script:GateOutput = Join-Path $script:RepoRoot 'scripts\gate-output.ps1'

. $script:GateOutput

function New-TempDirectory {
    param([string]$Prefix)

    $root = Join-Path ([System.IO.Path]::GetTempPath()) ($Prefix + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    return $root
}

function New-NoisyAction {
    param([string]$Message = 'noise')

    return { Write-Host $Message; Write-Output $Message }.GetNewClosure()
}

function New-FailingAction {
    param([int]$ExitCode = 3)

    return { & cmd.exe /c "exit $ExitCode" }.GetNewClosure()
}

# Write-Host writes to the information stream, so a gate's report is invisible to a test
# that only looks at the return value. Merging that stream into the success stream is the
# only way to observe what the gate actually printed; re-deriving the line with
# Get-StageLine instead would pass even if the gate stopped printing it altogether.
function Invoke-CapturingReport {
    param([Parameter(Mandatory)][scriptblock]$Action)

    $captured = @(& $Action 6>&1)

    return [pscustomobject]@{
        Printed  = @($captured | Where-Object { $_ -is [System.Management.Automation.InformationRecord] } | ForEach-Object { [string]$_.MessageData })
        Returned = @($captured | Where-Object { $_ -isnot [System.Management.Automation.InformationRecord] })[-1]
    }
}

# A stage result with a log file, standing in for what a finished stage hands to
# Get-ReportedFailureStage.
function New-StageResult {
    param(
        [Parameter(Mandatory)][string]$LogDirectory,
        [string]$Name = 'build',
        [int]$ExitCode = 1,
        [string[]]$Lines = @(),
        [switch]$Skipped
    )

    $logPath = Join-Path $LogDirectory "$Name.log"
    Set-Content -LiteralPath $logPath -Value $Lines
    return [pscustomobject]@{
        Name = $Name; ExitCode = $ExitCode; Seconds = 1.0
        LogPath = $logPath; Skipped = [bool]$Skipped; SkipReason = $null
    }
}

Describe 'gate-output.ps1 native command capture' {
    $work = $null

    BeforeEach { $work = New-TempDirectory -Prefix 'sqlharness-native-' }
    AfterEach { if ($work -and (Test-Path $work)) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue } }

    It 'captures the standard output of a native command' {
        $result = Invoke-NativeStageCommand -FilePath $env:ComSpec -Arguments @('/c', 'echo captured-line')

        $result.ExitCode | Should Be 0
        ($result.Output -join "`n") | Should Match 'captured-line'
    }

    It 'captures what a native command writes to standard error' {
        $result = Invoke-NativeStageCommand -FilePath $env:ComSpec -Arguments @('/c', 'echo oops 1>&2')

        ($result.Output -join "`n") | Should Match 'oops'
    }

    It 'returns the exit code of a native command' {
        $result = Invoke-NativeStageCommand -FilePath $env:ComSpec -Arguments @('/c', 'exit 7')

        $result.ExitCode | Should Be 7
    }

    It 'captures a command that floods standard error while writing to standard output' {
        # Reading stdout to the end before stderr deadlocks once the stderr pipe
        # buffer fills; the Linux gate hung on the UI check stage for 38 minutes.
        # The job bounds the test so a regression fails instead of hanging CI.
        $job = Start-Job -ScriptBlock {
            param($GateOutput)
            . $GateOutput
            $result = Invoke-NativeStageCommand -FilePath $env:ComSpec -Arguments @('/c', 'for /L %i in (1,1,20000) do @echo stderr-line-%i 1>&2 & echo stdout-line')
            return @{
                ExitCode  = $result.ExitCode
                Count     = $result.Output.Count
                HasStdout = (($result.Output -join "`n") -match 'stdout-line')
            }
        } -ArgumentList $script:GateOutput

        $finished = Wait-Job -Job $job -Timeout 120
        $isFinished = $null -ne $finished
        $payload = if ($isFinished) { Receive-Job -Job $job } else { $null }
        Stop-Job -Job $job -ErrorAction SilentlyContinue
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue

        $isFinished | Should Be $true
        $payload.Count | Should BeGreaterThan 19990
        $payload.HasStdout | Should Be $true
    }

    It 'never reports success for a command that could not start' {
        # wsl.exe writes outside PowerShell's redirection and a wedged WSL service
        # produced empty output with a zero exit code, which read as a green stage.
        $threw = $false
        try {
            $result = Invoke-NativeStageCommand -FilePath 'sqlharness-no-such-command-9f3a.exe' -Arguments @()
            if ($result.ExitCode -eq 0) { throw "a command that never started reported success" }
        }
        catch {
            $threw = $true
        }

        $threw | Should Be $true
    }
}

Describe 'gate-output.ps1' {
    $logDir = $null

    BeforeEach {
        $logDir = New-TempDirectory -Prefix 'sqlharness-gate-log-'
    }

    AfterEach {
        if ($logDir -and (Test-Path $logDir)) {
            Remove-Item -Recurse -Force $logDir -ErrorAction SilentlyContinue
        }
    }

    It 'returns the exit code of a successful stage and writes its output to a log' {
        $result = Invoke-GateStage -Name 'build' -LogDirectory $logDir -Action (New-NoisyAction -Message 'stage-noise')

        $result.ExitCode | Should Be 0
        Test-Path $result.LogPath | Should Be $true
        (Get-Content -Raw -LiteralPath $result.LogPath) | Should Match 'stage-noise'
    }

    It 'reports a successful stage as a single ok line' {
        $result = Invoke-GateStage -Name 'build' -LogDirectory $logDir -Action (New-NoisyAction)

        $line = Get-StageLine -Result $result

        $line | Should Match '==> build ok'
        $line | Should Match '\d+(\.\d+)?s'
    }

    It 'returns the failing exit code and names the stage on failure' {
        $result = Invoke-GateStage -Name 'test' -LogDirectory $logDir -Action (New-FailingAction -ExitCode 3)

        $result.ExitCode | Should Be 3
        $line = Get-StageLine -Result $result
        $line | Should Match '==> test FAILED'
        $line | Should Match 'exit 3'
    }

    It 'keeps stage output out of the console and shows only a tail on failure' {
        $long = 1..80 | ForEach-Object { "line $_" }
        $action = {
            $long | ForEach-Object { Write-Host $_ }
            & cmd.exe /c exit 3
        }.GetNewClosure()
        $result = Invoke-GateStage -Name 'test' -LogDirectory $logDir -Action $action

        $result.ExitCode | Should Be 3
        $text = Get-StageFailureText -Result $result -TailLines 5

        $text | Should Match 'line 80'
        $text | Should Match 'line 76'
        $text | Should Not Match 'line 75'
        $text | Should Match ([regex]::Escape($result.LogPath))
        # Everything is still in the log for when the tail is not enough.
        (Get-Content -Raw -LiteralPath $result.LogPath) | Should Match 'line 1\b'
    }

    It 'lets a stage action read caller variables that a stage name could shadow' {
        # PowerShell variable names are case-insensitive, so an internal accumulator
        # called $lines silently replaced the caller's $Lines. A Linux stage then ran
        # an empty script and reported green.
        $Lines = @('echo one', 'echo two')
        $result = Invoke-GateStage -Name 'stage' -LogDirectory $logDir -Action {
            $Lines | ForEach-Object { Write-Host $_ }
        }

        (Get-Content -Raw -LiteralPath $result.LogPath) | Should Match 'one'
        (Get-Content -Raw -LiteralPath $result.LogPath) | Should Match 'two'
    }

    It 'says nothing more than the ok line when a stage succeeds' {
        $result = Invoke-GateStage -Name 'format' -LogDirectory $logDir -Action (New-NoisyAction)

        Get-StageFailureText -Result $result -TailLines 40 | Should BeNullOrEmpty
    }

    It 'summarises a green gate in one unambiguous line' {
        Get-GateSummaryLine -Gate 'verify' -StageCount 6 -Seconds 123.4 | Should Match '^verify: OK {2}stages=6 {2}2m03s$'
    }

    It 'names the failing stage in a red gate summary' {
        $line = Get-GateSummaryLine -Gate 'verify-linux' -StageCount 6 -Seconds 61 -FailureStage 'build' -FailureExitCode 3

        $line | Should Match '^verify-linux: FAILED {2}stage=build {2}exit=3'
    }

    It 'reports a skipped stage instead of pretending it ran' {
        $result = Invoke-GateStage -Name 'format' -LogDirectory $logDir -Action { } -Skipped -SkipReason '0 changed C# files'

        $line = Get-StageLine -Result $result
        $result.ExitCode | Should Be 0
        $line | Should Match '==> format skipped'
        $line | Should Match '0 changed C# files'
    }

    It 'never reports a skipped stage as failed' {
        Get-StageFailureText -Result ([pscustomobject]@{
                Name = 'format'; ExitCode = 0; Seconds = 0.1; LogPath = 'x'; Skipped = $true; SkipReason = 'none'
            }) -TailLines 40 | Should BeNullOrEmpty
    }
}

Describe 'gate-output.ps1 background stages' {
    $logDir = $null

    BeforeEach {
        $logDir = New-TempDirectory -Prefix 'sqlharness-gate-bg-'
    }

    AfterEach {
        if ($logDir -and (Test-Path $logDir)) {
            Remove-Item -Recurse -Force $logDir -ErrorAction SilentlyContinue
        }
    }

    It 'returns a running process from Start and finishes it in Complete' {
        $process = Start-BackgroundGateStage -Name 'ui-check' -LogDirectory $logDir -FilePath $env:ComSpec -Arguments @('/c', 'echo background-line')

        $process | Should Not BeNullOrEmpty
        $result = Complete-BackgroundGateStage -Name 'ui-check' -LogDirectory $logDir -Process $process

        $result.ExitCode | Should Be 0
        $result.Name | Should Be 'ui-check'
        $result.LogPath | Should Be (Join-Path $logDir 'ui-check.log')
        (Get-Content -Raw -LiteralPath $result.LogPath) | Should Match 'background-line'
    }

    It 'reports a background stage with the same one-line summary a foreground stage gets' {
        $process = Start-BackgroundGateStage -Name 'build' -LogDirectory $logDir -FilePath $env:ComSpec -Arguments @('/c', 'echo build-noise')
        $report = Invoke-CapturingReport -Action {
            Complete-BackgroundGateStage -Name 'build' -LogDirectory $logDir -Process $process
        }

        # What the gate printed, not what Get-StageLine would have printed for the same
        # result: a test that re-derives the line passes even if the report is deleted.
        # The duration separator is the machine's culture decimal separator, not a dot.
        $report.Printed.Count | Should Be 1
        $report.Printed[0] | Should Match '^==> build ok \d+([.,]\d+)?s$'
        $report.Returned.ExitCode | Should Be 0
    }

    It 'replays the log with -Stream so a watcher still sees a background stage' {
        $process = Start-BackgroundGateStage -Name 'ui-check' -LogDirectory $logDir -FilePath $env:ComSpec -Arguments @('/c', 'echo streamed-ui-line')
        $report = Invoke-CapturingReport -Action {
            Complete-BackgroundGateStage -Name 'ui-check' -LogDirectory $logDir -Process $process -Stream
        }

        $report.Printed.Count | Should Be 2
        $report.Printed[0] | Should Match '^==> ui-check ok'
        $report.Printed[1] | Should Match 'streamed-ui-line'
    }

    It 'returns the failing exit code and prints the stage line and the failure tail' {
        $process = Start-BackgroundGateStage -Name 'ui-check' -LogDirectory $logDir -FilePath $env:ComSpec -Arguments @('/c', 'echo boom-marker 1>&2 & exit 4')
        $report = Invoke-CapturingReport -Action {
            Complete-BackgroundGateStage -Name 'ui-check' -LogDirectory $logDir -Process $process
        }

        $report.Returned.ExitCode | Should Be 4
        $report.Printed.Count | Should Be 2
        $report.Printed[0] | Should Match '^==> ui-check FAILED \(exit 4\)$'
        $report.Printed[1] | Should Match 'boom-marker'
        $report.Printed[1] | Should Match ([regex]::Escape($report.Returned.LogPath))
    }

    It 'prints the failure tail exactly once for a failed background stage' {
        # The gate prints the tail at the join and must not print it again when it decides
        # to stop, so the same tail text appears on the console exactly once.
        $process = Start-BackgroundGateStage -Name 'ui-check' -LogDirectory $logDir -FilePath $env:ComSpec -Arguments @('/c', 'echo once-only-marker 1>&2 & exit 4')
        $report = Invoke-CapturingReport -Action {
            Complete-BackgroundGateStage -Name 'ui-check' -LogDirectory $logDir -Process $process
        }

        ([regex]::Matches(($report.Printed -join "`n"), 'once-only-marker')).Count | Should Be 1
    }

    It 'drains a background stage that floods standard error while writing to standard output' {
        # Both pipes must be drained from the moment the process starts. Waiting first
        # and reading after deadlocks as soon as the stderr buffer fills, which hung the
        # UI check stage for 38 minutes in the foreground path.
        $process = Start-BackgroundGateStage -Name 'ui-check' -LogDirectory $logDir -FilePath $env:ComSpec -Arguments @('/c', 'for /L %i in (1,1,20000) do @echo stderr-line-%i 1>&2 & echo stdout-line')
        $result = Complete-BackgroundGateStage -Name 'ui-check' -LogDirectory $logDir -Process $process

        $log = Get-Content -Raw -LiteralPath $result.LogPath
        $log | Should Match 'stdout-line'
        ([regex]::Matches($log, 'stderr-line-').Count) | Should BeGreaterThan 19990
    }

    It 'reports a process the gate cannot start instead of a green stage' {
        $threw = $false
        try { $null = Start-BackgroundGateStage -Name 'ui-check' -LogDirectory $logDir -FilePath 'sqlharness-no-such-command-9f3a.exe' -Arguments @() }
        catch { $threw = $true }

        $threw | Should Be $true
    }

    It 'refuses to complete a process it never started' {
        # Completing a process with no pending read tasks would write an empty log and
        # report green for a stage that never ran.
        $threw = $false
        try {
            $foreign = Start-Process -FilePath $env:ComSpec -ArgumentList '/c', 'exit 0' -PassThru
            $null = Complete-BackgroundGateStage -Name 'ui-check' -LogDirectory $logDir -Process $foreign
        }
        catch { $threw = $true }

        $threw | Should Be $true
    }

    It 'removes its state even when the join throws' {
        # The entry holds two pending read tasks. A stale one would leak both and collide
        # with the next process id, so it is removed in a finally rather than on the way in.
        $before = $script:BackgroundGateStages.Count
        $process = Start-BackgroundGateStage -Name 'ui-check' -LogDirectory $logDir -FilePath $env:ComSpec -Arguments @('/c', 'echo state-line')
        $mid = $script:BackgroundGateStages.Count
        $null = Complete-BackgroundGateStage -Name 'ui-check' -LogDirectory $logDir -Process $process

        $mid | Should Be ($before + 1)
        $script:BackgroundGateStages.Count | Should Be $before
    }
}

# Invoke-ParallelGateStage launches `dotnet test <project> <arguments>`, so a test has to
# intercept dotnet itself rather than fake a project. It cannot name the command as a bare
# 'dotnet': Process.Start applies no PATHEXT and cannot launch a bare name, which is exactly
# why the stage resolves the command before starting anything. This fake records every
# argument line it is called with, and exits with the code mapped to the project it was
# asked about - different codes per project, so "the first project that failed" and "the
# last process joined" cannot be the same answer.
function New-FakeDotnetRoot {
    param(
        # Project text -> exit code. A project that is not mapped exits 0.
        [hashtable]$ExitCodes = @{}
    )

    $root = New-TempDirectory -Prefix 'sqlharness-fake-dotnet-'
    $calls = Join-Path $root 'calls.txt'
    $lines = @('@echo off', ">>`"$calls`" echo %*")
    foreach ($project in ($ExitCodes.Keys | Sort-Object)) {
        $lines += "echo %* | findstr /C:`"$project`" >nul && exit /b $($ExitCodes[$project])"
    }
    $lines += 'exit /b 0'
    Set-Content -LiteralPath (Join-Path $root 'dotnet.cmd') -Value ($lines -join "`r`n") -NoNewline

    return [pscustomobject]@{ Root = $root; Calls = $calls }
}

# Every project blocks until a release file exists, and records the moment it started. Two
# processes that both reach their start line before either is released were alive at the
# same time; a stage that started the second project only after joining the first would show
# one start line, because the first is still waiting for the release the test has not made
# yet. That is a structural proof of overlap rather than a timing comparison, so it does not
# weaken on a loaded machine.
function New-RendezvousFakeDotnetRoot {
    $root = New-TempDirectory -Prefix 'sqlharness-fake-meet-'
    $mark = Join-Path $root 'mark.txt'
    $release = Join-Path $root 'release.txt'
    $body = @"
@echo off
>>"$mark" echo start %2
:wait
if exist "$release" goto :done
ping -n 2 127.0.0.1 > nul
goto :wait
:done
>>"$mark" echo end %2
exit /b 0
"@
    Set-Content -LiteralPath (Join-Path $root 'dotnet.cmd') -Value $body -NoNewline
    return [pscustomobject]@{ Root = $root; Mark = $mark; Release = $release }
}

# One project floods standard error while writing to standard output. The other waits, so a
# stage that drains one project's pipes only after that project exits would deadlock here.
function New-LogFloodingFakeDotnetRoot {
    $root = New-TempDirectory -Prefix 'sqlharness-fake-flood-'
    $body = @'
@echo off
if "%2"=="tests/Flood/Flood.csproj" (
  for /L %%i in (1,1,20000) do @echo stderr-line-%%i 1>&2
  echo stdout-line
)
exit /b 0
'@
    Set-Content -LiteralPath (Join-Path $root 'dotnet.cmd') -Value $body -NoNewline
    return [pscustomobject]@{ Root = $root }
}

function Get-FakeDotnetCalls {
    param([Parameter(Mandatory)][string]$CallsFile)

    if (-not (Test-Path -LiteralPath $CallsFile)) { return @() }
    return @(Get-Content -LiteralPath $CallsFile)
}

Describe 'gate-output.ps1 parallel stages' {
    $logDir = $null
    $fake = $null
    $previousPath = $null

    BeforeEach {
        $logDir = New-TempDirectory -Prefix 'sqlharness-gate-par-'
        $previousPath = $env:PATH
    }

    AfterEach {
        $env:PATH = $previousPath
        foreach ($path in @($logDir, $(if ($fake) { $fake.Root }))) {
            if ($path -and (Test-Path $path)) { Remove-Item -Recurse -Force $path -ErrorAction SilentlyContinue }
        }
    }

    It 'runs every project and writes a log per project' {
        $fake = New-FakeDotnetRoot
        $env:PATH = $fake.Root + [IO.Path]::PathSeparator + $previousPath

        $result = Invoke-ParallelGateStage -Name 'test' -LogDirectory $logDir -Arguments @('--no-build', '--filter', 'FullyQualifiedName!~Integration') `
            -Project @('tests/A/A.csproj', 'tests/B/B.csproj')

        $result.Name | Should Be 'test'
        $result.ExitCode | Should Be 0
        $result.Skipped | Should Be $false
        $result.Seconds | Should BeGreaterThan 0
        $result.Projects.Count | Should Be 2
        Test-Path (Join-Path $logDir 'test-A.log') | Should Be $true
        Test-Path (Join-Path $logDir 'test-B.log') | Should Be $true

        # Both projects really ran, with the command the stage says it runs.
        $calls = Get-FakeDotnetCalls -CallsFile $fake.Calls
        $calls.Count | Should Be 2
        $calls[0] | Should Match '^test tests/A/A\.csproj --no-build --filter FullyQualifiedName!~Integration$'
        $calls[1] | Should Match '^test tests/B/B\.csproj --no-build --filter FullyQualifiedName!~Integration$'
    }

    It 'reports the exit code and the log of the project that failed' {
        $fake = New-FakeDotnetRoot -ExitCodes @{ 'tests/B/B.csproj' = 3 }
        $env:PATH = $fake.Root + [IO.Path]::PathSeparator + $previousPath

        $result = Invoke-ParallelGateStage -Name 'test' -LogDirectory $logDir -Arguments @('--no-build') `
            -Project @('tests/A/A.csproj', 'tests/B/B.csproj')

        $result.ExitCode | Should Be 3
        $result.LogPath | Should Be (Join-Path $logDir 'test-B.log')

        # The printed tail is the failing project's log, so it has to name the project and
        # its exit code on its own. The LAST line specifically: the gate prints a 40-line
        # tail, and a header at the top of a long log is never in it.
        $last = @(Get-Content -LiteralPath $result.LogPath)[-1]
        $last | Should Match 'tests/B/B\.csproj'
        $last | Should Match 'exit 3'
        (Get-StageLine -Result $result) | Should Match '^==> test FAILED \(exit 3\)$'
        (Get-StageFailureText -Result $result -TailLines 40) | Should Match 'tests/B/B\.csproj exit 3'
    }

    It 'reports the first project that failed, not the last process it joined' {
        # LASTEXITCODE holds whichever process was waited for last, so the stage cannot take
        # it from there. Both projects fail with DIFFERENT codes: a stage that returned the
        # last one, or the last one's log, would answer 4 here instead of 3.
        $fake = New-FakeDotnetRoot -ExitCodes @{ 'tests/A/A.csproj' = 3; 'tests/B/B.csproj' = 4 }
        $env:PATH = $fake.Root + [IO.Path]::PathSeparator + $previousPath

        $result = Invoke-ParallelGateStage -Name 'test' -LogDirectory $logDir -Arguments @('--no-build') `
            -Project @('tests/A/A.csproj', 'tests/B/B.csproj')

        $result.ExitCode | Should Be 3
        $result.LogPath | Should Be (Join-Path $logDir 'test-A.log')
        $codes = @($result.Projects | ForEach-Object { $_.ExitCode })
        $codes | Should Be @(3, 4)
    }

    It 'runs the projects at once rather than one after the other' {
        # A sequential stage would pass every other assertion in this block and still cost the
        # sum of both projects. Each fake project waits for a release file the test only
        # creates once BOTH have started, so one start line means the stage is sequential.
        $fake = New-RendezvousFakeDotnetRoot
        $job = Start-Job -ScriptBlock {
            param($GateOutput, $FakeRoot, $LogDir)
            . $GateOutput
            $env:PATH = $FakeRoot + [IO.Path]::PathSeparator + $env:PATH
            return Invoke-ParallelGateStage -Name 'test' -LogDirectory $LogDir `
                -Project @('tests/A/A.csproj', 'tests/B/B.csproj') -Arguments @('--no-build')
        } -ArgumentList $script:GateOutput, $fake.Root, $logDir

        $deadline = (Get-Date).AddSeconds(60)
        $started = 0
        while ((Get-Date) -lt $deadline -and $started -lt 2) {
            if (Test-Path -LiteralPath $fake.Mark) {
                $started = @(Get-Content -LiteralPath $fake.Mark | Where-Object { $_ -like 'start *' }).Count
            }
            if ($started -lt 2) { Start-Sleep -Milliseconds 200 }
        }
        # Released whether or not the second one arrived, so a sequential stage still ends
        # and the assertion below is what reports the problem.
        New-Item -ItemType File -Path $fake.Release -Force | Out-Null

        $finished = Wait-Job -Job $job -Timeout 60
        $isFinished = $null -ne $finished
        $payload = if ($isFinished) { Receive-Job -Job $job } else { $null }
        Stop-Job -Job $job -ErrorAction SilentlyContinue
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue

        $isFinished | Should Be $true
        $started | Should Be 2
        $payload.Projects.Count | Should Be 2
        $payload.ExitCode | Should Be 0
        Test-Path (Join-Path $logDir 'test-A.log') | Should Be $true
        Test-Path (Join-Path $logDir 'test-B.log') | Should Be $true
    }

    It 'reports a skipped parallel stage without starting anything' {
        $fake = New-FakeDotnetRoot
        $env:PATH = $fake.Root + [IO.Path]::PathSeparator + $previousPath

        $result = Invoke-ParallelGateStage -Name 'test' -LogDirectory $logDir -Arguments @('--no-build') `
            -Project @('tests/A/A.csproj') -Skipped -SkipReason '0 changed test projects'

        $result.Skipped | Should Be $true
        $result.ExitCode | Should Be 0
        $result.LogPath | Should BeNullOrEmpty
        $result.Projects.Count | Should Be 0
        (Get-StageLine -Result $result) | Should Match '^==> test skipped \(0 changed test projects\)$'
        (Get-FakeDotnetCalls -CallsFile $fake.Calls).Count | Should Be 0
    }

    It 'drains both pipes of a project that floods stderr while writing to stdout' {
        # Waiting for a project to exit before reading its pipes deadlocks as soon as the
        # stderr buffer fills, which hung the UI check stage for 38 minutes. The job bounds
        # the test so a regression fails instead of hanging the suite.
        $flood = New-LogFloodingFakeDotnetRoot
        $job = Start-Job -ScriptBlock {
            param($GateOutput, $FakeRoot, $LogDir)
            . $GateOutput
            $env:PATH = $FakeRoot + [IO.Path]::PathSeparator + $env:PATH
            $result = Invoke-ParallelGateStage -Name 'test' -LogDirectory $LogDir -Arguments @('--no-build') `
                -Project @('tests/Flood/Flood.csproj', 'tests/Quiet/Quiet.csproj')
            return @{ ExitCode = $result.ExitCode }
        } -ArgumentList $script:GateOutput, $flood.Root, $logDir

        $finished = Wait-Job -Job $job -Timeout 180
        $isFinished = $null -ne $finished
        $payload = if ($isFinished) { Receive-Job -Job $job } else { $null }
        Stop-Job -Job $job -ErrorAction SilentlyContinue
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue

        $isFinished | Should Be $true
        $payload.ExitCode | Should Be 0
        $log = Get-Content -Raw -LiteralPath (Join-Path $logDir 'test-Flood.log')
        $log | Should Match 'stdout-line'
        ([regex]::Matches($log, 'stderr-line-').Count) | Should BeGreaterThan 19990
    }

    It 'reports a stage whose command cannot start instead of a green stage' {
        # Process.Start applies no PATHEXT, so a PATH with nothing launchable on it leaves
        # the stage with no command to run. It must throw rather than report green.
        $fake = [pscustomobject]@{ Root = (New-TempDirectory -Prefix 'sqlharness-empty-path-') }
        $env:PATH = $fake.Root

        $threw = $false
        try {
            $result = Invoke-ParallelGateStage -Name 'test' -LogDirectory $logDir -Arguments @('--no-build') -Project @('tests/A/A.csproj')
            if ($result.ExitCode -eq 0) { throw 'a stage that never started reported success' }
        }
        catch {
            $threw = $true
        }

        $threw | Should Be $true
    }
}

Describe 'gate-output.ps1 reported failure stage' {
    $logDir = $null

    BeforeEach { $logDir = New-TempDirectory -Prefix 'sqlharness-gate-stage-' }
    AfterEach {
        if ($logDir -and (Test-Path $logDir)) { Remove-Item -Recurse -Force $logDir -ErrorAction SilentlyContinue }
    }

    It 'names the UI check when only the UI command failed' {
        # The Linux build stage runs the UI check and the build in one bash script. If it
        # returns the UI check's exit code, the summary must not read "stage=build".
        $result = New-StageResult -LogDirectory $logDir -ExitCode 7 -Lines @(
            '==> ui-check FAILED (exit 7)'
            'stage-failure: ui-check'
        )

        Get-ReportedFailureStage -Name 'build' -Result $result | Should Be 'ui-check'
    }

    It 'names the build when the build failed' {
        $result = New-StageResult -LogDirectory $logDir -ExitCode 3 -Lines @(
            '==> ui-check FAILED (exit 7)'
            '==> build FAILED (exit 3)'
            'stage-failure: build'
        )

        Get-ReportedFailureStage -Name 'build' -Result $result | Should Be 'build'
    }

    It 'falls back to the stage name when the log names nothing' {
        # Every other stage runs one command and never writes the marker.
        $result = New-StageResult -LogDirectory $logDir -Name 'test' -ExitCode 1 -Lines @('assertion failed')

        Get-ReportedFailureStage -Name 'test' -Result $result | Should Be 'test'
    }

    It 'falls back to the stage name for a green or skipped stage' {
        $green = New-StageResult -LogDirectory $logDir -ExitCode 0 -Lines @('stage-failure: ui-check')
        $skipped = New-StageResult -LogDirectory $logDir -ExitCode 0 -Lines @() -Skipped

        (Get-ReportedFailureStage -Name 'build' -Result $green) | Should Be 'build'
        (Get-ReportedFailureStage -Name 'build' -Result $skipped) | Should Be 'build'
    }
}