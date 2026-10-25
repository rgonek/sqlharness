[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$casePath = Join-Path $PSScriptRoot 'cases.json'
$resultPath = Join-Path $PSScriptRoot 'results.json'
if (-not (Test-Path -LiteralPath $casePath -PathType Leaf)) { throw "Missing input: $casePath" }
$fixture = Get-Content -LiteralPath $casePath -Raw | ConvertFrom-Json -AsHashtable
$utf8 = [System.Text.UTF8Encoding]::new($false)
$requiredIds = @(
    'release-identity', 'development-same-version-different-mvid', 'optional-metadata-unavailable',
    'old-artifact-without-producer', 'reader-build-differs-from-producer',
    'path-build-differs-from-loaded-facts', 'multiple-capability-root-pages'
)
if ($fixture.studyRevision -ne '443f468e620e98c2e65fb109040d91ef06c286c5') { throw 'Unexpected fixture revision.' }
if (@($fixture.cases).Count -ne 7) { throw 'Fixture must contain exactly seven scenarios.' }
foreach ($id in $requiredIds) {
    if (@($fixture.cases | Where-Object id -eq $id).Count -ne 1) { throw "Fixture must contain exactly one scenario named '$id'." }
}

function Get-ProducerState($facts) {
    $requiredPresent = -not [string]::IsNullOrWhiteSpace([string]$facts.mcp.version) -and
        -not [string]::IsNullOrWhiteSpace([string]$facts.core.version)
    if (-not $requiredPresent) {
        return [ordered]@{ availability = 'unavailable'; reason = 'loaded_assembly_metadata_unavailable' }
    }
    $optionalPresent = -not [string]::IsNullOrWhiteSpace([string]$facts.mcp.informationalVersion) -and
        -not [string]::IsNullOrWhiteSpace([string]$facts.mcp.moduleVersionId) -and
        -not [string]::IsNullOrWhiteSpace([string]$facts.core.informationalVersion) -and
        -not [string]::IsNullOrWhiteSpace([string]$facts.core.moduleVersionId)
    if (-not $optionalPresent) {
        return [ordered]@{ availability = 'partial'; reason = 'optional_metadata_missing' }
    }
    return [ordered]@{ availability = 'available'; reason = $null }
}

function New-Producer($facts, $state) {
    $mcp = $facts.mcp
    $core = $facts.core
    $mcpInfo = if ([string]::IsNullOrWhiteSpace([string]$mcp.version)) { $null } else { $mcp.informationalVersion }
    $mcpMvid = if ([string]::IsNullOrWhiteSpace([string]$mcp.version)) { $null } else { $mcp.moduleVersionId }
    $coreInfo = if ([string]::IsNullOrWhiteSpace([string]$core.version)) { $null } else { $core.informationalVersion }
    $coreMvid = if ([string]::IsNullOrWhiteSpace([string]$core.version)) { $null } else { $core.moduleVersionId }
    return [ordered]@{
        identityVersion = 1
        availability = $state.availability
        reason = $state.reason
        assemblies = [ordered]@{
            mcp = [ordered]@{
                name = 'SqlHarness.Mcp'
                version = $mcp.version
                informationalVersion = $mcpInfo
                moduleVersionId = $mcpMvid
            }
            core = [ordered]@{
                name = 'SqlHarness.Core'
                version = $core.version
                informationalVersion = $coreInfo
                moduleVersionId = $coreMvid
            }
        }
    }
}

function Get-ByteCount($value) {
    $json = ConvertTo-Json -InputObject $value -Depth 100 -Compress
    return $utf8.GetByteCount($json)
}

$evaluated = [System.Collections.Generic.List[object]]::new()
foreach ($case in $fixture.cases) {
    $state = Get-ProducerState $case.loaded
    $availability = $state.availability
    $reason = $state.reason
    $producer = New-Producer $case.loaded $state
    $omissions = [System.Collections.Generic.List[string]]::new()
    if ($availability -eq 'partial') { $omissions.Add('one or more optional loaded assembly facts are null') }
    if ($case.id -eq 'old-artifact-without-producer') { $omissions.Add('historical artifact producer was not recorded; no reader-build substitution') }
    if ($case.id -eq 'path-build-differs-from-loaded-facts') { $omissions.Add('candidate PATH/build facts excluded; loaded assembly facts retained') }

    $pages = [System.Collections.Generic.List[object]]::new()
    if ($case.id -eq 'multiple-capability-root-pages') {
        $roots = @($case.roots)
        $cursor = 0
        while ($cursor -lt $roots.Count) {
            $take = [Math]::Min([int]$case.pageSize, $roots.Count - $cursor)
            $end = $cursor + $take
            $next = if ($end -lt $roots.Count) { $end } else { $null }
            $page = [ordered]@{
                configurationRef = 'synthetic-configuration-ref'
                scopeMode = 'fixed'
                engines = @('sqlserver')
                producer = $producer
                inputRoots = @($roots[$cursor..($end - 1)])
                inputRootCount = $roots.Count
                inputRootsComplete = ($cursor -eq 0 -and $null -eq $next)
                inputRootsNextCursor = $next
                inputRootsOmitted = 0
            }
            $pages.Add([ordered]@{ cursor = $cursor; nextCursor = $next; document = $page })
            $cursor = $end
        }
    } else {
        $document = [ordered]@{
            configurationRef = 'synthetic-configuration-ref'
            scopeMode = 'fixed'
            engines = @('sqlserver')
            producer = $producer
            inputRoots = @()
            inputRootCount = 0
            inputRootsComplete = $true
            inputRootsNextCursor = $null
            inputRootsOmitted = 0
        }
        $pages.Add([ordered]@{ cursor = 0; nextCursor = $null; document = $document })
    }

    $pageEstimates = [System.Collections.Generic.List[object]]::new()
    foreach ($page in $pages) {
        $bytes = Get-ByteCount $page.document
        $pageEstimates.Add([ordered]@{
            cursor = $page.cursor
            nextCursor = $page.nextCursor
            utf8Bytes = $bytes
            budgets = [ordered]@{
                '4096' = [ordered]@{ fits = ($bytes -le 4096); nonFit = ($bytes -gt 4096) }
                '65536' = [ordered]@{ fits = ($bytes -le 65536); nonFit = ($bytes -gt 65536) }
            }
        })
    }

    $hasArtifactProducer = $case.ContainsKey('artifactProducer')
    $persistedProducer = if ($hasArtifactProducer -and $null -ne $case.artifactProducer) { $case.artifactProducer } else { $null }
    $secondLoadedProducer = if ($case.alsoLoaded) {
        $secondState = Get-ProducerState $case.alsoLoaded
        New-Producer $case.alsoLoaded $secondState
    } else { $null }
    $subcaseResults = [System.Collections.Generic.List[object]]::new()
    if ($case.ContainsKey('subcases')) {
        foreach ($subcase in $case.subcases) {
            $subcaseState = Get-ProducerState $subcase.loaded
            $subcaseResults.Add([ordered]@{
                id = $subcase.id
                producer = New-Producer $subcase.loaded $subcaseState
                availability = $subcaseState.availability
                reason = $subcaseState.reason
            })
        }
    }
    $artifactState = if (-not $hasArtifactProducer) { 'not_applicable' } elseif ($null -eq $persistedProducer) { 'unavailable' } else { 'available' }
    $artifactReason = if ($hasArtifactProducer -and $null -eq $persistedProducer) { 'producer_not_recorded' } else { $null }
    $artifactProducerBytes = if ($null -eq $persistedProducer) { $null } else { Get-ByteCount $persistedProducer }
    $evaluated.Add([ordered]@{
        caseId = $case.id
        proposedJson = [ordered]@{
            capabilityPages = @($pages.ToArray())
            persistedArtifactProducer = $persistedProducer
            secondDevelopmentProducer = $secondLoadedProducer
            subcases = @($subcaseResults.ToArray())
        }
        estimates = [ordered]@{
            serializer = $fixture.serializer
            pages = @($pageEstimates.ToArray())
            candidateJournalProducerBytes = Get-ByteCount $producer
            candidateArtifactProducerBytes = $artifactProducerBytes
        }
        availability = [ordered]@{
            value = $availability
            reason = $reason
            artifactValue = $artifactState
            artifactReason = $artifactReason
        }
        omissions = @($omissions.ToArray())
        subcases = @($subcaseResults.ToArray())
    })
}

$result = [ordered]@{
    studyRevision = $fixture.studyRevision
    serializer = $fixture.serializer
    budgetsBytes = @($fixture.minimumBudgetBytes, $fixture.defaultBudgetBytes)
    scenariosAccounted = $evaluated.Count
    results = @($evaluated.ToArray())
}
$result | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $resultPath -Encoding utf8
Write-Output "Wrote $resultPath ($($evaluated.Count) scenarios; non-wire UTF-8 estimates)."
