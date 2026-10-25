[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:EvidenceRoot = $PSScriptRoot
$script:FixtureRoot = Join-Path $PSScriptRoot 'fixtures'
$script:ScratchRoot = Join-Path $PSScriptRoot '.task2-scratch'
$script:MaxManifestBytes = 1MB
$script:MaxSqlBytes = 16MB
$script:MaxParameterFileBytes = 16MB
$script:MaxParameterIngressBytes = 32MB
$script:MaxComparisons = 16
$script:scriptDispatchCalls = [System.Collections.Generic.List[object]]::new()
$script:VirtualReparsePaths = @()

function Get-Bytes([string] $Text) {
    return [System.Text.UTF8Encoding]::new($false).GetBytes($Text)
}

function Get-Hash([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-JsonFile([string] $Path, [object] $Value) {
    $json = ConvertTo-Json -InputObject $Value -Depth 100
    [System.IO.File]::WriteAllText($Path, $json + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Copy-Tree([string] $Source, [string] $Destination) {
    [System.IO.Directory]::CreateDirectory($Destination) | Out-Null
    Get-ChildItem -LiteralPath $Source -Force -Recurse | ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($Source, $_.FullName)
        $target = Join-Path $Destination $relative
        if ($_.PSIsContainer) {
            [System.IO.Directory]::CreateDirectory($target) | Out-Null
        }
        else {
            [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $target
        }
    }
}

function Read-JsonFile([string] $Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
}

function Has-Keys([System.Collections.IDictionary] $Object, [string[]] $Required, [string[]] $Optional = @()) {
    foreach ($key in $Required) { if (-not $Object.Contains($key)) { return $false } }
    $allowed = @($Required) + @($Optional)
    foreach ($key in $Object.Keys) { if ($key -notin $allowed) { return $false } }
    return $true
}

function Get-ReferenceList([System.Collections.IDictionary] $Entry) {
    $refs = [System.Collections.Generic.List[object]]::new()
    $refs.Add([pscustomobject]@{ Reference = $Entry.baseline; Kind = 'sql' })
    $refs.Add([pscustomobject]@{ Reference = $Entry.candidate; Kind = 'sql' })
    if ($null -ne $Entry.setup) { $refs.Add([pscustomobject]@{ Reference = $Entry.setup; Kind = 'sql' }) }
    if ($Entry.input.kind -eq 'parameterSetFiles') {
        foreach ($file in $Entry.input.files) { $refs.Add([pscustomobject]@{ Reference = $file; Kind = 'parameter' }) }
    }
    return $refs.ToArray()
}

function Test-ScalarParameters([object] $Parameters) {
    if ($Parameters -isnot [array] -or $Parameters.Count -gt 2100) { throw 'invalid-fixed-parameters' }
    foreach ($parameter in $Parameters) {
        if ($parameter -isnot [System.Collections.IDictionary] -or -not (Has-Keys $parameter @('name','type','value'))) { throw 'invalid-fixed-parameter' }
        if ($parameter.name -isnot [string] -or [string]::IsNullOrWhiteSpace($parameter.name) -or $parameter.type -isnot [string] -or [string]::IsNullOrWhiteSpace($parameter.type)) { throw 'invalid-fixed-parameter' }
        $value = $parameter.value
        if ($value -is [System.Collections.IDictionary] -or $value -is [array]) { throw 'unsupported-nonscalar-parameter' }
        if ($null -ne $value -and $value -isnot [string] -and $value -isnot [bool] -and $value -isnot [int] -and $value -isnot [long] -and $value -isnot [double] -and $value -isnot [decimal]) { throw 'unsupported-parameter-value' }
    }
}

function Resolve-SafeReference([string] $InputRoot, [string] $RelativePath, [bool] $SimulatedReparse) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [System.IO.Path]::IsPathRooted($RelativePath) -or $RelativePath.Contains(':')) {
        throw 'path-not-relative'
    }
    $segments = $RelativePath.Replace('\', '/').Split('/')
    if ($segments -contains '..' -or $segments -contains '' -or $segments -contains '.') { throw 'path-traversal' }
    $rootFull = [System.IO.Path]::GetFullPath($InputRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $InputRoot ($RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
    if (-not $candidate.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'path-escape' }
    if ($SimulatedReparse -and $RelativePath -eq 'link-escape.sql') { throw 'symlink-reparse-escape-simulated' }
    $cursor = $InputRoot
    foreach ($segment in $segments) {
        $cursor = Join-Path $cursor $segment
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'symlink-reparse-escape' }
        }
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw 'missing-file' }
    $resolved = (Resolve-Path -LiteralPath $candidate).Path
    if (-not $resolved.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'path-escape' }
    return $resolved
}

function Test-Reference([string] $InputRoot, [object] $Reference, [string] $Kind, [bool] $SimulatedReparse) {
    if ($Reference -isnot [System.Collections.IDictionary] -or -not (Has-Keys $Reference @('path', 'sha256'))) { throw 'invalid-file-reference' }
    if ($Reference.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'invalid-sha256' }
    $resolved = Resolve-SafeReference $InputRoot ([string]$Reference.path) $SimulatedReparse
    $length = (Get-Item -LiteralPath $resolved).Length
    $limit = if ($Kind -eq 'parameter') { $script:MaxParameterFileBytes } else { $script:MaxSqlBytes }
    if ($length -gt $limit) { throw "file-size-limit-$Kind" }
    $actualHash = Get-Hash $resolved
    if ($actualHash -cne $Reference.sha256) { throw 'sha256-mismatch' }
    return [pscustomobject]@{ Path = $resolved; RelativePath = [string]$Reference.path; Length = [long]$length; Sha256 = $actualHash; Kind = $Kind }
}

function Test-EntryShape([object] $Entry) {
    if ($Entry -isnot [System.Collections.IDictionary] -or -not (Has-Keys $Entry @('id','surface','profile','vars','subject','scope','baseline','candidate','setup','repeat','timeoutSeconds','compareResults','compareNullability','input'))) { throw 'invalid-entry-shape' }
    if ($Entry.id -isnot [string] -or [string]::IsNullOrWhiteSpace($Entry.id)) { throw 'invalid-entry-id' }
    if ($Entry.surface -notin @('cli','mcp')) { throw 'invalid-surface' }
    if ($Entry.profile -isnot [string] -or [string]::IsNullOrWhiteSpace($Entry.profile)) { throw 'invalid-profile' }
    if ($Entry.vars -isnot [System.Collections.IDictionary]) { throw 'invalid-vars' }
    if ($Entry.subject -isnot [string] -or [string]::IsNullOrWhiteSpace($Entry.subject) -or $Entry.scope -isnot [string] -or [string]::IsNullOrWhiteSpace($Entry.scope)) { throw 'invalid-context' }
    if ($Entry.baseline -isnot [System.Collections.IDictionary] -or $Entry.candidate -isnot [System.Collections.IDictionary]) { throw 'invalid-sql-reference' }
    if ($null -ne $Entry.setup -and $Entry.setup -isnot [System.Collections.IDictionary]) { throw 'invalid-setup-reference' }
    if (($Entry.repeat -isnot [int] -and $Entry.repeat -isnot [long]) -or $Entry.repeat -lt 1 -or $Entry.repeat -gt 100) { throw 'invalid-repeat' }
    if (($Entry.timeoutSeconds -isnot [int] -and $Entry.timeoutSeconds -isnot [long]) -or $Entry.timeoutSeconds -lt 1 -or $Entry.timeoutSeconds -gt 300) { throw 'invalid-timeout' }
    if ($Entry.compareResults -notin @('ordered','multiset','set','off')) { throw 'invalid-comparison-policy' }
    if ($Entry.compareResults -eq 'off' -and $Entry.compareNullability) { throw 'invalid-nullability-policy' }
    if ($Entry.input -isnot [System.Collections.IDictionary]) { throw 'invalid-input' }
    switch ([string]$Entry.input.kind) {
        'fixed' {
            if (-not (Has-Keys $Entry.input @('kind','parameters'))) { throw 'mixed-inputs' }
            Test-ScalarParameters $Entry.input.parameters
        }
        'parameterSetFiles' {
            if ($Entry.surface -ne 'mcp') { throw 'unsupported-cli-named-sets' }
            if (-not (Has-Keys $Entry.input @('kind','files'))) { throw 'mixed-inputs' }
            if ($Entry.input.files -isnot [array] -or $Entry.input.files.Count -lt 1 -or $Entry.input.files.Count -gt 32) { throw 'invalid-set-file-count' }
        }
        default { throw 'unsupported-input-kind' }
    }
}

function Test-SuitePreflight([System.Collections.IDictionary] $Suite, [string] $InputRoot, [bool] $SimulatedReparse) {
    if (-not (Has-Keys $Suite @('version','comparisons'))) { throw 'invalid-manifest-shape' }
    if ($Suite.version -ne 1) { throw 'invalid-manifest-version' }
    if ($Suite.comparisons -isnot [array] -or $Suite.comparisons.Count -lt 1 -or $Suite.comparisons.Count -gt $script:MaxComparisons) { throw 'invalid-manifest-count' }
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($entry in $Suite.comparisons) {
        Test-EntryShape $entry
        if (-not $seen.Add([string]$entry.id)) { throw 'duplicate-entry-id' }
    }
    $validated = @{}
    foreach ($entry in $Suite.comparisons) {
        $entryRefs = [System.Collections.Generic.List[object]]::new()
        [long]$parameterBytes = 0
        foreach ($ref in (Get-ReferenceList $entry)) {
            $checked = Test-Reference $InputRoot $ref.Reference $ref.Kind $SimulatedReparse
            if ($checked.Kind -eq 'parameter') { $parameterBytes += $checked.Length }
            $entryRefs.Add($checked)
        }
        if ($parameterBytes -gt $script:MaxParameterIngressBytes) { throw 'parameter-ingress-budget-exceeded' }
        $caseInputs = @(Read-CaseInputs $entry $InputRoot)
        [long]$parameterValues = 0
        foreach ($caseInput in $caseInputs) {
            Test-ScalarParameters $caseInput.parameters
            $parameterValues += $caseInput.parameters.Count
        }
        if ($parameterValues -gt 100000) { throw 'parameter-value-budget-exceeded' }
        $materializedBytes = (Get-Bytes (ConvertTo-Json -InputObject $caseInputs -Depth 100 -Compress)).Length
        if ($materializedBytes -gt 32MB) { throw 'parameter-materialization-budget-exceeded' }
        $validated[[string]$entry.id] = $entryRefs.ToArray()
    }
    return $validated
}

function Read-CaseInputs([System.Collections.IDictionary] $Entry, [string] $InputRoot) {
    if ($Entry.input.kind -eq 'fixed') {
        return @([pscustomobject]@{ name = 'fixed'; parameters = @($Entry.input.parameters) })
    }
    $cases = [System.Collections.Generic.List[object]]::new()
    foreach ($reference in $Entry.input.files) {
        $path = Join-Path $InputRoot ($reference.path.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $parsed = Read-JsonFile $path
        if ($parsed -isnot [System.Collections.IDictionary] -or -not (Has-Keys $parsed @('name','parameters')) -or $parsed.parameters -isnot [array]) { throw 'invalid-parameter-set-file' }
        if ($parsed.name -isnot [string] -or [string]::IsNullOrWhiteSpace($parsed.name)) { throw 'invalid-parameter-set-file' }
        $cases.Add([pscustomobject]@{ name = [string]$parsed.name; parameters = @($parsed.parameters) })
    }
    return $cases.ToArray()
}

function Invoke-MockDispatcher([System.Collections.IDictionary] $Entry, [object] $Outcome, [string] $CaseId, [int] $Ordinal, [string] $InputRoot) {
    $record = [ordered]@{
        ordinal = $Ordinal
        entryId = [string]$Entry.id
        operationId = "mock-op-$CaseId-$($Entry.id)"
        surface = [string]$Entry.surface
        profile = [string]$Entry.profile
        vars = $Entry.vars
        subject = [string]$Entry.subject
        scope = [string]$Entry.scope
        repeat = [int]$Entry.repeat
        timeoutSeconds = [int]$Entry.timeoutSeconds
        compareResults = [string]$Entry.compareResults
        compareNullability = [bool]$Entry.compareNullability
        baselineSha256 = [string]$Entry.baseline.sha256
        candidateSha256 = [string]$Entry.candidate.sha256
        setupSha256 = if ($null -eq $Entry.setup) { $null } else { [string]$Entry.setup.sha256 }
        inputKind = [string]$Entry.input.kind
        caseInputs = @(Read-CaseInputs $Entry $InputRoot)
        dispatcher = 'in-memory-mock'
    }
    $script:scriptDispatchCalls.Add([pscustomobject]$record)
    return [pscustomobject]@{
        id = [string]$Entry.id
        operationId = [string]$record.operationId
        execution = [string]$Outcome.execution
        equivalence = [string]$Outcome.equivalence
        performance = [string]$Outcome.performance
        coverage = $Outcome.coverage
        mockEvidence = if ($Outcome.Contains('mockRows')) { $Outcome.mockRows } else { $null }
        stopReason = if ($Outcome.Contains('stopReason')) { [string]$Outcome.stopReason } else { $null }
    }
}

function Set-ReferencePath([System.Collections.IDictionary] $Reference, [string] $Path) {
    $Reference.path = $Path
}

function Set-CaseMutation([string] $Action, [System.Collections.IDictionary] $Suite, [System.Collections.IDictionary] $Outcomes, [string] $InputRoot, [string] $CaseRoot, [ref] $VirtualLink, [ref] $LinkProof) {
    switch ($Action) {
        'none' { }
        'stale-candidate-hash' { }
        'dispatch-duplicate-negative' {
            $Suite.comparisons = @($Suite.comparisons | Where-Object { $_.id -eq 'duplicate-counts' })
        }
        'outcome-regression' { $Outcomes.entries[0].equivalence = 'regression' }
        'outcome-inconclusive' { $Outcomes.entries[0].equivalence = 'inconclusive'; $Outcomes.entries[0].performance = 'inconclusive' }
        'stop-error-entry-2' { $Outcomes.entries[1].execution = 'error'; $Outcomes.entries[1].stopReason = 'injected-error' }
        'stop-timeout-entry-2' { $Outcomes.entries[1].execution = 'timeout'; $Outcomes.entries[1].stopReason = 'injected-timeout' }
        'stop-cancelled-entry-2' { $Outcomes.entries[1].execution = 'cancelled'; $Outcomes.entries[1].stopReason = 'injected-cancellation' }
        'stop-unknown-entry-2' { $Outcomes.entries[1].execution = 'unknown'; $Outcomes.entries[1].stopReason = 'injected-unknown-completion' }
        'corrupt-parameter-hash' { $Suite.comparisons[1].input.files[0].sha256 = ('0' * 64) }
        'corrupt-setup-hash' { $Suite.comparisons = @($Suite.comparisons[1]); $Suite.comparisons[0].setup.sha256 = ('0' * 64) }
        'missing-input' { Set-ReferencePath $Suite.comparisons[0].input.files[0] 'inputs/not-present.sqljson' }
        'duplicate-id' { $Suite.comparisons[1].id = $Suite.comparisons[0].id }
        'too-many-entries' {
            $entries = [System.Collections.Generic.List[object]]::new()
            foreach ($entry in $Suite.comparisons) { $entries.Add($entry) }
            while ($entries.Count -lt 17) { $copy = $Suite.comparisons[0] | ConvertTo-Json -Depth 50 | ConvertFrom-Json -AsHashtable; $copy.id = "extra-$($entries.Count)"; $entries.Add($copy) }
            $Suite.comparisons = $entries.ToArray()
        }
        'oversize-manifest' { $Suite.padding = 'x' * ($script:MaxManifestBytes + 1) }
        'parameter-budget-overflow' {
            $entry = $Suite.comparisons[0]
            $files = [System.Collections.Generic.List[object]]::new()
            for ($i = 0; $i -lt 3; $i++) {
                $relative = "inputs/budget-$i.bin"
                $path = Join-Path $InputRoot $relative
                $payload = [byte[]]::new(11MB)
                [System.IO.File]::WriteAllBytes($path, $payload)
                $files.Add(@{ path = $relative; sha256 = (Get-Hash $path) })
            }
            $entry.input = @{ kind = 'parameterSetFiles'; files = $files.ToArray() }
        }
        'path-escape' { Set-ReferencePath $Suite.comparisons[0].baseline '../outside.sql' }
        'symlink-escape' {
            $outsidePath = Join-Path $CaseRoot 'outside.sql'
            [System.IO.File]::WriteAllText($outsidePath, "select 'synthetic outside root';`n", [System.Text.UTF8Encoding]::new($false))
            $linkPath = Join-Path $InputRoot 'link-escape.sql'
            try {
                New-Item -ItemType SymbolicLink -Path $linkPath -Target $outsidePath -ErrorAction Stop | Out-Null
                $LinkProof.Value = 'executed-os-symlink-proof'
            }
            catch {
                $VirtualLink.Value = $true
                $LinkProof.Value = 'os-link-unavailable-simulated-rejection'
            }
            Set-ReferencePath $Suite.comparisons[0].baseline 'link-escape.sql'
            $Suite.comparisons[0].baseline.sha256 = Get-Hash $outsidePath
        }
        'mixed-inputs' { $Suite.comparisons[0].input.matrix = @{ parameter = 'x'; values = @('1','2') } }
        'unsupported-cli-sets' { $Suite.comparisons[0].surface = 'cli' }
        'prior-artifact-reuse' { $Suite.comparisons[0].priorArtifactId = 'mock-prior-operation' }
        'attempt-resume' { $Suite.resumeFrom = 'previous-results.json' }
        'mutation-entry' { $Suite.comparisons[0].operation = 'mutation' }
        'change-before-dispatch-entry-2' { }
        default { throw "unknown-case-action:$Action" }
    }
    if ($Action -like 'stop-*') {
        $tail = $Suite.comparisons[0] | ConvertTo-Json -Depth 50 | ConvertFrom-Json -AsHashtable
        $tail.id = 'unexecuted-tail'
        $Suite.comparisons = @($Suite.comparisons) + @($tail)
        $Outcomes.entries = @($Outcomes.entries) + @(@{ id = 'unexecuted-tail'; execution = 'completed'; equivalence = 'equivalent'; performance = 'improved'; coverage = @{ requestedCases = 1; completedCases = 1 } })
    }
}

function Invoke-Case([System.Collections.IDictionary] $Case) {
    $caseRoot = Join-Path $script:ScratchRoot ([string]$Case.id)
    $inputRoot = Join-Path $caseRoot 'input'
    Copy-Tree (Join-Path $script:FixtureRoot ([string]$Case.fixture)) $inputRoot
    $suitePath = Join-Path $inputRoot 'experiment-suite.json'
    $outcomePath = Join-Path $inputRoot 'mock-outcomes.json'
    $suite = Read-JsonFile $suitePath
    $outcomes = Read-JsonFile $outcomePath
    $virtualLink = $false
    $linkProof = 'not-applicable'
    Set-CaseMutation ([string]$Case.action) $suite $outcomes $inputRoot $caseRoot ([ref]$virtualLink) ([ref]$linkProof)
    $manifestJson = ConvertTo-Json -InputObject $suite -Depth 100
    $manifestBytes = Get-Bytes $manifestJson
    $calls = [System.Collections.Generic.List[object]]::new()
    $entryOutcomes = [System.Collections.Generic.List[object]]::new()
    $preflightError = $null
    $dispatchStopReason = $null
    if ($manifestBytes.Length -gt $script:MaxManifestBytes) {
        $preflightError = 'manifest-size-limit'
    }
    else {
        try { $validated = Test-SuitePreflight $suite $inputRoot $virtualLink }
        catch { $preflightError = $_.Exception.Message }
    }
    $preflightPassed = ($null -eq $preflightError)
    if ($null -eq $preflightError) {
        $outcomeById = @{}
        foreach ($outcome in $outcomes.entries) { $outcomeById[[string]$outcome.id] = $outcome }
        $ordinal = 0
        foreach ($entry in $suite.comparisons) {
            $ordinal++
            if ($Case.action -eq 'change-before-dispatch-entry-2' -and $entry.id -eq $suite.comparisons[1].id) {
                $candidatePath = Join-Path $inputRoot ($entry.candidate.path.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
                [System.IO.File]::AppendAllText($candidatePath, "-- synthetic post-preflight change`n", [System.Text.UTF8Encoding]::new($false))
            }
            try {
                foreach ($ref in (Get-ReferenceList $entry)) { [void](Test-Reference $inputRoot $ref.Reference $ref.Kind $false) }
            }
            catch {
                $dispatchStopReason = "pre-dispatch-$($_.Exception.Message)"
                break
            }
            if (-not $outcomeById.ContainsKey([string]$entry.id)) {
                $dispatchStopReason = 'missing-mock-outcome'
                break
            }
            $outcome = Invoke-MockDispatcher $entry $outcomeById[[string]$entry.id] ([string]$Case.id) $ordinal $inputRoot
            $calls.Add($script:scriptDispatchCalls[$script:scriptDispatchCalls.Count - 1])
            $entryOutcomes.Add($outcome)
            if ($outcome.execution -ne 'completed') { break }
        }
    }
    $stopReason = if ($null -ne $preflightError) { $preflightError } elseif ($null -ne $dispatchStopReason) { $dispatchStopReason } elseif ($entryOutcomes.Count -gt 0 -and $entryOutcomes[$entryOutcomes.Count - 1].execution -ne 'completed') { [string]$entryOutcomes[$entryOutcomes.Count - 1].execution } elseif ($calls.Count -lt $suite.comparisons.Count) { 'unexecuted-tail' } else { $null }
    return [pscustomobject]@{
        id = [string]$Case.id
        preflight = [pscustomobject]@{ passed = $preflightPassed; error = $preflightError }
        dispatchCount = $calls.Count
        dispatchOrder = @($calls | ForEach-Object { $_.entryId })
        invocations = $calls.ToArray()
        outcomes = $entryOutcomes.ToArray()
        stopReason = $stopReason
        symlinkProof = $linkProof
        realConnectionCapability = $false
    }
}

try {
    if (Test-Path -LiteralPath $script:ScratchRoot) { Remove-Item -LiteralPath $script:ScratchRoot -Recurse -Force }
    [System.IO.Directory]::CreateDirectory($script:ScratchRoot) | Out-Null
    $caseDocument = Read-JsonFile (Join-Path $script:EvidenceRoot 'cases.json')
    if ($caseDocument.version -ne 1 -or $caseDocument.cases -isnot [array] -or $caseDocument.cases.Count -lt 1) { throw 'invalid-cases-document' }
    $results = [System.Collections.Generic.List[object]]::new()
    $caseIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($case in $caseDocument.cases) {
        if (-not $caseIds.Add([string]$case.id)) { throw "duplicate-case-id:$($case.id)" }
        $script:scriptDispatchCalls.Clear()
        $results.Add((Invoke-Case $case))
    }
    $output = [ordered]@{
        schemaVersion = 1
        evaluator = 'synthetic-in-memory-dispatch-study'
        databaseCalls = 0
        externalProcessCalls = 0
        suiteEquivalence = $null
        cases = $results.ToArray()
    }
    Write-JsonFile (Join-Path $script:EvidenceRoot 'results.json') $output
    Write-Output "Evaluated $($results.Count) synthetic cases; dispatch records: $(($results | Measure-Object -Property dispatchCount -Sum).Sum); database/external-process calls: 0/0"
}
catch {
    [Console]::Error.WriteLine("Evaluation failed: $($_.Exception.Message)")
    exit 1
}
finally {
    if (Test-Path -LiteralPath $script:ScratchRoot) { Remove-Item -LiteralPath $script:ScratchRoot -Recurse -Force }
}
