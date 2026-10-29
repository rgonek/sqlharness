# Plan 123: Shared version table across conditions groups

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. A queue entry is not execution approval; live DB, installation and publication stay out of scope.

**Status:** IMPLEMENTED; Tasks 1–4 reviewed; Task 5 review and whole-branch Sol review pending; see [verification evidence](123-evidence/verification.md).

**Goal:** One version table per subject and scope across all conditions groups, with an explicitly labelled *approximate* A/B comparison when A and B come from different groups, supported by per-case result row counts.

**Architecture:** Core journals per-case result shape (result-set count, rows returned) and a varying-only case key. The dashboard keeps conditions groups as the unit of exact comparison and adds a cross-group comparison mode that pairs cases by the varying-only key, shows metrics side by side with row counts, and never emits a better/worse verdict or an equivalence path.

**Tech Stack:** C#/.NET, SQLite journal, React/TypeScript (TanStack Router, shadcn/Base UI), xUnit, Vitest.

**Spec:** This plan. Motivation: session 8 (`clients-add-has-active-cards`) has compares 203 (4 cases, fixed `@Now`) and 204 (5 cases, fixed `@Today`, extra `@clientId = -1`). Each lands in its own conditions group (v0/v1 and v2/v3), so the user has to switch the group select to see the history and cannot compare v1 with v3. Background: [Plan 118 decision](118-evidence/decision.md) deferred cross-group UI until an interface supplies both group identities explicitly; this plan provides that through an explicit `groupB` selection and does not infer pairs of operations.

## Global constraints

- Exact comparison semantics stay unchanged: within one group, `OptimizationComparer.Compare`, `PairBasis`, verdicts and equivalence labels behave exactly as today.
- A cross-group comparison never reports `better`/`worse`, never claims results equivalence, and always shows the copy `Different conditions — approximate, not a verdict`.
- Case pairing across groups uses only the journaled varying-only case key. No pairing by label text, by case index or by case id.
- Row counts are new evidence only: no backfill from artifacts, plan documents or `runs.jsonl`. Missing values render as `—`, never `0`.
- No backward compatibility work: old dashboard URLs, old payload shapes and pre-26 evidence need no special handling beyond rendering `—`. Remove replaced code (the group select, single-group-only URL handling) instead of keeping both paths.
- The varying-only key is an HMAC over the same sealed parameters as the existing case fingerprint, so `storeSensitive = false` reveals nothing new.
- Journal migration 26 only adds the two columns (needed so the existing journal opens); no rewrite of retained rows.

## Review focus

1. A version measured in both groups (same SQL hash re-run under new conditions) appears as two rows with distinct group badges, and selecting either one resolves to the correct group's evidence (Tasks 3, 4).
2. Cross-group selection where no case pairs (fixed and varying both differ) shows `0 of N cases paired`, SQL and plan diffs, and no metric totals (Task 3).
3. Repetitions within one variant/case returning different row counts (non-deterministic query, concurrent writes) journal `rows_returned = NULL` and are shown as `varies`, not as one of the values (Task 1).
4. A stale `groupB` or a `b` version not in `groupB` returns 404 on the compare endpoint, and the UI falls back to the group's defaults (Tasks 3, 4).

## Order and cost

Order **1 → 2 → 3 → 4 → 5**. Tasks 2–4 require 1; 4 requires 3. Serialize shared models (`OptimizationModels.cs`, `api/types.ts`) and the plans index.

| Task | Estimate |
|---|---|
| 1. Journal row counts and varying key | 1 day |
| 2. Dashboard evidence and version rows | 0.5 day |
| 3. Cross-group comparison and endpoints | 1–1.5 days |
| 4. Shared version table UI | 1–1.5 days |
| 5. Docs and verification | 0.5 day |

---

### Task 1: Journal per-case row counts and varying-only case key

**Files:**
- Modify: `src/SqlHarness.Core/CanonicalResults.cs` (`CanonicalResult`, `CanonicalResultAccumulator`)
- Modify: `src/SqlHarness.Core/Artifacts.cs` (`CompareRunArtifact`)
- Modify: `src/SqlHarness.Core/Dialect/SqlServerDialect.cs`, `src/SqlHarness.Core/Postgres/PostgresBenchmark.cs` (where `CompareRunArtifact` is built from `CollectedCompare`)
- Modify: `src/SqlHarness.Core/Journal/BenchmarkJournalRecord.cs` (`JournalVariantMetrics`)
- Modify: `src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs`
- Modify: `src/SqlHarness.Core/Journal/ActivityJournal.cs` (`InsertMetric`, conditions sealing at ~line 815)
- Modify: `src/SqlHarness.Core/Journal/ConditionsModel.cs`, `ConditionsModelBuilder.cs`
- Modify: `src/SqlHarness.Core/Journal/JournalSchema.cs`
- Test: `tests/SqlHarness.Tests/Journal/JournalBenchmarkBuilderTests.cs`, `ActivityJournalBenchmarkTests.cs`, `ConditionsModelBuilderTests.cs`, `JournalContractTests.cs`

