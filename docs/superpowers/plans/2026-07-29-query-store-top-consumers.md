# Query Store Top Consumers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a read-only `qstop` command that ranks logical Query Store queries by cumulative elapsed duration while keeping SQL text out of stdout and safe errors.

**Architecture:** A thin Spectre command parses bounds into a typed operation. Core executes one fixed parameterized Query Store batch, parses value-free metrics plus sensitive SQL text into separate models, atomically persists the text, and returns only the compact public report.

**Tech Stack:** .NET 10, C# 14, Spectre.Console.Cli, Microsoft.Data.SqlClient, System.Text.Json, xUnit

## Global Constraints

- The command is read-only and exposes no mutation flags.
- One item represents one Query Store `query_id`, aggregating all contributing plans and runtime intervals.
- Default ranking is weighted total elapsed duration over `24h`; default top is `20`.
- `--top` accepts `1..500`; `--timeout` accepts `1..300`.
- `--window` accepts positive integral `m`, `h`, or `d`, from one minute through 31 days.
- Runtime data is ranked by total duration, total CPU, execution count, then `query_id`.
- Stdout, stderr, public reports, safe errors, filenames, and gain records contain no SQL text.
- Exact SQL text for returned top queries exists only in the locally sensitive artifact.
- Query Store `READ_WRITE` and `READ_ONLY` are readable; an empty window returns `0`.
- Query Store `OFF`, `ERROR`, unreadable catalogs, malformed results, and SQL failures return `5`.
- Artifact persistence failure returns `6`.
- Target resolution, authentication, observed identity verification, redaction, and deferred gain accounting reuse existing SQLHarness contracts.

---

### Task 1: Define public contracts and parse the CLI command

**Files:**
- Modify: `src/SqlHarness.Core/Contracts.cs`
- Modify: `src/SqlHarness.Cli/SqlHarnessCli.cs`
- Create: `src/SqlHarness.Cli/Commands/QueryStoreTopCommand.cs`
- Create: `tests/SqlHarness.Tests/Cli/QueryStoreTopCommandTests.cs`

**Interfaces:**
- Consumes: `TargetSettings.TryTarget` and `SqlHarnessCommand<TSettings>.Dispatch`.
- Produces: `SqlHarnessQueryStoreTopOperation`, `QueryStoreTopItemReport`, `SqlHarnessQueryStoreTopReport`, and `QueryStoreWindowParser.Parse(string)`.

- [ ] **Step 1: Write failing parser tests**

```csharp
[Fact]
public async Task Qstop_dispatches_explicit_bounds_and_json()
{
    var module = new FakeModule();
    var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
        "qstop", "dev", "--var", "env=uat", "--top", "30",
        "--window", "2h", "--timeout", "15", "--json"
    ]);

    Assert.Equal(0, exit);
    var operation = Assert.IsType<SqlHarnessQueryStoreTopOperation>(
        Assert.Single(module.Operations));
    Assert.Equal(30, operation.Top);
    Assert.Equal(120, operation.WindowMinutes);
    Assert.Equal(15, operation.TimeoutSeconds);
    Assert.Equal("uat", operation.Target.Vars["env"]);
}
```

Add exact tests for defaults (`20`, `1440`, `30`), `1m`, `24h`, `31d`,
rejection of `0m`, `1.5h`, `24` without suffix, unknown suffix, overflow,
`32d`, top `0/501`, timeout `0/301`, and preservation of all direct-target
options. Assert parsing failures dispatch no operation and return exit `2`.

- [ ] **Step 2: Run parser tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~QueryStoreTopCommandTests
```

Expected: FAIL because the operation, parser, and command are missing.

- [ ] **Step 3: Add exact public contracts**

```csharp
public sealed record SqlHarnessQueryStoreTopOperation(
    SqlTargetRequest Target, int Top, int WindowMinutes,
    int TimeoutSeconds) : SqlHarnessOperation;

public sealed record QueryStoreTopItemReport(
    long QueryId,
    string QueryHash,
    string? ObjectName,
    long ExecutionCount,
    int PlanCount,
    decimal TotalDurationMilliseconds,
    decimal AverageDurationMilliseconds,
    decimal MaximumDurationMilliseconds,
    decimal TotalCpuMilliseconds,
    decimal AverageCpuMilliseconds,
    decimal MaximumCpuMilliseconds,
    decimal TotalLogicalReads,
    decimal AverageLogicalReads,
    decimal MaximumLogicalReads,
    DateTimeOffset LastExecutionAt);

