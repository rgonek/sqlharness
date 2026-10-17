# Plan 082: Per-statement IO attribution (stage C) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** plans 079, 080 and 081 must be merged. `git diff --stat 760f7d6..HEAD -- src/SqlHarness.Core/StatementMetrics.cs src/SqlHarness.Core/Journal src/SqlHarness.Core/Dialect/SqlServerDialect.cs src/SqlHarness.Dashboard`. Use the merged names of 079's journal tables/stream IO and 080's `OptimizationComparer`/`ComparisonPanel`.

**Goal:** Attribute logical reads and scans to statements and objects from actual plans, reconcile them with STATISTICS IO, recognize shared statements, and let the dashboard focus comparisons and verdicts on changed statements while the whole-batch total stays visible.

**Architecture:** Core extracts per-statement, per-object counters from each run's actual plan XML (sum over threads and operators), reconciles per object against the run's engine statistics, and journals the result. The Dashboard's comparer (plan 080) classifies statements (shared / different work / one-sided), chooses the verdict basis (changed statements when both sides are confirmed, else whole batch) and serves additive breakdowns from the median-total run; the UI adds the view toggle, by-statement rows and hidden-work flags.

**Tech Stack:** .NET 10, System.Xml, Microsoft.Data.Sqlite, xUnit; React + Vitest.

**Spec:** [docs/superpowers/specs/2026-10-16-statement-io-attribution-design.md](../docs/superpowers/specs/2026-10-16-statement-io-attribution-design.md)

## Review amendments (2026-10-16, cross-plan review) — these override task text below

