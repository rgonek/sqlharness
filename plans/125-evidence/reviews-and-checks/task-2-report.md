# Task 2: per-statement availability

Base: 32f75a9385e8cb7ce2bb76c4014185cacae6d978. All fixtures synthetic. Scoped product/test commit recorded below after creation; this ignored report is not part of the commit.

## Checklist and evidence

- Partial batch: `Partial_attribution_falls_back_to_whole_batch_without_collapsing` retains confirmed 100/50 reads, suppresses ambiguous prep and retains whole-batch verdict.
- Asymmetric sides and historical evidence: `Statement_projections_preserve_side_availability_and_are_unavailable_for_all_cases`, `Mixed_session_comparison_keeps_versioned_evidence_on_the_new_side`: historical confirmed IO remains visible while historical runtime/LOB remains unavailable. Runtime provenance carried independently; select-all retains single-case restriction.
- Repeated occurrence: `Occurrences_and_unmatched_hidden_work_do_not_mix_new_evidence`; duplicate key/occurrence now fails closed with whole-batch fallback (`Duplicate_occurrence_does_not_upgrade_verdict_or_show_counters`).
- Object identities: `Partial_coverage_and_ambiguous_object_identity_do_not_invent_values`, `Unknown_object_kind_is_not_matched_by_name`, `Journal_object_kind_is_preserved_for_identically_named_objects`; duplicate names and differing/missing kinds suppress object counters. No identity inferred from name alone.
- Missing versus proof: `Omitted_statement_and_missing_object_evidence_are_not_zero`, `Missing_branch_with_unavailable_attribution_stays_null`, `All_case_branch_uses_confirmed_absence_as_zero_and_mean_includes_it`; zero only follows prior confirmed complete attribution; absent counters are null.
- Multi-case coverage: `All_cases_report_partial_metric_coverage_without_aggregating_missing_evidence` verifies 1/2 coverage, unavailable before, complete after, and single-case runtime restriction.
- UI: `IoComparison.test.tsx` covers no IO explanation, Whole batch action, plan link and runtime availability; mixed confirmed/unavailable presentation; optional columns with no recorded values remain hidden. `StatementRows.test.tsx` covers operation no-IO and navigation/reasons.

## RED / GREEN

- RED `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter FullyQualifiedName~Partial_attribution_falls_back_to_whole_batch_without_collapsing --no-restore`: expected 100, actual null. Same command GREEN 1/1 after initial fix.
- RED `... --filter FullyQualifiedName~All_cases_report_partial_metric_coverage_without_aggregating_missing_evidence --no-restore`: expected 1 available case, actual 0. RED `... --filter FullyQualifiedName~Omitted_statement_and_missing_object_evidence_are_not_zero --no-restore`: expected unmatched, actual not-executed. Both included in final GREEN suite.
- RED object availability compilation: missing TableRow.Availability (expected missing DTO), then focused GREEN 3/3.
- UI focused first completed run failed 1/20 because an old test expected an all-empty LOB column; updated expectation. Earlier two attempts interrupted after long worker startup. Typecheck initially failed on test literal widening; fixed.
- Final backend: `$env:PATH="$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"; dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter 'FullyQualifiedName~OptimizationComparerTests|FullyQualifiedName~JournalReaderTests' --no-restore` PASS 156/156, 0 skipped.
- Final UI: `npm test -- --run src/components/optimization/IoComparison.test.tsx src/components/optimization/StatementRows.test.tsx --maxWorkers=1 --no-isolate` PASS 21/21. `npm run typecheck` PASS. `git diff --check` PASS (Git emitted LF/CRLF warnings only).

## Self-review

Reader preserves version eligibility for runtime/grants/LOB and historical confirmed IO without blanket version gating; statement identities and object kinds are checked separately; incomplete/colliding identity cannot upgrade CompareCase; aggregation refuses a value unless every selected case supplies it or existing proof of nonexecution. Numeric nulls remain compatible, DTO adds per-side typed availability with case coverage and provenance. Operation detail suppresses counters when case/variant identity cannot be expressed on the row, while retaining text and plan links. Existing fields and operation routes remain. UI explains unsupported/no-IO and mixed availability and keeps plan navigation. No SQL collector, query, database, thresholds, installation, restart, publication or push changed.

Concern: older journals retain their previous confirmation standard for recorded IO as instructed by controller; this does not retroactively establish newer runtime/grant provenance. Full `just verify`, browser/live DB and whole-plan integration are reserved for Task 6; no claim is made for them. Plan/index bookkeeping belongs to integration rather than this isolated task commit.

Files: JournalReader.cs; OptimizationModels.cs; OptimizationComparer.cs; ui/src/api/types.ts; IoComparison.tsx and test; StatementRows.tsx and test; JournalReaderTests.cs; OptimizationComparerTests.cs.
`nCommit: 496a1816 (tested product tree; report ignored).

## Fix round 1 (review of 496a1816)

- Important: `ProvesAbsence` now requires confirmed, unomitted and unique `(Key, Occurrence)` identities; a duplicate no longer supplies zero or confirmed nonexecution for another unmatched statement. `Duplicate_identity_does_not_prove_another_statement_absent` exercises the false-zero path.
- Important: historical recorded execution counts no longer depend on dashboard evidence version. `Mixed_session_comparison_keeps_versioned_evidence_on_the_new_side` covers operation and comparison; separate `executionsBefore`/`executionsAfter` values and matching availability cover unequal and missing sides. The legacy scalar remains populated from the first available side for compatibility; UI uses the explicit sides when supplied.
- Minors: unproved absence reports `unmatched` ahead of only-side labels; no-IO operation statement display retains hidden-work flags. Tests exercise both.
- RED backend: `$env:PATH="$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"; dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter 'FullyQualifiedName~Duplicate_identity_does_not_prove_another_statement_absent|FullyQualifiedName~Execution_counts_are_independent_per_side_and_survive_old_evidence|FullyQualifiedName~Mixed_session_comparison_keeps_versioned_evidence_on_the_new_side' --no-restore`: three failures (false zero; missing historical execution; missing side counts). Initial build before DTO addition also failed on missing fields.
- RED UI: `npm test -- --run src/components/optimization/IoComparison.test.tsx src/components/optimization/StatementRows.test.tsx --maxWorkers=1 --no-isolate`: 2 failed, 21 passed; missing unequal execution display in both views.
- GREEN backend: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter 'FullyQualifiedName~OptimizationComparerTests|FullyQualifiedName~JournalReaderTests' --no-restore`: 158 passed, 0 failed, exit 0. First full focused run had 1 stale expectation for `only-baseline`; corrected to `unmatched` and reran.
- GREEN UI: `npm test -- --run src/components/optimization/IoComparison.test.tsx src/components/optimization/StatementRows.test.tsx --maxWorkers=1 --no-isolate`: 23 passed, exit 0. `npm run typecheck` exit 0; `git diff --check` exit 0 (LF/CRLF warnings).
- Self-review: version gate remains active for runtime/grants/LOB only; execution availability corresponds to actual side values; incomplete identity cannot prove absence; old scalar and old frontend payload fallback remain. No full gate, live DB, install, publish, restart or push.
Fix round 1 commit: e86bf288.
