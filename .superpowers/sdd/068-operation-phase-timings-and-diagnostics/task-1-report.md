# Task 1 report: Core phase recorder and timed session factory

Implemented the per-operation phase recorder and timed SQL session factory in the plan-068 worktree.

The recorder keeps its current instance in `AsyncLocal`, restores nested scopes, and aggregates phase count, total duration, and maximum duration under a lock. Event capture is optional, capped at 256 events, and tracks dropped events. Event detail is clipped to 512 characters. A disposed scope records `succeeded` or `failed` and can retain a provider error number.

`TimedSessionFactory` wraps both session factory overloads. Without a current recorder, it returns the inner factory task directly. With a recorder, it records connect duration, keeps the original exception and SQL error number where available, and captures only the connection ID, attempt number, and supported auth label. PostgreSQL uses `postgres`; SQL Server labels use the specified lowercase forms; Integrated auth omits `authStrategy`. `SqlClientSession` exposes the provider connection ID through the optional session property.

Setup timing surrounds each existing setup-command await. Execute timing surrounds benchmark execution and the direct query collector await. Artifact timing surrounds artifact publication and matrix-index publication. Setup's raw command execution is not separately marked Execute, preventing nested setup/execute double counting. Instrumentation failures during scope disposal and diagnostic detail handling do not change the operation result.

## TDD and verification evidence

RED: the initial focused run failed during test compilation because the requested `SqlHarness.Core.Journal` recorder and phase types were missing. This was the expected pre-implementation failure.

Final focused GREEN, on the final source tree:

```text
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~OperationPhaseRecorderTests|FullyQualifiedName~TimedSessionFactoryTests" --no-restore
Passed: 10, Failed: 0, Skipped: 0, Total: 10
```

Full Core suite, command `dotnet test tests/SqlHarness.Tests --no-restore`:

```text
Passed: 3520, Failed: 0, Skipped: 29, Total: 3549
Duration: 5 m 16 s
Exit code: 0
```

The full-suite process built and ran before the final auth-label and additional focused-coverage edits. The final focused run above rebuilt the updated Core and test projects and passed all 10 focused tests. No full-suite rerun was made after those diagnostic-label-only changes. The controller separately reported a clean-base run at `cf0e7c4`: 3514 passed, 29 skipped, 3543 total.

`git diff --check` passed. Global Windows/Linux gates were not run, as directed for Task 1.

## Focused coverage

- Aggregates count, total, and maximum duration per phase; disabled capture returns no events and restores `Current`.
- Concurrent async flows keep their recorders separate.
- Event capture respects the 256-event cap and reports dropped events; detail is clipped at 512 characters.
- Timed connect covers both overloads, success and `SqlException` failure, error-number reporting, diagnostic labels, session connection ID exposure, and pass-through with no recorder.

## Review fix round 1

Restricted detailed events to `Connect` and `Artifact`; aggregate counts and durations continue to record `Setup` and `Execute`. Added `Setup_and_execute_aggregate_without_consuming_event_cap`, which records more than 256 Setup and Execute scopes and then confirms Connect and Artifact still appear, both high-volume phases retain their aggregates, and `DroppedEvents` stays at zero.

RED command:

```text
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~OperationPhaseRecorderTests|FullyQualifiedName~TimedSessionFactoryTests" --no-restore
Failed: 1, Passed: 10, Skipped: 0, Total: 11
Expected events: Connect, Artifact; actual events began with Setup, Execute.
```

GREEN command on the corrected tree:

```text
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~OperationPhaseRecorderTests|FullyQualifiedName~TimedSessionFactoryTests" --no-restore
Passed: 11, Failed: 0, Skipped: 0, Total: 11
```

Covering files: `src/SqlHarness.Core/Journal/OperationPhaseRecorder.cs` and `tests/SqlHarness.Tests/Journal/OperationPhaseRecorderTests.cs`. `git diff --check` passed. No full suite or global gates were run for this review fix.
