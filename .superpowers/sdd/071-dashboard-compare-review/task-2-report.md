# Task 2 report: SQL tabs and copy icons

## Implementation

- Added `CopyButton` with clipboard confirmation, a two-second status, and a selected read-only manual-copy dialog fallback.
- Replaced the old inline SQL diff and “Show full SQL versions” toggle with remembered Baseline, Candidate, and Diff tabs; dialect-aware lazy viewers; side-by-side/unified controls; formatting preference; and unified diff copy.
- Kept the 256 KiB/4,000-line raw-input guard, bounded previews, and added a post-format guard so expanded SQL cannot reach the merge editor or unified-diff helper. Formatting failure falls back to both raw texts consistently.
- Passed `engine` from `CompareDetail` and removed the temporary `SqlEditors` side-effect import from `main.tsx`.
- Updated the OperationPage expectation for the replaced full-SQL toggle and covered formatter expansion.

## Files

- `src/SqlHarness.Dashboard/ui/src/components/CopyButton.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/CopyButton.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/SqlDiff.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/SqlDiff.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/CompareDetail.tsx`
- `src/SqlHarness.Dashboard/ui/src/main.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/OperationPage.test.tsx`

## RED

Command: `npm run test -- src/components/SqlDiff.test.tsx src/components/CopyButton.test.tsx`

Expected failures observed before implementation: the CopyButton test could not resolve the missing component; SQL tab selection, oversized-side tab selection, and Copy diff were absent. The first attempt to set `navigator.clipboard` with `Object.assign` also hit the DOM shim's read-only property; tests were corrected to define the clipboard property and rerun to the expected feature failures.

## GREEN

- `npm run test -- src/components/SqlDiff.test.tsx src/components/CopyButton.test.tsx src/components/CompareDetail.test.tsx src/pages/OperationPage.test.tsx` — 4 files passed, 25 tests passed.
- `npm run typecheck` — `tsc -b` exited 0.
- `git diff --check` — exited 0.

## Concerns

- Full `npm run check`, `npm run build`, `just verify`, and the Task 7 manual fixture check remain at the parent plan's completion gate; they were not repeated for this focused task.
- Existing unrelated modification `.superpowers/sdd/071-dashboard-compare-review/task-1-report.md` was left untouched and excluded from the Task 2 commit.
