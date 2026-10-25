param([string]$InputPath = (Join-Path $PSScriptRoot 'cases.json'), [string]$OutputPath = (Join-Path $PSScriptRoot 'results.json'))
$ErrorActionPreference = 'Stop'
$MaxDifferences = 32
$MaxGaps = 16
$MaxBytes = 8192

function Json($value) { ConvertTo-Json -InputObject $value -Depth 80 -Compress }
function Utf8Size([string]$value) { [System.Text.Encoding]::UTF8.GetByteCount($value) }
function Has-Property($object, [string]$name) { return ($null -ne $object -and $object.PSObject.Properties.Name -contains $name) }
function Add-Gap($list, [string]$code) { if (-not $list.Contains($code)) { $list.Add($code) } }
function Add-Difference($list, [string]$field, [string]$kind, [string]$relation, $caseIdentity, $parameterIdentity, $before, $after, [bool]$hasBefore = $false, [bool]$hasAfter = $false) {
    $item = [ordered]@{ field = $field }
    if ($null -ne $caseIdentity) { $item.caseIdentity = $caseIdentity }
    if ($null -ne $parameterIdentity) { $item.parameterIdentity = $parameterIdentity }
    $item.kind = $kind; $item.relation = $relation
    if ($hasBefore) { $item.before = $before }
    if ($hasAfter) { $item.after = $after }
    $list.Add([pscustomobject]$item)
}
function Display-Value($wrapper) {
    if ($null -eq $wrapper -or -not (Has-Property $wrapper 'state')) { return [pscustomobject]@{ allowed = $false } }
    if ($wrapper.state -in @('retained','typed-null') -and (Has-Property $wrapper 'value')) { return [pscustomobject]@{ allowed = $true; value = $wrapper.value } }
    return [pscustomobject]@{ allowed = $false }
}
function Compare-Scalar($list, $gaps, [string]$field, $left, $right, [bool]$leftPresent, [bool]$rightPresent, [string]$relation, $caseIdentity, $parameterIdentity) {
    if (-not $leftPresent -or -not $rightPresent) { Add-Gap $gaps 'field-omitted'; return }
    if ((Json $left) -ceq (Json $right)) {
        if ($field -match '\.value$' -and -not (Display-Value $left).allowed) { Add-Gap $gaps 'field-omitted' }
        return
    }
    if ($field -match '\.value$') {
        $leftShown = Display-Value $left; $rightShown = Display-Value $right
        if (-not $leftShown.allowed -or -not $rightShown.allowed) { Add-Gap $gaps 'field-omitted'; return }
        Add-Difference $list $field 'changed' $relation $caseIdentity $parameterIdentity $leftShown.value $rightShown.value $true $true
        return
    }
    if ($field -match 'setup\.hash$|contentHash|fingerprint') { Add-Gap $gaps 'conditions-unavailable'; return }
    Add-Difference $list $field 'changed' $relation $caseIdentity $parameterIdentity $left $right $true $true
}
function Compare-Parameters($list, $gaps, $leftItems, $rightItems, [string]$fieldPrefix, $caseIdentity) {
    $leftMap = @{}; $rightMap = @{}
    foreach ($p in @($leftItems | Where-Object { $null -ne $_ })) { if (-not (Has-Property $p 'identity') -or $leftMap.ContainsKey([string]$p.identity)) { Add-Gap $gaps 'identity-unresolved'; continue }; $leftMap[[string]$p.identity] = $p }
    foreach ($p in @($rightItems | Where-Object { $null -ne $_ })) { if (-not (Has-Property $p 'identity') -or $rightMap.ContainsKey([string]$p.identity)) { Add-Gap $gaps 'identity-unresolved'; continue }; $rightMap[[string]$p.identity] = $p }
    foreach ($id in @(@($leftMap.Keys) + @($rightMap.Keys) | Sort-Object -Unique)) {
        $l = $leftMap[$id]; $r = $rightMap[$id]
        $path = if ($fieldPrefix) { "$fieldPrefix.parameters.$id" } else { "parameters.$id" }
        if ($null -eq $l) { Add-Difference $list $path 'added' 'measurement-condition' $caseIdentity $id $null $null; continue }
        if ($null -eq $r) { Add-Difference $list $path 'removed' 'measurement-condition' $caseIdentity $id $null $null; continue }
        foreach ($metadata in @('effectiveType','kind','count','columns','columnTypes')) {
            $leftHas = Has-Property $l $metadata; $rightHas = Has-Property $r $metadata
            if ($leftHas -or $rightHas) { Compare-Scalar $list $gaps "$path.$metadata" $l.$metadata $r.$metadata $leftHas $rightHas 'measurement-condition' $caseIdentity $id }
        }
        $leftHasValue = Has-Property $l 'value'; $rightHasValue = Has-Property $r 'value'
        if ($leftHasValue -or $rightHasValue) { Compare-Scalar $list $gaps "$path.value" $l.value $r.value $leftHasValue $rightHasValue 'measurement-condition' $caseIdentity $id }
        else { Add-Gap $gaps 'field-omitted' }
    }
}

