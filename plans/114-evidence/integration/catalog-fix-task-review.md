# Plan 114 Catalog Fix Task Review

## Verdicts

- **Spec compliant:** Yes, for the reviewed catalog fix.
- **Task quality:** Approved.

## Findings

None.

## Review notes

The authorized increase is scoped to the `tools/list` regression-test ceiling: `ToolsListBudgetBytes` is replaced with `ToolsListRegressionTestBudgetBytes = 36 KiB`, the documentation explicitly says this is not a runtime protocol or response-size limit, and catalog-size assertions use the renamed constant (`src/SqlHarness.Mcp/McpLimits.cs:38`, `docs/mcp/tools.md:5`, `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:1260`). A focused repository search found no remaining references to the old constant; the catalog tests and diagnostics consistently use the new ceiling.

The actual tool-result budget is unchanged at 1 MiB (`src/SqlHarness.Mcp/McpLimits.cs:51`), and result schemas/handlers continue to refer to `MaxCallToolResultBudgetBytes`. The fix does not touch credential handling or mutation classification. The revised artifact descriptions preserve continuation guidance and the section summary details; the catalog tests still exercise the serialized catalog in fixed, request, and annotation-enabled modes.

## Cannot verify

The post-fix integration gate is pending, so its result is not verified by this review. No suites were rerun. The reported Core2 timeouts are outside this diff and do not change the source-level verdict.
