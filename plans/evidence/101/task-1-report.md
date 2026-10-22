# Task 1 report — Plan 101

Worktree: `D:\Dev\sqlharness\.worktrees\plan-101`
Base: `2b0530b65e87fafe8cd09d05756389a56d8c0989`
Status: implemented; no commit created.

## Changes

- Added `JournalSettings.operationAnnotations` and set fixture defaults to `false`.
- Added the Agent operation annotations switch to Activity journal settings with the required explanatory text, MCP restart/reconnect guidance, and retention note.
- Routed annotation changes through the existing journal `setJournal` and save queue. Enabling annotations while the journal is off saves both booleans as true; disabling the journal saves both as false; re-enabling the journal leaves annotations false.
- Added focused UI coverage for default-off and loaded-on states, on/off saves, paired transitions, unrelated-setting preservation, rapid queued journal changes, and rejected saves.
- Added API coverage for GET defaults, GET/PUT round-trips for both values, unrelated settings, and validation errors that preserve the existing file.
- Added the `journal.operationAnnotations` validation error to `SqlHarnessConfigLoader.Parse` so invalid pairs are rejected by the shared Settings write path.

## RED evidence

- `npm --prefix src/SqlHarness.Dashboard/ui test -- src/pages/SettingsPage.test.tsx` initially could not start because the worktree had no `vitest` install. After `npm ci` in the UI worktree, the intended RED run completed: 6 new behavior tests failed because the switch was missing; 21 existing tests passed.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter FullyQualifiedName~DashboardSettingsTests` with the backend regression cases failed as expected: `Put_with_operation_annotations_without_journal_preserves_the_existing_file` expected `BadRequest` but got `OK`; 18 tests passed. This demonstrated that the shared config parser did not enforce the required journal/annotations relationship at the Settings endpoint.

## GREEN evidence

- `npm --prefix src/SqlHarness.Dashboard/ui test -- src/pages/SettingsPage.test.tsx` — passed, 28/28 tests.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter FullyQualifiedName~DashboardSettingsTests` — passed, 19/19 tests.
- `npm --prefix src/SqlHarness.Dashboard/ui run typecheck` — passed (`tsc -b`).
- `git diff --check` — passed.

## Self-review and concerns

The full-document settings object remains the save unit, so changes in other cards retain `operationAnnotations`. Coupled journal transitions are each asserted as one request with the exact valid boolean pair. The API round-trip tests confirm the configuration writer/reader already handled this setting; no `DashboardSettings.cs` change was needed.

Scope adjustment: `SqlHarnessConfigLoader.Parse` lacked the validation implied by Plan 101’s global constraint, despite the MCP process context checking it separately. The failing endpoint regression test justified adding the field-level validation there; this prevents Dashboard PUT from persisting an invalid pair and leaves the original config bytes unchanged on rejection.

The first `dotnet test` attempt encountered `npm ci` failing with a transient `ENOTEMPTY` error while preparing the UI dependencies. A standalone `npm ci` in the worktree UI then succeeded; subsequent focused API test runs passed. One intermediate API build showed Vite’s existing chunk-size warning; the final incremental focused API run emitted no build warning. No full `just verify` was run because Task 2 owns that gate.
