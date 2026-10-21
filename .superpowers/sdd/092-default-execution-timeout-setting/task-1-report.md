# Task 1 report: core execution timeout configuration

Status: DONE

## Changes

- Added `ExecutionConfig` with `DefaultTimeoutSeconds` defaulting to 30 and hard bounds of 1 through 300 seconds.
- Added the initialized `SqlHarnessConfig.Execution` section. Existing JSON without `execution` retains the default; explicit `null` is rejected by `IsComplete`.
- Added loader validation at `execution.defaultTimeoutSeconds` with the message `Must be between 1 and 300.`
- Added focused coverage for missing-section defaults, custom parse/serialization round-trip, out-of-range values, inclusive bounds, and explicit null.
- Left writer implementation, atomic replacement, and permission handling unchanged; the existing round-trip and atomic-write tests remain in the focused suite.

## TDD evidence

- RED: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlHarnessConfigTests"` exited 1 because the new tests referenced the not-yet-defined `SqlHarnessConfig.Execution` API (expected missing-feature failure).
- RED follow-up: after the minimal schema and range check were added, the focused suite ran 48 tests and exposed that the explicit-null diagnostic did not name `execution`. Updated the object-section error message to include `execution`.
- GREEN: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlHarnessConfigTests"` exited 0: 48 passed, 0 failed, 0 skipped.
- `git diff --check` exited 0.
- `just verify` was started but stopped after the build stage at the root agent's direction; the full gate is assigned to Task 4 on the final product tree. No full-gate pass is claimed here.

## Review and concerns

The new instance property name is preserved; the default constant is named `DefaultExecutionTimeoutSeconds` to avoid a constant/property name collision. The range is enforced by parsing and validation, while normal object construction continues to use the default value. No other concerns found in this task scope.

## Files

- `src/SqlHarness.Core/SqlHarnessConfig.cs`
- `tests/SqlHarness.Tests/Journal/SqlHarnessConfigTests.cs`
- `.superpowers/sdd/092-default-execution-timeout-setting/task-1-report.md`
