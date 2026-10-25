[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$casePath = Join-Path $PSScriptRoot 'cases.json'
$resultPath = Join-Path $PSScriptRoot 'results.json'
$designPath = Join-Path $PSScriptRoot 'design.md'
$decisionPath = Join-Path $PSScriptRoot 'decision.md'
foreach ($path in @($casePath, $resultPath, $designPath, $decisionPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing required input: $path" }
}
$fixture = Get-Content -LiteralPath $casePath -Raw | ConvertFrom-Json -AsHashtable
$results = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json -AsHashtable
$expectedIds = @(
    'release-identity',
    'development-same-version-different-mvid',
    'optional-metadata-unavailable',
    'old-artifact-without-producer',
    'reader-build-differs-from-producer',
    'path-build-differs-from-loaded-facts',
    'multiple-capability-root-pages'
)

function Assert($condition, [string]$message) {
    if (-not $condition) { throw "ASSERTION FAILED: $message" }
}

function Get-ExpectedProducerState($facts) {
    $assemblies = @('mcp', 'core')
    $missingRequired = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $assemblies) {
        $assemblyFacts = $facts[$name]
        if ([string]::IsNullOrWhiteSpace([string]$assemblyFacts.version)) { $missingRequired.Add($name) }
    }
    if ($missingRequired.Count -gt 0) {
        return [ordered]@{ availability = 'unavailable'; reason = 'loaded_assembly_metadata_unavailable' }
    }
    $missingOptional = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $assemblies) {
        $assemblyFacts = $facts[$name]
        if ([string]::IsNullOrWhiteSpace([string]$assemblyFacts.informationalVersion) -or
            [string]::IsNullOrWhiteSpace([string]$assemblyFacts.moduleVersionId)) { $missingOptional.Add($name) }
    }
    if ($missingOptional.Count -gt 0) {
        return [ordered]@{ availability = 'partial'; reason = 'optional_metadata_missing' }
    }
    return [ordered]@{ availability = 'available'; reason = $null }
}

Assert ($results.scenariosAccounted -eq 7) 'exactly seven scenarios must be evaluated'
Assert (@($results.results.caseId | Sort-Object -Unique).Count -eq 7) 'case ids must be unique'
foreach ($id in $expectedIds) {
    Assert (@($results.results | Where-Object caseId -eq $id).Count -eq 1) "scenario missing or duplicated: $id"
}
$design = Get-Content -LiteralPath $designPath -Raw
foreach ($token in @('A. Capabilities only', 'B. A + producer on new journal sessions', 'C. B + producer on new artifact manifests', 'defer B and C')) {
    Assert ($design.Contains($token)) "design must include disposition/matrix text: $token"
}
$decision = Get-Content -LiteralPath $decisionPath -Raw
foreach ($token in @('Accept candidate A', 'A: ACCEPT', 'B: DEFER', 'C: DEFER', 'RESEARCH COMPLETE', '443f468e620e98c2e65fb109040d91ef06c286c5', 'non-wire estimates', 'budget-driven paging', 'historical loaded identity remains unresolved')) {
    Assert ($decision.Contains($token)) "decision must include disposition/evidence limit: $token"
}
foreach ($token in @('4,096', '65,536', 'System.Text.Json', 'MCP SDK', 'client acceptance', 'Plan 107', 'Plan 113')) {
    Assert ($decision.Contains($token)) "decision must preserve future acceptance/incident limit: $token"
}
Assert ($results.studyRevision -eq '443f468e620e98c2e65fb109040d91ef06c286c5') 'results must identify exact study revision'
Assert ($results.serializer -match 'non-wire estimates') 'results must identify serializer estimates as non-wire'

