[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$resultsPath = Join-Path $root 'results.json'
$failures = [System.Collections.Generic.List[string]]::new()

function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}

function Get-Case([string] $Id) {
    $matches = @($script:results.cases | Where-Object { $_.id -ceq $Id })
    Assert ($matches.Count -eq 1) "case '$Id' must appear exactly once"
    if ($matches.Count -eq 1) { return $matches[0] }
    return $null
}

function Assert-Order([object] $Case, [string[]] $Expected, [string] $Label) {
    Assert ($null -ne $Case) "$Label result exists"
    if ($null -eq $Case) { return }
    Assert ($Case.dispatchCount -eq $Expected.Count) "$Label exact dispatch count"
    $actual = @($Case.dispatchOrder)
    Assert (($actual -join '|') -ceq ($Expected -join '|')) "$Label exact dispatch order"
    Assert (@($Case.invocations).Count -eq $Expected.Count) "$Label invocation inventory count"
    Assert (@($Case.outcomes).Count -eq $Expected.Count) "$Label outcome identity count"
    for ($i = 0; $i -lt [Math]::Min($Expected.Count, @($Case.invocations).Count); $i++) {
        Assert ($Case.invocations[$i].entryId -ceq $Expected[$i]) "$Label invocation[$i] entry identity"
        Assert ($Case.invocations[$i].ordinal -eq ($i + 1)) "$Label invocation[$i] ordinal"
        Assert ($Case.invocations[$i].dispatcher -ceq 'in-memory-mock') "$Label invocation[$i] uses only injected mock"
        Assert ($Case.invocations[$i].operationId -ceq "mock-op-$($Case.id)-$($Expected[$i])") "$Label invocation[$i] synthetic operation identity"
        Assert ($Case.outcomes[$i].id -ceq $Expected[$i]) "$Label outcome[$i] preserves requested ID"
        Assert ($Case.outcomes[$i].operationId -ceq $Case.invocations[$i].operationId) "$Label outcome[$i] preserves operation identity"
    }
    Assert (-not $Case.realConnectionCapability) "$Label declares no real connection capability"
}

