# Plan 056: Parameters bind like the application, rejections say what failed, and every database tool can dry-run

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** `git diff --stat 27d2a9a..HEAD -- src/SqlHarness.Core/SqlSafety.cs src/SqlHarness.Core/SqlValidation.cs src/SqlHarness.Core/SqlParameterMatrix.cs src/SqlHarness.Core/ParameterSetFileReader.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Core/Contracts.cs src/SqlHarness.Mcp/Tools src/SqlHarness.Mcp/McpOperationMapper.cs docs/mcp.md README.md`. Plan 055 is expected to have landed first; read its changes to the MCP tool parameters before adding `dryRun`.

**Goal:** A parameter written the way application code writes it binds the way the application binds it; every rejection names its cause; `query`, `measure` and `compare` can check their exact arguments without connecting.

**Architecture:** One Core helper normalizes parameter names for every input path. Unsized string parameters follow Dapper's sizing. Classifier denials and parameter errors carry their message into every report. A `DryRun` flag on the three Core operations returns a `SqlHarnessDryRunReport` immediately before the first connection, so a dry run performs exactly the checks of a real call.

**Tech Stack:** .NET 8/10, C#, xUnit, Microsoft.SqlServer.TransactSql.ScriptDom, ModelContextProtocol C# SDK.

**Spec:** [`docs/superpowers/specs/2026-10-12-agent-contract-review-design.md`](../docs/superpowers/specs/2026-10-12-agent-contract-review-design.md), decisions 5–7.

## Status

- Status: DONE — implementation `8275304..8e50fb5`; Task 5 commits `f3d48ff..6e8232e`; Windows and Linux gates passed. Independent Task 5 review and final whole-branch review are pending. See [056 verification](056-verification.md).
- Priority: P1
- Effort: M
- Risk: MEDIUM (binding sizes change benchmark plans; new Core operation flag)
- Depends on: 055
- Planned at: `27d2a9a`, 2026-10-12

## Global Constraints

- Accepted parameter name: optional single leading `@`, then `^[A-Za-z_][A-Za-z0-9_]*$`. Reports always print the name with `@`.
- Unsized variable-width `nvarchar`/`varchar` and untyped string values use size `4000` when the value length ≤ 4000 (or the value is NULL), otherwise `max`, matching Dapper so the benchmark compiles the plan the application compiles. Fixed-width `nchar`/`char` have no `max`: both use size `4000` through 4000 characters (or NULL); `char` lengths 4001–8000 use actual length; `nchar` over 4000 and `char` over 8000 are rejected. Explicit sizes are unchanged.
- Unsupported-statement detail format: `"<ScriptDom statement type> at line <n>"`, for example `"IfStatement at line 14"`.
- `dryRun: true` never opens a connection, never writes an artifact, and returns exit code 0 when the call would be allowed.
- The `sqlharness_validate` tool stays in this plan; plan 060 removes it.

## Review Focus

- A name of only `@` or `@@Name` must be rejected with a message naming the parameter (Task 1 test).
- A typed NULL `nvarchar` must bind with size 4000, not null (Task 2 test).
- A 4001-character `nvarchar`/`varchar` value binds as `max`; fixed-width values follow their representable limits (`char` 4001–8000 uses its actual length; `nchar` above 4000 and `char` above 8000 reject) (Task 2 tests).
- A dry run of a compare matrix must bind every matrix value, so a bad value fails the dry run exactly as it would fail the real call (Task 4 test).
- A dry run with a setup batch that creates a temp table used by the measured batch must pass, as the real call does (Task 4 test).

## Why this matters

An agent wrote `@ClientId` as application code does; validation rejected it with `detail: null`, while compare output printed `@ClientId` back. A string parameter bound as `nvarchar(26)` where the application binds `nvarchar(4000)`, so the benchmark compiled a different plan from production. An `IF` was rejected without naming the statement. The separate `validate` tool cannot see setup SQL or a matrix, so it cannot check a benchmark the way the benchmark runs.

## Current state (verify each)

