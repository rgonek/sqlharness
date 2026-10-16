# Plan 080: Optimization dashboard (stage B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** plans 077 and 079 must be merged to `main`. Rebase this plan's references on that commit: `git diff --stat 4154fe7..HEAD -- src/SqlHarness.Dashboard src/SqlHarness.Core/Journal`. Use 079's actual names for the conditions model, case ids/labels, fingerprints, `/api/operations/{id}/repro`, `ConditionsList` and `ReproScriptButtons`.

**Goal:** Reorganize the dashboard around finding and presenting optimization results: a session Optimization section with a version table and two-version comparison, compare-first operation pages, and de-noised lists.

**Architecture:** All comparison semantics (grouping by conditions, default pair, per-case better/worse, Mixed, aggregates, equivalence chains) live in one C# component in the Dashboard project and are served as ready-to-render JSON; the React UI renders it and builds the Markdown copy from the same payload, so screen and copy cannot diverge. Lists and badges change through existing components.

**Tech Stack:** .NET 10 (ASP.NET minimal API in `SqlHarness.Dashboard`), xUnit; React + TypeScript, TanStack Router, shadcn/Tailwind, Vitest.

**Spec:** [docs/superpowers/specs/2026-10-16-optimization-dashboard-design.md](../docs/superpowers/specs/2026-10-16-optimization-dashboard-design.md)

## Status and dependencies

- TODO. Planning base `4154fe7` (2026-10-16). Blocked until 077 and 079 are merged.
- Priority P1; effort L; risk MEDIUM (UI-wide changes; comparison semantics shown to reviewers).

## Global Constraints

- Better/worse per case by logical reads (data + LOB): `better` when the candidate median drops by ≥ 1% **and** ≥ 10 pages; `worse` when it rises by the same margins; else `same`; differing results ⇒ `results differ` regardless of reads. Worst case = largest relative logical-reads increase. Elapsed verdict (policy v2) shown separately, never combined.
- Numbers are put side by side only within one conditions group (operation-level fingerprint); otherwise show "conditions differ" and the SQL diff only.
- Default A = earliest version measured in the group; default B = newest version linked to A by a chain of equivalent-result compares in the group; never a version with differing, off, incomplete or inconclusive equivalence.
- Equivalence wording: `results equivalent (direct)`, `results equivalent via v2`, `results not compared`, `results differ`.
- Aggregate toggle: `Sum` (default) and `Mean` (mean of per-case medians); labelled; computed only over cases present on both sides, labelled `n of N cases` when partial.
- Copy Markdown = exactly the panel's table (selected case or aggregate, labelled), with scans and grouped temp objects, same wording as the screen.
- Optimization section appears only for sessions with at least one compare; other sessions keep their views.
- Version identity stays per (session, subject, scope); cards group by subject + database; `whole query` first, fragment scopes nested.
- Engines without per-table IO: show available metrics; per-table table replaced by `per-table IO not available for this engine`.
- Compares without subject/scope: listed as `Unassigned compares` with links; count for the "contains compare" filter, not for the Optimization column.
- over-granted only when granted ≥ 1 MB and used/granted < 25%.
- Naming: Cell → Case; `whole` → `whole query`; nav `Statistics` → `Usage`; "policy v2" in a tooltip.
- No auto-formatting; match local style. Commits `feat(080): …`, one short line. Completion requires `just verify`.

## Review Focus

1. A compare whose baseline and candidate have the same SQL hash → one version row; panel shows `same SQL` instead of a 0% table → Task 2 `Identical_versions_show_same_sql`.
2. A version measured under two conditions groups appears in both groups and its numbers never mix → Task 1 `Version_in_two_groups_is_listed_in_each`.
3. A partial matrix run (plan 076) with fewer completed cases → aggregate over common cases labelled `2 of 3 cases` → Task 2 `Partial_cases_aggregate_over_common_cases`.
4. Baseline reads of 0 → change shown as `+5 pages`, never `Infinity%`/`NaN` → Task 2 `Zero_baseline_reads_show_absolute_change`.
5. A session with 40 versions and long subject names stays usable: defaults computed, table renders, pickers work → Task 4 test `renders forty versions`.

