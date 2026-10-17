# Task 5 report: table IO survives message caps

## Implementation

Session messages now carry the provider message number and text through `ISqlSession`, SQL Server, PostgreSQL, and the test fakes. SQL Server consumes every `SqlError` in `SqlInfoMessageEventArgs.Errors`; PostgreSQL notices use number 0. `SessionMessageBuffer` streams engine message 3615 into a per-command `TableIoAccumulator` before applying its existing message and character caps. Consumption returns the counters and `TableIoComplete`, then resets the accumulator. Default `ISqlSession.ConsumeMessages` also aggregates numbered messages, so default fake implementations preserve IO evidence.

The SQL Server dialect parses IO only from message 3615 and timing only from 3612/3613. `TableIoComplete` is a journal-only artifact field; the journal builder uses it to retain availability after raw text is capped. Message 0 text that imitates statistics is ignored. Existing raw-message budgets and MCP output semantics remain intact. The required >1,000-message `JournalBenchmarkBuilder` test verifies table IO remains available after message eviction. Task 7 normalization/API work remains deferred.

## TDD evidence

**RED command**

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~StatisticsIoDetailParserTests"
```

**RED output:** build failed with `CS0246`: `TableIoAccumulator` was not found. This was the expected missing-implementation failure for the new accumulator tests.

**GREEN Core command**

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~StatisticsIoDetailParserTests|FullyQualifiedName~DialectMessageBudgetTests|FullyQualifiedName~JournalBenchmarkBuilderTests|FullyQualifiedName~SqlHarnessCompareTests|FullyQualifiedName~SqlHarnessMeasureTests|FullyQualifiedName~MeasureParameterSetExecutionTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~QueryResultCollectorTests|FullyQualifiedName~SqlExecutionTests|FullyQualifiedName~CompareRegressionOutputTests"
```

**GREEN output:** `Failed: 0, Passed: 281, Skipped: 0, Total: 281`.

**MCP per-case session fallout command**

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Measure_matrix_uses_distinct_sessions_and_reports_zero_based_set_names"
```

**Output:** `Failed: 0, Passed: 1, Skipped: 0, Total: 1`. The stale test expectation was updated from one session to two, consistent with Task 4's per-case sessions.

`git diff --check` passed. No full repository gate or Linux gate was run; those remain with the controller.

## Engine-number verification

No live SQL Server proof is claimed. `SQLHARNESS_INTEGRATION_CONNECTION_STRING` was absent, and no external database was provisioned. The implementation pins the numbers to Microsoft documentation: [Database Engine events and errors 3000 to 3999](https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors-3000-to-3999?view=sql-server-ver17) documents 3612/3613/3615, [SqlInfoMessageEventArgs.Errors](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlclient.sqlinfomessageeventargs.errors?view=sqlclient-dotnet-7.1) exposes the collection, and [SqlError.Number](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlclient.sqlerror.number?view=sqlclient-dotnet-core-7.0) provides each engine number. Numbered fixture tests pin parser behavior. Actual live emission remains unverified.

## Files changed

- Core: `Artifacts.cs`, `BenchmarkCollector.cs`, `Diagnostics.cs`, `Dialect/SqlServerDialect.cs`, `Journal/JournalBenchmarkBuilder.cs`, `Postgres/NpgsqlSessionFactory.cs`, `Postgres/PostgresBenchmark.cs`, `QueryResultCollector.cs`, `SqlExecution.cs`.
- Tests and fakes: `tests/SqlHarness.Mcp.Tests/McpCancellationTests.cs`, `McpMappingTests.cs`, `McpRequestScopeIsolationTests.cs`; and `tests/SqlHarness.Tests/BenchmarkRunTests.cs`, `CompareMatrixTests.cs`, `CompareRegressionOutputTests.cs`, `CompareTests.cs`, `CredentialRedactionTests.cs`, `Dashboard/MutationExecutorTests.cs`, `DialectMessageBudgetTests.cs`, `IndexInventoryCaptureTests.cs`, `Integration/BenchmarkSessionIntegrationTests.cs`, `Journal/JournalBenchmarkBuilderTests.cs`, `MeasureParameterSetExecutionTests.cs`, `MeasureTests.cs`, `Postgres/PostgresBenchmarkTests.cs`, `Postgres/PostgresParameterTests.cs`, `Postgres/PostgresQueryTests.cs`, `Postgres/PostgresReadOnlyRuntimeTests.cs`, `QueryResultCollectorTests.cs`, `QueryTests.cs`, `SnapshotScopeTests.cs`, `SnapshotTests.cs`, `SqlExecutionProviderNullabilityTests.cs`, `SqlExecutionTests.cs`, `StatisticsIoDetailParserTests.cs`, `TimedSessionFactoryTests.cs`, `WatchNdjsonTests.cs`, `WatchTests.cs`.
- Report: `.superpowers/sdd/079-optimization-evidence-core/task-5-report.md`.
- Plan tracking: `plans/079-optimization-evidence-core.md`, `plans/README.md`.

## Self-review and concerns

- Numbered SQL Server errors are processed individually rather than assuming one `InfoMessage` event contains one error. User-authored message text cannot create IO or time metrics without the matching engine number.
- IO accumulation precedes eviction, is reset per consume, and marks the result incomplete if an engine IO message cannot be parsed or exceeds the per-message bound. Existing agent-visible message truncation remains separate from journal IO completeness.
- The MCP session-count assertion change is integration fallout from the already-landed Task 4 behavior; it does not alter MCP output semantics.
- No live SQL Server fixture was available, so only official-source and numbered-fixture evidence is claimed. No other unresolved Task 5 concern found.

## Tooling correction

An earlier bulk replacement omitted the worktree working directory and mechanically edited 25 test files in the primary checkout. Its patch was captured as `accidental-primary-test-edits.patch`; only those edits were restored, and the primary checkout was verified clean. All subsequent commands targeted this worktree explicitly.
