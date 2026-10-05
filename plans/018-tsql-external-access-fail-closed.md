# Plan 018: T-SQL external-access and cross-database checks fail closed, and a ScriptDom upgrade cannot silently widen them

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- src/SqlHarness.Core/SqlSafety.cs src/SqlHarness.Core/Dialect/SqlServerDocument.cs tests/SqlHarness.Tests/SqlSafetyTests.cs tests/SqlHarness.Tests/SqlSafetyNodeCoverageTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1 (security)
- **Effort**: M
- **Risk**: MED (stricter rules may over-block some legitimate read syntax; each type is a recorded decision)
- **Depends on**: plans/016-restore-green-ci.md, plans/017-tsql-hash-named-cte-bypass.md (same file; land 017 first to avoid conflicts)
- **Category**: security
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

AGENTS.md promises: "Independent safety checks still reject unsupported top-level statements, cross-database access, external or stateful sources, dynamic SQL...". The T-SQL classifier enforces "external access" by overriding the visitor for **exactly four node types**. Every top-level `SELECT` without `INTO` is otherwise read-only. The pinned ScriptDom 180.37.3 already defines node types that sit outside that list:

- `OPENROWSET(PROVIDER = ..., CONNECTION = ..., OBJECT = ...)` parses as `OpenRowsetCosmos`, which is not a subclass of `OpenRowsetTableReference`. It is classified as **allowed**, even though OPENROWSET is meant to be denied (tests at `SqlSafetyTests.cs` ~78-87).
- `AI_GENERATE_EMBEDDINGS(... USE MODEL ...)`, `AI_GENERATE_RESPONSE`, other `AI*FunctionCall` types and `INVOKE_EXTERNAL_API`. On SQL Server 2025, Azure SQL and Fabric these send row data to external endpoints from a "read-only" SELECT.

Separately, the cross-database rule only inspects `SchemaObjectName` with more than two parts. A **scalar** function call `otherdb.dbo.fn(...)` is a `FunctionCall` whose `CallTarget` is a `MultiPartIdentifierCallTarget`, so it is allowed. A three-part table-valued function in FROM is correctly denied.

The structural problem is that the table-source and expression levels are **deny-lists** of node types, while the statement level is an allow-list. Every ScriptDom upgrade can add allowed-by-default node types with no test failing. This plan turns table sources into an allow-list, adds explicit external-expression denials, closes the scalar cross-database gap, and adds a reflection guard test so any new node type fails the build until someone classifies it. As the final step it moves the parser from `TSql170Parser` to `TSql180Parser`, which the package already ships, so the classifier understands the newest grammar.

## Current state

Files:
- `src/SqlHarness.Core/SqlSafety.cs`: `SafetyInspectionVisitor` (`:882` onwards) and `Classify` (`:194-233`).
- `src/SqlHarness.Core/Dialect/SqlServerDocument.cs:24-29`: the single parse entry point:

  ```csharp
      internal static SqlServerDocument Parse(string sql)
      {
          var parser = new TSql170Parser(initialQuotedIdentifiers: true);
          var fragment = parser.Parse(new StringReader(sql), out var errors);
          return new SqlServerDocument(fragment, errors);
      }
  ```
- `Directory.Packages.props`: `Microsoft.SqlServer.TransactSql.ScriptDom` `180.37.3`.

The current external-access overrides (`SqlSafety.cs:930-958`):

```csharp
        public override void ExplicitVisit(OpenRowsetTableReference node) { HasExternalAccess = true; base.ExplicitVisit(node); }
        public override void ExplicitVisit(AdHocTableReference node)      { HasExternalAccess = true; ... }
        public override void ExplicitVisit(OpenQueryTableReference node)  { HasExternalAccess = true; ... }
        public override void ExplicitVisit(BulkOpenRowset node)           { HasExternalAccess = true; ... }
        public override void ExplicitVisit(OpenXmlTableReference node)    { HasStatefulTableSource = true; ... }
```

(The real code has each on several lines; the shape is as shown.) Cross-database (`:909-917`):

```csharp
        public override void ExplicitVisit(SchemaObjectName node)
        {
            if (node.Identifiers.Count > 2)
            {
                HasCrossDatabaseReference = true;
            }

            base.ExplicitVisit(node);
        }
```

