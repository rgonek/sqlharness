param([string]$InputPath = (Join-Path $PSScriptRoot 'cases.json'), [string]$OutputPath = (Join-Path $PSScriptRoot 'results.json'))
$ErrorActionPreference = 'Stop'
$MaxDifferences = 32
$MaxGaps = 16
$MaxBytes = 8192

function Json($value) { ConvertTo-Json -InputObject $value -Depth 80 -Compress }
function Utf8Size([string]$value) { [System.Text.Encoding]::UTF8.GetByteCount($value) }
function Add-Gap($list, [string]$code) { if (-not $list.Contains($code)) { $list.Add($code) } }
function Add-Difference($list, [string]$field, [string]$kind, [string]$relation, $caseIdentity, $parameterIdentity, $before, $after) {
    $item = [ordered]@{ field = $field }
    if ($null -ne $caseIdentity) { $item.caseIdentity = $caseIdentity }
    if ($null -ne $parameterIdentity) { $item.parameterIdentity = $parameterIdentity }
    $item.kind = $kind; $item.relation = $relation
    if ($null -ne $before) { $item.before = $before }
    if ($null -ne $after) { $item.after = $after }
    $list.Add([pscustomobject]$item)
}
function Display-Value($wrapper) {
    if ($null -eq $wrapper) { return $null }
    if ($wrapper.state -eq 'retained' -or $wrapper.state -eq 'typed-null') { return [pscustomobject]@{ allowed = $true; value = $wrapper.value } }
    return $null
}
function Compare-Scalar($list, $gaps, [string]$field, $left, $right, [string]$relation, $caseIdentity, $parameterIdentity, [string]$rightPath) {
    if ($null -eq $left -or $null -eq $right) { return }
    if ((Json $left) -ceq (Json $right)) { return }
    if ($field -match '\.value$') {
        $leftShown = Display-Value $left; $rightShown = Display-Value $right
        if ($null -eq $leftShown -or $null -eq $rightShown) { Add-Gap $gaps 'field-omitted'; return }
        Add-Difference $list $field 'changed' $relation $caseIdentity $parameterIdentity $leftShown.value $rightShown.value
        return
    }
    if ($field -match 'setup\.hash$|contentHash|fingerprint') { Add-Gap $gaps 'conditions-unavailable'; return }
    Add-Difference $list $field 'changed' $relation $caseIdentity $parameterIdentity $left $right
}
function Compare-Parameters($list, $gaps, $leftItems, $rightItems, [string]$prefix, $caseIdentity) {
    $leftMap = @{}; $rightMap = @{}
    foreach ($p in @($leftItems | Where-Object { $null -ne $_ })) { if ($null -eq $p.identity -or $leftMap.ContainsKey([string]$p.identity)) { Add-Gap $gaps 'identity-unresolved'; continue }; $leftMap[[string]$p.identity] = $p }
    foreach ($p in @($rightItems | Where-Object { $null -ne $_ })) { if ($null -eq $p.identity -or $rightMap.ContainsKey([string]$p.identity)) { Add-Gap $gaps 'identity-unresolved'; continue }; $rightMap[[string]$p.identity] = $p }
    foreach ($id in @(@($leftMap.Keys) + @($rightMap.Keys) | Sort-Object -Unique)) {
        $l = $leftMap[$id]; $r = $rightMap[$id]
        $path = if ($prefix) { "$prefix.parameters" } else { 'parameters' }
        if ($null -eq $l) { Add-Difference $list "$path.$id" 'added' 'measurement-condition' $caseIdentity $id $null $null; continue }
        if ($null -eq $r) { Add-Difference $list "$path.$id" 'removed' 'measurement-condition' $caseIdentity $id $null $null; continue }
        if ($l.effectiveType -cne $r.effectiveType) { Add-Difference $list "$path.$id.effectiveType" 'changed' 'measurement-condition' $caseIdentity $id $l.effectiveType $r.effectiveType }
        foreach ($metadata in @('kind','count','columns','columnTypes')) {
            $leftHas = $l.PSObject.Properties.Name -contains $metadata; $rightHas = $r.PSObject.Properties.Name -contains $metadata
            if ($leftHas -and $rightHas -and (Json $l.$metadata) -cne (Json $r.$metadata)) { Add-Difference $list "$path.$id.$metadata" 'changed' 'measurement-condition' $caseIdentity $id $l.$metadata $r.$metadata }
            elseif ($leftHas -ne $rightHas) { Add-Gap $gaps 'field-omitted' }
        }
        Compare-Scalar $list $gaps "$path.$id.value" $l.value $r.value 'measurement-condition' $caseIdentity $id $null
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
    if ($before.available -and $after.available -and $before.identityResolved -and $after.identityResolved) {
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
            $l = $before.conditions.($f.parent).($f.name); $r = $after.conditions.($f.parent).($f.name)
            if ($null -ne $l -and $null -ne $r) { Compare-Scalar $differences $gaps $f.path $l $r $f.relation $null $null $null }
            elseif (($null -eq $l) -ne ($null -eq $r)) { Add-Gap $gaps 'field-omitted' }
        }
        $leftSetupText = $before.conditions.setup.text; $rightSetupText = $after.conditions.setup.text
        if ($null -ne $leftSetupText -and $null -ne $rightSetupText -and (Json $leftSetupText) -cne (Json $rightSetupText)) {
            if ($leftSetupText.state -in @('retained','typed-null') -and $rightSetupText.state -in @('retained','typed-null')) { Add-Difference $differences 'setup.text' 'changed' 'measurement-condition' $null $null $leftSetupText.value $rightSetupText.value }
            elseif ($leftSetupText.state -eq 'omitted' -or $rightSetupText.state -eq 'omitted') { Add-Gap $gaps 'field-omitted' }
            else { Add-Gap $gaps 'conditions-unavailable' }
        } elseif (($null -eq $leftSetupText) -ne ($null -eq $rightSetupText)) { Add-Gap $gaps 'field-omitted' }
        Compare-Parameters $differences $gaps $before.conditions.parameters $after.conditions.parameters '' $null
        if ($before.fingerprintDomainVerified -and $after.fingerprintDomainVerified) {
            $leftCases = @($before.conditions.cases | Where-Object { $null -ne $_ }); $rightCases = @($after.conditions.cases | Where-Object { $null -ne $_ })
            $leftKeys = @{}; $rightKeys = @{}
            foreach ($case in $leftCases) { if (-not $case.pairKey -or $leftKeys.ContainsKey([string]$case.pairKey)) { Add-Gap $gaps 'case-pairing-ambiguous' } else { $leftKeys[[string]$case.pairKey] = $case } }
            foreach ($case in $rightCases) { if (-not $case.pairKey -or $rightKeys.ContainsKey([string]$case.pairKey)) { Add-Gap $gaps 'case-pairing-ambiguous' } else { $rightKeys[[string]$case.pairKey] = $case } }
            foreach ($key in @(@($leftKeys.Keys) + @($rightKeys.Keys) | Sort-Object -Unique)) {
                $l = $leftKeys[$key]; $r = $rightKeys[$key]
                if ($null -eq $l) { Add-Difference $differences 'cases' 'added' 'measurement-condition' $r.identity $null $null $null }
                elseif ($null -eq $r) { Add-Difference $differences 'cases' 'removed' 'measurement-condition' $l.identity $null $null $null }
                else {
                    if (-not $l.identity -or -not $r.identity) { Add-Gap $gaps 'identity-unresolved' }
                    else { Compare-Parameters $differences $gaps $l.parameters $r.parameters "cases.$key" ([pscustomobject]@{ before=$l.identity; after=$r.identity }) }
                }
            }
        }
        if ($before.fingerprint -cne $after.fingerprint -and $differences.Count -eq 0 -and $gaps.Count -eq 0) { Add-Gap $gaps 'conditions-unavailable' }
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