foreach ($case in $fixture.cases) {
    $actual = @($results.results | Where-Object caseId -eq $case.id)[0]
    $expectedState = Get-ExpectedProducerState $case.loaded
    Assert ($case.expect.availability -eq $expectedState.availability) "$($case.id): golden availability disagrees with loaded facts"
    Assert ($case.expect.reason -eq $expectedState.reason) "$($case.id): golden reason disagrees with loaded facts"
    Assert ($actual.availability.value -eq $expectedState.availability) "$($case.id): capability availability differs from loaded facts"
    Assert ($actual.availability.reason -eq $expectedState.reason) "$($case.id): capability reason differs from loaded facts"
    $pages = @($actual.proposedJson.capabilityPages)
    Assert ($pages.Count -ge 1) "$($case.id): no capabilities page emitted"
    foreach ($pageRecord in $pages) {
        $producer = $pageRecord.document.producer
        Assert ($producer.identityVersion -eq 1) "$($case.id): wrong identity version"
        Assert ($producer.availability -eq $expectedState.availability) "$($case.id): page availability mismatch"
        Assert ($producer.reason -eq $expectedState.reason) "$($case.id): page reason mismatch"
        Assert ($producer.assemblies.mcp.name -eq 'SqlHarness.Mcp') "$($case.id): MCP assembly name missing"
        Assert ($producer.assemblies.core.name -eq 'SqlHarness.Core') "$($case.id): Core assembly name missing"

        # Producer facts must exactly match fixture facts; no reader/path substitution is allowed.
        foreach ($assemblyName in @('mcp', 'core')) {
            $expected = $case.loaded[$assemblyName]
            $observed = $producer.assemblies[$assemblyName]
            Assert ($observed.version -ceq $expected.version) "$($case.id): $assemblyName.version was not preserved"
            $expectedInformationalVersion = if ([string]::IsNullOrWhiteSpace([string]$expected.version)) { $null } else { $expected.informationalVersion }
            $expectedModuleVersionId = if ([string]::IsNullOrWhiteSpace([string]$expected.version)) { $null } else { $expected.moduleVersionId }
            Assert ($observed.informationalVersion -ceq $expectedInformationalVersion) "$($case.id): $assemblyName.informationalVersion does not follow required-version availability"
            Assert ($observed.moduleVersionId -ceq $expectedModuleVersionId) "$($case.id): $assemblyName.moduleVersionId does not follow required-version availability"
        }

        Assert ((@($producer.Keys | Sort-Object) -join ',') -eq 'assemblies,availability,identityVersion,reason') "$($case.id): producer contains an unexpected field"
        foreach ($assemblyName in @('mcp', 'core')) {
            Assert ((@($producer.assemblies[$assemblyName].Keys | Sort-Object) -join ',') -eq 'informationalVersion,moduleVersionId,name,version') "$($case.id): assembly entry contains an unexpected field"
        }
        $producerJson = ConvertTo-Json -InputObject $producer -Depth 30 -Compress
        Assert (-not [regex]::IsMatch($producerJson, '(?i)([A-Z]:\\|password|secret|environment|connectionString)')) "$($case.id): producer includes a path or secret marker"
    }

    # Independently remeasure the generated document and require fit or explicit non-fit at both budgets.
    foreach ($estimate in $actual.estimates.pages) {
        $pageRecord = @($pages | Where-Object cursor -eq $estimate.cursor)[0]
        $json = ConvertTo-Json -InputObject $pageRecord.document -Depth 100 -Compress
        $bytes = [System.Text.UTF8Encoding]::new($false).GetByteCount($json)
        Assert ($bytes -eq $estimate.utf8Bytes) "$($case.id): reported page byte estimate is incorrect"
        foreach ($budget in @('4096', '65536')) {
            $fits = $bytes -le [int]$budget
            $budgetResult = $estimate.budgets[$budget]
            Assert ($budgetResult.fits -eq $fits) "$($case.id): $budget fit flag is incorrect"
            Assert ($budgetResult.nonFit -eq (-not $fits)) "$($case.id): $budget non-fit must be explicit"
        }
    }

    $expectedArtifactBytes = if ($null -eq $case.artifactProducer) { $null } else {
        $artifactJson = ConvertTo-Json -InputObject $case.artifactProducer -Depth 100 -Compress
        [System.Text.UTF8Encoding]::new($false).GetByteCount($artifactJson)
    }
    Assert ($actual.estimates.candidateArtifactProducerBytes -eq $expectedArtifactBytes) "$($case.id): artifact payload size must measure persisted producer data only"
    $expectedArtifactAvailability = if (-not $case.ContainsKey('artifactProducer')) { 'not_applicable' } elseif ($null -eq $case.artifactProducer) { 'unavailable' } else { 'available' }
    $expectedArtifactReason = if ($case.ContainsKey('artifactProducer') -and $null -eq $case.artifactProducer) { 'producer_not_recorded' } else { $null }
    Assert ($actual.availability.artifactValue -eq $expectedArtifactAvailability) "$($case.id): artifact applicability/availability mismatch"
    Assert ($actual.availability.artifactReason -eq $expectedArtifactReason) "$($case.id): artifact availability reason mismatch"

    if ($case.ContainsKey('subcases')) {
        foreach ($subcase in $case.subcases) {
            $subcaseExpected = Get-ExpectedProducerState $subcase.loaded
            Assert ($subcase.expect.availability -eq $subcaseExpected.availability) "$($case.id)/$($subcase.id): golden unavailable state disagrees with facts"
            Assert ($subcase.expect.reason -eq $subcaseExpected.reason) "$($case.id)/$($subcase.id): golden reason disagrees with facts"
            $subcaseActual = @($actual.subcases | Where-Object id -eq $subcase.id)[0]
            Assert ($null -ne $subcaseActual) "$($case.id)/$($subcase.id): projected subcase missing"
            Assert ($subcaseActual.availability -eq $subcaseExpected.availability) "$($case.id)/$($subcase.id): availability was not derived from required metadata"
            Assert ($subcaseActual.reason -eq $subcaseExpected.reason) "$($case.id)/$($subcase.id): reason was not derived from required metadata"
            Assert ($null -eq $subcaseActual.producer.assemblies.core.version) "$($case.id)/$($subcase.id): missing required version must remain null"
            Assert ($null -eq $subcaseActual.producer.assemblies.core.informationalVersion) "$($case.id)/$($subcase.id): unavailable Core informational version must be null"
            Assert ($null -eq $subcaseActual.producer.assemblies.core.moduleVersionId) "$($case.id)/$($subcase.id): unavailable Core MVID must be null"
        }
    }
}

