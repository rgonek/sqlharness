# Plan 092 Task 3 report

## Changes

- Added `ExecutionSettings` and `Settings.execution` to the dashboard TypeScript contract.
- Added the SQL execution card with the requested description, 1..300-second number field, per-field validation message, save status, and settings save queue behavior.
- Added contract coverage proving the Settings response exposes the default 30-second execution timeout and matches the TypeScript shape.
- Added API tests for a custom timeout saved to disk and read back from GET, plus HTTP 400 field errors for 0 and 301 seconds. Existing Task 2 parsing, atomic config writer, and strict permissions remain the persistence path.
- Updated Settings fixtures used by UI tests and added card display, save-on-commit, preservation, and API error coverage.

## RED/GREEN evidence

- RED: focused Vitest run of the new SQL execution tests failed because the SQL execution card and field were absent (2 failed, 17 skipped).
- RED: focused C# contract/API run first exposed a test compile typo; after correcting the test input, the new contract case failed because the TypeScript `ExecutionSettings` declaration was missing. The new API persistence and both bounds cases already passed through Task 2's config contract.
- GREEN: focused UI run across `SettingsPage.test.tsx`, `SessionsPage.test.tsx`, `LivePage.test.tsx`, and `SettingsMutations.test.tsx` passed 53/53.
- GREEN: focused C# contract and API cases passed 4/4.
- GREEN: `npm run check` exited 0: typecheck and lint succeeded; Vitest passed 363 tests across 64 files.

The first broader dashboard-filtered C# run had 491 passed, 3 skipped, and one failure in the new contract assertion because its TypeScript declaration matcher requires a multiline object shape. The declaration was adjusted to the existing matcher contract, then the focused C# rerun passed all four relevant cases.

## Files

- `src/SqlHarness.Dashboard/ui/src/api/types.ts`
- `src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/SettingsMutations.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/SessionsPage.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/LivePage.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/lib/settingsRules.test.ts`
- `tests/SqlHarness.Tests/Dashboard/DashboardContractTests.cs`
- `tests/SqlHarness.Tests/Dashboard/DashboardSettingsTests.cs`

## Concerns

- The full repository `just verify` gate was not run; it remains Task 4's responsibility.
- `npm run check` emits the existing Node `DEP0190` shell warning. No check failed because of it.
