# Plan 104 + 105 integration review

Review scope: prepared integration commit `d2f2b060`, feature parent `e5c9ed70`, main parent `9718b052`. I read the integration brief/report, exact conflict resolution diff, complete product diff in passes, prior Plan 104 final rereview, Plan 104/105 contracts, and retained focused logs. This is an independent spec and quality review of the integration; no source, index, branch, or test changes were made.

## Spec Compliance

- ✅ **Spec compliant for the reviewed integration.** The resolved catalog keeps Plan 104’s per-case counters, run limits, and SQL Server IO pointer alongside Plan 105’s bounded diagnostics, selectors, defaults, page-size bounds, continuation, and clipping descriptions (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:711-720`). Capabilities contract 9 carries the full IO rule only for SQL Server; PostgreSQL omits the nullable field (`src/SqlHarness.Core/Capabilities.cs:95-105`). The schema conflict resolution keeps the context, hash, counter-scope, selector, and catalog-budget assertions (`tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1318-1350`).
- ⚠️ **Cannot verify from this review:** the combined Windows gate is controller-owned and remains in progress according to the controller update; I did not rerun it. The post-merge branch format was reported passed by the controller, but I did not independently rerun formatting. Linux, installed-binary behavior, and live database acceptance remain outside this offline integration review.
- The accepted bare `#` plus eight hexadecimal characters rule remains explicitly experimental and is documented as lacking a Microsoft cross-version guarantee (`src/SqlHarness.Core/Capabilities.cs:95-105`); I relied on the accepted brief and prior Plan 104 review for this unchanged producer decision.

## Strengths

- Typed producer identity is classified before temp-name normalization and keeps the aggregate distinct from an object literally named `table variables`; repeated-run distributions and collision labels are covered in behavior-level tests (`src/SqlHarness.Core/Diagnostics.cs:8-70`, `tests/SqlHarness.Tests/BenchmarkSummaryTests.cs:11-145)).
- The SQL Server-only pointer is appended to measure/compare descriptions, while fixed-scope capabilities use the resolved engine and request-scope capabilities use SQL Server; the PostgreSQL serialization test asserts the field is absent (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:852-870`, `src/SqlHarness.Mcp/McpOperationMapper.cs:145-195`, `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1844-1890)).
- The budgeted annotation catalog assertion, schema suite, Core artifact/capabilities/IO suite, and MCP token/output/workflow suite have clean retained logs: 1/1 (`plans/104-evidence/integration/logs/mcp-annotation-budget-final2.log:10`), 65/65 (`.../mcp-schema-suite-final.log:10`), 288/288 (`.../core-artifact-capabilities-io.log:10`), and 117/117 (`.../mcp-output-token-workflow.log:10`). These are focused integration checks, not a substitute for the pending combined gate.
- Legacy artifact reads preserve stored names and distributions, and dashboard/export consumers no longer infer table kind from saved display strings (`tests/SqlHarness.Tests/ArtifactReaderTests.cs:482-510`, `src/SqlHarness.Dashboard/ui/src/lib/tableIoDelta.ts:1-16`).

## Issues

### Critical (Must Fix)

None.

### Important (Should Fix)

None.

### Minor (Nice to Have)

- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:711-713`, `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1332-1345`: the merged measure/compare descriptions say diagnostics are optional and mention `artifactId`/the `statements` section, but they drop the explicit `sqlharness_artifact` and `metrics` retrieval hint that was in Plan 105’s main-side wording. The artifact section argument still describes its metrics and statements, so the interface remains discoverable; restoring a compact route hint would make optional diagnostics easier to find. Keep it within the existing catalog budget.

## Assessment

**Task quality:** Approved

**Reasoning:** The integration preserves both plans’ runtime and schema contracts, and the conflict-specific behavior is covered by passing retained tests. One small discoverability hint was shortened; it does not block the integration. The combined Windows gate remains a separate controller check.
