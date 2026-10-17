# Task 6 report: SQL Server reproduction script generator

## Implemented

- Added `ReproScript`, its form/completeness contracts, engine dispatch, SQL Server writer, and SQL literal formatter.
- Generated scripts include side/case context, warm-up/median caveat, setup-on-new-connection note, statistics options, and the SqlClient-default `ARITHABORT OFF` setting.
- `AsExecuted` uses `sys.sp_executesql` with typed declarations and constant arguments. Parameter-free setup prefixes and parameterized remainders follow `SetupSqlExecution.PrepareCommands` boundaries.
- Local-variable scripts declare each stored parameter. Missing values produce typed comments and `ValuesMissing`; absent stored query/setup SQL and non-SQL Server engines return unavailable scripts.
- Literal formatting preserves SQL string escaping and Unicode, date/time precision, invariant numeric JSON, and SQL Server's base64 JSON representation for `byte[]` as `0x` literals.
- SQL NULL follows the controller ruling: `sp_executesql` receives bare `NULL` while the `@params` declaration supplies the type; local variables use `DECLARE @x type = NULL`. The brief's `CAST(NULL AS type)` argument was not used because Microsoft documents `sp_executesql` values as constants or variables, not expressions.

## TDD evidence

RED command:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~Repro" --no-restore -v minimal
```

Before implementation, the test project failed to compile with `CS0103` for the expected missing `ReproScripts`, `ReproScriptForm`, `ReproCompleteness`, and `SqlServerLiteral` symbols. This confirmed the tests exercised the unimplemented contract.

A follow-up literal test also failed before correction:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~Literals_round_trip_for_supported_values" --no-restore -v minimal
```

It exposed that stored `varbinary` values are JSON base64 (`Cgs=` for `0x0A0B`), not hex text; the formatter was corrected to decode the stored representation.

GREEN command:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~Repro" --no-restore -v minimal
```

Output: `Passed! - Failed: 0, Passed: 10, Skipped: 0, Total: 10, Duration: 763 ms`. ScriptDom parsing assertions passed for generated `sp_executesql`, local-variable, and setup-split scripts.

Additional check: `git diff --check` exited 0.

## Self-review and limitations

- Re-read the generated-script paths for SQL escaping, missing values, setup command ordering, and case selection. Tests cover these branches and SQL NULL's declared-type rule.
- No live SQL Server or SSMS session was available, so syntax was checked with the repository's ScriptDom parser; SQL Server execution and server-specific conversion behavior remain unverified.
- `ARITHABORT OFF` reflects the documented default for application client connections. The effective session setting is not journaled; server/user options can override it. The generated header states this limitation.
- The full `just verify` gate was not run, per controller scope. Task 7/8 remain outside this task.

## Primary references

- Microsoft Learn, [Troubleshoot query performance differences between applications and SSMS](https://learn.microsoft.com/en-us/troubleshoot/sql/database-engine/performance/troubleshoot-application-slow-ssms-fast): application clients default to `ARITHABORT OFF`, while SSMS defaults to `ON`; match the application setting when reproducing.
- Microsoft Learn, [sp_executesql](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-executesql-transact-sql): parameter values are constants or variables, not expressions.
