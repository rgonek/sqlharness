# Plan 101 gate blocker report

## Root cause

Both supplied full-gate logs fail `ProcessRunnerTests.PidPublicationError_ReportsDiagnosticsAndCleansUp` after 480 polls. Each shows a single parent PID line, `Publisher task: RanToCompletion (exit code 1)`, and empty child stderr. The focused original test passes in isolation.

The test publisher's outer `catch` made one `Add-Content` attempt to publish its `ERROR:` line. On Windows, the poller's `FileShare.ReadWrite` read handle can still conflict with PowerShell's append open because sharing permissions must agree in both directions. If the one marker append lands during that overlap, it fails with `IOException`; the publisher exits without the marker, so the waiter times out and loses the original failure diagnostics.

I confirmed the sharing behavior with a scratch stress probe: eight concurrent .NET readers repeatedly opened the same file using `FileShare.ReadWrite` while PowerShell attempted 200 `Add-Content` writes. The probe recorded 2,453 successful read opens, 10,764 sharing-related read failures, and all 200 append attempts failed with `IOException` while readers overlapped.

## Deterministic RED/GREEN

Added `PidPublicationError_RetriesErrorMarkerAfterTransientSharingViolation`. Its optional publisher barriers let the test wait until the real error catch is reached, hold `processes.pid` open with `FileAccess.Read`/`FileShare.Read`, then resume the publisher and release the lock after 250 ms. The lock permits the poller's shared reads while denying append writes.

Before the fix, the test failed as expected: it received `TimeoutException` after 480 polls, saw one PID line, and the publisher had exited with code 1. The focused original error test passed before the change, demonstrating why isolation did not expose the race.

The helper now retries only `IOException` for at most 50 attempts at 100 ms, records the original exception message before retries, appends that original message, and rethrows the saved original error after successful publication. Other append errors stop retries immediately and are surfaced with the original error. The deterministic regression then passed in 722 ms.

## Verification and scope

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~PidPublicationError_RetriesErrorMarkerAfterTransientSharingViolation --logger "console;verbosity=normal"` — passed, 1/1.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~SqlHarness.Tests.Auth.ProcessRunnerTests --logger "console;verbosity=minimal"` — passed, 9/9, including a repeat after formatting.
- Targeted `dotnet format SqlHarness.sln --no-restore --include tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs --verify-no-changes --verbosity quiet` — passed. The formatter first corrected the edited file's final newline; no other files were formatted.
- No full gate was run, as requested for this bounded diagnosis.


The initial source diff was limited to `tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs` (71 insertions, 3 deletions). Pre-edit snapshot: `ProcessRunnerTests.cs.base` (SHA-256 `ABDCC9749D23BA7991B6580B2C38632D7FDEA45B74620144DA3380098640CEB5`). Scratch sharing probe: `share-repro.ps1`.

## Round-one review correction

The review found that a non-`IOException` from marker `Add-Content` escaped the nested catch and replaced the saved publisher error. Added `PidPublicationError_PreservesOriginalWhenMarkerWriteFails` with a synthetic original failure and a deterministic marker-write failure: while the catch is paused, the test replaces the PID file with a directory, making PowerShell `Add-Content` fail with `UnauthorizedAccessException`.

RED evidence: before the correction, the test failed because captured stderr contained the marker-write access error but not `Original publisher failure.`

The helper now throws an `AggregateException` for non-`IOException` marker failures. Its message includes the original publisher error and the marker-write exception type/message; its inner exceptions retain both original and marker-write exceptions. The original error is therefore preserved and the marker failure remains visible. The test passed after this change.

Verification after the correction:

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~PidPublicationError_PreservesOriginalWhenMarkerWriteFails --logger "console;verbosity=normal"` — passed, 1/1.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~SqlHarness.Tests.Auth.ProcessRunnerTests --logger "console;verbosity=minimal"` — passed, 10/10.
- `dotnet format SqlHarness.sln --no-restore --include tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs --verify-no-changes --verbosity quiet` — passed after formatting the assigned file's final newline.
- No full gate was run.

The current source diff against the task branch base remains confined to `ProcessRunnerTests.cs` (121 insertions, 3 deletions). For round-one re-review, `ProcessRunnerTests.cs.round1-review.base` is the pre-fix snapshot (SHA-256 `F12EBAF18623BA7991B6580B2C38632D7FDEA45B74620144DA3380098640CEB5`); the current test file SHA-256 is `208088A61951F1D64DE4C97B8DAF7993FD6649438E2525E3988DF546D57618D9`.

## Round-two review

The scoped re-review marked the Important diagnostic-preservation finding **ADDRESSED** and found no new Critical or Important issues. The reviewer confirmed that a non-`IOException` marker-write failure reports both the original publisher error and the marker-write exception, with both retained as inner exceptions. The new regression deterministically asserts that captured stderr contains both messages. The 10/10 focused suite and targeted format result were reported by the implementer; the re-review itself did not rerun them. No full gate has run after this test-helper correction.
