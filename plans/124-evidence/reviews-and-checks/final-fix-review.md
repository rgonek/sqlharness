# Plan 124 final fix scoped re-review

Range: `cc7af99f..f25a650b`. Scope: the two findings in the whole-branch review and new breakage in this fix diff. I inspected the three-commit diff and final fix report; I did not rerun tests. Product revision `71a42d2a` is the revision that passed the reported final Windows gate; `f25a650b` adds documentation and evidence only.

## Finding dispositions

- **Important, dirty form lost on refresh — ADDRESSED.** `src/SqlHarness.Dashboard/ui/src/pages/ProfilesPage.tsx:45,52-56,75-80` now holds the opening profile and revision in editing state. The form stays mounted when a refresh returns missing or invalid data, removes the selected profile, or raises a query error. The server state remains visible, Save uses the opening revision, and Cancel and profile changes retain dirty confirmation. `ProfilesPage.test.tsx:235-260` covers all four refresh outcomes, stale revision, and explicit discard. Recorded RED cases failed before the fix; 24 focused cases passed after it.
- **Minor, missing browser success/reconnect assertion — ADDRESSED.** `plans/124-evidence/browser-fixture.mjs:16` now waits for the saved/reconnect notice and checks that the form closes. The final Edge synthetic run records PASS.

## New breakage in fix diff

None found. The selected-profile click guard compares against the held editing profile, so a refreshed list does not silently change the draft. Query errors and missing/invalid states are displayed alongside the held form.

## Unrelated observations

- The Windows-only Save scope and its disclosed platform limitation are unchanged.
- Synthetic tests and the Windows gate do not establish installed-client activation, live database behavior, Unix/macOS behavior, or the broker pending-approval lifecycle. These limits remain as recorded in the whole-branch review.

## Assessment

**Scoped re-review: Approved.** Both findings are addressed, and I found no new issue in the fix diff. The final recorded Windows gate passed on the exact product revision: UI 462, Core 4834 passed/11 skipped, MCP 537 passed/4 skipped; branch C# format passed for 21 files.