---

### Task 0: Drift check and baseline

- [ ] **Step 1:** Confirm 077 and 079 are merged; run the drift check; record the base commit here.
- [ ] **Step 2:** `just verify` on the base; record the result.

### Task 1: Optimization summary builder

**Files:**
- Create: `src/SqlHarness.Dashboard/Optimization/OptimizationSummaryBuilder.cs`, `src/SqlHarness.Dashboard/Optimization/OptimizationModels.cs`
- Modify: `src/SqlHarness.Dashboard/JournalReader.cs` (query per-session compare evidence: subject, scope, database, operation fingerprint, baseline/candidate hashes, per-case medians and per-table rows from 079's journal, equivalence, regression verdict, status)
- Test: create `tests/SqlHarness.Tests/Dashboard/OptimizationSummaryBuilderTests.cs`; extend `JournalSeed.cs`

**Interfaces:**
- Consumes: 079 journal rows (conditions fingerprint, case id/label, per-table logical/LOB/scans, `table variables` group).
- Produces:
  - `sealed record OptimizationSection(IReadOnlyList<OptimizationSubject> Subjects, IReadOnlyList<UnassignedCompare> Unassigned)`
  - `sealed record OptimizationSubject(string Subject, string Database, string Engine, IReadOnlyList<ConditionsGroup> Groups)`
  - `sealed record ConditionsGroup(string Fingerprint, string Label /* "3 cases: @CustomerId = 1, 42, 9001" */, IReadOnlyList<ScopeVersions> Scopes, string DefaultA, string? DefaultB)`
  - `sealed record ScopeVersions(string Scope /* "whole query" or fragment */, IReadOnlyList<VersionRow> Versions)`
  - `sealed record VersionRow(string Version, string Hash, long? LogicalReadsSum, long? LogicalReadsMean, long? ScansSum, double? ElapsedMedianMs, string CaseOutcome /* "3/3 ok" | "2 better · 1 worse" | "results differ" | "equivalence off" | "incomplete" */, IReadOnlyList<long> MeasuredIn, long LatestOperationId)`
  - `sealed record UnassignedCompare(long OperationId, string StartedAt, string Status)`
  - `static OptimizationSection OptimizationSummaryBuilder.Build(IReadOnlyList<CompareEvidence> compares)`; `CompareEvidence` is the reader's row record defined in `OptimizationModels.cs`.

- [ ] **Step 1: Write failing tests:**
  - `Groups_by_subject_database_and_fingerprint` — two compares same subject, two databases → two subjects; same database, two fingerprints → two groups.
  - `Version_in_two_groups_is_listed_in_each` — v1 measured under fingerprints F1 and F2 appears in both groups with that group's numbers only.
  - `Default_pair_follows_equivalence_chain` — compares v0→v1 (equivalent), v1→v2 (equivalent), v2→v3 (results differ) → DefaultA `v0`, DefaultB `v2`.
  - `Equivalence_off_is_never_default_b` — only compare v0→v1 with comparison off → DefaultB null.
  - `Newest_measurement_wins_per_version` — v0 measured in #201 and #214 → row uses #214, `MeasuredIn = [201, 214]`.
  - `Whole_query_first_fragments_after`.
  - `Unassigned_compares_are_listed`.
- [ ] **Step 2:** `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~OptimizationSummaryBuilder"` → FAIL.
- [ ] **Step 3:** Implement. Case outcome per version uses the version's newest measurement against its baseline in that compare, with the Global Constraints rule.
- [ ] **Step 4:** Run → PASS. **Step 5:** Commit `feat(080): build session optimization summary`.

### Task 2: Two-version comparison

**Files:**
- Create: `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs`
- Modify: `OptimizationModels.cs`
- Test: create `tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs`

**Interfaces:**
- Consumes: Task 1 models and `CompareEvidence`.
- Produces:
  - `enum PairBasis { MeasuredTogether, DifferentRunsSameConditions, ConditionsDiffer, SameSql }`
  - `sealed record OptimizationComparison(string A, string B, PairBasis Basis, long? OperationId, string EquivalenceLabel, IReadOnlyList<CaseOption> Cases, string SelectedCase /* "all" or case id */, string Aggregate /* "sum" | "mean" */, string CoverageLabel /* "" or "2 of 3 cases" */, MixedWarning? Mixed, IReadOnlyList<TableRow> Tables, CollapsedRow? Collapsed, ElapsedInfo Elapsed)`
  - `sealed record CaseOption(string Id, string Label, string Outcome /* better|worse|same|results differ */)`
  - `sealed record MixedWarning(int Better, int Worse, string WorstCaseLabel, double WorstChangePercent)`
  - `sealed record TableRow(string Table, long? ScansBefore, long? ScansAfter, long? ReadsBefore, long? ReadsAfter, string Change /* "−95%" | "+5 pages" | "—" */)`
  - `sealed record CollapsedRow(int Count, string Label /* "table variables / worktables (3, 0 reads)" */)`
  - `sealed record ElapsedInfo(double? BeforeMs, double? AfterMs, string? PolicyVerdict, bool DifferentRun)`
  - `static OptimizationComparison OptimizationComparer.Compare(ConditionsGroup group, IReadOnlyList<CompareEvidence> compares, string a, string b, string selectedCase, string aggregate)`

- [ ] **Step 1: Write failing tests:**
  - `Measured_together_uses_that_operation` — pair present in #214 → Basis `MeasuredTogether`, OperationId 214.
  - `Different_runs_same_conditions` — v1 from #201 and v3 from #214, same fingerprint → `DifferentRunsSameConditions`, `Elapsed.DifferentRun == true`.
  - `Conditions_differ_returns_no_numbers` — Tables empty, Basis `ConditionsDiffer`.
  - `Identical_versions_show_same_sql`.
  - `Case_outcome_thresholds` — 1,000 → 985 pages (−1.5%, 15 pages) `better`; 1,000 → 995 (−0.5%) `same`; 500 → 505 (+1%, 5 pages) `same`; results differ overrides.
  - `Mixed_names_worst_case` — cases −95%, −60%, +35% → Mixed(2,1,"@CustomerId = 9001",35).
  - `Sum_and_mean_aggregates` — sum of per-case medians; mean = sum / case count; labels.
  - `Partial_cases_aggregate_over_common_cases` → CoverageLabel `2 of 3 cases`.
  - `Zero_baseline_reads_show_absolute_change` → `+5 pages`.
  - `Temp_objects_collapse` — `table variables`, `Worktable`, `Workfile` and zero-read rows go into `Collapsed`; real tables sorted by absolute reads change descending.
  - `Equivalence_labels` — direct, `via v2`, not compared.
  - `Engine_without_table_io_returns_message` — PostgreSQL evidence → Tables empty and a `TableIoUnavailable` flag (add `bool TableIoUnavailable` to the record).
- [ ] **Step 2:** `--filter "FullyQualifiedName~OptimizationComparer"` → FAIL. **Step 3:** Implement. **Step 4:** → PASS.
- [ ] **Step 5:** Commit `feat(080): compare two optimization versions`.

### Task 3: API and session list data

**Files:**
- Modify: `src/SqlHarness.Dashboard/DashboardServer.cs:~210-224`, `DashboardModels.cs:35` (`SessionSummary`), `JournalReader.cs` (session list, operations list, over-granted at `:44`)
- Test: `tests/SqlHarness.Tests/Dashboard/DashboardServerTests.cs`, `JournalReaderTests.cs`, `DashboardContractTests.cs`

**Interfaces:**
- Produces:
  - `GET /api/sessions/{id}/optimization` → `OptimizationSection`.
  - `GET /api/sessions/{id}/optimization/compare?subject=&database=&group=&a=&b=&case=all&aggregate=sum` → `OptimizationComparison`; 400 for unknown aggregate; 404 for unknown session/subject/group/version.
  - `SessionSummary` gains `bool HasCompare` and `IReadOnlyList<SessionOptimizationBadge> Optimization` (`Subject`, `Change` e.g. `−94%`, `bool Warning`); `GET /api/sessions` accepts `hasCompare=true`.
  - Operation summaries gain `string? Subject`, `string? VersionPair` (`v0 → v3`), `long? ReadsBefore`, `long? ReadsAfter`.
  - over-granted SQL: `m.grant_granted_kb >= 1024 AND m.grant_max_used_kb * 4 < m.grant_granted_kb`.

- [ ] **Step 1:** Failing tests: endpoint shapes and status codes; `Sessions_filter_has_compare`; `Session_list_optimization_badge_uses_default_pair` (equals Task 2 result for default A/B, `Warning` when Mixed or not equivalent); `Over_granted_requires_one_megabyte` (granted 512 KB, used 10 KB → false; granted 4096, used 100 → true); operation summary fields for a compare.
- [ ] **Step 2:** → FAIL. **Step 3:** Implement (compute badges only for the returned page). **Step 4:** → PASS.
- [ ] **Step 5:** Commit `feat(080): serve optimization section and list data`.

### Task 4: Session Optimization section (UI)

**Files:**
- Create: `ui/src/components/optimization/OptimizationSection.tsx`, `VersionTable.tsx`, `ComparisonPanel.tsx`, `ui/src/lib/optimizationMarkdown.ts`
- Modify: `ui/src/pages/SessionPage.tsx` (section first when `HasCompare`; remove `CompareSessionOverview` usage), `ui/src/api/types.ts`, `ui/src/api/queries.ts`, `ui/src/router.tsx` (session search params `subject`, `db`, `group`, `a`, `b`, `case`, `agg`)
- Delete: `ui/src/components/CompareSessionOverview.tsx` and its test when no longer referenced (keep `SessionVersionDiff` as the "SQL diff" action)
- Test: create `OptimizationSection.test.tsx`, `ComparisonPanel.test.tsx`, `lib/optimizationMarkdown.test.ts`; update `SessionPage.test.tsx`

**Interfaces:**
- Consumes: Task 3 endpoints; 079 `ReproScriptButtons`.
- Produces: `<ComparisonPanel comparison={OptimizationComparison} sessionId={number} />` (reused by Task 5); `optimizationMarkdown(c: OptimizationComparison, subject: string, scope: string): string`.

- [ ] **Step 1: Failing tests:**
  - section absent for a session without compares; present and first otherwise; unassigned compares listed with links.
  - version table: rows, `whole query` first, outcome text, A/B radios default to `DefaultA`/`DefaultB`; group selector shown only with >1 group; `renders forty versions` (40 rows, defaults selected, picker change updates URL `a`/`b`).
  - panel: basis badge text (`measured together in #214` / `different runs, same conditions` / `conditions differ`), case chips with labels, Sum/Mean toggle updates URL `agg`, Mixed warning text `Mixed: 2 better, 1 worse — @CustomerId = 9001: logical reads +35%`, collapsed row text, `per-table IO not available for this engine`.
  - actions: copy summary uses `optimizationMarkdown`; script buttons for both versions; links to SQL diff, plans, operation.
  - `optimizationMarkdown` produces a header line (subject, scope, `v0 → v3`, case or aggregate label, basis, equivalence label) and a table `| Table | Scans before | Scans after | Logical reads before | Logical reads after | Change |` matching the panel rows, plus the collapsed row and Mixed line.
- [ ] **Step 2:** `npm --prefix src/SqlHarness.Dashboard/ui test` → FAIL. **Step 3:** Implement with existing shadcn components. **Step 4:** → PASS.
- [ ] **Step 5:** Commit `feat(080): session optimization section`.

### Task 5: Compare-first operation page

**Files:**
- Modify: `ui/src/pages/OperationPage.tsx:60,191,215`, `ui/src/components/CompareDetail.tsx`, `ui/src/components/VariantPanel.tsx`
- Test: `OperationPage.test.tsx`, `CompareDetail.test.tsx`

**Interfaces:**
- Consumes: `ComparisonPanel`; `GET /api/sessions/{sid}/optimization/compare` with the operation's pair (or an equivalent operation-scoped call the builder adds to Task 3 if simpler).

- [ ] **Step 1: Failing tests:** compare operation renders a one-line header (subject, `whole query`, `v0 → v3`, database, time, status, back link with the session title) followed by `ComparisonPanel`; sections `SQL diff & conditions`, `Plans`, `Per-case & per-run details`, `Run diagnostics` are collapsed by default; token/bytes, phase timings, agent call and connection events appear only inside `Run diagnostics`; per-run details show one case and side at a time (selector), not a four-column grid; variable chips render once; measure operations show result first with diagnostics collapsed.
- [ ] **Step 2:** → FAIL. **Step 3:** Implement. **Step 4:** → PASS.
- [ ] **Step 5:** Commit `feat(080): compare-first operation page`.

### Task 6: Lists, badges, Live and naming

**Files:**
- Modify: `ui/src/components/SessionTable.tsx`, `ui/src/pages/SessionsPage.tsx`, `ui/src/components/OperationTable.tsx:10-24`, `ui/src/components/FlagBadges.tsx`, `ui/src/lib/flags.ts`, `ui/src/components/StatusBadge.tsx`, `ui/src/pages/LivePage.tsx`, `ui/src/components/AppLayout.tsx:18`, `ui/src/lib/compare.ts:80-107` (labels), `ui/src/pages/StatsPage.tsx` (title only)
- Test: corresponding `*.test.tsx`/`*.test.ts`

**Interfaces:**
- Produces: `badgeTier(kind): "outcome" | "warning" | "info"`; `OperationTable` prop `showInternal: boolean` persisted under localStorage key `sqlharness.showInternalOperations` (wrapped in try/catch).

- [ ] **Step 1: Failing tests:**
  - sessions: Optimization column `orders-by-customer −94%` with warning icon when `Warning`; "contains compare" filter sets `hasCompare=true`; working directory shows last segment with full path in `title`; state badges not clipped (column has no fixed width truncation).
  - operations: `artifact` rows hidden by default, toggle shows them and persists; session/subject column; agent single line with model in `title`; compare row shows `v0 → v3` and `48 210 → 3 115`.
  - badges: `succeeded`/`failed`/`rejected` outcome tier; regression/results differ/Mixed warning tier; info items render as muted text, not `Badge`.
  - Live: empty Running and Active sessions render as one line `Nothing running · no active sessions`.
  - naming: no visible `Cell `; `whole` rendered `whole query`; "No assessment" has an explanation tooltip; "policy v2" only in a tooltip; nav label `Usage`.
- [ ] **Step 2:** → FAIL. **Step 3:** Implement. **Step 4:** → PASS; `npm --prefix src/SqlHarness.Dashboard/ui run build`.
- [ ] **Step 5:** Commit `feat(080): de-noise lists, badges and naming`.

### Task 7: Documentation and verification

- [ ] **Step 1:** Update README dashboard section (Optimization section, Usage rename, artifact toggle); `plans/README.md` status.
- [ ] **Step 2:** `just verify` → exit 0; record revision and result here.
- [ ] **Step 3:** Live check against the running dashboard with the owner's journal (headless screenshots of a session with compares and a compare operation); record observations without business data.
- [ ] **Step 4:** Move the queue entry to history with evidence; commit `docs(080): record plan 080 evidence`.
