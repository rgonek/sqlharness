# Plan 082: Per-statement IO attribution (stage C) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Dependency:** follows plan 087 (measure observations in Optimization), in addition to plans 079, 080 and 081. **Drift check (run first):** dependencies must be merged. `git diff --stat 760f7d6..HEAD -- src/SqlHarness.Core/StatementMetrics.cs src/SqlHarness.Core/Journal src/SqlHarness.Core/Dialect/SqlServerDialect.cs src/SqlHarness.Dashboard`. Use the merged names of 079's journal tables/stream IO and 080's `OptimizationComparer`/`ComparisonPanel`.

**Goal:** Attribute logical reads and scans to statements and objects from actual plans, reconcile them with STATISTICS IO, recognize shared statements, and let the dashboard focus comparisons and verdicts on changed statements while the whole-batch total stays visible.

**Architecture:** Core extracts per-statement, per-object counters from each run's actual plan XML (sum over threads and operators), reconciles per object against the run's engine statistics, and journals the result. The Dashboard's comparer (plan 080) classifies statements (shared / different work / one-sided), chooses the verdict basis (changed statements when both sides are confirmed, else whole batch) and serves additive breakdowns from the median-total run; the UI adds the view toggle, by-statement rows and hidden-work flags.

**Tech Stack:** .NET 10, System.Xml, Microsoft.Data.Sqlite, xUnit; React + Vitest.

**Spec:** [docs/superpowers/specs/2026-10-16-statement-io-attribution-design.md](../docs/superpowers/specs/2026-10-16-statement-io-attribution-design.md)

## Review amendments (2026-10-16, cross-plan review) — these override task text below

1. **No per-run table; reconcile at build time (Task 2).** In `JournalBenchmarkBuilder`, per (variant, case): pick the median-total run (lower median), reconcile that run's plan counters against its `CompareRunArtifact.TableIo`, and journal only that run's statement objects and statuses. Do not add `operation_runs.median_total_run`. Status of a statement = its status in the median-total run. Omission counts live on the existing per-variant/case `operation_metrics` row. With no plan statements, the statement row set is empty; readers interpret that as unavailable at statement grain.
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

- [x] **Step 1:** Confirm 079/080/081/087 merged; drift check; `just verify` baseline. The existing baseline record covers the unchanged revision and predecessor implementation evidence; the passing gate was not rerun for the spike.
- [x] **Step 2 (operator-authorized UAT, read-only):** measured the five-iteration table-variable loop, local cursor, `CREATE INDEX` on a uniquely named `#temp`, and recursive spool query on the explicitly authorized SQL Server `civiclens-dev` profile. Recorded plan identity, executions, per-object reads, and unresolved Worktable reconciliation in [spike evidence](082-spike-evidence.md), with synthetic plan XML under the Task 0 scratch directory.

**Task 0 findings (2026-10-19):** The unchanged baseline was not rerun. The rebuilt worktree CLI/MCP passed the synthetic batch's offline validation. The first authorized local-playground attempt timed out before SQL execution; following user steering, the live spike used the exact `civiclens-dev` SQL Server profile after a successful read-only identity query confirmed `CivicLens` on SQL Server 2025. Loop-expanded plan documents share `StatementCompId` and have one root execution apiece, while explicit same-text source statements have distinct `StatementCompId` values. Table-variable and named-temp logical reads reconcile; Worktable reads do not, so the affected attribution must fail closed. Synthetic plan XML is retained in the Task 0 scratch directory for Task 1 fixture capture; durable findings are in [spike evidence](082-spike-evidence.md).

### Task 1: Extract per-statement object counters

**Files:**
- Modify: `src/SqlHarness.Core/StatementMetrics.cs` (add journal-only extraction with a byte bound, object counters, UDF times, TVF flags and normalized keys; keep the artifact `MaximumStatements` cap and wire shape unchanged)
- Test: `tests/SqlHarness.Tests/StatementMetricsTests.cs`; fixtures under `tests/SqlHarness.Tests/Fixtures/Plans/` (spike-captured cursor and recursive spool XML plus synthetic counters for `[@P]`/`[@R]`, `#F`, DOP-2 parallel scan, scalar UDF and MSTVF)

