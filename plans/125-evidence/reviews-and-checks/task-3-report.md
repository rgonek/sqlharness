# Task 3 report — stored table IO

Base: e86bf2887968706edf239230aa0b49456a46d341. Commit: 6df3d1b5. Scope: local implementation and commit; no database, install, publish, restart, merge or push.

## RED
- Temporarily reversed only this task's product diff and ran `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --no-restore --filter FullyQualifiedName~Batch_label_keeps_known_totals_when_shared_is_unknown`: 1 failed; expected `whole batch: 100 → 90 reads`, actual `whole batch: reads unavailable`.
- `npm --prefix src/SqlHarness.Dashboard/ui test -- --run src/components/optimization/IoComparison.test.tsx -t 'whole batch exposes recorded counters and hides unavailable ones' --maxWorkers=1`: 1 failed (15 skipped): Page server checkbox missing. Import took 199 seconds; no external service was used.

## Implementation and review
- JournalReader projects all stored fields, including page-server; CompareTableIoEvidence and TableRow carry nullable counters. Whole-batch aggregation uses one persisted per-case representative in sum/mean; logical total retains data + LOB. Unknown contributors remain null, zero remains zero.
- UI consumes comparison counters first, preserves legacy extras fallback only for absent fields, exposes only available counters for the active view, and never projects batch physical counters into statement rows. PostgreSQL has no fabricated table counters.
- Batch label preserves known totals when shared statement evidence is unavailable. Task 2 availability, per-case coverage, execution counts and fallback remain intact.
- Self-review: checked reader column ordinals, case aggregation, data+LOB total, existing PostgreSQL unavailability test, compatibility and `git diff --check`. No migration or SQL execution changes.
- Changed: JournalReader.cs; OptimizationModels.cs; OptimizationComparer.cs; ui api/types.ts, components/optimization/IoComparison.tsx and IoComparison.test.tsx, lib/ioComparison.ts; JournalReaderTests.cs; OptimizationComparerTests.cs.

## Verification
- Focused .NET: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --no-restore --filter 'FullyQualifiedName~JournalReaderTests|FullyQualifiedName~OptimizationComparerTests'`: 161 passed, 0 failed, 0 skipped (30 s). Synthetic fixture has three repetitions, distinct stored fields, null/zero and sum/mean assertions; existing PostgreSQL test asserts empty tables.
- Final focused UI: `npm test -- --run src/components/optimization/IoComparison.test.tsx --maxWorkers=1 --no-isolate`: 17 passed, 0 failed (195.82 s, import 98%). Includes PostgreSQL and unavailable scans controls.
- `npx tsc --noEmit -p tsconfig.app.json`: exit 0 on final committed product.
- `git diff --check`: passed.
- `just verify` deliberately deferred to integration Task 6 as instructed; no browser or live DB verification. Initial UI run had one outdated fixture expectation after available-column filtering; fixture corrected and prior focused run passed. Some local process imports were unusually slow.

## Task 3 review round 1 fix

- Review: `.superpowers/sdd/125-optimization-evidence-clarity/task-3-review.md` identified missing explicit journal-to-reader-to-comparer proof. Added a synthetic four-variant/two-case journal fixture: distinct values in all stored counters, zero and null contributors, `Repeat = 3` and `Runs = 3`; asserts exact sum and rounded mean, data plus LOB, and no multiplication by repetitions. Test drives both cases through actual journal writing, reader and comparison, with distinct sealed case identities.
- This is a test-only coverage gap; a product RED was not applicable. Initial fixture attempts failed 2/2 at the two-case assertion: both rows were assigned to one case because `MatrixCell` was omitted; corrected fixture with distinct parameters and matrix cells. This was not a product failure.
- Final: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --no-restore --filter FullyQualifiedName~Stored_table_io_flows_from_journal_to_case_aggregate_once_per_representative` — 2 passed, 0 failed, 0 skipped. `git diff --check` passed. Self-review checked every stored field on both sides and both modes, three-run setup, null and zero, and product unchanged. No full gate or database access; gate remains Task 6.
