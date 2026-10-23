# Plan 106 Task 1 evidence

Task 1 ran on branch `plan-106` from base `450ffd5d` (`docs: record plan 106 bounded error diagnostics`).

The MCP adapter and CLI renderer now give structured error `Message` and `Hint` a clipping ceiling of `max(requestedCellCharacters, DefaultCellCharacters)`. Both values remain bounded by the existing maximum because their requested limits are validated against it. MCP redacts known secrets before clipping. Result cells, command text, and `Location.Path` continue to use the requested cell limit. The existing byte-budget selection and fallback paths remain in place.

Synthetic tests cover connect, authentication, and SQL errors at cell limits 0, 200, default, and maximum. They assert actionable message and hint tails, MCP credential redaction across the old 200-character clipping boundary, unchanged result-cell and path limits, stable error code/phase, valid MCP envelope shape, and the total response byte ceiling.

## RED evidence

The new CLI test failed at limits 0 and 200 because the actionable message tail was clipped. Result: 2 failed, 89 passed, 91 total.

The new MCP test failed at limits 0 and 200 for the same missing actionable tail. Result: 2 failed, 47 passed, 49 total.

Raw logs are retained in the ignored plan workspace as `task1-red-core.log` and `task1-red-mcp.log`.

## GREEN evidence

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "McpTokenBudgetTests|AgentOutputTests"
```

Passed: 91, failed: 0, skipped: 0.

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --filter "McpTokenBudgetTests|AgentOutputTests"
```

Passed: 49, failed: 0, skipped: 0. The explicit `IsTestProject` property ensures test discovery for this project.

Raw green logs are retained in the ignored plan workspace as `task1-green-core.log` and `task1-green-mcp.log`. `git diff --check` exited 0 after the final edits. The controller's pre-change Core baseline passed 87/87 focused tests.

## Review and limits

The implementation is limited to Message/Hint clipping. No public option, envelope version/code mapping, retry behavior, database access, total response ceiling, or `Location.Path` behavior changed. One existing CLI assertion was updated from a 128-character error ceiling to the contract's default error floor; its 4096-byte and truncation assertions remain. This reconciles the pre-existing test expectation with Plan 106's explicit Contract.

No unresolved Task 1 concerns. This is focused feature-branch evidence; the Windows gate and whole-branch review remain with the controller.

## Minimum-budget fix round

At a 4,096-byte budget, the original fallback replaced `sql_execution_failed/sql` with `output_budget_too_small/render` when both long diagnostics hit the 2,048-character floor. The follow-up bounds Message and Hint together to the available serialized budget, retains the original error code and phase, and marks omitted content while leaving the 4,096-byte ceiling unchanged.

The new CLI and MCP minimum-budget tests first failed with `output_budget_too_small` instead of `sql_execution_failed` (Core/CLI 1 failed, 91 passed; MCP 1 failed, 49 passed). After the fix, Core/CLI passed 92/92 and MCP passed 50/50. Each test uses 8,000-character Message and Hint values at a 4,096-byte total budget and confirms both fields may fall below the cell floor with truthful truncation metadata. Full logs are in the ignored Task 1 workspace as `task1-fix1-red-core.log`, `task1-fix1-red-mcp.log`, `task1-fix1-green-core.log`, and `task1-fix1-green-mcp.log`.

## Distinct omission union fix

The fix round added exact omission accounting to the minimum-budget fallback. CLI counts independently clipped Message, Hint, Location.Path, and command fields; MCP counts independently clipped Message, Hint, and Location.Path fields. This reports the union of prior cell clipping and budget-driven diagnostic clipping. MCP continues to redact before clipping, and Location.Path retains the requested cell limit.

Regression inputs force the minimum budget (4,096 bytes; cell limit 0). CLI has long Message/Hint, a long Location.Path, and a clipped command; its exact expected omission count is 4. MCP has long Message/Hint and a long Location.Path; its exact expected count is 3. The CLI test also asserts the configured output newline and includes its UTF-8 bytes in the hard budget check. This caught and fixed a Windows CRLF reservation error that initially emitted 4,097 bytes.

RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "McpTokenBudgetTests|AgentOutputTests"` failed 1/92 (expected omissions 4, actual 3; after the newline correction). The initial run before the newline correction measured 4,097 bytes against 4,096. `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --filter "McpTokenBudgetTests"` failed 1/50 (expected omissions 3, actual 2). Raw logs: `task1-fix2-red-core.log`, `task1-fix2-red-mcp.log`.

GREEN: the same Core/CLI command passed 92/92; the MCP command passed 50/50. `git diff --check` exited 0. Raw logs: `task1-fix2-green-core.log`, `task1-fix2-green-mcp.log`. The full Windows gate remains with the controller.
