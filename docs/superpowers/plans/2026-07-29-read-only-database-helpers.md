# Read-Only Database Helpers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `ping`, approximate-or-exact `counts`, and exact `schema --object` inspection as compact read-only alternatives to recurring agent-authored SQL.

**Architecture:** Each command remains a thin Spectre adapter over a typed Core operation. `ping` and `counts` use fixed internal parameterized SQL and focused readers; `schema --object` extends the established schema query/reader so columns, indexes, and foreign keys retain one source of truth.

**Tech Stack:** .NET 10, C# 14, Spectre.Console.Cli, Microsoft.Data.SqlClient, xUnit

## Global Constraints

- Default remains **read-only**.
- `ping`, `counts`, and enhanced `schema` never accept arbitrary user SQL batches.
- Target profiles, `--var`, `--unsafe-direct`, authentication, target-identity checks, redaction, and exit mappings reuse the existing contracts.
- `counts` uses partition statistics by default; exact `COUNT_BIG(*)` is opt-in via `--exact`.
- Unknown explicit `--table` names fail validation with exit `2`; they never appear as silent zero counts.
- `schema --object` selects exactly one table or view and fails when missing or ambiguous.
- All identifiers supplied by users are bound as values or safely quoted after catalog resolution; never concatenate unresolved input into executable SQL.
- Prefer `--json`; keep text output compact and deterministic.
- Internal catalog commands record gain metadata without exposing inputs or result values in gain records.

---

### Task 1: Add helper operations, reports, command registration, and CLI parsing

**Files:**
- Modify: `src/SqlHarness.Core/Contracts.cs`
- Modify: `src/SqlHarness.Cli/SqlHarnessCli.cs`
- Create: `src/SqlHarness.Cli/Commands/PingCommand.cs`
- Create: `src/SqlHarness.Cli/Commands/CountsCommand.cs`
- Modify: `src/SqlHarness.Cli/Commands/SchemaCommand.cs`
- Create: `tests/SqlHarness.Tests/Cli/HelperCommandTests.cs`
- Modify: `tests/SqlHarness.Tests/Cli/SchemaCommandTests.cs`

**Interfaces:**
- Consumes: `TargetSettings.TryTarget` and `SqlHarnessCommand<TSettings>.Dispatch`.
- Produces: `SqlHarnessPingOperation`, `SqlHarnessCountsOperation`, `SqlHarnessPingReport`, `SqlHarnessCountReport`, `SqlHarnessCountsReport`, and `SqlHarnessSchemaOperation.Object`.

- [ ] **Step 1: Write failing parser tests**

```csharp
[Fact]
public async Task Counts_dispatches_explicit_tables_and_exact_mode()
{
    var module = new FakeModule(CountsReport());
    var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
        "counts", "dev", "--table", "Contracts", "--table", "audit.Runs",
        "--exact", "--top", "20", "--timeout", "12", "--json"
    ]);

    Assert.Equal(0, exit);
    var operation = Assert.IsType<SqlHarnessCountsOperation>(Assert.Single(module.Operations));
    Assert.Equal(["Contracts", "audit.Runs"], operation.Tables);
    Assert.True(operation.Exact);
    Assert.Equal(20, operation.Top);
    Assert.Equal(12, operation.TimeoutSeconds);
}
```

Add exact tests for `ping --timeout 5`, `counts --like "%Sync%"`, defaults
(`top=50`, approximate), `--table` combined with `--like` rejection, `top`
outside `1..500`, timeout outside `1..300`, `schema --object SyncRuns`,
`schema --object` combined with `--filter` rejection, and preservation of all
profile/direct target options.

- [ ] **Step 2: Run parser tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~HelperCommandTests|FullyQualifiedName~SchemaCommandTests"
```

Expected: FAIL because the new contracts and commands are absent.

- [ ] **Step 3: Add contracts and adapters**

```csharp
public sealed record SqlHarnessPingOperation(
    SqlTargetRequest Target, int TimeoutSeconds) : SqlHarnessOperation;