try {
    if (-not (Test-Path -LiteralPath $resultsPath -PathType Leaf)) { throw 'results.json is missing; run evaluate.ps1 first' }
    $script:results = Get-Content -LiteralPath $resultsPath -Raw | ConvertFrom-Json
    $caseDoc = Get-Content -LiteralPath (Join-Path $root 'cases.json') -Raw | ConvertFrom-Json
    $expectedIds = @(
        'all-success','valid-non-equivalence','ticket-b-boundary-date','regression','inconclusive','error-entry-2','timeout-entry-2',
        'cancellation-entry-2','unknown-completion-entry-2','changed-sql-hash','changed-parameter-hash',
        'changed-setup-hash','missing-file','duplicate-entry-id','invalid-entry-count','invalid-manifest-size',
        'malformed-nullability-type','parameter-budget-overflow','path-escape','symlink-reparse-escape','mixed-inputs',
        'unsupported-cli-named-sets','reused-prior-artifact','attempted-resume','mutation-entry','changed-after-preflight'
    )
    $actualIds = @($script:results.cases | ForEach-Object { [string]$_.id })
    Assert ($caseDoc.cases.Count -eq $expectedIds.Count) 'case definition count matches independent verifier inventory'
    Assert (($actualIds -join '|') -ceq ($expectedIds -join '|')) 'every named case appears once in deterministic order'
    Assert ($script:results.databaseCalls -eq 0) 'evaluator records zero database calls'
    Assert ($script:results.externalProcessCalls -eq 0) 'evaluator records zero external process calls'
    Assert ($null -eq $script:results.suiteEquivalence) 'no whole-suite equivalence verdict is synthesized'
    Assert ($script:results.evaluator -ceq 'synthetic-in-memory-dispatch-study') 'result identifies the mock-only evaluator'

    Assert-Order (Get-Case 'all-success') @('orders-by-customer','returns-by-customer') 'all-success'
    $all = Get-Case 'all-success'
    if ($null -ne $all -and $all.invocations.Count -eq 2) {
        Assert ($all.preflight.passed) 'all-success passes full-suite preflight'
        Assert ($all.outcomes[0].execution -ceq 'completed' -and $all.outcomes[0].equivalence -ceq 'equivalent') 'all-success first outcome'
        Assert ($all.outcomes[1].execution -ceq 'completed' -and $all.outcomes[1].equivalence -ceq 'regression') 'completed negative remains explicit in second outcome'
        $firstCases = @($all.invocations[0].caseInputs | ForEach-Object { $_.name })
        Assert (($firstCases -join '|') -ceq 'small|large|empty') 'named case order remains unchanged'
        Assert ($all.invocations[0].caseInputs[0].parameters[0].name -ceq 'customerId' -and $all.invocations[0].caseInputs[0].parameters[0].value -ceq '7') 'small scalar parameter is unchanged'
        Assert ($all.invocations[0].caseInputs[1].parameters[0].value -ceq '700000000') 'large scalar parameter is unchanged'
        Assert ($all.invocations[0].caseInputs[2].parameters[0].value -ceq '') 'empty string remains distinct from NULL'
        Assert ($all.invocations[0].profile -ceq 'synthetic-ticket-a' -and $all.invocations[0].vars.dataset -ceq 'orders') 'explicit synthetic target request is preserved'
    }

    Assert-Order (Get-Case 'valid-non-equivalence') @('duplicate-counts') 'valid-non-equivalence'
    $negative = Get-Case 'valid-non-equivalence'
    if ($null -ne $negative -and $negative.outcomes.Count -eq 1) {
        Assert ($negative.preflight.passed -and $negative.outcomes[0].execution -ceq 'completed' -and $negative.outcomes[0].equivalence -ceq 'different') 'valid negative comparison is completed and retained'
        Assert ($negative.invocations[0].compareResults -ceq 'multiset') 'negative comparison keeps multiplicity-aware policy'
        Assert ($negative.outcomes[0].mockEvidence.baseline.Count -eq 2 -and $negative.outcomes[0].mockEvidence.candidate.Count -eq 1) 'mock negative differs only in duplicate multiplicity'
        Assert ($negative.outcomes[0].coverage.completedCases -eq 1 -and $negative.outcomes[0].coverage.requestedCases -eq 1) 'negative result retains complete coverage identity'
    }

    Assert-Order (Get-Case 'ticket-b-boundary-date') @('date-boundary') 'ticket-b-boundary-date'
    $boundaryCase = Get-Case 'ticket-b-boundary-date'
    if ($null -ne $boundaryCase -and $boundaryCase.invocations.Count -eq 1) {
        $boundaryInvocation = $boundaryCase.invocations[0]
        Assert ($boundaryInvocation.surface -ceq 'cli' -and $boundaryInvocation.inputKind -ceq 'fixed') 'Ticket B boundary date is dispatched through its declared CLI fixed-scalar shape'
        Assert ($boundaryInvocation.caseInputs[0].parameters[0].name -ceq 'boundaryDate') 'Ticket B parameter name is preserved'
        Assert ($boundaryInvocation.caseInputs[0].parameters[0].type -ceq 'date') 'Ticket B parameter type is preserved'
        Assert ($boundaryInvocation.caseInputs[0].parameters[0].value -ceq '2024-02-29') 'Ticket B boundary date value is preserved'
    }
    $ticketB = Get-Content -LiteralPath (Join-Path $root 'fixtures/ticket-b/experiment-suite.json') -Raw | ConvertFrom-Json
    foreach ($ticketEntryId in @('date-boundary','changed-revision')) {
        $ticketEntries = @($ticketB.comparisons | Where-Object { $_.id -ceq $ticketEntryId })
        Assert ($ticketEntries.Count -eq 1) "Ticket B '$ticketEntryId' fixture entry exists once"
        if ($ticketEntries.Count -eq 1) {
            $ticketParameters = @($ticketEntries[0].input.parameters)
            Assert ($ticketEntries[0].input.kind -ceq 'fixed' -and $ticketParameters.Count -eq 1) "Ticket B '$ticketEntryId' uses exactly one fixed parameter"
            if ($ticketParameters.Count -eq 1) {
                Assert ($ticketParameters[0].name -ceq 'boundaryDate' -and $ticketParameters[0].type -ceq 'date' -and $ticketParameters[0].value -ceq '2024-02-29') "Ticket B '$ticketEntryId' pins boundaryDate/date/2024-02-29"
            }
        }
    }

    Assert-Order (Get-Case 'regression') @('orders-by-customer','returns-by-customer') 'regression'
    $regression = Get-Case 'regression'
    if ($null -ne $regression -and $regression.outcomes.Count -gt 0) { Assert ($regression.outcomes[0].execution -ceq 'completed' -and $regression.outcomes[0].equivalence -ceq 'regression') 'regression is preserved as a completed outcome' }

    Assert-Order (Get-Case 'inconclusive') @('orders-by-customer','returns-by-customer') 'inconclusive'
    $inconclusive = Get-Case 'inconclusive'
    if ($null -ne $inconclusive -and $inconclusive.outcomes.Count -gt 0) { Assert ($inconclusive.outcomes[0].execution -ceq 'completed' -and $inconclusive.outcomes[0].equivalence -ceq 'inconclusive' -and $inconclusive.outcomes[0].performance -ceq 'inconclusive') 'inconclusive evidence is not collapsed into failure or equivalence' }

    $stopExpectations = @(
        @{ id = 'error-entry-2'; status = 'error' },
        @{ id = 'timeout-entry-2'; status = 'timeout' },
        @{ id = 'cancellation-entry-2'; status = 'cancelled' },
        @{ id = 'unknown-completion-entry-2'; status = 'unknown' }
    )
    foreach ($expectation in $stopExpectations) {
        Assert-Order (Get-Case $expectation.id) @('orders-by-customer','returns-by-customer') $expectation.id
        $stopped = Get-Case $expectation.id
        if ($null -ne $stopped -and $stopped.outcomes.Count -eq 2) {
            Assert ($stopped.preflight.passed) "$($expectation.id) passes suite preflight before injected outcome"
            Assert ($stopped.outcomes[0].execution -ceq 'completed') "$($expectation.id) retains completed entry 1"
            Assert ($stopped.outcomes[0].comparisonStatus -ceq 'completed' -and $stopped.outcomes[0].equivalence -ceq 'equivalent') "$($expectation.id) retains entry 1 completed comparison semantics"
            Assert ($stopped.outcomes[0].coverage.completedCases -eq 3 -and $stopped.outcomes[0].coverage.requestedCases -eq 3) "$($expectation.id) retains entry 1 complete coverage"
            Assert ($stopped.outcomes[1].execution -ceq $expectation.status) "$($expectation.id) preserves entry 2 status"
            Assert ($stopped.outcomes[1].comparisonStatus -ceq 'not-run') "$($expectation.id) marks failed entry comparison not run"
            Assert ($stopped.outcomes[1].equivalence -ceq 'not-evaluated') "$($expectation.id) does not invent entry 2 equivalence"
            Assert ($stopped.outcomes[1].performance -ceq 'not-measured') "$($expectation.id) does not invent entry 2 performance"
            Assert ($stopped.outcomes[1].coverage.completedCases -eq 0 -and $stopped.outcomes[1].coverage.requestedCases -eq 3) "$($expectation.id) records zero of three entry 2 cases completed"
            Assert ($stopped.stopReason -ceq $expectation.status) "$($expectation.id) stops at entry 2"
            Assert (-not ($stopped.dispatchOrder -contains 'unexecuted-tail')) "$($expectation.id) never dispatches the explicit tail entry"
            Assert (-not ($stopped.outcomes.id -contains 'unexecuted-tail')) "$($expectation.id) does not create an outcome for the tail"
        }
    }

    $preflightCases = @(
        @{ id = 'changed-sql-hash'; error = 'sha256-mismatch' },
        @{ id = 'changed-parameter-hash'; error = 'sha256-mismatch' },
        @{ id = 'changed-setup-hash'; error = 'sha256-mismatch' },
        @{ id = 'missing-file'; error = 'missing-file' },
        @{ id = 'duplicate-entry-id'; error = 'duplicate-entry-id' },
        @{ id = 'invalid-entry-count'; error = 'invalid-manifest-count' },
        @{ id = 'invalid-manifest-size'; error = 'manifest-size-limit' },
        @{ id = 'malformed-nullability-type'; error = 'invalid-nullability-policy-type' },
        @{ id = 'parameter-budget-overflow'; error = 'parameter-ingress-budget-exceeded' },
        @{ id = 'path-escape'; error = 'path-traversal' },
        @{ id = 'mixed-inputs'; error = 'mixed-inputs' },
        @{ id = 'unsupported-cli-named-sets'; error = 'unsupported-cli-named-sets' },
        @{ id = 'reused-prior-artifact'; error = 'invalid-entry-shape' },
        @{ id = 'attempted-resume'; error = 'invalid-manifest-shape' },
        @{ id = 'mutation-entry'; error = 'invalid-entry-shape' }
    )
    foreach ($expectation in $preflightCases) {
        $case = Get-Case $expectation.id
        Assert-Order $case @() $expectation.id
        if ($null -ne $case) {
            Assert (-not $case.preflight.passed) "$($expectation.id) is rejected during all-suite preflight"
            Assert ($case.preflight.error -ceq $expectation.error) "$($expectation.id) has expected rejection"
            Assert ($case.invocations.Count -eq 0 -and $case.outcomes.Count -eq 0) "$($expectation.id) dispatches nothing and invents no outcome"
        }
    }
    $oversizedManifest = Get-Case 'invalid-manifest-size'
    if ($null -ne $oversizedManifest) { Assert ($oversizedManifest.manifestBytes -gt 1MB) 'manifest size case exceeds the source-file byte cap before parsing' }
    $stale = Get-Content -LiteralPath (Join-Path $root 'fixtures/ticket-b/pinned-revision.json') -Raw | ConvertFrom-Json
    Assert ($stale.pinnedSha256 -cne $stale.currentSha256) 'Ticket B fixture really pins different SQL bytes'
    $staleCase = Get-Case 'changed-sql-hash'
    if ($null -ne $staleCase) { Assert ($staleCase.dispatchCount -eq 0 -and $staleCase.preflight.error -ceq 'sha256-mismatch') 'stale Ticket B SQL hash is found by full-suite preflight before any entry dispatch' }

    $symlink = Get-Case 'symlink-reparse-escape'
    Assert-Order $symlink @() 'symlink-reparse-escape'
    if ($null -ne $symlink) {
        Assert (-not $symlink.preflight.passed -and $symlink.dispatchCount -eq 0) 'symlink escape is rejected before dispatch'
        Assert ($symlink.preflight.error -in @('symlink-reparse-escape','symlink-reparse-escape-simulated')) 'symlink escape reason is explicit'
        Assert ($symlink.symlinkProof -in @('executed-os-symlink-proof','os-link-unavailable-simulated-rejection')) 'symlink proof distinguishes OS execution from simulation'
        Assert (($symlink.preflight.error -eq 'symlink-reparse-escape') -eq ($symlink.symlinkProof -eq 'executed-os-symlink-proof')) 'symlink claim matches actual proof mode'
    }

    $changed = Get-Case 'changed-after-preflight'
    Assert-Order $changed @('orders-by-customer') 'changed-after-preflight'
    if ($null -ne $changed -and $changed.outcomes.Count -eq 1) {
        Assert ($changed.preflight.passed) 'changed-after-preflight demonstrates non-atomic per-entry recheck after successful suite preflight'
        Assert ($changed.stopReason -ceq 'pre-dispatch-sha256-mismatch') 'changed entry is stopped at immediate hash recheck'
        Assert ($changed.outcomes[0].execution -ceq 'completed' -and $changed.outcomes[0].id -ceq 'orders-by-customer') 'completed earlier negative/positive evidence remains retained'
        Assert (-not ($changed.dispatchOrder -contains 'returns-by-customer')) 'changed entry and tail are never dispatched'
    }

    $seenOperations = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($case in $script:results.cases) {
        $seenEntries = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($invocation in $case.invocations) {
            Assert ($seenEntries.Add([string]$invocation.entryId)) "case '$($case.id)' has no retry or duplicate dispatch for entry '$($invocation.entryId)'"
            Assert ($seenOperations.Add([string]$invocation.operationId)) "operation '$($invocation.operationId)' is not reused across cases"
            Assert ($invocation.dispatcher -ceq 'in-memory-mock') "case '$($case.id)' contains no real connection capability"
        }
    }

    if ($failures.Count -gt 0) {
        $failures | ForEach-Object { [Console]::Error.WriteLine("FAIL: $_") }
        [Console]::Error.WriteLine("$($failures.Count) assertion(s) failed.")
        exit 1
    }
    Write-Output "PASS: $($expectedIds.Count) named cases; dispatch order, outcomes, stop behavior, input semantics, hash/path preflight, and no-connection boundary verified."
}
catch {
    [Console]::Error.WriteLine("Verifier failed: $($_.Exception.Message)")
    exit 1
}