$dev = @($results.results | Where-Object caseId -eq 'development-same-version-different-mvid')[0]
$devA = @($dev.proposedJson.capabilityPages)[0].document.producer.assemblies
$devB = $dev.proposedJson.secondDevelopmentProducer.assemblies
Assert ($devA.mcp.version -eq '0.0.0' -and $devA.core.version -eq '0.0.0') 'development fixture must keep equal numeric versions'
Assert ($devB.mcp.version -eq '0.0.0' -and $devB.core.version -eq '0.0.0') 'second development fixture must keep equal numeric versions'
Assert ($devA.mcp.moduleVersionId -ne $devB.mcp.moduleVersionId) 'distinct development MCP modules must remain distinct'
Assert ($devA.core.moduleVersionId -ne $devB.core.moduleVersionId) 'distinct development Core modules must remain distinct'

$old = @($results.results | Where-Object caseId -eq 'old-artifact-without-producer')[0]
Assert ($null -eq $old.proposedJson.persistedArtifactProducer) 'old artifact must remain without producer data'
Assert ($old.availability.artifactValue -eq 'unavailable') 'old artifact identity must be explicitly unavailable'
Assert ($old.availability.artifactReason -eq 'producer_not_recorded') 'old artifact reason must be explicit'
Assert ($null -eq $old.estimates.candidateArtifactProducerBytes) 'old artifact must not receive an invented producer payload size'
$release = @($results.results | Where-Object caseId -eq 'release-identity')[0]
Assert ($release.availability.artifactValue -eq 'not_applicable') 'scenarios without artifact provenance must not be treated as missing-producer artifacts'

$reader = @($results.results | Where-Object caseId -eq 'reader-build-differs-from-producer')[0]
$producerId = $reader.proposedJson.persistedArtifactProducer.assemblies.mcp.moduleVersionId
$readerId = $reader.proposedJson.capabilityPages[0].document.producer.assemblies.mcp.moduleVersionId
Assert ($producerId -ne $readerId) 'artifact producer identity must not be replaced with the reader build'

$pathCase = @($results.results | Where-Object caseId -eq 'path-build-differs-from-loaded-facts')[0]
$pathJson = ConvertTo-Json -InputObject $pathCase.proposedJson -Depth 100 -Compress
Assert (-not $pathJson.Contains('candidatePathBuild')) 'PATH/build candidate must not be included in output'
Assert (-not $pathJson.Contains('C:\\candidate')) 'absolute executable path must not be included in output'
Assert ($pathCase.proposedJson.capabilityPages[0].document.producer.assemblies.mcp.version -eq '1.2.3') 'loaded facts must win over candidate PATH/build'

$paging = @($results.results | Where-Object caseId -eq 'multiple-capability-root-pages')[0]
$pagingPages = @($paging.proposedJson.capabilityPages)
Assert ($pagingPages.Count -eq 2) 'root fixture should span two pages'
Assert ($pagingPages[0].document.producer.assemblies.mcp.moduleVersionId -ceq $pagingPages[1].document.producer.assemblies.mcp.moduleVersionId) 'loaded identity must remain stable across pages'
Assert ($pagingPages[0].document.inputRootsNextCursor -eq 2) 'first page must publish a continuation cursor'
Assert (-not $pagingPages[0].document.inputRootsComplete) 'first page must not claim completeness'
Assert ($pagingPages[1].cursor -eq 2 -and $pagingPages[1].document.inputRoots.Count -eq 1) 'second page must advance and carry remaining root'
# Root metadata remains complete for the inventory, and the second page terminates even though only cursor zero can claim single-response completeness.
Assert ($pagingPages[1].document.inputRootsNextCursor -eq $null) 'last page must terminate paging'
Assert ($pagingPages[1].document.producer.identityVersion -eq 1) 'every page must retain producer reservation'
foreach ($estimate in $paging.estimates.pages) {
    Assert ($estimate.budgets['4096'].fits) 'core capability root page must remain progress-capable within the minimum 4096-byte budget'
}

Write-Output "PASS: seven independent synthetic scenarios; availability, identity stability, provenance, path exclusion, byte-budget declarations, and progress-capable root paging."