1. `SqlParameterParser.ValidateName` (`SqlSafety.cs:1694-1700`) rejects any `@`; `SqlParameterMatrix.cs:88`, `:137` and `ParameterSetFileReader` apply the same pattern.
2. `CreateUnicodeString` (`SqlSafety.cs:1356-1362`) sizes by `value.Length`; typed NULL `nvarchar` binds with size `null` (`:1323`); `CreateAnsiString` follows the same rule.
3. `SqlValidation.cs:177-180` catches `SqlHarnessSafetyException` without setting `detail`.
4. `SqlSafetyClassifier.ClassifyQuery` (`SqlSafety.cs:213-265`) returns `Denied(deny, null)` for a statement without unproven table-variable use; `ClassifyCompareSetup` (≈ `:276-324`) does the same.
5. `SqlHarnessModule` connects at `:190` (query), after `CompareOperationPreparer.ValidateVariants` (≈ `:286-300` compare, `:392-400` matrix), and at `:541` / single-set path (measure).

---

### Task 1: One name normalizer for every parameter input

**Files:**
- Create: `src/SqlHarness.Core/SqlParameterNames.cs`
- Modify: `src/SqlHarness.Core/SqlSafety.cs` (`Bind`, `ValidateName`), `src/SqlHarness.Core/SqlParameterMatrix.cs:88`, `:137`, `src/SqlHarness.Core/ParameterSetFileReader.cs` (entry name check)
- Test: `tests/SqlHarness.Tests/SqlParameterParserTests.cs`, `SqlParameterMatrixTests.cs`, `ParameterSetFileReaderTests.cs`

**Interfaces:**
- Produces: `internal static class SqlParameterNames { internal static string Normalize(string? name); }` — returns the bare name or throws `SqlHarnessSafetyException("Invalid SQL parameter name '<name>'.")`.

- [ ] **Step 1: Write the failing tests**

`SqlParameterParserTests.cs`:

```csharp
[Theory]
[InlineData("ClientId")]
[InlineData("@ClientId")]
public void Typed_parameter_accepts_an_optional_at_sign(string name)
{
    var bound = SqlParameterParser.Bind(new SqlHarnessParameterInput(name, "int", "7"));
    Assert.Equal("@ClientId", bound.Name);
}

[Theory]
[InlineData("@")]
[InlineData("@@ClientId")]
[InlineData("1Client")]
[InlineData("")]
public void Invalid_names_are_rejected_with_the_name(string name)
{
    var exception = Assert.Throws<SqlHarnessSafetyException>(() =>
        SqlParameterParser.Bind(new SqlHarnessParameterInput(name, "int", "7")));
    Assert.Contains($"'{name}'", exception.Message);
}
```

Add the matching case to `SqlParameterMatrixTests.cs` (matrix name `@BatchSize` binds as `@BatchSize`) and to `ParameterSetFileReaderTests.cs` (a `.sqljson` entry named `@Date` loads and binds as `@Date`), following the neighbouring tests' construction.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlParameterParser|FullyQualifiedName~SqlParameterMatrix|FullyQualifiedName~ParameterSetFileReader"`
Expected: FAIL — `@ClientId` rejected; message lacks the name.

- [ ] **Step 3: Implement**

```csharp
using System.Text.RegularExpressions;

namespace SqlHarness.Core;

internal static partial class SqlParameterNames
{
    internal static string Normalize(string? name)
    {
        var bare = name is { Length: > 1 } && name[0] == '@' ? name[1..] : name;
        if (bare is null || !BareName().IsMatch(bare))
            throw new SqlHarnessSafetyException($"Invalid SQL parameter name '{name}'.");
        return bare;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex BareName();
}
```

In `SqlParameterParser.Bind` replace `var name = input.Name; … ValidateName(name);` with `var name = SqlParameterNames.Normalize(input.Name);` and delete `ValidateName` and its `NamePattern` if nothing else uses them. In `SqlParameterMatrix` and `ParameterSetFileReader` call `SqlParameterNames.Normalize` where the name pattern is checked today and keep their own error wrapping (the matrix message names the option; the file reader names the path).

- [ ] **Step 4: Run focused tests; then the full Core suite**

