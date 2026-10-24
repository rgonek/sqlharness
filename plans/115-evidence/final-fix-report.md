# Plan 115 final whole-branch fix report

## Finding and change

Fixed the Important finding from `whole-branch-review.md`: in `McpToolCatalog.cs`, a successful `SqlHarnessDryRunReport` now returns before the executed-query result-set evidence check. Real successful executions with missing query/result-set evidence still produce the existing safety error. Added a served regression covering fixed and request modes with both `resultSetOrdinals: []` and `[0]`. The real module and unreachable configured target make the successful dry-run evidence dependent on zero database connections.

## TDD evidence

- RED command: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Query_dry_run_accepts_valid_result_set_selectors_in_fixed_and_request_modes_without_database_connections" --logger "console;verbosity=detailed"`
- RED result: 1 test failed as expected; the served envelope was `status:error`, `code:safety_rejected`, message `The successful query response did not contain result-set evidence.` This reproduced the review finding before the production change.
- Final GREEN command: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Query_dry_run_accepts_valid_result_set_selectors_in_fixed_and_request_modes_without_database_connections|FullyQualifiedName~Query_result_set_selector_returns_original_non_prefix_ordinals_and_complete_selection_metadata|FullyQualifiedName~Query_result_set_selector_distinguishes_empty_selection_from_invalid_and_out_of_range_ordinals|FullyQualifiedName~Query_result_set_selection_preserves_errors_and_rejects_missing_success_evidence|FullyQualifiedName~Query_result_set_selection_reports_clipping_and_omissions_for_unicode_rows_at_minimum_budget|FullyQualifiedName~Query_result_set_selector_schema_and_description_match_in_fixed_and_request_modes|FullyQualifiedName~Tools_list_fits_the_32KiB_budget|FullyQualifiedName~Operation_annotation_members_are_gated_by_the_startup_config" --logger "console;verbosity=detailed"`
- GREEN result: passed 8/8. This includes both selector values in both modes, existing invalid and out-of-range validation, missing-success-evidence and execution-error behavior, non-prefix selection, byte-budget clipping, fixed/request schemas, and `tools/list` budgets. Request-mode calls supplied the required target. The controller owns the full Windows gate; it was not run here.
- One intermediate run after the production fix passed 7/8 because the new regression omitted the request-mode required `target`; the test was corrected, and the final command above passed 8/8.

## Files and review

- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`: early return for successful dry-run report only.
- `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs`: served fixed/request regression, empty and nonempty valid selectors.
- `git diff --check`: exit 0.
- Self-review found no path that weakens the missing-evidence guard for executed successful queries. Selector validation remains before execution and unchanged. No documentation or schema change is needed because this restores existing dry-run validation behavior.

## Branch format evidence

- `pwsh -NoProfile -File ./scripts/format-changed.ps1 -Mode branch -BaseRef 9718b052 -Fix`: direct exit 0; `format: OK`. The fix handled final-newline violations in the branch's changed C# files.
- Final required check: `pwsh -NoProfile -File ./scripts/format-changed.ps1 -Mode branch -BaseRef 9718b052`: direct exit 0; checked 8 changed C# files; `format: OK`.
