# Task 4 report: measure per-case sessions

## Implementation

`measure` now opens one session per parameter set and processes sets sequentially. Setup binds to the current set, then its warm-up and all measured repetitions complete before the next session opens. The run report carries sequential `warmupOrder` and updated order/cache text. A failed set stops execution; a `MeasureCaseFailedException` retains only completed-set runs and the partial report. `SqlHarnessModule` returns those runs on the failed outcome with Plan 077 `MatrixFailedCase` coverage, and the MCP adapter reports the partial result with `error.matrixStop`. The existing post-bind `RecordConditions` callbacks and `ValueHash` projection remain intact.

Updated the MCP tool description, CLI help, capabilities, README and MCP tool contract. The report includes the per-case setup count and uses the observed server cache warning while describing separate sessions.

## TDD evidence

**RED command**

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~MeasureParameterSet|FullyQualifiedName~MeasureTests" --no-restore
```

**RED output:** `Failed: 8, Passed: 124, Skipped: 0, Total: 132`. The expected failures showed rotated order (`Expected [A,B,C,...], Actual [B,C,A,...]`), one connection where the test required one per set, and a null report on a second-set failure.

**GREEN command**

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~MeasureParameterSet|FullyQualifiedName~MeasureTests" --no-restore
```

**GREEN output:** `Failed: 0, Passed: 132, Skipped: 0, Total: 132`.

**MCP contract and partial-stop command**

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpToolSchemaTests.Tools_list_explains_optimization_evidence_semantics_in_both_modes|FullyQualifiedName~McpOutputTests.Partial_measure_keeps_coverage_on_error_matrix_stop" --no-restore
```

**Output:** `Failed: 0, Passed: 2, Skipped: 0, Total: 2`.

**Documentation contract command**

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~DocsSyncTests" --no-restore
```

**Output:** `Failed: 0, Passed: 6, Skipped: 0, Total: 6`.

`git diff --check` completed without output. The full `just verify` gate remains with the controller, as requested.

## Files changed

- `src/SqlHarness.Core/MeasureParameterSets.cs`
- `src/SqlHarness.Core/SqlHarnessModule.cs`
- `src/SqlHarness.Mcp/McpResultAdapter.cs`
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`
- `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs`
- `src/SqlHarness.Core/Capabilities.cs`
- `README.md`
- `docs/mcp/tools.md`
- `tests/SqlHarness.Tests/MeasureParameterSetExecutionTests.cs`
- `tests/SqlHarness.Tests/MeasureParameterSetReportTests.cs`
- `tests/SqlHarness.Tests/MeasureTests.cs`
- `tests/SqlHarness.Tests/Cli/MeasureParameterSetCommandTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpOutputTests.cs`
- `plans/079-optimization-evidence-core.md`

## Self-review and concerns

- Setup binding is asserted for both set values; the second-case failure test asserts completed-set runs, partial report, completed/failed/not-executed counts, and `error.matrixStop`.
- Credentials continue through the factory's known-secret overload and error text is redacted by the module.
- The final successful case session stays open only through the existing inventory and artifact capture path, then is disposed by the module. Earlier and failed-case sessions are disposed in the runner.
- No unresolved correctness concerns found. The full repository gate was not run by this task.
