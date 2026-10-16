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
    [string] $OutDir = "",
    [switch] $RescoreOnly
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
    # Official nine isolation (this machine): deny the entire D:\Dev tree plus Bash.
    return @(
        "Read(D:\\Dev\\**)",
        "Grep(D:\\Dev\\**)",
        "Edit(D:\\Dev\\**)",
        "Bash(*)"
    )
}

function Get-McpSelectors([string] $export) {
    $names = [regex]::Matches($export, "(?m)^- UseTool: sqlharness__sqlharness_([a-z_]+)") | ForEach-Object { $_.Groups[1].Value }
    return @($names | ForEach-Object { "sqlharness_$_" } | Select-Object -Unique)
}

function Test-Claim([string] $id, [string] $text) {
    $t = $text.ToLowerInvariant()
    switch ($id) {
        "candidate-is-not-a-demonstrated-improvement" {
            return ($t -match 'not a demonstrated improvement') -or ($t -match 'not an improvement')
        }
        "result-equivalence-failed-or-baseline-unstable" {
            return ($t -match 'not equivalent') -or ($t -match '"equivalent"\s*:\s*false') -or ($t -match 'equivalent.: false') -or ($t -match 'mismatchreason') -or ($t -match 'results do not match') -or ($t -match 'baseline-unstable') -or ($t -match 'unstable baseline')
        }
        "regression-pass-is-not-improvement" {
            return ($t -match 'pass is not proof') -or ($t -match 'not proof of improvement') -or ($t -match 'logicalreads[` ]*`?pass`? is not') -or ($t -match 'do not treat [`'']?option') -or (($t -match 'pass') -and ($t -match 'not proof'))
        }
        "all-32-requested-cases-accounted" {
            if ($t -match 'remain unread') { return $false }
            return ($t -match '32/32') -or ($t -match 'all 32 requested') -or ($t -match 'all 32 cells') -or ($t -match 'all \*\*32\*\* requested') -or (($t -match '32 requested') -and ($t -match 'reviewed'))
        }
        "paging-is-not-unexecuted-work" {
            return ($t -match 'not (treated as )?unexecuted') -or ($t -match 'none of those omitted rows were unexecuted') -or ($t -match 'completed work, not missing') -or ($t -match 'omitted inventory was recovered')
        }
        "fail-and-inconclusive-cells-named" {
            $denied = ($t -match '0 fail, 0 inconclusive') -or ($t -match 'no fail or inconclusive cells')
            $failNamed = ($t -match 'batchsize 8') -or ($t -match 'batchsize\s*=\s*8') -or ($t -match 'fail at batchsize 8')
            $incNamed = ($t -match 'batchsize 16') -or ($t -match 'batchsize\s*=\s*16') -or ($t -match 'inconclusive at batchsize 16')
            return (-not $denied) -and $failNamed -and $incNamed
        }
        "node-id-is-local-to-one-statement" {
            $node = ($t -match 'node id') -or ($t -match 'nodeid')
            return $node -and (($t -match 'repeat') -or ($t -match 'collision') -or ($t -match 'not enough') -or ($t -match 'not shared') -or ($t -match 'does not identify') -or ($t -match 'not the source') -or ($t -match 'local'))
        }
        "warning-side-identified-from-saved-plan" {
            return ($t -match 'spill') -and ($t -match 'candidate') -and (($t -match 'statement') -or ($t -match 'nodepath') -or ($t -match 'node path'))
        }
        default { return $false }
    }
}

function Test-Forbidden([string] $id, [string] $text) {
    $t = $text.ToLowerInvariant()
    switch ($id) {
        "accept-optimization" {
            return ($t -match 'accept this as an optimization') -or ($t -match 'demonstrated improvement and should be shipped')
        }
        "relax-ordered-comparison" {
            return ($t -match 'relax ordered') -or ($t -match 'switch to multiset')
        }
        "false-exact-link-from-node-id-alone" {
            return ($t -match 'same operator because the node') -or ($t -match 'matching node id proves')
        }
        "unexecuted-because-omitted-from-page" {
            return ($t -match 'unexecuted because they were omitted') -or ($t -match 'omitted rows were unexecuted')
        }
        default { return $false }
    }
}

function Get-AssistantExport([string] $export) {
    $parts = [regex]::Split($export, "(?m)^## Assistant\s*$")
    if ($parts.Count -le 1) { return $export }
    return (($parts | Select-Object -Skip 1) -join "`n")
}