$fixture = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json -Depth 80
$results = [System.Collections.Generic.List[object]]::new()
foreach ($scenario in $fixture.cases) {
    $differences = [System.Collections.Generic.List[object]]::new()
    $gaps = [System.Collections.Generic.List[string]]::new()
    $before = $scenario.before; $after = $scenario.after
    if ($scenario.syntheticParameterBurst) {
        $burst = $scenario.syntheticParameterBurst
        $leftValue = [string]::Concat(@($burst.beforeValue) * [int]$burst.repetitions)
        $rightValue = [string]::Concat(@($burst.afterValue) * [int]$burst.repetitions)
        for ($i = 0; $i -lt [int]$burst.count; $i++) {
            $identity = ('@p{0:D2}' -f $i)
            $before.conditions.parameters += [pscustomobject]@{ identity=$identity; effectiveType='nvarchar(max)'; value=[pscustomobject]@{ state='retained'; value=$leftValue } }
            $after.conditions.parameters += [pscustomobject]@{ identity=$identity; effectiveType='nvarchar(max)'; value=[pscustomobject]@{ state='retained'; value=$rightValue } }
        }
    }
    if (-not $before.identityResolved -or -not $after.identityResolved) { Add-Gap $gaps 'identity-unresolved' }
    if (-not $before.available -or -not $after.available) { Add-Gap $gaps 'legacy-model-unsupported' }
    if (-not $before.fingerprintDomainVerified -or -not $after.fingerprintDomainVerified) { Add-Gap $gaps 'fingerprint-domain-unverified' }
    foreach ($side in @($before,$after)) { foreach ($omitted in @($side.omitted)) { if ($omitted) { Add-Gap $gaps 'field-omitted' } } }
    $leftConditionsPresent = Has-Property $before 'conditions' -and $null -ne $before.conditions
    $rightConditionsPresent = Has-Property $after 'conditions' -and $null -ne $after.conditions
    if (-not $leftConditionsPresent -or -not $rightConditionsPresent) { Add-Gap $gaps 'conditions-unavailable' }
    if ($before.available -and $after.available -and $before.identityResolved -and $after.identityResolved -and $leftConditionsPresent -and $rightConditionsPresent) {
        $fields = @(
            @{ path='context.database'; relation='measurement-condition'; parent='context'; name='database' },
            @{ path='context.engine'; relation='measurement-condition'; parent='context'; name='engine' },
            @{ path='context.server'; relation='measurement-condition'; parent='context'; name='server' },
            @{ path='execution.comparisonMode'; relation='informational'; parent='execution'; name='comparisonMode' },
            @{ path='execution.repeat'; relation='informational'; parent='execution'; name='repeat' },
            @{ path='execution.timeoutSeconds'; relation='informational'; parent='execution'; name='timeoutSeconds' },
            @{ path='execution.sessionPolicy'; relation='measurement-condition'; parent='execution'; name='sessionPolicy' },
            @{ path='execution.setupBinding'; relation='measurement-condition'; parent='execution'; name='setupBinding' },
            @{ path='execution.warmup'; relation='informational'; parent='execution'; name='warmup' },
            @{ path='execution.operation'; relation='informational'; parent='execution'; name='operation' },
            @{ path='execution.compareNullability'; relation='informational'; parent='execution'; name='compareNullability' },
            @{ path='setup.hash'; relation='measurement-condition'; parent='setup'; name='hash' }
        )
        foreach ($f in $fields) {
            $leftParent = $before.conditions.($f.parent); $rightParent = $after.conditions.($f.parent)
            $leftHas = Has-Property $leftParent $f.name; $rightHas = Has-Property $rightParent $f.name
            if ($leftHas -or $rightHas) { Compare-Scalar $differences $gaps $f.path $leftParent.($f.name) $rightParent.($f.name) $leftHas $rightHas $f.relation $null $null }
            else { Add-Gap $gaps 'field-omitted' }
        }
        $leftSetup = $before.conditions.setup; $rightSetup = $after.conditions.setup
        $leftSetupTextPresent = Has-Property $leftSetup 'text'; $rightSetupTextPresent = Has-Property $rightSetup 'text'
        if ($leftSetupTextPresent -or $rightSetupTextPresent) {
            $leftText = $leftSetup.text; $rightText = $rightSetup.text
            Compare-Scalar $differences $gaps 'setup.text.value' $leftText $rightText $leftSetupTextPresent $rightSetupTextPresent 'measurement-condition' $null $null
        } else { Add-Gap $gaps 'field-omitted' }
        $leftParametersPresent = Has-Property $before.conditions 'parameters'; $rightParametersPresent = Has-Property $after.conditions 'parameters'
        if (-not $leftParametersPresent -or -not $rightParametersPresent) { Add-Gap $gaps 'field-omitted' }
        else { Compare-Parameters $differences $gaps $before.conditions.parameters $after.conditions.parameters '' $null }
        $leftCasesPresent = Has-Property $before.conditions 'cases'; $rightCasesPresent = Has-Property $after.conditions 'cases'
        if (-not $leftCasesPresent -or -not $rightCasesPresent) { Add-Gap $gaps 'field-omitted' }
        elseif ($before.fingerprintDomainVerified -and $after.fingerprintDomainVerified) {
            $leftCases = @($before.conditions.cases | Where-Object { $null -ne $_ }); $rightCases = @($after.conditions.cases | Where-Object { $null -ne $_ })
            $leftKeys = @{}; $rightKeys = @{}
            foreach ($case in $leftCases) { if (-not $case.pairKey -or $leftKeys.ContainsKey([string]$case.pairKey)) { Add-Gap $gaps 'case-pairing-ambiguous' } else { $leftKeys[[string]$case.pairKey] = $case } }
            foreach ($case in $rightCases) { if (-not $case.pairKey -or $rightKeys.ContainsKey([string]$case.pairKey)) { Add-Gap $gaps 'case-pairing-ambiguous' } else { $rightKeys[[string]$case.pairKey] = $case } }
            foreach ($key in @(@($leftKeys.Keys) + @($rightKeys.Keys) | Sort-Object -Unique)) {
                $l = $leftKeys[$key]; $r = $rightKeys[$key]
                if ($null -eq $l) {
                    if ($r.identity) { Add-Difference $differences 'cases' 'added' 'measurement-condition' $r.identity $null $null $null }
                    else { Add-Gap $gaps 'identity-unresolved' }
                } elseif ($null -eq $r) {
                    if ($l.identity) { Add-Difference $differences 'cases' 'removed' 'measurement-condition' $l.identity $null $null $null }
                    else { Add-Gap $gaps 'identity-unresolved' }
                } elseif (-not $l.identity -or -not $r.identity) { Add-Gap $gaps 'identity-unresolved' }
                else { Compare-Parameters $differences $gaps $l.parameters $r.parameters 'cases' ([pscustomobject]@{ before=$l.identity; after=$r.identity }) }
            }
        } else { Add-Gap $gaps 'fingerprint-domain-unverified' }
        $leftMeasurementPresent = Has-Property $before 'measurementFingerprint'; $rightMeasurementPresent = Has-Property $after 'measurementFingerprint'
        if (-not $leftMeasurementPresent -or -not $rightMeasurementPresent) { Add-Gap $gaps 'legacy-model-unsupported' }
        elseif ($before.measurementFingerprint -cne $after.measurementFingerprint) {
            $explainedMeasurementDifference = @($differences | Where-Object relation -CEQ 'measurement-condition').Count -gt 0
            if (-not $explainedMeasurementDifference) { Add-Gap $gaps 'conditions-unavailable' }
        }
    }
    $sortedDiffs = @($differences | Sort-Object field,caseIdentity,parameterIdentity,kind)
    $knownOmitted = [Math]::Max(0, $sortedDiffs.Count - $MaxDifferences)
    $boundedDiffs = [System.Collections.Generic.List[object]]::new()
    foreach ($item in ($sortedDiffs | Select-Object -First $MaxDifferences)) { $boundedDiffs.Add($item) }
    $sortedGaps = @($gaps | Sort-Object -Unique)
    if ($knownOmitted -gt 0) { $sortedGaps = @($sortedGaps + 'budget-exceeded' | Sort-Object -Unique) }
    if ($sortedGaps.Count -gt $MaxGaps) { $sortedGaps = @($sortedGaps | Select-Object -First ($MaxGaps - 1)) + 'budget-exceeded' }
    $explanation = [ordered]@{ availability='complete'; differences=@($boundedDiffs); gaps=@($sortedGaps); omittedDifferences=$knownOmitted }
    if ($sortedGaps.Count -gt 0 -or $knownOmitted -gt 0) { $explanation.availability = if ($boundedDiffs.Count -gt 0) { 'partial' } else { 'unavailable' } }
    $json = Json $explanation
    if ((Utf8Size $json) -gt $MaxBytes -and $boundedDiffs.Count -gt 0) {
        foreach ($item in $boundedDiffs) { $item.PSObject.Properties.Remove('before'); $item.PSObject.Properties.Remove('after') }
        if ($explanation.gaps -notcontains 'budget-exceeded') { $explanation.gaps = @($explanation.gaps + 'budget-exceeded' | Sort-Object -Unique) }
        $explanation.availability = 'partial'; $json = Json $explanation
    }
    while ((Utf8Size $json) -gt $MaxBytes -and $boundedDiffs.Count -gt 0) {
        $boundedDiffs.RemoveAt($boundedDiffs.Count - 1); $knownOmitted++; $explanation.omittedDifferences = $knownOmitted
        if ($explanation.gaps -notcontains 'budget-exceeded') { $explanation.gaps = @($explanation.gaps + 'budget-exceeded' | Sort-Object -Unique) }
        $explanation.availability = 'partial'; $json = Json $explanation
    }
    if ((Utf8Size $json) -gt $MaxBytes) { throw "Cannot fit minimal explanation for '$($scenario.name)' under $MaxBytes UTF-8 bytes." }
    $results.Add([pscustomobject]@{ name=$scenario.name; basis=$scenario.basis; conditionExplanation=[pscustomobject]$explanation; explanationUtf8Bytes=(Utf8Size $json) })
}
$observedBytes = @($results | ForEach-Object explanationUtf8Bytes)
$result = [ordered]@{ schema='plan118-condition-explanation-results-v1'; revision=(git -C (Split-Path $PSScriptRoot -Parent | Split-Path -Parent) rev-parse HEAD); evaluationState='working tree evidence based on revision above'; serializer='PowerShell ConvertTo-Json -Depth 80 -Compress; UTF-8 byte count via System.Text.Encoding'; limits=[ordered]@{ differences=$MaxDifferences; gaps=$MaxGaps; explanationUtf8Bytes=$MaxBytes }; caseCount=$results.Count; totalExplanationUtf8Bytes=[int](($observedBytes | Measure-Object -Sum).Sum); maxObservedExplanationUtf8Bytes=[int](($observedBytes | Measure-Object -Maximum).Maximum); results=@($results); knownLimits=@('Synthetic records only; no production serializer or browser measurement.','Fingerprint keys are fixture labels and never explanation values.','The 8 KiB ceiling applies to conditionExplanation JSON only, not its enclosing comparison response.') }
$outputJson = ConvertTo-Json -InputObject $result -Depth 80
[System.IO.File]::WriteAllText($OutputPath, $outputJson + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
Write-Output "cases=$($results.Count) output=$OutputPath"
