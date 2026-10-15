# Plan 072: Mutation risk tiers in Core

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** `git diff --stat 488598d..HEAD -- src/SqlHarness.Core/SqlSafety.cs src/SqlHarness.Core/Postgres src/SqlHarness.Core/Contracts.cs src/SqlHarness.Core/BenchmarkRunner.cs src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/SqlSafety*.cs tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs`. Line numbers below are from `488598d`. If the classifiers changed, re-read them and keep the same rules; that counts as adaptation, not a STOP.

**Goal:** Core classifies every batch into a risk tier (`read-only`, `session`, `dml`, `schema`, `privilege`, `opaque`) in both engines, through a new `SqlUsage.Mutation`. Every dry-run report shows the tier. Existing commands keep exactly today's accept/reject behaviour.

**Architecture:** `SqlSafetyDecision` gains `Tier` and `Grantees`.
- T-SQL: `StatementClassification` carries a tier. `SqlUsage.Mutation` enables a new switch arm for schema, privilege and opaque statements.
- PostgreSQL: `StatementKind` gains `Schema`, `Privilege` and `Opaque`. Today's `Unsupported` arms split under `SqlUsage.Mutation` only.
- A node-coverage test fails when a ScriptDom statement type is neither mapped nor explicitly rejected, so a package upgrade cannot silently widen the tiers.

No command reaches `SqlUsage.Mutation` in this plan. Plan 073 wires it to the `mutation` command and the dashboard broker.

**Tech Stack:** .NET 10, C#, Microsoft.SqlServer.TransactSql.ScriptDom, SqlParserCS, xUnit.

**Spec:** [2026-10-14-mutation-approval-design.md](../docs/superpowers/specs/2026-10-14-mutation-approval-design.md) §3. Plan 073 implements the rest.

## Status

- DONE on `feat/plan-072-mutation-risk-tiers`, implementation range `7a2482c..2aafd6a`. Final product revision `2aafd6a8e053ce146608541c1855d4a2aa145db7` passed Windows `just verify`; Linux was skipped at the operator's direction and is not required while CI runs locally. Final review findings are resolved. Plan 073 depends on it.
- Priority P1. Effort M. Risk MEDIUM: safety classifier code, but `Query` and `CompareSetup` behaviour must not change.

## Global Constraints

- `SqlUsage.Query` and `SqlUsage.CompareSetup` decisions stay identical, including `Allowed`, `Reason`, `Detail` and `HasMutation`. The only addition is `Tier`. Every existing safety test passes unchanged.
- The tier order is fixed: `ReadOnly < Session < Dml < Schema < Privilege < Opaque`. A batch's tier is the maximum over its statements.
- Wire labels are exactly `read-only`, `session`, `dml`, `schema`, `privilege`, `opaque`.
- These stay rejected under every usage:
  - cross-database references and linked servers (`EXEC … AT`, four-part names, `OPENQUERY`/`OPENROWSET`);
  - `INSERT … EXEC`;
  - server and instance scope: `CREATE/ALTER/DROP LOGIN`, `ALTER SERVER ROLE`, `ALTER DATABASE`, `CREATE/DROP DATABASE`, `BACKUP`, `RESTORE`, `SHUTDOWN`, `KILL`, `DBCC`, `RECONFIGURE`, server-scoped triggers, `xp_*` procedures and every `sp_*` procedure except `sp_executesql`;
  - PostgreSQL `CREATE EXTENSION`, `CREATE/DROP DATABASE`, `COPY`, `LOAD`, `LISTEN/NOTIFY`, `SET ROLE`, `PREPARE/DEALLOCATE`, `ALTER SYSTEM`;
  - `GO` separators and parse errors.
- Under `Mutation`, `Grantees` lists every principal named as the receiver in a privilege statement:
  - GRANT/DENY/REVOKE `TO`/`FROM` principals;
  - `ALTER ROLE … ADD MEMBER x`;
  - the target user of `ALTER USER`/`CREATE USER`.
  
  Names are unquoted identifier values in source order, without deduplication.
