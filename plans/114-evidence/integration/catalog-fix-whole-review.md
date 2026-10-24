# Plan 114 integration catalog fix: scoped whole review

Reviewed the supplied `d02cefd1..75a3d762` catalog fix diff and the `e2183282` follow-up test/report diff, against the approved scope of raising only the `tools/list` regression ceiling to 36 KiB and deferring catalog optimization. The independent task review approved the fix. No suite was rerun here.

## Verdict

**Source review approved; no Critical, Important, or Minor finding.** The combined Windows gate rerun and direct branch formatter remain pending controller confirmation. This verdict does not claim those checks passed.

## Scope and contract checks

- `src/SqlHarness.Mcp/McpLimits.cs:34-38` renames the catalog test ceiling to `ToolsListRegressionTestBudgetBytes` and sets it to `36 * 1024` (36,864). All catalog assertions and diagnostics in the changed tests use the renamed constant; no old `ToolsListBudgetBytes` reference remains in source or tests. `docs/mcp/tools.md:5` correctly labels it a regression-test ceiling, not a runtime or response limit.
- The actual call-result maximum stays `MaxCallToolResultBudgetBytes = 1048576` at `src/SqlHarness.Mcp/McpLimits.cs:51`; handlers, scope, and result adapter still use that separate limit. The fix does not change response projection, mutation classification, credentials, artifact schemas, or the Plan 114 saved-artifact reader.
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:711-720` restores readable Plan 104 measure/compare and Plan 114 artifact descriptions. `ArtifactCursorDescription` and `ArtifactDescription` explicitly retain `continuation`; section detail states compact CPU/elapsed identities have no operators or repetition. The fixed/request catalog tests continue checking serialized size and section contracts.
- The `e2183282` test changes at `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1455-1482` align phrase assertions with the restored descriptions while retaining checks for optimization scope, setup isolation, evidence limits, compare verdict, dry run, and optional diagnostics. They do not relax a behavioral assertion or modify product code.

## Evidence and residual risk

The implementation report records focused serialized catalog measurements of 29,028 bytes with annotations off, 32,636 with annotations on, and 33,092 in request mode; each is below 36,864. These figures are for the restored wording. The earlier integrated full gate failed on the old 32 KiB catalog assertion and a missing `continuation` phrase; the new source addresses both. The final combined gate must pass on the final candidate before integration is declared verified. Reported unrelated Core test timeouts also remain for that gate to resolve or characterize.