1. **No per-run table; reconcile at build time (Task 2).** In `JournalBenchmarkBuilder`, per (variant, case): pick the median-total run (lower median), reconcile that run's plan counters against its `CompareRunArtifact.TableIo`, and journal only that run's statement objects and statuses. Drop `operation_runs.median_total_run`. Status of a statement = its status in the median-total run.
2. **Journal version.** Declare a new step (`VersionN`, next free at execution; follows 079's `FirstRetainedVersion` rule) for `operation_statement_objects` and new `operation_statements` columns; pin in `JournalContractTests`.
3. **Statement cap stays artifact-neutral (Task 1).** Do not change `StatementMetricsExtractor.MaximumStatements` (it shapes 077's artifact `statements` section). Add a journal-only extraction path with a byte bound. Put extractor tests in 077's `StatementMetricsTests.cs` (not `PlanMetricsExtractorTests`, which tests another class).
4. **Statement text.** Text comes only from plan XML (`storeSensitive`); without it `StatementRow.Text` = `statement {ordinal} ({shortHash})`.
5. **Readers and producers.** Task 3 also modifies `JournalReader` (load keys, objects, statuses into `CompareEvidence`). Task 4's measure view needs operation-detail fields: extend `GET /api/operations/{id}` with `statements` (same `StatementRow` shape, no comparison).

## Status and dependencies

- TODO. Planning base `760f7d6`. Depends on 079, 080, 081 and 087 (measure observations in the comparer).
- Priority P2; effort L; risk MEDIUM (verdict basis changes what reviewers see).

## Global Constraints

- Counters: per statement, per object = Σ over operators referencing the object and Σ over `RunTimeCountersPerThread` of `ActualLogicalReads` (+ `ActualLobLogicalReads`) and `ActualScans`.
- Status per statement: `confirmed` when every object it touches reconciles in that run (Σ plan = observed engine IO for the object; table variables reconcile at group level), `partial` otherwise, `unavailable` without plans.
- Statement key = SHA-256 of normalized text (comments removed, whitespace runs collapsed to one space, trimmed) + occurrence index among equal keys; execution count kept as data (`ActualExecutions` of the root operator).
- `shared` = same key on both sides in the case AND reads within stage B margin (not ≥ 1% and ≥ 10 pages apart) AND equal scans AND equal statement plan hash; otherwise `same text, different work`; one-sided keys `only in baseline` / `only in candidate`; in `All cases`, collapse only when shared in every case; keys missing in some cases show `not executed in this case`.
- Verdict basis: changed statements when both sides of the case are `confirmed`, else whole batch; label `basis: changed statements` / `basis: whole batch`.
- Breakdowns come from the run whose batch total is the (lower) median; headline line exact text: `whole batch: {X} → {Y} reads (shared statements: {Z})`.
- Fallback note exact text: `per-statement attribution incomplete`.
- Hidden-work texts: `UDF time {p}% of statement`; `multi-statement function: internal reads not visible ({n} executions)`.
- Omission text: `omitted statements: {n} (their reads remain in the batch total)`.
- PostgreSQL: `per-statement IO not available for this engine`.
- No auto-formatting; commits `feat(082): …`; completion requires `just verify`.

## Review Focus

1. Two statements reading the same table where totals do not reconcile → both `partial`, nothing collapsed, whole-batch fallback → Task 3 `Mismatch_on_shared_object_fails_closed`.
2. Same text and same reads but a different plan (seek vs scan, scans 1 → 5,000 within margin) → `same text, different work`, visible → Task 3 `Equal_reads_different_plan_is_not_shared`.
3. An IF branch executed only in case 2 → shown `not executed in this case` for case 1, never collapsed in All cases → Task 3 `Branch_in_some_cases_is_never_collapsed`.
4. A batch with 300 statements → no silent truncation; omission text and batch total intact → Task 1 `Large_batch_reports_omissions`.
5. Parallel plan → counters summed over threads equal STATISTICS IO (spike-verified shape) → Task 1 `Parallel_counters_sum_over_threads`.

---

### Task 0: Drift check and post-081 spike

- [ ] **Step 1:** Confirm 079/080/081 merged; drift check; `just verify` baseline.
- [ ] **Step 2 (operator-authorized UAT, read-only):** measure a batch with a `WHILE` loop doing `INSERT @t SELECT …` (5 iterations), a local cursor over a `SELECT`, a `CREATE INDEX` on `#temp`, and a query producing a worktable/spool with reads. Record: plan documents per loop iteration vs per statement, `ActualExecutions`, reconciliation per object. Adapt Task 1's identity/aggregation to what SQL Server emits; record findings here without business data.

### Task 1: Extract per-statement object counters

**Files:**
- Modify: `src/SqlHarness.Core/StatementMetrics.cs:9-104` (raise `MaximumStatements` → byte bound, add object counters, UDF times, TVF flags, normalized key), `src/SqlHarness.Core/Dialect/SqlServerDialect.cs` (pass engine IO per object to reconciliation)
- Test: `tests/SqlHarness.Tests/Journal/PlanMetricsExtractorTests.cs`; fixtures under `tests/SqlHarness.Tests/Fixtures/Plans/` (synthetic XML shaped like the spike: table variables `[@P]`/`[@R]`, `#F`, DOP-2 scan with three thread counters, scalar UDF with `UdfElapsedTime`, MSTVF)

**Interfaces:**
- Produces: `sealed record StatementObjectCounters(string Object /* "[@P]", "[#F]", "[dbo].[T]" */, string Kind /* "table" | "temp" | "table-variable" | "worktable" */, long LogicalReads, long LobLogicalReads, long Scans)`; `StatementMetrics` gains `string Key`, `int Occurrence`, `long? Executions`, `string? PlanHash`, `IReadOnlyList<StatementObjectCounters> Objects`, `double? UdfElapsedMs`, `IReadOnlyList<HiddenWork> HiddenWork`; `sealed record HiddenWork(string Kind /* "scalar-udf" | "multi-statement-tvf" */, string Name, long? Executions, double? SharePercent)`; `static StatementKey.Normalize(string text) : string`.

- [ ] **Step 1: Failing tests:** `Counters_sum_operators_per_object` (insert into `[@P]` 1001 + scans 2 + 2 across statements → per statement values); `Parallel_counters_sum_over_threads` (41,943 + 42,810 + 2 = 84,755; scans 3); `Table_variable_names_come_from_plan`; `Scalar_udf_share` (`UdfElapsedTime` 28 of 29 → 96.6%); `Multi_statement_tvf_flagged` (executions from operator); `Normalized_key_ignores_comments_and_whitespace`; `Occurrence_distinguishes_repeated_text`; `Large_batch_reports_omissions` (300 statements, byte bound hit → `omittedStatements > 0`).
- [ ] **Step 2:** `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~PlanMetricsExtractor"` → FAIL. **Step 3:** Implement. **Step 4:** → PASS.
- [ ] **Step 5:** Commit `feat(082): extract per-statement object counters from actual plans`.

### Task 2: Reconcile and journal

**Files:**
- Create: `src/SqlHarness.Core/Journal/StatementReconciler.cs`
- Modify: `src/SqlHarness.Core/Journal/JournalSchema.cs` (`operation_statement_objects(statement_id, object, kind, logical_reads, lob_logical_reads, scans)`; `operation_statements.statement_key`, `occurrence`, `executions`, `plan_hash`, `status`, `udf_elapsed_ms`, `hidden_work_json`; `operation_runs.median_total_run INTEGER` flag), `JournalBenchmarkBuilder.cs`, `ActivityJournal.cs`
- Test: create `tests/SqlHarness.Tests/Journal/StatementReconcilerTests.cs`; `JournalBenchmarkBuilderTests.cs`

**Interfaces:**
- Produces: `static IReadOnlyList<StatementStatus> StatementReconciler.Reconcile(IReadOnlyList<StatementMetrics> statements, IReadOnlyList<TableIoCounters> observed)`; `enum StatementStatus { Confirmed, Partial, Unavailable }`; journal marks the median-total run per (operation, variant, case).

- [ ] **Step 1: Failing tests:** all objects reconcile → all `Confirmed`; one object off by 3 pages → every statement touching it `Partial`, others `Confirmed`; table variables reconcile by group sum; no plans → `Unavailable`; median-total run chosen (lower median for even count).
- [ ] **Step 2:** → FAIL. **Step 3:** Implement. **Step 4:** → PASS.
- [ ] **Step 5:** Commit `feat(082): reconcile and journal statement attribution`.

### Task 3: Comparer: statement classes and verdict basis

**Files:**
- Modify: `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs`, `OptimizationModels.cs`, `OptimizationSummaryBuilder.cs` (version-table outcome uses the same basis), sessions-list badge computation from plan 080 Task 3
- Test: `tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs`, `OptimizationSummaryBuilderTests.cs`

**Interfaces:**
- Produces: `OptimizationComparison` gains `string VerdictBasis /* "changed statements" | "whole batch" */`, `string View /* "changed" | "whole" | "statements" */`, `WholeBatchLine WholeBatch` (`Before`, `After`, `Shared`), `IReadOnlyList<StatementRow> Statements`, `string? FallbackNote`; `sealed record StatementRow(string Key, string Text, string Status /* shared | different-work | only-baseline | only-candidate | not-executed */, long? ReadsBefore, long? ReadsAfter, long? ScansBefore, long? ScansAfter, long? Executions, string Change, string Attribution /* confirmed|partial|unavailable */, IReadOnlyList<HiddenWork> HiddenWork, IReadOnlyList<TableRow> Objects)`; query parameter `view=changed|whole|statements` on the compare endpoint (default `changed`).

- [ ] **Step 1: Failing tests:** `Op203_shape_focuses_on_changed_statement` (shared 11,910 each side + changed 100 → 50: verdict `better`, basis `changed statements`, line `whole batch: 12010 → 11960 reads (shared statements: 11910)`); `Mismatch_on_shared_object_fails_closed` (basis `whole batch`, note `per-statement attribution incomplete`, nothing collapsed); `Equal_reads_different_plan_is_not_shared`; `Branch_in_some_cases_is_never_collapsed`; `All_cases_collapses_only_everywhere_shared`; `Breakdown_is_additive` (changed + shared = whole batch); `Version_table_and_session_badge_use_same_basis`.
- [ ] **Step 2:** → FAIL. **Step 3:** Implement. **Step 4:** → PASS.
- [ ] **Step 5:** Commit `feat(082): classify statements and focus verdicts on changed work`.

### Task 4: Dashboard views

**Files:**
- Modify: `ui/src/components/optimization/ComparisonPanel.tsx`, `ui/src/lib/optimizationMarkdown.ts`, measure branch of `ui/src/pages/OperationPage.tsx`, `ui/src/api/types.ts`
- Create: `ui/src/components/optimization/StatementRows.tsx`, `HiddenWorkFlag.tsx`
- Test: `ComparisonPanel.test.tsx`, `lib/optimizationMarkdown.test.ts`, create `StatementRows.test.tsx`, `OperationPage.test.tsx` (measure)

- [ ] **Step 1: Failing tests:** toggle `Changed statements | Whole batch | By statement` (URL param `view`), default `Changed statements`; headline line text exact; verdict shows `basis: changed statements`; fallback note shown and toggle defaults to `Whole batch`; by-statement rows sorted by absolute change, status labels, execution count, plan link per statement, expandable objects; hidden-work flag texts exact, also next to the reads headline; omission text; Markdown states the view and basis and includes the headline line; measure operation page shows by-statement rows and flags; PostgreSQL message.
- [ ] **Step 2:** `npm --prefix src/SqlHarness.Dashboard/ui test` → FAIL. **Step 3:** Implement. **Step 4:** → PASS; build.
- [ ] **Step 5:** Commit `feat(082): per-statement views and hidden-work flags`.

### Task 5: Documentation and verification

- [ ] **Step 1:** README dashboard section (views, verdict basis, attribution statuses, hidden work); `plans/README.md`.
- [ ] **Step 2:** `just verify` → exit 0; record revision/result.
- [ ] **Step 3 (operator-authorized):** UAT measure/compare of an op-203-like batch; confirm reconciliation `confirmed` and the focused verdict; record without business data.
- [ ] **Step 4:** Queue → history; commit `docs(082): record plan 082 evidence`.