**Interfaces:**
- Produces: `sealed record StatementObjectCounters(string Object /* "[@P]", "[#F]", "[dbo].[T]" */, string Kind /* "table" | "temp" | "table-variable" | "worktable" */, long LogicalReads, long LobLogicalReads, long Scans)`; `StatementMetrics` gains `string Key`, `int Occurrence`, `long? Executions`, `string? PlanHash`, `IReadOnlyList<StatementObjectCounters> Objects`, `double? UdfElapsedMs`, `IReadOnlyList<HiddenWork> HiddenWork`; `sealed record HiddenWork(string Kind /* "scalar-udf" | "multi-statement-tvf" */, string Name, long? Executions, double? SharePercent)`; `static StatementKey.Normalize(string text) : string`.

- [x] **Step 1: Failing tests:** `Counters_sum_operators_per_object`; `Parallel_counters_sum_over_threads`; `Table_variable_names_come_from_plan`; `Scalar_udf_share`; `Multi_statement_tvf_flagged`; `Normalized_key_ignores_comments_and_whitespace`; `Occurrence_distinguishes_repeated_text`; `Large_batch_reports_omissions`; captured cursor/spool shapes; legacy artifact cap and wire shape.
- [x] **Step 2:** `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~StatementMetricsTests" --no-restore` → FAIL because `JournalStatementMetricsExtractor` and `StatementKey` were missing. **Step 3:** Implemented. **Step 4:** Same command → PASS (18 passed, 0 failed).
- [x] **Step 5:** Commit `feat(082): extract per-statement object counters from actual plans` (`4c42914`).

### Task 2: Reconcile and journal

**Files:**
- Create: `src/SqlHarness.Core/Journal/StatementReconciler.cs`
- Modify: `src/SqlHarness.Core/Journal/JournalSchema.cs` (`operation_statement_objects(statement_id, object, kind, logical_reads, lob_logical_reads, scans)`; `operation_statements.statement_key`, `occurrence`, `statement_text`, `executions`, `plan_hash`, `status`, `udf_elapsed_ms`, `hidden_work_json`; `operation_metrics.omitted_statements`), `JournalBenchmarkBuilder.cs`, `ActivityJournal.cs`
- Test: create `tests/SqlHarness.Tests/Journal/StatementReconcilerTests.cs`; `JournalBenchmarkBuilderTests.cs`

**Interfaces:**
- Produces: `static IReadOnlyList<StatementStatus> StatementReconciler.Reconcile(IReadOnlyList<StatementMetrics> statements, IReadOnlyList<TableIoCounters> observed)`; `enum StatementStatus { Confirmed, Partial, Unavailable }`; journal marks the median-total run per (operation, variant, case).

- [x] **Step 1: Failing tests:** all objects reconcile → all `Confirmed`; one object off by 3 pages → every statement touching it `Partial`, others `Confirmed`; table variables reconcile by group sum; no plan statements produce no rows (unavailable at statement grain); median-total run chosen (lower median for even count); omitted statements fail closed.
- [x] **Step 2:** Focused filter → FAIL because `StatementReconciler`, `StatementStatus`, and new journal attribution fields were absent. **Step 3:** Implemented reconciliation, lower-median selected-run extraction, v18 persistence, and omission metadata. **Step 4:** Focused journal filter → PASS (45 passed, 0 failed).
- [x] **Step 5:** Commit `feat(082): reconcile and journal statement attribution` (see Task 2 report for revision and evidence).

### Task 3: Comparer: statement classes and verdict basis

**Files:**
- Modify: `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs`, `OptimizationModels.cs`, `OptimizationSummaryBuilder.cs` (version-table outcome uses the same basis), sessions-list badge computation from plan 080 Task 3
- Test: `tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs`, `OptimizationSummaryBuilderTests.cs`