public sealed record SqlHarnessQueryStoreTopReport(
    SqlHarnessTargetIdentityReport Target,
    int WindowMinutes,
    int Top,
    IReadOnlyList<QueryStoreTopItemReport> Queries,
    string? ArtifactDirectory);
```

Implement `QueryStoreWindowParser.Parse` with an anchored
`^([1-9][0-9]*)([mhd])$` regex, checked multiplication, and final
`1..44640` minute validation. Register command name `qstop`.

- [ ] **Step 4: Run parser tests**

Run the Task 1 test command.

Expected: PASS.

- [ ] **Step 5: Commit contracts and parser**

```powershell
git add src/SqlHarness.Core/Contracts.cs src/SqlHarness.Cli/SqlHarnessCli.cs src/SqlHarness.Cli/Commands/QueryStoreTopCommand.cs tests/SqlHarness.Tests/Cli/QueryStoreTopCommandTests.cs
git commit -m "feat: add qstop command contracts"
```

### Task 2: Define the fixed Query Store batch

**Files:**
- Create: `src/SqlHarness.Core/QueryStoreTop.cs`
- Create: `tests/SqlHarness.Tests/QueryStoreTopQueryTests.cs`

**Interfaces:**
- Consumes: normalized `windowMinutes` and `top`.
- Produces: `QueryStoreTopQuery.Sql` and `QueryStoreTopQuery.Parameters(int, int)`.

- [ ] **Step 1: Write SQL shape, formula, and parameter tests**

```csharp
[Fact]
public void Batch_is_fixed_read_only_and_binds_window_and_top()
{
    var parameters = QueryStoreTopQuery.Parameters(windowMinutes: 120, top: 30);

    Assert.DoesNotContain("ALTER ", QueryStoreTopQuery.Sql,
        StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("OPTION (RECOMPILE)", QueryStoreTopQuery.Sql,
        StringComparison.OrdinalIgnoreCase);
    Assert.Contains("avg_duration * rs.count_executions",
        QueryStoreTopQuery.Sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("avg_cpu_time * rs.count_executions",
        QueryStoreTopQuery.Sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("avg_logical_io_reads * rs.count_executions",
        QueryStoreTopQuery.Sql, StringComparison.OrdinalIgnoreCase);
    Assert.Collection(parameters,
        p => { Assert.Equal("@windowMinutes", p.Name); Assert.Equal(120, p.Value); },
        p => { Assert.Equal("@top", p.Name); Assert.Equal(30, p.Value); });
}
```

Require references to every catalog named by the spec, interval-overlap
filtering, regular execution type filtering, grouping by `query_id`, distinct
plan count, uppercase hex hash without `0x`, schema-qualified object context,
and deterministic order by total duration, total CPU, executions, query ID.

- [ ] **Step 2: Run query tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~QueryStoreTopQueryTests
```

Expected: FAIL because `QueryStoreTopQuery` is absent.

- [ ] **Step 3: Add the fixed three-result batch**

Return result sets in this exact order:

1. one state row: `actual_state_desc`;
2. ranked value-free metrics;
3. `query_id`, query hash, and exact `query_sql_text` for the same ranked IDs.

Use a table variable `@topQueries` to materialize the ranking once so metrics
and text cannot disagree. Weight totals with `count_executions`; divide
weighted duration/CPU totals by `1000.0` for milliseconds. Keep logical reads
as pages. Use:

```sql
WHERE rsi.start_time < SYSUTCDATETIME()
  AND rsi.end_time >= DATEADD(minute, -@windowMinutes, SYSUTCDATETIME())
  AND rs.execution_type = 0
```

Order before `TOP (@top)` and again when returning metrics. The text result
joins only IDs already stored in `@topQueries`.

- [ ] **Step 4: Run query tests**

Run the Task 2 test command.

Expected: PASS.

- [ ] **Step 5: Commit the fixed batch**

```powershell
git add src/SqlHarness.Core/QueryStoreTop.cs tests/SqlHarness.Tests/QueryStoreTopQueryTests.cs
git commit -m "feat: define query store ranking batch"
```

### Task 3: Parse public metrics and sensitive query text separately

**Files:**
- Modify: `src/SqlHarness.Core/QueryStoreTop.cs`
- Create: `tests/SqlHarness.Tests/QueryStoreTopReaderTests.cs`

**Interfaces:**
- Consumes: `ISqlReader` positioned on the Query Store state result.
- Produces: `CollectedQueryStoreTop`, `SensitiveQueryStoreText`, and `QueryStoreTopQuery.ReadAsync(ISqlReader, CancellationToken)`.

- [ ] **Step 1: Write reader and secrecy tests**

```csharp
[Fact]
public async Task Reader_separates_public_metrics_from_sensitive_text()
{
    var collected = await QueryStoreTopQuery.ReadAsync(
        Fixture.Reader(state: "READ_WRITE", querySqlText: "SELECT Secret FROM dbo.T"),
        CancellationToken.None);

    var item = Assert.Single(collected.Queries);
    Assert.Equal(42, item.QueryId);
    Assert.Equal("A1B2", item.QueryHash);
    Assert.Equal("SELECT Secret FROM dbo.T",
        Assert.Single(collected.SensitiveTexts).QuerySqlText);
    Assert.DoesNotContain("SELECT Secret", JsonSerializer.Serialize(collected.Queries),
        StringComparison.Ordinal);
}
```

Cover `READ_ONLY`, successful empty sets, `OFF`, `ERROR`, unknown state,
missing/extra state rows, missing result sets, mismatched metric/text query IDs,
duplicate text IDs, uppercase hash, optional object name, invariant decimals,
UTC timestamps, cancellation, and canonical raw footprint.

- [ ] **Step 2: Run reader tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~QueryStoreTopReaderTests
```

Expected: FAIL because the reader models and method are missing.

- [ ] **Step 3: Implement strict result parsing**

```csharp
internal sealed record SensitiveQueryStoreText(
    long QueryId, string QueryHash, string QuerySqlText);

internal sealed record CollectedQueryStoreTop(
    IReadOnlyList<QueryStoreTopItemReport> Queries,
    IReadOnlyList<SensitiveQueryStoreText> SensitiveTexts,
    OutputFootprint RawFootprint);
```

Accept state only when it equals `READ_WRITE` or `READ_ONLY`
case-insensitively. Throw `QueryStoreUnavailableException(state)` otherwise;
its message may contain only a safe normalized state matching
`^[A-Z_]{1,32}$`, or the word `unknown`.

Feed all three result sets, including SQL text, into
`CanonicalResultAccumulator` for raw gain accounting. Require exactly one text
row for every public query and no additional IDs. Do not add SQL text to any
public report type or exception.

- [ ] **Step 4: Run reader tests**

Run the Task 3 test command.

Expected: PASS.

- [ ] **Step 5: Commit strict parsing**

```powershell
git add src/SqlHarness.Core/QueryStoreTop.cs tests/SqlHarness.Tests/QueryStoreTopReaderTests.cs
git commit -m "feat: parse query store consumers safely"
```

### Task 4: Write atomic locally sensitive artifacts

**Files:**
- Modify: `src/SqlHarness.Core/SqlHarnessPaths.cs`
- Create: `src/SqlHarness.Core/QueryStoreArtifacts.cs`
- Create: `tests/SqlHarness.Tests/QueryStoreArtifactWriterTests.cs`
- Modify: `tests/SqlHarness.Tests/SqlHarnessPathsTests.cs`

**Interfaces:**
- Consumes: public report without directory, sensitive text rows, and resolved database name.
- Produces: `IQueryStoreArtifactWriter.Write(SqlHarnessQueryStoreTopReport, IReadOnlyList<SensitiveQueryStoreText>, string)` and final directory path.

- [ ] **Step 1: Write artifact publication and rollback tests**

```csharp
[Fact]
public void Writer_keeps_sql_only_in_queries_jsonl()
{
    using var temp = new TempDirectory();
    var writer = new QueryStoreArtifactWriter(
        temp.Path, () => DateTimeOffset.UnixEpoch);
    var directory = writer.Write(
        Fixture.Report(artifactDirectory: null),
        [new SensitiveQueryStoreText(42, "A1B2", "SELECT Secret FROM dbo.T")],
        "wind/../../unsafe");

    var reportJson = File.ReadAllText(Path.Combine(directory, "report.json"));
    var queriesJsonl = File.ReadAllText(Path.Combine(directory, "queries.jsonl"));
    Assert.DoesNotContain("SELECT Secret", reportJson, StringComparison.Ordinal);
    Assert.Contains("SELECT Secret", queriesJsonl, StringComparison.Ordinal);
    Assert.StartsWith(Path.GetFullPath(temp.Path), Path.GetFullPath(directory),
        StringComparison.OrdinalIgnoreCase);
}
```

Test unique safe directory names, final directory embedded in `report.json`,
one JSONL row per returned query, empty successful result producing an empty
`queries.jsonl`, text/metric ID mismatch rejection, report write failure,
JSONL write failure, directory-move failure, cleanup attempts after failures,
and preservation of the original exception if cleanup also fails.

- [ ] **Step 2: Run artifact tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~QueryStoreArtifactWriterTests|FullyQualifiedName~SqlHarnessPathsTests"
```

Expected: FAIL because the path and writer are absent.

- [ ] **Step 3: Implement the focused writer**

Add:

```csharp
public static string QueryStoreDir => Path.Combine(Home, "query-store");

internal interface IQueryStoreArtifactWriter
{
    string Write(
        SqlHarnessQueryStoreTopReport report,
        IReadOnlyList<SensitiveQueryStoreText> texts,
        string target);
}
```

Use web-default camel-case JSON, indented `report.json`, compact JSONL, UTF-8
without BOM, the existing `[A-Za-z0-9_-]` target allowlist, a staging directory,
and one atomic `Directory.Move(staging, final)`. Serialize a copy of the report
whose `ArtifactDirectory` equals the final path. Clean the staging tree
best-effort on failure.

- [ ] **Step 4: Run artifact/path tests**

Run the Task 4 test command.

Expected: PASS.

- [ ] **Step 5: Commit artifact persistence**

```powershell
git add src/SqlHarness.Core/SqlHarnessPaths.cs src/SqlHarness.Core/QueryStoreArtifacts.cs tests/SqlHarness.Tests/QueryStoreArtifactWriterTests.cs tests/SqlHarness.Tests/SqlHarnessPathsTests.cs
git commit -m "feat: persist sensitive query store text"
```

### Task 5: Execute qstop through the module

**Files:**
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`
- Create: `tests/SqlHarness.Tests/QueryStoreTopTests.cs`

**Interfaces:**
- Consumes: `QueryStoreTopQuery`, `IQueryStoreArtifactWriter`, target resolver, session factory, and gain store.
- Produces: complete module handling of `SqlHarnessQueryStoreTopOperation`.

- [ ] **Step 1: Write module outcome and phase tests**

```csharp
[Fact]
public async Task Qstop_executes_once_and_publishes_value_free_report()
{
    var session = Fixture.Session(state: "READ_WRITE");
    var artifacts = new CapturingQueryStoreArtifactWriter("qstop-artifacts");
    var outcome = await Module(session, artifacts).ExecuteAsync(
        new SqlHarnessQueryStoreTopOperation(Target(), 20, 1440, 30));

    Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
    var report = Assert.IsType<SqlHarnessQueryStoreTopReport>(outcome.Report);
    Assert.Equal("qstop-artifacts", report.ArtifactDirectory);
    Assert.Single(session.Commands);
    Assert.Single(artifacts.Texts);
    Assert.DoesNotContain("SELECT Secret", JsonSerializer.Serialize(report),
        StringComparison.Ordinal);
}
```

Test validation before authentication; auth `3`; target mismatch `4`; states
`OFF/ERROR` as `5`; permission, timeout, malformed shape, and SQL errors as `5`;
empty readable window as `0` plus artifact; writer failure as `6`; caller
cancellation; safe error redaction; raw footprint; and receipt storage failure
overriding the command exit with `6`.

- [ ] **Step 2: Run module tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~QueryStoreTopTests
```

Expected: FAIL because module dispatch and writer injection are missing.

- [ ] **Step 3: Add phase-aware orchestration**

Validate `Top`, `WindowMinutes`, and timeout in Core as defense in depth.
Resolve target, connect once, execute one `SqlExecutionCommand`, parse results,
construct a report with null artifact directory, then enter artifact phase and
write it. Return a copy with the resulting directory.

Extend the internal module constructor with optional/injected
`IQueryStoreArtifactWriter`; the public constructor uses
`new QueryStoreArtifactWriter()`. Map `QueryStoreUnavailableException` during
SQL phase to `SqlExecution`. Map writer exceptions in artifact phase to
`LocalStorage`. Complete gain accounting with command name `"qstop"`.

- [ ] **Step 4: Run module and query tests**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~QueryStoreTopTests|FullyQualifiedName~QueryStoreTopReaderTests|FullyQualifiedName~QueryStoreTopQueryTests"
```

Expected: PASS.

- [ ] **Step 5: Commit module execution**

```powershell
git add src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/QueryStoreTopTests.cs
git commit -m "feat: execute query store top consumers"
```

### Task 6: Render compact output and aggregate gain

**Files:**
- Modify: `src/SqlHarness.Cli/Commands/Renderer.cs`
- Modify: `src/SqlHarness.Core/GainStore.cs`
- Modify: `tests/SqlHarness.Tests/Cli/QueryStoreTopCommandTests.cs`
- Modify: `tests/SqlHarness.Tests/GainStoreTests.cs`

**Interfaces:**
- Consumes: `SqlHarnessQueryStoreTopReport` and deferred emission receipt.
- Produces: deterministic text/JSON rendering and `SqlHarnessGainReport.QueryStoreTop`.

- [ ] **Step 1: Write text, JSON, secrecy, and gain tests**

Assert text includes target/window/artifact and columns for ID, hash, object,
executions, plans, total/average/maximum duration, CPU, reads, and last
execution. Assert invariant formatting and an explicit empty-window line.

```csharp
Assert.DoesNotContain("SELECT Secret", output.ToString(), StringComparison.Ordinal);
Assert.Equal(1, gain.QueryStoreTop.Executions);
```

Serialize JSON and assert `queries[0]` has no property containing `sql`,
`text`, `statement`, or `batch` case-insensitively. Existing gain JSONL remains
readable.

- [ ] **Step 2: Run rendering/gain tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~QueryStoreTopCommandTests|FullyQualifiedName~GainStoreTests"
```

Expected: FAIL because rendering and gain scope are missing.

- [ ] **Step 3: Implement bounded rendering and aggregation**

Render decimals with invariant `0.##` formatting and no query text. Add
`QueryStoreTop` to `SqlHarnessGainReport`, accept `"qstop"` in validation, and
aggregate it separately while including it in total.

For an empty window render:

```text
No Query Store runtime data in the selected 1440-minute window.
```

- [ ] **Step 4: Run rendering/gain tests**

Run the Task 6 test command.

Expected: PASS.

- [ ] **Step 5: Commit output and gain accounting**

```powershell
git add src/SqlHarness.Cli/Commands/Renderer.cs src/SqlHarness.Core/GainStore.cs tests/SqlHarness.Tests/Cli/QueryStoreTopCommandTests.cs tests/SqlHarness.Tests/GainStoreTests.cs
git commit -m "feat: render and account for qstop"
```

### Task 7: Synchronize docs and run the complete gate

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `skills/sqlharness/SKILL.md`
- Modify: `tests/SqlHarness.Tests/ContractsTests.cs`
- Modify: `tests/SqlHarness.Tests/SmokeTests.cs`

**Interfaces:**
- Consumes: completed qstop behavior.
- Produces: synchronized safety/workflow guidance and a verified release build.

- [ ] **Step 1: Add failing documentation contract tests**

Require root help and all three documents to mention `qstop`, default `24h`,
aggregation per `query_id`, total-duration ranking, empty versus unavailable
telemetry, SQL-free stdout, sensitive artifact, and the handoff to
`measure`/`compare`/`plan`.

- [ ] **Step 2: Run documentation tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~ContractsTests|FullyQualifiedName~SmokeTests"
```

Expected: FAIL on missing qstop help and documentation.

- [ ] **Step 3: Add synchronized PowerShell guidance**

```powershell
sqlharness qstop prod-eu --var tenant=acme --var env=uat --top 20 --window 24h --json
```

Document that stdout contains IDs/hashes/metrics only, SQL exists in
`artifactDirectory/queries.jsonl`, and the artifact must not be pasted or
published without explicit review. Explain that a high rank is a lead to
measure, not proof of a defective query.

- [ ] **Step 4: Run the complete verification gate**

```powershell
dotnet format .\SqlHarness.sln --verify-no-changes
dotnet build .\SqlHarness.sln -c Release
dotnet test .\SqlHarness.sln -c Release --no-build
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- --help
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- qstop --help
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- gain --json
```

Expected: formatting exits `0`, build succeeds with zero errors, all tests
pass, qstop help matches the documented options, and gain JSON contains the
qstop scope.

- [ ] **Step 5: Inspect and commit documentation**

```powershell
git diff --check
git status --short
git add README.md AGENTS.md skills/sqlharness/SKILL.md tests/SqlHarness.Tests/ContractsTests.cs tests/SqlHarness.Tests/SmokeTests.cs
git commit -m "docs: document query store top consumers"
```

Expected: only the five listed documentation/contract files are staged for
this commit.
