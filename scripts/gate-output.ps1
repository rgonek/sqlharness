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

function Invoke-NativeStageCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory
    )

    # wsl.exe writes to the console directly, so PowerShell's own redirection drops
    # its output while still reporting exit code 0 - a green stage that ran nothing.
    # Redirecting the process itself is the only way to see what actually happened.
    $stdoutPath = [System.IO.Path]::GetTempFileName()
    $stderrPath = [System.IO.Path]::GetTempFileName()
    try {
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $FilePath
        foreach ($argument in $Arguments) { $null = $startInfo.ArgumentList.Add($argument) }
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        if ($WorkingDirectory) { $startInfo.WorkingDirectory = $WorkingDirectory }

        $process = [System.Diagnostics.Process]::Start($startInfo)
        if ($null -eq $process) {
            throw "'$FilePath' could not be started. A stage that cannot start must never look green."
        }

        # Both pipes are drained concurrently. Reading one to the end before the
        # other deadlocks as soon as the second pipe buffer fills, which hung the
        # UI check stage for 38 minutes.
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()

        $lines = [System.Collections.Generic.List[string]]::new()
        if (-not [string]::IsNullOrEmpty($stdout)) { $stdout.TrimEnd("`r", "`n") -split "`r?`n" | ForEach-Object { $lines.Add($_) } }
        if (-not [string]::IsNullOrEmpty($stderr)) { $stderr.TrimEnd("`r", "`n") -split "`r?`n" | ForEach-Object { $lines.Add($_) } }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output   = $lines.ToArray()
        }
    }
    finally {
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -ErrorAction SilentlyContinue
    }
}

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
    # Deliberately not named $lines or $action: PowerShell variable names are
    # case-insensitive, so those would shadow the caller's variables inside a stage
    # scriptblock and quietly run an empty stage.
    $capturedOutput = [System.Collections.Generic.List[string]]::new()
    $exitCode = 0

    try {
        foreach ($item in (& $Action *>&1)) {
            if ($null -ne $item) { $capturedOutput.Add([string]$item) }
        }
        $exitCode = $global:LASTEXITCODE
    }
    catch {
        $capturedOutput.Add($_.Exception.Message)
        $exitCode = 1
    }
    finally {
        $stopwatch.Stop()
    }

    Set-Content -LiteralPath $logPath -Value $capturedOutput

    return [pscustomobject]@{
        Name       = $Name
        ExitCode   = [int]$exitCode
        Seconds    = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
        LogPath    = $logPath
        Skipped    = $false
        SkipReason = $null
    }
}

# Keyed by process id, and only ever written by Start-BackgroundGateStage. The reads
# have to be in flight before the caller starts the stage it is overlapping, so they
# cannot be reconstructed from the process alone at join time.
$script:BackgroundGateStages = @{}

function Resolve-GateStageExecutable {
    param([Parameter(Mandatory)][string]$FilePath)

    # Process.Start applies no PATHEXT: 'npm' does not resolve the way `npm run` does in
    # PowerShell, and the bare name is not something CreateProcess can launch. Only a
    # real executable path is accepted, so a stage that cannot start fails loudly.
    if (Test-Path -LiteralPath $FilePath -PathType Leaf) {
        return (Resolve-Path -LiteralPath $FilePath).ProviderPath
    }

    $launchable = @('.exe', '.com', '.cmd', '.bat')
    $command = Get-Command -Name $FilePath -CommandType Application -ErrorAction SilentlyContinue |
        Where-Object { $launchable -contains [System.IO.Path]::GetExtension($_.Source) } |
        Select-Object -First 1
    if ($command) { return $command.Source }

    throw "'$FilePath' could not be resolved to an executable on PATH. A stage that cannot start must never look green."
}

