# Task 2 independent review

Spec verdict: CHANGES REQUESTED (1 P2 finding).
Quality verdict: CHANGES REQUESTED (same finding; no additional findings).

## Finding

- **P2 — Invisible stale dimension filter makes KPI/top lists disagree with dimension totals after changing range.** `src/SqlHarness.Dashboard/ui/src/pages/StatsPage.tsx:92` passes the entire retained `dimensionFilters` object to the now-effective server cohort predicate. Range changes retain this object. Meanwhile `ProfileDimensionsPanel.tsx:340` deliberately drops filter names absent from the current unfiltered dimension options. Reproduction: use an unconfigured recorded dimension present in older operations, select its value in All time, then switch to 24 hours where that dimension is absent but other operations exist. The dimension chooser no longer exposes the selected filter and the panel shows the newer operations, while the KPI/SQL query still requires the hidden old dimension and returns zero. This is newly observable because this task makes the aggregate predicates honor dimension filters. Normalize filters once against the current dimension options and share that effective set with both queries, or explicitly clear unavailable selections when the range/options change. Add a regression with a disappearing recorded dimension, asserting that KPI, SQL and dimension totals use the same cohort and no invisible predicate remains.

## Reviewed evidence

Read the task brief, screenshot spec, implementation report, full commit diff, shadcn skill, and relevant query/panel/workflow code. The SQL metric CTE collapses rows per operation before aggregation; all three rankings scan matching groups before LIMIT. Null, recorded zero, partial operation/metric coverage and representative-median semantics remain distinct. Added C#/TS fields are compatible with existing call sites/fixtures. ProfileOperations remains complete time-window chooser metadata intentionally. Workflow output sums matching measured calls, preserves producer-baseline lookup, and the displayed KPI excludes legacy volume. Detailed UI contents were removed without removing accounting contracts. No schema/collection/backfill changes found.

The reported focused .NET 11/UI 16 tests and typecheck/lint/build results were considered as supplied execution evidence; not rerun here. Independently ran `git diff --check 3c614588 abdacbe1` successfully. No full suite, source/index/HEAD changes, or live operations. Browser appearance/full Windows gate remain parent verification work.