`Classify` turns the flags into `Denied(SqlSafetyReason.CrossDatabaseReference)` and `Denied(SqlSafetyReason.UnsupportedStatement)` (`:209-220`).

**ScriptDom visitor mechanics you must rely on.** `TSqlFragmentVisitor` has two families of virtuals:

- `ExplicitVisit(T node)`: called once for the concrete type `T`.
- `Visit(T node)`: called for **each type in the inheritance chain** when `VisitBaseType` is true (the default).

So overriding `public override void Visit(TableReference node)` fires for every table-reference subclass, including future ones. Confirm this in Step 1 with a test; if it does not hold, STOP.

Existing tests that must keep passing and that represent legitimate syntax (`SqlSafetyTests.cs` ~535-571): CTEs, `WITH (INDEX(...), FORCESEEK)`, `OPTION (RECOMPILE, MAXDOP 1)`, `CROSS APPLY (SELECT ...)`, `TABLESAMPLE`, window functions, `sys.tables`, `INFORMATION_SCHEMA.TABLES`, `OPENJSON` (if present — grep), table variables, `#temp`.

Conventions: comments cite the plan (`// 018: ...`). Reuse `SqlSafetyReason.UnsupportedStatement` and `CrossDatabaseReference`; do not add enum members. Test style: xUnit `[Theory]` + `[InlineData]` against `_classifier.Classify(sql, SqlUsage.Query, "db", false, null)`, and also `SqlUsage.CompareSetup` for denial rows (pattern: `Classifier_denies_OPENDATASOURCE` at ~78-87).

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Classifier tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~SqlSafety"` | all pass |
| Validate/capabilities tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~Validate|FullyQualifiedName~Capabilities|FullyQualifiedName~DialectAnalysis"` | all pass |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**:
- `src/SqlHarness.Core/SqlSafety.cs` (`SafetyInspectionVisitor`, plus new `internal static` type-classification tables)
- `src/SqlHarness.Core/Dialect/SqlServerDocument.cs` (parser class, Step 6 only)
- `tests/SqlHarness.Tests/SqlSafetyTests.cs`
- `tests/SqlHarness.Tests/SqlSafetyNodeCoverageTests.cs` (create)

**Out of scope**:
- `Postgres/*` (plan 019).
- Synonyms that point at other databases: a documented static-analysis limit (`SqlSafetyAnalysis.HiddenEffectsVerified = false`).
- Lock hints such as `TABLOCKX`/`XLOCK`: a policy decision recorded in `plans/README.md`, not part of this plan.
- `Directory.Packages.props` version bumps (plan 022 owns dependency upgrades).
- AGENTS.md / capabilities wording (the existing promise already covers this).

## Git workflow

- Branch `fix/plan-018-tsql-external-access`. One commit per step, `test(018): ...` / `fix(018): ...`. Do NOT push.

## Steps

### Step 1: Pin the gaps with failing tests (RED)

Add to `SqlSafetyTests.cs`:

1. Theory `External_sources_outside_the_classic_four_are_denied` with these rows, expected `Allowed == false`, `Reason == UnsupportedStatement`, in both `Query` and `CompareSetup`:
   - `SELECT * FROM OPENROWSET(PROVIDER = 'CosmosDB', CONNECTION = 'Account=x;Database=y', OBJECT = 'c') AS r`
   - `SELECT AI_GENERATE_EMBEDDINGS(N'text' USE MODEL MyModel)`
   - one row each for any other `AI*` call syntax and `INVOKE_EXTERNAL_API` that **parses without errors under the current parser**. Find the exact syntax by trying candidates in a scratch test and keeping the ones with `document.HasErrors == false`. Record the final rows in the commit message. If none of the `AI_*`/`INVOKE_EXTERNAL_API` forms parse, keep only the OPENROWSET and AI_GENERATE_EMBEDDINGS rows. If those two do not parse either, STOP.
2. Theory `Three_part_scalar_function_calls_are_cross_database`, expected `Reason == CrossDatabaseReference`:
   - `SELECT otherdb.dbo.fn_x(1)`
   - `SELECT Id FROM dbo.Clients WHERE otherdb.dbo.fn_x(Id) = 1`
   - `SELECT otherdb..fn_x(1)`. If this does not parse, drop it.
