# Plan 107 follow-up — Task 1 report

## Outcome

Implementation is complete; final Windows gate and review status are pending. Worktree: `D:/Dev/sqlharness/.worktrees/plan-107-followup`, branch `plan-107-followup`, assigned base `81d0fa59`. No live database, credentials, installation, or historical workload was used.

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

First gate attempt (before `just fix`) passed UI install, build, UI check, and tests; formatting failed only on missing final newlines in four changed C# files. The failure output is preserved in `plans/107-evidence/windows-gate-follow-up/format.log`; its build/test/UI logs are in that directory. `just fix` reported `format: OK`.

Final gate: pending. Intended command: `just verify -LogDirectory plans/107-evidence/windows-gate-follow-up-final`. Record the tested revision/tree, all stage results, and final log paths here before commit.

## Files and concerns

Changed product/test files: `src/SqlHarness.Core/CompareCellRunner.cs`, `src/SqlHarness.Core/SqlHarnessModule.cs`, `tests/SqlHarness.Tests/CompareTests.cs`, and `tests/SqlHarness.Mcp.Tests/McpParameterBatchTests.cs`.

No known product concerns. The full gate has not yet completed; commit and review remain pending.