public sealed record SqlHarnessCountsOperation(
    SqlTargetRequest Target, IReadOnlyList<string> Tables, string? Like,
    int Top, bool Exact, int TimeoutSeconds) : SqlHarnessOperation;

public sealed record SqlHarnessPingReport(
    SqlHarnessTargetIdentityReport Target, string Server, string Database,
    string Login, long DurationMilliseconds);

public sealed record SqlHarnessCountReport(
    string Schema, string Name, long Rows, string Method);

public sealed record SqlHarnessCountsReport(
    SqlHarnessTargetIdentityReport Target,
    IReadOnlyList<SqlHarnessCountReport> Tables, int Omitted);
```

Extend `SqlHarnessSchemaOperation` with trailing `string? Object = null` so
existing call sites remain source-compatible. Register both commands in
`SqlHarnessCli.Create`. `PingCommand` defaults to timeout `5`; `CountsCommand`
defaults to timeout `30`, top `50`, and `Exact=false`.

- [ ] **Step 4: Run parser tests**

Run the Task 1 test command.

Expected: PASS.

- [ ] **Step 5: Commit contracts and CLI parsing**

```powershell
git add src/SqlHarness.Core/Contracts.cs src/SqlHarness.Cli/SqlHarnessCli.cs src/SqlHarness.Cli/Commands/PingCommand.cs src/SqlHarness.Cli/Commands/CountsCommand.cs src/SqlHarness.Cli/Commands/SchemaCommand.cs tests/SqlHarness.Tests/Cli/HelperCommandTests.cs tests/SqlHarness.Tests/Cli/SchemaCommandTests.cs
git commit -m "feat: add read-only helper command contracts"
```

### Task 2: Implement the fixed readiness probe

**Files:**
- Create: `src/SqlHarness.Core/Ping.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`
- Create: `tests/SqlHarness.Tests/PingTests.cs`

**Interfaces:**
- Consumes: resolved target, `ISqlSessionFactory`, and `ISqlReader`.
- Produces: `PingQuery.Sql`, `PingQuery.ReadAsync(ISqlReader, CancellationToken)`, and module handling for `SqlHarnessPingOperation`.

- [ ] **Step 1: Write fixed-SQL and outcome tests**

```csharp
[Fact]
public async Task Ping_runs_one_fixed_probe_after_identity_verification()
{
    var session = new FakeSession(Row(1, "db", "server", "login"));
    var outcome = await Module(session).ExecuteAsync(
        new SqlHarnessPingOperation(Target(), 5));

    var report = Assert.IsType<SqlHarnessPingReport>(outcome.Report);
    Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
    Assert.Equal("db", report.Database);
    Assert.Equal("login", report.Login);
    Assert.Equal(PingQuery.Sql, Assert.Single(session.Commands).Sql);
    Assert.Empty(Assert.Single(session.Commands).Parameters);
}
```

Test auth failure `3`, target mismatch `4`, SQL/network failure `5`, malformed
or extra result rows `5`, cancellation propagation, and no profile variables,
passwords, or input values in the SQL or error.

- [ ] **Step 2: Run ping tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~PingTests
```

Expected: FAIL because `PingQuery` and module dispatch are missing.

- [ ] **Step 3: Implement the probe**

Use this fixed SQL:

```sql
SELECT CAST(1 AS int) AS Ok,
       CONVERT(nvarchar(128), DB_NAME()) AS Db,
       CONVERT(nvarchar(128), @@SERVERNAME) AS Server,
       CONVERT(nvarchar(128), SUSER_SNAME()) AS Login;
```

Require exactly four columns, exactly one row, and `Ok = 1`; drain all result
sets. Return the already verified `session.Identity` plus probe values and
elapsed milliseconds. Route errors through the module’s existing phase-based
mapping and redaction.

- [ ] **Step 4: Run ping tests**

Run the Task 2 test command.

Expected: PASS.

- [ ] **Step 5: Commit ping**

