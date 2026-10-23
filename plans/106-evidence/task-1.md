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
