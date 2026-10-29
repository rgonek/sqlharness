# Task 2 report

Implemented screenshot 7-9 requests. No migration, journal writes, backfill, collection changes, live database operations, server restarts, full gate or publication.

## Screenshot dispositions

- 7: joined shadcn Card/Separator KPI section; operations show real distinct journal session count. Failed/rejected counts lead, `(failed + rejected) / all matching operations` percentage is secondary; empty cohort has unavailable rate. Estimated workflow-output tokens lead with measured bytes secondary. Only workflow-output accounting contributes; legacy artifact volumes, chat/input/discovery and model prices are never substituted.
- 8: Top SQL has Logical reads column/order. Each order selects the server's independently ranked full-population top-20 list; no client union of previously truncated lists. Column explicitly describes sum of recorded representative metric medians, not execution-total reads. Missing is unavailable, recorded zero remains zero, partial operation/metric coverage remains visible.
- 9: removed detailed accounting disclosure and all its UI contents (accounting card, per-day/status/exit/kind charts). Existing APIs and dimension/table/wait views remain.

## Projection and scope

`LogicalReadsMedianSum` sums available operation_metrics.logical_reads_median rows once each, including cases/variants, without multiplying by repetitions. Metrics collapse by operation before the operations join: count/time cannot fan out. Available/unavailable operation and metric counts distinguish partial coverage; operations lacking metric rows count as unavailable operations, not synthetic metric rows. Nullable reads remains null when no median exists. Top-by-reads scans all matching SQL groups before LIMIT.

All displayed aggregates, distinct session count, SQL lists, table/wait lists and output-accounting rows share time/profile/unprofiled/recorded-dimension cohort predicates. ProfileOperations intentionally retains the complete time-window profile-chooser metadata contract; unfiltered scope requests preserve dimension choices. Existing recorded-value resolver handles invalid, duplicate, non-string/missing values. JSON parsing is skipped when no dimension predicate exists. Filtered metrics show a loading skeleton without remounting dimension controls or displaying stale metrics.

## Evidence

Red before implementation: `npm test -- src/lib/stats.test.ts` failed because failureRate was missing; added .NET reads/session projection regression failed compilation on missing fields before DTO implementation. Initial behavioral follow-ups caught an incorrect test assumption about count ordering (fixed to inspect known no-metric duration leader), and filter-driven control remount (fixed implementation). Expanded existing dimension tests caught the chooser-metadata invariant; preserved that contract and reran.

Final focused .NET command:
`dotnet test tests/SqlHarness.Tests --filter 'FullyQualifiedName~Dashboard.JournalReaderTests.Stats_|FullyQualifiedName~Dashboard.JournalReaderTests.Workflow_cost|FullyQualifiedName~Dashboard.JournalReaderTests.Legacy_output|FullyQualifiedName~Dashboard.JournalReaderTests.Dimension_|FullyQualifiedName~Dashboard.JournalReaderTests.Unprofiled_scope' --no-restore -v quiet`
Result: 11 passed, 0 failed. Tests cover reads outside count/time top20, no fanout with two metric rows, null/zero/partial, actual two-session distinct count, shared displayed cohort, unprofiled/window and existing dimension contracts.

UI command `npm test -- src/pages/StatsPage.test.tsx src/lib/stats.test.ts`: 16 passed, 0 failed (including final fixture-only rerun). `npm run typecheck`, `npm run lint`, `npm run build`: passed on final product markup. Build retains existing chunk-size advisory. `git diff --check`: passed. CLI `npx shadcn@latest docs card table separator radio-group` and official Base docs consulted. Browser matrix/full Windows gate belong to parent; no full verification claim here.

Owned paths: DashboardModels.cs; JournalReader.cs; ui/src/api/types.ts; ui/src/lib/stats.ts and stats.test.ts; ui/src/pages/StatsPage.tsx and StatsPage.test.tsx; ui/src/test/fixtures.ts; tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs; docs/superpowers/specs/2026-10-06-activity-dashboard-design.md. No Task1/index.css/plan/index edits. Local report/logs are in ignored .superpowers evidence; parent screenshot-details evidence untouched.

Limitations: representative medians are not actual total execution reads; model/chat/session-per-agent costs remain unavailable. Browser appearance awaits parent captures. Linux/live acceptance unverified.

Commit: abdacbe1. Final fixture rerun UI 16/16 and typecheck exit 0. Only parent-owned plan/evidence remain dirty. Report intentionally remains ignored local execution evidence.

## Task 2 fix round 1 (review P2)

Reproduced the disappearing unconfigured recorded dimension: select legacy=older in All time, switch to 24h where legacy is absent but two other operations exist. Regression failed before the fix: dimension total=2 but new-cohort SQL was absent because the aggregate retained the invisible legacy predicate.

StatsPage now derives one effective filter set from current loaded unfiltered dimension options and passes that same set to aggregate query and dimension panel. Retained selections are not mutated or cleared while options are loading/undefined. The aggregate request waits for current dimension options through an additive optional enabled argument on useStats (default true for existing callers); this also avoids issuing a transient stale predicate before normalization. Valid retained filters remain active across ranges; unavailable names cannot constrain displayed metrics.

Owned fix paths: ui/src/pages/StatsPage.tsx, StatsPage.test.tsx, ui/src/api/queries.ts. No C# or other UI polish edits. Regression asserts 24h KPI=2, new-cohort SQL, dimension total=2, absent legacy chooser and no legacy predicate in active aggregate request. Existing range/profile/axis/filter regressions remain included.

Command: npm test -- src/pages/StatsPage.test.tsx src/lib/stats.test.ts — 17 passed, 0 failed. Final typecheck/lint/build in progress at writing; result appended below. No unchanged C# test rerun/full gate. git diff --check passed.

Fix round1 final npm run typecheck, npm run lint, npm run build: all exit 0; existing chunk-size advisory only.
Fix round1 commit: 9fad619e.
