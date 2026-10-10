#Requires -Version 7.0
<#
.SYNOPSIS
    Manual MCP acceptance against real Claude Code and Codex clients (not CI).

.DESCRIPTION
    Runs sqlharness mcp serve behind a logging stdio tap through headless
    `claude -p` and `codex exec`, with one-off MCP configuration only: no client
    configuration file is edited. Each client calls sqlharness_capabilities once
    and the target-free sqlharness_gain operation once for the journal check.
    Checks the negotiated revision (Claude Code: 2026-07-28 via server/discover;
    Codex: 2025-06-18), the revision the capabilities result reports, and that the
    journal session linked to the gain operation persisted clientInfo.
    Frames and journals stay in client-specific temporary homes and are not printed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Sqlharness,
    [string]$Profile = 'local-playground',
    [switch]$SkipClaude,
    [switch]$SkipCodex
)

$ErrorActionPreference = 'Stop'
$python = (Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
if (-not (Test-Path -LiteralPath $Sqlharness -PathType Leaf)) {
    throw "SQLHarness executable not found: $Sqlharness"
}
$Sqlharness = (Resolve-Path -LiteralPath $Sqlharness -ErrorAction Stop).Path
$tap = Join-Path $PSScriptRoot 'mcp-client-acceptance-tap.py'
$work = Join-Path ([IO.Path]::GetTempPath()) ("sqlharness-mcp-acceptance-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$prompt = 'Call sqlharness_capabilities from the acceptance MCP server exactly once, then call sqlharness_gain exactly once, then reply with one word: done.'
$results = [System.Collections.Generic.List[object]]::new()

function Add-Check([string]$client, [string]$check, [string]$expected, $actual, [bool]$pass) {
    $results.Add([pscustomobject]@{
        Client = $client
        Check = $check
        Expected = $expected
        Actual = if ($null -eq $actual) { '<missing>' } else { [string]$actual }
        Pass = $pass
    })
}

function New-IsolatedTarget([string]$clientHome, [string]$profile) {
    $profiles = @{}
    $profiles[$profile] = @{
        server = 'acceptance.invalid'
        database = 'acceptance'
        vars = @{}
        auth = 'integrated'
    }
    $profiles | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $clientHome 'targets.json') -Encoding utf8NoBOM
}

function Read-Frames([string]$logPath) {
    if (-not (Test-Path -LiteralPath $logPath)) { return @() }
    $frames = foreach ($line in Get-Content -LiteralPath $logPath) {
        $parts = $line -split ' ', 3
        if ($parts.Count -eq 3 -and $parts[1] -ne 'ERR') {
            try {
                [pscustomobject]@{
                    Direction = $parts[1]
                    Message = $parts[2] | ConvertFrom-Json -Depth 64
                }
            } catch { }
        }
    }
    return @($frames)
}

function Test-Client([string]$name, [string]$logPath, [string]$expected, [string]$clientHome) {
    $frames = @(Read-Frames $logPath)
    $toServer = @($frames | Where-Object Direction -eq 'C->S')
    $fromServer = @($frames | Where-Object Direction -eq 'S->C')
    $initializes = @($toServer | Where-Object { $_.Message.method -eq 'initialize' })
    $discovers = @($toServer | Where-Object { $_.Message.method -eq 'server/discover' })
    $calls = @($toServer | Where-Object {
        $_.Message.method -eq 'tools/call' -and $_.Message.params.name -eq 'sqlharness_capabilities'
    })
    $init = $initializes | Select-Object -First 1
    $call = $calls | Select-Object -First 1
    $initializeResponse = if ($init) {
        $fromServer | Where-Object { $_.Message.id -eq $init.Message.id } | Select-Object -First 1
    }
    $callResponse = if ($call) {
        $fromServer | Where-Object { $_.Message.id -eq $call.Message.id } | Select-Object -First 1
    }

    $negotiated = $null
    if ($expected -eq '2026-07-28' -and $call) {
        $negotiated = $call.Message.params._meta.'io.modelcontextprotocol/protocolVersion'
        if (-not $negotiated -and $discovers.Count -gt 0) {
            $negotiated = $discovers[0].Message.params._meta.'io.modelcontextprotocol/protocolVersion'
        }
    } elseif ($initializeResponse) {
        $negotiated = $initializeResponse.Message.result.protocolVersion
    }

    $clientName = if ($init) {
        $init.Message.params.clientInfo.name
    } elseif ($call) {
        $call.Message.params._meta.'io.modelcontextprotocol/clientInfo'.name
    }
    $clientVersion = if ($init) {
        $init.Message.params.clientInfo.version
    } elseif ($call) {
        $call.Message.params._meta.'io.modelcontextprotocol/clientInfo'.version
    }
    $capabilitiesText = if ($callResponse) { $callResponse.Message.result.content[0].text }
    $reported = $null
    if ($capabilitiesText) {
        try { $reported = ($capabilitiesText | ConvertFrom-Json -Depth 64).result.protocolVersion } catch { }
    }

    Add-Check $name 'tools/call count' '1' $calls.Count ($calls.Count -eq 1)
    if ($expected -eq '2025-06-18') {
        Add-Check $name 'initialize response present' 'yes' ([bool]$initializeResponse) ([bool]$initializeResponse)
    }
    Add-Check $name 'negotiated' $expected $negotiated ($negotiated -eq $expected)
    Add-Check $name 'capabilities.protocolVersion' $expected $reported ($reported -eq $expected)
    Add-Check $name 'clientInfo.name present' 'non-empty' $clientName (-not [string]::IsNullOrWhiteSpace($clientName))
    Add-Check $name 'capabilities response present' 'yes' ([bool]$callResponse) ([bool]$callResponse)
    if ($callResponse) {
        Add-Check $name 'capabilities call error' 'false' $callResponse.Message.result.isError ($callResponse.Message.result.isError -ne $true)
    }
    if ($expected -eq '2026-07-28') {
        Add-Check $name 'server/discover used' 'yes' ([bool]($discovers.Count -gt 0)) ($discovers.Count -gt 0)
    }

    $journalPath = Join-Path $clientHome 'data\activity.db'
    $journalQuery = @'
import json, sqlite3, sys
from urllib.parse import quote
try:
    database_uri = "file:" + quote(sys.argv[1].replace("\\", "/"), safe="/:") + "?mode=ro"
    connection = sqlite3.connect(database_uri, uri=True)
    rows = connection.execute("""
        SELECT s.client_name, s.client_version, o.operation
        FROM operations AS o JOIN sessions AS s ON s.id = o.session_id
        WHERE o.operation = 'gain'
    """).fetchall()
    if len(rows) != 1:
        raise RuntimeError()
    print(json.dumps(rows[0]))
except Exception:
    print("journal check failed", file=sys.stderr)
    raise SystemExit(2)
'@
    $journalOutput = & $python -I -c $journalQuery $journalPath 2>$null
    $journalExit = $LASTEXITCODE
    $journalRow = $null
    if ($journalExit -eq 0) {
        try { $journalRow = $journalOutput | ConvertFrom-Json -AsHashtable } catch { }
    }
    $storedName = if ($journalRow) { $journalRow[0] } else { $null }
    $storedVersion = if ($journalRow) { $journalRow[1] } else { $null }
    $storedOperation = if ($journalRow) { $journalRow[2] } else { $null }
    Add-Check $name 'journal operation linked' 'gain' $storedOperation ($storedOperation -eq 'gain')
    Add-Check $name 'journal clientInfo.name' $clientName $storedName (
        -not [string]::IsNullOrWhiteSpace($clientName) -and $storedName -eq $clientName)
    Add-Check $name 'journal clientInfo.version' $clientVersion $storedVersion (
        -not [string]::IsNullOrWhiteSpace($clientVersion) -and $storedVersion -eq $clientVersion)
}

if (-not $SkipClaude) {
    $claude = Get-Command claude -ErrorAction Stop | Select-Object -First 1
    $logPath = Join-Path $work 'claude-frames.log'
    $clientHome = Join-Path $work 'claude-home'
    New-Item -ItemType Directory -Path $clientHome | Out-Null
    New-IsolatedTarget $clientHome $Profile
    $config = @{ mcpServers = @{ acceptance = @{ command = $python; args = @('-I', $tap, $logPath, $clientHome, $Sqlharness, 'mcp', 'serve', $Profile) } } } |
        ConvertTo-Json -Depth 8
    $configPath = Join-Path $work 'claude-mcp.json'
    Set-Content -LiteralPath $configPath -Value $config -Encoding utf8NoBOM
    Push-Location $work
    $savedSqlHarnessHome = $env:SQLHARNESS_HOME
    try {
        $env:SQLHARNESS_HOME = $clientHome
        & $claude.Source -p $prompt --mcp-config $configPath --strict-mcp-config --allowedTools 'mcp__acceptance__sqlharness_capabilities,mcp__acceptance__sqlharness_gain' --model haiku 2>$null | Out-Null
        $clientExit = $LASTEXITCODE
    } finally {
        $env:SQLHARNESS_HOME = $savedSqlHarnessHome
        Pop-Location
    }
    Add-Check 'Claude Code' 'client exit code' '0' $clientExit ($clientExit -eq 0)
    Test-Client 'Claude Code' $logPath '2026-07-28' $clientHome
}

if (-not $SkipCodex) {
    $codex = Get-Command codex -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $logPath = Join-Path $work 'codex-frames.log'
    $clientHome = Join-Path $work 'codex-home'
    New-Item -ItemType Directory -Path $clientHome | Out-Null
    New-IsolatedTarget $clientHome $Profile
    $argsToml = '[' + ((@('-I', $tap, $logPath, $clientHome, $Sqlharness, 'mcp', 'serve', $Profile) | ForEach-Object { "'" + ($_ -replace '\\', '/') + "'" }) -join ',') + ']'
    Push-Location $work
    $savedSqlHarnessHome = $env:SQLHARNESS_HOME
    try {
        $env:SQLHARNESS_HOME = $clientHome
        & $codex.Source exec --ignore-user-config --skip-git-repo-check `
            -c ("mcp_servers.acceptance.command='" + ($python -replace '\\', '/') + "'") `
            -c ('mcp_servers.acceptance.args=' + $argsToml) `
            -c "mcp_servers.acceptance.default_tools_approval_mode='approve'" `
            $prompt 2>$null | Out-Null
        $clientExit = $LASTEXITCODE
    } finally {
        $env:SQLHARNESS_HOME = $savedSqlHarnessHome
        Pop-Location
    }
    Add-Check 'Codex' 'client exit code' '0' $clientExit ($clientExit -eq 0)
    Test-Client 'Codex' $logPath '2025-06-18' $clientHome
}

if ($SkipClaude -and $SkipCodex) {
    Write-Host 'No clients selected; use this mode to smoke-test script startup and argument handling.'
}
$results | Format-Table -AutoSize | Out-String | Write-Host
Write-Host 'Acceptance artifacts remain in a temporary directory; paths and frame contents are suppressed.'
if ($results | Where-Object { -not $_.Pass }) { exit 1 }
exit 0
