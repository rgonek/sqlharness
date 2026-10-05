# Plan 017: A `#`-named CTE can no longer pass off persistent DML as session-local `#temp` work

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- src/SqlHarness.Core/SqlSafety.cs tests/SqlHarness.Tests/SqlSafetyTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1 (security: safety-contract bypass)
- **Effort**: S
- **Risk**: LOW (only adds denials)
- **Depends on**: plans/016-restore-green-ci.md (for a trustworthy gate)
- **Category**: security
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

SQLHarness's core promise (AGENTS.md "Safety contract") is: read-only by default, and a persistent mutation requires `--allow-mutation --confirm-database <db>`. The only exception is session-local work on local temp tables (`#name`). The T-SQL classifier decides "local temp" **purely from the target's spelling** and never looks at common table expressions. A DML statement whose `WITH` clause defines a CTE named like a temp table (`#x` or `[#x]`) over a persistent table, and whose UPDATE/DELETE/INSERT/MERGE target is that CTE name, is classified as allowed, session-local and non-mutating. SQL Server resolves the target to the CTE, and an updatable single-table CTE writes through to the base table. The result is a persistent write with no approval. It reaches CLI `query`, the MCP server (whose contract is "no persistent mutations"), `watch`, `snapshot`, `measure` (repeated N+1 times) and `compare`, including compare **setup**, where persistent writes must never be allowed.

The audit agent reproduced the classifier verdict in memory. Whether the server accepts a `#`-prefixed CTE name is **not proven live**, and this plan does not depend on it: the fix denies the shape outright. A legitimate session-local workflow never needs a CTE whose name starts with `#`.

## Current state

Files:
- `src/SqlHarness.Core/SqlSafety.cs`: the T-SQL classifier (`SqlSafetyClassifier`, nested `SafetyInspectionVisitor`).
- `tests/SqlHarness.Tests/SqlSafetyTests.cs`: classifier tests (xUnit `[Theory]`/`[InlineData]`).

How a DML target becomes "local": `SqlSafety.cs:638-648` (`ResolveTarget`):

```csharp
        if (target is not NamedTableReference named)
            return TargetResolution.Unsupported;

        if (fromClause is null)
            return TargetResolution.Resolved(named.SchemaObject);
```

`SqlSafety.cs:670-673` (`ResolveDirectTarget`, used for INSERT and MERGE) returns `Resolved(named.SchemaObject)` for any named target. Then `ClassifyDmlWrites` (around `:625-633`) does:

```csharp
        foreach (var target in targets)
        {
            if (IsLocalTemp(target))
                hasSessionLocal = true;
            else
                hasPersistent = true;
        }
```

and `IsLocalTemp` (`:781-784`) is purely syntactic:

```csharp
    private static bool IsLocalTemp(SchemaObjectName? name) =>
        name?.Identifiers.Count == 1 &&
        name.BaseIdentifier.Value.StartsWith('#') &&
        !name.BaseIdentifier.Value.StartsWith("##", StringComparison.Ordinal);
```

Nothing in `src/SqlHarness.Core` references `CommonTableExpression` or `WithCtesAndXmlNamespaces` (check: `grep -rn "CommonTableExpression\|WithCtesAndXmlNamespaces" src --include=*.cs` → no match).

The global inspection visitor (`SqlSafety.cs:882` onwards, `SafetyInspectionVisitor : TSqlFragmentVisitor`) already sets boolean flags that `Classify` (`:207-220`) turns into denials:

```csharp
        if (inspection.HasExternalAccess ||
            inspection.HasStatefulExpression ||
            inspection.HasStatefulTableSource ||
            inspection.HasExecuteInsertSource)
        {
            return Denied(SqlSafetyReason.UnsupportedStatement);
        }
```

with flag-setting overrides like:

```csharp
        public override void ExplicitVisit(NextValueForExpression node)
        {
            HasStatefulExpression = true;
            base.ExplicitVisit(node);
        }
```

The existing test for the same attack through a FROM **alias** (`SqlSafetyTests.cs:233-251`, `HashAliasOfPersistentTableRequiresApproval`) shows the expected style:

```csharp
    [Theory]
    [InlineData("UPDATE #t SET id = 42 FROM dbo.items AS #t")]
    [InlineData("DELETE #t FROM dbo.items AS #t")]
    ...
    public void HashAliasOfPersistentTableRequiresApproval(string sql)
    {
        var denied = ClassifyQuery(sql);
        Assert.False(denied.Allowed);
        ...
        Assert.False(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);
    }
```

