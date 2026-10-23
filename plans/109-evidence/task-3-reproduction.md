# Plan 109 Task 3 reproduction record

Run these commands from `D:\Dev\sqlharness\.worktrees\plan-109` in PowerShell. The frozen inputs and outputs are adjacent JSON files. Task 1 is an offline real-SDK test; it uses the workflow host's canned SQL results/plans and records zero database connection attempts. Task 2 consumes the Task 1 JSON and applies deterministic test-only projections. Neither task contacts a live database or tokenizer.

## Commands and observed outcomes

| Purpose | Command | Expected outcome | Observed outcome |
| --- | --- | --- | --- |
| Core prescribed filter | `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "AgentOptimizationWorkflowTests|McpTokenBudgetTests"` | Exit 0 and matching tests discovered; zero-test runs do not count as evidence. | Run on `92f7723`; exit 0, zero tests discovered. **N/A**, because the named classes are MCP-only; not counted as a pass. Recorded in SDD progress. |
| Task 1 SDK baseline | `$env:SQLHARNESS_109_CAPTURE_DIR='D:\Dev\sqlharness\.worktrees\plan-109\plans\109-evidence'; dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter FullyQualifiedName~Artifact_review_study_freezes_four_set_batch_revisions_and_late_assertions --logger 'console;verbosity=minimal'` | One focused SDK workflow test passes and writes `task-1-sdk-baseline.json`. | Passed 1/1 on the Task 1 review-fix tree; the refreshed retained capture was 16 calls, 5,345 request-wire bytes, 56,159 response-wire bytes, and 37,649 model-visible UTF-8 bytes. No continuation or omitted items. |
| Task 2 candidate study | `$env:SQLHARNESS_109_CAPTURE_DIR='D:\Dev\sqlharness\.worktrees\plan-109\plans\109-evidence'; dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore -p:SkipDashboardUi=true --filter FullyQualifiedName~Artifact_review_study_compares_deterministic_equivalent_evidence_renderers --logger 'console;verbosity=minimal'` | One projection test passes and writes `task-2-candidate-study.json` from the frozen Task 1 input. | Passed 1/1. The Task 1 JSON remained unchanged. |
| Task 2 review-fix coverage | `Remove-Item Env:SQLHARNESS_109_CAPTURE_DIR -ErrorAction SilentlyContinue; dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore -p:SkipDashboardUi=true --filter FullyQualifiedName~Artifact_review_study_ --logger 'console;verbosity=minimal'` | Both offline workflow tests pass. | Passed 2/2, 0 failed, 0 skipped on `f6044c8`. |
| MCP prescribed filter | `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "AgentOptimizationWorkflowTests|McpTokenBudgetTests"` | Matching tests are discovered and pass. | Passed 55/55, 0 failed, 0 skipped on review-fix revision `92f7723`; raw log is `.superpowers/sdd/109-artifact-review-workflow-study/mcp-focused.log`. Final Windows gate is separately coordinated on `f6044c8`. |
| Notes/evidence whitespace check | `git diff --check` | Exit 0. | Passed for Task 1 and Task 2 review-fix changes. Task 3 notes check is recorded by the implementing agent's final check. |

The final Windows gate was running on `f6044c8` when this record was prepared. Its result is coordinator-owned evidence and is not inferred here. No dotnet, npm, or executable test command was rerun for this notes-only task.

## Source inventory

- `src/SqlHarness.Core/AgentBenchmarkOutput.cs:67` — `AgentMeasureSetItem` has per-set CPU/elapsed/aggregate reads and operator summaries, but no per-table read map.
- `src/SqlHarness.Core/ArtifactReader.cs:42-65` — `ArtifactNamedMetrics` and `ArtifactMetricsSection` carry per-table reads for named variants; the current metrics response includes all variants.
- `src/SqlHarness.Core/ArtifactReader.cs:103-118` — saved statement metrics include variant, parameter set, matrix cell, statement ordinal/hash, nullable CPU/elapsed/DOP and top operators. The saved statement record has no repetition field.
- `src/SqlHarness.Core/ArtifactReader.cs:488-559` — plan selection supports exact variant, statement, parameter set and repetition selectors.
- `src/SqlHarness.Core/AgentOutputProjection.cs:36-43,751-847` — the current query projection returns a prefix of result sets, with a trailing omitted-set count; the captured query request has no result-set selector.
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:84-110` — query request currently accepts row, cell and byte budgets but no result-set selector.
- `src/SqlHarness.Core/MeasureParameterSets.cs:342-365` and `src/SqlHarness.Core/SetupSqlExecution.cs` — setup is a separate input executed per parameter-set session; no counter in this study attributes setup's overlapping work.

Source path line numbers are those inspected on `f6044c8`; ordinary line drift is expected as future plans land.
