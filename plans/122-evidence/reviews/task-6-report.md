# Task 6 report

Commit: 80d06c9b (Remove unsupported dashboard metrics). Base before task: 1c8c0d37.

Changed files: `src/SqlHarness.Dashboard/ui/src/components/optimization/IoComparison.tsx`, `IoComparison.test.tsx`, `src/SqlHarness.Dashboard/ui/src/pages/OperationPage.tsx`, `OperationPage.test.tsx`.

By statement now exposes only LOB logical among extra counters; unsupported selected counters are hidden immediately and cleared on scope change. Whole batch retains all counters and actual values. Benchmark Rows removed. Query Columns and mutation Estimated were already absent; query Rows and mutation Rows affected remain. No changes to components/ui, backend, evidence warnings or other UX.

Focused verification in `src/SqlHarness.Dashboard/ui`:
- Before implementation: `./node_modules/.bin/vitest.cmd run src/components/optimization/IoComparison.test.tsx --pool=threads --maxWorkers=1` failed the new scope-switch test on stale Physical column (8 passed, 1 failed).
- `./node_modules/.bin/vitest.cmd run src/components/optimization/IoComparison.test.tsx src/pages/OperationPage.test.tsx --pool=threads --maxWorkers=1`: 2 files, 44 tests passed.
- `npm.cmd run typecheck`: passed.
- `git diff --check`: passed; Git warned about LF to CRLF for the IoComparison test.

Self-review: confirmed filtering applies both to options and rendered columns, selection is pruned on view switch, actual batch values remain available, and query row count is retained. No blocking concerns found. Desktop/mobile browser inspection, full Windows gate and independent whole-branch review remain with controller; no live DB, install, merge or push performed. An initial attempted test append used an incorrect relative path and did not modify a file; corrected before red test.

## Fix 1/5: expanded heading Details

Commit `b30a11e1` (base `80d06c9b`). Restricted heading Details Rows to query operations; mutation provider Rows affected remains in operation metrics. Added checks on expanded measure Details and both query Rows displays.

In `src/SqlHarness.Dashboard/ui`, `./node_modules/.bin/vitest.cmd run src/pages/OperationPage.test.tsx --pool=threads --maxWorkers=1` first failed as expected (34 passed, 1 failed: Rows remained in measure Details), then passed (35/35) after the fix. `npm.cmd run typecheck` passed. `git diff --check` passed without output. No full suite or browser check performed; controller evidence files excluded.