**Interfaces:**
- Produces: `OptimizationComparison` gains `string VerdictBasis /* "changed statements" | "whole batch" */`, `string View /* "changed" | "whole" | "statements" */`, `WholeBatchLine WholeBatch` (`Before`, `After`, `Shared`), `IReadOnlyList<StatementRow> Statements`, `string? FallbackNote`; `sealed record StatementRow(string Key, string Text, string Status /* shared | different-work | only-baseline | only-candidate | not-executed */, long? ReadsBefore, long? ReadsAfter, long? ScansBefore, long? ScansAfter, long? Executions, string Change, string Attribution /* confirmed|partial|unavailable */, IReadOnlyList<HiddenWork> HiddenWork, IReadOnlyList<TableRow> Objects)`; query parameter `view=changed|whole|statements` on the compare endpoint (default `changed`).

- [x] **Step 1: Failing tests:** Op203, partial shared-object fail-closed, equal-read/different-plan, branch in some cases, all-case shared/additivity, version-table basis, and session badge basis are covered by the named comparer, summary-builder and reader tests. The first feature RED reached the comparer and failed because the changed view included a shared row.
- [x] **Step 2:** Focused comparer/summary/reader/API filter → PASS (47 passed, 0 failed); Task 2 journal regression filter → PASS (47 passed, 0 failed); dashboard contract and fractional UDF persistence filter → PASS (9 passed, 0 failed).
- [x] **Step 3:** Implemented per-case statement classification, additive whole-batch line with per-side shared totals, fail-closed fallback, current-policy text fallback, and statement detail for measure operations.
- [x] **Step 4:** See `.superpowers/sdd/082-statement-io-attribution/task-3-report.md` for exact commands and evidence.
- [x] **Step 5:** Commit `feat(082): classify statements and focus verdicts on changed work`.

### Task 4: Dashboard views

**Files:**
- Modify: `ui/src/components/optimization/ComparisonPanel.tsx`, `ui/src/lib/optimizationMarkdown.ts`, measure branch of `ui/src/pages/OperationPage.tsx`, `ui/src/api/types.ts`
- Create: `ui/src/components/optimization/StatementRows.tsx`, `HiddenWorkFlag.tsx`
- Test: `ComparisonPanel.test.tsx`, `lib/optimizationMarkdown.test.ts`, create `StatementRows.test.tsx`, `OperationPage.test.tsx` (measure)

- [x] **Step 1: Failing tests:** RED observed for the missing view controls, fallback note and Markdown view/basis lines; the named tests cover URL `view`, fallback behavior, sorted status-bearing rows, execution counts, exact retained-plan destinations, expandable objects, both hidden-work forms, batch headline, omissions, Markdown, measure-only values and PostgreSQL wording.
- [x] **Step 2:** Focused UI suite → PASS (53 passed). Targeted backend schema, provenance, reader, and incomplete-attribution fallback filter → PASS (4 passed). UI production build passed with the Vite large-chunk warning; see ignored `.superpowers/sdd/082-statement-io-attribution/task-4-report.md` for exact commands/output.
- [x] **Step 3:** Added URL/query view wiring, comparison and measured statement views, copy evidence, basis labels/tooltips, and selected-run retained plan links. Operation detail now includes omission count and attribution summary.
- [x] **Step 4:** UI check/build and focused evidence are recorded in the Task 4 report; full `just verify` remains Task 5.
- [x] **Step 5:** Commit `feat(082): per-statement views and hidden-work flags`.

### Task 5: Documentation and verification

- [ ] **Step 1:** README dashboard section (views, verdict basis, attribution statuses, hidden work); `plans/README.md`.
- [ ] **Step 2:** `just verify` → exit 0; record revision/result.
- [ ] **Step 3 (operator-authorized):** UAT measure/compare of an op-203-like batch; confirm reconciliation `confirmed` and the focused verdict; record without business data.
- [ ] **Step 4:** Queue → history; commit `docs(082): record plan 082 evidence`.
