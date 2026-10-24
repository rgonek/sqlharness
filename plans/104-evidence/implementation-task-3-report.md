# Plan 104 Task 3 — Consumer compatibility report

Status: **DONE_WITH_CONCERNS**

## Scope and result

Removed dashboard-side `#8hex` name inference from `tableIoDelta`. Core owns raw-name classification and already emits per-run anonymous table-variable aggregates with collision-safe persisted labels. The UI now joins exact persisted labels and keeps historical names as stored. Data plus LOB totals and scan counts remain unchanged.

Extended `ArtifactReaderTests.MeasureAndMeasureSet_AllSectionsRead` to round-trip table metrics for a single measure, named-set measure, and compare. It proves legacy `#8hex` keys remain exact and collision-safe aggregate labels remain separate through the saved metrics section. Existing focused fixtures cover per-run aggregation and collision allocation (`BenchmarkSummaryTests.Compare_table_variable_reads_are_summed_within_each_run_before_distributions`, `Compare_preserves_real_names_that_resemble_aggregate_labels`, `Compare_variants_share_collision_safe_aggregate_labels`, `Compare_omits_typed_io_distribution_when_a_run_has_incomplete_provenance`); MCP fixtures cover clipped identity keys and retaining a later fitting row (`McpTokenBudgetTests.Compare_table_metric_keys_keep_identity_under_mcp_cell_clipping`, `Compare_omits_unfittable_long_table_identity_truthfully_within_minimum_budget`). UI regressions cover table comparison and Markdown export preserving distinct names and totals.

Updated the installed `measure` and `compare` MCP descriptions, `docs/mcp/tools.md`, and current dashboard/metric design contracts. Marked the exact-build-only recommendation as superseded in the primary-source research and experiment-resolution notes. Preserved the tested SQL Server `17.0.1145.4` boundary as empirical evidence, with no vendor naming guarantee or exact-build runtime gate.

## Changes

- `src/SqlHarness.Dashboard/ui/src/lib/tableIoDelta.ts`
- `src/SqlHarness.Dashboard/ui/src/lib/tableIoDelta.test.ts`
- `src/SqlHarness.Dashboard/ui/src/lib/prMarkdown.test.ts`
- `tests/SqlHarness.Tests/ArtifactReaderTests.cs`
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`
- `docs/mcp/tools.md`
- `docs/superpowers/specs/2026-10-06-activity-dashboard-design.md`
- `docs/superpowers/specs/2026-10-14-dashboard-compare-review-design.md`
- `docs/superpowers/specs/2026-10-16-optimization-dashboard-design.md`
- `docs/superpowers/specs/2026-10-16-optimization-evidence-core-design.md`
- `plans/104-evidence/sqlserver-internal-naming-research.md`
- `plans/104-evidence/experiment-resolution-review.md`

## Verification

- RED: `npm test -- src/lib/tableIoDelta.test.ts src/lib/prMarkdown.test.ts` against the old UI inference — exit 1, 2 failed / 7 passed. Raw output: [ui-red.txt](task-3-logs/ui-red.txt).
- GREEN: same focused UI command after the fix — exit 0, 2 files / 9 tests passed. Raw output: [ui-green.txt](task-3-logs/ui-green.txt).
- Saved artifact fixture: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~ArtifactReaderTests.MeasureAndMeasureSet_AllSectionsRead"` — exit 0, 1 passed. Raw output: [artifact-reader-focused.txt](task-3-logs/artifact-reader-focused.txt).
- Core actual-discovery filter: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "BenchmarkRunTests|BenchmarkSummaryTests|ArtifactReaderTests|AgentOutputTests|McpTokenBudgetTests"` — exit 0, 213 passed, 0 skipped. Raw output: [core-focused.txt](task-3-logs/core-focused.txt).
- MCP actual-discovery filter: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --filter "BenchmarkRunTests|BenchmarkSummaryTests|ArtifactReaderTests|AgentOutputTests|McpTokenBudgetTests"` — exit 0, 54 passed, 0 skipped. Raw output: [mcp-focused.txt](task-3-logs/mcp-focused.txt).
- `git diff --check` — exit 0. Raw output: [diff-check.txt](task-3-logs/diff-check.txt).

## Concerns and deferred checks

No Task 3 review or whole-branch review has been recorded yet; the controller owns those reviews and final formatting/gate. Linux and live SQL acceptance remain unverified/deferred. No live database, merge, push, install, or force-tracking action was taken.
