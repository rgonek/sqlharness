# Complete SELECT Syntax Safety Classification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Accept the complete ScriptDom-parsable syntax surface inside top-level `SELECT` statements while preserving explicit fail-closed checks for unsafe statements and behaviors.

**Architecture:** Keep `SqlSafetyClassifier` as the shared boundary for `query`, `watch`, `snapshot`, `measure`, and both sides of `compare`. Replace the reflective nested-fragment allowlist with top-level statement classification plus `SafetyInspectionVisitor` checks for prohibited behavior; preserve the existing `OPENXML` rejection by classifying that table source explicitly.

**Tech Stack:** .NET 8, C# 12, Microsoft.SqlServer.TransactSql.ScriptDom, xUnit 2, PowerShell.

## Global Constraints

- Read-only remains the default.
- Every ScriptDom-parsable construct inside a top-level `SelectStatement` is accepted unless an independent safety rule identifies prohibited behavior.
- Unknown or unsupported top-level statements remain rejected.
- Cross-database references, external or stateful table sources, `NEXT VALUE FOR`, `INSERT ... EXEC`, forbidden `SELECT INTO`, persistent `OUTPUT INTO`, dynamic SQL, transaction control, and unapproved persistent writes remain denied.
- Rejections must not echo SQL text, literals, runtime parameters, credentials, or target secrets.
- Do not add a `sqlcmd` or command-specific fallback.
- Do not expand the set of allowed top-level statement kinds.

## File structure

- `tests/SqlHarness.Tests/SqlSafetyTests.cs`: owns compatibility and safety regression cases for the shared classifier.
- `src/SqlHarness.Core/SqlSafety.cs`: owns parsing, top-level statement policy, and AST-wide prohibited-behavior inspection.
- `README.md`, `AGENTS.md`, and `skills/sqlharness/SKILL.md`: document the user/agent-visible complete-`SELECT` contract consistently.

---

### Task 1: Replace nested-fragment allowlisting with behavioral safety classification

**Files:**
- Modify: `tests/SqlHarness.Tests/SqlSafetyTests.cs`
- Modify: `src/SqlHarness.Core/SqlSafety.cs`

**Interfaces:**
- Consumes: `SqlSafetyClassifier.Classify(string sql, SqlUsage usage, string? database, bool allowMutation, string? confirmDatabase = null)`.
- Preserves: `SqlSafetyDecision` and `SqlSafetyDecision.RejectionDescription`.
- Produces: complete nested `SELECT` syntax acceptance with unchanged top-level statement and mutation policy.

- [ ] **Step 1: Add the exact failing compatibility regression**

Add this test beside `Query_allows_safe_window_syntax`:

```csharp
[Fact]
public void Query_allows_complete_read_only_SELECT_syntax_without_fragment_registration()
{
    const string sql = """
        WITH RecentOrders AS
        (
            SELECT o.ClientId, RIGHT(o.Reference, 4) AS ReferenceSuffix
            FROM dbo.Orders AS o WITH (INDEX(IX_Orders_ClientId), FORCESEEK)
        )
        SELECT ClientId, ReferenceSuffix
        FROM RecentOrders
        OPTION (RECOMPILE, MAXDOP 1);
        """;

    var decision = ClassifyQuery(sql);

    Assert.True(decision.Allowed, decision.RejectionDescription);
}
```

