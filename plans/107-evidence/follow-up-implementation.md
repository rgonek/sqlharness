# Plan 107 follow-up — Task 1 report

## Outcome

Implemented in commit `a8aded980e3f46c0663e238f7afb82db74b82069` on `plan-107-followup`, based on `81d0fa59`. Final Windows gate and branch formatting passed; the scoped Luna xhigh task review approved with no findings. The Sol low whole-branch review remains pending. No live database, credentials, installation, or historical workload was used.

## Diagnosis

Source inspection confirmed two compare-matrix defects. `ExecuteCompareMatrixAsync` called `PrepareFixed` before parameter-set inputs were available, so its initial SQL Server safety classification saw unexpanded bare `IN @Ids` syntax and rejected it as `ParseError`. Later, each matrix case expanded setup, baseline, and candidate independently, then nested `CombineClassificationParameters` calls treated the same generated list names as separate caller parameters. Repeated list references therefore caused false collisions. Empty lists generate `@Ids`; non-empty lists generate `@Ids1`, `@Ids2`, and so on.

The caller scalar `@Ids1` collision is independent and remains rejected when the generated list binding uses that name. The setup fixture creates `#ids` in a parameter-free `SELECT INTO`, then references the list in a later setup statement; the prior parameterized `SELECT INTO` fixture was invalid under the setup ordering validator.

## Changes

- Seed compare preflight classification with one representative list input per normalized name from the parameter sets, while keeping those set-only bindings out of the fixed-parameter report.
- Deduplicate identical generated list bindings across setup, baseline, and candidate within each case, then combine them with scalar inputs so caller collisions still fail.
- Validate and check missing parameters against each case's expanded SQL and exact list bindings. The matrix path skips the redundant fixed-variant reference pass because the per-case path has already validated each expanded request.
- Convert Core and real-SDK MCP characterization coverage to expected-success contracts for both `IN` spellings, singleton/multi/empty values, multiple named sets, setup reuse, zero connection attempts, and preserved caller collision rejection.

## RED and GREEN evidence

Core RED, before production changes:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore -p:IsTestProject=true --filter 'FullyQualifiedName~Compare_dry_run_classifies_identical_list_sql_for_named_sets_and_setup_reuse|FullyQualifiedName~Compare_dry_run_classifies_a_list_reused_by_setup_and_both_variants' --logger 'console;verbosity=minimal'
Failed 4, Passed 0. All four expected-success cases returned Safety.
```

MCP RED, before production changes:

```text
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --filter 'FullyQualifiedName~Served_compare_classifies_offline_list_paths_for_both_IN_forms' --logger 'console;verbosity=minimal'
Failed 1, Passed 0. The real-SDK served host logged all 12 requests; 11 ordinary cases returned safety_rejected and the caller-collision case was rejected before its collision path. Connection attempts: 0.
```

The earlier preserved Plan 107 red-contract and request-shape evidence remains at `plans/107-evidence/task-2-core-red-contract.log`, `plans/107-evidence/task-2-mcp-red-contract.log`, and `plans/107-evidence/task-2-request-shapes.json`.

Core GREEN:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore -p:IsTestProject=true --filter 'FullyQualifiedName~Compare_dry_run_classifies_identical_list_sql_for_named_sets_and_setup_reuse|FullyQualifiedName~Compare_dry_run_classifies_a_list_reused_by_setup_and_both_variants|FullyQualifiedName~Compare_dry_run_rejects_caller_scalar_that_collides_with_generated_list_name' --logger 'console;verbosity=minimal'
Passed 5, Failed 0. Every dry-run asserted FactoryOpenCount=0; caller @Ids1 collision remained a Safety rejection.
```

MCP GREEN:

```text
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore -p:IsTestProject=true --filter 'FullyQualifiedName~Served_compare_classifies_offline_list_paths_for_both_IN_forms' --logger 'console;verbosity=minimal'
Passed 1, Failed 0. The test covered 12 requests across both SQL spellings, verified ordinary success and the caller-collision rejection, and asserted zero connection attempts.
```

## Windows gate

The first gate attempt passed UI install, build, UI check, and tests; formatting failed only on missing final newlines in the four changed C# files. The failing `format.log` and other stage logs are preserved in `plans/107-evidence/windows-gate-follow-up/`. `just fix` reported `format: OK`.

The second gate attempt passed install/build/UI checks, but one unrelated Core test timed out waiting for its process marker: `SqlHarness.Tests.Auth.ProcessRunnerTests.PidPublicationError_PreservesOriginalWhenMarkerWriteFails` (`test-SqlHarness.Tests.log`). The MCP project passed 535 tests with 4 skipped. The unchanged failing test was rerun alone and passed 1/1 in 996 ms. The timeout remains recorded as an observed transient; its root cause is unproven. Logs are preserved in `plans/107-evidence/windows-gate-follow-up-final/`.

The final gate passed on commit `a8aded980e3f46c0663e238f7afb82db74b82069`:

```text
just verify -LogDirectory plans/107-evidence/windows-gate-follow-up-final2
exit 0; verify: OK stages=5; elapsed 10m51s
ui-install: skipped (lockfile unchanged)
build: passed
ui-check: passed in 6m33s
Core: 4,718 passed, 11 skipped, 0 failed
MCP: 535 passed, 4 skipped, 0 failed
format: skipped (0 changed C# files; committed clean tree)
```

The final gate logs are in `plans/107-evidence/windows-gate-follow-up-final2/`. The required explicit branch formatting check then passed against the assigned base, covering all four changed C# files:

```text
pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 81d0fa59
format: 4 changed C# file(s) (branch)
format: OK; exit 0
```

## Files and concerns

Changed product/test files: `src/SqlHarness.Core/CompareCellRunner.cs`, `src/SqlHarness.Core/SqlHarnessModule.cs`, `tests/SqlHarness.Tests/CompareTests.cs`, and `tests/SqlHarness.Mcp.Tests/McpParameterBatchTests.cs`.

No known product concerns. A previous full-gate run observed the process-marker timeout described above; its unchanged focused rerun and the final full gate passed. The whole-branch review remains pending.
