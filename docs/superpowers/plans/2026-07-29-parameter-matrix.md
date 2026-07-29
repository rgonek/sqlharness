# Parameter Matrix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run one `compare` operation sequentially across several typed values of one parameter, with a fresh SQL session and setup per value.

**Architecture:** Parse and validate a single matrix specification through the existing typed parameter parser, then refactor one comparison into a reusable cell runner. The matrix orchestrator opens cells sequentially, stops on the first failure, retains completed cell artifacts, and returns a compact cross-cell report.

**Tech Stack:** .NET 8, C# 12, Microsoft.Data.SqlClient, Spectre.Console.Cli, System.Text.Json, xUnit 2.

## Global Constraints

- `compare` alone accepts `--matrix`.
- Version 1 accepts exactly one matrix dimension and at least two values.
- Matrix and fixed parameters share identical parsing and SQL binding.
- A matrix name cannot also appear in `--param`.
- Values execute sequentially in user-supplied order.
- Every value gets a new connection and setup runs exactly once on it.
- The first failed cell stops execution; completed artifact directories remain.
- No Cartesian product and no parallel execution.
- All inputs are validated before the first authentication attempt.
- Do not modify or commit `docs/spec-watch-snapshot.md` or `out/`.

---

### Task 1: Parse one typed matrix without ambiguity

**Files:**
- Create: `src/SqlHarness.Core/SqlParameterMatrix.cs`
- Modify: `src/SqlHarness.Core/SqlSafety.cs`
- Test: `tests/SqlHarness.Tests/SqlParameterMatrixTests.cs`

**Interfaces:**
- Produces: `public sealed record SqlParameterMatrixSpec(string Name, string Type, IReadOnlyList<string> DisplayValues)`
- Produces: `internal sealed record ParsedParameterMatrix(string Name, IReadOnlyList<SqlHarnessParameter> Values)`
- Produces: `internal static ParsedParameterMatrix SqlParameterMatrixParser.Parse(string input, IReadOnlyList<string> fixedParameters)`
- Consumes: `SqlParameterParser.ParseOne(string input)`

- [ ] **Step 1: Write failing valid-shape tests**

Add:

```csharp
[Fact]
public void Parse_preserves_user_order_and_shared_int_typing()
{
    var matrix = SqlParameterMatrixParser.Parse("BatchSize:int=1,20,100", []);

    Assert.Equal("@BatchSize", matrix.Name);
    Assert.Equal([1, 20, 100], matrix.Values.Select(value => value.Value));
}

[Fact]
public void Decimal_type_comma_is_not_a_value_separator()
{
    var matrix = SqlParameterMatrixParser.Parse("Amount:decimal(10,2)=1.25,2.50", []);

    Assert.All(matrix.Values, value =>
    {
        Assert.Equal((byte)10, value.Precision);
        Assert.Equal((byte)2, value.Scale);
    });
}
```

Parsing must split the declaration from values at the first `=` and split commas
only in the value portion.

- [ ] **Step 2: Write failing validation tests**

Reject:

- no `=`;
- zero or one value;
- empty value;
- duplicate typed values (`1,01` for `int`);
- matrix name duplicated by a fixed `--param`, case-insensitively;
- malformed name/type/value;
- more than one CLI `--matrix`.

Errors name the option and parameter name but do not echo the complete matrix
string.

- [ ] **Step 3: Run parser tests and confirm failure**

```powershell
dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~SqlParameterMatrixTests --no-restore
```

Expected: FAIL because the parser does not exist.

- [ ] **Step 4: Implement parsing through `ParseOne`**

For every raw display value, construct:

```csharp
var parsed = SqlParameterParser.ParseOne($"{declaration}={displayValue}");
```

Detect typed duplicates using a key composed of `SqlDbType`, precision, scale,
size, and invariant value. Preserve the original display values separately for
successful matrix reports; do not use them in error messages.

- [ ] **Step 5: Rerun tests and commit**

Run the Step 3 command, expect PASS, then:

