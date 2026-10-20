# Task 1 report: renderer identity regressions

## Evidence

- Controller baseline: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~AgentOutputTests|FullyQualifiedName~ArtifactReaderTests"` passed 129/129 before edits; log: `D:/temp/plan093-baseline.log`. The controller observed an existing UI bundle chunk-size diagnostic and classified it as a baseline build diagnostic.
- CLI focused run after the test changes: the same command exited 1 with 9 failures and 130 passes. All 9 failures are the intended duplicate-key regressions: measure map (0, 4, 2048 chars), compare baseline and candidate maps (0, 4, 2048), and artifact metric map (0, 4, 2048). Exceptions are `System.ArgumentException: An item with the same key has already been added.` The stack points to `AgentOutputProjection.ClipVariant` at `AgentOutputProjection.cs:570` for measure/compare and the artifact variant projection at line 383 for artifact metrics.
- MCP focused run: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter FullyQualifiedName~McpTokenBudgetTests` exited 1 with 2 failures and 41 passes. Both failures are the intended duplicate-key exceptions (`xxxx` at max-cell 4; empty key at max-cell 0) through `AgentOutputProjection.ClipVariant`. The 4096-byte long-identity case passed and confirmed the long key is omitted with positive `truncation.omittedItems` while the response stays within budget.
- Measure-set characterization: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter FullyQualifiedName~Measure_set_summary_retains_each_requested_set` passed 1/1. The measure-set summary exposes scalar per-set metrics, not a per-table map; the test confirms both requested sets and their median reads survive the shape.
- `git diff --check` passed. Fixtures construct renderer reports directly and perform zero database executions.

## Fixture coverage

Synthetic distinct table names share exactly the configured clipping prefix and carry distinct `CompareDistribution` medians (11 and 29). The CLI covers measure, both compare sides, and artifact metrics at max-cell limits 0, 4, and 2048. MCP covers compare baseline/candidate at 0 and 4, plus a 5005-character identity under a 4096-byte response ceiling. The measure-set characterization verifies its scalar summary shape.

## Self-review and concerns

The RED is caused by projection-key collisions in real CLI/MCP response renderers, not malformed fixture data. The tests do not change product code and do not claim full-gate verification. The long-key test permits either retention or omission of the short sibling, but requires the long identity to be absent and the response to report omissions; the output byte ceiling is asserted. No additional concern found.
