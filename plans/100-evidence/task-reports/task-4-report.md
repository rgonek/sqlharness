# Task 4 implementation report

Implemented Plan 100 Task 4 on branch `plan-100`, starting from `171c1b6e63c5e960e00a03b71077b6e9516225aa`.

## Changes

- Migrated the from/to version, case, side, approval sound, default mutation mode and statistics profile pickers to the generated Base UI Select composition.
- Kept version values as the existing JSON-encoded version/hash keys so queries still address the exact retained SQL. Added item label mappings so triggers show the retained label (or version) instead of the internal key. Empty version histories display “No versions available.”
- Preserved exact case IDs, side values, sound IDs, mutation modes, statistics profile keys and the conditional custom-sound option. Duplicate case labels now receive a case-ID-specific accessible option name while the selected trigger keeps the original visible label. A persisted custom-sound selection with a missing local audio blob remains readable as “Custom audio” while the unavailable custom option stays absent.
- Connected Settings and statistics labels to trigger IDs. The statistics label is now consistently “Statistics profile” in the visible label and accessible name.
- Reworked the migrated picker interactions to open the trigger and click the portal option. Tests check initial and updated labels plus downstream SQL hash requests, case callback, selected variant metrics, saved preferences, profile requests and profile-scope reset. Number-input assertions in SettingsPage tests remain covered.
- Marked Task 4 complete in `plans/100-shadcn-primitive-adoption.md`. Plan 100 remains in progress, so the plan index status was left unchanged.

## API review

Inspected the installed generated API at `src/SqlHarness.Dashboard/ui/node_modules/@base-ui/react/select/root/SelectRoot.d.ts`, `value/SelectValue.d.ts`, and `item/SelectItem.d.ts`. `Select.Root` accepts explicit `items`; controlled `value` can be `null`; `onValueChange` can return `null`; `Select.Value` supports a formatter and placeholder; and items support `aria-label`. The implementation uses this Base UI API through the generated `src/components/ui/select.tsx` exports.

## Verification

From `src/SqlHarness.Dashboard/ui`:

- `npm exec -- vitest run src/components/SessionVersionDiff.test.tsx src/components/CompareDetail.test.tsx src/pages/SettingsMutations.test.tsx src/pages/SettingsPage.test.tsx src/pages/StatsPage.test.tsx` — passed, 5 files / 50 tests. Output: ``.
- `npm run typecheck` — passed. Output: ``.
- `npm run lint` — passed. Output: ``.
- `git diff --check` — passed.
- Duplicate-label regression probe: without the case-ID accessible label, the new duplicate-label scenario failed to find a uniquely named case option. Restoring the label made the CompareDetail suite pass, including all 8 tests. Evidence: `` and ``.

## Browser checks

Used the cached Chromium executable at `C:/Users/rgone/AppData/Local/ms-playwright/chromium-1228/chrome-win64/chrome.exe` and `playwright-core` from `D:/Dev/CivicLens/frontend/node_modules/playwright-core/index.mjs`. The retained browser fixture renders the real `SessionVersionDiff` component and stubs its SQL-text API response.

- ArrowDown + Enter changed the from trigger to the v2 long label and requested its matching hash.
- Escape closed the to popup and returned focus to its trigger.
- Typeahead + Enter changed the from trigger to v3 and its matching hash.
- At 320 px wide, the document width remained 320 px; both triggers were 272 px wide and ended at x=296, with no horizontal overflow.
- Initial closed trigger text contained readable labels and no SHA-256 key.

The machine-readable observations are in ``; the viewport screenshot is ``. The temporary Vite fixture copies and server were removed after the check. Browser checks covered the shared Select implementation through the version picker; the other six pickers were covered by focused component tests. No backend, database, or live writes were used.

## Review and limits

Self-review covered all seven Select sites, exact selected values, accessible labeling, option order/conditional rendering, long-label sizing, empty options and test changes. No remaining Task 4 issue was found. This is scoped Task 4 verification; the full UI suite and repository `just verify` gate were not run because the task brief requests the focused suites and plan-wide integration is a later task.

## Files

- `plans/100-shadcn-primitive-adoption.md`
- `src/SqlHarness.Dashboard/ui/src/components/SessionVersionDiff.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/SessionVersionDiff.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/CompareDetail.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/CompareDetail.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/SettingsMutations.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/StatsPage.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/StatsPage.test.tsx`

## Review round 1 fixes

Review found that a compare can retain result cells after all complete baseline/candidate pairs disappear. The controlled Case Select now receives `null` when it has no selectable cases, so the configured “No cases available” placeholder is shown. A rerender regression test transitions from one complete option to no complete options while keeping the external selected case ID, then checks the placeholder and empty state.

The approval-sound test now also asserts that choosing “Glass drop” calls `saveSoundPreferences` with `selection: "glass-drop"`.

Commands run from `src/SqlHarness.Dashboard/ui` after the review fixes:

- `npm exec -- vitest run src/components/CompareDetail.test.tsx src/pages/SettingsMutations.test.tsx` — passed, 2 files / 16 tests. Output: ``.
- `npm run typecheck` — passed. Output: ``.
- `npm run lint` — passed. Output: ``.
- `git diff --check` — passed before commit.

SettingsPage and StatsPage long-label browser layout remains unverified and is carried to Task 11's integrated browser fixtures, as the reviewer recommends.
