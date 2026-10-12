# Plan 057: T-SQL `IF … ELSE` and `BEGIN … END` are classified, not rejected

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** `git diff --stat 27d2a9a..HEAD -- src/SqlHarness.Core/SqlSafety.cs tests/SqlHarness.Tests/SqlSafetyTests.cs tests/SqlHarness.Tests/SqlSafetyNodeCoverageTests.cs README.md docs/mcp.md`. Plan 056 changes the denial detail in `ClassifyQuery`; build on it.

**Goal:** An application batch that branches with `IF … ELSE` can be queried, measured and compared as written, under exactly the safety rules that already apply to each statement inside it.

**Architecture:** `ClassifyProvenStatement` gains cases for `IfStatement` and `BeginEndBlockStatement` that classify every contained statement with the existing rules and combine the results (any denial denies; any persistent write is a persistent write; any session-local write is session-local). Variable declarations inside blocks join the batch scope, because T-SQL variables are batch-scoped. The predicate needs no new rule: the global inspection visitor already walks the whole script, predicates included. Every other control-flow statement stays denied.

**Tech Stack:** .NET 8/10, C#, xUnit, Microsoft.SqlServer.TransactSql.ScriptDom.

**Spec:** [`docs/superpowers/specs/2026-10-12-agent-contract-review-design.md`](../docs/superpowers/specs/2026-10-12-agent-contract-review-design.md), decision 8.

## Status

- Status: TODO
- Priority: P2
- Effort: S
- Risk: HIGH (widens the safety classifier; needs a security-focused review before merge)
- Depends on: 056 (denial detail format)
- Planned at: `27d2a9a`, 2026-10-12

## Global Constraints

- Allowed control flow: `IF`, `ELSE`, `BEGIN … END` (any nesting). Still denied: `WHILE`, `BREAK`, `CONTINUE`, `GOTO`, labels, `RETURN`, `TRY … CATCH`, `WAITFOR`, `THROW`, `RAISERROR`.
- A branch is classified even when its predicate is constant false: the classifier never evaluates predicates.
- SQL Server only. PostgreSQL classification is unchanged.

## Review Focus

- A persistent write inside any branch, including a never-taken `ELSE`, must deny the batch on the MCP path (Task 1 test).
- A cross-database or external reference inside the `IF` predicate must deny the batch (Task 1 test).
- `EXEC` inside a branch must deny the batch (Task 1 test).
- A variable declared inside a `BEGIN … END` and used after the block must be accepted, because T-SQL scope is the batch (Task 2 test).
- A table variable used inside a branch before its declaration must stay denied (Task 2 test).

## Why this matters

Application batches commonly choose a branch from a looked-up flag. Today such a batch is rejected as `unsupported_statement`, so the agent splits it into branch batches, which measures something other than what the application runs (in the full batch the branch values are local variables, which the optimizer estimates differently from parameters).

## Current state (verify each)

1. `ClassifyProvenStatement` (`SqlSafety.cs:338-436`) has no `IfStatement` or `BeginEndBlockStatement` case; the default branch denies them.
2. `CollectBatchScope` (`SqlSafety.cs:498-527`) collects declarations from `batch.Statements` only.
3. `ClassifyCompareSetup` (≈ `:276-324`) denies any statement that is not a `SelectStatement`/`SetVariableStatement` and has no session-local write.
4. `SafetyInspectionVisitor` is a `TSqlFragmentVisitor` over the whole script; it visits predicates and nested statements.

---

### Task 1: Classify `IF`, `ELSE` and `BEGIN … END`

**Files:**
- Modify: `src/SqlHarness.Core/SqlSafety.cs` (`ClassifyProvenStatement`, `ClassifyCompareSetup` statement-kind guard)
- Test: `tests/SqlHarness.Tests/SqlSafetyTests.cs`

