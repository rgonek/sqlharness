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