# Plan 105 Task 3 — workflow retrieval evidence

Base: `14be2826535e454176c2bd1e47edfce16e39357f` (`HEAD` before Task 3).

## RED / GREEN

- RED: the first new offline workflow run failed because its synthetic Showplan contained no `MemoryGrantInfo`, `CompileCPU`, `DegreeOfParallelism`, or runtime statement counters. The artifact could not provide the requested diagnostic values; this was a fixture gap, not a production extraction or projection defect.
- GREEN: added a dedicated `artifact-diagnostic-evidence` synthetic workflow scenario with explicit Showplan and STATISTICS IO source data. The original artifact-review and frozen portable fixtures remain unchanged. The new workflow test passed at both byte budgets and made no database connections.

## Measured retrieval

`Artifact_diagnostics_support_offline_memory_tradeoffs_and_multi_statement_io_at_both_budgets` measured MCP text response UTF-8 bytes:

| Section | Budget | Response bytes | Pages |
|---|---:|---:|---:|
| metrics | 65,536 (default) | 7,506 | 1 |
| metrics | 4,096 (minimum) | 3,066 | 1 |
| statements | 65,536 (default) | 17,648 aggregate | 4 |
| statements | 4,096 (minimum) | 11,416 aggregate | 4 |

Each statement page used `pageSize=1`, returned within its individual byte budget, and advanced its continuation. The four candidate statements at repetition 1 retained ordinals 0–3 and object identities `[dbo].[Orders]`, `[dbo].[Orders]`, `[dbo].[Customers]`, `[dbo].[Payments]`. Each had `objectIoAvailability=available`; the test checks exact logical/LOB reads, separate Showplan `ActualScans`, and source labels. STATISTICS IO uses `sql-server-statistics-io`; Showplan diagnostic and `ActualScans` values use `sql-server-showplan-query-plan`.

Metrics had source-tagged SQL Server Showplan diagnostic distributions at both budgets. Exact candidate Orders STATISTICS IO `ScanCount` median was 5. At these budgets plan diagnostic distributions are partial and their omitted count is reported. `StatisticsIO` scan counts and Showplan `ActualScans` stay distinct.

## Contracts and limits

- Added assertions for the deferred Task 1 scalar mappings (`degreeOfParallelism`, `grantMaxUsedKb`, `compileCpuMs`) in the persisted artifact roundtrip.
- Added coverage for partial and unavailable IO versus measured zero, plus the 64-object bound and `omittedObjects` count.
- Existing `Artifact_statement_that_cannot_fit_the_minimum_budget_returns_a_render_error` and `Artifact_plan_adapter_reports_no_node_fit_when_one_source_node_exceeds_budget` provide the true item/node no-fit cases; both passed in the focused MCP run. This task did not duplicate those assertions.
- Diagnostic reads remain optional guidance for memory, compilation, and per-statement investigations; the tool descriptions do not require these reads for every optimization.
- No production runtime behavior change was needed. Task 3 adds workflow fixture/test evidence and updates artifact docs, tool descriptions, capability wording, and their contract tests.

## Focused verification

- Core: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "StatementMetricsTests|ArtifactWriterTests|ArtifactReaderTests|AgentOutputTests|AgentOptimizationWorkflowTests|McpTokenBudgetTests"` — passed 219/219.
- MCP: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "StatementMetricsTests|ArtifactWriterTests|ArtifactReaderTests|AgentOutputTests|AgentOptimizationWorkflowTests|McpTokenBudgetTests" --logger "console;verbosity=normal"` — passed 60/60.
- Workflow byte capture: the new test run at detailed verbosity reported the exact bytes above and passed 1/1.
- `git diff --check` — passed before final report creation.
- The full Windows verification gate and branch formatting remain assigned to the controller for the final reviewed tree. Linux and live database acceptance were not run.
