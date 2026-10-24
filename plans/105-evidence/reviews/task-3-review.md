### Spec Compliance

- ❌ Issues found: Important — The new workflow does not demonstrate a baseline/candidate memory or compile tradeoff, and it does not verify compile retrieval end to end. WorkflowModule.cs:285-287 builds both plans with the same diagnostic fixture flag; WorkflowModule.cs:455-463 derives memory and compile values from statement ordinal alone, so baseline and candidate receive identical diagnostic values. AgentOptimizationWorkflowTests.cs:229 filters to candidate rows, then lines 244-245 check only requested memory and DOP. It never asserts granted/max-used memory, compile time, or compile CPU. The task report claims compile CPU is asserted in the workflow at task-3-report.md:15, but the test does not. Use distinct baseline/candidate diagnostic values and assert the returned grant and compile fields for both budgets.
- ✅ Optional retrieval guidance is consistent across the MCP tool descriptions, capabilities, and artifact documentation: McpToolCatalog.cs:146-148, Capabilities.cs:240, docs/mcp/artifacts.md:17.
- ⚠️ Cannot verify legacy artifact compatibility from this diff. The new ArtifactReaderTests coverage writes current artifacts; it does not exercise a legacy artifact shape, and this diff does not change the reader implementation. The controller should rely on the approved Task 1/2 legacy-reader evidence or check that coverage before final completion.

### Strengths

- The workflow checks exact candidate statement ordinals and object names, logical and LOB reads, Showplan ActualScans, plan diagnostic source, and requested grant/DOP at both budgets: AgentOptimizationWorkflowTests.cs:225-247. It separately checks the STATISTICS IO source and median: lines 219-221.
- The reader regressions distinguish measured zero, partial counters, absent counters, and omitted objects beyond the 64-object bound: ArtifactReaderTests.cs:939-961. Scalar reader coverage also checks compile CPU for measured zero and missing values: lines 812, 817, and 824.
- The fixture asserts no database connections: AgentOptimizationWorkflowTests.cs:252. The referenced minimum-budget statement and plan no-fit tests exist at McpTokenBudgetTests.cs:63 and :599.
- Documentation and tool descriptions keep diagnostics optional, preserve the distinction between run-level STATISTICS IO ScanCount and per-statement Showplan ActualScans, and direct callers to statement continuation for exact records: docs/mcp/artifacts.md:17 and McpToolCatalog.cs:146-148.

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

- AgentOptimizationWorkflowTests.cs:229 and :244-245; WorkflowModule.cs:285-287 and :455-463 — The required workflow proves retrieval of requested memory values but no compile fields or baseline/candidate tradeoff. Because both variants receive identical diagnostic values and assertions inspect candidate rows only, the scenario cannot show how memory and compile evidence differ across the comparison. Add distinct values by variant and assert requested, granted, max-used, compile-time, and compile-CPU values in the returned artifact at the minimum and default budgets.

#### Minor (Nice to Have)

- AgentOptimizationWorkflowTests.cs:181-203 — ReadStatementsAsync has an unbounded loop. If a regression returns an empty page without a continuation, the code resets cursor to null and can keep requesting the first page indefinitely. Bound page attempts and fail when a page is empty or continuation does not advance before all four rows are collected.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The documentation, source distinctions, bounded object IO, and zero/partial coverage are clear. The central workflow requirement remains incomplete because the fixture and assertions do not demonstrate compile evidence or a memory/compile tradeoff across the compared variants.

**Review checks:** I did not rerun the reported suites, as requested. The implementer reports Core 219/219, MCP 60/60, and the workflow capture 1/1 in task-3-report.md:35-38. I confirmed the cited no-fit test methods exist by name; I did not execute them.
