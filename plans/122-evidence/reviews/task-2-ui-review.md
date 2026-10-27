### Spec Compliance

- ❌ Issues found: All cases explanation is rendered only inside an expanded statement row, so an empty statement list or `tableIoUnavailable` suppresses it entirely (src/SqlHarness.Dashboard/ui/src/components/optimization/IoComparison.tsx:37,45). The requirement calls for an explicit explanation in All cases; render the API reason independently of individual rows and cover an empty list.
- ⚠️ Cannot verify from this UI diff: backend eligibility, old/new session boundary, selection provenance, completeness and preservation of exact links; these depend on backend work reviewed separately. Full UI suite was interrupted in the implementer report, and a clean run was pending.

### Strengths

- Nullable timings and LOB values retain zero and display absence as an em dash; existing plan links and batch total remain in place (src/SqlHarness.Dashboard/ui/src/components/optimization/StatementRows.tsx:24,39; src/SqlHarness.Dashboard/ui/src/components/optimization/IoComparison.tsx:44,48).
- Selected-run provenance and repetition are shown alongside statement timings, with focused tests for values, links and LOB (src/SqlHarness.Dashboard/ui/src/components/optimization/StatementRows.tsx:27; src/SqlHarness.Dashboard/ui/src/components/optimization/IoComparison.test.tsx:30).

### Issues

#### Important

- src/SqlHarness.Dashboard/ui/src/components/optimization/IoComparison.tsx:37,45 — All cases reason disappears when there are no statement rows or per-table IO is unavailable, and when every row is collapsed. This leaves the required unavailability unexplained. Present it outside the row expansion and test the empty-row case.

#### Minor

- src/SqlHarness.Dashboard/ui/src/components/optimization/StatementRows.test.tsx:61 — The LOB test checks only a column heading, not the zero/null cell values; asserting both would protect the stated null-versus-zero contract.

### Assessment

**Task quality: Needs fixes.** The implementation preserves source links and missing values, but the required All cases explanation is conditional on the statement rows and their expansion.

Focused outside-diff checks: searched StatementRows and IoComparison call sites to assess whether the explanation reaches other displays; StatementRows is used by OperationPage for measured operation details, while IoDetails supplies comparison to IoComparison. Checked OptimizationModels.cs field names/types for UI/API mismatch; the nullable timing, repetition, LOB and reason names match. No tests run by reviewer; full UI suite result pending controller.