3. Regression theory `Two_part_and_builtin_function_calls_stay_allowed`, expected allowed:
   - `SELECT dbo.fn_x(1)`, `SELECT LEN(N'x')`, `SELECT Id FROM dbo.Clients WHERE dbo.fn_x(Id) = 1`.

**Verify**: build, run classifier tests. The rows in (1) and (2) FAIL (allowed today) and the rows in (3) PASS. Record the failing count.

### Step 2: Close the scalar cross-database gap

In `SafetyInspectionVisitor`, add:

```csharp
        // 018: a scalar call db.schema.fn(...) carries its database in the call
        // target, not in a SchemaObjectName.
        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers.Count: >= 2 })
                HasCrossDatabaseReference = true;
            base.ExplicitVisit(node);
        }
```

**Known over-block.** A CLR UDT method on an alias-qualified column, `t.Shape.STArea()`, also has a two-part call target. Add a test row documenting the decision: `SELECT t.Shape.STArea() FROM dbo.Places AS t` is **denied** (`CrossDatabaseReference`), and the unqualified form `SELECT Shape.STArea() FROM dbo.Places` stays **allowed**. If the unqualified form is also denied, STOP and report the AST shape (`CallTarget` type and identifier count).

**Verify**: the rows from Step 1 (2) and (3) and the UDT rows pass. The full classifier suite passes.

### Step 3: Turn table sources into an allow-list

In `SqlSafety.cs` add an `internal static readonly IReadOnlySet<Type> AllowedTableReferenceTypes` containing the concrete ScriptDom table-reference types that are safe inside a read-only statement. Start with:

`NamedTableReference`, `VariableTableReference`, `QueryDerivedTable`, `InlineDerivedTable`, `QualifiedJoin`, `UnqualifiedJoin`, `JoinParenthesisTableReference`, `OdbcQualifiedJoinTableReference`, `PivotedTableReference`, `UnpivotedTableReference`, `SchemaObjectFunctionTableReference`, `BuiltInFunctionTableReference`, `GlobalFunctionTableReference`, `OpenJsonTableReference`, `FullTextTableReference`, `SemanticTableReference`, `ChangeTableChangesTableReference`, `ChangeTableVersionTableReference`, `DataModificationTableReference`, `VariableMethodCallTableReference`.

Then remove names that do not exist in 180.37.3 (the compiler tells you). Add any concrete type the existing **allowed** tests need. `DataModificationTableReference` stays allowed because its writes are classified by `NestedDmlWriteVisitor`.

Add:

```csharp
        // 018: table sources are an allow-list. A type ScriptDom adds later is
        // external until someone classifies it (see SqlSafetyNodeCoverageTests).
        public override void Visit(TableReference node)
        {
            if (!AllowedTableReferenceTypes.Contains(node.GetType()))
                HasExternalAccess = true;
            base.Visit(node);
        }
```

Keep the existing `OpenXmlTableReference` → `HasStatefulTableSource` override. The four explicit external overrides can stay; they are now redundant but harmless.

**Verify**: the Cosmos OPENROWSET row passes. Run the **whole** classifier suite plus Validate/Capabilities/DialectAnalysis tests: every previously allowed test still passes. If an allowed test now fails, add its concrete type to the allow-set only if the type is plainly local, e.g. a derived-table variant. Otherwise STOP and report it.

### Step 4: Deny external-call expression types explicitly

Add `internal static readonly IReadOnlySet<Type> DeniedExpressionTypes` with every concrete type in the ScriptDom assembly whose name starts with `AI` and ends with `FunctionCall`, plus `InvokeExternalApiFunctionCall`. List them **by name in code**, not computed by reflection, so a reviewer sees each one. Get the names from the reflection listing in Step 5's test. Override `Visit(ScalarExpression node)` (or the narrowest common base the reflection shows) to set `HasExternalAccess = true` when `DeniedExpressionTypes.Contains(node.GetType())`.