```powershell
git add src/SqlHarness.Core/SqlParameterMatrix.cs src/SqlHarness.Core/SqlSafety.cs tests/SqlHarness.Tests/SqlParameterMatrixTests.cs
git commit -m "feat: parse typed parameter matrices"
```

### Task 2: Extract one comparison cell without changing behavior

**Files:**
- Create: `src/SqlHarness.Core/CompareCellRunner.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`
- Modify: `src/SqlHarness.Core/Artifacts.cs`
- Test: `tests/SqlHarness.Tests/CompareTests.cs`

**Interfaces:**
- Produces: `internal sealed record CompareCellRequest(ResolvedTarget Target, string? SetupSql, string BaselineSql, string CandidateSql, IReadOnlyList<SqlHarnessParameter> Parameters, int TimeoutSeconds, int Repeat, ResultComparisonMode CompareResults)`
- Produces: `internal sealed record CompareCellResult(SqlHarnessCompareReport Report, IReadOnlyList<CompareRunArtifact> Runs, OutputFootprint RawFootprint)`
- Produces: `internal sealed class CompareCellRunner`
- Produces: `Task<CompareCellResult> CompareCellRunner.RunAsync(CompareCellRequest request, CancellationToken ct)`

- [ ] **Step 1: Lock current call order with a failing extraction test**

Retain and strengthen the existing fake-session assertion:

```csharp
Assert.Equal(
    ["setup", "warmup-A", "warmup-B", "A", "B", "B", "A"],
    session.Labels);
Assert.Equal(1, factory.ConnectCount);
```

Add assertions that equivalence and artifacts are unchanged after invoking the
new runner through `SqlHarnessModule`.

- [ ] **Step 2: Run compare tests before refactoring**

```powershell
dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~CompareTests --no-restore
```

Expected: existing tests PASS; the new runner-specific compile assertion FAILS.

- [ ] **Step 3: Move cell execution behind one interface**

Move connection ownership, setup, warm-ups, alternating repetitions, metrics,
equivalence, and cell artifact writing into `CompareCellRunner`. Inject
`ISqlSessionFactory` and `ICompareArtifactWriter`.

`SqlHarnessModule.ExecuteCompareAsync` retains:

- operation validation;
- target resolution;
- SQL safety classification;
- parameter parsing/reference validation;
- secret collection;
- phase-to-exit-code mapping;
- gain receipt.

It constructs one `CompareCellRequest` and calls the runner exactly once.

- [ ] **Step 4: Rerun the complete compare suite**

Run:

```powershell
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CompareTests|FullyQualifiedName~ArtifactWriterTests" --no-restore
```

Expected: PASS with byte-for-byte compatible full report JSON except for fields
explicitly added by the preceding plan.

- [ ] **Step 5: Commit the behavior-preserving extraction**

```powershell
git add src/SqlHarness.Core/CompareCellRunner.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Core/Artifacts.cs tests/SqlHarness.Tests/CompareTests.cs
git commit -m "refactor: isolate one comparison cell"
```

### Task 3: Add matrix contracts and sequential orchestration

**Files:**
- Modify: `src/SqlHarness.Core/Contracts.cs`
- Create: `src/SqlHarness.Core/CompareMatrixRunner.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`
- Modify: `src/SqlHarness.Core/Artifacts.cs`
- Test: `tests/SqlHarness.Tests/CompareMatrixTests.cs`

**Interfaces:**
- Produces: `public sealed record SqlHarnessCompareMatrixOperation(..., string Matrix, ResultComparisonMode CompareResults) : SqlHarnessOperation`
- Produces: `public sealed record CompareMatrixCellReport(int Index, string ParameterValue, SqlHarnessCompareReport Compare)`
- Produces: `public sealed record SqlHarnessCompareMatrixReport(string ParameterName, string ParameterType, IReadOnlyList<CompareMatrixCellReport> Cells)`
- Produces: `internal sealed class CompareMatrixRunner`

- [ ] **Step 1: Write failing orchestration tests**