Run: `dotnet test tests/SqlHarness.Tests` → PASS. A test asserting the old message text `"Invalid SQL parameter name."` is updated to the new message.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Core tests/SqlHarness.Tests
git commit -m "feat(056): accept @-prefixed parameter names on every input path"
```

---

### Task 2: Unsized strings bind like Dapper

**Files:**
- Modify: `src/SqlHarness.Core/SqlSafety.cs` (`CreateUnicodeString`, `CreateAnsiString`, typed-null branch for unsized string types ≈ `:1314-1330`)
- Test: `tests/SqlHarness.Tests/SqlParameterParserTests.cs`

**Interfaces:**
- Produces: `internal const int DefaultStringParameterSize = 4000;` in `SqlParameterParser`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("nvarchar", "abc", 4000)]
[InlineData("varchar", "abc", 4000)]
[InlineData(null, "abc", 4000)]
[InlineData("nvarchar(26)", "abc", 26)]
public void Unsized_strings_bind_with_the_application_default(string? type, string value, int expected) =>
    Assert.Equal(expected, SqlParameterParser.Bind(new SqlHarnessParameterInput("Name", type, value)).Size);

[Fact]
public void Long_unsized_string_binds_as_max() =>
    Assert.Equal(-1, SqlParameterParser.Bind(new SqlHarnessParameterInput("Name", "nvarchar", new string('x', 4001))).Size);

[Theory]
[InlineData("nvarchar")]
[InlineData("varchar")]
public void Typed_null_unsized_string_binds_with_the_default(string type) =>
    Assert.Equal(4000, SqlParameterParser.Bind(new SqlHarnessParameterInput("Name", type, null)).Size);
```

Use the `SqlMaxSize` constant value of HEAD in place of `-1` if it differs.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlParameterParser"`
Expected: FAIL — size 3 and null.

- [ ] **Step 3: Implement**

```csharp
internal const int DefaultStringParameterSize = 4000;

private static int StringSize(string? value, bool max) =>
    max || value is { Length: > DefaultStringParameterSize } ? SqlMaxSize : DefaultStringParameterSize;
```

`CreateUnicodeString` returns `new($"@{name}", SqlDbType.NVarChar, value, StringSize(value, max))`; `CreateAnsiString` uses `StringSize` the same way for its unsized case. In the typed-null switch the unsized `nvarchar`, `varchar`, `nchar`, `char` cases pass `DefaultStringParameterSize`; the untyped NULL branch in `Bind` passes it too.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test SqlHarness.sln` → PASS. Benchmark tests that pinned `size` equal to the value length change to 4000; record each in the commit body.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Core/SqlSafety.cs tests
git commit -m "feat(056): unsized string parameters bind as size 4000 like Dapper"
```

---

### Task 3: Rejections carry their cause

**Files:**
- Modify: `src/SqlHarness.Core/SqlSafety.cs` (`ClassifyQuery` ≈ `:231-236`, `ClassifyCompareSetup` ≈ `:293-316`)
- Modify: `src/SqlHarness.Core/SqlValidation.cs:177-180`
- Test: `tests/SqlHarness.Tests/SqlSafetyTests.cs`, `tests/SqlHarness.Tests/Cli/ValidateCommandTests.cs`

**Interfaces:**
- Produces: `private static string StatementDetail(TSqlStatement statement) => $"{statement.GetType().Name} at line {statement.StartLine}";`

- [ ] **Step 1: Write the failing tests**

`SqlSafetyTests.cs`:

```csharp
[Fact]
public void Unsupported_statement_names_its_kind_and_line()
{
    var decision = ClassifyQuery("SELECT 1 AS A;\nWHILE 1 = 0 BEGIN SELECT 2 AS B; END;");

    Assert.False(decision.Allowed);
    Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    Assert.Equal("WhileStatement at line 2", decision.Detail);
}
```

(`WHILE` stays unsupported after plan 057; `IF` does not, so the test uses `WHILE`.)

