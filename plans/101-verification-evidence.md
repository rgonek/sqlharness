# Plan 101 verification evidence

Status: implementation and browser acceptance passed; Luna xhigh task reviews and Sol low whole-branch/scoped reviews approved. The final Windows gate passed on the tree including the reviewed automatic-journal-enable guidance. This is an uncommitted worktree result, not an integrated branch. No commit, merge, push, live database call, or real operator-config change was made.

## Tested tree

- Worktree: `D:\Dev\sqlharness\.worktrees\plan-101`
- Base: `2b0530b65e87fafe8cd09d05756389a56d8c0989`
- Tested source tree: base plus the uncommitted Task 1 diff and Task 2 documentation. The source/test diff was 10 files, 171 insertions, 8 deletions; its pre-gate patch SHA-256 was `3BF84DD4DE035B76BBFCBE7CADD5E446A658391B41795DCB5C5B17A7B35CE4C8`.
- Before the first helper review, the source/test diff was 11 files, 242 insertions, 11 deletions; its SHA-256 was `A6B1C2680FBEC32E0CC2DFDD7D53A924B90DE0E42E7A30E3A52791619AB7138F`.
- The helper-fix gate tested the source/test diff of 11 files, 292 insertions, 11 deletions; pre-gate and post-gate SHA-256 are both `8524C505FD788F9EA273A90DAF3E4851A7430AFA0D481216EFBE79132DAB4053`. The tracked product tree did not change during verification.
- Task 1 implementation report, approved review, and review package are archived under [Task 1 evidence](evidence/101/task-1-report.md), [review](evidence/101/task-1-review.md), and [review package](evidence/101/task-1-review-package.md).

## Browser acceptance

Headless Microsoft Edge driven by Playwright exercised the built dashboard against a disposable `SQLHARNESS_HOME`, initially configured with both journal settings off. The browser enabled annotations while the journal was off and submitted one `PUT /api/settings` with both `journal.enabled` and `journal.operationAnnotations` set to `true`. Both values persisted across reload. It then disabled the journal in one save with both values `false`; both persisted across reload.

Both saves returned HTTP 200 and showed `Saved`; no Settings alerts or page errors appeared. Restart/reconnect, journal requirement, retention, and journal-disable guidance were visible. After the saves, `compare.requireContext` was `true` and execution timeout was 30. The browser artifact recorded their initial on-disk values as `null`, so this is post-save/default evidence rather than proof of explicit initial values. The sanitized result is [browser-acceptance.json](evidence/101/browser-acceptance.json). The dashboard was stopped through `dashboard --stop` (exit 0); the disposable home and token were not copied into this repository. No real operator configuration was read or changed.

## Windows gate attempts

The first command was `just verify -LogDirectory D:/temp/sqlharness-plan101-verify-20261021-final`; direct exit code 1, summary `verify: FAILED stage=test exit=1 9m50s`. `ui-install` passed (31.9s), build passed (1m12s; .NET reported 0 warnings and 0 errors), and UI check passed (64 files, 372 tests; 5m33s). The UI build emitted Vite's non-failing chunk-size advisory; UI check emitted Node `DEP0190`. MCP passed 496, skipped 4. Core reported 4,618 passed, 11 skipped, and one failure: `ProcessRunnerTests.PidPublicationError_ReportsDiagnosticsAndCleansUp` expected `InvalidOperationException` but got `TimeoutException` after 480 PID-publication attempts. The PID file contained one line, the publisher task exited 1, and child stderr was empty. The gate stopped before its format stage.

After inspecting the failure, the exact test passed alone (1/1, 546ms). The test helper and `src/SqlHarness.Core/Auth` have no diff in this task. The full gate was retried once at `D:/temp/sqlharness-plan101-verify-20261021-retry`; direct exit code 1, summary `verify: FAILED stage=test exit=1 8m40s`. UI check passed again (64 files, 372 tests; 4m53s), build passed (23.8s), MCP passed 496 with 4 skipped, and Core again reported 4,618 passed, 11 skipped, and the same PID-publication failure (22s). The second gate also stopped before its format stage.

At the time of those two failures, the root cause was unknown; the isolated pass alone did not establish why the full suite failed. A subsequent bounded investigation demonstrated the mechanism: the error-path publisher made one `Add-Content` attempt for its `ERROR:` marker, which can fail with a sharing violation while the PID poller has its read handle open. A scratch probe reproduced append failures under concurrent readers. A deterministic regression forced that overlap, failed before the fix with the same missing-marker timeout, and passed after adding bounded retries for `IOException` on the marker append. A second review found and drove correction of another diagnostic edge: non-sharing append failures must preserve the original publisher error as well as the marker-write failure. The process-runner suite passed 10/10 after that correction, and targeted formatting passed. The scoped re-review marked the Important finding **ADDRESSED** with no new Critical or Important issues. See [gate-blocker report](evidence/101/gate-blocker-report.md), [round-one review](evidence/101/gate-blocker-review.md), and [round-two review](evidence/101/gate-blocker-fix1-review.md). The original failed gate logs remain archived in [gate-first](evidence/101/gate-first/) and [gate-retry](evidence/101/gate-retry/); the original focused reproduction is [focused-pid-publication.txt](evidence/101/focused-pid-publication.txt).

