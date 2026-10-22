# Plan 097 verification evidence

## Tested product tree

- Original immutable implementation base recorded before edits: `898f8b20f40246b3d0ff5263c5969ed4e2130b12` ([archived base record](evidence/097/plan097-base.txt)).
- Task 3 started at `0b9c2e2c7304cd78e78523d87a19b3d953901546` after the authorized Plan 096 merge; preserved separately in `task3-starting-revision.txt`.
- First product gate revision: `6d658da7566d614ae22e4b61c3835715a67a2665`, tree `997a45e416da250c0d6ab2136819f737eae093e7`. This gate preceded a correct-original-base formatter check, so it is historical and does not verify the formatting-corrected branch.
- Formatting-corrected product revision: `aea93d65347e4b74af6e39f5266910f1b527cc10`, tree `66cb76325673e218d66492a7de33f1d66edc95b9`. The first gate on this formatting-corrected revision failed one process-test timeout; the focused rerun and full unchanged-tree retry passed.
- Plan 097 product commits: `ecf0c52 fix(097): add singleton parameter batch regression tests`; `c3e6868 fix(097): pin singleton compare reproduction identity`; `a5d5ccd Allow singleton parameter batches`; `a3c869b test(097): prove split 32+1 batch workflow`; `6d658da fix(097): normalize regression test formatting`; `aea93d6 fix(097): normalize original-base branch formatting`.
- Plan 097's original implementation base is `898f8b2`. Authorized Plan 096 was integrated into main at `ebf7a16`; merge `0b9c2e2` brought that main state into Plan 097 and is the Task 3 starting revision, not the original Plan 097 base.
- Documentation/evidence commits are `08b69bc` and `4e8737e`; they do not change source or tests. Final scoped review is approved.

## Deterministic 32+1 workflow

The served-MCP regression exercises the actual `SqlHarnessModule` over stream transport, using a recording fake SQL session factory and temporary artifact home. It submits 33 uniquely named and valued parameter sets in two operations of 32 and 1. Each report has each expected name once; each value is seen by SQL twice under the existing warmup plus `repeat: 1` contract. The operations have distinct labels and artifact IDs, and both artifacts are recovered through `sqlharness_artifact`.

Measured transport accounting across two measure calls and two artifact recovery calls:

- Calls: 4 total (2 measure, 2 artifact recovery).
- Request: 3,200 UTF-8 wire bytes.
- Response: 78,187 UTF-8 wire bytes.
- Unique cases/sessions: 33.
- SQL executions: 66 (one warmup and one measured execution per case).
- Live database connections: 0; the fixture has no live provider connection path.

These are measured fixture transport bytes and fake-session execution counts. They do not establish model-token savings, actual-agent behavior, database/provider behavior, or cross-call atomicity. See `evidence/097/raw/task-3-regression-green.log`.

## Verification commands and results

- Focused split regression: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter "FullyQualifiedName~Split_32_plus_1_measure_workflow_executes_each_named_case_once_and_keeps_operation_artifacts_separate" --logger "console;verbosity=detailed"` — passed 1/1.
- Focused Core matrix suite after updating two stale minimum-count assertions: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~CompareMatrixTests" --logger "console;verbosity=normal"` — passed 64/64.
- Focused dashboard shutdown request-capture rerun: passed 1/1 in 150 ms. This had failed once during an earlier full Core run and passed in isolation; it was not reproduced.
- Full MCP suite: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --logger "console;verbosity=normal"` — 505 passed, 4 skipped, 509 total.
- The earlier Task 3-only direct branch formatting check from `0b9c2e2` covered two C# files and passed, but did not cover the immutable base's full branch diff.
- Correct-base formatter check initially failed with exit 2 on 28 changed C# files. The complete output and direct exit are preserved at `task-3-original-base-format-check-failed.log`. It found final-newline adjustments and one whitespace correction in `ParameterSetResolver.cs`.
- Branch formatting fix mode from `898f8b2` passed and changed 18 C# files: 17 final-newline corrections and one whitespace wrap. `git diff --ignore-space-at-eol` confirmed the whitespace-only code-shape change in `ParameterSetResolver.cs`. Raw output: `task-3-original-base-format-fix.log`.
- Direct post-commit check: `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 898f8b20f40246b3d0ff5263c5969ed4e2130b12` — 28 C# files, `format: OK`, process exit 0. Raw output: `task-3-original-base-format-check-final.log`.
- Earlier Windows gate: `just verify -LogDirectory D:/temp/sqlharness-plan097-verify` passed on `6d658da` (five stages, 8m46s). UI: 64 files/372 tests; Core: 4,652 passed/11 skipped; MCP: 505 passed/4 skipped; build: zero warnings/errors. It is retained as historical evidence only because the full original-base formatter check had not yet run. UI emitted a non-failing Node `DEP0190` diagnostic because a child process uses `shell: true`.
- Formatting-corrected gate: `just verify -LogDirectory D:/temp/sqlharness-plan097-final-verify` exited 1 on `aea93d6`: Core 4,651 passed/11 skipped/1 failed. `PidPublicationError_PreservesOriginalWhenMarkerWriteFails` timed out at ProcessRunnerTests.cs:245. Its isolated rerun passed 1/1 in 506 ms (direct exit 0); both logs are archived. No source or test change was made for this timeout.
- Final unchanged-tree retry: `just verify -LogDirectory D:/temp/sqlharness-plan097-final-retry-verify` passed on `aea93d6`, direct exit 0, five stages in 7m57s. Core: 4,652 passed/11 skipped; MCP: 505 passed/4 skipped; UI: 64 files/372 tests. Build passed. Gate formatting skipped committed files; the separate correct-base 28-file format check passed. Raw summary: `evidence/097/raw/task-3-final-retry-just-verify.log`; stages: `evidence/097/raw/final-retry-*.log`.

The first gate's stage and command logs, formatter failure/fix/success logs, and focused test logs are archived in `evidence/097/raw/`. The failed formatting-corrected gate and successful final retry summaries and stage logs are also archived there.

## Regression and review record

The earlier full Core run before the stale matrix assertions were corrected reported 4,655 passed, 29 skipped and 3 failed. Two failures were obsolete assertions for the former two-case minimum; the third was the dashboard request-capture case, which passed the isolated rerun. The focused matrix suite and final Windows gate pass after the assertion updates. Task 1's meaningful RED logs and report, plus Task 2's report, are preserved in `evidence/097/`.

Task 3 review and the Sol low whole-branch review report no product findings on the original reviewed product. Their archived reports are `evidence/097/task-reports/task-3-review.md` and `evidence/097/task-reports/whole-review.md`; they were conditional on completion of the original-base formatting and final gate. The scoped fix-wave report `evidence/097/task-reports/whole-rereview.md` confirms the formatter changes are semantic-neutral and flags final gate/evidence consistency as pending. The base/revision descriptions are now corrected; the final gate is now green and final scoped documentation approval is complete (Sol low: Ready to merge Yes, no findings; `evidence/097/task-reports/whole-rereview-final.md`). The historical `Fixtures/OperationAnnotations/off/tools-list.json` remains unchanged; whole-branch review agreed it is archived pre-change fixture evidence, not the current served schema contract.

No integration, push, live database access, paid-agent trial or Linux gate occurred.

## Local integration

The owner selected local merge. Main fast-forwarded from ebf7a16 to 3563283; source, tests and scripts exactly match the Windows-verified aea93d6. No integrated-tree gate was rerun because executable content is unchanged. The pre-existing main README edit was temporarily stashed and reapplied unchanged; it remains uncommitted. The plan worktree retains its pre-existing untracked noop and was preserved. No push occurred.