`ValidateCommandTests.cs` (or the closest `SqlValidation` test file): validate `SELECT @Name AS N;` with typed parameter `{ Name = "1bad", Type = "int", Value = "1" }` and assert `report.Reason == "parameter_validation_failed"` and `report.Detail` contains `"'1bad'"`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafetyTests.Unsupported_statement|FullyQualifiedName~Validate"`
Expected: FAIL — detail null.

- [ ] **Step 3: Implement**

In `ClassifyQuery`:

```csharp
if (classification.DenyReason is { } deny)
    return Denied(deny, hasUnprovenUse
        ? UnprovenTableVariableDetail
        : deny == SqlSafetyReason.UnsupportedStatement ? StatementDetail(statement) : null);
```

Apply the same rule in `ClassifyCompareSetup` wherever it returns `Denied(SqlSafetyReason.UnsupportedStatement, hasUnprovenUse ? UnprovenTableVariableDetail : null)`.

In `SqlValidation.cs`:

```csharp
catch (SqlHarnessSafetyException exception)
{
    reason = "parameter_validation_failed";
    detail = exception.Message;
}
```

- [ ] **Step 4: Run and commit**

Run: `dotnet test SqlHarness.sln --filter "FullyQualifiedName~SqlSafety|FullyQualifiedName~Validat"` → PASS.

```bash
git add src/SqlHarness.Core tests
git commit -m "feat(056): unsupported statements and parameter errors name their cause"
```

---

### Task 4: `DryRun` on query, measure and compare

**Files:**
- Modify: `src/SqlHarness.Core/Contracts.cs` (operations `:30`, `:43`, `:119`; new report record)
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs` (four return points listed in Current state 5)
- Modify: `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` (`McpToolHandlers.QueryAsync`, `MeasureAsync`, `CompareAsync`), `McpRequestToolHandlers.cs` (same three), `McpOperationMapper.cs` (`MapQueryAsync`, measure and compare mappers take `dryRun`)
- Modify: `src/SqlHarness.Core/AgentOutputProjection.cs` (pass-through case for the new report)
- Test: `tests/SqlHarness.Tests/QueryTests.cs`, `CompareMatrixTests.cs`, `MeasureTests.cs`, `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs`

**Interfaces:**
- Produces: `public bool DryRun { get; init; }` on `SqlHarnessQueryOperation`, `SqlHarnessMeasureOperation`, `SqlHarnessCompareOperation`.
- Produces: `public sealed record SqlHarnessDryRunReport(string Command, string Classification, IReadOnlyList<BenchmarkParameterReport> Parameters, int ParameterSets);`

- [ ] **Step 1: Write the failing Core tests**

`CompareMatrixTests.cs`:

```csharp
[Fact]
public async Task Matrix_dry_run_binds_every_value_without_connecting()
{
    using var artifacts = new DirectoryArtifactWriter();
    var factory = new MatrixSessionFactory();

    var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix("BatchSize:int=1,20,100") with { DryRun = true });

    Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
    var report = Assert.IsType<SqlHarnessDryRunReport>(outcome.Report);
    Assert.Equal(3, report.ParameterSets);
    Assert.Equal(0, factory.ConnectCount);
    Assert.Empty(artifacts.Directories);
}

[Fact]
public async Task Matrix_dry_run_fails_on_a_bad_value_like_the_real_call()
{
    using var artifacts = new DirectoryArtifactWriter();
    var factory = new MatrixSessionFactory();

    var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix("BatchSize:int=1,abc") with { DryRun = true });

    Assert.NotEqual(SqlHarnessExitCode.Success, outcome.ExitCode);
    Assert.Equal(0, factory.ConnectCount);
}
```

`QueryTests.cs`: a dry run of `SELECT @Name AS N` with typed `{ "@Name", "nvarchar", "x" }` returns `SqlHarnessDryRunReport` whose single parameter is `("@Name", "nvarchar", 4000, null, null)`, with zero connections (use the session factory the file already uses; if it has no counter, add a `ConnectCount` field to it the way `MatrixSessionFactory` does).

`MeasureTests.cs`: a dry run with setup `CREATE TABLE #ids (Id int); INSERT #ids VALUES (1);` and query `SELECT Id FROM #ids WHERE Id = @Id` returns success with zero connections.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~dry_run"`
Expected: build error — `DryRun` missing.