**Interfaces:**
- Produces: `private static StatementClassification Combine(StatementClassification left, StatementClassification right)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("DECLARE @UseGroup bit = 0; IF (@UseGroup = 1) BEGIN SELECT 1 AS A; END ELSE BEGIN SELECT 2 AS A; END;")]
[InlineData("DECLARE @X int = 1; IF @X = 1 SELECT 1 AS A;")]
[InlineData("DECLARE @X int = 1; IF @X = 1 BEGIN IF @X = 2 SELECT 1 AS A; ELSE SELECT 2 AS A; END")]
[InlineData("BEGIN SELECT 1 AS A; END")]
public void Query_allows_branching_reads(string sql) =>
    Assert.True(ClassifyQuery(sql).Allowed);

[Fact]
public void Query_allows_temp_work_inside_branches()
{
    var decision = ClassifyQuery("DECLARE @X int = 1; CREATE TABLE #t (Id int); IF @X = 1 BEGIN INSERT #t VALUES (1); END ELSE BEGIN INSERT #t VALUES (2); END; SELECT Id FROM #t;");
    Assert.True(decision.Allowed);
}

[Theory]
[InlineData("IF 1 = 0 UPDATE dbo.Clients SET Active = 0;")]
[InlineData("IF 1 = 1 SELECT 1 AS A; ELSE DELETE dbo.Clients;")]
[InlineData("IF 1 = 1 BEGIN SELECT 1 AS A; END ELSE BEGIN INSERT dbo.Clients (Id) VALUES (1); END")]
public void Query_denies_a_persistent_write_in_any_branch(string sql)
{
    var decision = _classifier.Classify(sql, SqlUsage.Query, "db", false, null);
    Assert.False(decision.Allowed);
    Assert.Equal(SqlSafetyReason.MutationNotAllowed, decision.Reason);
}

[Theory]
[InlineData("IF EXISTS (SELECT 1 FROM OtherDb.dbo.Clients) SELECT 1 AS A;")]
[InlineData("IF 1 = 1 EXEC dbo.DoWork;")]
[InlineData("IF 1 = 1 SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'SELECT 1');")]
public void Query_denies_unsafe_content_in_predicates_and_branches(string sql) =>
    Assert.False(ClassifyQuery(sql).Allowed);

[Theory]
[InlineData("WHILE 1 = 0 SELECT 1 AS A;", "WhileStatement")]
[InlineData("BEGIN TRY SELECT 1 AS A; END TRY BEGIN CATCH SELECT 2 AS A; END CATCH", "TryCatchStatement")]
[InlineData("IF 1 = 1 WAITFOR DELAY '00:00:01';", "WaitForStatement")]
[InlineData("IF 1 = 1 RETURN;", "ReturnStatement")]
public void Query_still_denies_other_control_flow(string sql, string kind)
{
    var decision = ClassifyQuery(sql);
    Assert.False(decision.Allowed);
    Assert.StartsWith(kind, decision.Detail);
}
```

Use the denial reason the classifier gives cross-database references on HEAD; if `ClassifyQuery` in the test file passes `allowMutation: true`, the persistent-write tests call `_classifier.Classify(..., allowMutation: false, ...)` directly as written above.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafetyTests"`
Expected: the allow tests FAIL (`unsupported_statement`); deny tests may already pass.

- [ ] **Step 3: Implement**

In `ClassifyProvenStatement` before `default`:

```csharp
case IfStatement ifStatement:
    // The predicate rides on the global inspection. Every branch is classified,
    // taken or not: the classifier never evaluates predicates.
    return ifStatement.ElseStatement is null
        ? ClassifyProvenStatement(ifStatement.ThenStatement, scope)
        : Combine(
            ClassifyProvenStatement(ifStatement.ThenStatement, scope),
            ClassifyProvenStatement(ifStatement.ElseStatement, scope));

case BeginEndBlockStatement block:
    var combined = StatementClassification.ReadOnly;
    foreach (var inner in block.StatementList.Statements)
    {
        combined = Combine(combined, ClassifyProvenStatement(inner, scope));
        if (combined.DenyReason is not null)
            return combined;
    }
    return combined;
```

`BeginEndAtomicBlockStatement` derives from `BeginEndBlockStatement`; deny it explicitly first (`case BeginEndAtomicBlockStatement: return StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);`), because atomic blocks belong to natively compiled modules.

```csharp
private static StatementClassification Combine(StatementClassification left, StatementClassification right) =>
    left.DenyReason is not null ? left
    : right.DenyReason is not null ? right
    : new(null, left.HasSessionLocalWrite || right.HasSessionLocalWrite, left.HasPersistentWrite || right.HasPersistentWrite);
```

Nested `DeclareVariableStatement`s reach `ClassifyProvenStatement` through the block and are classified by the existing `DeclareVariableStatement` case.

In `ClassifyCompareSetup`, extend the read-only guard to `statement is not (SelectStatement or SetVariableStatement or IfStatement or BeginEndBlockStatement)`.

The unsupported-statement detail from plan 056 must name the innermost denied statement. Change `ClassifyProvenStatement`'s denial for nested statements to carry it: return the denied inner statement's detail by adding a `TSqlStatement? DeniedStatement` member to `StatementClassification` (set in `Denied(...)` calls inside the two new cases) and use `classification.DeniedStatement ?? statement` in `StatementDetail(...)`.

