# Plan 105 Task 3 — workflow retrieval evidence

Base: `14be2826535e454176c2bd1e47edfce16e39357f` (`HEAD` before Task 3).

## RED / GREEN

- Initial RED: the synthetic workflow had no `MemoryGrantInfo`, `CompileCPU`, `DegreeOfParallelism`, or runtime statement counters. The artifact could not provide the requested diagnostic values; this was a fixture gap, not a production extraction or projection defect.
- Review-fix RED: after adding assertions for both variants, baseline ordinal 0 returned the same requested grant as before (`Actual: 100`) instead of the expected baseline value (`Expected: 240`). The fixture had been generating identical diagnostics for baseline and candidate.
- GREEN: the dedicated `artifact-diagnostic-evidence` scenario now emits a memory-heavier, lower-compile baseline and lower-memory, higher-compile candidate. The workflow reads all four repetition-1 statement records for both variants at both budgets, asserts source identities and all requested/granted/max-used grant values plus compile time/CPU, and passed without database connections. The original artifact-review and frozen portable fixtures remain unchanged.

## Measured retrieval

`Artifact_diagnostics_support_offline_memory_tradeoffs_and_multi_statement_io_at_both_budgets` measured MCP text response UTF-8 bytes:

| Section | Budget | Response bytes | Pages |
|---|---:|---:|---:|
| metrics | 65,536 (default) | 7,491 | 1 |
| metrics | 4,096 (minimum) | 3,063 | 1 |
| statements | 65,536 (default) | 35,299 aggregate | 8 |
| statements | 4,096 (minimum) | 22,835 aggregate | 8 |

Each statement page used `pageSize=1`, returned within its individual byte budget, and advanced its continuation. The four baseline and four candidate statements at repetition 1 retained ordinals 0–3 and object identities `[dbo].[Orders]`, `[dbo].[Orders]`, `[dbo].[Customers]`, `[dbo].[Payments]`. Each had `objectIoAvailability=available`; the test checks exact logical/LOB reads, separate Showplan `ActualScans`, and source labels. STATISTICS IO uses `sql-server-statistics-io`; Showplan diagnostic and `ActualScans` values use `sql-server-showplan-query-plan`.

The baseline shows requested/granted/max-used memory of 240/200/160 KB and compile time/CPU of 5/2 ms for ordinal 0. The candidate shows 120/90/60 KB and 20/10 ms respectively. Ordinals 1–3 assert the corresponding exact per-statement formulas too, proving the workflow distinguishes the lower-memory, higher-compile candidate from the baseline.

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
- Review-fix RED command: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Artifact_diagnostics_support_offline_memory_tradeoffs_and_multi_statement_io_at_both_budgets" --logger "console;verbosity=normal"` — failed 1/1 at the new baseline grant assertion, expected 240, actual 100.
- Review-fix GREEN command: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Artifact_diagnostics_support_offline_memory_tradeoffs_and_multi_statement_io_at_both_budgets" --logger "console;verbosity=detailed"` — passed 1/1. Captured output: `metrics budget=65536 responseUtf8Bytes=7491`; `metrics budget=4096 responseUtf8Bytes=3063`; `Offline diagnostic retrieval: metrics UTF-8 bytes min/default=2963/7443; statements min/default calls=8/8, response bytes=22835/35299; all four statement identities for both variants and grant/compile source values matched.`
- `git diff --check` — passed before final report creation.
- The full Windows verification gate and branch formatting remain assigned to the controller for the final reviewed tree. Linux and live database acceptance were not run.
