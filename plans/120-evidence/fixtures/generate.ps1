param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Write-Utf8([string]$RelativePath, [string]$Text) {
    $path = Join-Path $OutputRoot $RelativePath
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($path)) | Out-Null
    [System.IO.File]::WriteAllText($path, $Text, $utf8)
}

function Get-Sha256([string]$RelativePath) {
    $path = Join-Path $OutputRoot $RelativePath
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function File-Reference([string]$RelativePath, [string]$Digest = (Get-Sha256 $RelativePath)) {
    $manifestPath = $RelativePath -replace '^fixtures/ticket-[ab]/', ''
    @{ path = $manifestPath.Replace('\', '/'); sha256 = $Digest }
}

function Write-Json([string]$RelativePath, [object]$Value) {
    function ConvertTo-CanonicalJsonValue([object]$Item) {
        if ($Item -is [System.Collections.IDictionary]) {
            $ordered = [ordered]@{}
            foreach ($key in @($Item.Keys | Sort-Object -CaseSensitive)) {
                $ordered[$key] = ConvertTo-CanonicalJsonValue $Item[$key]
            }
            return ,$ordered
        }
        if ($Item -is [System.Collections.IEnumerable] -and $Item -isnot [string]) {
            $items = [System.Collections.Generic.List[object]]::new()
            foreach ($nestedItem in $Item) {
                $items.Add((ConvertTo-CanonicalJsonValue $nestedItem))
            }
            return ,$items.ToArray()
        }
        return $Item
    }

    $canonical = ConvertTo-CanonicalJsonValue $Value
    Write-Utf8 $RelativePath ((ConvertTo-Json -InputObject $canonical -Depth 30) + "`n")
}

$a = 'fixtures/ticket-a'
$b = 'fixtures/ticket-b'

# Ticket A: two independent SQL subjects and three scalar populations. The
# MCP named-set file shape is deliberate: current CLI compare has no named-set
# file option, so this is not presented as CLI/MCP parity.
Write-Utf8 "$a/sql/orders-baseline.sql" "select order_id, total from orders where customer_key = @customerId order by order_id;`n"
Write-Utf8 "$a/sql/orders-candidate.sql" "select order_id, total from orders where customer_key = @customerId and is_archived = 0 order by order_id;`n"
Write-Utf8 "$a/sql/returns-baseline.sql" "select return_id, reason from returns where customer_key = @customerId order by return_id;`n"
Write-Utf8 "$a/sql/returns-candidate.sql" "select return_id, reason from returns where customer_key = @customerId and is_void = 0 order by return_id;`n"
Write-Json "$a/inputs/small.sqljson" @{ name = 'small'; parameters = @(@{ name = 'customerId'; type = 'nvarchar'; value = '7' }) }
Write-Json "$a/inputs/large.sqljson" @{ name = 'large'; parameters = @(@{ name = 'customerId'; type = 'nvarchar'; value = '700000000' }) }
Write-Json "$a/inputs/empty.sqljson" @{ name = 'empty'; parameters = @(@{ name = 'customerId'; type = 'nvarchar'; value = '' }) }
$aCaseFiles = @((File-Reference "$a/inputs/small.sqljson"), (File-Reference "$a/inputs/large.sqljson"), (File-Reference "$a/inputs/empty.sqljson"))

$aComparisons = @(
    @{ id = 'orders-by-customer'; profile = 'synthetic-ticket-a'; vars = @{ dataset = 'orders' }; subject = 'orders'; scope = 'customer lookup'; baseline = (File-Reference "$a/sql/orders-baseline.sql"); candidate = (File-Reference "$a/sql/orders-candidate.sql"); setup = $null; repeat = 5; timeoutSeconds = 30; compareResults = 'ordered'; compareNullability = $false; input = @{ kind = 'parameterSetFiles'; files = $aCaseFiles } },
    @{ id = 'returns-by-customer'; profile = 'synthetic-ticket-a'; vars = @{ dataset = 'returns' }; subject = 'returns'; scope = 'customer lookup'; baseline = (File-Reference "$a/sql/returns-baseline.sql"); candidate = (File-Reference "$a/sql/returns-candidate.sql"); setup = $null; repeat = 5; timeoutSeconds = 30; compareResults = 'ordered'; compareNullability = $false; input = @{ kind = 'parameterSetFiles'; files = $aCaseFiles } }
)
Write-Json "$a/experiment-suite.json" @{ version = 1; comparisons = $aComparisons }
Write-Json "$a/mock-outcomes.json" @{ entries = @(
    @{ id = 'orders-by-customer'; execution = 'completed'; equivalence = 'equivalent'; performance = 'no-regression'; coverage = @{ requestedCases = 3; completedCases = 3 } },
    @{ id = 'returns-by-customer'; execution = 'completed'; equivalence = 'regression'; performance = 'inconclusive'; coverage = @{ requestedCases = 3; completedCases = 3 } }
) }

# Ticket B: three revision pairs, shared setup semantics, a boundary date and
# duplicate-sensitive mock rows. Pair 03 records the pinned revision hash and
# then deliberately changes the referenced file so pre-dispatch validation
# must reject it.
Write-Utf8 "$b/setup/prepare.sql" "select 1 as fixture_setup;`n"
Write-Utf8 "$b/revisions/pair-01/baseline.sql" "select event_id, event_date from events where event_date >= @boundaryDate order by event_id;`n"
Write-Utf8 "$b/revisions/pair-01/candidate.sql" "select event_id, event_date from events where event_date >= @boundaryDate and is_visible = 1 order by event_id;`n"
Write-Utf8 "$b/revisions/pair-02/baseline.sql" "select account_id, event_count from events where is_visible = @includeHidden order by event_id;`n"
Write-Utf8 "$b/revisions/pair-02/candidate.sql" "select distinct account_id, event_count from events where is_visible = @includeHidden order by account_id, event_count;`n"
Write-Utf8 "$b/revisions/pair-03/baseline.sql" "select event_id, label from events where event_date = @boundaryDate order by event_id;`n"
$pair3Path = "$b/revisions/pair-03/candidate.sql"
$pair3PinnedSql = "select event_id, label from events where event_date = @boundaryDate and is_visible = 1 order by event_id;`n"
Write-Utf8 $pair3Path $pair3PinnedSql
$pair3PinnedHash = Get-Sha256 $pair3Path
Write-Json "$b/inputs/boundary-date.sqljson" @{ name = 'boundary-date'; parameters = @(@{ name = 'boundaryDate'; type = 'date'; value = '2024-02-29' }) }
$sharedSetup = File-Reference "$b/setup/prepare.sql"
$boundaryInputs = File-Reference "$b/inputs/boundary-date.sqljson"
$duplicateBaselineRows = [System.Collections.Generic.List[object]]::new()
$duplicateBaselineRows.Add([object[]]@(4, 3))
$duplicateBaselineRows.Add([object[]]@(4, 3))
$duplicateCandidateRows = [System.Collections.Generic.List[object]]::new()
$duplicateCandidateRows.Add([object[]]@(4, 3))
$bPairs = @(
    @{ id = 'date-boundary'; baseline = (File-Reference "$b/revisions/pair-01/baseline.sql"); candidate = (File-Reference "$b/revisions/pair-01/candidate.sql"); input = @{ kind = 'parameterSetFile'; file = $boundaryInputs } },
    @{ id = 'duplicate-counts'; baseline = (File-Reference "$b/revisions/pair-02/baseline.sql"); candidate = (File-Reference "$b/revisions/pair-02/candidate.sql"); input = @{ kind = 'fixed'; parameters = @(@{ name = 'includeHidden'; type = 'bit'; value = '1' }) } },
    @{ id = 'changed-revision'; baseline = (File-Reference "$b/revisions/pair-03/baseline.sql"); candidate = (File-Reference $pair3Path $pair3PinnedHash); input = @{ kind = 'parameterSetFile'; file = $boundaryInputs } }
)
$bComparisons = foreach ($pair in $bPairs) {
    @{ id = $pair.id; profile = 'synthetic-ticket-b'; vars = @{ dataset = 'revision-fixture' }; subject = $pair.id; scope = 'revision pair'; baseline = $pair.baseline; candidate = $pair.candidate; setup = $sharedSetup; repeat = 7; timeoutSeconds = 45; compareResults = 'multiset'; compareNullability = $true; input = $pair.input }
}
Write-Json "$b/experiment-suite.json" @{ version = 1; comparisons = @($bComparisons) }
Write-Json "$b/mock-outcomes.json" @{ entries = @(
    @{ id = 'date-boundary'; execution = 'completed'; equivalence = 'equivalent'; performance = 'improved'; coverage = @{ requestedCases = 1; completedCases = 1 }; mockRows = @{ baseline = @(@(11, '2024-02-29'), @(11, '2024-02-29')); candidate = @(@(11, '2024-02-29'), @(11, '2024-02-29')) } },
    @{ id = 'duplicate-counts'; execution = 'completed'; equivalence = 'different'; performance = 'no-regression'; coverage = @{ requestedCases = 1; completedCases = 1 }; mockRows = @{ baseline = $duplicateBaselineRows.ToArray(); candidate = $duplicateCandidateRows.ToArray() } },
    @{ id = 'changed-revision'; execution = 'not-dispatched'; equivalence = 'not-evaluated'; performance = 'not-measured'; coverage = @{ requestedCases = 1; completedCases = 0 }; stopReason = 'sha256-mismatch' }
) }
Write-Utf8 $pair3Path "select event_id, label from events where event_date = @boundaryDate and is_visible = 1 and is_public = 1 order by event_id;`n"
Write-Json "$b/pinned-revision.json" @{ path = ($pair3Path -replace '^fixtures/ticket-b/', '').Replace('\', '/'); pinnedSha256 = $pair3PinnedHash; currentSha256 = (Get-Sha256 $pair3Path); reason = 'deliberately changed after manifest pinning' }

$tracked = Get-ChildItem (Join-Path $OutputRoot 'fixtures') -File -Recurse | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object { [System.IO.Path]::GetRelativePath($OutputRoot, $_.FullName).Replace('\', '/') }
$sumLines = foreach ($file in $tracked) {
    $relative = [System.IO.Path]::GetRelativePath($OutputRoot, $file.FullName).Replace('\', '/')
    "$(Get-Sha256 $relative)  $relative"
}
Write-Utf8 'fixtures/SHA256SUMS.txt' (($sumLines -join "`n") + "`n")
Write-Output "Generated $($tracked.Count) fixture files under $OutputRoot"
Write-Output "Ticket B changed-file pin: $pair3PinnedHash; current: $(Get-Sha256 $pair3Path)"
