# Index Overlap Analysis Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a read-only `indexes` command that ranks missing-index telemetry and classifies each candidate against existing indexes without generating DDL.

**Architecture:** A thin CLI command dispatches a typed operation. One fixed Core batch returns volatile DMV evidence and complete catalog metadata; pure C# components parse bracketed candidate columns, classify overlap deterministically, and pass sensitive filter definitions only to an atomic artifact writer.

**Tech Stack:** .NET 10, C# 14, Spectre.Console.Cli, Microsoft.Data.SqlClient, System.Text.Json, SHA-256, xUnit

## Global Constraints

- `indexes` is fixed-query and read-only; it accepts no user SQL, mutation approval, or DDL output option.
- Default analysis is database-wide top `20`; `--top` accepts `1..500`.
- `--object` resolves exactly one user table by `name` or `schema.name`.
- Candidate score is `(user_seeks + user_scans) * avg_total_user_cost * (avg_user_impact / 100)`.
- Classifications are exactly `covered`, `include-gap`, `partial-key`, and `new-shape`.
- Filtered indexes never produce `covered`.
- Stdout, safe errors, filenames, and gain records contain no filter predicate, query text, DDL, credentials, or row data.
- Exact filter definitions exist only in the locally sensitive artifact.
- Missing/ambiguous object returns `2`; auth `3`; target mismatch `4`; DMV/SQL/malformed data `5`; artifact failure `6`; valid empty telemetry `0`.
- Observation start and volatility warning are always public.

---

### Task 1: Add operations, reports, parser, and CLI registration

**Files:**
- Modify: `src/SqlHarness.Core/Contracts.cs`
- Modify: `src/SqlHarness.Cli/SqlHarnessCli.cs`
- Create: `src/SqlHarness.Cli/Commands/IndexesCommand.cs`
- Create: `tests/SqlHarness.Tests/Cli/IndexesCommandTests.cs`

**Interfaces:**
- Consumes: `TargetSettings.TryTarget` and `SqlHarnessCommand<TSettings>.Dispatch`.
- Produces: `SqlHarnessIndexesOperation`, `IndexOverlapClassification`, `IndexCandidateReport`, and `SqlHarnessIndexesReport`.

- [ ] **Step 1: Write failing parser tests**

```csharp
[Fact]
public async Task Indexes_dispatches_object_top_timeout_and_json()
{
    var module = new FakeModule();
    var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
        "indexes", "dev", "--object", "dbo.Contracts",
        "--top", "30", "--timeout", "15", "--json"
    ]);

    Assert.Equal(0, exit);
    var operation = Assert.IsType<SqlHarnessIndexesOperation>(
        Assert.Single(module.Operations));
    Assert.Equal("dbo.Contracts", operation.Object);
    Assert.Equal(30, operation.Top);
    Assert.Equal(15, operation.TimeoutSeconds);
}
```

Test defaults, no-object database mode, top `0/501`, timeout `0/301`,
empty/three-part object names, profile variables, direct target options, and
absence of SQL/mutation/DDL options.

- [ ] **Step 2: Run parser tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~IndexesCommandTests
```

Expected: FAIL because the command and contracts are missing.

- [ ] **Step 3: Add exact public contracts**

```csharp
public sealed record SqlHarnessIndexesOperation(
    SqlTargetRequest Target, int Top, string? Object,
    int TimeoutSeconds) : SqlHarnessOperation;

[JsonConverter(typeof(JsonStringEnumConverter<IndexOverlapClassification>))]
public enum IndexOverlapClassification
{
    Covered, IncludeGap, PartialKey, NewShape
}

public sealed record IndexCandidateReport(
    long CandidateId, string Schema, string Table,
    IReadOnlyList<string> EqualityColumns,
    IReadOnlyList<string> InequalityColumns,
    IReadOnlyList<string> IncludeColumns,
    long UserSeeks, long UserScans,
    decimal AverageTotalUserCost, decimal AverageUserImpactPercent,
    decimal CumulativeImpactScore,
    DateTimeOffset? LastUserSeek, DateTimeOffset? LastUserScan,
    IndexOverlapClassification Classification,
    string? BestExistingIndex,
    int MatchedKeyColumnCount, int CandidateKeyColumnCount,
    IReadOnlyList<string> MissingIncludeColumns,
    bool? ExistingIndexDisabled, bool? ExistingIndexHasFilter,
    string? ExistingIndexFilterHash);

