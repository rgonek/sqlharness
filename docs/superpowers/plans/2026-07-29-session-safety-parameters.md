# Session, Safety, and Parameters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove the single-session `#temp` contract, safely accept practical temporary-table and window syntax, improve rejection diagnostics, and complete SQL parameter binding.

**Architecture:** Keep the existing one-connection benchmark flow intact and prove it with a real SQL Server integration test. Extend the fail-closed ScriptDom allowlist narrowly, centralize typed parameter parsing in `SqlSafety.cs`, and carry decimal precision/scale through `SqlExecutionCommand` binding.

**Tech Stack:** .NET 8, C# 12, Microsoft.Data.SqlClient 7, Microsoft.SqlServer.TransactSql.ScriptDom, Spectre.Console.Cli, xUnit 2.

## Global Constraints

- Read-only remains the default.
- Only local temporary objects whose unqualified name starts with exactly one `#` receive the session-only allowance.
- Persistent objects, `##temp`, dynamic SQL, external access, cross-database references, and transaction control remain denied.
- Setup runs exactly once per benchmark connection; warm-up and all measured repetitions reuse that connection.
- SQL values are parameterized and culture-invariant.
- The integration test uses only `SQLHARNESS_INTEGRATION_CONNECTION_STRING`; it never loads user target profiles.
- Do not modify or commit `docs/spec-watch-snapshot.md` or `out/`.

---

### Task 1: Make safety rejections actionable and allow safe analytical syntax

**Files:**
- Modify: `src/SqlHarness.Core/SqlSafety.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`
- Test: `tests/SqlHarness.Tests/SqlSafetyTests.cs`
- Test: `tests/SqlHarness.Tests/QueryTests.cs`

**Interfaces:**
- Produces: `SqlSafetyDecision(bool Allowed, SqlSafetyReason Reason, bool HasMutation = false, string? Detail = null)`
- Produces: `SqlSafetyDecision.RejectionDescription`
- Consumes: existing `SqlSafetyClassifier.Classify(...)`

- [ ] **Step 1: Write failing diagnostics and window tests**

Add tests that require type-only diagnostics and accepted windows:

```csharp
[Fact]
public void Rejection_names_unsupported_statement_without_echoing_SQL()
{
    const string secret = "SECRET_PROC";
    var decision = ClassifyQuery($"EXEC dbo.{secret}");

    Assert.False(decision.Allowed);
    Assert.Contains("ExecuteStatement", decision.RejectionDescription);
    Assert.DoesNotContain(secret, decision.RejectionDescription);
}

[Theory]
[InlineData("SELECT ROW_NUMBER() OVER (ORDER BY Id) FROM dbo.Clients")]
[InlineData("SELECT SUM(Amount) OVER (PARTITION BY ClientId ORDER BY Id ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) FROM dbo.Orders")]
public void Query_allows_safe_window_syntax(string sql) =>
    Assert.True(ClassifyQuery(sql).Allowed);
```

Add a module test asserting the emitted safe error contains
`Unsupported SQL statement types: ExecuteStatement.` and not the SQL batch.

- [ ] **Step 2: Run the focused tests and confirm failure**

Run:

```powershell
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~QueryTests" --no-restore
```

Expected: FAIL because `SqlSafetyDecision` has no detail and window fragments are not allowlisted.

- [ ] **Step 3: Implement bounded type diagnostics**

Change the decision shape and replace the boolean allowlist walk:

```csharp
internal sealed record SqlSafetyDecision(
    bool Allowed,
    SqlSafetyReason Reason,
    bool HasMutation = false,
    string? Detail = null)
{
    internal string RejectionDescription =>
        Detail is null ? $"{Reason}." : $"{Reason}. {Detail}";
}

private sealed record UnsupportedSyntax(
    IReadOnlyList<string> StatementTypes,
    IReadOnlyList<string> FragmentTypes)
{
    internal bool Any => StatementTypes.Count > 0 || FragmentTypes.Count > 0;
}
```

Walk all fragments, collect distinct `GetType().Name` values, separate top-level
statements from nested fragments, sort ordinally, and format only:

```text
Unsupported SQL statement types: ExecuteStatement.
Unsupported AST fragment types: NullableConstraintDefinition.
```

Use `decision.RejectionDescription` in both query validation and `EnsureSafe`.

- [ ] **Step 4: Add the exact safe window fragments**

Add ScriptDom types required by the two tests:

```csharp
typeof(IdentifierLiteral),
typeof(OverClause),
typeof(WindowFrameClause),
typeof(WindowDelimiter),
```