- Do not auto-format files; touch only the lines the change needs (operator rule).
- Done means `just verify` passes on the final product tree ([Development](../README.md#development)). `just verify-linux` is not a completion criterion while CI runs locally; report it as unverified when skipped.

## Review Focus

1. `TRUNCATE TABLE dbo.Orders` under `Query` is still `UnsupportedStatement` (not `Allowed`), and under `Mutation` it is `schema`. Test in T2.
2. A batch `UPDATE dbo.T SET x = 1; DROP TABLE dbo.U;` is `schema`, not `dml`, because the highest statement wins. Test in T2.
3. `EXEC sp_configure 'show advanced options', 1` and `EXEC master.dbo.xp_cmdshell 'dir'` are rejected under `Mutation`, while `EXEC sp_executesql N'…'` is `opaque`. Test in T4.
4. `GRANT SELECT ON dbo.T TO [Reader]; ALTER ROLE db_owner ADD MEMBER Reader;` gives `Grantees = ["Reader", "Reader"]`. Test in T3.
5. PostgreSQL `DROP TABLE public.orders` is `schema` under `Mutation`. `DROP TABLE` of a proven TEMP is still `session`. Test in T5.

## File map

| File | Responsibility |
|---|---|
| `src/SqlHarness.Core/SqlRiskTier.cs` (new) | `SqlRiskTier` enum, `SqlRiskTiers.Label`, `Max` |
| `src/SqlHarness.Core/SqlSafety.cs` | `SqlUsage.Mutation`; `SqlSafetyDecision.Tier`/`Grantees`; T-SQL tier mapping |
| `src/SqlHarness.Core/SqlMutationStatementMap.cs` (new) | T-SQL statement-type sets for the schema and privilege tiers, the rejected set, opaque rules, grantee extraction |
| `src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs` | PostgreSQL tiers under `Mutation` |
| `src/SqlHarness.Core/Contracts.cs` | `SqlHarnessDryRunReport.Tier` |
| `src/SqlHarness.Core/SqlHarnessModule.cs` | fill `Tier` in dry-run reports |
| `tests/SqlHarness.Tests/SqlSafetyTierTests.cs` (new) | T-SQL tier tests |
| `tests/SqlHarness.Tests/SqlSafetyNodeCoverageTests.cs` | statement-type coverage test |
| `tests/SqlHarness.Tests/Postgres/PostgresSafetyTierTests.cs` (new) | PostgreSQL tier tests |
| `README.md` | dry-run `tier` field |

---

### Task 1: Tier model and the T-SQL baseline tiers

**Files:**
- Create: `src/SqlHarness.Core/SqlRiskTier.cs`
- Modify: `src/SqlHarness.Core/SqlSafety.cs` (the `SqlUsage` and `SqlSafetyDecision` declarations at 74-108, `StatementClassification` at 829-840, `Allowed(...)` and `ClassifyQuery` at 215-270)
- Test: `tests/SqlHarness.Tests/SqlSafetyTierTests.cs`

**Interfaces:**
- Produces:
  - `internal enum SqlRiskTier { ReadOnly, Session, Dml, Schema, Privilege, Opaque }`
  - `internal static class SqlRiskTiers { string Label(SqlRiskTier); SqlRiskTier Max(SqlRiskTier, SqlRiskTier); }`
  - `SqlUsage.Mutation`
  - `SqlSafetyDecision.Tier { get; init; }` (default `ReadOnly`)
  - `SqlSafetyDecision.Grantees { get; init; }` (default empty `IReadOnlyList<string>`)

- [x] **Step 1: Write the failing tests**

```csharp
using SqlHarness.Core;

namespace SqlHarness.Tests;

public class SqlSafetyTierTests
{
    private readonly SqlSafetyClassifier _classifier = new();

    private SqlSafetyDecision Mutation(string sql) => _classifier.Classify(sql, SqlUsage.Mutation, "appdb", false);
    private SqlSafetyDecision Query(string sql) => _classifier.Classify(sql, SqlUsage.Query, "appdb", false);

    [Theory]
    [InlineData("SELECT 1 AS A", SqlRiskTier.ReadOnly)]
    [InlineData("CREATE TABLE #t (Id int); INSERT #t VALUES (1);", SqlRiskTier.Session)]
    [InlineData("UPDATE dbo.Orders SET Total = 0 WHERE Id = 1", SqlRiskTier.Dml)]
    [InlineData("CREATE TABLE #t (Id int); DELETE dbo.Orders WHERE Id = 1;", SqlRiskTier.Dml)]
    public void Mutation_usage_reports_the_highest_tier(string sql, SqlRiskTier expected)
    {
        var decision = Mutation(sql);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.Equal(expected, decision.Tier);
    }

    [Theory]
    [InlineData("SELECT 1 AS A", SqlRiskTier.ReadOnly)]
    [InlineData("CREATE TABLE #t (Id int);", SqlRiskTier.Session)]
    public void Query_usage_reports_tier_without_changing_decisions(string sql, SqlRiskTier expected)
    {
        var decision = Query(sql);
        Assert.True(decision.Allowed);
        Assert.Equal(expected, decision.Tier);
    }

    [Fact]
    public void Query_usage_still_rejects_unapproved_dml_with_the_same_reason() =>
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, Query("DELETE dbo.Orders WHERE Id = 1").Reason);

    [Theory]
    [InlineData(SqlRiskTier.ReadOnly, "read-only")]
    [InlineData(SqlRiskTier.Session, "session")]
    [InlineData(SqlRiskTier.Dml, "dml")]
    [InlineData(SqlRiskTier.Schema, "schema")]
    [InlineData(SqlRiskTier.Privilege, "privilege")]
    [InlineData(SqlRiskTier.Opaque, "opaque")]
    public void Labels_are_stable(SqlRiskTier tier, string label) =>
        Assert.Equal(label, SqlRiskTiers.Label(tier));
}
```

- [x] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~SqlSafetyTierTests`
Expected: compile failure (`SqlRiskTier` and `SqlUsage.Mutation` do not exist).

- [x] **Step 3: Implement**

`src/SqlHarness.Core/SqlRiskTier.cs`:

```csharp
namespace SqlHarness.Core;

/// <summary>Risk of a batch's persistent effects; a batch takes the maximum over its statements.</summary>
internal enum SqlRiskTier
{
    ReadOnly,
    Session,
    Dml,
    Schema,
    Privilege,
    Opaque,
}

internal static class SqlRiskTiers
{
    internal static string Label(SqlRiskTier tier) => tier switch
    {
        SqlRiskTier.ReadOnly => "read-only",
        SqlRiskTier.Session => "session",
        SqlRiskTier.Dml => "dml",
        SqlRiskTier.Schema => "schema",
        SqlRiskTier.Privilege => "privilege",
        SqlRiskTier.Opaque => "opaque",
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

    internal static SqlRiskTier Max(SqlRiskTier left, SqlRiskTier right) => left >= right ? left : right;
}
```

In `SqlSafety.cs`:

1. Add `Mutation` to `SqlUsage`.
2. Add to `SqlSafetyDecision`:

   ```csharp
   internal SqlRiskTier Tier { get; init; }
   internal IReadOnlyList<string> Grantees { get; init; } = [];
   ```

3. Give `StatementClassification` a `SqlRiskTier Tier` field:
   - `ReadOnly` → `SqlRiskTier.ReadOnly`
   - `SessionLocal` → `SqlRiskTier.Session`
   - `Persistent` → `SqlRiskTier.Dml`
   - `Combine` takes `SqlRiskTiers.Max`.
4. In `ClassifyQuery`, accumulate `tier = SqlRiskTiers.Max(tier, classification.Tier)` next to `hasMutation`, and set `Tier = tier` on every `Allowed(...)` it returns. Do the same in `ClassifyCompareSetup` (its batches can only reach `Session`).
5. Route `SqlUsage.Mutation` to a new `ClassifyMutation(script.Batches, inspection)`. For now it copies `ClassifyQuery`'s statement loop and returns `Allowed(hasMutation: tier >= SqlRiskTier.Dml, hasSessionLocal: …) with { Tier = tier }`. There is no `allowMutation`/`confirmDatabase` check: under `Mutation`, approval is the broker's job (plan 073).

- [x] **Step 4: Run the tier tests and the full safety suite**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafety|FullyQualifiedName~Postgres"`
Expected: PASS, with every existing test unchanged.

- [x] **Step 5: Commit**

```bash
git add src/SqlHarness.Core/SqlRiskTier.cs src/SqlHarness.Core/SqlSafety.cs tests/SqlHarness.Tests/SqlSafetyTierTests.cs
git commit -m "Add SQL risk tiers and the Mutation usage to the T-SQL classifier"
```

### Task 2: T-SQL schema tier

**Files:**
- Create: `src/SqlHarness.Core/SqlMutationStatementMap.cs`
- Modify: `src/SqlHarness.Core/SqlSafety.cs` (`ClassifyMutation` and the `Classify` pre-checks at 163-212)
- Test: `tests/SqlHarness.Tests/SqlSafetyTierTests.cs`, `tests/SqlHarness.Tests/SqlSafetyNodeCoverageTests.cs`

**Interfaces:**
- Consumes: Task 1 types.
- Produces:
  - `internal static class SqlMutationStatementMap { IReadOnlySet<Type> SchemaStatementTypes; IReadOnlySet<Type> RejectedStatementTypes; }`
  - `ClassifyMutation` maps `SchemaStatementTypes` to `SqlRiskTier.Schema`.

Rules under `Mutation`. Each statement goes through these steps in order:

1. Session-local first, through the existing `ClassifyProvenStatement`. A `#temp` CREATE/DROP/TRUNCATE/ALTER/INDEX stays `Session`.
2. If the statement was denied there and its type is in `SchemaStatementTypes`, the tier is `Schema`. Persistent `SELECT … INTO dbo.X` is also `Schema`; today that case is `SelectIntoNotAllowed`.
3. If its type is in `RejectedStatementTypes`, deny with `UnsupportedStatement` and its statement detail.
4. Global inspection under `Mutation`:
   - still denies cross-database references, external access, `INSERT … EXEC`, hash-named CTEs and `GO`;
   - **ignores** `HasStatefulExpression` and `HasStatefulTableSource`, because a persistent change is already the point. Read `SafetyInspectionVisitor` first and list in the commit message which flags were relaxed.
5. Routine bodies (`CREATE/ALTER PROCEDURE|FUNCTION|TRIGGER`) are classified as `Schema` without classifying the body statements. The inspection visitor still walks them, so a cross-database name inside a body still denies the batch.
6. `CREATE TRIGGER … ON ALL SERVER` is rejected. `CreateTriggerStatement` with `TriggerObject.TriggerScope == TriggerScope.AllServer` is denied.

`SchemaStatementTypes` (verify each name compiles against the ScriptDom package version):
- `CreateTableStatement`, `AlterTableStatement` and all its subclasses (match with `IsAssignableTo(typeof(AlterTableStatement))`), `DropTableStatement`;
- `CreateIndexStatement`, `AlterIndexStatement`, `DropIndexStatement`;
- `CreateViewStatement`, `AlterViewStatement`, `CreateOrAlterViewStatement`, `DropViewStatement`;
- `CreateProcedureStatement`, `AlterProcedureStatement`, `CreateOrAlterProcedureStatement`, `DropProcedureStatement`;
- `CreateFunctionStatement`, `AlterFunctionStatement`, `CreateOrAlterFunctionStatement`, `DropFunctionStatement`;
- `CreateTriggerStatement`, `AlterTriggerStatement`, `CreateOrAlterTriggerStatement`, `DropTriggerStatement`;
- `CreateSchemaStatement`, `AlterSchemaStatement`, `DropSchemaStatement`;
- `CreateTypeTableStatement`, `CreateTypeUddtStatement`, `DropTypeStatement`;
- `CreateSequenceStatement`, `AlterSequenceStatement`, `DropSequenceStatement`;
- `CreateSynonymStatement`, `DropSynonymStatement`;
- `CreateStatisticsStatement`, `UpdateStatisticsStatement`, `DropStatisticsStatement`;
- `TruncateTableStatement`.

`RejectedStatementTypes`:
- `CreateLoginStatement`, `AlterLoginStatement`, `DropLoginStatement`;
- `AlterServerRoleStatement`, `CreateServerRoleStatement`, `DropServerRoleStatement`;
- `AlterDatabaseStatement` and its subclasses, `CreateDatabaseStatement`, `DropDatabaseStatement`;
- `BackupStatement` subclasses, `RestoreStatement`;
- `ShutdownStatement`, `KillStatement`, `DbccStatement`, `ReconfigureStatement`;
- `CreateEndpointStatement`, `AlterServerConfigurationStatement` subclasses;
- `UseStatement`.

- [x] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("CREATE TABLE dbo.T (Id int)")]
[InlineData("ALTER TABLE dbo.T ADD Name nvarchar(50)")]
[InlineData("DROP TABLE dbo.T")]
[InlineData("TRUNCATE TABLE dbo.Orders")]
[InlineData("CREATE INDEX IX_T_Id ON dbo.T (Id)")]
[InlineData("CREATE OR ALTER VIEW dbo.V AS SELECT 1 AS A")]
[InlineData("CREATE PROCEDURE dbo.P AS SELECT NEWID() AS G")]
[InlineData("SELECT Id INTO dbo.Copy FROM dbo.T")]
[InlineData("UPDATE dbo.T SET Id = 1; DROP TABLE dbo.U;")]
public void Schema_statements_are_schema_tier(string sql)
{
    var decision = Mutation(sql);
    Assert.True(decision.Allowed, decision.RejectionDescription);
    Assert.Equal(SqlRiskTier.Schema, decision.Tier);
}

[Theory]
[InlineData("DROP TABLE #t")]
[InlineData("TRUNCATE TABLE #t")]
public void Temp_ddl_stays_session_tier(string sql) =>
    Assert.Equal(SqlRiskTier.Session, Mutation("CREATE TABLE #t (Id int); " + sql).Tier);

[Theory]
[InlineData("TRUNCATE TABLE dbo.Orders")]
[InlineData("DROP TABLE dbo.T")]
public void Query_usage_still_rejects_schema_statements(string sql) =>
    Assert.Equal(SqlSafetyReason.UnsupportedStatement, Query(sql).Reason);

[Theory]
[InlineData("ALTER DATABASE CURRENT SET RECOVERY SIMPLE")]
[InlineData("CREATE LOGIN x WITH PASSWORD = 'p@ssw0rd!'")]
[InlineData("BACKUP DATABASE appdb TO DISK = 'c:\\x.bak'")]
[InlineData("DBCC FREEPROCCACHE")]
[InlineData("CREATE TRIGGER t ON ALL SERVER FOR CREATE_DATABASE AS SELECT 1 AS A")]
[InlineData("DROP TABLE otherdb.dbo.T")]
[InlineData("USE otherdb")]
public void Server_scope_and_cross_database_stay_rejected(string sql) =>
    Assert.False(Mutation(sql).Allowed);
```

In `SqlSafetyNodeCoverageTests`, add a coverage test. It enumerates every concrete, non-abstract `TSqlStatement` subclass in the ScriptDom assembly and asserts that each one is in one of these sets:
- `SqlMutationStatementMap.SchemaStatementTypes`;
- `PrivilegeStatementTypes` (Task 3);
- `RejectedStatementTypes`;
- a test-local `KnownQueryOrOpaqueStatementTypes` set that names the types `ClassifyProvenStatement` already handles, plus `ExecuteStatement`.

Other types are listed in a test-local `KnownDeniedByOmission` set with a one-line comment per group, following the existing `KnownDeniedTableReferenceTypes` pattern. The test fails on any unlisted type, so a ScriptDom upgrade forces a decision.

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafetyTierTests|FullyQualifiedName~SqlSafetyNodeCoverageTests"`
Expected: the schema-tier tests FAIL (denied).

- [x] **Step 3: Implement the map and the `ClassifyMutation` arm** as described above. Keep `Query` and `CompareSetup` paths untouched: the new code runs only when `usage == SqlUsage.Mutation`.

- [x] **Step 4: Run the full safety suites**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafety|FullyQualifiedName~Dialect"`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add src/SqlHarness.Core/SqlMutationStatementMap.cs src/SqlHarness.Core/SqlSafety.cs tests/SqlHarness.Tests/SqlSafetyTierTests.cs tests/SqlHarness.Tests/SqlSafetyNodeCoverageTests.cs
git commit -m "Classify persistent T-SQL DDL as the schema tier under Mutation"
```

### Task 3: T-SQL privilege tier and grantees

**Files:**
- Modify: `src/SqlHarness.Core/SqlMutationStatementMap.cs`, `src/SqlHarness.Core/SqlSafety.cs`
- Test: `tests/SqlHarness.Tests/SqlSafetyTierTests.cs`

**Interfaces:**
- Produces:
  - `SqlMutationStatementMap.PrivilegeStatementTypes`
  - `internal static IEnumerable<string> SqlMutationStatementMap.Grantees(TSqlStatement statement)`
  - `SqlSafetyDecision.Grantees` filled under `Mutation`.

`PrivilegeStatementTypes`:
- `GrantStatement`, `DenyStatement`, `RevokeStatement`;
- `CreateRoleStatement`, `AlterRoleStatement`, `DropRoleStatement`;
- `CreateUserStatement`, `AlterUserStatement`, `DropUserStatement`;
- `CreateApplicationRoleStatement`, `AlterApplicationRoleStatement`, `DropApplicationRoleStatement`.

A GRANT/DENY/REVOKE with no `SecurityTargetObject` (a server-level or database-level permission such as `GRANT CONTROL TO x`) is still `Privilege`. The broker's grantee rule covers it.

Grantee extraction:
- `SecurityStatement.Principals` → each `SecurityPrincipal.Identifier?.Value`; `PUBLIC` yields `"public"`;
- `AlterRoleStatement` whose action is `AddMemberAlterRoleAction` → `Member.Value`;
- `CreateUserStatement.Name.Value`;
- `AlterUserStatement.Name.Value`.

- [x] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Privilege_statements_report_tier_and_grantees()
{
    var decision = Mutation("GRANT SELECT ON dbo.T TO [Reader]; ALTER ROLE db_owner ADD MEMBER Reader;");
    Assert.True(decision.Allowed, decision.RejectionDescription);
    Assert.Equal(SqlRiskTier.Privilege, decision.Tier);
    Assert.Equal(["Reader", "Reader"], decision.Grantees);
}

[Theory]
[InlineData("REVOKE INSERT ON dbo.T FROM app")]
[InlineData("DENY DELETE ON dbo.T TO app")]
[InlineData("CREATE USER app WITHOUT LOGIN")]
[InlineData("GRANT CONTROL TO app")]
public void Other_privilege_statements_are_privilege_tier(string sql)
{
    var decision = Mutation(sql);
    Assert.Equal(SqlRiskTier.Privilege, decision.Tier);
    Assert.Equal(["app"], decision.Grantees);
}

[Fact]
public void Query_usage_still_rejects_grant() =>
    Assert.False(Query("GRANT SELECT ON dbo.T TO app").Allowed);
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~SqlSafetyTierTests`
Expected: FAIL.

- [x] **Step 3: Implement** the set, the extraction and the arm in `ClassifyMutation`. Accumulate grantees across statements in source order.

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafety"`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add src/SqlHarness.Core/SqlMutationStatementMap.cs src/SqlHarness.Core/SqlSafety.cs tests/SqlHarness.Tests/SqlSafetyTierTests.cs
git commit -m "Classify T-SQL privilege statements and collect grantees under Mutation"
```

### Task 4: T-SQL opaque tier

**Files:**
- Modify: `src/SqlHarness.Core/SqlMutationStatementMap.cs`, `src/SqlHarness.Core/SqlSafety.cs`
- Test: `tests/SqlHarness.Tests/SqlSafetyTierTests.cs`

**Interfaces:**
- Produces: `internal static bool SqlMutationStatementMap.IsAllowedOpaque(ExecuteStatement statement)`.

`ExecuteStatement` under `Mutation`:
- `ExecuteSpecification.LinkedServer` is not null → rejected.
- `ExecutableEntity` is `ExecutableStringList` (`EXEC(@sql)` / `EXEC('…')`) → `Opaque`.
- `ExecutableEntity` is `ExecutableProcedureReference` whose `ProcedureReference.ProcedureReference.Name` is:
  - a three- or four-part name → rejected;
  - `BaseIdentifier` `sp_executesql` (case-insensitive) → `Opaque`;
  - `BaseIdentifier` starting with `xp_` or `sp_` → rejected;
  - a variable procedure name (`EXEC @proc`) → rejected;
  - otherwise → `Opaque`.

The `opaque` tier also covers statements nested in `IF`/`BEGIN…END` through the existing `Combine` path.

- [x] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("EXEC dbo.RebuildTotals @From = 1")]
[InlineData("EXEC sp_executesql N'UPDATE dbo.T SET Id = 1'")]
[InlineData("DECLARE @s nvarchar(100) = N'SELECT 1'; EXEC(@s);")]
[InlineData("IF 1 = 1 EXEC dbo.P")]
public void Procedures_and_dynamic_sql_are_opaque(string sql)
{
    var decision = Mutation(sql);
    Assert.True(decision.Allowed, decision.RejectionDescription);
    Assert.Equal(SqlRiskTier.Opaque, decision.Tier);
}

[Theory]
[InlineData("EXEC sp_configure 'show advanced options', 1")]
[InlineData("EXEC master.dbo.xp_cmdshell 'dir'")]
[InlineData("EXEC xp_cmdshell 'dir'")]
[InlineData("EXEC otherdb.dbo.P")]
[InlineData("EXEC ('SELECT 1') AT LinkedSrv")]
[InlineData("DECLARE @p sysname = N'dbo.P'; EXEC @p;")]
[InlineData("INSERT dbo.T EXEC dbo.P")]
public void Dangerous_execs_stay_rejected(string sql) =>
    Assert.False(Mutation(sql).Allowed);

[Fact]
public void Query_usage_still_rejects_exec() =>
    Assert.False(Query("EXEC dbo.P").Allowed);
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~SqlSafetyTierTests`
Expected: FAIL.

- [x] **Step 3: Implement** `IsAllowedOpaque` and the `ExecuteStatement` arm in `ClassifyMutation`.

- [x] **Step 4: Run the T-SQL suites**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafety"`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add src/SqlHarness.Core/SqlMutationStatementMap.cs src/SqlHarness.Core/SqlSafety.cs tests/SqlHarness.Tests/SqlSafetyTierTests.cs
git commit -m "Classify T-SQL procedure calls and dynamic SQL as the opaque tier under Mutation"
```

### Task 5: PostgreSQL tiers

**Files:**
- Modify: `src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs` (`Classify` at 47-75, `ClassifyStatement` at 183-326, `StatementKind` at 710)
- Test: `tests/SqlHarness.Tests/Postgres/PostgresSafetyTierTests.cs`

**Interfaces:**
- Consumes: Task 1 types.
- Produces: under `SqlUsage.Mutation`, PostgreSQL decisions with `Tier` and `Grantees`. `Query` and `CompareSetup` also get `Tier`, with their decisions otherwise unchanged.

Rules under `Mutation`:
- **Proof:** a TEMP target needs the read-write name proof (`proofRequired = true`), because the batch runs read-write. A proven TEMP stays `Session`.
- **Base tiers:** `Mutation` (persistent DML) → `Dml`.
- **Schema:** `DROP TABLE`, `CREATE INDEX` and `TRUNCATE` of a non-proven target, plus:
  - `CreateTable` (non-TEMP);
  - `AlterTable`, `AlterIndex`, `AlterView`, `CreateView`, `CreateSchema`, `CreateSequence`, `CreateFunction`, `CreateProcedure`, `CreateType`, `CreateTrigger`;
  - `Drop` of any other object type;
  - `Analyze`.
- **Privilege:** `Grant`, `Revoke`, `CreateRole`, plus `AlterRole` and `DropRole` when SqlParserCS exposes them (check the `Statement` subtypes it defines). Grantees are the grantee/role names, unquoted and folded the way `FoldIdent` folds them.
- **Opaque:** `Call`, `Execute` (of a prepared statement), `Do` (if SqlParserCS parses `DO`; if it does not, `DO` stays a parse error and is therefore rejected, so note that in the test).
- **Rejected:**
  - `CreateExtension`, `CreateDatabase`, `Copy`, `Load`, `Listen`, `Notify`, `SetRole`, `SetVariable`, `Prepare`, `Deallocate`;
  - anything in `DeniedExactFunctions` (unchanged);
  - every `default:` arm.

- [x] **Step 1: Write the failing tests** (follow the `PostgresSafetyTests` construction: `new PostgresSafetyClassifier().Classify(sql, SqlUsage.Mutation, "appdb", false, null, Empty)`).

```csharp
[Theory]
[InlineData("UPDATE public.orders SET total = 0 WHERE id = 1", SqlRiskTier.Dml)]
[InlineData("DROP TABLE public.orders", SqlRiskTier.Schema)]
[InlineData("TRUNCATE public.orders", SqlRiskTier.Schema)]
[InlineData("CREATE INDEX ix_orders_id ON public.orders (id)", SqlRiskTier.Schema)]
[InlineData("ALTER TABLE public.orders ADD COLUMN note text", SqlRiskTier.Schema)]
[InlineData("GRANT SELECT ON public.orders TO reader", SqlRiskTier.Privilege)]
[InlineData("CALL public.rebuild_totals(1)", SqlRiskTier.Opaque)]
[InlineData("CREATE TEMP TABLE t (id int); DROP TABLE t;", SqlRiskTier.Session)]
public void Mutation_usage_tiers(string sql, SqlRiskTier expected)
{
    var decision = Classify(sql, SqlUsage.Mutation);
    Assert.True(decision.Allowed, decision.RejectionDescription);
    Assert.Equal(expected, decision.Tier);
}

[Fact]
public void Grant_reports_grantees() =>
    Assert.Equal(["reader"], Classify("GRANT SELECT ON public.orders TO Reader", SqlUsage.Mutation).Grantees);

[Theory]
[InlineData("CREATE EXTENSION dblink")]
[InlineData("COPY public.orders FROM PROGRAM 'cat x'")]
[InlineData("SET ROLE postgres")]
[InlineData("CREATE DATABASE other")]
[InlineData("SELECT pg_terminate_backend(1)")]
public void Rejected_under_every_usage(string sql) =>
    Assert.False(Classify(sql, SqlUsage.Mutation).Allowed);

[Theory]
[InlineData("DROP TABLE public.orders")]
[InlineData("GRANT SELECT ON public.orders TO reader")]
public void Query_usage_is_unchanged(string sql) =>
    Assert.False(Classify(sql, SqlUsage.Query).Allowed);
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~PostgresSafetyTierTests`
Expected: FAIL.

- [x] **Step 3: Implement**
  1. Add `Schema`, `Privilege` and `Opaque` to `StatementKind`, and a `Tier` property to the outcome.
  2. Pass `usage` into `ClassifyStatement`. The new arms return the new kinds only when `usage == SqlUsage.Mutation`; otherwise the original `Unsupported` stays.
  3. Add a `ClassifyMutation` that loops like `ClassifyQuery`, takes the maximum tier and collects grantees.

- [x] **Step 4: Run the PostgreSQL suites**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Postgres"`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs tests/SqlHarness.Tests/Postgres/PostgresSafetyTierTests.cs
git commit -m "Classify PostgreSQL schema, privilege and opaque statements under Mutation"
```

### Task 6: `tier` in dry-run reports, docs, plan index

**Files:**
- Modify: `src/SqlHarness.Core/Contracts.cs:178-182` (`SqlHarnessDryRunReport`), `src/SqlHarness.Core/SqlHarnessModule.cs` (every place that builds `SqlHarnessDryRunReport`), `README.md` (Safety contract › Dry run), `plans/README.md`
- Test: `tests/SqlHarness.Tests/Cli/DryRunCommandTests.cs`, `tests/SqlHarness.Mcp.Tests` (the MCP dry-run test that asserts the report shape; find it with `grep -rn "dryRun" tests/SqlHarness.Mcp.Tests`)

**Interfaces:**
- Produces: `SqlHarnessDryRunReport(string Command, string Classification, string Tier, IReadOnlyList<BenchmarkParameterReport> Parameters, int ParameterSets)`. `Tier` is a `SqlRiskTiers.Label` value. For `measure`/`compare` it is the maximum over the measured SQL and the setup.

- [x] **Step 1: Write the failing test** in `DryRunCommandTests`: `query --dry-run` over `CREATE TABLE #t (Id int); SELECT 1 AS A;` emits `"tier":"session"`, and over `SELECT 1 AS A` emits `"tier":"read-only"`. Mirror the existing assertions in that file for JSON output.

- [x] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~DryRunCommandTests`
Expected: FAIL (no `tier`).

- [x] **Step 3: Implement**
  1. Add `Tier` after `Classification` in the record and fill it from the decision(s).
  2. Update every JSON shape test that pins the dry-run report.
  3. README › Dry run: add `tier` to the listed report fields: "`tier` (`read-only`, `session`, `dml`, `schema`, `privilege`, `opaque`; the highest-risk statement wins)".

- [x] **Step 4: Run both test projects**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~DryRun|FullyQualifiedName~Contracts|FullyQualifiedName~DocsSync"` and `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true`
Expected: PASS.

- [x] **Step 5: Final gate and completion bookkeeping** — Windows `just verify` passed on reviewed product revision `2aafd6a`. Linux was skipped at the operator's direction; it is not a completion criterion while CI runs locally.

Run: `just verify --LogDirectory C:/Users/rgone/AppData/Local/Temp/sqlharness-plan072-20261015/windows`

Expected: PASS on the final reviewed product tree.

The MCP test project requires `-p:IsTestProject=true` in this checkout; without it `dotnet test` only builds and discovers no tests.

### PostgreSQL parser limits

SqlParserCS 0.6.5 does not parse `DO`, the PostgreSQL `CREATE/ALTER USER` aliases, canonical `ANALYZE`, or the tested `ALTER VIEW ... RENAME` and `CREATE PROCEDURE` forms; they remain fail-closed parse errors. `DROP ROLE` is represented as generic `Drop(ObjectType.Role)` and classified as `Privilege`. See the Task 5 report for test evidence.

## Final verification evidence

- Product revision: `2aafd6a8e053ce146608541c1855d4a2aa145db7`; implementation range: `7a2482c..2aafd6a`.
- Windows command: `just verify --LogDirectory C:/Users/rgone/AppData/Local/Temp/sqlharness-plan072-20261015/windows`; exit 0, all six stages passed. Core: 3,831 passed and 11 skipped; MCP: 386 passed and 4 skipped. UI checks and build passed.
- Linux: skipped at the operator's direction. An earlier attempt was interrupted and exited 1; no Linux pass is claimed. Per [verification policy](../docs/agents/verification.md), Linux is not a completion criterion while CI runs locally.
- Final scoped review findings were resolved on `2aafd6a`; the final Sol low re-review was clean.
