# Task 10 — Ordinary table-family adoption

Worktree: `C:\Users\rgone\MultiCliProfiles\codex\account-b\worktrees\cf54\sqlharness`
Branch/base: `plan-100`, commit `bbd3d60` based on `ec8493c1b6c6a36b71b21d789a128cda36058eee`

## Changes

Converted seven table surfaces to the existing `Table`, `TableHeader`, `TableBody`, `TableRow`, `TableHead`, and `TableCell` primitives: operation phase timings, transcript matching coverage, optimization versions, statement IO, nested object IO, comparison object IO, and approval parameters. Removed hand-added horizontal overflow wrappers where `Table` already supplies its own container. Kept row headers in tbody, column headers, comparison `colSpan`, unknown/null metrics, links, all parameter values, and the existing SQL wrapping. Long SQL and parameter text wraps and remains selectable.

VersionTable keeps its two independent name-based native radio groups, including keyboard arrow selection across each group. The profile-dimension matrix remains native with its sticky row headers and separate borders. The approval dialog retains its existing buttons and policy timing; tests use fixture data, confirm Approve is initially disabled and Deny is presented, and assert no decision endpoint call. No approval was submitted and no live target was used.

## Verification

From `src/SqlHarness.Dashboard/ui`:

```text
npm exec -- vitest run src/pages/OperationPage.test.tsx src/pages/SettingsPage.test.tsx src/components/optimization/OptimizationSection.test.tsx src/components/optimization/StatementRows.test.tsx src/components/optimization/ComparisonPanel.test.tsx src/mutations/Approval.test.tsx
Test Files  6 passed (6)
Tests       75 passed (75)
Exit code   0

npm run typecheck
> tsc -b
Exit code 0

npm run lint
> oxlint
Exit code 0
```

`git diff --check` passed. One earlier focused run reported 73/75 because the table container had replaced the Time breakdown region. Restoring the named non-scrolling region wrapper fixed both affected OperationPage tests; that suite passed 28/28 and the final named set passed 75/75.

Browser fixture used the running Vite dev server, Playwright Core at `D:/Dev/CivicLens/frontend/node_modules/playwright-core/index.mjs`, and cached Chromium 1228. Command: `node ` (exit 0). The browser confirmed eight headers and a tbody row header; the matrix had local horizontal overflow and its sticky row header stayed at x=17 after scrolling 600px; the approval dialog fit within desktop and 320px viewports after layout settled; a long parameter wrapped to the available cell width and remained selectable; and there were no console or page errors. Screenshot: ``. Raw browser observations: ``.

Browser output:

```json
{
  "columnHeaders": 8,
  "rowHeader": true,
  "matrixSticky": {
    "before": { "left": "0px", "position": "sticky", "x": 17 },
    "after": { "x": 17, "scrollLeft": 600 }
  },
  "matrixContainedScroll": true,
  "approvalDialogContained": { "x": 448, "y": 45, "width": 384, "height": 810 },
  "longParameterWrappedAndSelectable": {
    "cellBox": { "x": 574.78125, "y": 353, "width": 241.21875, "height": 1497 },
    "selected": true
  },
  "narrowApproval": {
    "dialog": { "x": 0, "y": 20.9140625, "width": 320, "height": 758.171875 },
    "tableContainer": { "client": 288, "width": 288, "overflowX": "auto" }
  },
  "consoleErrors": []
}
```

The long parameter's content exceeds the dialog's visible height, so it is reachable through the dialog's existing vertical scroll. The task-level browser fixture verified layout and selection; the full Plan 100 browser matrix and `just verify` remain Task 11 work. No backend, database, merge, push, or live write was involved. Review was a self-review because this dispatched task explicitly disallowed subagents; the parent handles task/branch review.
