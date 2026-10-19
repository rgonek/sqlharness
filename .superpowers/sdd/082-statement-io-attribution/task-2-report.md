# Plan 082 Task 2 report

## Result

Task 2 implements statement IO reconciliation and selected-run journal persistence. Journal schema is v18. No persistent database, merge, or push was used.

## RED and GREEN evidence

RED command:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~StatementReconcilerTests|FullyQualifiedName~Statement_attribution_uses_the_lower_median_total_run_and_its_io" -v minimal
```

The build failed at the intended missing API: `StatementReconciler`, `StatementStatus`, and journal statement fields (`Objects`, `Status`, `Executions`) did not exist.

GREEN command:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~StatementReconcilerTests|FullyQualifiedName~JournalBenchmarkBuilderTests|FullyQualifiedName~ActivityJournalBenchmarkTests|FullyQualifiedName~JournalContractTests" -v minimal
```

Result: **Passed: 45, Failed: 0, Skipped: 0**. The focused tests cover object reconciliation and mismatch scope, table-variable group reconciliation, unknown IO fail-closed behavior, no-plan absence semantics, lower-median selection for an even run count, omitted statement handling, journal persistence, and v18 schema columns.

`git diff --check` passed. The full `just verify` gate was not run for this task slice; this report does not claim a full plan or branch verification pass.

## Files changed

- Added `StatementReconciler`, comparing per-object plan sums with the selected run's `TableIo`, including LOB reads and scans. Missing observed counters yield `Unavailable`; mismatches or incomplete plan counters yield `Partial`. Unmatched nonzero observed IO makes all retained statements partial. Table-variable names reconcile as one group.
- Changed `JournalBenchmarkBuilder` to select the lower-median-total run per variant, parameter set, and case, then extract and reconcile only that run's statement metrics. Existing statement CPU, elapsed, and operator summaries remain populated.
- Added schema v18 fields for statement key/occurrence, plan hash, status, executions, optional plan text, UDF/hidden work, statement object counters, and `operation_metrics.omitted_statements`. `FirstRetainedVersion` advanced from 17 to 18, following the journal archive rule; no v17 migration is added.
- Added tests in `StatementReconcilerTests`, `JournalBenchmarkBuilderTests`, `ActivityJournalBenchmarkTests`, and `JournalContractTests`; updated the Plan 082 task checklist and plan index.

## Task 3/4 interface

`JournalStatementMetrics` carries `StatementKey`, `Occurrence`, `Executions`, `PlanHash`, `Status`, `UdfElapsedMilliseconds`, `HiddenWork`, `Objects`, and plan-derived `Text`. `JournalVariantMetrics.OmittedStatements` records omissions for the selected run at the existing variant/case grain. SQLite stores statement text from plan XML only when `storeSensitive` is enabled; otherwise it stores `statement {ordinal} ({shortHash})`.

An empty statement row set with zero omissions means no plan statements were available and is unavailable at statement grain; there is no synthetic sentinel row. Any omitted statements are recorded on the matching `operation_metrics` row and every retained statement from that selected run is marked partial. This prevents retained subsets from appearing complete.

## Concerns and commit

Worktable/spool attribution remains partial when engine IO cannot be mapped to plan objects, matching the Task 0 spike evidence. Linux, live database behavior, and the full `just verify` gate remain unverified.

Commit: `feat(082): reconcile and journal statement attribution` (the final revision is reported with this handoff).