public sealed record SqlHarnessIndexesReport(
    SqlHarnessTargetIdentityReport Target,
    DateTimeOffset ObservationSince, DateTimeOffset ObservedAt,
    int Top, string? ObjectFilter, IReadOnlyList<string> Warnings,
    IReadOnlyList<IndexCandidateReport> Candidates,
    string? ArtifactDirectory);
```

Register `indexes`; default top `20`, timeout `30`. Parse object syntax in Core
too, so direct module callers receive the same validation. Add
`using System.Text.Json.Serialization;` to `Contracts.cs`; the enum converter
ensures JSON emits the documented classification names instead of integers.

- [ ] **Step 4: Run parser tests**

Run the Task 1 command.

Expected: PASS.

- [ ] **Step 5: Commit CLI contracts**

```powershell
git add src/SqlHarness.Core/Contracts.cs src/SqlHarness.Cli/SqlHarnessCli.cs src/SqlHarness.Cli/Commands/IndexesCommand.cs tests/SqlHarness.Tests/Cli/IndexesCommandTests.cs
git commit -m "feat: add index analysis command contracts"
```

### Task 2: Define one fixed DMV and catalog batch

**Files:**
- Create: `src/SqlHarness.Core/IndexAnalysisQuery.cs`
- Create: `tests/SqlHarness.Tests/IndexAnalysisQueryTests.cs`

**Interfaces:**
- Consumes: top, optional schema, and optional table.
- Produces: `IndexAnalysisQuery.Sql` and `IndexAnalysisQuery.Parameters(int, string?, string?)`.

- [ ] **Step 1: Write fixed-SQL contract tests**

```csharp
[Fact]
public void Batch_is_read_only_parameterized_and_uses_exact_score()
{
    var parameters = IndexAnalysisQuery.Parameters(20, "dbo", "Contracts");

    Assert.DoesNotContain("CREATE INDEX", IndexAnalysisQuery.Sql,
        StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("DROP INDEX", IndexAnalysisQuery.Sql,
        StringComparison.OrdinalIgnoreCase);
    Assert.Contains("(migs.user_seeks + migs.user_scans)",
        IndexAnalysisQuery.Sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("migs.avg_total_user_cost",
        IndexAnalysisQuery.Sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("migs.avg_user_impact / 100.0",
        IndexAnalysisQuery.Sql, StringComparison.OrdinalIgnoreCase);
    Assert.Collection(parameters,
        p => Assert.Equal("@top", p.Name),
        p => Assert.Equal("@objectSchema", p.Name),
        p => Assert.Equal("@objectName", p.Name));
}
```

Require every catalog/DMV from the spec, current `database_id`, user-table
filter, exact-object filter, `sqlserver_start_time`, one captured
`SYSUTCDATETIME`, deterministic ranking, and no interpolation.

- [ ] **Step 2: Run query tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~IndexAnalysisQueryTests
```

Expected: FAIL because the query component is absent.

- [ ] **Step 3: Add the fixed six-result batch**

Return:

1. observation timestamps and exact-object match count;
2. ranked candidates with raw bracketed equality/inequality/include strings;
3. all catalog columns for selected candidate tables;
4. existing index headers including exact filter definitions;
5. rowwise existing key/INCLUDE columns with ordinal and sort direction;
6. rowwise partition compression descriptions.

Materialize ranked candidates in a table variable before returning dependent
metadata. Use `TOP (@top)` only after database/object filtering and documented
ordering.

- [ ] **Step 4: Run query tests**

Run the Task 2 command.

Expected: PASS.

- [ ] **Step 5: Commit fixed query**

```powershell
git add src/SqlHarness.Core/IndexAnalysisQuery.cs tests/SqlHarness.Tests/IndexAnalysisQueryTests.cs
git commit -m "feat: define index analysis batch"
```

### Task 3: Parse bracketed candidate column lists safely

**Files:**
- Create: `src/SqlHarness.Core/BracketedIdentifierListParser.cs`
- Create: `tests/SqlHarness.Tests/BracketedIdentifierListParserTests.cs`

**Interfaces:**
- Consumes: nullable DMV strings and same-table catalog column names.
- Produces: `BracketedIdentifierListParser.ParseAndResolve(string?, IReadOnlyList<string>)`.

- [ ] **Step 1: Write grammar and resolution tests**

```csharp
[Theory]
[InlineData("[A], [B]", new[] { "A", "B" })]
[InlineData("[A,B], [C]]D]", new[] { "A,B", "C]D" })]
public void Parser_handles_commas_and_escaped_brackets(
    string text, string[] expected)
{
    Assert.Equal(expected,
        BracketedIdentifierListParser.ParseAndResolve(text, expected));
}
```

Add cases for null/blank as empty, surrounding whitespace, unbracketed names
rejected, unclosed bracket, trailing comma, empty name, duplicate
case-insensitive name, unknown column, ambiguous case-insensitive catalog
match, and Unicode.

- [ ] **Step 2: Run parser tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~BracketedIdentifierListParserTests
```

Expected: FAIL because the parser is missing.

- [ ] **Step 3: Implement the finite-state parser**

Scan one character at a time. Outside brackets accept whitespace, `[` and
commas only. Inside brackets, convert `]]` to `]`; a single `]` closes the
identifier. Resolve each result against the catalog with
`StringComparer.OrdinalIgnoreCase` and return the exact catalog spelling.
Throw `InvalidOperationException("Missing-index column metadata is malformed.")`
for every invalid branch; never echo the source string.

- [ ] **Step 4: Run parser tests**

Run the Task 3 command.

Expected: PASS.

- [ ] **Step 5: Commit the parser**

```powershell
git add src/SqlHarness.Core/BracketedIdentifierListParser.cs tests/SqlHarness.Tests/BracketedIdentifierListParserTests.cs
git commit -m "feat: parse missing index column lists"
```

### Task 4: Read candidate and existing-index metadata

**Files:**
- Modify: `src/SqlHarness.Core/IndexAnalysisQuery.cs`
- Create: `tests/SqlHarness.Tests/IndexAnalysisReaderTests.cs`

**Interfaces:**
- Consumes: six ordered result sets and the identifier parser.
- Produces: `IndexCandidate`, `ExistingIndex`, `SensitiveExistingIndex`, and `CollectedIndexAnalysis`.

- [ ] **Step 1: Write strict reader tests**

```csharp
[Fact]
public async Task Reader_builds_normalized_candidates_and_sensitive_indexes()
{
    var collected = await IndexAnalysisQuery.ReadAsync(
        Fixture.Reader(filter: "[Status]=(1)"), objectRequested: true,
        CancellationToken.None);

    Assert.Equal(["TenantId"], Assert.Single(collected.Candidates).EqualityColumns);
    var index = Assert.Single(collected.ExistingIndexes);
    Assert.True(index.HasFilter);
    Assert.NotNull(index.FilterHash);
    Assert.DoesNotContain("[Status]", JsonSerializer.Serialize(index),
        StringComparison.Ordinal);
    Assert.Equal("[Status]=(1)",
        Assert.Single(collected.SensitiveIndexes).FilterDefinition);
}
```

Cover missing/ambiguous object, database-wide mode, empty candidates, catalog
column mismatch, rowwise key ordering and direction, INCLUDE, disabled/unique/
constraint flags, no filter, SHA-256 uppercase filter hash, mixed compression,
missing result set, invalid numeric/timestamp values, and raw footprint.

- [ ] **Step 2: Run reader tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~IndexAnalysisReaderTests
```

