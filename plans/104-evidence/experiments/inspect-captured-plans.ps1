$ErrorActionPreference = 'Stop'
$summaryPaths = @((Join-Path $PSScriptRoot 'summary.json')) + @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'runs') -Filter 'summary.json' -Recurse -File | ForEach-Object FullName)
foreach ($summaryPath in $summaryPaths) {
    if (-not (Test-Path -LiteralPath $summaryPath)) { continue }
    $summary = Get-Content -Raw -LiteralPath $summaryPath | ConvertFrom-Json
    "SUMMARY=$summaryPath exit=$($summary.exitCode) started=$($summary.startedUtc) ended=$($summary.endedUtc)"
    $summary.cases | Where-Object { $_.name -in @('explicit_hex_temp_repeat3', 'conventional_temp_repeat3', 'mixed_variable_and_hex_temp_repeat3', 'session_temp_procedure_repeat3') } | ForEach-Object {
        foreach ($io in $_.statisticsIo) {
            $underscores = ([regex]::Matches($io.tableRaw, '_')).Count
            "IO=$($_.name) table=$($io.tableRaw) underscores=$underscores reads=$($io.logicalReads)"
        }
    }
    $summary.plans | Where-Object { $_.case -eq 'session_temp_procedure_repeat3' } | ForEach-Object {
        $objectText = ($_.objects | ForEach-Object { $_.table }) -join ','
        "PLAN=$($_.file) cached=$($_.retrievedFromCache) statement=$($_.statementText) objects=$objectText"
    }
}