A second, low-impact rule drift to fix in the same place: `IsScalarDeclaration` (`SqlSafety.cs:510-513`) accepts any `SqlDataTypeReference`, and `DECLARE @c CURSOR` parses as one. So a cursor variable is registered as a "scalar local" (`CollectBatchScope`, `:524-532`), against the comment "Scalar variables only" at `:374-377`. It is harmless today because OPEN/FETCH/`SET @c = CURSOR` are denied, but it widens the allow-list definition.

Conventions: comments in this file cite the plan/task that introduced a rule (`// 011/T3: ...`). Write new comments as `// 017: ...`. Denials reuse existing `SqlSafetyReason` values. Do **not** add a new enum member: reason codes are pinned in capabilities/MCP tests.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings, 0 errors |
| Classifier tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~SqlSafetyTests"` | all pass |
| Full offline gate | `pwsh -NoProfile -File scripts/verify.ps1` (from plan 016) | `verify: OK` |

## Scope

**In scope**:
- `src/SqlHarness.Core/SqlSafety.cs` (`SafetyInspectionVisitor`, `Classify`, `IsScalarDeclaration`)
- `tests/SqlHarness.Tests/SqlSafetyTests.cs`

**Out of scope**:
- `Postgres/PostgresSafetyClassifier.cs`: PostgreSQL cannot target a CTE with DML; its holes are plan 019.
- Collation-aware alias matching (`StringComparer.OrdinalIgnoreCase` at `SqlSafety.cs:650`). This was a LOW-confidence audit item and stays out until there is live evidence.
- External-access / cross-database functions (plan 018).
- `AGENTS.md` wording: the contract already says this is denied; the code is what is wrong.

## Git workflow

- Branch `fix/plan-017-hash-cte`. Commits: `test(017): pin #-named CTE DML as denied` (RED), then `fix(017): deny #-named common table expressions`, then `fix(017): cursor DECLARE is not a scalar declaration`.
- Do NOT push.

## Steps

### Step 1: Write failing tests (RED)

Add to `SqlSafetyTests.cs` a theory `HashNamedCteCannotStandInForTempTable` with these inputs. Each is a single statement; write the SQL exactly as shown.

```
WITH [#x] AS (SELECT Id, Active FROM dbo.Clients) UPDATE [#x] SET Active = 0
WITH #x AS (SELECT Id, Active FROM dbo.Clients) UPDATE #x SET Active = 0
WITH #x AS (SELECT Id FROM dbo.Clients) DELETE #x
WITH #x AS (SELECT TOP (10) Id FROM dbo.Clients) DELETE TOP (1) FROM #x
WITH #x AS (SELECT Id FROM dbo.Clients) INSERT INTO #x (Id) VALUES (1)
WITH #x AS (SELECT Id, Active FROM dbo.Clients) MERGE #x AS t USING (SELECT 1 AS Id) AS s ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET Active = 0;
WITH #x AS (SELECT Id FROM dbo.Clients) SELECT Id FROM #x
```

The last row is a pure read. It is still denied by the chosen rule (deny any `#`-named CTE), which is acceptable over-blocking.

For each row assert:
- `ClassifyQuery(sql).Allowed` is `false` and `Reason == SqlSafetyReason.UnsupportedStatement`;
- with `allowMutation: true, confirmDatabase: "db"` it is still `false` (the shape is denied, not routed to approval);
- `_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed` is `false`.

Also add one nested case through INSERT…SELECT into a real temp, expected denied:

```
CREATE TABLE #log (Id int); WITH #x AS (SELECT Id FROM dbo.Clients) INSERT #log (Id) SELECT d.Id FROM (DELETE #x OUTPUT deleted.Id) AS d (Id)
```

If ScriptDom reports a parse error for this nested row, replace it with any composable-DML form that parses, and record the substitution in the commit message. A parse error is also a denial, so the assertion still holds, but the point is to exercise the visitor.

Add a regression row asserting a normally named CTE over a persistent table **still** requires approval, unchanged behavior:

```
WITH x AS (SELECT Id, Active FROM dbo.Clients) UPDATE x SET Active = 0
```

Expected: denied with `MutationNotAllowed` without approval; allowed with `HasMutation == true` with approval.