- [ ] **Step 3: Implement Core**

`Contracts.cs`: add `public bool DryRun { get; init; }` to the three operation records and:

```csharp
/// <summary>
/// Result of a dry run: the call passed every offline check a real call performs
/// (bounds, safety classification, parameter binding and references, setup shape,
/// matrix or parameter-set binding) and stopped before connecting.
/// </summary>
public sealed record SqlHarnessDryRunReport(
    string Command,
    string Classification,
    IReadOnlyList<BenchmarkParameterReport> Parameters,
    int ParameterSets);
```

`SqlHarnessModule.cs`: insert a dry-run return at each point, immediately before `phase = OperationPhase.Authentication` (query, measure single-set and parameter-set paths) or before `_artifactWriter.CheckStorage()` (compare, compare matrix):

```csharp
if (query.DryRun)
    return DryRunOutcome("query", safety, parameters, parameterSets: 1);
```

`DryRunOutcome` builds `SqlHarnessDryRunReport` with the classification string the query report uses for `StatementClassification` (reuse that helper; search `StatementClassification` in `SqlHarnessModule.cs`) and `BenchmarkParameterReport` entries built the way the compare report builds its `Parameters` (search `new BenchmarkParameterReport(`). For a matrix pass the number of matrix values; for parameter sets their count. Return `SqlHarnessExitCode.Success`. A dry run does not create a journal emission receipt with raw tokens (raw footprint is zero).

`AgentOutputProjection.Project`: add `case SqlHarnessDryRunReport dryRun: return dryRun;` before `default`.

- [ ] **Step 4: Expose `dryRun` in MCP**

Add `[Description("Check every argument offline and stop before connecting. Nothing runs, nothing is written.")] bool dryRun = false` to `query`, `measure` and `compare` in both handler classes; add `"dryRun"` to each `ThrowIfUnknown`/`ThrowUnknown` list; pass it into the mappers, which set `DryRun = dryRun` on the operation. `McpMappingTests.cs`: assert each mapper sets `DryRun` when asked.

- [ ] **Step 5: Run and commit**

Run: `dotnet test SqlHarness.sln` → PASS; `McpTokenBudgetTests` tools/list budget still passes (if not, shorten descriptions, never drop the parameter).

```bash
git add src tests
git commit -m "feat(056): dryRun on query, measure and compare stops before connecting"
```

---

### Task 5: Documentation and gates

**Files:** `docs/mcp.md` (tool rows and parameters/dry-runs contract), `README.md` (Safety contract parameter rules and binding sizes), `skills/sqlharness/SKILL.md`, `Capabilities.cs` and capability/docs contract tests.

- [x] **Step 1:** Document parameter names and type-specific sizes, dry-run scope/report fields and limits, unsupported-statement detail format, and the plan 060 removal of `sqlharness_validate`.
- [x] **Step 2:** `dotnet build SqlHarness.sln -warnaserror`; `pwsh ./scripts/verify.ps1` → `verify: OK`; `pwsh ./scripts/verify-linux.ps1` → `verify-linux: OK`.
- [x] **Step 3:** Status DONE with commit range; update `plans/README.md`.

```bash
git add docs README.md skills tests plans
git commit -m "docs(056): parameter names and sizes, rejection detail, dry run"
```

## Done criteria

- [x] `@Name` and `Name` bind identically on typed, matrix and parameter-set paths.
- [x] `nvarchar` without size binds as 4000.
- [x] A dry run of each of query, measure, compare and compare matrix makes zero connections and writes zero artifacts.
- [x] `WHILE` rejection reports `WhileStatement at line n`.
- [x] Both gates pass; README row updated.

## STOP conditions

- An existing integration test shows a measured plan regression caused by the 4000 default for a parameter the test binds without size. Report the case; do not change the default without the operator.
- A dry-run return point would skip a check that the real call performs after it (for example a check placed after `CheckStorage`). Move the dry-run return after that check; if the check needs a connection, stop and report it.
