# Task 7 evidence — Plan100 pickers

Worktree: `C:\Users\rgone\MultiCliProfiles\codex\account-b\worktrees\cf54\sqlharness`
Branch: `plan-100`
Base before Task 7: `a2a27d7818f568e488871b1e774233ccab42bab0`

## Implementation

Migrated the occurrence, case, repetition, plan-document, and statement pickers to the installed Base UI Select API. Initial and selected trigger text uses human labels while callbacks and controlled values retain their existing string keys and numeric identities. The four PlanWorkbench handlers remain distinct, including their different downstream document/statement, selected-node, SQL-selection, and reveal behavior.

Focused coverage selects a node and SQL span through the rendered viewer callbacks before changing case/repetition, checks conditional/empty plan options, changed options, initial and updated labels, repeated statement index 0 across two documents, and document selection persistence after closing and reopening the CompareDetail `Plans` accordion. OperationPage's existing compare-case path was adapted to interact with the Select popup.

## Verification

TDD red evidence: before implementation, the new PlanWorkbench test selected a node through the viewer and then changed case; it failed because the selected-node key remained instead of being cleared (`PlanWorkbench.test.tsx`, expectation “No operator selected”). A preliminary fixture mistake returning a plain object from `stubFetch` was corrected; the recorded red was the behavior assertion.

Final focused command from `src/SqlHarness.Dashboard/ui`:

```text
npm exec -- vitest run src/components/PlanChanges.test.tsx src/components/plan/PlanWorkbench.test.tsx src/components/CompareDetail.test.tsx src/pages/OperationPage.test.tsx
Test Files  4 passed (4)
Tests       42 passed (42)
Exit code   0
```

Also from the UI root:

```text
npm run typecheck  -> exit 0
npm run lint       -> exit 0
```

`git diff --check` passed. These are focused checks, not the Plan 100 final UI suite or repository `just verify` gate.

## Browser observation

Used the disposable fixture and cached Chromium 1228 through Playwright Core at `D:\Dev\CivicLens\frontend\node_modules\playwright-core\index.mjs`. Fixture and logs are retained under `../task-artifacts/task-7/`; it renders the actual PlanWorkbench with intercepted fixture data and makes no backend or database writes.

The first run exposed a long case label wider than the popup (desktop popup 512px vs 658px content; 320px viewport popup 256px vs 658px content). After constraining the case popup to the viewport and allowing option text wrapping, the final browser check passed: desktop popup width/client/scroll width `690.45/690/690px`; at 320px viewport popup `288/288/288px`, with option content wrapping to 288px. Screenshot: ``; final measurements/log: ``.

## Scope and review

Reviewed the task diff locally, including distinct callbacks, retained domain values, labels, tests, plan index, and `git diff --check`. Parent disclosure reopen coverage is in the relevant CompareDetail integration fixture. This task did not run a separate independent reviewer, the full UI suite, or the final repository gate; those remain for the plan's integration task. No browser-console claim is made from this fixture. No backend, database, merge, or push action was performed.

Commit: `d3dbd543654664430fc4e29b7b594e4707fc73da` (`Adopt Base UI selects for plan pickers`). The report, browser fixture, measurements and screenshot are ignored scratch artifacts and were not added to the commit.