Expected: FAIL because reader models and method are missing.

- [ ] **Step 3: Implement separated internal models**

```csharp
internal sealed record IndexCandidate(
    long CandidateId, string Schema, string Table,
    IReadOnlyList<string> EqualityColumns,
    IReadOnlyList<string> InequalityColumns,
    IReadOnlyList<string> IncludeColumns,
    long UserSeeks, long UserScans,
    decimal AverageTotalUserCost, decimal AverageUserImpactPercent,
    decimal CumulativeImpactScore,
    DateTimeOffset? LastUserSeek, DateTimeOffset? LastUserScan);

internal sealed record ExistingIndex(
    string Schema, string Table, int IndexId, string Name, string Type,
    IReadOnlyList<string> KeyColumns, IReadOnlyList<bool> KeyDescending,
    IReadOnlyList<string> IncludeColumns,
    bool Unique, bool PrimaryKey, bool UniqueConstraint,
    bool Disabled, bool HasFilter, string? FilterHash, string Compression);

internal sealed record SensitiveExistingIndex(
    string Schema, string Table, int IndexId, string Name,
    string? FilterDefinition);
```

Keep exact filter text only in `SensitiveExistingIndex`. Feed every raw result
cell into `CanonicalResultAccumulator` for gain accounting.

- [ ] **Step 4: Run reader/parser tests**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~IndexAnalysisReaderTests|FullyQualifiedName~BracketedIdentifierListParserTests"
```

Expected: PASS.

- [ ] **Step 5: Commit metadata reading**

```powershell
git add src/SqlHarness.Core/IndexAnalysisQuery.cs tests/SqlHarness.Tests/IndexAnalysisReaderTests.cs
git commit -m "feat: read index analysis metadata"
```

### Task 5: Classify overlap deterministically

**Files:**
- Create: `src/SqlHarness.Core/IndexOverlapClassifier.cs`
- Create: `tests/SqlHarness.Tests/IndexOverlapClassifierTests.cs`

**Interfaces:**
- Consumes: one `IndexCandidate` and same-table `ExistingIndex` values.
- Produces: `IndexOverlapMatch` and `IndexOverlapClassifier.FindBest(IndexCandidate, IReadOnlyList<ExistingIndex>)`.

- [ ] **Step 1: Write the classification matrix**

```csharp
[Theory]
[MemberData(nameof(Cases))]
public void Finds_expected_best_match(
    IndexCandidate candidate, ExistingIndex[] indexes,
    IndexOverlapClassification expected)
{
    Assert.Equal(expected,
        IndexOverlapClassifier.FindBest(candidate, indexes).Classification);
}
```

Cases must independently prove equality reordering, ordered inequality,
trailing existing keys, complete coverage, include gap and exact missing list,
partial leading overlap, unrelated/new shape, filtered downgrade, disabled
tie-break, lower-ID tie-break, case-insensitive identity, and heaps excluded.

- [ ] **Step 2: Run classifier tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~IndexOverlapClassifierTests
```

