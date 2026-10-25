### Spec Compliance

- ✅ The merged implementation matches Plan 115's selector contract: fixed/request allowlists and schemas include the nullable 64-item selector, inputs are validated and sorted after execution, successful opted-in results carry the source count, original ordinals, and SQL hash, and the projection accounts for selection and budget omissions. Plan 114's `statement-summary` artifact description and projection paths remain present in the reviewed diff (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:749`, `src/SqlHarness.Core/AgentOutputProjection.cs:216-220`).
- ⚠️ Cannot verify from this diff: the integrated Windows `just verify` is still controller-owned and running on `0baa1ade`; do not treat this review as its result.

### Strengths

- The request path checks duplicates, negative values, and the 64-item limit before execution, then checks source bounds and sorts valid values after execution (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:115-132`).
- Projection keeps the selected source ordinals and counts both explicit and budget omissions (`src/SqlHarness.Core/AgentOutputProjection.cs:216-220`, `988-995`, `1078-1095`).
- Tests cover fixed and request schemas, dry-run, missing and failed evidence, zero sets, non-prefix selections, invalid inputs, and minimum-budget Unicode clipping (`tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1543-1704`, `tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs:581-700`). The existing 36 KiB tools/list test ceiling remains in place (`tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1537-1540`).
- The integration ledger reports the focused MCP selection/schema tests at 142/142 and branch formatting for eight C# files passed; these are read from the resolution report, not rerun here (`.superpowers/sdd/115-query-result-set-selection/integration/resolution-report.md:25-29`).

### Issues

#### Critical (Must Fix)

#### Important (Should Fix)

- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:124-130`: when an executed query has missing result evidence or an out-of-range selector, this returns a fresh `SqlHarnessOutcome` and drops the original `EmissionReceipt`, `OperationRef`, and journal flags. The journal wrapper attaches those fields and receipt to completed operations (`src/SqlHarness.Core/Journal/JournalingModule.cs:123-140`); MCP only completes a receipt when the returned outcome retains it (`src/SqlHarness.Mcp/McpResultAdapter.cs:324-333`). As a result, these post-execution selector errors omit the eligible operation reference and leave the emitted-response footprint unrecorded. Preserve the original journal metadata/receipt while presenting the selector error.

#### Minor (Nice to Have)

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The selector and Plan 114 projection semantics are preserved, and the focused checks are green. The new post-execution rejection path drops journal correlation and emission accounting for calls that already ran, so it should be fixed before approval.