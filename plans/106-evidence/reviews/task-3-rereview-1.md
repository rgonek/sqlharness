### Finding Verdicts

- **MCP capability omits the tiny-budget preservation guarantee** — ADDRESSED. The shared capability description now says tiny byte budgets preserve the error code and phase and report omitted content in truncation (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:695`); the served-capabilities regression asserts both phrases in fixed and request-scope modes (`tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1261`).

### New Breakage in the Fix Diff

- Minor — `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:695`: the shortened capability description replaces the explicit `connectionTimeoutSeconds` and `defaultExecutionTimeoutSeconds` listing with the generic word “timeouts.” The latter name appears later in the description, but the connection-timeout field is less discoverable in tools/list. Consider preserving the field names if the existing catalog ceiling allows it.

### Out-of-Scope Observations

None.

### Verdict

**Fix round:** All findings addressed, no new Critical or Important breakage. One Minor description-discoverability regression is recorded above.

The appended report names the covering schema and tools/list budget tests and gives their results. No tests or Git commands were rerun for this review. The diff's remaining C# changes are formatter final-newline corrections.