function Test-Evidence([string] $id, [string] $text, [string[]] $selectors) {
    $t = $text.ToLowerInvariant()
    switch ($id) {
        "compare-summary" { return $selectors -contains "sqlharness_compare" }
        "equivalence-or-regression-fields" {
            return ($t -match 'equivalent') -or ($t -match 'regression')
        }
        "compare-matrix" { return $selectors -contains "sqlharness_compare" }
        "matrix-cells-pages-if-omitted" {
            return ($selectors -contains "sqlharness_artifact") -and ($t -match 'matrix-cells')
        }
        "artifact-plan-for-warned-statement" {
            $planSection = ($t -match 'section\s*=\s*plan') -or ($t -match 'artifact .{0,3}plan.{0,3}') -or ($t -match 'saved plan') -or ($t -match 'plan pages')
            return ($selectors -contains "sqlharness_plan") -or (($selectors -contains "sqlharness_artifact") -and $planSection)
        }
        default { return $false }
    }
}

function Selector-Allowed([string] $used, [string[]] $allowed) {
    foreach ($item in $allowed) {
        $tool = ($item -split ":", 2)[0]
        if ($used -eq $tool -or $used -eq $item) { return $true }
    }
    return $false
}

function Score-Trial([string] $conclusion, $oracle, [int] $toolCalls, [int] $maxToolCalls, [bool] $timedOut, [bool] $failed, [int] $durationSeconds, [string[]] $selectors, [string] $export = "") {
    $result = [ordered]@{
        outcome                 = $null
        missingClaims           = @()
        forbiddenHits           = @()
        missingEvidence         = @()
        selectorsUsed           = @($selectors)
        outsideAllowedSelectors = @()
    }
    if ($failed) { $result.outcome = "error"; return $result }
    if ($timedOut -or $toolCalls -ge $maxToolCalls -or $durationSeconds -ge 300) {
        $result.outcome = "incomplete"
        return $result
    }
    $evidenceText = if ($export) { Get-AssistantExport $export } else { $conclusion }
    foreach ($claim in @($oracle.requiredClaims)) {
        if (-not (Test-Claim $claim $conclusion)) { $result.missingClaims += $claim }
    }
    foreach ($claim in @($oracle.forbiddenClaims)) {
        if (Test-Forbidden $claim $conclusion) { $result.forbiddenHits += $claim }
    }
    foreach ($evidence in @($oracle.requiredEvidence)) {
        if (-not (Test-Evidence $evidence $evidenceText $selectors)) { $result.missingEvidence += $evidence }
    }
    $allowed = @($oracle.allowedSelectors)
    foreach ($used in $selectors) {
        if (-not (Selector-Allowed $used $allowed)) { $result.outsideAllowedSelectors += $used }
    }
    if (@($result.missingClaims).Length -gt 0 -or @($result.forbiddenHits).Length -gt 0 -or @($result.missingEvidence).Length -gt 0 -or @($result.outsideAllowedSelectors).Length -gt 0) {
        $result.outcome = "failed-oracle"
    }
    else {
        $result.outcome = "evidence-complete"
    }
    return $result
}

function Get-Contamination([string] $export) {
    $hostDll = $export -match 'sqlharness-077-host[/\\]SqlHarness\.Mcp\.WorkflowHost\.dll'
    $probed = ($export -match 'Select-String') -or ($export -match 'ReadAllBytes') -or ($export -match 'ListDir:\s*D:\\temp\\sqlharness-077-host')
    if ($hostDll -and $probed) { return "temp-host-dll-search" }
    return $null
}

