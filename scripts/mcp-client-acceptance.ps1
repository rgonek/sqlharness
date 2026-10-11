#Requires -Version 7.0
<#
.SYNOPSIS
    Manual MCP acceptance against real Claude Code, Codex, Copilot CLI, opencode and Grok Build clients (not CI).

.DESCRIPTION
    Runs sqlharness mcp serve behind a logging stdio tap through headless
    `claude -p`, `codex exec`, `copilot -p`, `opencode run --standalone` and
    `grok -p`, with one-off MCP configuration only: no client configuration file
    is edited. The one exception is Grok Build, which has no one-off flag: it loads
    ./.grok/config.toml (written by `grok mcp add --scope project` in a scratch
    folder) only for folders listed in ~/.grok/trusted_folders.toml, so the script
    appends a trust entry for that scratch folder alone and restores the file byte
    for byte (or removes it if it did not exist) afterwards. If the project config
    is not where expected, the Grok run is skipped and fails a check instead of
    trusting any other folder.
    Each client calls sqlharness_capabilities once
    and sqlharness_plan once on a fixture plan copied into a per-run input root
    for the journal check.
    Checks the negotiated revision (Claude Code and Copilot CLI: 2026-07-28 via
    server/discover; Codex: 2025-06-18; opencode and Grok Build: 2025-11-25), the
    revision the capabilities result reports, and that the
    journal session linked to the plan operation persisted clientInfo.
    Frames and journals stay in client-specific temporary homes and are not printed.
    opencode's default model may hit a plan usage limit; pass -OpencodeModel with
    a free model such as opencode/step-5-preview-free.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Sqlharness,
    [string]$Profile = 'local-playground',
    [switch]$SkipClaude,
    [switch]$SkipCodex,
    [switch]$SkipCopilot,
    [switch]$SkipOpencode,
    [switch]$SkipGrok,
    # opencode's default model may hit a plan usage limit; pass a free model such as opencode/step-5-preview-free.
    [string]$OpencodeModel
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
$inputRoot = Join-Path $work 'inputs'
New-Item -ItemType Directory -Path $inputRoot | Out-Null
$planPath = Join-Path $inputRoot 'distiller-sample.sqlplan'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..' 'tests' 'SqlHarness.Tests' 'Fixtures' 'distiller-sample.sqlplan') -Destination $planPath -ErrorAction Stop
$prompt = "Do not change any configuration or trust settings. Call sqlharness_capabilities from the acceptance MCP server exactly once, then call sqlharness_plan exactly once with file `"$planPath`", then reply with one word: done."
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
    if ($expected -ne '2026-07-28') {
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
        WHERE o.operation = 'plan'
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
    Add-Check $name 'journal operation linked' 'plan' $storedOperation ($storedOperation -eq 'plan')
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
    $config = @{ mcpServers = @{ acceptance = @{ command = $python; args = @('-I', $tap, $logPath, $clientHome, $Sqlharness, 'mcp', 'serve', $Profile, '--input-root', $inputRoot) } } } |
        ConvertTo-Json -Depth 8
    $configPath = Join-Path $work 'claude-mcp.json'
    Set-Content -LiteralPath $configPath -Value $config -Encoding utf8NoBOM
    Push-Location $work
    $savedSqlHarnessHome = $env:SQLHARNESS_HOME
    try {
        $env:SQLHARNESS_HOME = $clientHome
        & $claude.Source -p $prompt --mcp-config $configPath --strict-mcp-config --allowedTools 'mcp__acceptance__sqlharness_capabilities,mcp__acceptance__sqlharness_plan' --model haiku 2>$null | Out-Null
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
    $argsToml = '[' + ((@('-I', $tap, $logPath, $clientHome, $Sqlharness, 'mcp', 'serve', $Profile, '--input-root', $inputRoot) | ForEach-Object { "'" + ($_ -replace '\\', '/') + "'" }) -join ',') + ']'
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

if (-not $SkipCopilot) {
    $copilot = Get-Command copilot -ErrorAction Stop | Select-Object -First 1
    $logPath = Join-Path $work 'copilot-frames.log'
    $clientHome = Join-Path $work 'copilot-home'
    New-Item -ItemType Directory -Path $clientHome | Out-Null
    New-IsolatedTarget $clientHome $Profile
    $config = @{ mcpServers = @{ acceptance = @{ type = 'local'; command = $python; args = @('-I', $tap, $logPath, $clientHome, $Sqlharness, 'mcp', 'serve', $Profile, '--input-root', $inputRoot); tools = @('*') } } } |
        ConvertTo-Json -Depth 8
    $configPath = Join-Path $work 'copilot-mcp.json'
    Set-Content -LiteralPath $configPath -Value $config -Encoding utf8NoBOM
    Push-Location $work
    try {
        # No --model: Copilot picks its default model.
        & $copilot.Source -p $prompt --additional-mcp-config "@$configPath" --disable-builtin-mcps --allow-tool 'acceptance' --no-color 2>$null | Out-Null
        $clientExit = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    Add-Check 'Copilot CLI' 'client exit code' '0' $clientExit ($clientExit -eq 0)
    Test-Client 'Copilot CLI' $logPath '2026-07-28' $clientHome
}

if (-not $SkipOpencode) {
    $opencode = Get-Command opencode -ErrorAction Stop | Select-Object -First 1
    $logPath = Join-Path $work 'opencode-frames.log'
    $clientHome = Join-Path $work 'opencode-home'
    New-Item -ItemType Directory -Path $clientHome | Out-Null
    New-IsolatedTarget $clientHome $Profile
    $config = @{
        '$schema' = 'https://opencode.ai/config.json'
        mcp = @{ acceptance = @{ type = 'local'; enabled = $true; command = @($python, '-I', $tap, $logPath, $clientHome, $Sqlharness, 'mcp', 'serve', $Profile, '--input-root', $inputRoot) } }
    } | ConvertTo-Json -Depth 8
    $configPath = Join-Path $work 'opencode.json'
    Set-Content -LiteralPath $configPath -Value $config -Encoding utf8NoBOM
    Push-Location $work
    $savedOpencodeConfig = $env:OPENCODE_CONFIG
    try {
        $env:OPENCODE_CONFIG = $configPath
        $modelArgs = if ($OpencodeModel) { @('-m', $OpencodeModel) } else { @() }
        & $opencode.Source run --standalone --auto @modelArgs $prompt 2>$null | Out-Null
        $clientExit = $LASTEXITCODE
    } finally {
        $env:OPENCODE_CONFIG = $savedOpencodeConfig
        Pop-Location
    }
    Add-Check 'opencode' 'client exit code' '0' $clientExit ($clientExit -eq 0)
    Test-Client 'opencode' $logPath '2025-11-25' $clientHome
}

if (-not $SkipGrok) {
    $grok = Get-Command grok -ErrorAction Stop | Select-Object -First 1
    $logPath = Join-Path $work 'grok-frames.log'
    $clientHome = Join-Path $work 'grok-home'
    $grokProject = Join-Path $work 'grok-project'
    New-Item -ItemType Directory -Path $clientHome, $grokProject | Out-Null
    New-IsolatedTarget $clientHome $Profile
    $trusted = Join-Path $HOME '.grok/trusted_folders.toml'
    $trustedExisted = Test-Path -LiteralPath $trusted
    $trustedBefore = if ($trustedExisted) { [IO.File]::ReadAllBytes($trusted) } else { $null }
    $clientExit = $null
    $grokRan = $false
    Push-Location $grokProject
    try {
        & $grok.Source mcp add --scope project acceptance $python -- -I $tap $logPath $clientHome $Sqlharness mcp serve $Profile --input-root $inputRoot 2>$null | Out-Null
        # Only trust the scratch folder when the project config landed inside it;
        # otherwise stop rather than trusting a different folder.
        if (Test-Path -LiteralPath (Join-Path $grokProject '.grok/config.toml') -PathType Leaf) {
            # Grok loads project MCP config only in a trusted folder and has no one-off flag:
            # trust this scratch folder for the run and restore the file byte for byte afterwards.
            $entry = "`n[folders.'$grokProject']`ntrusted = true`ndecided_at = $([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())`n"
            [IO.File]::AppendAllText($trusted, $entry)
            # No model flag: Grok picks its default model.
            & $grok.Source -p $prompt --always-approve 2>$null | Out-Null
            $clientExit = $LASTEXITCODE
            $grokRan = $true
        }
    } finally {
        Pop-Location
        if ($trustedExisted) { [IO.File]::WriteAllBytes($trusted, $trustedBefore) } else { Remove-Item -LiteralPath $trusted -ErrorAction SilentlyContinue }
    }
    $restored = if ($trustedExisted) {
        [Linq.Enumerable]::SequenceEqual([byte[]][IO.File]::ReadAllBytes($trusted), [byte[]]$trustedBefore)
    } else { -not (Test-Path -LiteralPath $trusted) }
    Add-Check 'Grok Build' 'trusted_folders.toml restored' 'yes' $restored $restored
    Add-Check 'Grok Build' 'project config written in scratch folder' 'yes' $grokRan $grokRan
    if ($grokRan) {
        Add-Check 'Grok Build' 'client exit code' '0' $clientExit ($clientExit -eq 0)
        Test-Client 'Grok Build' $logPath '2025-11-25' $clientHome
    }
}

if ($SkipClaude -and $SkipCodex -and $SkipCopilot -and $SkipOpencode -and $SkipGrok) {
    Write-Host 'No clients selected; use this mode to smoke-test script startup and argument handling.'
}
$results | Format-Table -AutoSize | Out-String | Write-Host
Write-Host 'Acceptance artifacts remain in a temporary directory; paths and frame contents are suppressed.'
if ($results | Where-Object { -not $_.Pass }) { exit 1 }
exit 0
