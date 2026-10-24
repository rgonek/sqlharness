# Plan 105 Task 2 report

Status: implementation and focused verification complete; commit pending final self-review. No live database, merge, push, or full `just verify` was run; the controlling workflow still owns the full branch gate.

## Public saved-artifact contract

`ArtifactReader.ReadSection(..., "statements", ...)` now exposes bounded per-statement `objects` with `object`, `kind`, nullable `logicalReads`, nullable `lobLogicalReads`, and nullable `showplanActualScans`. `objectIoAvailability` is `available`, `partial`, or `unavailable`; incomplete counters remain null, and `omittedObjects` counts source and response omissions. Object lists remain within existing statements paging and byte projection.

The statements section carries `statisticsIoScanCountRuns`, one record per captured run (up to the source bound), with variant, parameter set, matrix cell, repetition, availability, per-table `STATISTICS IO ScanCount`, and `omittedTables`. The top-level availability and omitted-run count preserve diagnostics when the artifact has no statements. Metrics exposes `planDiagnosticDistributions` and `statisticsIoScanCountDistributions`, with explicit source availability and omitted distribution counts. Plan distributions are grouped by `variant`, parameter set, matrix cell, source scope, plan document ordinal, and plan statement index. Each numeric field reports availability, sample count, and min/median/max distribution; requested/granted/max-used grants are never summed. Sources are `sql-server-showplan-query-plan` and `sql-server-statistics-io`. Showplan `ActualScans` is retained separately and is never equated with `STATISTICS IO ScanCount`.

Public selectors remain the existing `variant`, `statement`, `parameterSet`, and `repetition` arguments for `section=plan`; descriptions state their execution behavior and clarify that metrics/statements return all saved source identities. Legacy artifacts normalize new values to unavailable/empty rather than zero. No raw XML or new public section was added.

## Regression and root cause

The offline Core regression `ArtifactReaderTests.Saved_numeric_diagnostics_keep_statement_io_and_run_distributions_source_scoped` uses two repetitions with differing grants, compile metrics, statement IO, Showplan scans, and separate STATISTICS IO counts. It asserts the source identities and field separation, and that legacy artifacts report unavailable.

The original 4096-byte MCP failure was traced in a fresh normal build. `McpToolHandlers.RunAsync` caught `SqlHarness.Core.SqlHarnessSafetyException: The output budget cannot fit an artifact statement.` The stack ran from `AgentOutputProjection.ProjectCore` through `AgentOutputProjection.Project`, `McpResultAdapter.Adapt`, and `AdaptAndCompleteAsync`. At `detailLimit=int.MaxValue`, the smallest statement candidate was 4,058 bytes (879-byte row plus 3,179 bytes of fixed section/evidence data); the projector also reserves 384 bytes, so the 4,442-byte candidate exceeded the 4,096-byte response budget by 346 bytes. The thrown exception stopped `McpResultAdapter` from trying its existing lower detail limits, where the nested evidence can be reduced while the exact identity stays intact. The generic catch then returned `internal_error`; with `maxCellChars=0`, `RedactError` clipped its default message to an empty string. A stale build did not explain the failure: the fresh build reproduced it and the temporary exception/stack log named the source.

The fix adds the typed `ArtifactStatementProjectionException` for this one projection fit condition. `McpResultAdapter` catches it inside the detail-candidate loop and continues to lower detail levels. If no candidate can fit, its existing minimum-budget path returns `output_budget_too_small` in phase `render`, rather than allowing a generic internal error. The new regression `McpTokenBudgetTests.Artifact_statement_that_cannot_fit_the_minimum_budget_returns_a_render_error` uses a 5,000-character exact variant identity to verify the true no-fit case remains an explicit render-budget error; identity is never clipped.

Two earlier projection changes handled required Task 2 response data: bounded object lists and nested run/diagnostic/table lists with omission accounting. They did not fix the MCP exception. Self-review moved top-operator selection outside the candidate search so failed candidate attempts cannot inflate omission counts, and added omitted counts/partial availability for capped metrics distributions. These changes are covered by the Core and MCP focused suites below.

## Verification

The TDD RED for the new saved numeric diagnostics test failed before implementation because `objectIoAvailability` was absent. After implementation, verification ran sequentially:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "StatementMetricsTests|ArtifactWriterTests|ArtifactReaderTests|AgentOutputTests|AgentOptimizationWorkflowTests|McpTokenBudgetTests"
# Passed: 218, Failed: 0, Skipped: 0

dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "StatementMetricsTests|ArtifactWriterTests|ArtifactReaderTests|AgentOutputTests|AgentOptimizationWorkflowTests|McpTokenBudgetTests"
# Passed: 59, Failed: 0, Skipped: 0

git diff --check
# exit 0
```

The narrowed MCP check also passed all three cases: `Compact_compare_keeps_partial_statement_case_and_artifact_reference` at 4096 and 65536 bytes, plus `Artifact_statement_that_cannot_fit_the_minimum_budget_returns_a_render_error`.

## Remaining scope

The full repository `just verify`, branch formatting gate, and independent task review remain for the controlling workflow. No Linux gate or live database was used.
