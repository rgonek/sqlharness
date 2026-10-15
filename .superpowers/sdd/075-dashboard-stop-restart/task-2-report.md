# Task 2: Safe lifecycle client and bounded stop confirmation

Status: DONE

## Changes

- Added `DashboardStopStatus`, `DashboardStopResult`, and `DashboardLifecycle.StopAsync` with one shared timeout for endpoint discovery, guarded HTTP shutdown, and lock/process confirmation.
- The client validates the published PID and start time, proves a missing endpoint is stopped by acquiring the home lock, and retries held or unpublished state until the deadline.
- Shutdown uses one `POST` to the validated `127.0.0.1` port, with the persistent dashboard cookie, `X-SqlHarness-Dashboard: 1`, JSON `{}` content, redirects and proxies disabled, and no token in the URL or returned diagnostics.
- The client confirms the home lock is released and the captured process identity is gone before returning `Stopped`. It detects a changed endpoint, reports older endpoints with manual-stop guidance, propagates caller cancellation, and never kills a process or deletes replacement state.
- Added lifecycle tests using isolated temporary homes, loopback test servers, and a real `DashboardHost` that publishes its own endpoint.

## RED evidence

The first syntax-correct test-first run reached compilation and failed because `DashboardLifecycle` and `DashboardStopStatus` did not exist yet, as expected.

Command:

```text
dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~DashboardLifecycleTests
```

Output included `CS0103` for the missing `DashboardLifecycle` and `DashboardStopStatus` symbols; exit code 1.

## GREEN evidence

Final lifecycle, lock, and host focused run:

```text
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~DashboardLifecycleTests|FullyQualifiedName~DashboardLockTests|FullyQualifiedName~DashboardHostTests"
```

```text
Passed! - Failed: 0, Passed: 54, Skipped: 3, Total: 57, Duration: 15 s - SqlHarness.Tests.dll
```

The 3 skipped tests are Unix-only permission checks on this Windows run. The lifecycle group accounts for 15 passing cases, including the actual host publication and stop, lock confirmation, stale PID, held/corrupt metadata timeout, 404/405 guidance, 401/connection/timeout failures, replacement protection, transport headers/body, redirect refusal, proxy configuration, token-safe errors, and cancellation.

## Files changed

- `src/SqlHarness.Dashboard/DashboardLifecycle.cs`
- `tests/SqlHarness.Tests/Dashboard/DashboardLifecycleTests.cs`
- `.superpowers/sdd/075-dashboard-stop-restart/task-2-brief.md`
- `.superpowers/sdd/075-dashboard-stop-restart/task-2-report.md`

## Review follow-up

Added dedicated coverage for a stale published start time and for a lost shutdown response followed by independently confirmed lock release and process exit. The existing `Self_only_reader_cannot_claim_captured_process_stopped` test covers inability to verify a non-self PID.

Final focused lifecycle command:

```text
dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~DashboardLifecycleTests
```

Final output:

```text
Passed! - Failed: 0, Passed: 17, Skipped: 0, Total: 17, Duration: 4 s - SqlHarness.Tests.dll
```

Whole-repository verification is assigned to the controller after task integration.

## Concerns

No known implementation concern. The two test coverage gaps are listed above for the controller's review.
