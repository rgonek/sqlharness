# Task 1 report

Status: DONE_WITH_CONCERNS (task source complete; controller browser/review/full gate pending).

Base: 0aa90410. Product commit: fa5aba0f4cd6e41807d06bd738199de746247433.

## Requirements and implementation

Read the task brief, spec, audit F1/F2, AGENTS, plans index and verification contract. Used the shadcn skill, ran `npx shadcn@latest docs field switch radio-group`, fetched official base component documentation. No new dependencies/components.

Changed only:
- src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx
- src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.test.tsx
- src/SqlHarness.Dashboard/ui/src/components/optimization/VersionTable.tsx
- src/SqlHarness.Dashboard/ui/src/components/optimization/VersionTable.test.tsx
- src/SqlHarness.Dashboard/ui/src/components/optimization/OptimizationSection.test.tsx

Settings uses installed FieldGroup, responsive Field, FieldContent, FieldLabel, FieldDescription and FieldError. Switch titles click their controls; switch/number/sound descriptions have explicit IDs and aria-describedby. Field groups themselves have explicit labels and descriptions (also useful for composed mutation controls). Local/API number errors have stable IDs, alert role, aria-errormessage and aria-describedby; Field data-invalid and Input aria-invalid agree. Existing save queue, confirmation decisions, sound actions, restart/browser tags and descriptions are preserved. Multi-paragraph compare help uses block spans within FieldDescription's paragraph to preserve valid HTML. Base UI linked labels now name switches consistently with their visible titles (Activity journal, Agent annotations, Retention); tests assert those names.

A/B uses native radio inputs in ordinary table cells. Two interleaved column groups cannot use overlapping ancestor RadioGroup wrappers without breaking table HTML or introducing a second representation. Native names provide genuine browser exclusivity/arrow navigation without invented handlers. Each table has useId-scoped group names; each item names side/version and references the Baseline A/Candidate B heading as its description. Explicit value and semantic accent-primary token; no custom radio skin. Selecting an already checked radio does not emit a new selection, per native behavior. An existing multi-card context test now selects another version instead of clicking the checked value.

## Verification

- Before implementation: two new Settings tests failed because accessible descriptions were absent.
- Focused command: `npx vitest run src/pages/SettingsPage.test.tsx src/pages/SettingsMutations.test.tsx src/components/optimization/VersionTable.test.tsx src/components/optimization/OptimizationSection.test.tsx` — 4 files, 50 tests passed (82.46 s).
- Final focused rerun after adding API linkage assertions and native accent/value: `npx vitest run src/pages/SettingsPage.test.tsx src/components/optimization/VersionTable.test.tsx -t 'API field errors|visible switch|invalid numbers expose|independent exclusive'` — 2 files, 4 passed, 27 skipped (30.15 s).
- `npm run typecheck` and `npm run lint` both passed on final source.
- Task-path `git diff --check` passed; manually inspected source and test diffs before committing.
- Earlier full focused runs exposed expected old switch-name and selected-toggle assertions; corrected to the new semantic behavior, then reran successfully.

New native coverage checks independent A/B exclusivity, Space activation, no-initial-B state, accessible column descriptions and two simultaneously mounted table instances. Real-browser ArrowDown verification remains with the controller because DOM test environments do not implement native browser arrow behavior faithfully. No bespoke arrow handler was added.

## Self-review and tradeoffs

Spec review: F1/F2 covered without backend/runtime-policy changes. Save/confirmation regression coverage remains green. Table structure remains valid and groups do not couple across instances. No unrelated dirty paths staged.

Quality review: defaults carry Field typography/colors, responsive layout is handled by the installed component and semantic layout utilities; no blanket overrides or component edits. Existing Settings card shells/other views are outside Task 1. Parent group descriptions preserve meaningful context for helper-composed controls without expanding that helper's API.

Mock tradeoffs: Settings now uses default Mira Field typography/gaps and responsive stacking rather than legacy setting-row typography. A/B retains circular selection intent via real radios and A/B column headings, but drops letters inside custom 24px circles and bespoke selected colors in favor of native dots with semantic accent-primary. Matching the old mock skin is not worth replacing native behavior. Browser visuals/mobile overflow and independent review/full Windows gate are controller responsibilities and are not claimed complete here.

No merge, push, publication, installed-dashboard restart, live database action or approval supplied.

## Gate-discovered cross-task selector correction

The combined full gate on 3fe231df failed ui-check with 413 passed / 1 failed. Read D:/temp/sqlharness-default-alignment-gate-20261028/ui-check.log and current SessionPage.tsx: Task 2 deliberately renamed the compact operations title to Operations (loaded count). This was a stale cross-task test selector, not a UI behavior regression.

Changed only OptimizationSection.test.tsx line 65 to `screen.getByText(/^Operations \(\d+\)$/)`. Existing compareDocumentPosition ordering assertion and every scope/URL/radio assertion remain unchanged. No runtime product edits.

Validation: `npx vitest run src/components/optimization/OptimizationSection.test.tsx` passed all 12 tests (20.25 s); scoped git diff --check passed; inspected the one-line diff. Commit: c2c3dcfdde3326fc5e2815c45c5a9c024421124f. Full gate rerun remains with the controller.