```powershell
git add src/SqlHarness.Core/Ping.cs src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/PingTests.cs
git commit -m "feat: add database readiness probe"
```

### Task 3: Resolve requested tables safely for counts

**Files:**
- Create: `src/SqlHarness.Core/Counts.cs`
- Create: `tests/SqlHarness.Tests/CountsResolutionTests.cs`

**Interfaces:**
- Consumes: `SqlHarnessCountsOperation`.
- Produces: `CountsQuery.CatalogSql`, `CountsQuery.CatalogParameters`, `ResolvedCountObject`, and `CountsQuery.ReadCatalogAsync(ISqlReader, CancellationToken)`.

- [ ] **Step 1: Write catalog-resolution tests**

```csharp
[Fact]
public async Task Explicit_unqualified_name_must_resolve_once()
{
    var reader = Reader(
        ["RequestedName", "SchemaName", "ObjectName", "ObjectId", "ApproxRows"],
        ["Contracts", "dbo", "Contracts", 42, 100L]);

    var resolved = await CountsQuery.ReadCatalogAsync(
        reader, ["Contracts"], CancellationToken.None);

    Assert.Equal(new ResolvedCountObject(
        "Contracts", "dbo", "Contracts", 42, 100), Assert.Single(resolved.Objects));
}
```

Cover schema-qualified names, unknown explicit names, ambiguous unqualified
names, duplicate requested names case-insensitively, `LIKE` ordering by rows
then schema/name, default top-N mode, omitted count, and names containing `]`,
quotes, wildcard characters, or Unicode.

- [ ] **Step 2: Run resolution tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~CountsResolutionTests
```

Expected: FAIL because the counts catalog component is missing.

- [ ] **Step 3: Add parameterized catalog resolution**

```csharp
internal sealed record ResolvedCountObject(
    string RequestedName, string Schema, string Name, int ObjectId, long ApproxRows);

internal sealed record ResolvedCountSelection(
    IReadOnlyList<ResolvedCountObject> Objects, int Omitted);
```

Pass explicit names as a JSON array bound to `@tables nvarchar(max)` and resolve
them through `OPENJSON`, `PARSENAME`, `sys.tables`, `sys.schemas`, and
`sys.dm_db_partition_stats` restricted to `index_id IN (0,1)`. Bind `@like`
and `@top`; never interpolate names or patterns. Throw
`SqlHarnessSafetyException` listing only the normalized requested name when it
contains safe identifier characters; otherwise use the generic message
`Requested table was not found or was ambiguous.`

- [ ] **Step 4: Run resolution tests**

Run the Task 3 test command.

Expected: PASS.

- [ ] **Step 5: Commit catalog resolution**

```powershell
git add src/SqlHarness.Core/Counts.cs tests/SqlHarness.Tests/CountsResolutionTests.cs
git commit -m "feat: resolve count targets safely"
```

### Task 4: Execute approximate and exact counts

**Files:**
- Modify: `src/SqlHarness.Core/Counts.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`
- Create: `tests/SqlHarness.Tests/CountsTests.cs`

**Interfaces:**
- Consumes: `ResolvedCountSelection` and one verified SQL session.
- Produces: `CountsQuery.ExecuteExactAsync(ISqlSession, IReadOnlyList<ResolvedCountObject>, int, CancellationToken)` and module handling of `SqlHarnessCountsOperation`.

- [ ] **Step 1: Write approximate/exact execution tests**

```csharp
[Fact]
public async Task Approximate_mode_returns_catalog_rows_without_dynamic_count()
{
    var session = FakeSession.ForCatalog(("dbo", "Contracts", 123L));
    var outcome = await Module(session).ExecuteAsync(
        new SqlHarnessCountsOperation(Target(), ["dbo.Contracts"], null, 50, false, 30));

    var report = Assert.IsType<SqlHarnessCountsReport>(outcome.Report);
    Assert.Equal(new SqlHarnessCountReport("dbo", "Contracts", 123, "approx"),
        Assert.Single(report.Tables));
    Assert.Single(session.Commands);
}
```

For exact mode assert a second command uses only catalog-resolved,
`QUOTENAME`-escaped schema/table names, returns `COUNT_BIG(*)`, retains input
ordering for explicit tables, maps SQL failure to `5`, and never executes the
exact batch if catalog resolution fails.

- [ ] **Step 2: Run counts tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~CountsTests
```

