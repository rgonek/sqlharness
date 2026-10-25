# Plan 115 combined local integration review

Base: `621a25210fc3338dcea90392895ac62957d8f5f0` (current `main`). Candidate: `0d37fa9e5e549118702b9a195e42bd68aabcf273`. Scope: combined product/test/docs/index delta and the integration reports. Read-only review; no tests rerun. The full Windows `just verify` result is controller-owned and was pending at review time.

### Strengths

- The selector validates length, negativity, and duplicates before execution, validates source bounds after execution, and passes normalized source ordinals and a hash of resolved SQL to the existing bounded query projection (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:115-140`).
- Query projection retains original ordinals, counts excluded and budget-omitted sets, and keeps the omitted-selector JSON shape (`src/SqlHarness.Core/AgentOutputProjection.cs:895-1095,1228-1244`). The combined catalog retains Plan 114's `statement-summary` guidance and Plan 104's table IO wording; the 36 KiB tools/list regression ceiling remains in the test (`tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1561-1568`).
- The post-execution failure conversion retains operation correlation, journal flags and the emission receipt while recomputing the Safety machine error (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:135-147`). The served regressions assert one execution and one receipt completion with the emitted footprint (`tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1700-1765`).
- Fixed/request served schema, dry-run, zero-set, non-prefix, invalid-selection and bounded Unicode cases have focused coverage. The resolution ledger reports 164/164 focused tests and eight-file branch formatting passed; those checks were not rerun in this review.

### Issues

#### Critical (Must Fix)

None found.

#### Important (Should Fix)

None found.

#### Minor (Nice to Have)

None found.

### Declined to judge

- Live database behavior and paid-agent efficiency: Plan 115 explicitly leaves these out of scope; the workflow and schema tests are offline.
- Historical Plan 104 and 114 implementations outside the combined delta: this review checks that their relevant contracts survive the integration, not their original full audits.
- Final Windows `just verify`: the controller owns the gate on this exact candidate, and its result was pending.

### Recommendations

Use the controller's exact-HEAD Windows gate result before transferring the candidate to `main`; retain its log reference with the integration evidence.

### Assessment

**Ready to merge?** Yes, conditional on the full Windows `just verify` passing on `0d37fa9e`.

**Reasoning:** The combined delta implements Plan 115's opted-in selection without changing default query output, preserves the relevant Plan 104/114 contracts, and addresses the post-execution receipt defect. No review finding remains; this review does not establish the pending full-gate result.