- [ ] **Step 4: Run focused tests**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafety"`
Expected: PASS, including `SqlSafetyNodeCoverageTests`.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Core/SqlSafety.cs tests/SqlHarness.Tests/SqlSafetyTests.cs
git commit -m "feat(057): classify IF/ELSE and BEGIN/END by their branches"
```

---

### Task 2: Declarations inside blocks join the batch scope

**Files:**
- Modify: `src/SqlHarness.Core/SqlSafety.cs` (`CollectBatchScope`)
- Test: `tests/SqlHarness.Tests/SqlSafetyTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Variable_declared_inside_a_block_is_in_batch_scope()
{
    var decision = ClassifyQuery("IF 1 = 1 BEGIN DECLARE @Limit int = 5; END; SET @Limit = 6; SELECT @Limit AS L;");
    Assert.True(decision.Allowed);
}

[Fact]
public void Table_variable_used_in_a_branch_before_its_declaration_is_denied()
{
    var decision = ClassifyQuery("IF 1 = 1 BEGIN SELECT Id FROM @ids; END; DECLARE @ids TABLE (Id int);");
    Assert.False(decision.Allowed);
}

[Fact]
public void Table_variable_declared_in_a_branch_and_used_later_is_allowed()
{
    var decision = ClassifyQuery("IF 1 = 1 BEGIN DECLARE @ids TABLE (Id int); INSERT @ids VALUES (1); END; SELECT Id FROM @ids;");
    Assert.True(decision.Allowed);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafetyTests.Variable_declared|FullyQualifiedName~SqlSafetyTests.Table_variable"`
Expected: the first and third FAIL (declaration not collected).

- [ ] **Step 3: Implement**

Make `CollectBatchScope` walk nested statements:

```csharp
foreach (var statement in Flatten(batch.Statements))
{
    // existing body unchanged
}

static IEnumerable<TSqlStatement> Flatten(IEnumerable<TSqlStatement> statements)
{
    foreach (var statement in statements)
    {
        yield return statement;
        var nested = statement switch
        {
            IfStatement ifStatement => ifStatement.ElseStatement is null
                ? [ifStatement.ThenStatement]
                : new[] { ifStatement.ThenStatement, ifStatement.ElseStatement },
            BeginEndBlockStatement block => block.StatementList.Statements,
            _ => (IEnumerable<TSqlStatement>)[],
        };
        foreach (var inner in Flatten(nested))
            yield return inner;
    }
}
```

`DeclarationEnd` uses fragment offsets, so "declared before use" still holds for nested declarations.

- [ ] **Step 4: Run and commit**

Run: `dotnet test tests/SqlHarness.Tests` → PASS.

```bash
git add src/SqlHarness.Core/SqlSafety.cs tests/SqlHarness.Tests/SqlSafetyTests.cs
git commit -m "feat(057): declarations inside blocks join the batch scope"
```

---

### Task 3: End-to-end benchmark of a branching batch, docs, gates

**Files:**
- Test: `tests/SqlHarness.Tests/CompareTests.cs` (fake-session compare of a branching baseline vs candidate)
- Modify: `README.md` (Safety contract: supported control flow), `docs/mcp.md`, `skills/sqlharness/SKILL.md`

- [ ] **Step 1: Write the test**

Following an existing successful compare test in `CompareTests.cs`, compare baseline `DECLARE @Flag bit = 0; IF @Flag = 1 SELECT 1 AS A ELSE SELECT 2 AS A;` with an identical candidate under typed parameters none, and assert `ExitCode == Success` and `Classification.Baseline == "read-only"` (use the property names of the compare report on HEAD).

- [ ] **Step 2: Run** `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CompareTests"` → PASS.

- [ ] **Step 3: Docs.** List the allowed and denied control flow verbatim from Global Constraints; state that every branch is classified whether or not it can run.

- [ ] **Step 4: Security review.** Before merge, request a review focused on: nested denial propagation, atomic blocks, declarations hoisted out of blocks, predicates with subqueries. Record the reviewer's verdict in this plan.

- [ ] **Step 5: Gates and index.** `dotnet build SqlHarness.sln -warnaserror`; `pwsh scripts/verify.ps1`; `pwsh scripts/verify-linux.ps1`. Status DONE; update `plans/README.md`.

```bash
git add tests README.md docs skills plans
git commit -m "docs(057): supported T-SQL control flow"
```

## Done criteria

- [ ] All Task 1 and Task 2 tests pass; no existing safety test changed its expectation.
- [ ] A branching batch compares end to end on the fake session.
- [ ] Security review verdict recorded.
- [ ] Both gates pass; README row updated.

## STOP conditions

- Any existing denial test starts passing as allowed. Stop: the change widened more than control flow.
- ScriptDom on HEAD parses `IF … ELSE` into a shape other than `IfStatement` with `ThenStatement`/`ElseStatement` for some syntax (for example a trailing `;` between branches). Add that shape as a test and stop for review if it cannot be handled without new rules.
