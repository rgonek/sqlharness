$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& (Join-Path $root 'evaluate.ps1')
if ($LASTEXITCODE -ne 0) { throw "evaluate.ps1 failed with exit code $LASTEXITCODE" }
$fixture = Get-Content -LiteralPath (Join-Path $root 'cases.json') -Raw | ConvertFrom-Json -Depth 80
$actual = Get-Content -LiteralPath (Join-Path $root 'results.json') -Raw | ConvertFrom-Json -Depth 80
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Json($value) { ConvertTo-Json -InputObject $value -Depth 80 -Compress }
function Has-Property($object, [string]$name) { return ($null -ne $object -and $object.PSObject.Properties.Name -contains $name) }
function Difference-Signature($item) {
    $parameter = if (Has-Property $item 'parameterIdentity') { Json $item.parameterIdentity } else { '<absent>' }
    $case = if (Has-Property $item 'caseIdentity') { Json $item.caseIdentity } else { '<absent>' }
    $before = if (Has-Property $item 'before') { Json $item.before } else { '<absent>' }
    $after = if (Has-Property $item 'after') { Json $item.after } else { '<absent>' }
    "$($item.field)|$($item.kind)|$($item.relation)|$parameter|$case|$before|$after"
}
$expected = @{
    'equal conditions'=@{ availability='complete'; differences=@(); gaps=@(); omitted=0 }
    'changed setup hash'=@{ availability='unavailable'; differences=@(); gaps=@('conditions-unavailable','field-omitted'); omitted=0 }
    'changed retained setup text'=@{ availability='partial'; differences=@('setup.text.value|changed|measurement-condition|<absent>|<absent>|"SET NOCOUNT ON"|"SET XACT_ABORT ON"'); gaps=@('conditions-unavailable'); omitted=0 }
    'changed effective type'=@{ availability='complete'; differences=@('parameters.@id.effectiveType|changed|measurement-condition|"@id"|<absent>|"int"|"bigint"'); gaps=@(); omitted=0 }
    'added removed case'=@{ availability='complete'; differences=@('cases|added|measurement-condition|<absent>|"candidate-c"|<absent>|<absent>','cases|removed|measurement-condition|<absent>|"baseline-b"|<absent>|<absent>'); gaps=@(); omitted=0 }
    'informational repeat timeout'=@{ availability='complete'; differences=@('execution.repeat|changed|informational|<absent>|<absent>|2|5','execution.timeoutSeconds|changed|informational|<absent>|<absent>|20|40'); gaps=@(); omitted=0 }
    'changed comparison mode only'=@{ availability='complete'; differences=@('execution.comparisonMode|changed|informational|<absent>|<absent>|"ordered"|"unordered"'); gaps=@(); omitted=0 }
    'hidden values with mismatching combined fingerprints'=@{ availability='unavailable'; differences=@(); gaps=@('conditions-unavailable','field-omitted'); omitted=0 }
    'unavailable old model'=@{ availability='unavailable'; differences=@(); gaps=@('legacy-model-unsupported'); omitted=0 }
    'omitted long fields'=@{ availability='unavailable'; differences=@(); gaps=@('conditions-unavailable','field-omitted'); omitted=0 }
    'ambiguous case rename and order'=@{ availability='unavailable'; differences=@(); gaps=@('case-pairing-ambiguous','conditions-unavailable'); omitted=0 }
    'cross-journal fingerprints without verified common key domain'=@{ availability='unavailable'; differences=@(); gaps=@('conditions-unavailable','fingerprint-domain-unverified'); omitted=0 }
    'unresolved subject scope'=@{ availability='unavailable'; differences=@(); gaps=@('identity-unresolved'); omitted=0 }
    'identical SQL with different cases'=@{ availability='complete'; differences=@('cases|added|measurement-condition|<absent>|"new-case"|<absent>|<absent>','cases|removed|measurement-condition|<absent>|"old-case"|<absent>|<absent>'); gaps=@(); omitted=0 }
    'typed NULL versus absent value'=@{ availability='unavailable'; differences=@(); gaps=@('conditions-unavailable','field-omitted'); omitted=0 }
    'unknown value versus empty collection'=@{ availability='unavailable'; differences=@(); gaps=@('conditions-unavailable','field-omitted'); omitted=0 }
    'multibyte differences exceed caps'=@{ availability='partial'; differences=$null; gaps=@('budget-exceeded'); omitted=16 }
    'informational-only with unexplained measurement mismatch'=@{ availability='partial'; differences=@('execution.repeat|changed|informational|<absent>|<absent>|2|4'); gaps=@('conditions-unavailable'); omitted=0 }
    'absent parameters collection versus empty'=@{ availability='unavailable'; differences=@(); gaps=@('conditions-unavailable','field-omitted'); omitted=0 }
    'absent conditions property versus empty object'=@{ availability='unavailable'; differences=@(); gaps=@('conditions-unavailable'); omitted=0 }
    'paired case parameter difference with opaque pair key'=@{ availability='complete'; differences=@('cases.parameters.@color.value|changed|measurement-condition|"@color"|{"before":"baseline-case","after":"candidate-case"}|"red"|"blue"'); gaps=@(); omitted=0 }
    'typed NULL retained before and after'=@{ availability='complete'; differences=@('parameters.@value.value|changed|measurement-condition|"@value"|<absent>|null|7'); gaps=@(); omitted=0 }
}
Assert ($fixture.cases.Count -eq $expected.Count) "Named fixture coverage mismatch: fixture=$($fixture.cases.Count), oracle=$($expected.Count)."
Assert ($actual.caseCount -eq $expected.Count) "Evaluator case count mismatch: $($actual.caseCount)."
foreach ($inputCase in $fixture.cases) {
    $want = $expected[$inputCase.name]
    Assert ($null -ne $want) "No independent oracle row for: $($inputCase.name)."
    $got = @($actual.results | Where-Object name -CEQ $inputCase.name | Select-Object -First 1)[0]
    Assert ($null -ne $got) "Missing result: $($inputCase.name)."
    Assert ((Json $got.basis) -ceq (Json $inputCase.basis)) "Authoritative basis changed: $($inputCase.name)."
    $e = $got.conditionExplanation
    Assert ($e.availability -ceq $want.availability) "Availability mismatch for $($inputCase.name): $($e.availability)."
    Assert ((Json @($e.gaps | Sort-Object)) -ceq (Json @($want.gaps | Sort-Object))) "Gap mismatch for $($inputCase.name): $(@($e.gaps) -join ',')."
    Assert ($e.omittedDifferences -eq $want.omitted) "Omission count mismatch for $($inputCase.name): $($e.omittedDifferences)."
    $actualSignatures = @($e.differences | ForEach-Object { Difference-Signature $_ } | Sort-Object)
    if ($null -ne $want.differences) {
        Assert ((Json $actualSignatures) -ceq (Json @($want.differences | Sort-Object))) "Difference oracle mismatch for $($inputCase.name): $($actualSignatures -join ';')."
    } else {
        Assert ($e.differences.Count -eq 32) 'Difference item cap not enforced.'
        Assert ($e.omittedDifferences -eq 16) 'Multibyte overflow omission count mismatch.'
        $expectedRetained = @('@p00','@p01','@p02','@p03','@p04','@p05','@p06','@p07','@p08','@p09','@p10','@p11','@p12','@p13','@p14','@p15','@p16','@p17','@p18','@p19','@p20','@p21','@p22','@p23','@p24','@p25','@p26','@p27','@p28','@p29','@p30','@p31')
        $actualRetained = @($e.differences | ForEach-Object parameterIdentity | Sort-Object)
        Assert ((Json $actualRetained) -ceq (Json $expectedRetained)) 'Overflow retained identity set does not exactly match the expected first 32 parameter identities.'
        Assert (@($e.differences | Where-Object { (Has-Property $_ 'before') -or (Has-Property $_ 'after') }).Count -eq 0) 'Overflow value displays should be dropped before item identities.'
        Assert (@($e.differences | Where-Object { $_.field -notmatch '^parameters\.@p\d{2}\.value$' }).Count -eq 0) 'Overflow contains an unexpected field path.'
        Assert (($e.gaps -contains 'budget-exceeded')) 'Overflow omitted its truthful budget gap.'
    }
    Assert ($got.explanationUtf8Bytes -le 8192) "Explanation exceeds UTF-8 byte cap: $($inputCase.name)."
    $serialized = Json $e
    Assert ([System.Text.Encoding]::UTF8.GetByteCount($serialized) -eq $got.explanationUtf8Bytes) "Reported UTF-8 byte count is not exact: $($inputCase.name)."
}
$modeInput = $fixture.cases | Where-Object name -CEQ 'changed comparison mode only'
Assert ($modeInput.before.regularFingerprint -cne $modeInput.after.regularFingerprint) 'Comparison-mode fixture must differ in the regular fingerprint.'
Assert ($modeInput.before.measurementFingerprint -ceq $modeInput.after.measurementFingerprint) 'Comparison-mode fixture must preserve the measurement fingerprint.'
$typedNull = $actual.results | Where-Object name -CEQ 'typed NULL retained before and after'
Assert ((Has-Property $typedNull.conditionExplanation.differences[0] 'before') -and $null -eq $typedNull.conditionExplanation.differences[0].before) 'Present typed NULL must remain a present null before-value.'
$missingValueInput = $fixture.cases | Where-Object name -CEQ 'typed NULL versus absent value'
Assert (-not (Has-Property $missingValueInput.after.conditions.parameters[0] 'value')) 'Missing-value case must use a genuinely absent JSON property.'
$omittedInput = $fixture.cases | Where-Object name -CEQ 'omitted long fields'
Assert (-not (Has-Property $omittedInput.before.conditions.setup 'text')) 'Omitted setup case must omit the JSON property.'
Assert (-not (Has-Property $omittedInput.before.conditions.parameters[0] 'value')) 'Omitted parameter case must omit the JSON property.'
$absentCollectionInput = $fixture.cases | Where-Object name -CEQ 'absent parameters collection versus empty'
Assert (-not (Has-Property $absentCollectionInput.after.conditions 'parameters') -and @($absentCollectionInput.before.conditions.parameters).Count -eq 0) 'Missing and empty parameter collections are not distinct fixture states.'
$hiddenResult = $actual.results | Where-Object name -CEQ 'hidden values with mismatching combined fingerprints'
$hiddenJson = Json $hiddenResult.conditionExplanation
Assert ($hiddenJson -notmatch 'opaque-a|opaque-b|secret') 'Hidden parameter value, label, or fingerprint leaked into explanation.'
$pairedResult = $actual.results | Where-Object name -CEQ 'paired case parameter difference with opaque pair key'
$pairedJson = Json $pairedResult.conditionExplanation
Assert ($pairedJson -notmatch 'opaque-journal-key-DO-NOT-LEAK|pairKey') 'Opaque pair key leaked into a field path or explanation.'
Assert ($pairedResult.conditionExplanation.differences[0].field -ceq 'cases.parameters.@color.value') 'Paired case field path is not key-free.'
Assert ($actual.results | Where-Object name -CEQ 'changed comparison mode only' | ForEach-Object { $_.conditionExplanation.gaps.Count -eq 0 }) 'Comparison mode incorrectly changed the measurement-key explanation.'
$measurementMismatch = $actual.results | Where-Object name -CEQ 'informational-only with unexplained measurement mismatch'
Assert ($measurementMismatch.conditionExplanation.gaps -contains 'conditions-unavailable') 'Informational-only differences suppressed an unexplained measurement-fingerprint mismatch.'
Write-Output "VERIFIED: $($expected.Count) named scenarios; exact oracle values, basis preservation, absent-property states, fingerprint semantics, pairing non-disclosure, omissions, and UTF-8 limits passed."
