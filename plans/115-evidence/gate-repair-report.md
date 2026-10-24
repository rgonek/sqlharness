# Plan 115 Task 1 verification repair

## Cause

The full Windows gate (`test-SqlHarness.Mcp.Tests.log`) had 527 passed, 4 skipped, and 2 failures. The Task 115 selector description had replaced the existing query guidance that a query duration is per execution and is not benchmark evidence, along with optimizer-estimate, actual-row, execution-count, and dry-run distinctions. The shortened capabilities description also dropped the required post-redaction diagnostic floor and response-byte cap wording. The complete assertions in both tests confirmed the remaining query/capability clauses that must stay discoverable in fixed and request tool catalogs.

## Repair

Only `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` changed. Restored the capabilities wording for post-redaction Message/Hint clipping, response-byte limits, Location.Path behavior, tiny-budget error identity, and timeout/token context. Restored concise query duration/benchmark, estimate/actual-row/execution, and dry-run guidance while retaining the selector description and per-item constraints. Condensed repeated measure/compare diagnostics wording and operation annotation descriptions to keep the request tools/list under 32 KiB; their documented meanings and all existing assertion clauses remain present.

## Verification

- Scoped suite command: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpTokenBudgetTests|FullyQualifiedName~Query_result_set_selector_returns_original_non_prefix_ordinals_and_complete_selection_metadata|FullyQualifiedName~Query_result_set_selector_distinguishes_empty_selection_from_invalid_and_out_of_range_ordinals" --logger "console;verbosity=detailed"` — **125 passed, 0 failed**. This included the full MCP schema and token-budget classes and both selector workflow tests.
- That run measured fixed tools/list at 28,965 bytes; annotations-on fixed at 32,262/32,768; annotations-on request at 32,737/32,768.
- After restoring the final `setup` classification wording in compare, reran the affected description and annotation checks: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Tools_list_explains_optimization_evidence_semantics_in_both_modes|FullyQualifiedName~Operation_annotation_members_are_gated_by_the_startup_config|FullyQualifiedName~Cell_limit_argument_describes_error_diagnostic_floor_in_both_modes"` — **3 passed, 0 failed**. A final detailed annotation-bound run passed 1/1 and measured annotations-off 28,971 bytes, annotations-on fixed 32,268/32,768, and annotations-on request 32,743/32,768.
- `git diff --check` passed.
- Formatting: `pwsh -NoProfile -File ./scripts/format-changed.ps1 -Mode branch -BaseRef 9718b052 -Fix` — `format: 8 changed C# file(s) (branch)`, `format: OK`, exit 0. Final check with `pwsh -NoProfile -File ./scripts/format-changed.ps1 -Mode branch -BaseRef 9718b052` — `format: 8 changed C# file(s) (branch)`, `format: OK`, exit 0. The initial check found only the repository's required no-final-newline convention and was corrected by the prescribed formatter.
- The full Windows gate was not rerun by this repair task; the controller owns it.

## Concerns

No unresolved concern. The request annotated catalog has 25 bytes of remaining tools/list capacity. No test assertion or budget was weakened.

Commit: 3ffc574b fix(115): restore query response guidance.