Expected: FAIL because exact execution and module dispatch are absent.

- [ ] **Step 3: Implement execution**

Build exact SQL only from resolved catalog names:

```csharp
var statement =
    $"SELECT CAST({ordinal} AS int) AS Ordinal, COUNT_BIG(*) AS Rows " +
    $"FROM {Quote(schema)}.{Quote(name)};";

static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
```

The resolved object ID and quoted names originate from the same catalog row.
Execute all statements as one internal batch, validate one ordinal/count row
per requested object, and project reports with method `"exact"`. Approximate
mode uses the already-read partition counts and method `"approx"`.

- [ ] **Step 4: Run all counts tests**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~CountsTests|FullyQualifiedName~CountsResolutionTests"
```

Expected: PASS.

- [ ] **Step 5: Commit counts execution**

```powershell
git add src/SqlHarness.Core/Counts.cs src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/CountsTests.cs
git commit -m "feat: add table row inventory"
```

### Task 5: Extend schema inspection with exact object selection

**Files:**
- Modify: `src/SqlHarness.Core/Schema.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`
- Modify: `tests/SqlHarness.Tests/SchemaTests.cs`
- Modify: `tests/SqlHarness.Tests/Cli/SchemaCommandTests.cs`

**Interfaces:**
- Consumes: existing `SchemaQuery.Sql`, `SchemaQuery.Parameters`, and `SqlHarnessSchemaOperation.Object`.
- Produces: exact object parameters `@objectSchema`/`@objectName` and a single-object schema report.

- [ ] **Step 1: Write exact-object tests**

```csharp
[Fact]
public async Task Object_mode_binds_schema_and_name_and_returns_one_object()
{
    var session = new FakeSession(Reader(1, [["audit", "Runs", "TABLE"]]));
    var outcome = await Module(session).ExecuteAsync(
        new SqlHarnessSchemaOperation(Target(), null, 30, 50, "audit.Runs"));

    var report = Assert.IsType<SqlHarnessSchemaReport>(outcome.Report);
    Assert.Equal("Runs", Assert.Single(report.Objects).Name);
    Assert.Contains(session.Commands.Single().Parameters,
        p => p.Name == "@objectSchema" && Equals(p.Value, "audit"));
    Assert.Contains(session.Commands.Single().Parameters,
        p => p.Name == "@objectName" && Equals(p.Value, "Runs"));
}
```

Cover unqualified unique resolution, ambiguous unqualified name exit `2`,
missing object exit `2`, table/view support, exact match despite wildcard
characters, and unchanged filter/default behavior.

- [ ] **Step 2: Run schema tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~SchemaTests|FullyQualifiedName~SchemaCommandTests"
```

Expected: FAIL on the new object-mode assertions.

- [ ] **Step 3: Extend the existing batch**

Split `schema.name` with `PARSENAME` in C# only when it contains one dot;
reject more than two parts or empty parts. Bind both pieces. In SQL, select an
object when:

```sql
(@objectName IS NULL AND (@filter IS NULL OR o.name LIKE @filter))
OR
(@objectName IS NOT NULL AND o.name = @objectName
 AND (@objectSchema IS NULL OR s.name = @objectSchema))
```

Before building the report, require exactly one object in object mode. Keep the
current columns/indexes/FKs result sets and rowwise aggregation unchanged.

- [ ] **Step 4: Run schema tests**

Run the Task 5 test command.

Expected: PASS.

- [ ] **Step 5: Commit exact schema inspection**

```powershell
git add src/SqlHarness.Core/Schema.cs src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/SchemaTests.cs tests/SqlHarness.Tests/Cli/SchemaCommandTests.cs
git commit -m "feat: inspect one schema object exactly"
```