This single query must reproduce the reported `IndexTableHint`,
`OptimizerHint`, `RightFunctionCall`, and `TableHint` failures.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~Query_allows_complete_read_only_SELECT_syntax_without_fragment_registration
```

Expected: FAIL because `decision.Allowed` is false and the rejection detail
names one or more of the reported nested AST fragment types. If ScriptDom rejects
the test SQL as a parse error, correct only the fixture syntax and rerun until it
fails specifically as `UnsupportedStatement`.

- [ ] **Step 3: Add a representative read-only syntax matrix while still RED**

Add this theory immediately after the exact regression:

```csharp
[Theory]
[InlineData("SELECT DISTINCT TOP (10) PERCENT WITH TIES Id FROM dbo.Clients ORDER BY Id")]
[InlineData("SELECT IIF(Active = 1, 'active', 'inactive') FROM dbo.Clients")]
[InlineData("SELECT LAG(Amount, 1, 0) IGNORE NULLS OVER (PARTITION BY ClientId ORDER BY Id) FROM dbo.Orders")]
[InlineData("SELECT p.Id FROM dbo.Parent AS p CROSS APPLY (SELECT TOP (1) c.Id FROM dbo.Child AS c WHERE c.ParentId = p.Id ORDER BY c.Id DESC) AS latest")]
[InlineData("SELECT Id FROM dbo.Clients TABLESAMPLE (10 PERCENT) OPTION (OPTIMIZE FOR UNKNOWN)")]
public void Query_allows_representative_parsed_SELECT_syntax(string sql)
{
    var decision = ClassifyQuery(sql);

    Assert.True(decision.Allowed, decision.RejectionDescription);
}
```

These cases intentionally exercise unrelated ScriptDom fragment families so
the test protects the architectural contract rather than only four named types.

- [ ] **Step 4: Run the compatibility tests and verify they fail for the same reason**

Run:

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~Query_allows_complete_read_only_SELECT_syntax_without_fragment_registration|FullyQualifiedName~Query_allows_representative_parsed_SELECT_syntax"
```

Expected: FAIL assertions with `UnsupportedStatement`, not compilation errors.
If a matrix fixture is not valid for `TSql170Parser`, replace only that fixture
with valid SQL exercising the same feature family; do not weaken the assertion.

- [ ] **Step 5: Preserve unsafe nested-source coverage before removing the allowlist**

Rename the existing `Query_denies_unallowlisted_nested_fragment` test and make
its intended behavioral rule explicit:

```csharp
[Fact]
public void Query_denies_stateful_OPENXML_table_source()
{
    var decision = ClassifyQuery(
        "SELECT Id FROM OPENXML(@handle, '/root/item') WITH (Id int '@id')");

    Assert.False(decision.Allowed);
    Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
}
```

The production change that would make this test fail is removal of nested
allowlisting without an explicit `OpenXmlTableReference` visitor rule.

- [ ] **Step 6: Implement the minimal behavioral classifier change**

In `src/SqlHarness.Core/SqlSafety.cs`:

1. Remove `using System.Collections;` and `using System.Reflection;`.
2. Delete `AllowedFragmentTypes`, `UnsupportedSyntax`,
   `CollectUnsupportedSyntax`, and `FormatUnsupported`.
3. Delete this classification gate:

```csharp
var unsupported = CollectUnsupportedSyntax(script);
if (unsupported.Any)
{
    return Denied(SqlSafetyReason.UnsupportedStatement, FormatUnsupported(unsupported));
}
```

4. Extend `SafetyInspectionVisitor` with an explicit stateful-source flag:

```csharp
internal bool HasStatefulTableSource { get; private set; }

public override void ExplicitVisit(OpenXmlTableReference node)
{
    HasStatefulTableSource = true;
    base.ExplicitVisit(node);
}
```

5. Include it in the existing prohibited-behavior gate:

```csharp
if (inspection.HasExternalAccess ||
    inspection.HasStatefulExpression ||
    inspection.HasStatefulTableSource ||
    inspection.HasExecuteInsertSource)
{
    return Denied(SqlSafetyReason.UnsupportedStatement);
}
```

Do not add any of the reported fragments to another allowlist. Do not change
`ClassifyQuery`, `ClassifyCompareSetup`, `IsSessionOnlyWork`, mutation
confirmation, or cross-database detection.

- [ ] **Step 7: Run all classifier tests and verify GREEN**

Run:

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~SqlSafetyTests
```

Expected: PASS. This includes the new compatibility cases and all existing
denials for `EXEC`, DDL, transaction control, cross-database names,
`OPENDATASOURCE`, `OPENXML`, `NEXT VALUE FOR`, persistent `SELECT INTO`,
`INSERT ... EXEC`, and persistent `OUTPUT INTO`.

- [ ] **Step 8: Run the command-path safety regressions**

Run:

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~QueryTests|FullyQualifiedName~MeasureTests|FullyQualifiedName~CompareTests|FullyQualifiedName~WatchTests|FullyQualifiedName~SnapshotTests"
```

