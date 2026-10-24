### Spec Compliance

- ✅ Spec compliant: the new exact section value is wired into the fixed/request schemas, validator, capabilities and docs; the response DTO contains the required identity and nullable CPU/elapsed fields, omits null `parameterSet`, and excludes repetition/operator/plan data. The reader reuses owner-checked saved statements, preserving legacy missing metrics as null. Existing `statements` code and its frozen response fixtures remain intact. See `src/SqlHarness.Core/ArtifactReader.cs:391`, `src/SqlHarness.Core/ArtifactReader.cs:1203`, `src/SqlHarness.Core/AgentOutputProjection.cs:586`, and `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:897`.
- ⚠️ Cannot verify from diff: none.

### Strengths

The compact DTO makes its wire contract explicit and uses property-level null handling for `parameterSet`, while keeping `matrixCell`, CPU, and elapsed nulls visible. It has no repetition field or reusable evidence path. The new byte-budget projection keeps identities whole, skips an oversized leading record with an omission count, and advances the cursor so the same record cannot cause a continuation loop (`src/SqlHarness.Core/AgentOutputProjection.cs:586`).

Coverage exercises a legacy statement record with absent CPU/elapsed properties, ordering and page continuation, Unicode and escaped identity strings, the minimum 4096-byte response budget, whole-record omission, and exact snapshots for the existing `statements` response. The complete retrieval fixture compares full bounded response text and reports bytes separately from call counts (`tests/SqlHarness.Tests/ArtifactReaderTests.cs:1309`, `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:988`, `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1114`).

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

None.

#### Minor (Nice to Have)

- `docs/mcp/artifacts.md:14` and `docs/mcp/artifacts.md:18`: the existing safe-section and `pageSize` inventories still omit `statement-summary`, even though the new section is described below. Update these lists so readers do not get conflicting guidance about whether the section is supported and pageable.
- `plans/114-evidence/verification/ui-check.log:29`: the final UI check output includes Node warning `[DEP0190]` about passing arguments to a child process with `shell: true`. The verification output is therefore not pristine; remove the warning at its source or identify it as an accepted baseline warning.

### Assessment

**Task quality:** Approved

**Reasoning:** The implementation meets the behavioral contract and handles legacy nulls, exact statements compatibility, complete-response evaluation, and record-level byte-budget omissions. The remaining findings are documentation consistency and test-output noise; neither indicates a functional defect. The controller confirmed the Windows gate and direct branch-format check exited 0; neither was rerun for this review.