Do not allowlist new statement types.

- [ ] **Step 5: Rerun focused tests**

Run the Step 2 command.

Expected: PASS.

- [ ] **Step 6: Commit the safety diagnostics slice**

```powershell
git add src/SqlHarness.Core/SqlSafety.cs src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/SqlSafetyTests.cs tests/SqlHarness.Tests/QueryTests.cs
git commit -m "feat: explain safe SQL rejections"
```

### Task 2: Allow constrained local temporary-table definitions

**Files:**
- Modify: `src/SqlHarness.Core/SqlSafety.cs`
- Test: `tests/SqlHarness.Tests/SqlSafetyTests.cs`

**Interfaces:**
- Consumes: `SqlSafetyClassifier.Classify(...)`
- Produces: no new public API; expands only the local-`#temp` AST allowlist

- [ ] **Step 1: Write failing accepted and denied cases**

Add:

```csharp
[Theory]
[InlineData("CREATE TABLE #Req(Id int NULL)")]
[InlineData("CREATE TABLE #Req(Id int NOT NULL PRIMARY KEY)")]
[InlineData("CREATE TABLE #Req(Id int NOT NULL, Code int, CONSTRAINT UQ_Req UNIQUE(Code))")]
[InlineData("CREATE TABLE #Req(Id int NOT NULL); CREATE UNIQUE INDEX IX_Req ON #Req(Id)")]
public void Setup_allows_constraints_and_indexes_on_local_temp_tables(string sql) =>
    Assert.True(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false).Allowed);

[Theory]
[InlineData("CREATE TABLE ##Req(Id int NOT NULL PRIMARY KEY)")]
[InlineData("CREATE TABLE dbo.Req(Id int NOT NULL PRIMARY KEY)")]
[InlineData("CREATE UNIQUE INDEX IX_Req ON dbo.Req(Id)")]
public void Setup_denies_equivalent_persistent_or_global_temp_work(string sql) =>
    Assert.False(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false).Allowed);
```

- [ ] **Step 2: Verify the constraint cases fail**

Run:

```powershell
dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~SqlSafetyTests --no-restore
```

Expected: FAIL naming `NullableConstraintDefinition` and
`UniqueConstraintDefinition`.

- [ ] **Step 3: Add only the required constraint fragments**

Add the concrete ScriptDom fragment types reported by the tests, including:

```csharp
typeof(NullableConstraintDefinition),
typeof(UniqueConstraintDefinition),
```

If ScriptDom reports an additional nested type for the named table constraint,
add that concrete nested fragment only after a failing assertion names it. Do
not allowlist `AlterTableStatement`, persistent `CreateIndexStatement`, or an
abstract family solely to make the test pass.

- [ ] **Step 4: Verify accepted and denied cases**

Run the Step 2 command.

Expected: PASS, including all existing denial tests.

- [ ] **Step 5: Commit the local-temp syntax slice**

```powershell
git add src/SqlHarness.Core/SqlSafety.cs tests/SqlHarness.Tests/SqlSafetyTests.cs
git commit -m "feat: allow constrained local temp setup"
```

### Task 3: Complete typed parameter parsing and SQL binding

**Files:**
- Modify: `src/SqlHarness.Core/SqlSafety.cs`
- Modify: `src/SqlHarness.Core/SqlExecution.cs`
- Test: `tests/SqlHarness.Tests/SqlParameterParserTests.cs`
- Test: `tests/SqlHarness.Tests/SqlExecutionTests.cs`

**Interfaces:**
- Produces: `SqlHarnessParameter(string Name, SqlDbType Type, object Value, int? Size, byte? Precision = null, byte? Scale = null)`
- Produces: `SqlParameterParser.ParseOne(string input)` as an internal reusable parser
- Consumes: `SqlClientSession.BindCommand(...)`

- [ ] **Step 1: Write failing parser tests**

Cover exact type/range behavior:

```csharp
[Theory]
[InlineData("at:datetime=2026-07-29T12:00:00", SqlDbType.DateTime)]
[InlineData("at:datetime2=2026-07-29T12:00:00.1234567", SqlDbType.DateTime2)]
[InlineData("at:datetimeoffset=2026-07-29T12:00:00+02:00", SqlDbType.DateTimeOffset)]
public void Parse_binds_supported_temporal_types(string input, SqlDbType expected)
{
    var parameter = Assert.Single(SqlParameterParser.Parse([input]));
    Assert.Equal(expected, parameter.Type);
}

[Fact]
public void Parse_binds_explicit_decimal_precision_and_scale()
{
    var parameter = Assert.Single(SqlParameterParser.Parse(["amount:decimal(19,4)=1234.5600"]));
    Assert.Equal(SqlDbType.Decimal, parameter.Type);
    Assert.Equal((byte)19, parameter.Precision);
    Assert.Equal((byte)4, parameter.Scale);
    Assert.Equal(1234.5600m, parameter.Value);
}
```

