# Task 3: PlanGraph builder report

## Contract and design choices

- `PlanGraph`, nodes, edges, build errors, and property-tree results are renderer-neutral contracts. Both graph and property results carry `Engine = "sqlserver"`; SQL Server Showplan field names remain inside the builder and property payload.
- Stable graph IDs are `statementIndex:path` (for example, `0:0/1`). The native Showplan `NodeId` is carried separately. The statement index provides document-local namespacing; IDs are deterministic for a given statement/operator order.
- Node extraction walks only elements owned by that `RelOp`; traversal stops at child `RelOp` boundaries so a child Object, predicate, defined value, runtime counter, or warning cannot leak onto its parent. Direct child operator order defines the path and edges.
- The graph is all-or-error. The builder checks the 10,000-node ceiling before appending a node, then serializes the complete graph with `JsonSerializerDefaults.Web` and measures the resulting UTF-8 byte array against 1 MiB. It returns a stable explicit error code on either limit; it never returns a clipped graph or clipped predicates.
- Properties are a separate response with a 128 KiB serialized UTF-8 ceiling. The builder budgets property nodes using UTF-8 JSON serialization, includes an explicit truncation node and omitted count when space runs out, then serializes the whole result and checks the exact response size, including envelope, escaped text, multibyte characters, and marker. If even the marker-only response cannot fit, it returns an explicit size-limit error. The graph node list is unaffected by property truncation.
- Limits are named internal builder settings and use `PlanDistiller.DefaultLimits`: 16 MiB XML characters, 100,000 elements, and depth 128. The shared loader retains DTD prohibition, a null resolver, zero entity expansion, and structural validation.
- Runtime rows and executions are summed across per-thread counters with checked overflow. Edge estimated size is child estimated rows multiplied by the operator's average row size. Unknown operator names and extra properties remain available without requiring renderer-specific interpretation.

The parent-approved 10,000-node / 1 MiB graph and 128 KiB property ceilings bound responses while leaving substantial margin above the synthetic and renderer benchmark plans. If a real plan exceeds them, the caller sees a specific error sooner than desired; the named limits can be adjusted without changing contract shape.

## TDD evidence

- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter FullyQualifiedName~PlanGraphBuilderTests --no-restore` — against the initial contract stub, 7 failed, 0 passed; behavior was absent (`graph_not_built`).
- RED: the node-limit case was also run with the node guard temporarily removed; it failed because a partial graph was returned. Restoring the guard made that case pass. No guard change remains.
- GREEN: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~PlanGraphBuilderTests|FullyQualifiedName~PlanDistillerTests" --no-restore` — Passed, Failed 0, Passed 39, Skipped 0, Total 39 (324 ms).
- `git diff --check` — exit 0.

The property-budget test uses a 2,048-byte internal test ceiling to force truncation. It verifies the complete three-node graph remains intact, the explicit marker reports omitted entries, and the final `JsonSerializer.SerializeToUtf8Bytes` result is at most 2,048 bytes. Its property attributes include Å, 漢字, quotes, ampersand, and angle brackets, so the check covers UTF-8 and JSON escaping overhead. The production ceiling uses the same accounting path at 128 KiB.

## Files and self-review

- Added `src/SqlHarness.Core/Plans/PlanGraph.cs` and `PlanGraphBuilder.cs`.
- Refactored `src/SqlHarness.Core/PlanDistiller.cs` to expose its existing limits and validated XML loader internally, retaining the safety settings used by Distill.
- Added `tests/SqlHarness.Tests/PlanGraphBuilderTests.cs` and the hand-written fixture `tests/SqlHarness.Tests/Fixtures/Plans/graph-properties.sqlplan`.
- Updated `plans/088-plan-visualization.md` and `plans/README.md` for Task 3 implementation and pending task review.

Self-review found no known contract blocker. Large or verbose valid plans can produce an explicit graph-size error before renderer selection; predicates remain intact, and a property tree can be truncated independently. Verification here is focused to PlanGraphBuilder and PlanDistiller tests; the full `just verify` gate remains for Task 7. No live database was used and no schema version was changed.

## Sol review correction: account for the full property response

The first review identified that the original 128 KiB check measured only `PlanGraphPropertyTree`. The builder now measures the envelope overhead with the actual engine and node ID, subtracts it from the available tree budget, and performs an exact final UTF-8 serialization check on the complete `PlanGraphPropertyResult`. The test sets the ceiling to one byte below the complete result size: the tree itself fits, but the envelope does not. It verifies truncation and that both the tree and entire result fit. The existing multibyte/escaped property test now checks the entire result too.

- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~Property_response_budget_includes_the_result_envelope" --no-restore` — exit 1; Failed 1, Passed 0. `Assert.True(boundedTree.IsTruncated)` failed (`Expected: True; Actual: False`), demonstrating that the prior builder returned the full tree without accounting for the envelope.
- GREEN: the same command after the fix — exit 0; Failed 0, Passed 1, Skipped 0, Total 1 (162 ms).
- Focused regression: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~PlanGraphBuilderTests|FullyQualifiedName~PlanDistillerTests" --no-restore` — exit 0; Failed 0, Passed 40, Skipped 0, Total 40 (326 ms).
- `git diff --check` — exit 0.

Task 5 property API error propagation and Task 7 full verification remain pending as tracked by the plan; they are outside this correction.
