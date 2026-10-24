# Plan 103 final fix report

Scope: the two findings in `whole-review.md`, against starting revision `92cacf37` on `plan-103`. Product/test commit: `0ba460b9`. No database access, safety-boundary change, blanket limit increase, or agent-session report edit.

The statement response now fits hidden-work detail within the existing response budget. It keeps exact statement selectors, attribution and limitations, prefers complete names for retained entries, and increases a known `HiddenWorkOmitted` count by response-omitted entries. If a legacy row has null omission history, projection preserves null. Any name that is shortened carries `NameTruncated=true`. The 4096-byte response remains within the existing ceiling.

Compact statement coverage now tracks source omissions by variant, parameter set and matrix cell. A case with retained rows and source omissions is partial; an unaffected complete case remains complete, and a case with no available rows remains unavailable.

## Behavioral RED

With the four changed product files temporarily restored to `HEAD` and the new tests retained, the focused Core run exited 1. It discovered three cases: two failed for the expected behavior, one passed. The 4096-byte hidden-work case threw `SqlHarnessSafetyException: The output budget cannot fit an artifact statement`; the unaffected candidate case was reported `partial` instead of `complete`.

The real MCP SDK client/server regression exited 1 with the actual `sqlharness_artifact` response `status=error`, `code=internal_error` at the minimum budget. The old projection could not retrieve the statement.

Logs: `%TEMP%\plan103-red-core.log` and `%TEMP%\plan103-red-mcp.log`.

The test commands were the same focused commands listed under GREEN. The product files were first restored to `HEAD` for this controlled baseline run; the separate MCP response assertion observed the actual error above.

## Behavioral GREEN

The restored GREEN source files were byte-identical to the saved pre-RED sources. Because `Copy-Item` retained old timestamps, an intermediate incremental run used the RED assemblies; that failed output is stale-build evidence and is not treated as a product regression. Source timestamps were advanced, then both focused suites rebuilt and ran against the restored source.

Core command:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false -p:SkipDashboardUi=true --filter 'FullyQualifiedName~Statement_projection_budgets_hidden_work_and_counts_response_omissions|FullyQualifiedName~Compact_statement_evidence_scopes_omissions_to_the_affected_case' --logger 'console;verbosity=minimal'
```

Result: exit 0; 3 passed, 0 failed, 0 skipped. Log: `%TEMP%\plan103-rebuilt-core.log`.

MCP command:

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false -p:SkipDashboardUi=true --filter FullyQualifiedName~Served_artifact_statement_keeps_identity_and_hidden_work_omissions_at_minimum_and_default_budgets --logger 'console;verbosity=minimal'
```

Result: exit 0; 1 passed, 0 failed, 0 skipped. The test calls the served MCP tool through the SDK client at 4096 and default 65536 bytes, with 16 distinct long escaped/multibyte names; it checks row identity, hidden-work omissions, execution attribution and limitations. Log: `%TEMP%\plan103-rebuilt-mcp.log`.

`git diff --check` exited 0. The seven changed C# files have no final newline, per repository formatting convention. The focused runs are feature-branch evidence, not the Windows gate. The controller owns the scoped Sol low re-review and `just verify`; Linux and live database behavior remain unverified.

## Changed files

- `src/SqlHarness.Core/AgentOutputProjection.cs`
- `src/SqlHarness.Core/AgentBenchmarkOutput.cs`
- `src/SqlHarness.Core/ArtifactReader.cs`
- `src/SqlHarness.Core/StatementMetrics.cs`
- `tests/SqlHarness.Tests/ArtifactReaderTests.cs`
- `tests/SqlHarness.Tests/StatementMetricsTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpParameterBatchTests.cs`
