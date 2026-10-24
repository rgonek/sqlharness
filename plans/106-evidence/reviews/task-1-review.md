### Spec Compliance

- ❌ Issues found: the CLI error floor is applied independently of the total response budget. At a 4,096-byte budget, a long Message and Hint can each be clipped to the 2,048-character default, after which `WriteAgent` exhausts detail limits and substitutes `output_budget_too_small`/`render`, losing the original error code and phase (`src/SqlHarness.Cli/Commands/Renderer.cs:141`, `src/SqlHarness.Cli/Commands/Renderer.cs:175`, `src/SqlHarness.Cli/Commands/Renderer.cs:184`). This violates the binding tiny-budget behavior and makes the new floor able to trigger the generic fallback.
- ⚠️ Cannot verify from this diff: MCP behavior for tiny total budgets is not exercised by the new test, which uses 65,536 bytes (`tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:96`). The controller should cover preservation of error code/phase and truthful truncation in the shared-policy/edge task.

### Strengths

- The local change implements `max(requestedCellCharacters, DefaultCellCharacters)` for Message and Hint in both renderers, while keeping result cells and `Location.Path` on the caller limit (`src/SqlHarness.Cli/Commands/Renderer.cs:141`, `src/SqlHarness.Cli/Commands/Renderer.cs:155`, `src/SqlHarness.Cli/Commands/Renderer.cs:156`, `src/SqlHarness.Mcp/McpResultAdapter.cs:372`, `src/SqlHarness.Mcp/McpResultAdapter.cs:375`). MCP redacts before clipping in the changed adapter method.
- Synthetic tests cover connect, authentication, and SQL failures at 0, 200, default, and maximum cell limits. They assert actionable text, error code/phase, result/path bounds, and total response bounds; the MCP case also checks credential removal across the old clipping boundary (`tests/SqlHarness.Tests/Cli/AgentOutputTests.ErrorDiagnosticFloor.cs:17`, `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:96`).
- The recorded RED logs show the two expected failures per suite before the change. The GREEN logs are clean and show Core/CLI 91/91 and MCP 49/49 passing. I reviewed those logs and did not rerun tests.

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

- `src/SqlHarness.Cli/Commands/Renderer.cs:141`, `src/SqlHarness.Cli/Commands/Renderer.cs:175`, `src/SqlHarness.Cli/Commands/Renderer.cs:184`: the new per-field floor can make an otherwise valid error envelope exceed the 4,096-byte minimum. The candidate loop only reduces report details; the final fallback replaces the underlying error code and phase with `output_budget_too_small` and `render`. Keep the original code/phase and report truthful truncation when the total budget forces Message or Hint below the character floor. Add a focused minimum-budget case with long Message and Hint to the relevant edge coverage.

#### Minor (Nice to Have)

None.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The two renderer changes are narrow and the core low-cell regression is covered, but the CLI floor can crowd out required error metadata under the minimum total budget. The MCP tiny-budget contract also remains unproven by this task's tests.
