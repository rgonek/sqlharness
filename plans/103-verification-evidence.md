# Plan 103 verification evidence

Status: DONE; locally integrated into `main` at `1301aa7b`; all task/whole-branch/scoped reviews approved; Windows verification and explicit branch format passed. Not pushed; no installation, paid-agent run or live database access.

Branch base: `6d543e509734a374caf882bc8c05ffe5ccaa98b2`. Isolated managed worktree: `C:/Users/rgone/MultiCliProfiles/codex/account-b/worktrees/3aed/sqlharness`. Source checkout dirty README/index and unrelated untracked plans preserved. Plan 082 implementation merge `dd375124` inspected; plan drift scan against `677b2f93` was empty for the named product paths.

## Staged implementation

| Task | Commits | Focused verification | Independent review |
|---|---|---|---|
| 1: fixtures and reusable attribution classification | `51d6fdd6..e997cc43` | Behavioral RED with unavailable stub; GREEN 48/48 statement/journal tests; format OK | Luna xhigh approved spec and quality, no findings |
| 2: persisted evidence and offline reader | `e997cc43..b8a0f4f5` | Behavioral RED for new evidence fields; GREEN 207/207 focused tests. Fix RED for 18 TVF entries; GREEN 100/100. Format OK | Luna xhigh found silent hidden-work list omission; fixed exact omitted count and full-list limitations; scoped Luna xhigh re-review approved |
| 3: compact indications and MCP workflow | `b8a0f4f5..36541d56` | Required filters Core 101/101, MCP 57/57; SDK minimum/default 2/2; UDF limitation 1/1. Fix regression RED; GREEN 116/116 | Luna xhigh found missing-run evidence could appear complete; corrected per-run/case classification; scoped Luna xhigh approved. Minor global omission scope subsequently fixed and approved in final review |

Implementers: Luna high. Task reviewers: Luna xhigh. Whole-branch reviewer: Sol low (user-selected).

## Controller decisions

- Task 1 introduced the smallest reusable classifier to make its behavioral regression green; storage and compact responses stayed in Tasks 2/3. Risk: a small amount of Task 2 semantics moved earlier.
- Only the assigned untracked plan was copied into the native managed worktree; index integration is reconstructed in isolation. Risk: later index integration may need conflict resolution.
- Newly discovered full-gate failures were repaired as verification remediation after the final-review fix wave, without increasing ceilings. Cost: extra scoped review and two repeated full gates; failed evidence remains archived.

## Final verification

Sol low whole-branch review found one Important minimum-budget hidden-work retrieval gap and confirmed the per-case omission Minor. Both were fixed in `0ba460b9`; scoped Sol low re-review approved both with no new breakage. The rebuilt final-fix regressions passed Core 3/3 and real MCP SDK 1/1 at 4096/default budgets. See [reviews](103-evidence/reviews/final-rereview.md) and [fix report](103-evidence/reviews/final-fix-report.md).

First Windows gate on `167829f7`: UI/install/build passed; Core 4666 passed, 11 skipped; MCP 508 passed, 4 skipped, 2 failed (request-scope annotation catalog exceeded 32768 bytes; stale exact default statement-page count). Raw logs are retained in [first-gate](103-evidence/first-gate/test-SqlHarness.Mcp.Tests.log).

Remediation `bb7533ed` shortens descriptions without contract loss or raised limits and retains bounded source-page assertions/full 512-row reconstruction. Targeted tests passed 2/2; scoped Sol low review approved. Request catalog range is verified by the test; its 32672-byte size is derived from observed prior size and exact ASCII description delta (96-byte headroom), not separately printed final measurement. See [fix](103-evidence/reviews/gate-fix-report.md) and [review](103-evidence/reviews/gate-review.md).

Second gate on `bb7533ed`: UI/build passed; Core 4665 passed, 11 skipped, one unchanged `ProcessRunnerTests.PidPublicationError_PreservesOriginalWhenMarkerWriteFails` five-second timeout; MCP 509 passed, 4 skipped, one stale exact-description assertion. See [Core](103-evidence/second-gate/test-SqlHarness.Tests.log) and [MCP](103-evidence/second-gate/test-SqlHarness.Mcp.Tests.log). The isolated unchanged ProcessRunner test then passed 1/1 without auth changes; this does not erase the failed gate or prove its underlying cause.

Test-only revisions `e497b69e` and `874b4fc9` retain semantic description assertions, including explicit session isolation. The complete MCP schema class passed 63/63; scoped Sol low review approved the isolation follow-up. See [review](103-evidence/reviews/gate-test-final-review.md). `874b4fc9` was committed before the final gate build.

**VERIFICATION PASS** on final product/test revision `874b4fc957269156e84b856f65118adf255313ad`:

- `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 6d543e509734a374caf882bc8c05ffe5ccaa98b2`: exit 0, 14 changed C# files, `format: OK` ([log](103-evidence/format/format-final.log), [exit](103-evidence/format/format-final.exit)).
- `just verify -LogDirectory D:/temp/sqlharness-plan103-final-verify-r3`: exit 0, five stages, 24m13s. UI install skipped because the lockfile was unchanged; build/UI/test passed. Gate working-tree format skipped because all C# was committed; the explicit branch check above covers those files.
- Core: **4666 passed, 11 skipped, 0 failed** ([log](103-evidence/final-gate/test-SqlHarness.Tests.log)); MCP: **510 passed, 4 skipped, 0 failed** ([log](103-evidence/final-gate/test-SqlHarness.Mcp.Tests.log)).
- UI: **64 files / 372 tests passed**, typecheck and lint passed ([log](103-evidence/final-gate/ui-check.log)). Existing non-failing Node `DEP0190` warning appears in all archived gate UI logs; no UI source changed in this plan.
- [Gate exit](103-evidence/final-gate/verify.exit) is 0. Final relative Markdown links and `git diff --check` passed. Delivery commits after the tested revision contain only plans/evidence and scratch cleanup; executable source and tests are unchanged.

No unresolved review findings remain. The catalog has only 96 bytes of derived request-scope headroom; existing schema tests enforce the unchanged 32768-byte ceiling for future changes. Attribution remains counter coverage, not complete SQL cost. Older/engine-absent evidence stays unavailable, and bounded lists retain explicit omissions. Research-only checklist clauses are not applicable; this was an offline implementation plan.

Linux and live database/installed-binary acceptance remain unverified. Synthetic/offline tests establish the implemented contract only; they do not establish measured agent-token savings or complete SQL execution cost coverage.

## Local main integration (2026-10-23)

User authorized local integration. `main` fast-forwarded from `6d543e50` to reviewed branch head `1301aa7b`; no merge conflicts or new executable changes. `git diff --quiet 874b4fc9 HEAD -- src tests scripts` exited 0 after integration, preserving the exact Windows-verified product/test tree. Existing gate evidence is reused; no redundant full gate was run for integration bookkeeping only, in accordance with repository verification policy.

The unrelated dirty root README and other untracked plans are preserved. The local planning queue was temporarily stashed by path; its unrelated entries are restored as uncommitted edits, while Plan 103 is represented by its completed history entry. The superseded untracked Plan 103 draft and original index are backed up under `D:/temp/sqlharness-plan103-main-integration-20261023-202657`. The managed feature worktree is retained. No push occurred. Linux/live acceptance boundaries remain unchanged.
