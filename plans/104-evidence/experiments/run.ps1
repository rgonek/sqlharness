param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Execute', 'AnalyzeCapturedRun')]
    [string]$Mode
)

$ErrorActionPreference = 'Stop'
$experimentRoot = $PSScriptRoot
$sqlPath = Join-Path $experimentRoot 'matrix.sql'
$sqlcmdPath = 'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\180\Tools\Binn\SQLCMD.EXE'

if ($Mode -eq 'AnalyzeCapturedRun') {
    $runDirectory = $experimentRoot
    $stdoutPath = Join-Path $runDirectory 'matrix.stdout.txt'
    $stderrPath = Join-Path $runDirectory 'matrix.stderr.txt'
    $capture = Get-Content -Raw -LiteralPath (Join-Path $experimentRoot 'successful-run-manifest.json') | ConvertFrom-Json
    $exitCode = $capture.exitCode
    $startedUtc = $capture.captureStartedUtc
    $endedUtc = $capture.captureCompletedUtc
}
else {
    $runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
    $runDirectory = Join-Path $experimentRoot "runs\$runId"
    New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
    $stdoutPath = Join-Path $runDirectory 'stdout.txt'
    $stderrPath = Join-Path $runDirectory 'stderr.txt'
    $startedUtc = [DateTime]::UtcNow.ToString('o')
    $process = Start-Process -FilePath $sqlcmdPath -ArgumentList @(
        '-S', 'localhost', '-E', '-C', '-d', 'tempdb', '-l', '5', '-t', '30',
        '-b', '-y', '0', '-i', $sqlPath
    ) -Wait -PassThru -NoNewWindow -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    $exitCode = $process.ExitCode
    $endedUtc = [DateTime]::UtcNow.ToString('o')
    $exitTempPath = Join-Path $runDirectory 'exit-code.txt.tmp'
    [IO.File]::WriteAllText($exitTempPath, "$exitCode`n", [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $exitTempPath -Destination (Join-Path $runDirectory 'exit-code.txt')
}

$sha256 = [Security.Cryptography.SHA256]::Create()
function Get-Sha256Hex([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    return ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
}

if ($Mode -eq 'AnalyzeCapturedRun' -and (Get-Sha256Hex $sqlPath) -ne $capture.inputSqlSha256) {
    throw 'matrix.sql does not match the successful capture manifest; refusing to summarize mismatched evidence.'
}

$stdout = [IO.File]::ReadAllText($stdoutPath)
$lines = $stdout -split "`r?`n"
$cases = [Collections.Generic.List[object]]::new()
$plans = [Collections.Generic.List[object]]::new()
$currentCase = $null
$planIndex = 0
$lineIndex = 0

foreach ($line in $lines) {
    $lineIndex++
    if ($line -match '^CASE=(.+)$') {
        $currentCase = [ordered]@{
            name = $Matches[1]
            statisticsIo = [Collections.Generic.List[object]]::new()
            metadataRows = [Collections.Generic.List[string]]::new()
        }
        $cases.Add($currentCase)
        continue
    }

    if ($null -eq $currentCase) { continue }

    if ($line -match "^Table '((?:''|[^'])+)'\.(.*)$") {
        $rawTable = $Matches[1].Replace("''", "'")
        $counterText = $Matches[2]
        $scan = [regex]::Match($counterText, 'Scan count (\d+)')
        $logical = [regex]::Match($counterText, 'logical reads (\d+)')
        $lob = [regex]::Match($counterText, 'lob logical reads (\d+)')
        if ($scan.Success -and $logical.Success) {
            $currentCase.statisticsIo.Add([ordered]@{
                tableRaw = $rawTable
                scanCount = [int64]$scan.Groups[1].Value
                logicalReads = [int64]$logical.Groups[1].Value
                lobLogicalReads = if ($lob.Success) { [int64]$lob.Groups[1].Value } else { $null }
                sourceLine = $lineIndex
            })
        }
    }

    if ($line -match '^(?:CATALOG_|PROC_CATALOG_)') {
        $currentCase.metadataRows.Add($line.Trim())
    }

    if ($line.StartsWith('<ShowPlanXML', [StringComparison]::Ordinal)) {
        $planIndex++
        $planFile = "plan-{0:D3}.xml" -f $planIndex
        [IO.File]::WriteAllText((Join-Path $runDirectory $planFile), $line, [Text.UTF8Encoding]::new($false))
        try {
            [xml]$planXml = $line
            $objects = @($planXml.SelectNodes('//*[local-name()="Object"]') | ForEach-Object {
                [ordered]@{
                    table = $_.GetAttribute('Table')
                    database = $_.GetAttribute('Database')
                    schema = $_.GetAttribute('Schema')
                }
            })
            $statement = $planXml.SelectSingleNode('//*[local-name()="StmtSimple"]')
            $plans.Add([ordered]@{
                file = $planFile
                case = $currentCase.name
                statementText = if ($null -ne $statement) { $statement.GetAttribute('StatementText') } else { $null }
                retrievedFromCache = if ($null -ne $statement) { $statement.GetAttribute('RetrievedFromCache') } else { $null }
                objects = $objects
            })
        }
        catch {
            $plans.Add([ordered]@{ file = $planFile; case = $currentCase.name; parseError = $_.Exception.Message })
        }
    }
}

$manifest = [ordered]@{
    format = 'sqlharness-plan104-experiment-v1'
    sqlServer = 'localhost/tempdb (Windows integrated auth; no credentials recorded)'
    sqlcmdArguments = @('-S localhost', '-E', '-C', '-d tempdb', '-l 5', '-t 30', '-b', '-y 0')
    startedUtc = $startedUtc
    endedUtc = $endedUtc
    exitCode = $exitCode
    inputSqlSha256 = Get-Sha256Hex $sqlPath
    stdoutSha256 = Get-Sha256Hex $stdoutPath
    stderrSha256 = Get-Sha256Hex $stderrPath
    rawStdout = [IO.Path]::GetFileName($stdoutPath)
    rawStderr = [IO.Path]::GetFileName($stderrPath)
    cases = @($cases)
    plans = @($plans)
}

$jsonPath = Join-Path $runDirectory 'summary.json'
$jsonTempPath = "$jsonPath.tmp"
[IO.File]::WriteAllText($jsonTempPath, ($manifest | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $jsonTempPath -Destination $jsonPath -Force
Write-Output "mode=$Mode exit=$exitCode cases=$($cases.Count) plans=$($plans.Count) summary=$jsonPath"
if ($Mode -eq 'Execute' -and $exitCode -ne 0) { exit $exitCode }
