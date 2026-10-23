# Task 3 implementation report

## Result

Measure and compare MCP compact results now include optional `statementEvidence` projected from the `SqlHarnessOutcome.BenchmarkRuns` already in memory. The projection groups by variant, parameter set, and matrix cell, preserves partial and unavailable counts and limitations per case, and reports omitted statement/case totals when its bounded summary cannot represent all rows. Attribution describes coverage of collected statement counters only. Missing runs produce `unavailable`; they are not represented as zero or complete. Hidden UDF/TVF limitations come from the same statement projector and remain visible when counters reconcile.

The compact evidence is attached after the shared Core projection. `McpResultAdapter` computes it once per outcome before its budget/detail loop, so the compact response does not reopen artifacts or depend on journal retention. MCP tool descriptions and `docs/mcp/artifacts.md` explain the attribution boundary and how to retrieve exact saved statement identities through `sqlharness_artifact`. For matrices, the affected case's artifact reference identifies the saved cell.

## RED/GREEN

The offline SDK workflow test first failed with `coverageBasis` absent because the workflow fixture returned reports without passing its saved `BenchmarkRuns` into `SqlHarnessOutcome`. The fixture was corrected to carry those already-collected runs, after which the test verified partial evidence and statement retrieval through the real MCP SDK. It then exposed an existing identity-clipping issue at `maxCellChars=0` (the variant became empty); identity fields are now preserved, and the workflow follows artifact continuation to retrieve the exact candidate repetition and all its partial rows at both 4096-byte and 65536-byte budgets.

A Core regression test also verifies that a scalar-UDF limitation remains in the compact case even when its collected counters reconcile as complete. The artifact review workflow now reads all statement pages and checks the complete per-repetition identity sets for compare and named-set measure. Its current-contract scripted MCP golden records were recaptured after the response shape changed. The separate Plan 109 historical baseline was not modified.

## Verification

Required focused Core filter, with actual test discovery; passed 101/101:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore -m:1 -p:SkipDashboardUi=true --filter "StatementMetricsTests|ArtifactReaderTests|AgentOptimizationWorkflowTests|McpTokenBudgetTests" --logger "console;verbosity=minimal"
```

Output retained at `D:\temp\sqlharness-plan103-task3-core-focused-final.log`.

Required focused MCP filter, with actual test discovery; passed 57/57:

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore -m:1 -p:SkipDashboardUi=true --filter "StatementMetricsTests|ArtifactReaderTests|AgentOptimizationWorkflowTests|McpTokenBudgetTests" --logger "console;verbosity=minimal"
```

Output retained at `D:\temp\sqlharness-plan103-task3-mcp-focused-final.log`. An earlier run of this filter before refreshing the repeated-statement expectations failed 2/57: one assertion expected one row per statement rather than each repetition, and the other compared against the prior compact response goldens. Both were updated and the required final filter passed.

The offline budget/retrieval workflow also passed directly at both requested budgets (2/2), and the targeted UDF limitation test passed (1/1). `git diff --check` passed; Git emitted only its normal LF-to-CRLF working-copy notices for the refreshed JSON fixtures.

## Scope and remaining verification

No database was contacted. No artifact scan, storage diagnostic, grant, journal-retention dependency, full `just verify`, or branch formatter was added or run. The full gate and branch formatting remain with the controller after review. The collected-counter coverage label is not a total-cost or equivalence claim, and saved statement artifacts remain the source for the exact per-repetition detail.