Server-filesystem readers: add a small exact-name deny set for two-part system TVF calls in `SchemaObjectFunctionTableReference` (`sys.fn_get_audit_file`, `sys.fn_xe_file_target_read_file`, `sys.fn_trace_gettable`, `sys.dm_os_enumerate_filesystem`), compared `OrdinalIgnoreCase` on schema and base name. Add test rows.

**Verify**: all Step 1 (1) rows pass; the full classifier suite passes.

### Step 5: Guard test: every ScriptDom node type is classified

Create `tests/SqlHarness.Tests/SqlSafetyNodeCoverageTests.cs`. It must make the classifier classes visible to tests (check how `SqlSafetyTests` already reaches `SqlSafetyClassifier`; the Core project has `InternalsVisibleTo` for tests — grep `InternalsVisibleTo`).

1. `Every_concrete_TableReference_type_is_classified`: reflect over `typeof(TSqlFragment).Assembly` for non-abstract subclasses of `TableReference`. For each, assert it is in `AllowedTableReferenceTypes` **or** in an explicit `KnownDeniedTableReferenceTypes` set declared in the test (e.g. `OpenRowsetTableReference`, `OpenRowsetCosmos`, `InternalOpenRowset`, `AdHocTableReference`, `OpenQueryTableReference`, `BulkOpenRowset`, `OpenXmlTableReference`, any `AI*TableReference`). The failure message lists every unclassified type name.
2. `Every_AI_or_external_call_expression_type_is_denied`: reflect for non-abstract subclasses of `ScalarExpression` (or the base from Step 4) whose names start with `AI` or contain `External`. Assert each is in `DeniedExpressionTypes`.
3. `The_parser_is_the_newest_in_the_package`: reflect for types named `TSql\d+Parser` and assert `SqlServerDocument` uses the highest number. Read the parser type through a small `internal static Type ParserType` you add to `SqlServerDocument`. This test FAILS until Step 6.

**Verify**: tests 1 and 2 pass, test 3 fails (expected, documented RED for Step 6).

### Step 6: Move the parser to `TSql180Parser`

In `SqlServerDocument.Parse`, use `new TSql180Parser(initialQuotedIdentifiers: true)` and expose `ParserType => typeof(TSql180Parser)`.

**Verify**: `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`. The full suite must pass **without** editing any existing test expectation. If any existing classifier, validate or param-reference test changes verdict, STOP and report the SQL and the before/after verdicts. Do not adapt the test.

## Test plan

- New: Step 1 theories (about 10 rows), the UDT-method decision rows, server-filesystem TVF rows, and the 3 guard tests in `SqlSafetyNodeCoverageTests.cs`.
- Pattern: `Classifier_denies_OPENDATASOURCE` (Query + CompareSetup), and `Query_allows_representative_parsed_SELECT_syntax` for the regression side.

## Done criteria

- [ ] `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~SqlSafety"` passes, including `SqlSafetyNodeCoverageTests`
- [ ] `grep -n "TSql170Parser" src` → no match; `grep -n "TSql180Parser" src/SqlHarness.Core/Dialect/SqlServerDocument.cs` → match
- [ ] `grep -n "Visit(TableReference" src/SqlHarness.Core/SqlSafety.cs` → match
- [ ] `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`
- [ ] No existing test expectation was edited (`git diff main -- tests/SqlHarness.Tests/SqlSafetyTests.cs` shows only additions)
- [ ] `plans/README.md` row updated

## STOP conditions

- `Visit(TableReference)` does not fire for subclasses (the Step 3 OPENROWSET row stays allowed after the change).
- Any previously allowed test row becomes denied and its node type is not obviously local.
- The parser switch (Step 6) changes any existing verdict.
- The reflection listing shows more than about 15 unclassified table-reference types. Report the list before inventing classifications.

## Maintenance notes

- On every ScriptDom bump, `SqlSafetyNodeCoverageTests` fails until each new node type is placed in the allow-set or the known-denied set. That failure is intended; review each new type against AGENTS.md's "external or stateful sources" rule.
- The UDT-method over-block (`alias.column.Method()`) is a recorded trade-off. If users hit it, resolve the first identifier against FROM aliases in the same query scope rather than loosening the rule globally.
- Reviewers: check that every override calls `base.Visit`/`base.ExplicitVisit` so traversal still reaches nested fragments.
