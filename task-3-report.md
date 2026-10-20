# Task 3 implementation report

Implemented Plan 100 Task 3 on `plan-100` from HEAD `7164958da9c1b324b78b3ad52ba4a777224a849d`.

## Changes

- Replaced CompareDetail's three native disclosures with a single Base UI Accordion in `multiple` mode. Items use stable values (`sql-diff`, `plans`, `case-details`), start closed, retain the existing summary text, and use `h2` headings appropriate to the page hierarchy. Accordion panels keep descendants mounted.
- Replaced the requested OperationPage disclosures, Session metadata, Table IO More, Optimization Unassigned, StatementRows Objects, and mapped profile dimension disclosures with independent Base UI Collapsibles. Every panel uses the installed primitive's `keepMounted` API. Profile dimensions remain keyed by dimension name and default open without shared controlled state.
- Reworked structural tests into trigger, expanded, and hidden-content interactions. Added coverage for independently open compare sections, retained nested More/Objects and SQL tab state across reopening, and keyed dimension add/remove behavior.
- No backend, SQL, or live database writes. Temporary browser fixture files were removed after checks.

## Verification

Commands were run in `src/SqlHarness.Dashboard/ui` unless noted:

- Red phase: target Vitest files initially failed the new interaction expectations (21 failed, 54 passed); raw output is `scratch/task-3/red-tests.log`.
- `npm exec -- vitest run src/components/CompareDetail.test.tsx src/components/optimization/OptimizationSection.test.tsx src/components/optimization/StatementRows.test.tsx src/pages/OperationPage.test.tsx src/pages/SessionPage.test.tsx src/pages/StatsPage.test.tsx` — passed, 6 files / 75 tests. Raw output: `scratch/task-3/focused-tests.log`.
- `npm run typecheck` — exit 0. Raw output: `scratch/task-3/typecheck.log`.
- `npm run lint` — exit 0. Raw output: `scratch/task-3/lint.log`.
- `npm test` — exit 0, 62 files / 335 tests. Raw output: `scratch/task-3/full-ui-tests.log`. Test output included non-failing `ECONNREFUSED` diagnostics to localhost:3000 from unrelated UI tests; no tests failed.
- `git diff --check` — passed.

## Browser checks

A disposable fixture rendered the real CompareDetail with local fixture data in cached Chromium. The browser evidence and raw observations are in `scratch/task-3/browser-checks.log`; screenshots are `scratch/task-3/compare-open.png` and `scratch/task-3/compare-styled.png`.

All three compare panels were initially closed and could remain open together. Keyboard Tab moved from the first disclosure trigger to Plans; Shift+Tab then Enter activated SQL diff. Nested More toggled without closing Plans and retained its open state after Plans was closed and reopened. Reduced-motion emulation was active. After revealing SQL diff, the two visible Monaco panes measured 455x512 at a 1280x900 viewport and rendered side by side; the inactive Monaco instance remained display:none at 0x0.

## Files changed

- `src/SqlHarness.Dashboard/ui/src/components/CompareDetail.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/CompareDetail.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/TableIoComparison.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/optimization/OptimizationSection.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/optimization/OptimizationSection.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/optimization/StatementRows.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/optimization/StatementRows.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/ui/accordion.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/OperationPage.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/OperationPage.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/ProfileDimensionsPanel.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/SessionPage.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/SessionPage.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/StatsPage.test.tsx`

## Concerns

The full UI suite passed, though unrelated tests emitted connection-refused diagnostics against localhost:3000. A working service was not started because this task has no backend scope.
