# Task 1 implementation report

## Outcome

Implemented classifier admissions for approved session-local and control-flow constructs on read-only `Query` and `CompareSetup` paths. Mutation usage and `Query` with `allowMutation=true` retain their planning-base classifications. `SqlSafetyDecision.Notices` now carries the exact isolation notices requested by the plan.

## TDD evidence

Planning-base characterization was run before production edits. The focused suite first went red after adding the admission tests:

```text
Command: dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafety" --no-restore
Result: Failed: 43, Passed: 527, Skipped: 0, Total: 570
```

The red failures were the newly added read-only admission expectations. The controller's full baseline gate also ran before production edits: build and UI passed; Core reported 4,207 passed and 11 skipped, plus the 29 expected failures from the newly compiled `SqlSafetyControlFlowTests`; MCP reported 426 passed and 4 skipped. The gate exit was 1 because that baseline test run included the intentionally red tests.

Before implementation, the mutation characterization test recorded the planning-base fingerprints. For all admitted samples, both `SqlUsage.Mutation` and `SqlUsage.Query` with `allowMutation=true` remained `False/UnsupportedStatement/False/False/ReadOnly`, except local table-variable and local `#temp` CTE updates, which remained `True/Allowed/True/True/Dml`. The `EXEC dbo.P` sentinel remained `True/Allowed/True/False/Opaque` for Mutation and `False/UnsupportedStatement/False/False/ReadOnly` for the query mutation-enabled path. The new exact assertions continue to pass.

Final focused GREEN run:

```text
Command: dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafety" --no-restore
Output:
  SqlHarness.Core -> D:\Dev\sqlharness\.worktrees\plan-081\src\SqlHarness.Core\bin\Debug\net10.0\SqlHarness.Core.dll
  SqlHarness.Mcp -> D:\Dev\sqlharness\.worktrees\plan-081\src\SqlHarness.Mcp\bin\Debug\net10.0\SqlHarness.Mcp.dll
  SqlHarness.Dashboard -> D:\Dev\sqlharness\.worktrees\plan-081\src\SqlHarness.Dashboard\bin\Debug\net10.0\SqlHarness.Dashboard.dll
  SqlHarness.Cli -> D:\Dev\sqlharness\.worktrees\plan-081\src\SqlHarness.Cli\bin\Debug\net10.0\sqlharness.dll
  SqlHarness.Tests -> D:\Dev\sqlharness\.worktrees\plan-081\tests\SqlHarness.Tests\bin\Debug\net10.0\SqlHarness.Tests.dll
Test run for D:\Dev\sqlharness\.worktrees\plan-081\tests\SqlHarness.Tests\bin\Debug\net10.0\SqlHarness.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
Passed!  - Failed:     0, Passed:   582, Skipped:     0, Total:   582, Duration: 922 ms - SqlHarness.Tests.dll (net10.0)
```

## Changes

- Threaded a `readOnlyAdmissions` gate through statement classification. Only read-only Query and CompareSetup set it; the mutation classifier explicitly sets it false.
- Admitted the exact approved SET option list, non-distributed/unmarked transaction control, isolation level, loop and try/catch control flow, value-less RETURN, THROW, RAISERROR without LOG, PRINT, and scoped local cursors. Unsupported SET options, `WAITFOR`, `GOTO`, global/cursor-variable forms, `SCROLL_LOCKS`, `FOR UPDATE`, `RAISERROR ... WITH LOG`, and value-bearing RETURN remain denied.
- Allowed same-batch CTE UPDATE only when its single-source CTE chain resolves to a proven table variable or local temporary table. Joins, persistent targets, and other shapes remain outside this exception.
- Added the SERIALIZABLE and REPEATABLE READ notices, recursively including nested control flow. Their exact texts are `isolation level SERIALIZABLE in effect` and `isolation level REPEATABLE READ in effect`.
- Extended statement-node coverage and updated prior tests whose expectations intentionally changed. The admission tests assert read-only Query and CompareSetup behavior, persistent DML denial, notices, and planning-base mutation fingerprints.

## Node coverage and tier effects

The node coverage allowlist now explicitly accounts for the newly classified ScriptDom nodes: transaction commands, cursor declaration/operations, predicate and general SET, isolation SET, PRINT, THROW/RAISERROR, RETURN, BREAK/CONTINUE, TRY/CATCH, and WHILE. They are exempted only from the “unmapped node” omission assertion; existing mutation-map classification remains unchanged.

Approved SET/control-flow/transaction/cursor commands classify as session-local where they alter session state; output/control-flow-only nodes classify read-only. A CTE DML statement targeting a proven local table variable or local `#temp` retains DML risk tier and local-write flags. Persistent writes inside loops or TRY/CATCH remain denied by the existing mutation-not-allowed path. No admission changes persistent-mutation tiering.

## Handoff and boundaries

Parameterized CompareSetup prefix rejection is assigned to Task 2: the classifier has no parameter context, and Task 1 deliberately admits non-parameterized whole-setup transactions. Task 2 owns prefix enforcement and integration tests. The batch-timeout notice and connection cleanup/report warnings are also outside Task 1.

No auto-formatting was run. The full `just verify` gate is left to the controller after all plan tasks are integrated; this task ran the complete focused SQL safety filter only.