Expected: FAIL because classifier is missing.

- [ ] **Step 3: Implement pure matching**

```csharp
internal sealed record IndexOverlapMatch(
    IndexOverlapClassification Classification,
    ExistingIndex? BestIndex,
    int MatchedKeyColumnCount,
    int CandidateKeyColumnCount,
    IReadOnlyList<string> MissingIncludeColumns);
```

Score classification strength `4/3/2/1`, followed by matched keys descending,
missing includes ascending, enabled before disabled, and index ID ascending.
Apply the filtered-index downgrade after structural matching and before
best-match selection.

- [ ] **Step 4: Run classifier tests**

Run the Task 5 command.

Expected: PASS.

- [ ] **Step 5: Commit classification**

```powershell
git add src/SqlHarness.Core/IndexOverlapClassifier.cs tests/SqlHarness.Tests/IndexOverlapClassifierTests.cs
git commit -m "feat: classify existing index overlap"
```

### Task 6: Persist artifacts and execute through the module

**Files:**
- Modify: `src/SqlHarness.Core/SqlHarnessPaths.cs`
- Create: `src/SqlHarness.Core/IndexAnalysisArtifacts.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`
- Create: `tests/SqlHarness.Tests/IndexAnalysisArtifactWriterTests.cs`
- Create: `tests/SqlHarness.Tests/IndexesTests.cs`

**Interfaces:**
- Consumes: public report, candidates, sensitive/existing indexes, classifier, target/session/gain infrastructure.
- Produces: `IIndexAnalysisArtifactWriter.Write(SqlHarnessIndexesReport, IReadOnlyList<IndexCandidate>, IReadOnlyList<ExistingIndex>, IReadOnlyList<SensitiveExistingIndex>, string)`, `SqlHarnessPaths.IndexAnalysisDir`, and module dispatch for `SqlHarnessIndexesOperation`.

- [ ] **Step 1: Write artifact and module tests**

Assert atomic `report.json`, `candidates.jsonl`, and
`existing-indexes.jsonl`; filter text only in the last file; no DDL/query text;
safe unique path; cleanup after each write/move failure; and original exception
preservation.

For module behavior test success, valid empty telemetry, validation before
auth, missing/ambiguous object `2`, auth `3`, mismatch `4`, malformed/SQL `5`,
artifact `6`, cancellation, redaction, one verified session/batch, and gain
receipt command `"indexes"`.