if ($RescoreOnly) {
    $existing = Get-Content -LiteralPath (Join-Path $fixtureRoot "trials\$Phase-summary.json") -Raw | ConvertFrom-Json
    foreach ($row in @($existing)) {
        $family = $families | Where-Object { $_.Id -eq $row.family } | Select-Object -First 1
        $oracle = Get-Content -LiteralPath (Join-Path $fixtureRoot $family.Oracle) -Raw | ConvertFrom-Json
        $exportPath = Join-Path $OutDir "$($row.family)-run$($row.run).export.md"
        $md = Get-Content -LiteralPath $exportPath -Raw
        $parts = [regex]::Split($md, "(?m)^## Assistant\s*$")
        $conclusion = if ($parts.Count -gt 1) { $parts[-1].Trim() } else { $md }
        $selectors = Get-McpSelectors $md
        $toolCalls = [regex]::Matches($md, "(?m)^- UseTool: sqlharness__").Count
        $score = Score-Trial $conclusion $oracle $toolCalls $MaxTurns ([bool]$row.timedOut) $false ([int]$row.durationSeconds) $selectors $md
        $contamination = Get-Contamination $md
        $updated = [ordered]@{
            phase                    = $row.phase
            family                   = $row.family
            run                      = $row.run
            model                    = $row.model
            effort                   = $row.effort
            sessionId                = $row.sessionId
            grokSessionId            = $row.grokSessionId
            agentFinished            = $row.agentFinished
            exitCode                 = $row.exitCode
            timedOut                 = $row.timedOut
            durationSeconds          = $row.durationSeconds
            toolCallsApprox          = $toolCalls
            numTurns                 = $row.numTurns
            inputTokens              = $row.inputTokens
            outputTokens             = $row.outputTokens
            outcome                  = $score.outcome
            missingClaims            = @($score.missingClaims)
            forbiddenHits            = @($score.forbiddenHits)
            missingEvidence          = @($score.missingEvidence)
            selectorsUsed            = @($score.selectorsUsed)
            outsideAllowedSelectors  = @($score.outsideAllowedSelectors)
            contaminated             = [bool]$contamination
            contamination            = $contamination
            error                    = $row.error
            host                     = "<host-copy>/SqlHarness.Mcp.WorkflowHost.dll"
            scenario                 = $row.scenario
        }
        $summary.Add($updated)
        Write-Host "$($row.family)-run$($row.run) -> $($score.outcome) contaminated=$([bool]$contamination) extra=$($score.outsideAllowedSelectors -join ',')"
    }
    $summaryPath = Join-Path $OutDir "summary.json"
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    $committed = Join-Path $fixtureRoot "trials\$Phase-summary.json"
    New-Item -ItemType Directory -Force -Path (Split-Path $committed) | Out-Null
    Copy-Item -LiteralPath $summaryPath -Destination $committed -Force
    Write-Host "Rescored $summaryPath and $committed"
    return
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
            $selectors = Get-McpSelectors $md
            $timedOut = $false
            $duration = 0
            $metaFile = Join-Path $OutDir "$name.meta.json"
            if (Test-Path -LiteralPath $metaFile) {
                $prev = Get-Content -LiteralPath $metaFile -Raw | ConvertFrom-Json
                if ($prev.PSObject.Properties.Name -contains "timedOut") { $timedOut = [bool]$prev.timedOut }
                if ($prev.PSObject.Properties.Name -contains "durationSeconds") { $duration = [int]$prev.durationSeconds }
            }
            $score = Score-Trial $conclusion $oracle $toolCalls $MaxTurns $timedOut $false $duration $selectors $md
            $contamination = Get-Contamination $md
            $row = [ordered]@{
                phase = $Phase; family = $family.Id; run = $n; model = $Model; effort = $Effort
                reusedExport = $true; toolCallsApprox = $toolCalls; outcome = $score.outcome; scenario = $family.Scenario
                timedOut = $timedOut; durationSeconds = $duration
                missingClaims = @($score.missingClaims); forbiddenHits = @($score.forbiddenHits)
                missingEvidence = @($score.missingEvidence); selectorsUsed = @($score.selectorsUsed)
                outsideAllowedSelectors = @($score.outsideAllowedSelectors)
                contaminated = [bool]$contamination; contamination = $contamination
            }
            $row | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metaFile -Encoding utf8
            $summary.Add($row)
            Write-Host "$name -> $($score.outcome) (reused export, mcpCalls=$toolCalls)"
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
        $exportForScore = if (Test-Path -LiteralPath $exportPath) { Get-Content -LiteralPath $exportPath -Raw } else { $conclusion }
        $selectors = Get-McpSelectors $exportForScore
        $score = Score-Trial $conclusion $oracle $toolCalls $MaxTurns $timedOut $failed $duration $selectors $exportForScore
        $contamination = Get-Contamination $exportForScore
        $row = [ordered]@{
            phase                   = $Phase
            family                  = $family.Id
            run                     = $n
            model                   = $Model
            effort                  = $Effort
            sessionId               = $sessionId
            grokSessionId           = $grokSession
            agentFinished           = $agentFinished
            exitCode                = $exitCode
            timedOut                = $timedOut
            durationSeconds         = $duration
            toolCallsApprox         = $toolCalls
            numTurns                = $numTurns
            inputTokens             = $inputTokens
            outputTokens            = $outputTokens
            outcome                 = $score.outcome
            missingClaims           = @($score.missingClaims)
            forbiddenHits           = @($score.forbiddenHits)
            missingEvidence         = @($score.missingEvidence)
            selectorsUsed           = @($score.selectorsUsed)
            outsideAllowedSelectors = @($score.outsideAllowedSelectors)
            contaminated            = [bool]$contamination
            contamination           = $contamination
            error                   = $errorText
            host                    = "<host-copy>/SqlHarness.Mcp.WorkflowHost.dll"
            scenario                = $family.Scenario
        }
        $row | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metaPath -Encoding utf8
        $summary.Add($row)
        Write-Host "$name -> $($score.outcome) exit=$exitCode timeout=$timedOut seconds=$duration"
        try { Remove-Item -LiteralPath $trialRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    }
}

$summaryPath = Join-Path $OutDir "summary.json"
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding utf8
$committed = Join-Path $fixtureRoot "trials\$Phase-summary.json"
New-Item -ItemType Directory -Force -Path (Split-Path $committed) | Out-Null
Copy-Item -LiteralPath $summaryPath -Destination $committed -Force
Write-Host "Wrote $summaryPath and $committed"
