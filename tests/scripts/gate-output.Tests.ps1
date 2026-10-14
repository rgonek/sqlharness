# Pester 3.4-compatible unit tests for gate-output.ps1
# Pure helpers over throwaway log files; never runs a build, a test or dotnet.

$ErrorActionPreference = 'Stop'

$script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
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