## Formatting and scope

The first working-tree formatting check found missing final newlines in `src/SqlHarness.Core/SqlHarnessConfig.cs` and `tests/SqlHarness.Tests/Dashboard/DashboardSettingsTests.cs`. Per the repository verification contract, `pwsh ./scripts/format-changed.ps1 -Fix` corrected only those final newlines. The subsequent `pwsh ./scripts/format-changed.ps1 -Mode working-tree` passed for both files. Its output is [format-working-tree.txt](evidence/101/format-working-tree.txt).

The runtime behavior resolution is recorded in the SDD ledger: MCP loads configuration at startup into immutable process context, and the Plan 101 change leaves MCP startup, authorization, mutation, sensitive-storage, and retention code untouched. The gate fix is limited to the process-runner test helper and its regression test; it does not change production behavior.

## Final Windows gate

Command: `just verify -LogDirectory D:/temp/sqlharness-plan101-verify-20261021-helperfix-final`; direct exit code 0, `verify: OK stages=5 11m54s`. `ui-install` skipped because the lockfile was unchanged. Build passed with 0 warnings and 0 errors. UI check passed: 64 files and 372 tests; the output included Node `DEP0190` and Vitest's non-failing isolation performance advisory. MCP passed 496, skipped 4 (500 total). Core passed 4,621, skipped 11 (4,632 total). Working-tree format passed for 3 changed C# files. Raw stage logs and the console summary are archived in [final gate evidence](evidence/101/gate-final/).

The earlier two failed gates remain documented above as historical pre-fix runs. The demonstrated test-helper sharing race was reviewed and corrected before this passing gate. The disposable test home remains under `D:\temp\sqlharness-plan101-evidence-20261021\disposable-home`; it is stopped, isolated from the operator home, and absent from the tracked evidence archive.


## Final guidance fix, review and verification

The Sol low whole-branch review identified one Important omission: the annotation help needed to say explicitly that enabling annotations automatically enables the activity journal. The final fix added that sentence and a focused assertion while retaining the required original guidance. SettingsPage tests passed 28/28 and typecheck passed. The scoped Sol low review marked the finding and browser-value wording ADDRESSED, with no new Critical or Important issues. See [whole-branch review](evidence/101/whole-branch-review.md), [fix report](evidence/101/final-fix-report.md), [scoped approval](evidence/101/final-fix-review.md) and [Task 2 approval](evidence/101/task-2-review.md).

The final product tree then passed `just verify -LogDirectory D:/temp/sqlharness-plan101-verify-20261021-guidance-final`, direct exit 0, `verify: OK stages=5 10m20s`. The controller recovered the completed process record after the worker reached its usage limit. Build: 0 warnings/errors. UI: 64 files, 372 tests. Core: 4,621 passed, 11 skipped. MCP: 496 passed, 4 skipped. Working-tree format: 3 C# files passed. UI diagnostics remain disclosed; no pristine-output claim. The pre/post source/test patch SHA-256 matches: `D02013025E3FA30061BD0BEF7C985597E6B2BD766E0B0D4EDF79C0CE7FD88C87`. [Raw final-tree logs and console exit evidence](evidence/101/gate-guidance-final/). The earlier helper-fix gate remains historical evidence for its earlier tree. Linux and live databases were not exercised.

## Controller decisions

- Keep changes uncommitted pending separate authorization, as Task 2 explicitly requires; review working-tree diffs. Cost if wrong: later commit bookkeeping.
- Add minimal shared configuration validation after RED proved the invalid pair was accepted. The fix contract requires rejection despite its assumption that validation existed. Cost if wrong: relocate or revert the extra validation and retain API coverage.
- Investigate and minimally repair the test-helper error-marker publication only after demonstrating the sharing race, because it blocked both required gates. Cost if wrong: unrelated helper-test rework and additional verification.

The worktree and SDD ledger remain available because no commit or integration has been authorized. Automatic review rejected deletion of the isolated disposable home/config/token; the dashboard is stopped and those files remain outside the repository. No generated token is included in the evidence archive.

## Integration authorization

The owner selected option 1: commit and merge locally into `main`. Commit and integration evidence will be recorded after the operation. No push is authorized. The historical uncommitted-tree descriptions above refer to the verification state before this authorization.
