### Finding Verdicts

- **Omitted result-set count stays set-only** — ADDRESSED — `src/SqlHarness.Core/AgentOutputProjection.cs:1036,1050,1063-1064` now keeps `omittedResultSetCount` separate from aggregate `omittedItems`; the inventory projection returns the set-only value. The added budget regression asserts zero omitted sets alongside positive row/cell omissions (`tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1515-1540`).
- **Selector lower bound is on array items** — ADDRESSED — `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:911-923` removes a root `minimum` and sets `items.minimum` to zero. Fixed and request served-schema checks assert the item bound and absence of the root bound (`tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1423-1425`).
- **Legacy omitted/null selector inventory shape and selected set-only count** — Check: the fix regression serializes the no-selector inventory and verifies `omittedResultSetCount` plus all selector metadata remain absent when unset; the selected inventory includes zero set omissions while aggregate `omittedItems` remains positive (`tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1515-1540`).
- **Reported fix evidence** — Check: the appended fix report names the RED cases and GREEN focused run of 4/4, and records tools/list sizes of 28,630 bytes (annotations off), 32,238 fixed, and 32,713 request (annotations on). I did not rerun tests.

### New Breakage in the Fix Diff

- **Minor — final newlines regressed** — The diff marks missing final newlines in `src/SqlHarness.Mcp/McpResultAdapter.cs:549`, `tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs:1596`, `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:2171`, `tests/SqlHarness.Mcp.WorkflowHost/Program.cs:121`, and `tests/SqlHarness.Mcp.WorkflowHost/WorkflowModule.cs:540`. Restore final newlines if the running branch-format check confirms these remain.

### Out-of-Scope Observations

- None.

### Verdict

**Fix round:** All findings addressed, no new Critical/Important breakage.
