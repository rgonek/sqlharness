# Plan 105 + Plan 106 integration resolution

Plan 105 (`1195297d`) was merged with local main `ab46345a` in this worktree. Merge commit: `4c3028e2` before metadata-only amendment. Both completed plan entries and Plan 106's tracked report, documentation, review evidence and verification logs are retained. No push, installation, live database access or primary-branch update occurred here.

## Conflict decisions

- `plans/README.md`: retained both Plan 105 and Plan 106 history entries. Plan 106's prior integration at `59fc0359` remains historical evidence; Plan 105 now says its feature gate passed at `f6a7f492` and combined-tree verification is pending.
- `src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs`: retained Plan 105's callable plan selectors: zero-based statement ordinal, exact measure parameter-set name, exact repetition/default, and optional bounded plan text. Kept shared cell-description parity and the result-byte range/process cap from Plan 106.
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`: retained Plan 105's section-specific evidence, variant choices, page bounds/defaults, and continuation semantics. Kept the shared cell-description contract from Plan 106. The cursor description still names matrix-cells, operators, statements and plan. Concise descriptions preserve the 32,768-byte catalog limit.
- Auto-merged `src/SqlHarness.Mcp/McpResultAdapter.cs`, `src/SqlHarness.Cli/Commands/Renderer.cs` and `src/SqlHarness.Core/AgentOutputLimits.cs` were inspected. They retain redaction before clipping; a Message/Hint floor of `max(requested, 2048)` bounded by the existing 4096-character and response-byte ceilings; caller-sized paths and result cells; and truthful truncation with error identity preserved when optional diagnostics must shrink. No credential or mutation classification, retry behavior, or response ceiling changed.
- Auto-merged tests retain Plan 105 artifact no-fit behavior at the 4096-byte minimum, artifact selectors/continuations, and Plan 106 diagnostic-floor and catalog-contract assertions. Existing Plan 106 tracked evidence, including `.superpowers/sdd/106-bounded-error-diagnostics/final-fix-report.md`, remains present.

## Focused verification

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~AgentOutputTests|FullyQualifiedName~AgentOptimizationWorkflowTests"` — exit 0; 189 passed, 0 failed, 0 skipped.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpTokenBudgetTests|FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~AgentOptimizationWorkflowTests"` — exit 0; 132 passed, 0 failed, 0 skipped.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~Operation_annotation_members_are_gated_by_the_startup_config" --logger "console;verbosity=detailed"` — exit 0; fixed catalog annotations-off 28,678 bytes, fixed annotations-on 32,286 bytes, request-scope annotations-on 32,761 bytes. All are below 32,768.
- `git diff --cached --check` and post-commit `git show --check` — exit 0.

During integration, the first focused MCP run exposed request-scope catalog size 32,907 bytes and loss of the explicit `parameter-set` selector phrase. The artifact descriptions were compacted while retaining their selector meaning; the subsequent schema run caught cell-description parity, which was restored to the shared description. The final focused suites and catalog-size check above passed.

This evidence is scoped to the focused integration checks. The combined Windows gate, branch formatting and independent reviews remain controller-owned. Linux, live database acceptance, installation and push remain unverified/not performed. The previously recorded ProcessRunner five-second transient was not changed or re-investigated.