**Interfaces:**
- Produces: `CanonicalResult(string Hash, OutputFootprint Footprint)` gains `IReadOnlyList<long> RowCounts { get; init; } = []` (one entry per non-plan result set).
- Produces: `CompareRunArtifact` gains `[JsonIgnore] IReadOnlyList<long>? ResultRowCounts { get; init; }` — journal only, `runs.jsonl` unchanged.
- Produces: `JournalVariantMetrics` gains `int? ResultSets { get; init; }` and `long? RowsReturned { get; init; }`.
- Produces: `ConditionsCase` gains `public string? VaryingFingerprint { get; init; }`, stored inside the existing conditions JSON.
- Produces: `operation_metrics.result_sets INTEGER`, `operation_metrics.rows_returned INTEGER` (migration 26).

- [x] **Step 1: Write failing tests**
  - `JournalBenchmarkBuilderTests.Variant_rows_returned_is_sum_of_result_sets_when_repetitions_agree`: three runs with `ResultRowCounts = [4, 1]` → `ResultSets == 2`, `RowsReturned == 5`.
  - `JournalBenchmarkBuilderTests.Variant_rows_returned_is_null_when_repetitions_disagree`: runs `[5]`, `[5]`, `[6]` → `RowsReturned == null`, `ResultSets == 1`.
  - `JournalBenchmarkBuilderTests.Variant_rows_returned_is_null_without_capture`: `ResultRowCounts == null` → both null.
  - `ConditionsModelBuilderTests.Varying_fingerprint_ignores_fixed_parameters`: two models with the same varying `@clientId = 6795` and fixed `@Now` vs `@Today` → equal `VaryingFingerprint`, different `Fingerprint`.
  - `ConditionsModelBuilderTests.Varying_fingerprint_differs_by_varying_value`: `@clientId = 6795` vs `18` → different.
  - `ActivityJournalBenchmarkTests.Metrics_round_trip_result_sets_and_rows_returned` and `JournalContractTests` schema version 26 with both columns present after migrating a v25 database.
- [x] **Step 2: Run** `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~JournalBenchmarkBuilderTests|FullyQualifiedName~ConditionsModelBuilderTests|FullyQualifiedName~ActivityJournalBenchmarkTests|FullyQualifiedName~JournalContractTests"` — expected FAIL (missing members/columns).
- [x] **Step 3: Implement**
  - `CanonicalResultAccumulator.EndResultSet` appends `_currentRowCount` to a list returned via `Complete()`.
  - Fill `ResultRowCounts` from `CollectedCompare.Canonical.RowCounts` wherever `CompareRunArtifact` is created (compare and measure paths).
  - Builder: per `(variant, parameterSet, matrixCell)` group, set `ResultSets`/`RowsReturned` only when every run has non-null counts and all lists are equal; otherwise `RowsReturned = null` (and `ResultSets` = the common count when all counts agree, else null).
  - `ConditionsModelBuilder.CaseVaryingFingerprintPayload(ConditionsModel model, string setupHash, ConditionsCase item)`: same payload as `CaseMeasurementFingerprintPayload` minus `@fixed`. Seal it with `Hmac(salt, ...)` next to `MeasurementFingerprint` in `ActivityJournal`.
  - Migration `[26] = "ALTER TABLE operation_metrics ADD COLUMN result_sets INTEGER; ALTER TABLE operation_metrics ADD COLUMN rows_returned INTEGER;"`, add both columns to `Current`, `CurrentVersion = 26`, extend `InsertMetric`.
- [x] **Step 4: Run** the same filter — expected PASS.
- [x] **Step 5: Commit** `Journal per-case row counts and varying case key`

### Task 2: Dashboard evidence and version-row row counts

**Files:**
- Modify: `src/SqlHarness.Dashboard/Optimization/OptimizationModels.cs` (`CompareMetricEvidence`, `CompareCaseEvidence`, `VersionRow`)
- Modify: `src/SqlHarness.Dashboard/JournalReader.cs` (~lines 600–645 metric/case evidence)
- Modify: `src/SqlHarness.Dashboard/Optimization/OptimizationSummaryBuilder.cs` (`BuildVersions`)
- Modify: `src/SqlHarness.Dashboard/ui/src/api/types.ts`
- Test: `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs`, `OptimizationSummaryBuilderTests.cs`

