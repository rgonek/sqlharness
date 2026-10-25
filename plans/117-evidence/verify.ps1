[CmdletBinding()]
param([string]$EvidenceDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$casesPath = Join-Path $EvidenceDirectory 'cases.json'
$resultsPath = Join-Path $EvidenceDirectory 'results.json'
$logsPath = Join-Path $EvidenceDirectory 'logs'

function Assert-Study([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "VERIFY FAILED: $Message" }
}
function Json-Compact($Value) { ConvertTo-Json -InputObject $Value -Depth 100 -Compress }
function Assert-Keys($Value, [string[]]$Allowed, [string]$Path) {
    if ($Value -is [System.Collections.IDictionary]) {
        foreach ($key in $Value.Keys) {
            Assert-Study ($Allowed -contains [string]$key) "unexpected output field at ${Path}: $key"
            $childPath = "$Path.$key"
            if ($Value[$key] -is [System.Collections.IList]) { $childPath += '[]' }
            Assert-Keys $Value[$key] $script:KeySets[$childPath] $childPath
        }
    } elseif ($Value -is [System.Collections.IList]) {
        foreach ($entry in $Value) { Assert-Keys $entry $Allowed $Path }
    }
}

foreach ($path in @($casesPath,$resultsPath)) {
    Assert-Study (Test-Path -LiteralPath $path -PathType Leaf) "missing required evidence file $path"
}
$casesDoc = Get-Content -LiteralPath $casesPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
$resultsDoc = Get-Content -LiteralPath $resultsPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
Assert-Study ($casesDoc.formatVersion -eq 1 -and $resultsDoc.formatVersion -eq 1) 'unsupported evidence format version'
Assert-Study ($casesDoc.cases.Count -eq 31) 'cases.json must contain 31 independently expected scenarios'
Assert-Study ($resultsDoc.results.Count -eq $casesDoc.cases.Count) 'results are missing or add scenarios'
Assert-Study ($resultsDoc.accounting.runMetadataCapBytes -eq 32768 -and $resultsDoc.accounting.cellMetadataCapBytes -eq 262144) 'metadata budget differs from the candidate limits'
Assert-Study ($resultsDoc.accounting.resultSetDescriptorCap -eq 64 -and $resultsDoc.accounting.columnsPerSetCap -eq 256 -and $resultsDoc.accounting.itemCap -eq 16) 'descriptor or emission cap differs from the candidate limits'
Assert-Study ($resultsDoc.accounting.metadataEstimator -like '*not production serialization or heap allocation*') 'capture estimates are not labeled as non-production estimates'

$script:KeySets = @{
    '$' = @('formatVersion','purpose','accounting','results')
    '$.accounting' = @('runMetadataCapBytes','cellMetadataCapBytes','resultSetDescriptorCap','columnsPerSetCap','itemCap','metadataEstimator')
    '$.results[]' = @('caseId','artifactId','matrixCell','mode','equivalence','equivalenceDiagnostics','captureOutcome','captureEstimate','serializationViews')
    '$.results[].equivalence' = @('equivalent','baselineStable','candidateStable','baselineOnlyCount','candidateOnlyCount','nullabilityEquivalent')
    '$.results[].equivalenceDiagnostics' = @('version','availability','items','omissionCounts','gapReasons')
    '$.results[].equivalenceDiagnostics.omissionCounts' = @('items','resultSetDescriptors','columns','metadataBytes')
    '$.results[].equivalenceDiagnostics.items[]' = @('pair','resultSetOrdinal','kind','columnOrdinal','rowOrdinal','policy')
    '$.results[].equivalenceDiagnostics.items[].pair' = @('left','right')
    '$.results[].equivalenceDiagnostics.items[].pair.left' = @('artifactId','caseId','matrixCell','side','repetition')
    '$.results[].equivalenceDiagnostics.items[].pair.right' = @('artifactId','caseId','matrixCell','side','repetition')
    '$.results[].captureEstimate' = @('maximumRetainedRunMetadataBytes','retainedCellMetadataBytes')
    '$.results[].serializationViews[]' = @('budgetBytes','serializedBytes','emittedItems','omittedItems','diagnosticsObjectOmitted','projectedEnvelope')
    '$.results[].serializationViews[].projectedEnvelope' = @('artifactId','caseId','matrixCell','equivalence','equivalenceDiagnostics','diagnosticsOmitted','diagnosticItemsOmitted')
    '$.results[].serializationViews[].projectedEnvelope.equivalence' = @('equivalent','baselineStable','candidateStable','baselineOnlyCount','candidateOnlyCount','nullabilityEquivalent')
    '$.results[].serializationViews[].projectedEnvelope.equivalenceDiagnostics' = @('version','availability','items','omissionCounts','gapReasons')
    '$.results[].serializationViews[].projectedEnvelope.equivalenceDiagnostics.omissionCounts' = @('items','resultSetDescriptors','columns','metadataBytes')
    '$.results[].serializationViews[].projectedEnvelope.equivalenceDiagnostics.items[]' = @('pair','resultSetOrdinal','kind','columnOrdinal','rowOrdinal','policy')
    '$.results[].serializationViews[].projectedEnvelope.equivalenceDiagnostics.items[].pair' = @('left','right')
    '$.results[].serializationViews[].projectedEnvelope.equivalenceDiagnostics.items[].pair.left' = @('artifactId','caseId','matrixCell','side','repetition')
    '$.results[].serializationViews[].projectedEnvelope.equivalenceDiagnostics.items[].pair.right' = @('artifactId','caseId','matrixCell','side','repetition')
}

$outputJson = Get-Content -LiteralPath $resultsPath -Raw -Encoding utf8
Assert-Keys $resultsDoc $script:KeySets['$'] '$'
foreach ($case in $casesDoc.cases) {
    Assert-Study ($case.expected -is [System.Collections.IDictionary] -and $case.expected.Contains('equivalent')) "case $($case.caseId) has no independent expected verdict"
    Assert-Study (-not $outputJson.Contains([string]$case.sqlHash)) "results publish SQL hash for $($case.caseId)"
}

for ($i=0; $i -lt $casesDoc.cases.Count; $i++) {
    $case = $casesDoc.cases[$i]
    $expected = $case.expected
    $actual = $resultsDoc.results[$i]
    Assert-Study ($actual.caseId -eq $case.caseId -and $actual.artifactId -eq $case.artifactId) "result identity mismatch at scenario $i"
    Assert-Study ($actual.matrixCell -eq $case.matrixCell) "matrix-cell identity mismatch for $($case.caseId)"
    $eq = $actual.equivalence
    foreach ($field in @('equivalent','baselineStable','candidateStable','nullabilityEquivalent','baselineOnlyCount','candidateOnlyCount')) {
        if ($expected.Contains($field)) {
            Assert-Study ((Json-Compact $eq[$field]) -ceq (Json-Compact $expected[$field])) "$($case.caseId): $field changed (expected $(Json-Compact $expected[$field]), got $(Json-Compact $eq[$field]))"
        }
    }
    $diag = $actual.equivalenceDiagnostics
    Assert-Study ($diag.version -eq 1) "$($case.caseId): diagnostics version missing"
    if ($expected.Contains('availability')) { Assert-Study ($diag.availability -eq $expected.availability) "$($case.caseId): availability differs" }
    if ($expected.Contains('gapReasons')) { Assert-Study ((Json-Compact @($diag.gapReasons)) -ceq (Json-Compact @($expected.gapReasons))) "$($case.caseId): gap reasons differ" }
    if ($expected.Contains('items')) {
        Assert-Study ($diag.items.Count -eq $expected.items.Count) "$($case.caseId): item count differs"
        for ($j=0; $j -lt $expected.items.Count; $j++) {
            $want=$expected.items[$j]; $got=$diag.items[$j]
            foreach ($field in @('kind','resultSetOrdinal','columnOrdinal','rowOrdinal','policy')) {
                if ($want.Contains($field)) { Assert-Study ((Json-Compact $got[$field]) -ceq (Json-Compact $want[$field])) "$($case.caseId): item $j field $field differs" }
            }
            if ($want.Contains('pair')) {
                Assert-Study ($got.pair.left.side -eq $want.pair[0].side -and $got.pair.left.repetition -eq $want.pair[0].repetition -and
                    $got.pair.right.side -eq $want.pair[1].side -and $got.pair.right.repetition -eq $want.pair[1].repetition) "$($case.caseId): same-side/unstable pair provenance differs"
            }
            if ($got.rowOrdinal -ne $null -and $case.mode -ne 'ordered') { throw "VERIFY FAILED: $($case.caseId) has positional evidence in $($case.mode) mode" }
            Assert-Study ($got.pair.left.artifactId -eq $case.artifactId -and $got.pair.right.artifactId -eq $case.artifactId -and
                $got.pair.left.caseId -eq $case.caseId -and $got.pair.right.caseId -eq $case.caseId -and
                $got.pair.left.matrixCell -eq $case.matrixCell -and $got.pair.right.matrixCell -eq $case.matrixCell) "$($case.caseId): item does not identify the original run pair"
        }
    }
    if ($expected.omissions -is [long] -or $expected.omissions -is [int]) {
        Assert-Study ($diag.omissionCounts.items -eq [long]$expected.omissions) "$($case.caseId): item omission count differs"
    } elseif ($expected.omissions -is [System.Collections.IDictionary]) {
        foreach ($field in $expected.omissions.Keys) {
            $value=$expected.omissions[$field]
            if ($value -is [string] -and $value.StartsWith('positive;')) {
                Assert-Study ($diag.omissionCounts[$field] -gt 0) "$($case.caseId): expected positive $field omission count"
            } else {
                Assert-Study ($diag.omissionCounts[$field] -eq $value) "$($case.caseId): $field omission count differs"
            }
        }
    }
    if ($expected.Contains('omissionsKnownBeforeProjection')) { Assert-Study ($expected.omissionsKnownBeforeProjection -eq $true) "$($case.caseId): oracle does not require pre-projection omission counting" }
    if ($expected.Contains('verdictUnchangedByCap')) { Assert-Study ((Json-Compact $eq.equivalent) -ceq (Json-Compact $expected.equivalent)) "$($case.caseId): cap changed the authoritative verdict" }
    if ($case.mode -eq 'off') { Assert-Study ($diag.availability -eq 'unavailable' -and $diag.gapReasons -contains 'comparison-off' -and $eq.equivalent -eq $null) 'off mode must remain unavailable with no verdict' }
    if ($case.legacyArtifact) { Assert-Study ($diag.availability -eq 'unavailable' -and $diag.gapReasons -contains 'diagnostic-metadata-absent' -and $diag.items.Count -eq 0) 'legacy missing metadata must not be empty complete success' }
    Assert-Study ($diag.items.Count -le 16) "$($case.caseId): item cap exceeded"
    Assert-Study ($actual.captureEstimate.maximumRetainedRunMetadataBytes -le 32768 -and $actual.captureEstimate.retainedCellMetadataBytes -le 262144) "$($case.caseId): retained capture estimate exceeds candidate cap"
    Assert-Study ($actual.serializationViews.Count -eq 2) "$($case.caseId): serializer budgets missing"
    foreach ($view in $actual.serializationViews) {
        Assert-Study (@(4096,65536) -contains $view.budgetBytes) "$($case.caseId): unexpected output budget"
        Assert-Study ($view.serializedBytes -le $view.budgetBytes -and $view.emittedItems -le 16 -and $view.omittedItems -ge 0) "$($case.caseId): bounded-output assertion failed"
        $projected=$view.projectedEnvelope
        Assert-Study ($projected.artifactId -ceq $case.artifactId -and $projected.caseId -ceq $case.caseId -and $projected.matrixCell -eq $case.matrixCell) "$($case.caseId): projected envelope lost outer identity"
        Assert-Study ((Json-Compact $projected.equivalence.equivalent) -ceq (Json-Compact $eq.equivalent)) "$($case.caseId): response projection changed verdict"
        if ($view.diagnosticsObjectOmitted) {
            Assert-Study (-not $projected.Contains('equivalenceDiagnostics') -and $projected.diagnosticsOmitted -eq 'response-budget-diagnostics-omitted') "$($case.caseId): whole-object fallback marker is missing or malformed"
            Assert-Study ($projected.diagnosticItemsOmitted -eq $view.omittedItems -and $view.emittedItems -eq 0) "$($case.caseId): whole-object omission count is not truthful"
        } else {
            Assert-Study ($projected.Contains('equivalenceDiagnostics') -and -not $projected.Contains('diagnosticsOmitted')) "$($case.caseId): partial projection marker is malformed"
            $projectedDiag=$projected.equivalenceDiagnostics
            $responseDrops=$diag.items.Count-$projectedDiag.items.Count
            Assert-Study ($responseDrops -ge 0 -and $projectedDiag.items.Count -eq $view.emittedItems) "$($case.caseId): projection did not retain whole items"
            for ($k=0; $k -lt $projectedDiag.items.Count; $k++) {
                Assert-Study ((Json-Compact $projectedDiag.items[$k]) -ceq (Json-Compact $diag.items[$k])) "$($case.caseId): output-budget projection truncated or changed item $k"
            }
            $knownOmissions=$diag.omissionCounts.items+$responseDrops
            Assert-Study ($view.omittedItems -eq $knownOmissions -and $projectedDiag.omissionCounts.items -eq $knownOmissions) "$($case.caseId): response-budget omissions are not exact"
            $hasBudgetGap=@($projectedDiag.gapReasons) -contains 'response-budget-items-omitted'
            Assert-Study (($responseDrops -gt 0) -eq $hasBudgetGap) "$($case.caseId): response-budget gap is missing or spurious"
            if ($responseDrops -gt 0) { Assert-Study ($projectedDiag.availability -eq 'partial') "$($case.caseId): response omission still claims complete availability" }
        }
    }
    if ($expected.Contains('serializationViews')) {
        Assert-Study ($actual.serializationViews.Count -eq $expected.serializationViews.Count) "$($case.caseId): serializer expectation count differs"
        for ($j=0; $j -lt $expected.serializationViews.Count; $j++) {
            foreach ($field in @('budgetBytes','emittedItems','omittedItems','diagnosticsObjectOmitted')) {
                Assert-Study ($actual.serializationViews[$j][$field] -eq $expected.serializationViews[$j][$field]) "$($case.caseId): $field differs at budget view $j"
            }
        }
    }
    if ($case.caseId -eq 'eq-duplicate-multiplicity-003') {
        Assert-Study ($diag.availability -eq 'partial' -and $diag.gapReasons -contains 'row-difference-location-unsupported-for-multiset' -and $diag.items.Count -eq 0) 'multiset row mismatch must have an explicit unsupported gap'
    }
}

$log = "verify.ps1: PASS; scenarios=$($casesDoc.cases.Count); independent expected verdicts and locations checked; pair provenance, omission counts, no-value/hash shape, descriptor/output caps, off/legacy gaps and 4096/65536 views checked."
New-Item -ItemType Directory -Path $logsPath -Force | Out-Null
Set-Content -LiteralPath (Join-Path $logsPath 'verify.log') -Value $log -Encoding utf8
Write-Output $log