Expected: PASS. These commands must continue to reach the same shared
classifier without adding a separate parser or fallback.

- [ ] **Step 9: Commit the classifier slice**

```powershell
git add .\src\SqlHarness.Core\SqlSafety.cs .\tests\SqlHarness.Tests\SqlSafetyTests.cs
git commit -m "fix: accept complete safe select syntax"
```

---

### Task 2: Publish the complete-SELECT contract and run the repository gate

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `skills/sqlharness/SKILL.md`

**Interfaces:**
- Consumes: the classifier behavior delivered by Task 1.
- Produces: identical human-facing and agent-facing guidance for authoring read-only SQL.

- [ ] **Step 1: Document the contract in the main safety table**

In the `SQL input` row of `README.md`, after the sentence describing the single
SQL source and bound parameters, add:

```markdown
Parsed syntax inside a top-level `SELECT` is accepted without a fragment allowlist; independent safety checks still reject cross-database access, external or stateful sources, dynamic SQL, and writes outside the approved local-`#temp` or mutation contracts.
```

Keep the existing helper and mutation text in the same row.

- [ ] **Step 2: Synchronize the agent contract**

In `AGENTS.md`, add this paragraph immediately before `## Benchmark setup contract`:

```markdown
For agent-authored SQL, every ScriptDom-parsable construct inside a top-level `SELECT` is accepted without per-fragment registration. This includes CTEs, scalar functions, table/index hints, optimizer hints, windowing, and derived/apply syntax. Independent safety checks still reject unsupported top-level statements, cross-database access, external or stateful sources, dynamic SQL, and writes outside the approved local-`#temp` or mutation contracts. Do not fall back to `sqlcmd` merely because a safe nested `SELECT` construct is unfamiliar to the parser AST.
```

- [ ] **Step 3: Synchronize the reusable SQLHarness skill**

In `skills/sqlharness/SKILL.md`, add this paragraph immediately before its
safety section:

```markdown
Agent-authored SQL may use every ScriptDom-parsable construct inside a top-level `SELECT`, including CTEs, scalar functions, table/index hints, optimizer hints, windowing, and derived/apply syntax. SQLHarness classifies safety by the top-level statement and explicit prohibited behaviors, not by a nested-fragment allowlist. A rejection must not be bypassed, but an unfamiliar safe nested fragment should be reported as a SQLHarness compatibility bug rather than worked around with `sqlcmd`.
```

- [ ] **Step 4: Check documentation consistency and formatting**

Run:

```powershell
rg -n -- "fragment allowlist|top-level.*SELECT|sqlcmd" .\README.md .\AGENTS.md .\skills\sqlharness\SKILL.md
git diff --check
```

Expected: the three surfaces describe the same contract; `git diff --check`
exits `0`.

- [ ] **Step 5: Run the complete repository verification gate**

Run in order:

```powershell
dotnet format .\SqlHarness.sln --verify-no-changes
dotnet build .\SqlHarness.sln -c Release
dotnet test .\SqlHarness.sln -c Release --no-build
git diff --check
```

Expected: formatting exits `0`, build succeeds with zero warnings/errors, all
tests pass, and the diff check is clean. A timeout or aborted command is
incomplete evidence and must be rerun or reported as incomplete.

- [ ] **Step 6: Commit the synchronized contract**

```powershell
git add .\README.md .\AGENTS.md .\skills\sqlharness\SKILL.md
git commit -m "docs: define complete select syntax contract"
```

- [ ] **Step 7: Inspect the final bounded diff**

Run:

```powershell
git status --short --branch
git diff HEAD~2..HEAD --check
git diff HEAD~2..HEAD --stat
```

Expected: only the classifier, classifier tests, and three synchronized
documentation surfaces changed after the already committed design and plan;
the working tree is clean. Do not push or merge without an explicit request.