function Start-BackgroundGateStage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$LogDirectory,
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory
    )

    # The command is a native executable rather than a scriptblock because the only
    # overlapped stage is `npm run check`: a scriptblock would make the caller capture
    # and redirect its own output, which is the part that has to be shared.
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = Resolve-GateStageExecutable -FilePath $FilePath
    foreach ($argument in $Arguments) { $null = $startInfo.ArgumentList.Add($argument) }
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    if ($WorkingDirectory) { $startInfo.WorkingDirectory = $WorkingDirectory }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "'$FilePath' could not be started. A stage that cannot start must never look green."
    }

    # Both reads start here, before the caller runs anything else. Reading one to the
    # end before the other deadlocks as soon as the second pipe buffer fills, which hung
    # the UI check stage for 38 minutes; Complete- only collects what these two have
    # already gathered.
    try {
        $script:BackgroundGateStages[$process.Id] = [pscustomobject]@{
            Stopwatch      = [System.Diagnostics.Stopwatch]::StartNew()
            StandardOutput = $process.StandardOutput.ReadToEndAsync()
            StandardError  = $process.StandardError.ReadToEndAsync()
        }
    }
    catch {
        # A running process nothing tracks would fill a pipe nobody drains and survive the
        # gate, so a stage that cannot be registered is killed rather than orphaned.
        if (-not $script:BackgroundGateStages.ContainsKey($process.Id)) {
            try { $process.Kill($true) } catch { }
        }
        throw
    }

    return $process
}

function Complete-BackgroundGateStage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$LogDirectory,
        [Parameter(Mandatory)][System.Diagnostics.Process]$Process,

        # Replay the stage's output the way -Stream does for a foreground stage.
        [switch]$Stream
    )

    $state = $null
    if ($null -ne $script:BackgroundGateStages -and $script:BackgroundGateStages.ContainsKey($Process.Id)) {
        $state = $script:BackgroundGateStages[$Process.Id]
    }
    if ($null -eq $state) {
        # Joining a process Start- never returned would write an empty log and report
        # green for a stage that never ran.
        throw "'$Name' was completed with a process Start-BackgroundGateStage did not return. A stage that never started must never look green."
    }

    try {
        $Process.WaitForExit()
        $stdout = $state.StandardOutput.GetAwaiter().GetResult()
        $stderr = $state.StandardError.GetAwaiter().GetResult()
        $state.Stopwatch.Stop()

        if (-not (Test-Path -LiteralPath $LogDirectory)) {
            New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
        }
        $logPath = Join-Path $LogDirectory ("$Name.log")

        $capturedOutput = [System.Collections.Generic.List[string]]::new()
        if (-not [string]::IsNullOrEmpty($stdout)) { $stdout.TrimEnd("`r", "`n") -split "`r?`n" | ForEach-Object { $capturedOutput.Add($_) } }
        if (-not [string]::IsNullOrEmpty($stderr)) { $stderr.TrimEnd("`r", "`n") -split "`r?`n" | ForEach-Object { $capturedOutput.Add($_) } }
        Set-Content -LiteralPath $logPath -Value $capturedOutput

        $result = [pscustomobject]@{
            Name       = $Name
            ExitCode   = [int]$Process.ExitCode
            Seconds    = [Math]::Round($state.Stopwatch.Elapsed.TotalSeconds, 1)
            LogPath    = $logPath
            Skipped    = $false
            SkipReason = $null
        }

        # The same one line and the same failure tail a foreground stage prints, so an
        # overlapped stage is indistinguishable in the transcript from one that ran
        # alone. This is the stage's only report: whoever decides what a failure means
        # must not print the tail a second time.
        Write-Host (Get-StageLine -Result $result)
        if ($Stream) { Get-Content -LiteralPath $logPath | ForEach-Object { Write-Host $_ } }
        if ($result.ExitCode -ne 0) {
            $failure = Get-StageFailureText -Result $result -TailLines 40
            if ($failure) { Write-Host $failure }
        }

        return $result
    }
    finally {
        # The entry holds two pending read tasks. Removing it here rather than before the
        # work means a throw anywhere in the join cannot leave a stale entry behind to
        # leak both tasks and collide with the next process id.
        $script:BackgroundGateStages.Remove($Process.Id)
    }
}

function Get-ReportedFailureStage {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)]$Result
    )

    # A stage that runs more than one command in a single script names the command whose
    # exit code it returned. Reporting the wrapper's name instead would let a UI-only
    # failure read "stage=build" with the UI check's exit code beside it.
    if ($Result.ExitCode -eq 0 -or $Result.Skipped -or [string]::IsNullOrWhiteSpace($Result.LogPath)) { return $Name }
    if (-not (Test-Path -LiteralPath $Result.LogPath)) { return $Name }

    $marker = Select-String -LiteralPath $Result.LogPath -Pattern '(?m)^stage-failure: (?<stage>\S+)$' |
        Select-Object -First 1
    if ($marker) { return $marker.Matches[0].Groups['stage'].Value }

    return $Name
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