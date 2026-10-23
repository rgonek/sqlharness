# Plan 108 Task 3 report

## Result

Task 3 adds an offline MCP protocol request regression for omitted plan variants, strengthens the artifact-kind diagnostic assertion, captures focused test evidence, and relocates the accidentally tracked Task 2 report to `plans/108-evidence/task-2-report.md`. The ignored scratch copy remains at its original path for reviewer references.

No product source change remains from Task 3. The temporary removal of the Core measure default was used only for the required RED proof and restored before the GREEN and focused checks.

## Scope and evidence

- `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs`: real in-memory MCP client/server requests omit `variant`. Measure and measure-set return successful plan sections with public variant `query`; compare returns an error listing `baseline, candidate`. The fixture uses an unreachable synthetic profile and does not connect to a database.
- `tests/SqlHarness.Tests/ArtifactReaderTests.cs`: invalid-variant tests now assert the error names the manifest artifact kind as well as its supported values.
- Existing compatibility assertions were exercised by the mandated focused Core/CLI run: scope mismatch is checked before variant validation; absent saved-plan evidence stays an error while earlier available repetitions remain readable; non-plan sections reject selectors; and projection tests retain the 4096-byte minimum budget behavior.
- Task 2's accidentally tracked report was moved with `git mv` to `plans/108-evidence/task-2-report.md`; a scratch copy was restored for the reviewer.

## Test record

- RED: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter 'FullyQualifiedName~Offline_artifact_requests_default_omitted_variant_only_for_measure_kinds'` exited 1 with the expected first measure request error while the defaulting line was temporarily removed. See `plans/108-evidence/task-3-red-mcp-omitted-variant.log`.
- GREEN: the same regression command exited 0, 1 passed. See `plans/108-evidence/task-3-green-mcp-omitted-variant.log`.
- Required Core/CLI focused command: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "ArtifactReaderTests|ArtifactCommandTests|McpTokenBudgetTests"` exited 0, 87 passed. See `plans/108-evidence/task-3-focused-core.log`.
- Required MCP focused command: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "ArtifactReaderTests|ArtifactCommandTests|McpTokenBudgetTests"` exited 0, 46 passed. See `plans/108-evidence/task-3-focused-mcp.log`.
- `git diff --check` exited 0 before commit.

## Deferred checks and status

This is provisional task evidence, not plan completion. Task review, branch formatting, `just verify -LogDirectory D:/temp/sqlharness-plan108-final-verify`, whole-branch review, plan/index updates and final archive are outstanding for the controller. Linux and live UAT remain unverified; no merge, push, install, or live database action occurred.

## Review notes

The integration test owns and restores `SQLHARNESS_HOME`, cancels the in-memory server in `finally`, and uses a per-test temporary directory. Test output confirms all 3 omitted-variant request outcomes. No other concerns found in the scoped diff.

## Scoped review follow-up

Task review approved with no findings. The first branch-format check had exit code 2 because these five branch-changed C# files lacked the required final newline: `src/SqlHarness.Core/ArtifactReader.cs`, `tests/SqlHarness.Tests/ArtifactReaderTests.cs`, `src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`, and `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs`. The original result is preserved in `plans/108-evidence/branch-format-initial.log`.

The formatter fixed only those final newlines with `pwsh ./scripts/format-changed.ps1 -Fix -Mode branch -BaseRef 6d543e50`; it exited 0 and printed `format: OK`. The successful direct output and `EXIT_CODE=0` are in `plans/108-evidence/branch-format.log`. `git diff --ignore-space-at-eol --exit-code` exited 0, confirming the whitespace fix has no semantic diff. No tests were rerun for this whitespace-only fix; the controller's full Windows gate follows scoped re-review.

The compatibility cases covered by the earlier aggregate focused logs are located at:

- Scope mismatch is checked before selector validation by `ArtifactReaderTests.Plan_section_invalid_variant_reports_values_for_manifest_kind` in `tests/SqlHarness.Tests/ArtifactReaderTests.cs:144`; the wrong-owner call and scope error assertion are at lines 160–163.
- Missing saved plans remain explicit evidence gaps in `ArtifactReaderTests.Plan_section_keeps_measured_runs_without_saved_plan_evidence_in_inventory` at lines 403–440. It asserts repetition 3 has no saved plan evidence while repetition 2 remains readable.
- Invalid selectors remain rejected by `ArtifactReaderTests.Plan_section_does_not_repair_explicit_nonempty_measure_variants` at lines 124–142 and `ArtifactReaderTests.Non_plan_sections_reject_plan_selectors_before_reading_the_section` at lines 192–204.
- The 4096-byte floor is exercised by `McpTokenBudgetTests.Artifact_plan_pages_fit_the_mcp_budget_and_continue_by_nodes_returned` at `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:408` and `McpTokenBudgetTests.Minimum_budget_is_accepted_and_smaller_is_rejected` at lines 1015–1027. The focused MCP run discovered and passed both under the saved 46-test result.
