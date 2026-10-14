#Requires -Version 7.0
<#
.SYNOPSIS
    Shared agent-facing output for the local CI gates.

.DESCRIPTION
    A gate run is consumed by an agent first and a human second. Stage output
    therefore goes to a log file instead of the console: a green stage prints one
    line, a red stage prints the stage name, the exit code, a short tail and the
    log path. Success is a statement the gate makes, not something the reader
    infers from the absence of an error.

    Dot-source this file; it defines functions and runs nothing.

.NOTES
    Used by scripts/verify.ps1 and scripts/verify-linux.ps1.
#>

function Invoke-GateStage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$LogDirectory,

        # Report the stage as skipped without running it.
        [switch]$Skipped,

        [string]$SkipReason
    )

    if ($Skipped) {
        return [pscustomobject]@{
            Name       = $Name
            ExitCode   = 0
            Seconds    = 0.0
            LogPath    = $null
            Skipped    = $true
            SkipReason = $SkipReason
        }
    }

    if (-not (Test-Path -LiteralPath $LogDirectory)) {
        New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
    }
    $logPath = Join-Path $LogDirectory ("$Name.log")

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $global:LASTEXITCODE = 0
    $lines = [System.Collections.Generic.List[string]]::new()
    $exitCode = 0

    try {
        foreach ($item in (& $Action *>&1)) {
            if ($null -ne $item) { $lines.Add([string]$item) }
        }
        $exitCode = $global:LASTEXITCODE
    }
    catch {
        $lines.Add($_.Exception.Message)
        $exitCode = 1
    }
    finally {
        $stopwatch.Stop()
    }

    Set-Content -LiteralPath $logPath -Value $lines

    return [pscustomobject]@{
        Name       = $Name
        ExitCode   = [int]$exitCode
        Seconds    = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
        LogPath    = $logPath
        Skipped    = $false
        SkipReason = $null
    }
}

function Format-Duration {
    param([Parameter(Mandatory)][double]$Seconds)

    if ($Seconds -lt 60) { return ('{0:0.0}s' -f $Seconds) }
    return ('{0}m{1:00}s' -f [Math]::Floor($Seconds / 60), [int]($Seconds % 60))
}

function Get-StageLine {
    param([Parameter(Mandatory)]$Result)

    if ($Result.Skipped) {
        return "==> $($Result.Name) skipped ($($Result.SkipReason))"
    }
    if ($Result.ExitCode -ne 0) {
        return "==> $($Result.Name) FAILED (exit $($Result.ExitCode))"
    }
    return "==> $($Result.Name) ok $(Format-Duration -Seconds $Result.Seconds)"
}

function Get-StageFailureText {
    param(
        [Parameter(Mandatory)]$Result,
        [int]$TailLines = 40
    )

    if ($Result.Skipped -or $Result.ExitCode -eq 0) { return '' }
    if (-not (Test-Path -LiteralPath $Result.LogPath)) { return '' }

    $all = @(Get-Content -LiteralPath $Result.LogPath)
    if ($all.Count -eq 0) { return "full log: $($Result.LogPath)" }

    $tail = @($all | Select-Object -Last ([Math]::Min($TailLines, $all.Count)))
    $shown = $all.Count - $tail.Count

    $builder = [System.Text.StringBuilder]::new()
    if ($shown -gt 0) { [void]$builder.AppendLine("($shown earlier lines in the log)") }
    [void]$builder.AppendLine(($tail -join [Environment]::NewLine))
    [void]$builder.Append("full log: $($Result.LogPath)")
    return $builder.ToString()
}

function Get-GateSummaryLine {
    param(
        [Parameter(Mandatory)][string]$Gate,
        [Parameter(Mandatory)][int]$StageCount,
        [Parameter(Mandatory)][double]$Seconds,
        [string]$FailureStage,
        [int]$FailureExitCode = 0
    )

    $duration = Format-Duration -Seconds $Seconds
    if ([string]::IsNullOrWhiteSpace($FailureStage)) {
        return "$Gate`: OK  stages=$StageCount  $duration"
    }
    return "$Gate`: FAILED  stage=$FailureStage  exit=$FailureExitCode  $duration"
}