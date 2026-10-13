# Task 4: Matrix case inventory before optional detail

## Result

Matrix output now puts the shared target, repetition count, classification, parameter definitions, comparison mode, and matrix parameter definition on the matrix envelope. It emits a bounded `cases` inventory containing each completed case's safe artifact id, nullable median metrics, candidate-minus-baseline deltas, equivalence availability, and warnings before allocating optional `details`. Details use `{ index, compare }`; the compact `compare` contains per-case variants, equivalence diagnostics, noteworthy operators, and the existing regression report, while shared matrix fields are omitted.

Completed cases are ordered by index. The output distinguishes `caseCount`, `omittedCaseCount`, and `omittedDetailCount`. When an inventory does not fit, `continuation` advances through the scope-owned matrix artifact. Artifact pages carry the same `AgentMatrixCase` schema and trim to a fitting prefix at the active output budget. Individual-cell artifact reads remain available.

Legacy matrix index files containing only `{ cells: [{ index, artifactId }] }` remain readable. Since those files contain no metrics or comparison evidence, the reader returns null medians/equivalence and `equivalenceAvailability: unavailable` rather than inferring outcomes.

## TDD evidence

- **RED:** `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~Agent_matrix_keeps_completed_case_inventory_ahead_of_optional_detail` failed at both 4096 and 65536 bytes because the current projection had no `cases` field.
- **GREEN:** `dotnet test SqlHarness.sln --no-restore --filter "FullyQualifiedName~AgentOutput|FullyQualifiedName~McpTokenBudget"` passed: 52 CLI tests and 26 MCP budget tests.
- **Final scoped check:** `dotnet test SqlHarness.sln --no-restore --filter "FullyQualifiedName~AgentOutput|FullyQualifiedName~McpTokenBudget|FullyQualifiedName~ArtifactReader|FullyQualifiedName~McpOutput|FullyQualifiedName~BenchmarkSummary|FullyQualifiedName~CompareRegression"` passed: 112 CLI/core tests and 55 MCP tests.

The cases exercise six completed runs at both output budgets with large warnings/table/operator details, verify identities and metrics/deltas, null CPU availability, disabled and false equivalence, compact shared fields, and preserved regression diagnostics. Other focused coverage verifies partial matrix status/error and outcomes, 40-case pagination with every case returned once, low-budget artifact page progress, legacy-index null semantics, and scope-owner checks. MCP output tests were updated to the already-landed schema-v2 compact-query contract.

## Concerns

Legacy matrix indexes remain useful as outcome references but cannot supply medians or equivalence; these are deliberately explicit as unavailable until a subsequent individual-cell read obtains the compare report.