Use a factory that returns a distinct fake session per connection. For
`BatchSize:int=1,20,100`, assert:

```csharp
Assert.Equal(3, factory.ConnectCount);
Assert.All(factory.Sessions, session => Assert.Equal(1, session.SetupCount));
Assert.Equal(["1", "20", "100"], report.Cells.Select(cell => cell.ParameterValue));
```

Assert every session observes only its own typed matrix parameter and all fixed
parameters.

- [ ] **Step 2: Write prevalidation and failure tests**

Assert invalid SQL in the final cell shape, an invalid matrix value, a fixed
parameter conflict, or an unreferenced parameter returns exit 2 with
`ConnectCount == 0`.

Make cell index 1 fail during SQL execution and assert:

- exit code 5;
- `ConnectCount == 2`;
- the error identifies `cell 1` and `@BatchSize`;
- the error does not echo the failed display value;
- cell 0's artifact directory still exists;
- no success matrix report is returned.

- [ ] **Step 3: Run matrix tests and confirm failure**

```powershell
dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~CompareMatrixTests --no-restore
```

Expected: FAIL because the contracts and runner do not exist.

- [ ] **Step 4: Validate every cell before authentication**

In `SqlHarnessModule`, parse fixed parameters and the complete matrix first.
For each typed matrix value, combine parameters and call
`SqlParameterReferenceValidator.Validate(...)` against setup, baseline, and
candidate. Classify all three SQL batches once because their text is invariant.
Resolve the target once.

- [ ] **Step 5: Run cells sequentially**

Implement:

```csharp
for (var index = 0; index < matrix.Values.Count; index++)
{
    var request = baseRequest with
    {
        Parameters = [.. fixedParameters, matrix.Values[index]],
    };
    var cell = await _cellRunner.RunAsync(request, ct);
    cells.Add(new CompareMatrixCellReport(
        index,
        matrix.DisplayValues[index],
        cell.Report));
}
```

Do not use `Task.WhenAll`, PLINQ, or parallel loops. Wrap failures with a bounded
matrix-cell exception carrying only index and parameter name while preserving
the original phase/exit-code mapping.

- [ ] **Step 6: Rerun matrix and compare tests**

```powershell
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~CompareTests" --no-restore
```

Expected: PASS.

- [ ] **Step 7: Commit matrix orchestration**

```powershell
git add src/SqlHarness.Core/Contracts.cs src/SqlHarness.Core/CompareMatrixRunner.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Core/Artifacts.cs tests/SqlHarness.Tests/CompareMatrixTests.cs
git commit -m "feat: run sequential comparison matrices"
```

### Task 4: Expose `--matrix` and compact cross-cell output

**Files:**
- Modify: `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs`
- Modify: `src/SqlHarness.Cli/Commands/Renderer.cs`
- Modify: `src/SqlHarness.Core/BenchmarkSummary.cs`
- Modify: `tests/SqlHarness.Tests/Cli/CommandTests.cs`
- Modify: `tests/SqlHarness.Tests/BenchmarkSummaryTests.cs`

**Interfaces:**
- Produces: compare option `--matrix <NAME:TYPE=VALUES>`
- Produces: `BenchmarkSummaryProjector.Project(SqlHarnessCompareMatrixReport report)`

- [ ] **Step 1: Write failing CLI shape tests**

Assert one matrix dispatches `SqlHarnessCompareMatrixOperation`, no matrix
dispatches the existing `SqlHarnessCompareOperation`, and repeated `--matrix`
fails before dispatch:

```csharp
var exit = await app.RunAsync([
    "compare", "dev",
    "--baseline", baseline,
    "--candidate", candidate,
    "--matrix", "BatchSize:int=1,20,100",
    "--json-summary"]);
```

Assert `measure --matrix` and `query --matrix` are rejected by Spectre as unknown
options.

- [ ] **Step 2: Write failing summary tests**

Create three cell reports and assert summary rows retain input order and contain:

