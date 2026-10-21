# Plan 095 Task 1 report

Task 1 adds regression coverage for saved-artifact response budgets, bounded source windows, and continuation. Production code remains unchanged so the intended RED is available for Task 2.

The test fixtures use only synthetic saved artifacts under temporary roots. The served test opens no database and sets a 262144-byte process ceiling, compares the implicit 65536-byte call with a requested 131072-byte call, checks complete statement identities across continuations, and exercises page sizes 1, omitted/default, 512, 0, and 513. It also checks non-pageable section refusal and content-free unknown/foreign artifact results under selectors and an explicit larger budget. Reader fixtures contain 513 plan nodes and 65 matrix cases; both are reconstructed after 4096-byte and 65536-byte projections. Reflection keeps the reader tests compilable before the Task 2 `pageSize` API exists.

## Evidence

Before edits, the requested focused baselines passed:

| Command | Baseline output |
|---|---|
| `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpTokenBudgetTests"` | Passed 98, failed 0, skipped 0; 6 s |
| `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter FullyQualifiedName~ArtifactReaderTests` | Passed 63, failed 0, skipped 0; 2 s |

After edits, both requested focused commands compile and show behavior RED:

| Command | RED output |
|---|---|
| `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpTokenBudgetTests"` | Failed 7, passed 99, skipped 0, total 106. The served artifact schema lacks the budget/page-size properties; valid pageSize 1/512 and the explicit 131072-byte request return an error, while invalid/non-pageable cases lack the required page-size validation message. See [task1-mcp-red.log](task1-mcp-red.log). |
| `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter FullyQualifiedName~ArtifactReaderTests` | Failed 7, passed 62, skipped 0, total 69. Six page-window cases find no `pageSize` parameter on `ArtifactReader.ReadSection`; the statements default-window regression returns 300 rows where 128 is expected. See [task1-core-red.log](task1-core-red.log). |

`git diff --check` passed. No full gate was run: Task 1 explicitly delivers tests that remain RED until Task 2 implements the behavior.

## Files changed

- `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs` — fixed/request schema expectations, served budget comparison and continuation reconstruction, page-size boundaries, and refusal content checks.
- `tests/SqlHarness.Tests/ArtifactReaderTests.cs` — default 128-statement source window and synthetic 513-node/65-case source-window reconstruction under small and large budgets.
- `task1-mcp-red.log`, `task1-core-red.log` — focused RED evidence.

## Concerns for Task 2

- The reflection helper expects a public `ArtifactReader.ReadSection` parameter named `pageSize`; keep that seam named consistently when plumbing the optional window through Core.
- The explicit 131072-byte test requires a response over 65536 bytes with complete statement records, while continuation must resume at the actual returned ordinal when the page is budget-trimmed.
- The worktree still has intentional failing tests; no production fix, database access, merge, or push was performed.

## Review fixes (2026-10-21)

The Task 1 review identified two test robustness gaps. The unknown/foreign refusal regression now verifies that the response has no non-null `result` payload and contains neither saved statement fields nor distinctive fixture values: a 64-character hash, ordinal 0, and the synthetic operator/object/index strings. All three page reconstruction loops now require strictly advancing continuation cursors and have finite iteration limits matching the source item counts. No production files changed.

After these amendments, the focused test commands were rerun:

| Command | Actual output |
|---|---|
| `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpTokenBudgetTests"` | Failed 7, passed 99, skipped 0, total 106. The strengthened unknown/foreign refusal test passes; remaining failures are the expected missing budget/page-size schema and behavior. Full output: [task1-mcp-red.log](task1-mcp-red.log). |
| `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter FullyQualifiedName~ArtifactReaderTests` | Failed 7, passed 62, skipped 0, total 69. Expected missing reflected `pageSize` seam and default statement window mismatch remain. Full output: [task1-core-red.log](task1-core-red.log). |

`git diff --check` passed. Both projects compiled in the final focused runs. An initial parallel launch hit a transient compiler output-file lock; I reran the MCP suite sequentially and excluded the lock failure from the retained RED evidence. The saved logs contain the final sequential runs.
