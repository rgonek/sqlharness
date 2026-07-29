# Database Space Inspection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a compact read-only `space` command reporting database files, aggregate allocation, top tables, and optional per-index storage for one exact object.

**Architecture:** A thin CLI adapter dispatches one typed operation. Core executes one fixed, parameterized DMV/catalog batch and uses a focused multi-result reader; object resolution occurs inside that batch, and the command never exposes mutation or recommendation behavior.

**Tech Stack:** .NET 10, C# 14, Spectre.Console.Cli, Microsoft.Data.SqlClient, xUnit

## Global Constraints

- `space` is read-only and accepts no user SQL batch.
- It performs no shrink, recovery-model change, compression change, or index mutation.
- Output contains database files, reserved/used/data MB, top tables by reserved space, and optional per-index detail only for `--object`.
- Default `--top` is `25`; accepted bounds are `1..500`; timeout bounds are `1..300`.
- `--object` accepts one exact `name` or `schema.name`; missing or ambiguous objects fail validation with exit `2`.
- Bind all filters and limits; never concatenate unresolved identifiers.
- Keep output compact and deterministic; prefer `--json`.
- Preserve target resolution, authentication, identity verification, redaction, and exit codes `0/2/3/4/5/6`.

---

### Task 1: Add space contracts, parser, and registration

**Files:**
- Modify: `src/SqlHarness.Core/Contracts.cs`
- Modify: `src/SqlHarness.Cli/SqlHarnessCli.cs`
- Create: `src/SqlHarness.Cli/Commands/SpaceCommand.cs`
- Create: `tests/SqlHarness.Tests/Cli/SpaceCommandTests.cs`

**Interfaces:**
- Consumes: `TargetSettings.TryTarget` and `SqlHarnessCommand<TSettings>.Dispatch`.
- Produces: `SqlHarnessSpaceOperation`, `SqlHarnessSpaceReport`, `DatabaseFileSpaceReport`, `DatabaseAllocationReport`, `TableSpaceReport`, and `IndexSpaceReport`.

- [ ] **Step 1: Write failing parser tests**

```csharp
[Fact]
public async Task Space_dispatches_top_object_timeout_and_json()
{
    var module = new FakeModule();
    var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
        "space", "dev", "--top", "40", "--object", "dbo.Contracts",
        "--timeout", "20", "--json"
    ]);

    Assert.Equal(0, exit);
    var operation = Assert.IsType<SqlHarnessSpaceOperation>(Assert.Single(module.Operations));
    Assert.Equal(40, operation.Top);
    Assert.Equal("dbo.Contracts", operation.Object);
    Assert.Equal(20, operation.TimeoutSeconds);
}
```

Test defaults, top `0/501`, timeout `0/301`, invalid three-part object names,
profile variables, and direct target options.

- [ ] **Step 2: Run parser tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~SpaceCommandTests
```

Expected: FAIL because the command and contracts are missing.

- [ ] **Step 3: Add contracts and command**

```csharp
public sealed record SqlHarnessSpaceOperation(
    SqlTargetRequest Target, int Top, string? Object,
    int TimeoutSeconds) : SqlHarnessOperation;

public sealed record DatabaseFileSpaceReport(
    string LogicalName, string Type, string? PhysicalName,
    decimal SizeMb, decimal UsedMb, decimal FreeMb);

public sealed record DatabaseAllocationReport(
    decimal ReservedMb, decimal UsedMb, decimal DataMb);

public sealed record TableSpaceReport(
    string Schema, string Name, long Rows,
    decimal ReservedMb, decimal UsedMb, decimal DataMb);

public sealed record IndexSpaceReport(
    string Schema, string Table, string Index, string Type,
    decimal ReservedMb, decimal UsedMb, decimal DataMb, string? Compression);

public sealed record SqlHarnessSpaceReport(
    SqlHarnessTargetIdentityReport Target,
    IReadOnlyList<DatabaseFileSpaceReport> Files,
    DatabaseAllocationReport Allocation,
    IReadOnlyList<TableSpaceReport> Tables,
    IReadOnlyList<IndexSpaceReport> Indexes);