**Interfaces:**
- Consumes: Task 1 columns and `ConditionsCase.VaryingFingerprint`.
- Produces: `CompareMetricEvidence` gains `long? RowsReturned { get; init; }`, `int? ResultSets { get; init; }`; `CompareCaseEvidence` gains `string? VaryingFingerprint { get; init; }`; `VersionRow` gains `long? RowsReturnedSum { get; init; }` (sum over the latest measurement's cases; null if any case is null). TS: `rowsReturnedSum?: number | null`.

- [x] **Step 1: Failing tests**: `JournalReaderTests.Optimization_evidence_reads_rows_returned_and_varying_key` (seeded v26 rows → values; seeded NULL → null). `OptimizationSummaryBuilderTests.Version_row_rows_returned_sum_is_null_when_any_case_missing` and `..._sums_cases` (cases 1, 0, 3 → 4).
- [x] **Step 2: Run** `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~JournalReaderTests|FullyQualifiedName~OptimizationSummaryBuilderTests"` — FAIL.
- [x] **Step 3: Implement** reader mapping (`SELECT` the two new columns; varying key from the parsed conditions case, as `MeasurementFingerprint` already is) and `RowsReturnedSum` in `BuildVersions`.
- [x] **Step 4: Run** the filter — PASS.
- [x] **Step 5: Commit** `Expose per-case row counts in optimization evidence`

### Task 3: Cross-group approximate comparison and endpoints

**Files:**
- Modify: `src/SqlHarness.Dashboard/Optimization/OptimizationModels.cs` (`PairBasis`, `OptimizationComparison`, `CaseOption`)
- Modify: `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs`
- Modify: `src/SqlHarness.Dashboard/DashboardServer.cs` (both `/optimization/compare` routes and their handlers, ~lines 268, 293, 440–560)
- Modify: `src/SqlHarness.Dashboard/ui/src/api/types.ts`, `ui/src/api/queries.ts`
- Test: `tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs`, `DashboardServerTests.cs`, `DashboardContractTests.cs`

**Interfaces:**
- Produces: `PairBasis.ApproximateAcrossConditions` (serialized like the existing members).
- Produces: `OptimizationComparer.CompareAcrossGroups(ConditionsGroup groupA, ConditionsGroup groupB, IReadOnlyList<CompareEvidence> compares, string a, string b, string selectedCase, string aggregate, string view = "changed") -> OptimizationComparison`. `compares` are both groups' evidence for the selected subject/scope.
- Produces: `OptimizationComparison` gains `string? GroupA`, `string? GroupB`, `string? GroupLabelA`, `string? GroupLabelB` and `IReadOnlyList<CaseRows> CaseRows` where `record CaseRows(string CaseKey, string LabelA, string LabelB, long? RowsA, long? RowsB)`.
- Produces: compare endpoints accept optional `groupB`; when absent or equal to `group`, behaviour is today's `Compare`. TS query params gain `groupB?: string`.

- [x] **Step 1: Failing tests** in `OptimizationComparerTests`:
  - `Across_groups_pairs_cases_by_varying_fingerprint_only`: group A cases {6795, 18, 4017, 1027}, group B {6795, 18, 4017, 1027, -1} with matching varying keys → 4 paired, `CoverageLabel == "4 of 5 cases paired"`, `Basis == ApproximateAcrossConditions`.
  - `Across_groups_never_emits_verdict_or_equivalence`: B reads lower than A → `EquivalenceLabel == "Different conditions — approximate, not a verdict"`, no `better`/`worse` in any row outcome.
  - `Across_groups_with_no_pairs_keeps_sql_and_plan_snapshots`: no shared varying key → `Cases` empty, `Tables` empty, SQL/plan snapshots for both hashes present, `CoverageLabel == "0 of 5 cases paired"`.
  - `Across_groups_case_rows_carry_rows_returned`: A 6795 rows 1, B 6795 rows 1 → `CaseRows` entry with `RowsA == 1 && RowsB == 1`; null stays null.
  - `Same_group_compare_is_unchanged`: existing `Compare` output for one group equals the current snapshot (guard).
  - `DashboardServerTests.Optimization_compare_with_group_b_returns_approximate_comparison` (both session and agent-session routes) and `..._with_unknown_group_b_returns_404` and `..._b_not_in_group_b_returns_404`.
- [x] **Step 2: Run** `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~OptimizationComparerTests|FullyQualifiedName~DashboardServerTests|FullyQualifiedName~DashboardContractTests"` — FAIL.
- [x] **Step 3: Implement**
  - Resolve version A in `groupA`'s selected scope and B in `groupB`'s same-named scope; different scope names → existing `ConditionsDiffer` empty result.
  - Pair key = `VaryingFingerprint`; a case without it never pairs. Labels come from each side's case label.
  - Metrics (`Tables`, `WholeBatch`, statements) use the latest snapshot of each hash restricted to paired cases, reusing the existing snapshot/aggregation helpers; `OperationId = null`; no `Equivalent`/`EquivalentPath` call.
  - Handlers: shared helper resolves subject, `group`, optional `groupB`, scope, A in group, B in groupB; evidence filter includes both fingerprints.
- [x] **Step 4: Run** the filter — PASS.
- [x] **Step 5: Commit** `Add approximate cross-group optimization comparison`

### Task 4: Shared version table UI

**Files:**
- Modify: `src/SqlHarness.Dashboard/ui/src/components/optimization/OptimizationSection.tsx`
- Modify: `src/SqlHarness.Dashboard/ui/src/components/optimization/VersionTable.tsx`
- Modify: `src/SqlHarness.Dashboard/ui/src/components/optimization/ComparisonPanel.tsx`
- Modify: `src/SqlHarness.Dashboard/ui/src/router.tsx` (`SessionSearch.groupB`)
- Test: `OptimizationSection.test.tsx`, `VersionTable.test.tsx`, `ComparisonPanel.test.tsx`, `src/test/fixtures.ts`

**Interfaces:**
- Consumes: Task 2 `rowsReturnedSum`, Task 3 `groupB` param and comparison fields.
- Produces: `SessionSearch.groupB?: string`. URL semantics: `group` = A's group, `groupB` = B's group (omitted when equal).

- [x] **Step 1: Failing tests**
  - `OptimizationSection.test.tsx` "lists versions from all conditions groups in one table": fixture with the session-8 shape → rows v0, v1, v2, v3 in one table, each with a conditions badge (`A` for the first group, `B` for the second, with the full group label in a tooltip/legend); the "Conditions group" select is gone.
  - "selecting B from another group sets groupB and shows the approximate banner": click B on v3 while A = v1 → navigation search `{ group: <6542…>, groupB: <d5e2…>, a: "v1", b: "v3" }`, banner text `Different conditions — approximate, not a verdict`.
  - `VersionTable.test.tsx` "shows rows column and dash for missing": `rowsReturnedSum` 5 → `5`, null → `—`.
  - `ComparisonPanel.test.tsx` "approximate comparison shows per-case rows and coverage": `CaseRows` rendered as `6795: 1 → 1 rows`; differing rows highlighted as `rows differ`; no verdict chip.
- [x] **Step 2: Run** `npm --prefix src/SqlHarness.Dashboard/ui run test -- OptimizationSection VersionTable ComparisonPanel` — FAIL.
- [x] **Step 3: Implement**: build table rows as `groups.flatMap(group => scopeOf(group).versions.map(row => ({ ...row, group })))`, ordered by numeric version; row identity `${group.fingerprint}:${version}`; A/B selection stores version + group; the scope select lists the union of scope names. Remove the group select. Keep defaults from the group in `group` (or the first group).
- [x] **Step 4: Run** the same command — PASS.
- [x] **Step 5: Commit** `Show one version table across conditions groups`

### Task 5: Documentation and verification

**Files:**
- Modify: `README.md` (dashboard optimization section: shared table, approximate cross-group mode, row counts for new sessions only)
- Modify: `plans/README.md` (status), this plan's status line

- [x] **Step 1:** Update README copy; no tool description/schema change is needed (MCP interfaces unchanged — confirm with `git diff --stat src/SqlHarness.Mcp` being empty).
- [x] **Step 2:** Run `just verify` on the final tree per [verification](../docs/agents/verification.md); record revision and counts in `plans/123-evidence/verification.md`.
- [x] **Step 3:** Browser check of the session-8 shape with a fixture (not a live journal): one table v0–v3, cross-group banner, rows column. Save screenshots under `plans/123-evidence/`.
- [x] **Step 4: Commit** `Document shared version table and verify plan 123`

## Deferred

- Journaling the per-case result hash for a stronger "same result" signal across groups.
- Explaining which fixed parameters differ (Plan 118's bounded explanation subset); the banner shows both group labels instead.
