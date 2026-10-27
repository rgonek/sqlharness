### Spec Compliance

- ❌ Issues found: benchmark Rows remains in the expandable heading Details for measure operations (`src/SqlHarness.Dashboard/ui/src/pages/OperationPage.tsx:191`, `:200`), contrary to the global removal requirement. The new assertion checks only Operation metrics (`src/SqlHarness.Dashboard/ui/src/pages/OperationPage.test.tsx:110`), so misses it.
- ⚠️ Cannot verify from diff: desktop/mobile browser behavior, full Windows gate, and preservation of all evidence warnings across the application; controller should complete the planned browser and branch-level checks. Focused tests and typecheck are implementer-reported, not rerun.

### Strengths

- `src/SqlHarness.Dashboard/ui/src/components/optimization/IoComparison.tsx:27` filters both options and rendered cells immediately and prunes unsupported selections after scope changes; the test exercises round-trip switching and an actual batch value (`IoComparison.test.tsx:82`).
- `src/SqlHarness.Dashboard/ui/src/pages/OperationPage.tsx:231` preserves query Rows and provider Rows affected, including unavailable values; the query test checks the recorded value (`OperationPage.test.tsx:503`).
- Named risk check: searched `OperationPage.tsx` and its tests for Rows, Columns, Estimated, Results, and warnings; confirmed the existing query results non-retention message at `OperationPage.tsx:287` and discovered the remaining benchmark Rows at `:200`.

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- `src/SqlHarness.Dashboard/ui/src/pages/OperationPage.tsx:200`: Rows is rendered unconditionally in heading Details, including measure/benchmark operations, so users still see the unsupported field after opening Details. Restrict this fact to operations with a suitable rows contract, and extend the measure test to inspect heading Details while preserving query Rows.

#### Minor (Nice to Have)

- `.superpowers/sdd/122-dashboard-data-value-new-sessions/task-6-report.md:14`: reported `git diff --check` included an LF-to-CRLF warning; investigate or document the line-ending configuration so verification output is clean.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The scope-switch implementation preserves supported batch data and correctly hides unsupported statement options, but the global benchmark field removal is incomplete and its focused test misses the remaining display.