- [ ] **Step 2: Run artifact/module tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~IndexAnalysisArtifactWriterTests|FullyQualifiedName~IndexesTests"
```

Expected: FAIL because writer and module dispatch are absent.

- [ ] **Step 3: Implement writer and orchestration**

```csharp
internal interface IIndexAnalysisArtifactWriter
{
    string Write(
        SqlHarnessIndexesReport report,
        IReadOnlyList<IndexCandidate> candidates,
        IReadOnlyList<ExistingIndex> indexes,
        IReadOnlyList<SensitiveExistingIndex> sensitiveIndexes,
        string target);
}
```

Add `IndexAnalysisDir => Path.Combine(Home, "index-analysis")`. Use staging plus
atomic directory move, camel-case JSON, UTF-8 without BOM, and sanitized target.

In the module validate, resolve, connect, execute once, read, classify, build
public candidate reports, write artifacts, return the final report, and attach
deferred gain accounting. Inject the writer in internal constructors and use a
real writer in the public constructor.

- [ ] **Step 4: Run all index-analysis tests**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~IndexesTests|FullyQualifiedName~IndexAnalysisArtifactWriterTests|FullyQualifiedName~IndexOverlapClassifierTests|FullyQualifiedName~IndexAnalysisReaderTests"
```

Expected: PASS.

- [ ] **Step 5: Commit execution and artifacts**

```powershell
git add src/SqlHarness.Core/SqlHarnessPaths.cs src/SqlHarness.Core/IndexAnalysisArtifacts.cs src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/IndexAnalysisArtifactWriterTests.cs tests/SqlHarness.Tests/IndexesTests.cs
git commit -m "feat: execute index overlap analysis"
```

### Task 7: Render, account, document, and verify

**Files:**
- Modify: `src/SqlHarness.Cli/Commands/Renderer.cs`
- Modify: `src/SqlHarness.Core/GainStore.cs`
- Modify: `tests/SqlHarness.Tests/Cli/IndexesCommandTests.cs`
- Modify: `tests/SqlHarness.Tests/GainStoreTests.cs`
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `skills/sqlharness/SKILL.md`
- Modify: `tests/SqlHarness.Tests/ContractsTests.cs`
- Modify: `tests/SqlHarness.Tests/SmokeTests.cs`

**Interfaces:**
- Consumes: completed index-analysis report and receipt.
- Produces: compact text/JSON, `SqlHarnessGainReport.Indexes`, synchronized docs, and complete verification.

- [ ] **Step 1: Write failing output, gain, and documentation tests**

Assert text always shows observation timestamps/warning, candidate score,
classification, best match, and include gaps; JSON contains no filter
definition; empty telemetry is explicit; one record increments
`gain.Indexes.Executions`.

Require help/docs to cover top-20 database mode, exact object mode, formula,
volatile since-restart evidence, four classifications, no DDL, sensitive
artifact, and validation via `measure`/`compare`.

- [ ] **Step 2: Run focused tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~IndexesCommandTests|FullyQualifiedName~GainStoreTests|FullyQualifiedName~ContractsTests|FullyQualifiedName~SmokeTests"
```

Expected: FAIL because output, gain, and docs are absent.

- [ ] **Step 3: Implement output, gain, and synchronized docs**

Render decimals invariantly and never render filter text. Add `Indexes` to
`SqlHarnessGainReport`; accept and aggregate command `"indexes"`.

Document:

```powershell
sqlharness indexes prod-eu --var tenant=acme --var env=uat --top 20 --json
sqlharness indexes prod-eu --var tenant=acme --var env=uat --object dbo.Contracts --json
```

State that results are diagnostic leads, not DDL recommendations.

- [ ] **Step 4: Run the complete gate**

```powershell
dotnet format .\SqlHarness.sln --verify-no-changes
dotnet build .\SqlHarness.sln -c Release
dotnet test .\SqlHarness.sln -c Release --no-build
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- --help
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- indexes --help
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- gain --json
```

Expected: formatting exit `0`, build succeeds with zero errors, all tests pass,
help matches docs, and gain JSON contains the indexes scope.

- [ ] **Step 5: Commit output and docs**

```powershell
git add src/SqlHarness.Cli/Commands/Renderer.cs src/SqlHarness.Core/GainStore.cs tests/SqlHarness.Tests/Cli/IndexesCommandTests.cs tests/SqlHarness.Tests/GainStoreTests.cs README.md AGENTS.md skills/sqlharness/SKILL.md tests/SqlHarness.Tests/ContractsTests.cs tests/SqlHarness.Tests/SmokeTests.cs
git commit -m "docs: document index overlap analysis"
```
