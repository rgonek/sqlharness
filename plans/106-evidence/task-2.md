# Plan 106 Task 2 evidence — shared error clipping policy

The implementation places the error diagnostic floor and surrogate-safe prefix clipping in Core `AgentOutputLimits`. CLI and MCP now call the same policy. The change preserves redaction-before-clipping, caller limits for location paths, independent envelope versions, error codes, and authentication retry behavior.

New CLI and MCP behavior tests cover `Hint = null`, oversized multibyte error text, error-only responses, minimum 4096-byte budgets, preserved error code/phase, nonempty clipped messages, and truthful truncation.

TDD evidence: Task 1 already established the behavior, so the new tests passed on baseline and after extraction per controller ruling. Baseline summaries are in `.superpowers/sdd/106-bounded-error-diagnostics/task2-baseline-core.log` and `task2-baseline-mcp.log`. The first MCP attempt had a test-only expectation mismatch because null values are omitted by serialization; the corrected assertion accepts either omission or JSON null.

Post-extraction checks:

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:IsTestProject=true --no-restore --filter FullyQualifiedName~AgentOutputTests` — 93 passed, 0 failed, 0 skipped.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter FullyQualifiedName~McpTokenBudgetTests` — 51 passed, 0 failed, 0 skipped.
- `git diff --check` — passed.

The full Windows gate is deferred until Task 3 as directed. No live database was used. Task review is pending.
