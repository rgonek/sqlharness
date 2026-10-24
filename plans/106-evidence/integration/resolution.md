# Plan 106 integration conflict resolution

## Merge context

- Feature branch: `plan-106` at `11aeb149` (`docs(plan-106): close verified implementation and archive evidence`).
- Incoming `main`: `33651662`.
- Merge base: `6d543e509734a374caf882bc8c05ffe5ccaa98b2`.
- Merge is in progress without a merge commit. This report records resolution and focused checks only; the controller owns the integrated full gate and final review.

## Resolution decisions

- `plans/README.md`: retained plan 106's completed-work entry and all incoming main entries for plans 103, 107, and 108. Updated plan 106's branch-status wording to say local integration is in progress and it has not been pushed.
- `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs`: retained plan 106's `AssertCapabilitiesContractDiscoverable(requestTools)` coverage for connection timeout, error diagnostic floor, path clipping, and tiny-budget truncation. Kept one request-scope tools/list byte measurement and bound it by `McpLimits.ToolsListBudgetBytes`, deduplicating main's equivalent literal `32768` assertion. Both branches were measuring the same serialized catalog, so the shared assertion preserves both contracts without duplicate measurements.
- Automatic merges were inspected for `McpResultAdapter`, request handlers, tool catalog, `ArtifactCommand`, and MCP runtime/artifact documentation. The shared error floor, redaction-before-clipping, tiny-budget fallback/error identity, and truthful truncation remain present. Main's artifact plan default, artifact-specific invalid-selection behavior, and CLI cell-limit help description remain present. No response or tools/list budget was raised and no public knob was added.

## Focused verification

- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpTokenBudgetTests"` — exit 0; 118 passed, 0 failed, 0 skipped. This includes the omitted-variant artifact default regression and tools/list budget/schema checks.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:IsTestProject=true --filter "FullyQualifiedName~AgentOutputTests"` — exit 0; 95 passed, 0 failed, 0 skipped.
- An initial concurrent invocation of these two test commands encountered a Roslyn shared-output file lock on `SqlHarness.Core.dll` (CS2012). After the MCP run completed, the Core/CLI command was rerun sequentially and passed; this was build contention, not a test failure.
- `git diff --check` on the resolved paths exited 0, and a scan found no conflict markers in either resolved file.
- The integrated full Windows gate and final merge review remain controller-owned and are not claimed here.
