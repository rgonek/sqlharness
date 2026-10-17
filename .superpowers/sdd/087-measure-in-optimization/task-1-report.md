# Plan 087 Task 1 — measure subject/scope

Status: DONE

## RED/GREEN evidence

Before implementation, the focused test command

`dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter FullyQualifiedName~Measure_mapping --logger 'console;verbosity=minimal'`

failed at compile time for the expected missing API: `MapMeasureAsync` had no `subject` parameter (CS1739), and `SqlHarnessMeasureOperation` had no `Context` property (CS1061). This confirmed the new mapping/context assertions exercised behavior absent from the baseline.

After implementation, MCP mapping, handler forwarding, fixed/request schemas and the tool-list byte-budget check passed:

`dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Measure_mapping|FullyQualifiedName~Measure_handler_forwards_context|FullyQualifiedName~Request_scope_schema_is_required_only_on_the_target_tools|FullyQualifiedName~Tools_list_fits_the_32KiB_budget|FullyQualifiedName~Enum_required_and_default_shapes_are_correct" --logger 'console;verbosity=minimal'`

Output: `Passed! - Failed: 0, Passed: 7, Skipped: 0`.

CLI context flow, omission under `compare.requireContext=true`, invalid-label rejection, and journal projection/hash passed:

`dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~Measure_accepts_subject_and_scope_without_compare_context_policy|FullyQualifiedName~Measure_context_is_optional_when_compare_context_is_required|FullyQualifiedName~Measure_rejects_invalid_context_labels_before_dispatch|FullyQualifiedName~Measure_start_carries_optional_context_and_query_hash" --logger 'console;verbosity=minimal'`

Output: `Passed! - Failed: 0, Passed: 4, Skipped: 0`.

`git diff --check` passed. No full gate was run for this task; the controller owns the final gate. The pre-edit baseline build passed, while the unrelated UI Vitest formatting-expansion test timed out at 20 seconds under machine load and .NET tests were not reached. The controller recorded this baseline boundary and asked that the UI test remain untouched.

## Changes

- Added optional `CompareContext` to `SqlHarnessMeasureOperation`; the CLI and both MCP catalog paths accept `subject` and `scope` as an optional pair.
- Reused `CompareContext.Parse(..., required: false)` for slug and pair validation, independently of compare policy, and carried the context through the MCP mapper.
- Stored measure context in the existing journal `Context` field alongside the measured SQL hash; no journal schema or mutation classification changed.
- Updated MCP measure description and allowlists. Added CLI, MCP mapping/schema, journal, and 32 KiB catalog budget coverage.

## Self-review and concerns

Reviewed the changed diff for API parity and data flow. Fixed and request MCP schemas both contain optional `subject`/`scope`; MCP and CLI share the established parser; absent context remains valid with compare context required; the query hash still comes from measured SQL. `git diff --check` is clean. No Task 1 concerns remain. Documentation edits are left to the documentation task.
