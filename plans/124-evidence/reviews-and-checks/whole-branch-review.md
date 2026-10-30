# Plan 124 whole-branch review

Range: `8d8d959f..cc7af99f`. Read-only review of the branch, plan, design spec, recorded verification, and task review ledger. I did not rerun the gate. The reported Windows gate ran on `21567424`; the later product change removes only an EOF blank line.

## Strengths

- `MutationPolicyResolver` supplies a common mode and readiness interpretation to dashboard presentation, capabilities, submission, and execution. An absent profile mode continues to follow the global default while the writer stays an explicit profile choice.
- `ProfileWriter` validates the original and candidate documents, checks the exact-byte revision, preserves sibling JSON bytes, creates a protected same-directory temp file, rechecks before atomic replacement, and cleans up failures. The endpoint takes no caller path and uses the dashboard write guard.
- The form captures its opening revision, freezes controls while saving, keeps the draft on server errors, and communicates that saved profiles require client restart or reconnection. The accepted Windows-only write restriction is disclosed in both API metadata and UI.
- The archived Windows gate reports UI 458 passed, Core 4834 passed/11 skipped, and MCP 537 passed/4 skipped. Focused policy, writer, endpoint, and form tests exercise the main synthetic contracts.

## Issues

### Critical

None found.

### Important

1. **A background Profiles refresh can discard an unsaved edit without confirmation.** `src/SqlHarness.Dashboard/ui/src/pages/ProfilesPage.tsx:49-57,70` returns the error or missing/invalid view ahead of the form, and chooses the form profile from the refreshed, filtered list. If an external editor deletes the selected profile, the file becomes invalid/missing, or a query refetch fails while the form is dirty, the form unmounts and its local draft is lost. `useBlocker` only covers route navigation. Keep an editing snapshot mounted through these refresh outcomes, show the new server state alongside it, and require explicit discard. Test each refresh path. This is a branch-wide user data-loss issue despite being a deferred observation in the task-3 ledger.

### Minor

1. **The browser fixture does not assert the success and reconnect notice.** `plans/124-evidence/browser-fixture.mjs:1-17` exercises Save, but the final browser evidence does not assert the post-save message from `src/SqlHarness.Dashboard/ui/src/pages/ProfilesPage.tsx:68`. Add an assertion when refining the fixture; existing component tests and source establish the intended rendering.

## Declined to judge

- Unix/macOS profile-save support: the explicit controller ruling and revised spec limit writes to Windows; platform metadata preservation and native behavior need a separately scoped implementation.
- Broker pending-approval lifecycle under external profile edits: the task-4 test proves the prepared-target executor boundary, not a full broker lifecycle. I did not infer a live broker failure from that narrower proof.
- Installed dashboard, existing MCP process activation, real file ACL behavior, and database writes: synthetic local verification does not establish these live outcomes.
- The initial SDD ledger's pending task snapshot: this is archival status wording, not product behavior.

## Recommendations

Preserve the dirty edit snapshot before any query-state early return or profile selection fallback. After that fix, run focused UI cases for missing, invalid, removed-profile, and query-error refreshes; the already recorded full gate need not be repeated solely to restate its prior result.

## Assessment

**Ready to merge? With fixes.** The implementation and recorded verification cover the core policy and save contracts, but the dirty-draft loss needs correction before the branch is ready. Windows-only saving is a disclosed, safety-motivated scope decision; installed and live behavior remain unverified.
