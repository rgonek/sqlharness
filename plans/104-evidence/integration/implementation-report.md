# Plan 104 integration preparation report

## Scope and revision

The isolated `plan-104` worktree started clean at feature commit `e5c9ed70e2fc8bdaf35bde36cffabb1f38f1c07c`. Current `main` was `9718b052f40335d3647cd96750d80431248f9140`, based on shared ancestor `ab46345a2bfc2c89db4646c33895edf11ceede5a`. Main's working tree was left untouched. This work prepared the Plan 104 feature branch with current Plan 105 main; it did not advance the main checkout.

## Merge decisions

The merge auto-resolved the plan index and most overlapping code. Two content conflicts required semantic resolution:

- `McpToolCatalog.cs`: retained Plan 105's artifact selectors, page bounds/defaults, continuation, and diagnostics; retained Plan 104's exact run limits, IO identity/evidence semantics, and benchmark qualifiers. The Plan 104 `engineGuidance.tableIo` pointer remains in the SQL Server-specific guidance builder and is absent from PostgreSQL tools/list. Descriptions fit the existing 32,768-byte tools/list ceiling, including annotation-enabled request scope.
- `McpToolSchemaTests.cs`: kept assertions for both plans' contracts, including optimization-key/context independence, per-set stability and no cross-set equivalence, TVP/list hash exclusions, collected statement counters, saved diagnostics, exact artifact selectors/limits, engine-scoped guidance, and bounded tool-list size.

Plan 104 and Plan 105 evidence and history remain present. Plan 104's feature-branch verification evidence remains labeled as feature evidence; focused overlap tests are recorded separately below.

## Focused verification

All commands used `-p:IsTestProject=true` for actual test discovery and ran in the `plan-104` worktree:

- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter 'FullyQualifiedName~Operation_annotation_members_are_gated_by_the_startup_config'` — passed, 1/1. This test checks annotation-enabled request-scope tools/list against the unchanged 32,768-byte cap. Log: [mcp-annotation-budget-final2.log](logs/mcp-annotation-budget-final2.log).
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter 'FullyQualifiedName~McpToolSchemaTests'` — passed, 65/65. Log: [mcp-schema-suite-final.log](logs/mcp-schema-suite-final.log).
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:IsTestProject=true --no-restore --filter 'FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~StatisticsIoDetailParserTests|FullyQualifiedName~StatisticsParserTests|FullyQualifiedName~StatementMetricsTests|FullyQualifiedName~JournalBenchmarkBuilderTests|FullyQualifiedName~StatementReconcilerTests'` — passed, 288/288. Log: [core-artifact-capabilities-io.log](logs/core-artifact-capabilities-io.log).
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter 'FullyQualifiedName~McpTokenBudgetTests|FullyQualifiedName~McpOutputTests|FullyQualifiedName~AgentOptimizationWorkflowTests'` — passed, 117/117. Log: [mcp-output-token-workflow.log](logs/mcp-output-token-workflow.log).

Changed C# files were formatted with `dotnet format whitespace SqlHarness.sln --include src/SqlHarness.Mcp/Tools/McpToolCatalog.cs tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs --no-restore`; `git diff --cached --check` passed after the final edits. Earlier schema attempts exposed an over-budget request-scope listing, PostgreSQL leakage of the SQL Server IO pointer, and outdated exact wording assertions. Their raw logs are retained beside the passing logs. The wording was tightened while retaining the contracts above; the schema and annotation budget tests then passed.

## Remaining integration gates

The combined `just verify`, whole-branch formatting review, independent integration reviews, and fast-forward of the main checkout remain controller-owned and pending. The full combined gate has not run on this prepared tree. No database, installation, push, or main-checkout edits were performed. Linux and live UAT remain unverified as documented in the feature evidence.