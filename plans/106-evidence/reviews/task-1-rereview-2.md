### Finding Verdicts

- **Truncation counts underreported distinct clipped fields** — ADDRESSED. CLI now tracks Message, Hint, Location.Path, and command clipping independently, then counts each affected field once in both budget-fitting paths (`src/SqlHarness.Cli/Commands/Renderer.cs:145`, `src/SqlHarness.Cli/Commands/Renderer.cs:201`, `src/SqlHarness.Cli/Commands/Renderer.cs:232`). MCP carries explicit Message/Hint/Location.Path flags from redaction and clipping through both paths (`src/SqlHarness.Mcp/McpResultAdapter.cs:145`, `src/SqlHarness.Mcp/McpResultAdapter.cs:192`, `src/SqlHarness.Mcp/McpResultAdapter.cs:215`). The new regressions assert exact counts of 4 for CLI and 3 for MCP (`tests/SqlHarness.Tests/Cli/AgentOutputTests.ErrorDiagnosticFloor.cs:35`, `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:114`).

### New Breakage in the Fix Diff

None. The CLI reserves the UTF-8 byte count of `output.NewLine` in each budget check (`src/SqlHarness.Cli/Commands/Renderer.cs:141`, `src/SqlHarness.Cli/Commands/Renderer.cs:183`, `src/SqlHarness.Cli/Commands/Renderer.cs:213`, `src/SqlHarness.Cli/Commands/Renderer.cs:243`, `src/SqlHarness.Cli/Commands/Renderer.cs:255`). `OutputCaptureWriter` copies the wrapped writer's newline and forwards writes to it (`src/SqlHarness.Cli/Infrastructure/OutputCaptureWriter.cs:27`, `src/SqlHarness.Cli/Infrastructure/OutputCaptureWriter.cs:30`, `src/SqlHarness.Cli/Infrastructure/OutputCaptureWriter.cs:48`, `src/SqlHarness.Cli/Infrastructure/OutputCaptureWriter.cs:49`); the regression checks the captured output ends with that newline and stays under the byte ceiling (`tests/SqlHarness.Tests/Cli/AgentOutputTests.ErrorDiagnosticFloor.cs:27`, `tests/SqlHarness.Tests/Cli/AgentOutputTests.ErrorDiagnosticFloor.cs:28`).

### Out-of-Scope Observations

None.

### Verdict

**Fix round:** All findings addressed, no new Critical/Important breakage. The appended report names the focused Core/CLI and MCP commands and reports 92/92 and 50/50 green; I did not rerun tests.
