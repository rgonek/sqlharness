#Requires -Version 7.0
<#
.SYNOPSIS
    Manual MCP acceptance against real Claude Code and Codex clients (not CI).

.DESCRIPTION
    Runs sqlharness mcp serve behind a logging stdio tap through headless
    `claude -p` and `codex exec`, with one-off MCP configuration only: no client
    configuration file is edited. Each client calls sqlharness_capabilities once.
    Checks the negotiated revision (Claude Code: 2026-07-28 via server/discover;
    Codex: 2025-06-18), the revision the capabilities result reports, and that the
    session's clientInfo name was received. Frames contain tool results and stay
    in a temporary directory that is printed at the end.
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
$prompt = 'Call the sqlharness_capabilities tool from the acceptance MCP server exactly once, then reply with one word: done.'
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

function Test-Client([string]$name, [string]$logPath, [string]$expected) {
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
}

if (-not $SkipClaude) {
    $claude = Get-Command claude -ErrorAction Stop | Select-Object -First 1
    $logPath = Join-Path $work 'claude-frames.log'
    $config = @{ mcpServers = @{ acceptance = @{ command = $python; args = @('-I', $tap, $logPath, $Sqlharness, 'mcp', 'serve', $Profile) } } } |
        ConvertTo-Json -Depth 8
    $configPath = Join-Path $work 'claude-mcp.json'
    Set-Content -LiteralPath $configPath -Value $config -Encoding utf8NoBOM
    Push-Location $work
    try {
        & $claude.Source -p $prompt --mcp-config $configPath --strict-mcp-config --allowedTools 'mcp__acceptance__sqlharness_capabilities' --model haiku | Out-Null
        $clientExit = $LASTEXITCODE
    } finally { Pop-Location }
    Add-Check 'Claude Code' 'client exit code' '0' $clientExit ($clientExit -eq 0)
    Test-Client 'Claude Code' $logPath '2026-07-28'
}

if (-not $SkipCodex) {
    $codex = Get-Command codex -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $logPath = Join-Path $work 'codex-frames.log'
    $argsToml = '[' + ((@('-I', $tap, $logPath, $Sqlharness, 'mcp', 'serve', $Profile) | ForEach-Object { "'" + ($_ -replace '\\', '/') + "'" }) -join ',') + ']'
    Push-Location $work
    try {
        & $codex.Source exec --ignore-user-config --skip-git-repo-check `
            -c ("mcp_servers.acceptance.command='" + ($python -replace '\\', '/') + "'") `
            -c ('mcp_servers.acceptance.args=' + $argsToml) `
            -c "mcp_servers.acceptance.default_tools_approval_mode='approve'" `
            $prompt | Out-Null
        $clientExit = $LASTEXITCODE
    } finally { Pop-Location }
    Add-Check 'Codex' 'client exit code' '0' $clientExit ($clientExit -eq 0)
    Test-Client 'Codex' $logPath '2025-06-18'
}

if ($SkipClaude -and $SkipCodex) {
    Write-Host 'No clients selected; use this mode to smoke-test script startup and argument handling.'
}
$results | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "Frames (local only, contain tool results): $work"
if ($results | Where-Object { -not $_.Pass }) { exit 1 }
exit 0
