# Plan 103 final-gate remediation

Fixed the annotation-enabled request-scope `tools/list` size and the stale default statement-page count assertion. The measure/compare descriptions retain optimization context, ordered per-set session/setup and failure behavior, stability/equivalence, TVP/list hash treatment, compare verdict and dry-run semantics, statement-counter coverage limits, and artifact section guidance.

## Evidence

RED gate log: `D:/temp/sqlharness-plan103-final-verify/test-SqlHarness.Mcp.Tests.log` (gate `167829f7`). MCP summary was 508 passed, 4 skipped, 2 failed. `Operation_annotation_members_are_gated_by_the_startup_config` measured annotation-enabled `tools/list` at 33,195 bytes against 32,768; this remediation's first focused run measured 32,841 bytes. The annotation-enabled fixed catalog was 32,398 bytes. `Artifact_requests_raise_the_call_budget_and_reconstruct_statement_pages` failed its stale default count assertion (expected 128, returned 113); the source window remains 128 with continuation 128, and the raised-budget test reconstructs all 512 rows.

Changed descriptions are ASCII. Their combined lengths moved from 1,205 bytes in the first focused run to 1,036 bytes now, reducing the serialized request list by 169 bytes. Derived final request-scope size: 32,841 - 169 = **32,672 bytes**, leaving 96 bytes under the 32,768 limit. The passing schema test asserts the actual serialized response stays within the limit; xUnit console did not print successful-test `ITestOutputHelper` messages, so the final exact count above is derived from the observed RED response and exact description byte delta.

GREEN command:

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore -m:1 -p:SkipDashboardUi=true -p:UseSharedCompilation=false --filter 'FullyQualifiedName~Operation_annotation_members_are_gated_by_the_startup_config|FullyQualifiedName~Artifact_requests_raise_the_call_budget_and_reconstruct_statement_pages' --logger 'console;verbosity=minimal'
```

Exact output:

```text
SqlHarness.Core -> ...\SqlHarness.Core.dll
SqlHarness.Mcp -> ...\SqlHarness.Mcp.dll
SqlHarness.Dashboard -> ...\SqlHarness.Dashboard.dll
SqlHarness.Cli -> ...\sqlharness.dll
SqlHarness.Mcp.WorkflowHost -> ...\SqlHarness.Mcp.WorkflowHost.dll
SqlHarness.Mcp.Tests -> ...\SqlHarness.Mcp.Tests.dll
Test run for ...\SqlHarness.Mcp.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 10 s - SqlHarness.Mcp.Tests.dll (net10.0)
```

Captured command log: `D:/temp/plan103-gate-fix-targeted-final.log`. No full gate was run in this remediation. C# files retain no final newline.

## MCP schema assertion follow-up

The second gate after `bb7533ed` exposed stale exact wording in `Tools_list_explains_optimization_evidence_semantics_in_both_modes`. Updated only that test to assert the approved Measure and Compare wording's essential semantics: subject/textual-scope independence; ordered isolated session/setup sets, stop-and-retain behavior and per-set stability; TVP/list hash caveats; collected-counter-only evidence and the artifact section reference; compare context/target, verdict meaning, equivalence, dry-run status and classifications. The assertions use literal semantic clauses, not the implementation constants. No product description changed.

First class run discovered 63 cases and reported 62 passed / 1 failed because the new assertion said `Per-set session/setup` while the approved text says `Session/setup per set`. Corrected that test expectation; final class run passed all 63.

Final command:

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore -m:1 -p:SkipDashboardUi=true -p:UseSharedCompilation=false --filter 'FullyQualifiedName~SqlHarness.Mcp.Tests.McpToolSchemaTests' --logger 'console;verbosity=minimal'
```

Final output:

```text
Test run for ...\SqlHarness.Mcp.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
Passed!  - Failed:     0, Passed:    63, Skipped:     0, Total:    63, Duration: 9 s - SqlHarness.Mcp.Tests.dll (net10.0)
```

Log: `D:/temp/plan103-gate-fix-schema-class-final.log`. This was a test-only focused run; no full gate was run. The C# file ends without a final newline.
