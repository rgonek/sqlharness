# Task 9 — Standard Button actions

Worktree: `C:\Users\rgone\MultiCliProfiles\codex\account-b\worktrees\cf54\sqlharness`
Branch/base: `plan-100` at `92370a67b856f87d693bfb4bf72badb3e925f0a9`

## Changes

Adopted the existing Base UI `Button` for the seven standard action controls: named-operation retry and load-more, graph fit-selection, subtree expand/collapse, plan-error retry, ambiguous source-match selection, and profile-matrix cell selection. Kept explicit `type="button"`, disabled/pending states, accessible labels and pressed state, callbacks, titles, and event propagation behavior. The source-match action allows its content to wrap. Matrix actions fill their cells, retain end alignment, and use tabular numerals.

The multi-line React Flow operator card remains a native button. Its JSX comment records the exception: the card is a dense graph surface whose layout and React Flow keyboard/event handling are domain behavior.

No tests were added for mechanical substitutions. Existing pagination/retry, matrix selection, PlanWorkbench, compare-plan and OperationPage behavior tests were reused.

## Verification

From `src/SqlHarness.Dashboard/ui`:

```text
npm exec -- vitest run src/pages/NamedOperationsPage.test.tsx src/pages/StatsPage.test.tsx src/components/plan/PlanGraphView.test.tsx src/components/plan/PlanWorkbench.test.tsx src/components/PlanChanges.test.tsx src/components/CompareDetail.test.tsx src/pages/OperationPage.test.tsx
Test Files  7 passed (7)
Tests       58 passed (58)
Exit code   0

npm run typecheck -> exit 0
npm run lint      -> exit 0
```

These are scoped UI checks, not the whole UI suite or repository `just verify` gate.

Browser fixture: ``, rendered through Vite with cached Chromium 1228 and Playwright Core at `D:/Dev/CivicLens/frontend/node_modules/playwright-core/index.mjs`. The browser check passed actual React Flow pointer and keyboard operator selection, fit-selection disabled before selection and enabled afterward, subtree collapse/expand, source-match keyboard activation, source-match wrapping at a 320px viewport (262px button width, 70px height, `white-space: normal`), end-aligned numeric matrix cell content, and matrix scrolling contained in its region. No page errors or console errors were observed. Screenshot and raw output are `` and ``.

The fixture placed the properties panel after the graph canvas; React Flow's pane intercepted pointer input at the panel location, so the source-match action was checked with native Enter activation. Graph pointer selection was checked independently. The fixture's outer page width includes the intentionally scrollable graph/matrix content and a debug selection `<output>`; its measured page scroll width is not a product layout claim.

## Scope

`git diff --check` passed. No backend, database, merge, push, or full repository gate was run. Task 10 and Plan 100 integration verification remain outstanding.
