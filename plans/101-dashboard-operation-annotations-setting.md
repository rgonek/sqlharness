# Plan 101: Operation annotations in Dashboard Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans or superpowers:subagent-driven-development to implement this plan task-by-task.

**Goal:** Let the operator enable and disable agent operation annotations from Dashboard Settings.

**Architecture:** Reuse the existing Settings API, configuration loader/writer and journal card. Expose the existing `journal.operationAnnotations` boolean in the frontend; preserve process-start MCP configuration semantics.

**Tech Stack:** .NET, React, TypeScript, Vitest, xUnit.

**Spec:** [Fix contract](#fix-contract) below; existing [operation annotations contract](../docs/mcp/tools.md#operation-annotations).

## Status and dependencies

TODO — plan only. Implementation is not authorized by adding this plan. Depends on the existing operation annotations feature (085, locally integrated). Coordinate shared Settings files with pending plan 092.

## Fix contract

- Add an **Agent operation annotations** toggle in Settings > Activity journal, bound to `journal.operationAnnotations`. Keep the existing default `false` and full-document autosave behavior.
- Explain beside it: "Allows agents to label operations and mark them for keeping. Requires the activity journal. Restart the MCP server and reconnect the client after changing this setting. Keep markers do not prevent retention."
- Enabling annotations while the journal is disabled saves both `journal.enabled: true` and `journal.operationAnnotations: true` in one request. Explain this behavior beside the toggle; do not send an invalid intermediate configuration.
- Disabling the journal also saves `journal.operationAnnotations: false` in the same request. Explain that disabling the journal disables agent annotations. Re-enabling the journal alone leaves annotations off.
- Disabling annotations leaves the journal, saved annotations, owner stars/notes and retention settings unchanged. Do not delete historical evidence.
- Treat the toggle as the saved setting, not proof that a running MCP process has reconfigured. Do not automatically restart processes or reconnect clients.
- Use the existing journal save feedback and error handling. Failed saves must remain visible and must not be represented as successfully persisted.

## Global constraints

- Preserve the existing configuration validation: annotations require `journal.enabled: true`.
- Reuse the existing Settings endpoint; introduce no new endpoint or configuration key.
- Preserve all unrelated settings during saves, including annotations when another card changes.
- Keep existing sensitive-storage policy, mutation approval, retention and MCP authorization intact.
- No new confirmation dialog is required for this boolean setting.

## Review focus

1. Existing config omitting the key renders the default off.
2. Enabling annotations with the journal off must produce one valid save.
3. Disabling the journal with annotations on must produce one valid save.
4. Other settings saves and rapid journal edits must preserve the latest annotation value.
5. Save failures and pending restart must not appear as active MCP support.

## Task 1: Expose the setting through the existing journal card

**Files:**
- `src/SqlHarness.Dashboard/ui/src/api/types.ts` — `JournalSettings`.
- `src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx` — journal toggle and atomic transitions.
- `src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.test.tsx` — behavior and error coverage.
- Other typed Settings fixtures, including `SettingsMutations.test.tsx`, as required by the typecheck.
- `tests/SqlHarness.Tests/Dashboard/DashboardSettingsTests.cs` — existing API round-trip coverage.
- `src/SqlHarness.Dashboard/DashboardSettings.cs` only if a demonstrated round-trip gap requires a fix.

**Interfaces:** Consume and persist `Settings.journal.operationAnnotations: boolean` using the existing Settings read/save API and journal save queue. No new public interface.

- [ ] Add focused failing UI tests for default off, loaded on, on/off saves, enabling with journal off, disabling the journal with annotations on, and enabling the journal alone. Assert the exact paired booleans in each save and preservation of unrelated values.
- [ ] Add tests for changing another card with annotations on, queued rapid journal changes, a rejected save and restart/reconnect guidance. Reuse current save-queue and failure behavior.
- [ ] Add backend regression coverage: GET/PUT round-trips both annotation values; invalid `enabled: false, operationAnnotations: true` returns a field error without changing disk; unrelated settings survive.
- [ ] Run focused tests and confirm the intended failures before implementation: `npm --prefix src/SqlHarness.Dashboard/ui test -- src/pages/SettingsPage.test.tsx` and `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter FullyQualifiedName~DashboardSettingsTests`.
- [ ] Add the boolean type and fixture defaults, the toggle, explanatory text and paired journal transitions. Reuse the existing `setJournal`/autosave path and feedback; avoid separate requests for coupled values.
- [ ] Rerun the focused tests; run `npm --prefix src/SqlHarness.Dashboard/ui run typecheck`. All must pass.

## Task 2: Document and verify the delivered behavior

**Files:** `README.md`, this plan and `plans/README.md`; add `plans/101-verification-evidence.md` when implementation lands.

- [ ] Update the existing human-facing annotation setup guidance to mention the Dashboard Settings toggle and the required MCP restart/reconnect. Link to the authoritative interface contract rather than duplicating it.
- [ ] Browser-check Settings with a disposable config: enable with journal off, reload, disable journal, reload and inspect save/error feedback. Confirm saved values; do not alter a real operator's configuration for acceptance testing.
- [ ] Run `just verify` on the final product tree as required by [verification](../docs/agents/verification.md). Record actual results and revision; no live database calls are required.
- [ ] Record evidence and update plan/index status when work lands. Commit or publish only when separately authorized; keep delivered changes cohesive.

## Completion criteria

The dashboard can persist either annotation setting without hand-editing JSON, cannot submit an invalid journal/annotation pair through these controls, preserves unrelated settings, and clearly communicates restart/reconnect requirements. Focused tests, browser checks and the required implementation gate pass.