Add a theory `CursorDeclarationIsNotScalar` with `DECLARE @c CURSOR` and `DECLARE @c CURSOR; SELECT 1`. Expected: denied (`UnsupportedStatement`).

**Verify**: build, then run the classifier tests. The new `#`-CTE rows and cursor rows FAIL (they are allowed today). The `WITH x` regression row PASSES. If any `#`-CTE row already fails for a different reason (for example `ParseError`), STOP and report which.

### Step 2: Deny `#`-named CTEs in the global inspection

In `SafetyInspectionVisitor`, add a flag `HasHashNamedCommonTableExpression` and an override:

```csharp
        // 017: a CTE named like a temp table could stand in for one as a DML
        // target and write through to its base table. No session-local
        // workflow needs such a name, so the shape is denied outright.
        public override void ExplicitVisit(CommonTableExpression node)
        {
            if (node.ExpressionName?.Value is { } name && name.StartsWith('#'))
                HasHashNamedCommonTableExpression = true;
            base.ExplicitVisit(node);
        }
```

In `Classify`, add `inspection.HasHashNamedCommonTableExpression` to the condition that returns `Denied(SqlSafetyReason.UnsupportedStatement)` (the `HasExternalAccess || ...` block).

**Verify**: classifier tests: all `#`-CTE rows pass. Full `SqlSafetyTests` pass.

### Step 3: Exclude cursor types from scalar declarations

Change `IsScalarDeclaration` so an element whose `DataType` is `SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.Cursor }` is **not** scalar:

```csharp
    private static bool IsScalarDeclaration(DeclareVariableStatement declare) =>
        declare.Declarations.All(d =>
            d is DeclareVariableElement element &&
            element.DataType is (SqlDataTypeReference or UserDataTypeReference) and
                not SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.Cursor });
```

Keep the "one definition" comment above it. If `DECLARE @c CURSOR` turns out to parse as a different statement type (e.g. `DeclareCursorStatement`) rather than `DeclareVariableStatement`, the test from Step 1 already passes before this step. In that case skip the code change and note it in the commit.

**Verify**: classifier tests all pass. In particular the existing test at `SqlSafetyTests.cs:909` (`DECLARE @t TABLE ...; DECLARE @t CURSOR; ...`) still passes.

### Step 4: Full gate

**Verify**: `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`.

## Test plan

- New theory rows (Step 1): 7 `#`-CTE rows × 3 assertions, 1 nested row, 1 regression row, 2 cursor rows.
- Pattern: `HashAliasOfPersistentTableRequiresApproval` (`SqlSafetyTests.cs:233-251`).
- Optional, recommended if a live SQL Server is explicitly authorized by the user: an opt-in integration test in `tests/SqlHarness.Tests/Integration/` (pattern `BenchmarkSessionIntegrationTests.cs`, attribute `SqlServerIntegrationFact`) that runs `WITH [#x] AS (SELECT ...) UPDATE [#x] ...` on a throwaway table **with mutation approval**. It records whether the server binds to the CTE. This is evidence only. Never run it against a user profile.

## Done criteria

- [ ] `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~SqlSafetyTests"` passes, including the new theories
- [ ] `grep -n "HasHashNamedCommonTableExpression" src/SqlHarness.Core/SqlSafety.cs` shows the flag, the override and its use in `Classify`
- [ ] `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`
- [ ] Only the two in-scope files changed (`git diff --name-only main`)
- [ ] `plans/README.md` row updated

## STOP conditions

- Any existing test in `SqlSafetyTests`, `ValidateCommandTests`, `DialectAnalysisConsistencyTests` or `CapabilitiesCommandTests` starts failing. The rule may be denying a CTE name used elsewhere; report which.
- `CommonTableExpression.ExpressionName` does not exist in ScriptDom 180.37.3 under that name. Report the actual property; do not guess.
- You find a way the fix needs a new `SqlSafetyReason` member. Report instead; capabilities wording is pinned.

## Maintenance notes

- This is a fail-closed name rule, not a binding model. If CTE-aware target resolution is ever added (to allow `#`-named CTEs for reads), the new code must resolve DML targets against statement CTE names **before** `IsLocalTemp`, and keep these tests.
- Reviewers: confirm the override calls `base.ExplicitVisit(node)`, so nested CTE bodies are still inspected for external access.
- Related, deliberately deferred: collation-dependent alias matching (OrdinalIgnoreCase vs accent-insensitive server collation). Investigate only with an `_AI` database integration test.
