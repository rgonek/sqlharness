# Plan 105 Task 2 bounded-budget evidence

Status: resolved in the Task 2 working tree; final task report: `.superpowers/sdd/105-artifact-diagnostic-evidence/task-2-report.md`.

The 4096-byte `section=statements` MCP failure was not a transport or stale-build problem. A fresh-build temporary file trace at the generic `McpToolHandlers.RunAsync` catch captured:

```text
SqlHarness.Core.SqlHarnessSafetyException: The output budget cannot fit an artifact statement.
AgentOutputProjection.ProjectCore -> AgentOutputProjection.Project -> McpResultAdapter.Adapt -> McpResultAdapter.AdaptAndCompleteAsync -> McpToolHandlers.RunAsync
```

Sizing at `detailLimit=int.MaxValue`: candidate 4,058 bytes, statement row 879 bytes, fixed section/evidence overhead 3,179 bytes. The adapter requires another 384-byte reserve, so the candidate exceeded 4096 by 346 bytes. The projection exception interrupted the adapter's existing detail fallback. The generic catch turned it into `internal_error`; `maxCellChars=0` then clipped the default message to empty.

The fix introduces a typed `ArtifactStatementProjectionException`; `McpResultAdapter` catches it only around one projection candidate and tries the next lower detail limit. If no candidate fits, the adapter returns its semantic `output_budget_too_small` render error. A 5,000-character exact-identity regression verifies this path without clipping identity. Temporary instrumentation and byte-size probes were removed after the cause was confirmed.

Regression and focused verification:

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Artifact_statement_that_cannot_fit_the_minimum_budget_returns_a_render_error"
# RED before the fix: SqlHarnessSafetyException from AgentOutputProjection.ProjectCore.

dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "StatementMetricsTests|ArtifactWriterTests|ArtifactReaderTests|AgentOutputTests|AgentOptimizationWorkflowTests|McpTokenBudgetTests"
# Passed: 218, Failed: 0, Skipped: 0

dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "StatementMetricsTests|ArtifactWriterTests|ArtifactReaderTests|AgentOutputTests|AgentOptimizationWorkflowTests|McpTokenBudgetTests"
# Passed: 59, Failed: 0, Skipped: 0
```

The narrowed MCP regression passes at both 4096 and 65536 bytes, and the true no-fit case returns `output_budget_too_small` / `render`. No live database was used.