Add rejection cases for `decimal(0,0)`, `decimal(39,0)`, `decimal(10,11)`,
overflowing value `decimal(5,2)=12345.67`, invalid offsets, and `datetime`
outside `1753-01-01` through `9999-12-31`.

- [ ] **Step 2: Write a failing binding test**

In `SqlExecutionTests.cs`, build a `SqlCommand`, call `BindCommand`, and assert:

```csharp
Assert.Equal((byte)19, command.Parameters["@amount"].Precision);
Assert.Equal((byte)4, command.Parameters["@amount"].Scale);
```

Also assert plain `decimal` leaves both properties at provider defaults.

- [ ] **Step 3: Run focused parameter tests**

Run:

```powershell
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlParameterParserTests|FullyQualifiedName~SqlExecutionTests" --no-restore
```

Expected: FAIL for the missing types and metadata.

- [ ] **Step 4: Implement the shared parameter specification**

Extend the parameter record:

```csharp
internal sealed record SqlHarnessParameter(
    string Name,
    SqlDbType Type,
    object Value,
    int? Size,
    byte? Precision = null,
    byte? Scale = null);
```

Make `ParseOne` internal. Parse `decimal(p,s)` with a generated, anchored regex:

```csharp
[GeneratedRegex(@"^decimal\((?<precision>[0-9]{1,2}),(?<scale>[0-9]{1,2})\)$",
    RegexOptions.CultureInvariant)]
private static partial Regex DecimalTypePattern();
```

Enforce `1 <= precision <= 38`, `0 <= scale <= precision`, use invariant
`decimal.Parse`, and reject values whose integer digits exceed `p-s` or
fractional digits exceed `s`. Bind:

```csharp
"datetime" => new($"@{name}", SqlDbType.DateTime,
    DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), null),
"datetimeoffset" => new($"@{name}", SqlDbType.DateTimeOffset,
    DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), null),
```

After parsing `datetime`, reject values earlier than `SqlDateTime.MinValue.Value`
or later than `SqlDateTime.MaxValue.Value`. Require `datetimeoffset` input to
contain an explicit `Z` or signed offset so a machine-local timezone cannot
change the bound value.

Keep plain `decimal` without precision/scale metadata.

- [ ] **Step 5: Bind precision and scale**

In `SqlClientSession.BindCommand`:

```csharp
if (parameter.Precision is { } precision)
    sqlParameter.Precision = precision;
if (parameter.Scale is { } scale)
    sqlParameter.Scale = scale;
```

- [ ] **Step 6: Rerun focused tests**

Run the Step 3 command.

Expected: PASS.

- [ ] **Step 7: Commit the parameter slice**

```powershell
git add src/SqlHarness.Core/SqlSafety.cs src/SqlHarness.Core/SqlExecution.cs tests/SqlHarness.Tests/SqlParameterParserTests.cs tests/SqlHarness.Tests/SqlExecutionTests.cs
git commit -m "feat: complete SQL parameter types"
```

### Task 4: Prove `#temp` session scope against SQL Server

**Files:**
- Create: `tests/SqlHarness.Tests/Integration/SqlServerIntegrationFactAttribute.cs`
- Create: `tests/SqlHarness.Tests/Integration/BenchmarkSessionIntegrationTests.cs`
- Modify: `README.md`

**Interfaces:**
- Consumes: environment variable `SQLHARNESS_INTEGRATION_CONNECTION_STRING`
- Consumes: `SqlHarnessModule`, `ISqlSessionFactory`, and `SqlClientSession`
- Produces: opt-in integration test category `Category=SqlServerIntegration`

- [ ] **Step 1: Add the opt-in test attribute**

Implement:

```csharp
[AttributeUsage(AttributeTargets.Method)]
internal sealed class SqlServerIntegrationFactAttribute : FactAttribute
{
    internal const string Variable = "SQLHARNESS_INTEGRATION_CONNECTION_STRING";

    public SqlServerIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"{Variable} is not configured.";
    }
}
```

Annotate each integration test with both
`[SqlServerIntegrationFact]` and `[Trait("Category", "SqlServerIntegration")]`.

