# Plan 088 gate fix report

## Base and diagnosis

- Base revision: `c1676d83fb41da08bc71088419e9523b456c887a` (`plan-088`).
- The Task 7 gate log at `D:/temp/sqlharness-plan088-task7-gate-c1676d8/test-SqlHarness.Tests.log` showed two failures in `BenchmarkRunTests`: whole-record equality expected `ExecutedSql` to be null, while the runner returned the exact SQL supplied to it.
- The Task 2 report and source establish the intended contract: `BenchmarkRunner.ExecuteAsync` stores its input SQL in `CompareRunArtifact.ExecutedSql` for statement pairing, and `ExecutedSql` is `[JsonIgnore]` journal-only metadata. `ParameterSet` continues to be copied to the artifact and omitted from JSON when null.

## TDD and focused verification

RED command:

```powershell
dotnet test tests\SqlHarness.Tests\SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~BenchmarkRunTests --logger "console;verbosity=minimal"
```

Result before the edit: failed, with the two expected failures (`Null_parameter_set_does_not_change_serialized_run_json` and `ExecuteAsync_returns_the_dialect_result_and_copies_parameter_set`); 6 passed, 2 failed, 0 skipped. Both failure diffs showed the actual `ExecutedSql` equal to the exact SQL passed into the runner.

Final focused GREEN command (benchmark-run tests plus journal and statement-pairing regressions):

```powershell
dotnet test tests\SqlHarness.Tests\SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~BenchmarkRunTests|FullyQualifiedName~StatementSpanPairerTests|FullyQualifiedName~StatementMetricsTests|FullyQualifiedName~JournalBenchmarkBuilderTests|FullyQualifiedName~ActivityJournalBenchmarkTests|FullyQualifiedName~JournalContractTests" --logger "console;verbosity=minimal"
```

Result after the final assertion-preserving edit: passed, 65 passed, 0 failed, 0 skipped. `git diff --check -- tests/SqlHarness.Tests/BenchmarkRunTests.cs` also passed.

## Scope and remaining gate

- Commit: `a737e6d` (`Fix Plan 088 benchmark run test expectations`); it contains only `tests/SqlHarness.Tests/BenchmarkRunTests.cs`.
- The tests now assert exact SQL in the in-memory artifact, preserve the non-null and null `ParameterSet` behavior, and verify the executed SQL and property name are omitted by both JSON serializers. Existing serialized-artifact and writer assertions remain.
- No production code, Task 7 docs, plan/index files, or other dirty worktree changes were edited or included in the commit.
- The full `just verify` gate was not rerun. Task 7 remains pending its worker resuming and final whole-branch review; the earlier gate did not reach the MCP and formatting stages after the two Core failures. No live database, merge, or push was used.
