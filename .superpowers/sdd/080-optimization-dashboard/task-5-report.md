# Task 5 report: Compare-first operation page

## Result

Compare operations now lead with a compact subject, scope, version pair, database, timestamp, status, and session-title back link, followed by the operation-scoped `ComparisonPanel`. SQL/conditions, plans, and per-case/per-run details use collapsed disclosures. The case selector and baseline/candidate side selector show one actual operation variant at a time; plan links and reproduction calls use that variant's stored operation, side, and case references. Run diagnostics keep output bytes/tokens, phase timings, agent metadata, and connection events collapsed.

Measure and other non-compare operation pages now place measured result panels before their collapsed Run diagnostics disclosure. Variables render once on those pages and compare pages. The existing `return*` session search fields survive case changes and return navigation. Numeric case IDs are normalized to strings by route validation because the router parses numeric query values as numbers.

## TDD evidence

- RED command: `npm --prefix src/SqlHarness.Dashboard/ui test -- src/pages/OperationPage.test.tsx`
- RED result: 17 tests passed and the new compare-first test failed on the existing `Operation #10` heading; it expected the compare subject.
- GREEN command (from `src/SqlHarness.Dashboard/ui`): `vitest run src/pages/OperationPage.test.tsx src/components/CompareDetail.test.tsx --reporter=dot`
- GREEN result: 2 files passed, 22 tests passed.

## Verification

- `npm run typecheck` — passed.
- `npm run lint` — passed after moving the case-detail state hook above its empty-state return.
- `npm run build` — passed; Vite reported the existing large Monaco chunk warning.
- `git diff --check` — passed.
- The complete UI suite was not repeated, per task instructions; Task 4 recorded a prior full run with 274 tests passing.
- No browser check or screenshot was attempted; the controller waived that check and owns any visual review.

## Files changed

- `OperationPage.tsx` and focused page tests.
- `CompareDetail.tsx` and focused detail tests.
- `ComparisonPanel.tsx` supports hiding its case controls when the operation detail selector owns that selection.
- `router.tsx` validates operation `case` and `agg` search values and preserves numeric case IDs.
- Plan 080 checklist/index updated.

## Self-review and concerns

The compare header links back using the session title, falling back to the existing session key when no title exists. Operation-scoped comparisons without complete retained pairs show a concise empty state in the detail selector. No API gaps remain. The visual browser check remains with the controller as directed.

Commit: `feat(080): compare-first operation page` (see branch HEAD).