- [ ] **Step 2: Write the real-session integration test**

Create a test-only `ISqlSessionFactory` that opens a fresh `SqlConnection` from
the environment variable on every `ConnectAsync`, attaches an info-message
handler, wraps it in `SqlClientSession`, and sets `Identity` from the connection's
`DataSource` and `Database`.

Execute a one-repeat compare through `SqlHarnessModule`:

```csharp
var operation = new SqlHarnessCompareOperation(
    new SqlTargetRequest("integration", new Dictionary<string, string>()),
    """
    CREATE TABLE #Req (Id int NOT NULL PRIMARY KEY);
    INSERT #Req VALUES (1);
    """,
    "SELECT Id FROM #Req;",
    "SELECT Id FROM #Req;",
    [],
    30,
    1);
```

Supply a test-only profile whose server/database match the connection string and
an artifact writer rooted in a disposable temporary directory. Assert success,
equivalent results, and exactly one factory connection.

- [ ] **Step 3: Run without configuration and confirm a skip**

Run:

```powershell
Remove-Item Env:SQLHARNESS_INTEGRATION_CONNECTION_STRING -ErrorAction SilentlyContinue
dotnet test tests/SqlHarness.Tests --filter Category=SqlServerIntegration --no-restore
```

Expected: one skipped test, zero failures.

- [ ] **Step 4: Run against the isolated test database**

Set the explicit connection string for the isolated SQLHarness database, then:

```powershell
dotnet test tests/SqlHarness.Tests --filter Category=SqlServerIntegration --no-restore
```

Expected: PASS. `#Req` is visible to setup, warm-up, baseline, and candidate.

- [ ] **Step 5: Document the opt-in test**

Add a README development section that names the environment variable, states
that it must reference an isolated test database, and includes the exact command
from Step 4. State that the test never loads `~/.sqlharness/targets.json`.

- [ ] **Step 6: Commit the integration proof**

```powershell
git add tests/SqlHarness.Tests/Integration/SqlServerIntegrationFactAttribute.cs tests/SqlHarness.Tests/Integration/BenchmarkSessionIntegrationTests.cs README.md
git commit -m "test: prove temp setup session scope"
```

### Task 5: Synchronize help and agent workflow documentation

**Files:**
- Modify: `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs`
- Modify: `tests/SqlHarness.Tests/Cli/CommandTests.cs`
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `skills/sqlharness/SKILL.md`

**Interfaces:**
- Produces: CLI help text listing all supported parameter types
- Preserves: explicit `--file` wins over redirected stdin

- [ ] **Step 1: Add failing help and stdin regression assertions**

Capture `sqlharness query --help` and assert it contains:

```text
nvarchar, int, bigint, decimal, decimal(p,s), bit, date, datetime, datetime2, datetimeoffset, uniqueidentifier
```

Retain `Query_prefers_file_over_redirected_stdin` unchanged and include it in the
focused test run.

- [ ] **Step 2: Run CLI tests and confirm help failure**

Run:

```powershell
dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~CommandTests --no-restore
```

Expected: the new help assertion fails; the stdin regression passes.

- [ ] **Step 3: Add parameter descriptions to all three commands**

Use the same exact description on `query`, `measure`, and `compare`:

```csharp
[Description("Bind name[:type]=value. Types: nvarchar, int, bigint, decimal, decimal(p,s), bit, date, datetime, datetime2, datetimeoffset, uniqueidentifier.")]
[CommandOption("--param <VALUE>")]
public string[] Parameters { get; set; } = [];
```

- [ ] **Step 4: Update workflow documentation**

Document:

- setup once per connection;
- setup/warm-up/repetitions sharing one session;
- supported types and ISO examples;
- local `#temp` constraints and indexes;
- stop-on-rejected-setup behavior;
- representative inventory: row count, cardinality distribution, ordering ties,
  missing history, boundary dates, and empty results;
- benchmark SQL outside the application repository;
- plans and runtime parameters as locally sensitive.

Do not mention historical repositories or migration provenance.

- [ ] **Step 5: Run the complete stage gate**

Run:

```powershell
dotnet test -c Release
dotnet build -c Release --no-restore
git diff --check
```

Expected: all tests pass, build succeeds, and `git diff --check` is clean.

- [ ] **Step 6: Commit documentation and help**

```powershell
git add src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs tests/SqlHarness.Tests/Cli/CommandTests.cs README.md AGENTS.md skills/sqlharness/SKILL.md
git commit -m "docs: define benchmark setup contract"
```