- matrix display value;
- technical equivalence;
- baseline/candidate CPU, elapsed, and reads min/median/max;
- reads per table;
- warnings/spills/conversions;
- noteworthy operators;
- the cell artifact directory.

Assert the summary has no full run or full operator arrays.

- [ ] **Step 3: Run focused tests**

```powershell
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CommandTests|FullyQualifiedName~BenchmarkSummaryTests" --no-restore
```

Expected: FAIL for missing option/projection.

- [ ] **Step 4: Add the single compare option**

Add:

```csharp
[CommandOption("--matrix <NAME:TYPE=VALUES>")]
public string[] Matrix { get; set; } = [];
```

Reject `Matrix.Length > 1` with
`Version 1 accepts exactly one --matrix option.` When one exists, dispatch the
matrix operation; otherwise preserve the existing compare operation.

- [ ] **Step 5: Render matrix text and JSON summary**

Text output uses one tab-separated row per cell with value, equivalence,
baseline elapsed median, candidate elapsed median, and artifact directory.
`--json-summary` uses the bounded projector. Full `--json` serializes the
complete matrix report containing complete per-cell compare reports but no SQL
text or parameter values other than the explicit matrix dimension.

- [ ] **Step 6: Run focused tests and commit**

Run the Step 3 command, expect PASS, then:

```powershell
git add src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs src/SqlHarness.Cli/Commands/Renderer.cs src/SqlHarness.Core/BenchmarkSummary.cs tests/SqlHarness.Tests/Cli/CommandTests.cs tests/SqlHarness.Tests/BenchmarkSummaryTests.cs
git commit -m "feat: expose comparison matrices"
```

### Task 5: Prove per-cell SQL Server session isolation and document usage

**Files:**
- Modify: `tests/SqlHarness.Tests/Integration/BenchmarkSessionIntegrationTests.cs`
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `skills/sqlharness/SKILL.md`

**Interfaces:**
- Consumes: `SQLHARNESS_INTEGRATION_CONNECTION_STRING`
- Verifies: one physical session and one setup per matrix cell

- [ ] **Step 1: Add the matrix integration test**

Use setup that creates a temp table and stamps the current session:

```sql
CREATE TABLE #Req
(
    Id int NOT NULL PRIMARY KEY,
    SessionId int NOT NULL
);
INSERT #Req(Id, SessionId) VALUES (@BatchSize, @@SPID);
```

Baseline and candidate both select `Id, SessionId FROM #Req`. Run two matrix
values. Assert success, two cell reports, two factory connections, and distinct
`SqlConnection.ServerProcessId` values captured by the test session factory.
This proves both within-cell visibility and between-cell isolation without
exposing result-row values in reports.

- [ ] **Step 2: Run the opt-in integration test**

```powershell
dotnet test tests/SqlHarness.Tests --filter Category=SqlServerIntegration --no-restore
```

Expected: PASS when the explicit isolated connection string is configured; skip
without it.

- [ ] **Step 3: Document the final command contract**

Add:

```powershell
sqlharness compare prod-eu `
  --var tenant=acme --var env=uat `
  --baseline .\before.sql `
  --candidate .\after.sql `
  --matrix BatchSize:int=1,20,100 `
  --param AsOfDate:datetime2=2026-07-29T12:00:00 `
  --repeat 7 `
  --compare-results ordered `
  --json-summary
```

State explicitly:

- one matrix dimension only;
- at least two typed values;
- sequential user-supplied order;
- a new connection and one setup per value;
- first failure stops the run;
- completed cell artifacts remain;
- ticket SQL stays outside the application repository.

- [ ] **Step 4: Run the complete stage gate**

```powershell
dotnet test -c Release
dotnet build -c Release --no-restore
git diff --check
```

Expected: all tests pass, build succeeds, diff check is clean.

- [ ] **Step 5: Commit integration documentation**

```powershell
git add tests/SqlHarness.Tests/Integration/BenchmarkSessionIntegrationTests.cs README.md AGENTS.md skills/sqlharness/SKILL.md
git commit -m "docs: define parameter matrix workflow"
```
