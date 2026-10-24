### Spec Compliance

- ✅ Spec compliant. The diff places the shared diagnostic floor and surrogate-safe prefix clipping in Core, and uses both from CLI and MCP while retaining the caller limit for location paths (src/SqlHarness.Core/AgentOutputLimits.cs:10; src/SqlHarness.Cli/Commands/Renderer.cs:142; src/SqlHarness.Mcp/McpResultAdapter.cs:423).
- ✅ The added error-only cases exercise null hints, multibyte diagnostics, minimum byte budgets, preserved code/phase, and truthful truncation in both renderers (tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:107; tests/SqlHarness.Tests/Cli/AgentOutputTests.ErrorDiagnosticFloor.cs:2).
- ✅ No extra knobs, retries, or budget changes appear in this diff. The envelope construction, total-byte fit loops, and error identity fields remain in their existing paths (src/SqlHarness.Cli/Commands/Renderer.cs:187; src/SqlHarness.Mcp/McpResultAdapter.cs:180).

### Strengths

- `AgentOutputLimits` now owns the exact shared floor and surrogate-safe prefix operation, removing duplicate private implementations from CLI and MCP (src/SqlHarness.Core/AgentOutputLimits.cs:10; src/SqlHarness.Core/AgentOutputLimits.cs:13).
- Both call sites preserve their distinct path cap while applying the shared message/hint policy; MCP still redacts all three diagnostic fields before clipping (src/SqlHarness.Cli/Commands/Renderer.cs:142; src/SqlHarness.Mcp/McpResultAdapter.cs:424).
- The focused tests retain error identity and require a nonempty diagnostic plus a truncation marker under the minimum envelope budget (tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:107; tests/SqlHarness.Tests/Cli/AgentOutputTests.ErrorDiagnosticFloor.cs:2).

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- None.

#### Minor (Nice to Have)

- None.

### Assessment

**Task quality:** Approved

**Reasoning:** The extraction keeps the existing floor, per-field path cap, redaction order, byte-budget fitting, and error identity behavior intact. The added CLI and MCP cases cover the requested minimum-budget error-only edge, and the baseline-green evidence is consistent with the controller's ruling that Task 2 is a pure policy extraction after Task 1 established the behavior.

**Review scope:** Base `3b42ebfc`, head `2c0d9b36`; assigned diff only. No tests were rerun during review; the report records focused Core 93/93 and MCP 51/51 runs plus `git diff --check`.