### Task 6: Render helpers and record gain scopes

**Files:**
- Modify: `src/SqlHarness.Cli/Commands/Renderer.cs`
- Modify: `src/SqlHarness.Core/GainStore.cs`
- Modify: `tests/SqlHarness.Tests/Cli/HelperCommandTests.cs`
- Modify: `tests/SqlHarness.Tests/GainStoreTests.cs`

**Interfaces:**
- Consumes: ping/count reports and emission receipts.
- Produces: compact text/JSON and `Ping`/`Counts` gain summaries.

- [ ] **Step 1: Write output and gain tests**

Assert ping text is one target/readiness line, counts text has
`schema/name/rows/method`, JSON round-trips typed reports, and one successful
record per command increments:

```csharp
Assert.Equal(1, gain.Ping.Executions);
Assert.Equal(1, gain.Counts.Executions);
```

Existing gain JSONL records without these commands must still aggregate.

- [ ] **Step 2: Run output/gain tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~HelperCommandTests|FullyQualifiedName~GainStoreTests"
```

Expected: FAIL because renderers and gain scopes are missing.

- [ ] **Step 3: Implement output and accounting**

Add `Ping` and `Counts` properties to `SqlHarnessGainReport`, accept both
command names in validation/aggregation, and render:

```text
Ready: <server>/<database> as <login>; <milliseconds> ms
Schema	Name	Rows	Method
dbo	Contracts	123	approx
```

Never render connection strings or auth material.

- [ ] **Step 4: Run output/gain tests**

Run the Task 6 test command.

Expected: PASS.

- [ ] **Step 5: Commit rendering and gain**

```powershell
git add src/SqlHarness.Cli/Commands/Renderer.cs src/SqlHarness.Core/GainStore.cs tests/SqlHarness.Tests/Cli/HelperCommandTests.cs tests/SqlHarness.Tests/GainStoreTests.cs
git commit -m "feat: render and account for database helpers"
```

### Task 7: Synchronize documentation and run the full gate

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `skills/sqlharness/SKILL.md`
- Modify: `tests/SqlHarness.Tests/ContractsTests.cs`
- Modify: `tests/SqlHarness.Tests/SmokeTests.cs`

**Interfaces:**
- Consumes: completed CLI contracts.
- Produces: synchronized PowerShell examples, safety wording, and verified release build.

- [ ] **Step 1: Add failing documentation assertions**

Require all three docs and root help to mention `ping`, `counts`, approximate
default, `--exact`, and `schema --object`. Require the skill to prefer JSON.

- [ ] **Step 2: Run documentation tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~ContractsTests|FullyQualifiedName~SmokeTests"
```

Expected: FAIL on missing helper documentation.

- [ ] **Step 3: Add synchronized examples**

```powershell
sqlharness ping prod-eu --var tenant=acme --var env=uat --json
sqlharness counts prod-eu --var tenant=acme --var env=uat --table Contracts --table SourceFiles --exact --json
sqlharness counts prod-eu --var tenant=acme --var env=uat --like "%Sync%" --json
sqlharness schema prod-eu --var tenant=acme --var env=uat --object dbo.Contracts --json
```

Explain that `counts` defaults to partition estimates and none of these
commands accepts arbitrary SQL or mutations.

- [ ] **Step 4: Run the complete verification gate**

```powershell
dotnet format .\SqlHarness.sln --verify-no-changes
dotnet build .\SqlHarness.sln -c Release
dotnet test .\SqlHarness.sln -c Release --no-build
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- --help
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- ping --help
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- counts --help
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- schema --help
```

Expected: formatting exit `0`, build succeeds with zero errors, all tests pass,
and help matches the documented options.

- [ ] **Step 5: Commit documentation**

```powershell
git add README.md AGENTS.md skills/sqlharness/SKILL.md tests/SqlHarness.Tests/ContractsTests.cs tests/SqlHarness.Tests/SmokeTests.cs
git commit -m "docs: document read-only database helpers"
```
