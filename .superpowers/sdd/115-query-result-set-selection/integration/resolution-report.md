# Plan 115 merge resolution report

## Scope and merge state

- Local merge: `main` (`621a25210fc3338dcea90392895ac62957d8f5f0`) into `plan-115` (`0e2403328243098dd41f17ec12e91b74e84b875d`).
- No push or `main` ref update was performed. This is a merge candidate awaiting the integrated Windows gate and transfer to `main`.
- No live database, publication, or other branch merge was performed.

## Conflict decisions

- `plans/README.md`: kept Plan 114's completed history entry and removed its stale pending entry. Kept Plan 115 as implementation-complete on `plan-115` with the feature-branch evidence, and described this merge as a candidate pending the integrated gate and transfer. This avoids claiming that the local `main` ref has already moved.
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`: combined Plan 115's `resultSetOrdinals` request/response semantics and existing query dry-run guidance with Plan 114's statement-summary artifact contract and Plan 104's table IO evidence guidance. Preserved Plan 104's catalog-size reduction intent and main's `ToolsListRegressionTestBudgetBytes` 36 KiB regression-test ceiling. The value remains test-only; no runtime limit was raised.
- The served measure/compare text retains Plan 114's statement-evidence and saved-artifact retrieval guidance, using the current main wording. The artifact description retains Plan 114's `statement-summary` fields and availability semantics, Plan 104's IO identity caveats, and Plan 115's query-selection description.

## Auto-merge review

- Reviewed the query projection and request handler path: `AgentQuerySelection`, post-execution ordinal validation, source-order selection, source counts, ordinals, query hash, and existing omission/clipping projection remain present after the auto-merge.
- Confirmed `McpToolSchemaTests` retains Plan 115's selector schema, dry-run, missing/error evidence, and Unicode budget tests alongside Plan 114's `statement-summary` schema and pagination fixtures.
- Confirmed the main-side test rename to `Tools_list_fits_the_36KiB_regression_test_ceiling` and corresponding references are retained.
- Reviewed the main-side statement-summary projection branch in `AgentOutputProjection`; it paginates complete statement identities, counts skipped records, and advances continuation without fabricating a partial identity.
- No auto-merge semantic defect was found in these inspected paths. Broader product verification is delegated to the controller's full Windows gate.

## Focused verification

- Command: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpTokenBudgetTests|FullyQualifiedName~AgentOptimizationWorkflowTests" --logger "console;verbosity=minimal"`
- Result: passed, 142 passed, 0 failed, 0 skipped (1 minute 7 seconds). Covers the MCP schema, token budget, Plan 115 selector workflow, and Plan 114 statement-summary cases in those classes.
- Format command: `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 621a25210fc3338dcea90392895ac62957d8f5f0`
- Direct final result: `format: OK` for 8 changed C# files. The first attempt caught the catalog file's repository-specific EOF convention (`insert_final_newline = false`); it was corrected and the direct branch check passed.
- The unstaged resolution diff passed `git diff --check`. The complete staged merge reports trailing whitespace in four blank lines of the imported `plans/114-evidence/integration/verification-attempt-1/build.log`; this raw main-branch evidence was retained unchanged.

## Remaining verification

The full Windows `just verify` on the merged candidate remains for the controller. This report does not claim integrated-gate success or transfer to `main`.
