### Re-review

- ✅ Addressed — the request-scope `tools/list` budget regression. Both served variant descriptions now use the shorter guidance, and the report records the focused schema test passing its 32,768-byte request-scope assertion (`src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs:194`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:388`). The exact new request-scope size is not reported.
- ✅ Selection guidance preserved: measure/measure-set defaults to `query`; compare needs `baseline` or `candidate` (`src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs:194`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:388`). The diff changes only descriptions; byte-budget and page-size range attributes remain (`src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs:207`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:401`).
- No new issues found. The reported focused MCP tests passed 109/109; I did not rerun them. The full gate remains pending the controller.
