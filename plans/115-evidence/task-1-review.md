### Spec Compliance

- ❌ Issues found: `omittedResultSetCount` includes omitted rows, columns, and clipped cells in the inventory fallback, and the selector schema does not place the required lower bound on array items; see Important findings below.
- ⚠️ Cannot verify from diff: The final Windows `just verify` gate was not run by this task; the implementer report assigns that gate to the controller.

### Strengths

- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:115-129` preserves the null-selector path, validates duplicates and negative values before execution, validates source ordinals after successful execution, sorts them into source order, and hashes `operation.Sql`. Focused outside-diff check of `src/SqlHarness.Mcp/McpOperationMapper.cs:322-328` confirmed that `ReadSqlAsync` output is assigned directly to `SqlHarnessQueryOperation.Sql`, covering inline SQL and resolved file contents without mapper normalization.
- Both fixed and request handlers expose the optional selector and include it in their unknown-key allowlists (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:104-116`, `src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs:502-508`). The served query descriptions, capabilities description, and `docs/mcp/tools.md` mention the selector and its projection-only boundary (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:726,755-756`, `docs/mcp/tools.md:11`).
- The opt-in metadata is nullable, so it is omitted for legacy calls; selected sets retain source ordinals and the row/sample projection carries existing clipping metadata (`src/SqlHarness.Core/AgentOutputProjection.cs:29-43,869-912`). The error and missing-evidence branches stay distinct from a valid zero-set result (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:119-129`; `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:984-1034`).
- The new workflow coverage compares complete bounded MCP responses against the frozen Plan 109 fixture, checks source-order selection and SQL/parameter execution evidence, and exercises first/middle/last and empty selections (`tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs:763-887`). Fixed/request schema, missing/error/zero-set, and Unicode minimum-budget cases are also covered (`tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:962-1073`).

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

- `src/SqlHarness.Core/AgentOutputProjection.cs:1050,1063-1064` — The inventory fallback initializes `omitted` with source result sets absent from the response, then adds `omittedColumns`, `omittedRows`, and `clipped` at line 1050 and passes that aggregate as `omittedResultSetCount` at line 1064. This makes the set-count field report partial content omissions as missing result sets, contrary to the contract. Keep the result-set omission count separate from `omittedItems`; pass `baseOmittedResultSets + source.Count - kept` to `QueryShell`. Extend the minimum-budget test to assert that a retained selected set with clipped rows/columns still has `omittedResultSetCount == 0`.
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:104-105`; `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:971-976` — `[Range]` is attached to the `int[]` argument, and the schema test asserts `selector.minimum` while checking only `selector.items.type`. JSON Schema `minimum` constrains a numeric instance; the contract requires `items.minimum: 0` for this array. The served schema therefore does not state the non-negative bound on each ordinal, even though runtime validation rejects negatives. Emit the bound on the item schema and assert `selector.items.minimum == 0` (the array-level `minimum` assertion is not a substitute).

#### Minor (Nice to Have)

None.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The main selection flow follows the requested execution and hash semantics, and the offline tests cover the key boundaries. The public omission count is incorrect under budget clipping, and the published selector schema misses the required per-item lower bound, so the task is not ready to pass review.
