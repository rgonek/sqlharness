# Plan 115 local integration verification

User selected local merge; no push was authorized or performed. Previous main: `621a25210fc3338dcea90392895ac62957d8f5f0` (Plans 104/114). Plan 115 feature head: `0e240332`. Merge candidate: `0baa1ade`; final product/test revision: `0d37fa9e5e549118702b9a195e42bd68aabcf273`. Local main fast-forwarded to that exact verified candidate on 2026-10-25. Subsequent archival/status changes are documentation only.

## Resolution and review

[Resolution report](resolution-report.md) records both conflicts and auto-merge checks. The combined catalog keeps query selection, statement-summary and current IO guidance; the inherited 36 KiB tools/list regression-test ceiling remains test-only and unchanged. No response or runtime limit was raised.

[Luna xhigh task review](task-review.md) found post-execution selector errors discarded journal correlation and emission accounting. Fix `0d37fa9e` constructs a proper safety error while preserving the original operation reference, journal flags, coverage and emission receipt. Served regressions reproduced the missing reference before the fix and verify one execution, one receipt completion and the actual emitted footprint afterward. The [scoped rereview](task-rereview-1.md) approved the correction; the [Sol low combined review](whole-review.md) approved the integration without findings, conditional on the full gate that subsequently passed.

Focused suites: 142/142 before the accounting correction; 164/164 afterward. Explicit branch format against `621a2521`: eight changed C# files, `format: OK`, direct exit 0, recorded in the resolution report. No findings remain parked or deferred.

## Full Windows verification

`just verify -LogDirectory .superpowers/sdd/115-query-result-set-selection/integration/windows-final-retry` on `0d37fa9e`: **exit 0**, `verify: OK`, five stages. [Direct summary](windows-final-gate.log), [build](windows-final/build.log), [UI](windows-final/ui-check.log), [Core](windows-final/test-SqlHarness.Tests.log), [MCP](windows-final/test-SqlHarness.Mcp.Tests.log).

- Build: zero warnings/errors.
- UI: 64 files, 372 tests passed.
- Core: 4718 passed, 11 skipped.
- MCP: 535 passed, 4 skipped.
- Gate format skipped committed files; the separate branch check above covers the eight changed C# files.

The overnight run records 513m29s total and 509m05s for the test stage. These are retained log durations, not a performance claim. The existing UI Node DEP0190 warning is retained in the raw log.

## Earlier attempts and preservation

The [initial candidate gate](windows-interrupted.log) was stopped after review found the accounting defect. The [first corrected gate](windows-failed-gate.log) failed two inherited Core tests: dashboard stopping and cancellation process cleanup. The cancellation test left a confirmed test child holding stdout open; the gate waited after both test processes had exited. Read-only inspection located the wait and recovered the failure output. Only the identified child from that run was stopped, allowing the gate to finish. [Core failure log](windows-failed/test-SqlHarness.Tests.log); [buffered recovery](windows-failed/core-recovered-StandardOutput.log).

The [exact two failed tests](focused-environment-retry.log) then passed 2/2 without source changes, followed by the full successful gate above. Failed and interrupted runs are not counted as passing evidence.

Primary checkout README/index edits and untracked Plans 111/112/113 were backed up with hashes under `D:/temp/sqlharness-plan115-preserve-20261024T211020`. Only the overlapping local plan index was temporarily stashed for fast-forward; the final restoration checks are recorded in the integration ledger. No live database, installation, publication or paid-agent run occurred. Linux was not run.

Restoration completed on 2026-10-25: the original 35-line local index patch is identical after apply; root README and untracked Plans 111/112/113 match their saved hashes. The final main source/test/script tree is byte-identical to Windows-verified `0d37fa9e`. Managed worktree retained; no push.
