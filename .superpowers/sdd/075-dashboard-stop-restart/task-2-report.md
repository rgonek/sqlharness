# Task 2: Safe lifecycle client and bounded stop confirmation

Status: DONE

## Changes

- Added `DashboardStopStatus`, `DashboardStopResult`, and `DashboardLifecycle.StopAsync` with one shared timeout for endpoint discovery, guarded HTTP shutdown, and lock/process confirmation.
- The client validates the published PID and start time, proves a missing endpoint is stopped by acquiring the home lock, and retries held or unpublished state until the deadline.
- Shutdown uses one `POST` to the validated `127.0.0.1` port, with the persistent dashboard cookie, `X-SqlHarness-Dashboard: 1`, JSON `{}` content, redirects and proxies disabled, and no token in the URL or returned diagnostics.
- The client confirms the home lock is released and the captured process identity is gone before returning `Stopped`. It detects a changed endpoint, reports older endpoints with manual-stop guidance, propagates caller cancellation, and never kills a process or deletes replacement state.
- Lifecycle POSTs include the captured PID and UTC start-time ticks. The receiver checks both against its current process identity before scheduling shutdown; direct authenticated POSTs without identity headers retain the Task 1 contract.
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

The 3 skipped tests are Unix-only permission checks on this Windows run. This was the initial implementation run; review follow-up below adds the receiver identity check, an actual reused-port replacement, and retained stale PID/start-time cases.

## Files changed

- `src/SqlHarness.Dashboard/DashboardLifecycle.cs`
- `src/SqlHarness.Dashboard/DashboardSecurity.cs`
- `src/SqlHarness.Dashboard/DashboardServer.cs`
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

## Review fix round 1

The reused-port replacement test failed before the receiver identity check was added: the replacement accepted HTTP 202 while the test expected HTTP 409.

RED command:

```text
dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~Reused_port_replacement_rejects_captured_identity_and_remains_live
```

RED output:

```text
Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1
Expected: Conflict
Actual:   Accepted
```

The stale PID and stale start-time tests now keep the lock held and the stale endpoint published for the full call, then assert failure by deadline and no request at the raw endpoint.

Final focused command:

```text
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~DashboardLifecycleTests|FullyQualifiedName~DashboardServerTests|FullyQualifiedName~DashboardHostTests|FullyQualifiedName~DashboardLockTests"
```

Final output:

```text
Passed! - Failed: 0, Passed: 90, Skipped: 3, Total: 93, Duration: 13 s - SqlHarness.Tests.dll
```

The 3 skipped tests are Unix-only permission checks on this Windows run. The reused-port test verifies HTTP 409 and then confirms the replacement server remains live with its authenticated `/api/agents` route.

## Concerns

No known implementation concern. Whole-repository verification remains with the controller after task integration.
