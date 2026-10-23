# Plan 105 Task 1: persisted diagnostic shape

Base: `3365166212f4e6a5767e3fe2bec3f838faec5fe0`

Task branch: `plan-105` in `.worktrees/plan-105`

Scope: offline extraction, statement artifact persistence, and offline artifact read. No live database.

## Frozen source and value contract

Persisted plan evidence keeps one record for each source `QueryPlan`; values are not combined across plan documents or copied from a run aggregate onto a statement.

| Field | Unit | Source | Aggregation and absence |
|---|---|---|---|
| `degreeOfParallelism` | workers, count | `QueryPlan@DegreeOfParallelism` | Direct value from the source query plan. |
| `grantRequestedKb` | KB | `MemoryGrantInfo@RequestedMemory` | Direct value from the source query plan; no summing across documents or statements. |
| `grantGrantedKb` | KB | `MemoryGrantInfo@GrantedMemory` | Direct value from the source query plan; no summing across documents or statements. |
| `grantMaxUsedKb` | KB | `MemoryGrantInfo@MaxUsedMemory` | Direct value from the source query plan; no summing across documents or statements. |
| `compileTimeMs` | milliseconds | `QueryPlan@CompileTime` | Direct query-plan value; no cross-document or cross-statement sum. |
| `compileCpuMs` | milliseconds | `QueryPlan@CompileCPU` | Direct query-plan value; no cross-document or cross-statement sum. |

Each record carries `source=sql-server-showplan-query-plan`, scope (`statement` or `batch`), the plan document ordinal, and the statement index when the source is inside `StmtSimple`. Statement-owned records are attached to the corresponding statement row. Batch-owned records remain in the statements section with variant, parameter set, matrix cell, and repetition identity. A source with a missing numeric attribute keeps that field `null`; a measured zero remains `0`. Run-level availability cases distinguish PostgreSQL plans (unavailable for these SQL Server fields) from SQL Server query plans whose individual fields are absent.

Statement IO remains a separate contract for Task 2. The current extractor reads `ActualLogicalReads`, `ActualLobLogicalReads`, and `ActualScans` from `RunTimeCountersPerThread`, sums thread counters within an operator, then combines operators for the same object in the statement. Logical and LOB reads remain separate. `ActualScans` remains its own Showplan counter and is not described as equivalent to SQL Server `STATISTICS IO ScanCount`. Missing/unavailable values remain distinct from zero. UDF/TVF evidence keeps the existing partial-coverage classification.

The existing journal `PlanMetricsExtractor.Extract(IEnumerable<string>)` aggregate behavior was left unchanged. The artifact path uses a source-preserving extractor and does not call that multi-document combine path for saved evidence.

## Regression and verification

The offline fixture writes and reads an artifact with two statements carrying different grants, explicit zero and absent metrics, a second plan document, repeated parameter-set runs, UDF and TVF partial attribution, a batch-scoped fallback, and a PostgreSQL plan. It asserts source, units via field names, exact values, scope, and identities after `ArtifactReader.ReadSection`.

RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~Statements_section_persists_source_scoped_plan_diagnostics_for_offline_reads"` failed with `KeyNotFoundException` for the missing `planDiagnostics` field after successfully writing and reading the artifact. An earlier fixture attempt lacked a required `RelOp`; that failure was corrected before accepting the RED evidence.

Focused baseline on the clean task tree:

- Core: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "StatementMetricsTests|ArtifactWriterTests|ArtifactReaderTests|AgentOutputTests|AgentOptimizationWorkflowTests|McpTokenBudgetTests"` — 215 passed, 0 failed.
- MCP: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "StatementMetricsTests|ArtifactWriterTests|ArtifactReaderTests|AgentOutputTests|AgentOptimizationWorkflowTests|McpTokenBudgetTests"` — 58 passed, 0 failed.

GREEN after implementation, on the final Task 1 tree:

- Core focused suite — 216 passed, 0 failed.
- MCP focused suite — 58 passed, 0 failed.
- `git diff --check` — exit 0.

No live DB, merge, push, install, full `just verify`, or branch-format run was performed. Those full-plan checks remain for the controlling implementation workflow. Task review is pending.

## Review fix round 1/5

The source extractor now validates the canonical SQL Server Showplan namespace on the `ShowPlanXML` root and diagnostic elements. Wrong-root and wrong-diagnostic-namespace fixtures remain unavailable in both extraction and artifact projection.

RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~Noncanonical_showplan_namespaces_do_not_produce_sql_server_diagnostics"` failed because wrong-namespace XML yielded RequestedMemory=8, GrantedMemory=6, MaxUsedMemory=5, CompileTime=9, and CompileCPU=4.

GREEN command: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~StatementMetricsTests"`

GREEN result: `Passed!  - Failed: 0, Passed: 115, Skipped: 0, Total: 115, Duration: 11 s - SqlHarness.Tests.dll (net10.0)`.

The review's Minor about direct assertions for DOP, maximum grant usage, and compile CPU remains deferred to final review per controller direction.
