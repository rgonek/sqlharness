# Task 6 report — Optimization group and scope Selects

Worktree: `C:\Users\rgone\MultiCliProfiles\codex\account-b\worktrees\cf54\sqlharness`

Branch/base: `plan-100`, base `54435c01aa1270c233ccdd78bef2fd5d12b7f197`

## Changes

- Replaced the conditions-group and scope native selects in `OptimizationSection.tsx` with the generated Base UI Select and Label compositions. Both retain controlled values, the existing guards, dynamic subject-specific accessible names, and the original navigation callbacks.
- Added explicit value/label item mappings so closed triggers show group labels instead of fingerprints. Group changes still select the first scope under the existing ordering and clear `a`, `b`, `case`, `agg`, and `view`; scope changes retain the same reset path.
- Extended `OptimizationSection.test.tsx` to exercise scope switching, group switching/reset behavior, one-option hidden controls, subject-scoped names, and independent native A/B radio selection scoped to the versions table.
- Updated the existing `ComparisonPanel.test.tsx` SQL version checks to assert readable trigger labels (`v1`/`v2`); the previous `toHaveValue` expectation does not apply to Base UI Select buttons and was already failing at the task baseline.

## TDD evidence

The first focused run with the new tests failed as expected on native-select popup interactions and the reset assertions. It also exposed a pre-existing stale SQL diff assertion in `ComparisonPanel.test.tsx`. A later focused run found that the new group trigger initially displayed raw `fp-2`; the explicit `items` mapping fixed this and the display assertion then passed.

Raw logs are in `../task-artifacts/task-6/`: `red-focused.log`, `green-focused.log`, `focused-final.log`, `typecheck.log`, and `lint.log`.

## Final verification

Commands run from `src\SqlHarness.Dashboard\ui`:

- `npm exec -- vitest run src/components/optimization/OptimizationSection.test.tsx src/components/optimization/ComparisonPanel.test.tsx` — exit 0; 2 files passed, 20 tests passed.
- `npm run typecheck` — exit 0 (`tsc -b`).
- `npm run lint` — exit 0 (`oxlint`).
- `git diff --check` — exit 0.

Scope: focused task checks only. No full UI suite, `just verify`, browser walkthrough, backend, live database, merge, or push was run as part of this task.

## Review and concerns

Self-review of the explicit diff confirmed the existing group/scope selection and reset handlers remain intact, the one-option guards remain unchanged, and `VersionTable` remains untouched with native independent A/B radio groups. No remaining task-level concern identified. Independent review was not run under the task dispatch constraint prohibiting subagents.

Commit: `a2a27d7818f568e488871b1e774233ccab42bab0` (`Adopt Base UI selects for optimization group scope`).

The 	ask-done helper could not run in the WSL ash.exe environment because this Windows worktree .git pointer resolves to a Windows-only path there. Its final test command was run directly from the UI directory; the equivalent verification passed. The Plan 100 ledger was updated in PowerShell.
