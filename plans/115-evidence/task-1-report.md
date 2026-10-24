# Plan 115 Task 1 implementation report

## Implemented behavior

Added the nullable `resultSetOrdinals` selector to fixed and request `sqlharness_query` tools. It accepts at most 64 unique non-negative source ordinals, preserves source order, validates ordinal range against the executed response, and leaves omitted/null requests on the legacy response shape. Explicit `[]` is a successful empty selection. Opted-in successful query results add `sourceResultSetCount`, normalized `selectedResultSetOrdinals`, and uppercase SHA-256 `queryHash` of exact resolved SQL UTF-8 bytes. Errors remain on the standard envelope error path; a successful operation without query result-set evidence becomes an error.

Selection is applied only to output projection. Query execution and parameter mapping remain unchanged. Row, column, cell and result-set budgets retain omission/clipping metadata and original ordinals.

## Files changed

- `src/SqlHarness.Core/AgentOutputProjection.cs` — opt-in selection metadata, original ordinals and combined source/budget omission counts; default projection remains unchanged.
- `src/SqlHarness.Mcp/McpResultAdapter.cs` — passes selection metadata into bounded query projection.
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` — fixed tool field, validation, SQL hash and post-execution selection; fixed unknown-key allowlist.
- `src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs` — request-mode field and unknown-key allowlist forwarding.
- `docs/mcp/tools.md` — query request/response contract and execution-versus-projection boundary.
- `tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs` — frozen Plan 109 MCP fixture comparison, selector coverage, exact SQL/parameter and invocation evidence.
- `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs` — fixed/request schemas, zero-result/missing/error boundaries and minimum-budget Unicode clipping.
- `tests/SqlHarness.Mcp.WorkflowHost/Program.cs`, `WorkflowModule.cs` — test-only query call evidence written by the offline workflow host.

## TDD and verification evidence

- RED: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter FullyQualifiedName~Query_result_set_selector_returns_original_non_prefix_ordinals_and_complete_selection_metadata --logger 'console;verbosity=minimal'` — failed because the fixed query handler rejected the new field as unknown, as expected.
- GREEN focused selection run: the six focused selection/schema/error/budget/tools-list tests passed, 6/6, using `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Query_result_set_selector_returns_original_non_prefix_ordinals_and_complete_selection_metadata|FullyQualifiedName~Query_result_set_selector_distinguishes_empty_selection_from_invalid_and_out_of_range_ordinals|FullyQualifiedName~Query_result_set_selector_schema_and_description_match_in_fixed_and_request_modes|FullyQualifiedName~Query_result_set_selection_preserves_errors_and_rejects_missing_success_evidence|FullyQualifiedName~Query_result_set_selection_reports_clipping_and_omissions_for_unicode_rows_at_minimum_budget|FullyQualifiedName~Operation_annotation_members_are_gated_by_the_startup_config" --logger 'console;verbosity=detailed'`.
- The final tools/list measurements were 32,259 bytes for fixed scope and 32,734 bytes for request scope with annotations enabled; the budget is 32,768 bytes.
- Complete bounded MCP response comparison against the frozen Plan 109 three-set assertion fixture: legacy full response 11,469 UTF-8 bytes; selected `[2,1]` response (emitted `[1,2]`) 732 bytes; delta −10,737 bytes. This compares full MCP text responses, including envelope and opted-in metadata.
- The offline host recorded five query invocations for five calls, including exactly one invocation for the selected call. It captured the original SQL and typed `batch`/`int`/`7` parameter for both compared requests and recorded zero database connection attempts.
- Tests also cover first/middle/last ordinals, non-prefix selection, explicit empty selection, valid zero source sets, duplicates, negatives, more than 64 values, out-of-range values, errors, missing result evidence, Unicode/escaped cell clipping and omission metadata.
- `git diff --check` passed. The controller owns the final full Windows gate; it has not been run by this task.

## Self-review and concerns

No unresolved implementation concern. Request-mode annotation-enabled `tools/list` fits with 34 bytes of headroom; the focused contract test passes. No live database, paid-agent trial, merge, push or publication was performed.

## Branch formatting

- First branch-format check found only whitespace/import-ordering issues after the feature commit. Applied the prescribed formatting fix as whitespace-only commit `88c630df4690452a7c8a5e38d9482865686d4d3c`.
- Reran `pwsh -NoProfile -File ./scripts/format-changed.ps1 -Mode branch -BaseRef 9718b052` on that commit: 8 changed C# files checked; `format: OK`; direct exit code 0. Raw output is archived in `branch-format.log`.

## Review fix 1

- Finding 1: separated `omittedResultSetCount` from aggregate `omittedItems` in the inventory fallback. A direct minimum-budget Core projection regression forces `detailLimit: 0` with one retained selected set, clipped cells and omitted rows/columns. RED: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Query_result_set_selection_reports_clipping_and_omissions_for_unicode_rows_at_minimum_budget"` failed with expected omitted set count `0`, actual `180`. GREEN asserts selected `omittedResultSetCount == 0`, aggregate `omittedItems > 0`; served minimum-budget result remains within its byte budget and reports truncation. The same inventory fixture without selector confirms legacy nullable omission count and absence of all selector metadata.
- Finding 2: removed array-level `[Range]` and used the existing schema creation transform to emit `items.minimum: 0` for `resultSetOrdinals`; `maxItems: 64` remains. Fixed and request served-schema assertions check per-item minimum and absence of array-level minimum. RED: the selector schema test failed because `items.minimum` was absent.
- GREEN focused command: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Query_result_set_selector_schema_and_description_match_in_fixed_and_request_modes|FullyQualifiedName~Query_result_set_selection_reports_clipping_and_omissions_for_unicode_rows_at_minimum_budget|FullyQualifiedName~Operation_annotation_members_are_gated_by_the_startup_config|FullyQualifiedName~Tools_list_fits_the_32KiB_budget" --logger "console;verbosity=detailed"` — passed 4/4. Served list sizes: annotations off 28,630 bytes; fixed annotations on 32,238/32,768; request annotations on 32,713/32,768. `git diff --check` passed. No full suite run; controller owns that gate.
- Self-review: only the inventory count, selector schema metadata, and focused regressions changed. Omitted/null requests still omit selector metadata; the legacy omitted-set property remains null when no source set is absent. Runtime validation remains unchanged. No new conflict with the brief found.
- Branch formatting rerun and commit evidence will be appended after commit.
- Final branch-format command after commits `8ea23266` and `2d75e665`: `pwsh -NoProfile -File ./scripts/format-changed.ps1 -Mode branch -BaseRef 9718b052` — checked 8 changed C# files; `format: OK`; direct exit code 0. The initial check’s final-newline-only failures, prescribed `-Fix` output, and final passing check with direct exit codes are appended to `branch-format.log`.
- Review-fix commits: `8ea23266 fix(115): separate query omission counts`; `2d75e665 style(115): preserve repository final newlines` (formatting only).
