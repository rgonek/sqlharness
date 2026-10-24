# Plan 105 final review fixes

## Scope and result

This bounded fix wave addresses the two Important findings and one Minor in `.superpowers/sdd/105-artifact-diagnostic-evidence/whole-branch-review.md`:

- An advertised `statements.json` that is corrupt, oversized, or unreadable now makes metrics reads fail with a content-free artifact error. A valid legacy file with no diagnostic fields remains readable and reports diagnostics unavailable.
- Each Showplan `QueryPlan` receives a zero-based ordinal within its document. Persistence and metrics distribution grouping retain this identity in addition to document ordinal, statement, variant, set, and repetition. Legacy entries without the new ordinal are not assigned one from artifact order; records ambiguous within a repetition are omitted and the result is partial. Unique legacy records remain usable with a null ordinal.
- The agent workflow page helper has a finite page count and requires an advancing cursor until the expected rows are collected; missing continuation is accepted only at exhaustion.

The tool descriptions were shortened in both fixed-scope and request-scope catalogs so the existing 32 KiB tools/list limit is preserved. The artifact schema retains `planQueryOrdinal` and the relevant diagnostic field names; fuller retrieval and compatibility semantics live in `docs/mcp/artifacts.md`.

## Regression evidence

RED was observed before the implementation for corrupt, oversized and unreadable advertised statement evidence, missing per-QueryPlan identity, and ambiguous legacy attribution. The failure was the expected behavior gap: no artifact error, absent identity, and ambiguous records reported as available. The fixes then passed the focused Core regressions.

Passing checks on the final fix tree before commit:

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~StatementMetricsTests|FullyQualifiedName~PlanMetricsExtractorTests|FullyQualifiedName~JournalBenchmarkBuilderTests"` — 160 passed.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentOptimizationWorkflowTests"` — 1 passed for the bounded pager regression.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Operation_annotation_members_are_gated_by_the_startup_config|FullyQualifiedName~Tools_list_explains_optimization_evidence_semantics_in_both_modes"` — 2 passed. Both fixed-scope and request-scope tool catalogs are under 32,768 UTF-8 bytes.
- Earlier MCP focused artifact/workflow/schema run: 74 passed and 2 failed before the tools/list description and assertion-surface corrections. The two failures were the catalog ceiling and an assertion looking for artifact schema details on the top-level description. The final narrow schema tests above pass after those corrections; the broad MCP filter was not rerun in this bounded wave.
- `git diff --check` — passed before report creation; rerun after the final documentation edits.

## Verification boundary

No live database, raw public XML retrieval, full Windows gate, branch formatter, merge, push, or installed-binary acceptance was run by this fix wave. The controller's earlier Windows run compiled the pre-fix `d78f06ea` tree and passed UI, then stopped before the full gate; it is not evidence for these changes. A fresh full gate and independent scoped review remain pending.

The changes preserve existing response ceilings; no blanket limit was raised. The plan remains IN PROGRESS until the independent review and final gate are complete.
