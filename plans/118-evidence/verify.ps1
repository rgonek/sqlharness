$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& (Join-Path $root 'evaluate.ps1')
if ($LASTEXITCODE -ne 0) { throw "evaluate.ps1 failed with exit code $LASTEXITCODE" }
$fixture = Get-Content -LiteralPath (Join-Path $root 'cases.json') -Raw | ConvertFrom-Json -Depth 80
$actual = Get-Content -LiteralPath (Join-Path $root 'results.json') -Raw | ConvertFrom-Json -Depth 80
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Same($a, $b) { (ConvertTo-Json -InputObject $a -Depth 80 -Compress) -ceq (ConvertTo-Json -InputObject $b -Depth 80 -Compress) }
$expected = @{
    'equal conditions'=@{ availability='complete'; differences=@(); gaps=@(); omitted=0 }
    'changed setup hash'=@{ availability='unavailable'; differences=@(); gaps=@('conditions-unavailable'); omitted=0 }
    'changed retained setup text'=@{ availability='partial'; differences=@('setup.text|changed|measurement-condition||'); gaps=@('conditions-unavailable'); omitted=0 }
    'changed effective type'=@{ availability='complete'; differences=@('parameters.@id.effectiveType|changed|measurement-condition|@id|'); gaps=@(); omitted=0 }
    'added removed case'=@{ availability='complete'; differences=@('cases|added|measurement-condition||candidate-c','cases|removed|measurement-condition||baseline-b'); gaps=@(); omitted=0 }
    'informational repeat timeout'=@{ availability='complete'; differences=@('execution.repeat|changed|informational||','execution.timeoutSeconds|changed|informational||'); gaps=@(); omitted=0 }
    'changed comparison mode only'=@{ availability='complete'; differences=@('execution.comparisonMode|changed|informational||'); gaps=@(); omitted=0 }
    'hidden values with mismatching combined fingerprints'=@{ availability='unavailable'; differences=@(); gaps=@('conditions-unavailable'); omitted=0 }
    'unavailable old model'=@{ availability='unavailable'; differences=@(); gaps=@('legacy-model-unsupported'); omitted=0 }
    'omitted long fields'=@{ availability='unavailable'; differences=@(); gaps=@('field-omitted'); omitted=0 }
    'ambiguous case rename and order'=@{ availability='unavailable'; differences=@(); gaps=@('case-pairing-ambiguous'); omitted=0 }
    'cross-journal fingerprints without verified common key domain'=@{ availability='unavailable'; differences=@(); gaps=@('fingerprint-domain-unverified'); omitted=0 }
    'unresolved subject scope'=@{ availability='unavailable'; differences=@(); gaps=@('identity-unresolved'); omitted=0 }
    'identical SQL with different cases'=@{ availability='complete'; differences=@('cases|added|measurement-condition||new-case','cases|removed|measurement-condition||old-case'); gaps=@(); omitted=0 }
    'typed NULL versus absent value'=@{ availability='unavailable'; differences=@(); gaps=@('field-omitted'); omitted=0 }
    'unknown value versus empty collection'=@{ availability='unavailable'; differences=@(); gaps=@('field-omitted'); omitted=0 }
    'multibyte differences exceed caps'=@{ availability='partial'; differences=$null; gaps=@('budget-exceeded'); omitted=16 }
}
Assert ($fixture.cases.Count -eq $expected.Count) "Named fixture coverage mismatch: fixture=$($fixture.cases.Count), oracle=$($expected.Count)."
Assert ($actual.caseCount -eq $expected.Count) "Evaluator case count mismatch: $($actual.caseCount)."
foreach ($inputCase in $fixture.cases) {
    $want = $expected[$inputCase.name]
    $got = @($actual.results | Where-Object name -CEQ $inputCase.name | Select-Object -First 1)[0]
    Assert ($null -ne $got) "Missing result: $($inputCase.name)."
    Assert (Same $got.basis $inputCase.basis) "Authoritative basis changed: $($inputCase.name)."
    $e = $got.conditionExplanation
    Assert ($e.availability -ceq $want.availability) "Availability mismatch for $($inputCase.name): $($e.availability)."
    Assert (Same @($e.gaps | Sort-Object) @($want.gaps | Sort-Object)) "Gap mismatch for $($inputCase.name): $(@($e.gaps) -join ',')."
    Assert ($e.omittedDifferences -eq $want.omitted) "Omission count mismatch for $($inputCase.name): $($e.omittedDifferences)."
    $actualIds = @($e.differences | ForEach-Object {
        $caseIdentity = if ($_.caseIdentity -is [string]) { $_.caseIdentity } elseif ($_.caseIdentity) { "$($_.caseIdentity.before)>$($_.caseIdentity.after)" } else { '' }
        "$($_.field)|$($_.kind)|$($_.relation)|$($_.parameterIdentity)|$caseIdentity"
    } | Sort-Object)
    if ($null -ne $want.differences) {
        Assert (Same $actualIds @($want.differences | Sort-Object)) "Difference oracle mismatch for $($inputCase.name): $($actualIds -join ';')."
    } else {
        Assert ($e.differences.Count -eq 32) 'Difference item cap not enforced.'
        Assert ($e.omittedDifferences -gt 0) 'Multibyte overflow did not report omitted differences.'
        Assert (($e.differences | ForEach-Object parameterIdentity | Sort-Object -Unique).Count -eq 32) 'Overflow lost or duplicated retained item identities.'
    }
    Assert ($got.explanationUtf8Bytes -le 8192) "Explanation exceeds UTF-8 byte cap: $($inputCase.name)."
    $serialized = ConvertTo-Json -InputObject $e -Depth 80 -Compress
    Assert ([System.Text.Encoding]::UTF8.GetByteCount($serialized) -eq $got.explanationUtf8Bytes) "Reported UTF-8 byte count is not exact: $($inputCase.name)."
}
$hiddenResult = $actual.results | Where-Object name -CEQ 'hidden values with mismatching combined fingerprints'
$hiddenJson = ConvertTo-Json -InputObject $hiddenResult.conditionExplanation -Depth 80 -Compress
Assert ($hiddenJson -notmatch 'opaque-a|opaque-b|secret') 'Hidden parameter value, case label, or fingerprint leaked into explanation.'
$caseResults = @($actual.results | Where-Object { $_.name -in @('added removed case','identical SQL with different cases') })
Assert ((ConvertTo-Json -InputObject $caseResults -Depth 80 -Compress) -notmatch 'case-a|case-b|case-c|pairKey') 'Opaque case-pairing keys leaked into explanation output.'
$burstResult = $actual.results | Where-Object name -CEQ 'multibyte differences exceed caps'
Assert ($burstResult.conditionExplanation.differences[0].parameterIdentity -eq '@p00') 'First retained overflow item lost its identity.'
Assert ($burstResult.conditionExplanation.gaps -contains 'budget-exceeded') 'Overflow omitted its truthful budget gap.'
Write-Output "VERIFIED: $($expected.Count) named scenarios; verdict preservation, oracle categories, value protection, pairing, omissions, item limits, and UTF-8 limits passed."
