### Finding Verdicts

- **Minimum-budget CLI error identity loss** — ADDRESSED. The fallback now binary-searches Message and Hint lengths and serializes the original exit code and error object (`src/SqlHarness.Cli/Commands/Renderer.cs:193`, `src/SqlHarness.Cli/Commands/Renderer.cs:200`, `src/SqlHarness.Cli/Commands/Renderer.cs:205`). The added 4,096-byte test uses long fields and asserts the original `sql_execution_failed`/`sql` identity plus truncation (`tests/SqlHarness.Tests/Cli/AgentOutputTests.ErrorDiagnosticFloor.cs:13`, `tests/SqlHarness.Tests/Cli/AgentOutputTests.ErrorDiagnosticFloor.cs:28`, `tests/SqlHarness.Tests/Cli/AgentOutputTests.ErrorDiagnosticFloor.cs:32`).
- **MCP tiny-budget behavior was unverified** — ADDRESSED. The adapter now has the equivalent budget-fitting error path (`src/SqlHarness.Mcp/McpResultAdapter.cs:190`, `src/SqlHarness.Mcp/McpResultAdapter.cs:197`, `src/SqlHarness.Mcp/McpResultAdapter.cs:210`), and the added 4,096-byte test checks byte bounds, original code/phase, fields below the cell floor, and truncation (`tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:92`, `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:107`, `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:109`, `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:113`). The appended report names both focused test commands and reports Core/CLI 92/92 and MCP 50/50 green; I did not rerun tests.

### New Breakage in the Fix Diff

- **Important — truncation counts can underreport distinct clipped fields**: both budget-fitting paths combine the prior aggregate `errorOmitted` count and new Message/Hint clips with `Math.Max` (`src/SqlHarness.Cli/Commands/Renderer.cs:195`, `src/SqlHarness.Cli/Commands/Renderer.cs:226`, `src/SqlHarness.Mcp/McpResultAdapter.cs:192`, `src/SqlHarness.Mcp/McpResultAdapter.cs:215`). The prior count can include a clipped command or `Location.Path`, while the new count covers Message and Hint. When those are disjoint, `Math.Max` reports fewer omitted items than were clipped. The new tests only assert `omittedItems > 0` (`tests/SqlHarness.Tests/Cli/AgentOutputTests.ErrorDiagnosticFloor.cs:32`, `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:113`), so they do not catch this. Track already-clipped fields individually and report the union.

### Out-of-Scope Observations

None.

### Verdict

**Fix round:** Findings remain open — the two prior findings are addressed, but an Important truncation-count defect remains in the fix diff.
