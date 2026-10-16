# Session-scoped Grok trials against the test-only workflow host.
# Does not edit ~/.grok/config.toml. Restores trusted_folders.toml byte-for-byte.
[CmdletBinding()]
param(
    [ValidateSet("prechange", "postchange")]
    [string] $Phase = "prechange",
    [string] $Model = "grok-4.6",
    [string] $Effort = "medium",
    [int] $MaxTurns = 20,
    [int] $TimeoutSeconds = 300,
    [string] $HostDll = "",
    [string] $OutDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..\")).Path
$fixtureRoot = $PSScriptRoot
$builtHost = Join-Path $repo "tests\SqlHarness.Mcp.WorkflowHost\bin\Debug\net10.0\SqlHarness.Mcp.WorkflowHost.dll"
if (-not $HostDll) {
    $hostCopy = Join-Path ([System.IO.Path]::GetTempPath()) "sqlharness-077-host"
    if (-not (Test-Path -LiteralPath $builtHost)) {
        throw "Workflow host is missing at $builtHost. Build tests/SqlHarness.Mcp.WorkflowHost first."
    }
    if (Test-Path -LiteralPath $hostCopy) { Remove-Item -LiteralPath $hostCopy -Recurse -Force }
    Copy-Item -LiteralPath (Split-Path $builtHost) -Destination $hostCopy -Recurse
    $HostDll = Join-Path $hostCopy "SqlHarness.Mcp.WorkflowHost.dll"
}
if (-not (Test-Path -LiteralPath $HostDll)) {
    throw "Workflow host is missing at $HostDll. Build tests/SqlHarness.Mcp.WorkflowHost first."
}
if (-not $OutDir) {
    $OutDir = Join-Path $repo "artifacts\plan-077\$Phase"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$families = @(
    @{ Id = "no-improvement"; Prompt = "prompts\no-improvement.md"; Oracle = "oracle\no-improvement.json"; Scenario = "no-improvement"; InputPrefix = "no-improvement" },
    @{ Id = "partial-matrix-32"; Prompt = "prompts\partial-matrix-32.md"; Oracle = "oracle\partial-matrix-32.json"; Scenario = "partial-matrix-32"; InputPrefix = "partial-matrix" },
    @{ Id = "colliding-operators"; Prompt = "prompts\colliding-operators.md"; Oracle = "oracle\colliding-operators.json"; Scenario = "colliding-operators"; InputPrefix = "colliding" }
)

$grok = (Get-Command grok).Source
$skillSrc = Join-Path $repo "skills\sqlharness"
$trustedPath = Join-Path $env:USERPROFILE ".grok\trusted_folders.toml"
$summary = [System.Collections.Generic.List[object]]::new()

function Get-DenyGlobs {
    $roots = [System.Collections.Generic.List[string]]::new()
    $roots.Add($repo.TrimEnd('\', '/'))
    $parent = Split-Path $repo
    if ((Split-Path $parent -Leaf) -eq '.worktrees') {
        $roots.Add((Split-Path $parent).TrimEnd('\', '/'))
    }
    $globs = [System.Collections.Generic.List[string]]::new()
    foreach ($root in $roots | Select-Object -Unique) {
        $escaped = $root.Replace('\', '\\')
        $globs.Add("Read($escaped\\**)")
        $globs.Add("Grep($escaped\\**)")
        $globs.Add("Edit($escaped\\**)")
    }
    $globs.Add("Bash(*)")
    return $globs
}

function Get-ClaimHits([string] $text, [string[]] $needles) {
    $lower = $text.ToLowerInvariant()
    return @($needles | Where-Object { $lower.Contains($_.ToLowerInvariant()) })
}

function Score-Trial([string] $conclusion, $oracle, [int] $toolCalls, [int] $maxToolCalls, [bool] $timedOut, [bool] $failed) {
    if ($failed) { return "error" }
    if ($timedOut -or $toolCalls -ge $maxToolCalls) { return "incomplete" }
    $required = @($oracle.requiredClaims)
    $forbidden = @($oracle.forbiddenClaims)
    $requiredHits = switch ($oracle.family) {
        "no-improvement-invalid-comparison" {
            @(
                (Get-ClaimHits $conclusion @("not a demonstrated improvement", "not an improvement", "cannot treat", "do not treat", "not proof of improvement", "inconclusive")),
                (Get-ClaimHits $conclusion @("not equivalent", "equivalence", "unstable", "result-equivalence", "mismatch")),
                (Get-ClaimHits $conclusion @("pass is not", "not proof", "absence of", "do not treat a regression", "regression pass"))
            )
        }
        "partial-32-case-matrix" {
            @(
                (Get-ClaimHits $conclusion @("32", "all requested", "every case", "all 32")),
                (Get-ClaimHits $conclusion @("omitted", "page", "continuation", "not unexecuted", "paging")),
                (Get-ClaimHits $conclusion @("fail", "inconclusive"))
            )
        }
        "colliding-operator-ids" {
            @(
                (Get-ClaimHits $conclusion @("node", "statement", "local", "repeat")),
                (Get-ClaimHits $conclusion @("warning", "spill", "candidate", "statement"))
            )
        }
        default { @() }
    }
    $forbiddenHit = Get-ClaimHits $conclusion @(
        "accept this as an optimization",
        "demonstrated improvement and should be shipped",
        "same operator because the node id",
        "unexecuted because they were omitted"
    )
    $missingRequired = 0
    foreach ($hit in $requiredHits) {
        if (@($hit).Length -eq 0) { $missingRequired++ }
    }
    if (@($forbiddenHit).Length -gt 0) { return "failed-oracle" }
    if ($missingRequired -gt 0) { return "failed-oracle" }
    return "evidence-complete"
}

$runIndex = 0
foreach ($family in $families) {
    for ($n = 1; $n -le 3; $n++) {
        $runIndex++
        $name = "$($family.Id)-run$n"
        $oracle = Get-Content -LiteralPath (Join-Path $fixtureRoot $family.Oracle) -Raw | ConvertFrom-Json
        $exportPath = Join-Path $OutDir "$name.export.md"
        if (Test-Path -LiteralPath $exportPath) {
            Write-Host "=== $Phase $name ($runIndex/9) reuse existing export ==="
            $md = Get-Content -LiteralPath $exportPath -Raw
            $parts = [regex]::Split($md, "(?m)^## Assistant\s*$")
            $conclusion = if ($parts.Count -gt 1) { $parts[-1].Trim() } else { $md }
            $toolCalls = [regex]::Matches($md, "(?m)^- UseTool: sqlharness__").Count
            $outcome = Score-Trial $conclusion $oracle $toolCalls $MaxTurns $false $false
            $row = [ordered]@{
                phase = $Phase; family = $family.Id; run = $n; model = $Model; effort = $Effort
                reusedExport = $true; toolCallsApprox = $toolCalls; outcome = $outcome; scenario = $family.Scenario
            }
            $row | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutDir "$name.meta.json") -Encoding utf8
            $summary.Add($row)
            Write-Host "$name -> $outcome (reused export, mcpCalls=$toolCalls)"
            continue
        }
        Write-Host "=== $Phase $name ($runIndex/9) ==="
        $trialRoot = Join-Path ([System.IO.Path]::GetTempPath()) "sqlharness-077-trial-$name-$([guid]::NewGuid().ToString('N'))"
        $isolatedHome = Join-Path $trialRoot "home"
        $work = Join-Path $trialRoot "work"
        New-Item -ItemType Directory -Force -Path $isolatedHome, $work, (Join-Path $work ".grok\skills\sqlharness") | Out-Null
        Copy-Item -LiteralPath (Join-Path $skillSrc "SKILL.md") -Destination (Join-Path $work ".grok\skills\sqlharness\SKILL.md")
        Copy-Item -LiteralPath (Join-Path $fixtureRoot $family.Prompt) -Destination (Join-Path $work "prompt.md")
        New-Item -ItemType Directory -Force -Path (Join-Path $work "inputs") | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $fixtureRoot "inputs") -Filter "$($family.InputPrefix)*" | Copy-Item -Destination (Join-Path $work "inputs")
        $oracle = Get-Content -LiteralPath (Join-Path $fixtureRoot $family.Oracle) -Raw | ConvertFrom-Json
        $sessionId = [guid]::NewGuid().ToString()
        $stdoutPath = Join-Path $OutDir "$name.stdout.json"
        $stderrPath = Join-Path $OutDir "$name.stderr.txt"
        $metaPath = Join-Path $OutDir "$name.meta.json"
        $debugPath = Join-Path $OutDir "$name.debug.log"
        $grokSession = $sessionId

        $trustedExisted = Test-Path -LiteralPath $trustedPath
        $trustedBefore = if ($trustedExisted) { [IO.File]::ReadAllBytes($trustedPath) } else { $null }
        $timedOut = $false
        $agentFinished = $false
        $exitCode = -1
        $errorText = $null
        $started = Get-Date
        try {
            Push-Location $work
            & $grok mcp add --scope project sqlharness -e "SQLHARNESS_HOME=$isolatedHome" -e "SQLHARNESS_077_FIXTURE_PW=unused-fixture-password" -- dotnet $HostDll --scenario $($family.Scenario) --home $isolatedHome
            if ($LASTEXITCODE -ne 0) { throw "grok mcp add failed with $LASTEXITCODE" }
            $onlyTrial = "[folders.'$work']`ntrusted = true`ndecided_at = $([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())`n"
            [IO.File]::WriteAllText($trustedPath, $onlyTrial)
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $grok
            $psi.ArgumentList.Add("--prompt-file")
            $psi.ArgumentList.Add((Join-Path $work "prompt.md"))
            $psi.ArgumentList.Add("--model")
            $psi.ArgumentList.Add($Model)
            $psi.ArgumentList.Add("--reasoning-effort")
            $psi.ArgumentList.Add($Effort)
            $psi.ArgumentList.Add("--always-approve")
            $psi.ArgumentList.Add("--no-subagents")
            $psi.ArgumentList.Add("--disable-web-search")
            $psi.ArgumentList.Add("--max-turns")
            $psi.ArgumentList.Add("$MaxTurns")
            $psi.ArgumentList.Add("--output-format")
            $psi.ArgumentList.Add("json")
            $psi.ArgumentList.Add("--session-id")
            $psi.ArgumentList.Add($sessionId)
            $psi.ArgumentList.Add("--cwd")
            $psi.ArgumentList.Add($work)
            $psi.ArgumentList.Add("--debug-file")
            $psi.ArgumentList.Add((Join-Path $OutDir "$name.debug.log"))
            foreach ($deny in Get-DenyGlobs) {
                $psi.ArgumentList.Add("--deny")
                $psi.ArgumentList.Add($deny)
            }
            $psi.WorkingDirectory = $work
            $psi.UseShellExecute = $false
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $psi.CreateNoWindow = $true
            $proc = [Diagnostics.Process]::Start($psi)
            $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
            $stderrTask = $proc.StandardError.ReadToEndAsync()
            $debugPathLive = Join-Path $OutDir "$name.debug.log"
            $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
            $agentFinished = $false
            while (-not $proc.HasExited -and [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 500
                if (Test-Path -LiteralPath $debugPathLive) {
                    $tail = Get-Content -LiteralPath $debugPathLive -Tail 15 -ErrorAction SilentlyContinue
                    if ($tail -match 'headless: no pending background tasks, exiting' -or $tail -match '"stopReason":"end_turn"') {
                        $agentFinished = $true
                        Start-Sleep -Seconds 2
                        try { $proc.Kill($true) } catch { }
                        break
                    }
                }
            }
            if (-not $proc.HasExited) {
                $timedOut = -not $agentFinished
                try { $proc.Kill($true) } catch { }
                $proc.WaitForExit()
            }
            else {
                $proc.WaitForExit()
            }
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $stderr = $stderrTask.GetAwaiter().GetResult()
            $exitCode = $proc.ExitCode
            Set-Content -LiteralPath $stdoutPath -Value $stdout -Encoding utf8
            Set-Content -LiteralPath $stderrPath -Value $stderr -Encoding utf8
        }
        catch {
            $errorText = "$_"
            Set-Content -LiteralPath $stderrPath -Value $errorText -Encoding utf8
        }
        finally {
            Pop-Location
            if ($trustedExisted) { [IO.File]::WriteAllBytes($trustedPath, $trustedBefore) }
            else { Remove-Item -LiteralPath $trustedPath -ErrorAction SilentlyContinue }
        }

        $duration = [int]((Get-Date) - $started).TotalSeconds
        $conclusion = ""
        $toolCalls = 0
        $inputTokens = $null
        $outputTokens = $null
        $numTurns = $null
        if (Test-Path -LiteralPath $stdoutPath) {
            $raw = Get-Content -LiteralPath $stdoutPath -Raw -ErrorAction SilentlyContinue
            if ($raw) {
                $conclusion = $raw
                try {
                    $parsed = $raw | ConvertFrom-Json
                    if ($parsed.PSObject.Properties.Name -contains "text") { $conclusion = [string]$parsed.text }
                    if ($parsed.PSObject.Properties.Name -contains "num_turns") { $numTurns = [int]$parsed.num_turns }
                    if ($parsed.usage) {
                        if ($parsed.usage.input_tokens) { $inputTokens = [int]$parsed.usage.input_tokens }
                        if ($parsed.usage.output_tokens) { $outputTokens = [int]$parsed.usage.output_tokens }
                    }
                }
                catch { }
            }
        }
        $debugPath = Join-Path $OutDir "$name.debug.log"
        if (Test-Path -LiteralPath $debugPath) {
            $debug = Get-Content -LiteralPath $debugPath -Raw
            $toolCalls = [regex]::Matches($debug, '"name"\s*:\s*"sqlharness_[a-z_]+"').Count
            if ($toolCalls -eq 0) {
                $toolCalls = [regex]::Matches($debug, "sqlharness_[a-z_]+").Count
            }
        }
        $usageText = ""
        try { $usageText = & $grok usage $sessionId 2>&1 | Out-String } catch { $usageText = "$_" }
        Set-Content -LiteralPath (Join-Path $OutDir "$name.usage.txt") -Value $usageText -Encoding utf8

        $exportPath = Join-Path $OutDir "$name.export.md"
        $grokSession = $sessionId
        if (Test-Path -LiteralPath $debugPath) {
            $debugRaw = Get-Content -LiteralPath $debugPath -Raw
            $sid = [regex]::Match($debugRaw, "session\{session_id=([0-9a-f-]{36})\}")
            if ($sid.Success) { $grokSession = $sid.Groups[1].Value }
            if ($debugRaw -match '"stopReason":"end_turn"') { $agentFinished = $true }
        }
        try {
            & $grok export $grokSession $exportPath 2>$null | Out-Null
        } catch { }
        if (Test-Path -LiteralPath $exportPath) {
            $md = Get-Content -LiteralPath $exportPath -Raw
            $parts = [regex]::Split($md, "(?m)^## Assistant\s*$")
            if ($parts.Count -gt 1) { $conclusion = $parts[-1].Trim() }
            $toolCalls = [regex]::Matches($md, "(?m)^- UseTool: sqlharness__").Count
        }
        if ($agentFinished) { $timedOut = $false }
        $failed = [bool]$errorText -and -not $agentFinished
        $outcome = Score-Trial $conclusion $oracle $toolCalls $MaxTurns $timedOut $failed
        $row = [ordered]@{
            phase            = $Phase
            family           = $family.Id
            run              = $n
            model            = $Model
            effort           = $Effort
            sessionId        = $sessionId
            grokSessionId    = $grokSession
            agentFinished    = $agentFinished
            exitCode         = $exitCode
            timedOut         = $timedOut
            durationSeconds  = $duration
            toolCallsApprox  = $toolCalls
            numTurns         = $numTurns
            inputTokens      = $inputTokens
            outputTokens     = $outputTokens
            outcome          = $outcome
            error            = $errorText
            host             = "<host-copy>/SqlHarness.Mcp.WorkflowHost.dll"
            scenario         = $family.Scenario
        }
        $row | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metaPath -Encoding utf8
        $summary.Add($row)
        Write-Host "$name -> $outcome exit=$exitCode timeout=$timedOut seconds=$duration"
        try { Remove-Item -LiteralPath $trialRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    }
}

$summaryPath = Join-Path $OutDir "summary.json"
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding utf8
$committed = Join-Path $fixtureRoot "trials\$Phase-summary.json"
New-Item -ItemType Directory -Force -Path (Split-Path $committed) | Out-Null
Copy-Item -LiteralPath $summaryPath -Destination $committed -Force
Write-Host "Wrote $summaryPath and $committed"