```

Register `SpaceCommand`; default `Top=25`, `TimeoutSeconds=30`.

- [ ] **Step 4: Run parser tests**

Run the Task 1 test command.

Expected: PASS.

- [ ] **Step 5: Commit contracts and parser**

```powershell
git add src/SqlHarness.Core/Contracts.cs src/SqlHarness.Cli/SqlHarnessCli.cs src/SqlHarness.Cli/Commands/SpaceCommand.cs tests/SqlHarness.Tests/Cli/SpaceCommandTests.cs
git commit -m "feat: add space command contracts"
```

### Task 2: Define the fixed DMV batch and parameters

**Files:**
- Create: `src/SqlHarness.Core/Space.cs`
- Create: `tests/SqlHarness.Tests/SpaceQueryTests.cs`

**Interfaces:**
- Consumes: `top`, parsed object schema, and object name.
- Produces: `SpaceQuery.Sql` and `SpaceQuery.Parameters(int, string?, string?)`.

- [ ] **Step 1: Write SQL safety and parameter tests**

```csharp
[Fact]
public void Space_batch_is_fixed_read_only_and_binds_every_input()
{
    var parameters = SpaceQuery.Parameters(25, "dbo", "Contracts");

    Assert.DoesNotContain("DBCC", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("ALTER ", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("SHRINK", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Contracts", SpaceQuery.Sql, StringComparison.Ordinal);
    Assert.Collection(parameters,
        p => Assert.Equal("@top", p.Name),
        p => Assert.Equal("@objectSchema", p.Name),
        p => Assert.Equal("@objectName", p.Name));
}
```

Assert the SQL references `sys.database_files`,
`FILEPROPERTY(name,'SpaceUsed')`, `sys.dm_db_partition_stats`,
`sys.tables`, `sys.schemas`, `sys.indexes`, and `sys.partitions`, and contains
deterministic ordering by reserved pages then schema/name.

- [ ] **Step 2: Run query tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~SpaceQueryTests
```

Expected: FAIL because `SpaceQuery` is missing.

- [ ] **Step 3: Add the fixed five-result batch**

Return result sets in this order:

1. exact object match count;
2. database files;
3. one aggregate allocation row;
4. top `@top` tables;
5. per-index rows when `@objectName IS NOT NULL`, otherwise an empty shaped set.

Calculate MB as `CAST(pages AS decimal(19,2)) * 8 / 1024`. Count table rows
from `index_id IN (0,1)`. Aggregate reserved/used/data pages consistently from
`sys.dm_db_partition_stats`. Derive compression from `sys.partitions` and emit
`MIXED` when an index spans more than one compression description.

- [ ] **Step 4: Run query tests**

Run the Task 2 test command.

Expected: PASS.

- [ ] **Step 5: Commit the DMV batch**

```powershell
git add src/SqlHarness.Core/Space.cs tests/SqlHarness.Tests/SpaceQueryTests.cs
git commit -m "feat: define read-only space query"
```

### Task 3: Parse deterministic space reports

**Files:**
- Modify: `src/SqlHarness.Core/Space.cs`
- Create: `tests/SqlHarness.Tests/SpaceReaderTests.cs`

**Interfaces:**
- Consumes: an `ISqlReader` positioned on the object-count result.
- Produces: `SpaceQuery.ReadAsync(ISqlReader, bool objectRequested, CancellationToken)` returning files, allocation, tables, indexes, and raw footprint.

- [ ] **Step 1: Write multi-result reader tests**

```csharp
[Fact]
public async Task Reader_builds_files_allocation_tables_and_indexes()
{
    var result = await SpaceQuery.ReadAsync(
        SpaceFixture.Reader(objectMatches: 1), objectRequested: true,
        CancellationToken.None);

    Assert.Single(result.Files);
    Assert.Equal(100m, result.Allocation.ReservedMb);
    Assert.Equal("Contracts", Assert.Single(result.Tables).Name);
    Assert.Equal("IX_Contracts_Date", Assert.Single(result.Indexes).Index);
    Assert.True(result.Raw.Bytes > 0);
}
```

Cover zero object matches, two object matches, missing result sets, duplicate
allocation rows, decimal conversion under `pl-PL`, `DBNull` physical path,
empty index set without object mode, and cancellation.

- [ ] **Step 2: Run reader tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~SpaceReaderTests
```

Expected: FAIL because the reader is missing.

- [ ] **Step 3: Implement the reader**

Use a result type:

```csharp
internal sealed record CollectedSpaceReport(
    IReadOnlyList<DatabaseFileSpaceReport> Files,
    DatabaseAllocationReport Allocation,
    IReadOnlyList<TableSpaceReport> Tables,
    IReadOnlyList<IndexSpaceReport> Indexes,
    OutputFootprint Raw);
```

Feed every column/row into `CanonicalResultAccumulator`. When object mode is
requested, require match count exactly one; throw `SqlHarnessSafetyException`
for zero or multiple matches. When no object was requested, require match count
zero. Parse numeric values with `Convert` plus invariant culture.

- [ ] **Step 4: Run reader tests**

Run the Task 3 test command.

Expected: PASS.

- [ ] **Step 5: Commit report parsing**

```powershell
git add src/SqlHarness.Core/Space.cs tests/SqlHarness.Tests/SpaceReaderTests.cs
git commit -m "feat: parse database space reports"
```

### Task 4: Execute space through the module

**Files:**
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`
- Create: `tests/SqlHarness.Tests/SpaceTests.cs`

**Interfaces:**
- Consumes: `SpaceQuery.Sql`, parameters, and reader.
- Produces: module handling of `SqlHarnessSpaceOperation` with deferred gain receipt.

- [ ] **Step 1: Write module outcome tests**

```csharp
[Fact]
public async Task Space_executes_once_on_verified_target()
{
    var session = SpaceFixture.Session(objectMatches: 0);
    var outcome = await Module(session).ExecuteAsync(
        new SqlHarnessSpaceOperation(Target(), 25, null, 30));

    Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
    Assert.IsType<SqlHarnessSpaceReport>(outcome.Report);
    Assert.Single(session.Commands);
    Assert.Equal(SpaceQuery.Sql, session.Commands[0].Sql);
}
```

Test validation before authentication, auth `3`, mismatch `4`, SQL failure `5`,
object not found/ambiguous `2`, redaction, raw footprint, and gain receipt
completion/storage failure.

- [ ] **Step 2: Run module tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~SpaceTests
```

Expected: FAIL because module dispatch is missing.

- [ ] **Step 3: Add module dispatch**

Parse object input before target resolution: zero dots means name only, one dot
means schema/name, more dots or empty parts fail validation. Resolve target,
connect once, execute the fixed batch once, read it fully, then create
`SqlHarnessSpaceReport(session.Identity, collected.Files, collected.Allocation,
collected.Tables, collected.Indexes)`. Use command name `"space"` in the
deferred gain receipt.

- [ ] **Step 4: Run space tests**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~SpaceTests|FullyQualifiedName~SpaceReaderTests|FullyQualifiedName~SpaceQueryTests"
```

Expected: PASS.

- [ ] **Step 5: Commit module execution**

```powershell
git add src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/SpaceTests.cs
git commit -m "feat: inspect database storage footprint"
```

### Task 5: Render and account for space

**Files:**
- Modify: `src/SqlHarness.Cli/Commands/Renderer.cs`
- Modify: `src/SqlHarness.Core/GainStore.cs`
- Modify: `tests/SqlHarness.Tests/Cli/SpaceCommandTests.cs`
- Modify: `tests/SqlHarness.Tests/GainStoreTests.cs`

**Interfaces:**
- Consumes: `SqlHarnessSpaceReport`.
- Produces: bounded text/JSON rendering and `SqlHarnessGainReport.Space`.

- [ ] **Step 1: Write output and gain tests**

Assert text sections appear in order (`Files`, `Allocation`, `Tables`,
`Indexes` only when nonempty), decimals render invariantly, physical paths
remain only in file rows, JSON round-trips, and:

```csharp
Assert.Equal(1, gain.Space.Executions);
```

- [ ] **Step 2: Run output/gain tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~SpaceCommandTests|FullyQualifiedName~GainStoreTests"
```

Expected: FAIL because renderer and gain scope are absent.

- [ ] **Step 3: Add compact rendering and aggregation**

Render MB with `0.##` invariant formatting. Add a `Space` summary to
`SqlHarnessGainReport`; accept and aggregate `"space"` while retaining
compatibility with existing JSONL records.

- [ ] **Step 4: Run output/gain tests**

Run the Task 5 test command.

Expected: PASS.

- [ ] **Step 5: Commit output and accounting**

```powershell
git add src/SqlHarness.Cli/Commands/Renderer.cs src/SqlHarness.Core/GainStore.cs tests/SqlHarness.Tests/Cli/SpaceCommandTests.cs tests/SqlHarness.Tests/GainStoreTests.cs
git commit -m "feat: render and account for space reports"
```

### Task 6: Document the workflow and run the full gate

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `skills/sqlharness/SKILL.md`
- Modify: `tests/SqlHarness.Tests/ContractsTests.cs`
- Modify: `tests/SqlHarness.Tests/SmokeTests.cs`

**Interfaces:**
- Consumes: completed `space` contract.
- Produces: synchronized safety guidance, PowerShell examples, and verified release build.

- [ ] **Step 1: Add failing documentation assertions**

Require docs/help to include `space`, `--top`, `--object`, read-only DMV
wording, and the explicit prohibition on shrink/recovery/compression mutation.

- [ ] **Step 2: Run documentation tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~ContractsTests|FullyQualifiedName~SmokeTests"
```

Expected: FAIL on missing space documentation.

- [ ] **Step 3: Add synchronized examples**

```powershell
sqlharness space prod-eu --var tenant=acme --var env=uat --top 25 --json
sqlharness space prod-eu --var tenant=acme --var env=uat --object dbo.Contracts --json
```

State that the command diagnoses storage only and that any mutation still
requires a separately approved `query --allow-mutation` batch.

- [ ] **Step 4: Run the complete gate**

```powershell
dotnet format .\SqlHarness.sln --verify-no-changes
dotnet build .\SqlHarness.sln -c Release
dotnet test .\SqlHarness.sln -c Release --no-build
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- --help
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- space --help
```

Expected: formatting exit `0`, build succeeds with zero errors, all tests pass,
and help matches the documented contract.

- [ ] **Step 5: Commit documentation**

```powershell
git add README.md AGENTS.md skills/sqlharness/SKILL.md tests/SqlHarness.Tests/ContractsTests.cs tests/SqlHarness.Tests/SmokeTests.cs
git commit -m "docs: document database space inspection"
```
