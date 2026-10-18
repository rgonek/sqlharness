# Plan 083: Table-valued and list parameters (stage D) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** plans 077 and 079 must be merged. `git diff --stat 3be878e..HEAD -- src/SqlHarness.Core/Contracts.cs src/SqlHarness.Core/SqlSafety.cs src/SqlHarness.Core/ParameterSetResolver.cs src/SqlHarness.Core/SqlParameterReferences.cs src/SqlHarness.Core/SqlExecution.cs src/SqlHarness.Core/CompareCellRunner.cs src/SqlHarness.Core/Repro src/SqlHarness.Core/Journal src/SqlHarness.Mcp src/SqlHarness.Cli`. Use 077's MCP envelope and 079's conditions model/script writer names.

**Goal:** Let agents pass table-valued parameters (inline rows or rows from a read-only query) and Dapper-style list parameters the way application code does, across read-only tools, journaled and reproducible.

**Architecture:** `SqlHarnessParameterInput` gains a kind (`scalar` | `tvp` | `list`) with rows/values or a source query. Offline, lists expand on a representative element for classification and dry run; at execution, per case after connect and before setup, SQL Server resolves table types from the catalog, runs `rowsFrom`/`valuesFrom` through the read-only classifier, validates shape, expands lists to Dapper's form, and binds TVPs as `SqlDbType.Structured`. The conditions model and reproduction scripts (079) extend to both kinds.

**Tech Stack:** .NET 10, Microsoft.Data.SqlClient (`SqlDbType.Structured`, `DataTable`/`SqlDataRecord`), ScriptDom, xUnit; MCP SDK.

**Spec:** [docs/superpowers/specs/2026-10-16-table-and-list-parameters-design.md](../docs/superpowers/specs/2026-10-16-table-and-list-parameters-design.md)

## Review amendments (2026-10-16, cross-plan review) — these override task text below

1. **Classifier must know TVP names (new Task 1b, before Task 3).** `SELECT … FROM @Ids` is denied today (`HasUnprovenTableVariableUse`). Pass declared TVP parameter names into `Classify` as proven **read-only** table sources (DML/OUTPUT INTO targeting them stays denied). `IN @Ids` is not valid T-SQL for ScriptDom: every path (query, measure, compare, watch, snapshot, setup, dry run) classifies the representative expansion; execution then uses the full expansion. Modify `src/SqlHarness.Core/SqlSafety.cs` (`Classify` signature and table-variable proof) with tests.
2. **Cases with different lists/TVPs use parameter sets**, not matrix (matrix stays one scalar parameter). The "10 ids vs 5,000 ids" focus test uses two named sets.
3. **Materialization on every accepting path.** Besides compare/measure runners, add it to the `query` path in `SqlHarnessModule`, `WatchRunner` and `SnapshotRunner`. Per-case order: connect → materialize → setup → (081) capture session state → measured runs.
4. **Conditions fields.** `ConditionsParameter` gains `Kind`, `Count`, `Columns`, `ContentHash`, `SourceText` (storeSensitive only); bump `ConditionsModel.Version`. `ContentHash` is an HMAC computed by `ActivityJournal` with the journal salt (079 amendment 2) — never an unkeyed hash. The model is re-recorded via `RecordConditions` after per-case materialization so `rowsFrom` content is fingerprinted.
5. **077 `ValueHash`.** For tvp/list kinds in measure sets, `ValueHash` is `null` (an unkeyed hash of their values would disclose low-entropy ids); scalar `ValueHash` is unchanged. Document it in the measure tool description.
6. **Version identity** already hashes the operation text (unexpanded); keep `List_length_is_condition_not_version` as a regression test.
7. **Tool catalog budget.** New schema members must keep 077's 32 KiB tool-catalog budget; add/adjust its pin test.

## Status and dependencies

- IN PROGRESS. Tasks 1–4 are implemented and approved; Task 5 documentation is implemented on the `plan-083` feature branch. Controller whole-branch review and final `just verify` plus operator-authorized live UAT remain pending. Do not treat this plan as complete or integrated. Planning base `3be878e`; depends on 077 (MCP schemas) and 079 (conditions, scripts); independent of 080–082.
- Priority P2; effort L; risk MEDIUM (binding fidelity; new execution step before setup).

## Global Constraints

- Kinds: `tvp` `{ name, type, rows | rowsFrom }`, `list` `{ name, type, values | valuesFrom }`; exactly one source each.
- Accepted by `query`, `measure`, `compare`, setup, `watch`, `snapshot`; `mutation` rejects with `table-valued and list parameters are not supported by mutation`.
- Per case, summed over all tvp/list parameters: ≤ 100,000 rows/values and ≤ 32 MiB materialized; ≤ 8 tvp/list parameters per call; ≤ 2,100 parameters per command after expansion. Error texts name the parameter and the limit.
- TVP binding: schema-qualified resolved type name, columns in type order with type, size/precision/scale and nullability from `sys.table_types`/`sys.columns`; no reordering or coercion beyond SqlClient's exact type mapping; mismatch error `rows for @{name} do not match {type}: {detail}`.
- `rowsFrom`/`valuesFrom`: read-only classifier must allow it; executed after connect, before setup, once per case; not measured; same materialized data for baseline and candidate.
- List expansion (Dapper): `IN @X` / `IN (@X)` → `IN (@X1, @X2, …, @XN)`; empty → `IN (SELECT @X WHERE 1 = 0)` with `@X` typed NULL; no padding.
- Version identity hashes the unexpanded text.
- Dry run offline: representative expansion (one element), notes `table type shape not verified offline` / `list length not known offline`.
- Conditions labels: `@Ids = 500 rows (dbo.IdList)`, `@Types IN (1, 2, 3)`, `@Types IN (… 250 values)`.
- PostgreSQL rejects both kinds: `table-valued and list parameters are SQL Server only`.
- No auto-formatting; commits `feat(083): …`; completion requires `just verify`.

## Review Focus

1. Empty TVP and empty list must execute and differ from a missing parameter → Task 3 `Empty_tvp_binds_zero_rows`, Task 2 `Empty_list_uses_dapper_empty_form`.
2. `rowsFrom` returning columns in a different order or type → explicit mismatch error, no silent coercion → Task 3 `RowsFrom_column_mismatch_is_rejected`.
3. `rowsFrom` containing a write (`SELECT … INTO dbo.T`, `UPDATE`) → rejected by the classifier before execution → Task 3 `RowsFrom_must_be_read_only`.
4. Matrix of two cases: 10 ids vs 5,000 ids → two cases, one SQL version, labels show sizes → Task 4 `List_length_is_condition_not_version`.
5. Unqualified type `[IdList]` resolving through default schema → stored as `dbo.IdList` in conditions and scripts → Task 3 `Unqualified_type_resolves_schema`.

---

### Task 0: Drift check and baseline

- [ ] Confirm 077/079 merged; drift check; `just verify`; record base and result.

### Task 1: Input model, MCP/CLI arguments and offline validation

**Files:**
- Modify: `src/SqlHarness.Core/Contracts.cs:132` (`SqlHarnessParameterInput` gains `string Kind = "scalar"`, `IReadOnlyList<IReadOnlyList<string?>>? Rows`, `IReadOnlyList<string?>? Values`, `string? SourceSql`; `ToString` still excludes values), `src/SqlHarness.Mcp/Tools/McpToolArguments.cs:14` (`McpParameterArgument` gains `kind`, `rows`, `rowsFrom`, `values`, `valuesFrom`), `McpOperationMapper.cs`, `McpToolCatalog.cs` (descriptions; mutation rejection), `src/SqlHarness.Core/Capabilities.cs` (limits), `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs` (`.sqljson` sets accept the new kinds; new `--params-json <PATH>` for fixed parameters), `src/SqlHarness.Core/ParameterSetResolver.cs`
- Test: `tests/SqlHarness.Mcp.Tests` (schema/mapping), `tests/SqlHarness.Tests/MeasureParameterSetValidationTests.cs`, CLI tests

**Interfaces:**
- Produces: `ParameterKind { Scalar, Tvp, List }`; validation errors for both-sources/no-source, >8 tvp/list, inline arity mismatch between rows; dry-run notes text exact.

- [x] **Step 1: Failing tests:** MCP mapping of each kind; `rows` and `rowsFrom` together → error; mutation with a tvp → exact error; dry run of `SELECT * FROM @Ids` with a tvp reports the offline note and classifies read-only; inline rows of unequal length → error; CLI `--params-json` round-trip; capabilities expose the limits.
- [x] **Step 2:** → FAIL. **Step 3:** Implement. **Step 4:** → PASS.
- [x] **Step 5:** Commit `feat(083): accept table-valued and list parameter inputs`.

### Task 2: Dapper list expansion

**Files:**
- Create: `src/SqlHarness.Core/ListParameterExpander.cs`
- Modify: `src/SqlHarness.Core/SqlParameterReferences.cs` (references to list names count as satisfied)
- Test: create `tests/SqlHarness.Tests/ListParameterExpanderTests.cs`

**Interfaces:**
- Produces: `static ExpandedSql ListParameterExpander.Expand(string sql, IReadOnlyList<ListValues> lists)` with `sealed record ExpandedSql(string Text, IReadOnlyList<SqlHarnessParameter> Parameters)`; `sealed record ListValues(string Name, string Type, IReadOnlyList<string?> Values)`.

- [x] **Step 1: Failing tests:** `IN @Ids` with [1,2,3] → `IN (@Ids1, @Ids2, @Ids3)` and three typed params; `IN (@Ids)` same; `IN@Ids` (no space) expands; repeated `IN @Ids` twice in text both expand with shared params; name prefix collision (`@Ids` vs `@IdsOther`) not touched; `Empty_list_uses_dapper_empty_form`; occurrences inside string literals and comments untouched (ScriptDom token-based); expansion over 2,100 parameters → error naming the list. Representative dry-run classification is covered for query, measure, compare, watch, and snapshot.
- [x] **Step 2:** → FAIL. **Step 3:** Implement on ScriptDom tokens. **Step 4:** → PASS.
- [x] **Step 5:** Commit `feat(083): expand Dapper list parameters`.

### Task 3: TVP resolution, sources and binding (SQL Server)

**Files:**
- Create: `src/SqlHarness.Core/Dialect/SqlServerTableTypes.cs`, `src/SqlHarness.Core/TableParameterMaterializer.cs`
- Modify: `src/SqlHarness.Core/SqlExecution.cs:343-360` (`BindCommand` handles `SqlDbType.Structured` with `TypeName`), `src/SqlHarness.Core/SqlSafety.cs:1332` (`SqlHarnessParameter` gains `IReadOnlyList<SqlDataRecord>? Rows` / `string? TableTypeName`; `ToString` safe), `CompareCellRunner.cs:186-198` and 079's per-case measure runner (materialize after connect, before setup; release after case)
- Test: create `tests/SqlHarness.Tests/TableParameterMaterializerTests.cs` (fake session for catalog and source queries); `CompareTests.cs`, `MeasureTests.cs`

**Interfaces:**
- Produces: `Task<TableTypeShape> SqlServerTableTypes.ResolveAsync(ISqlSession s, string typeName, CancellationToken ct)` with `sealed record TableTypeShape(string QualifiedName, IReadOnlyList<TableTypeColumn> Columns)`; `Task<MaterializedParameters> TableParameterMaterializer.MaterializeAsync(ISqlSession s, IReadOnlyList<SqlHarnessParameterInput> inputs, ISqlDialect dialect, CancellationToken ct)` enforcing per-case limits.

- [ ] **Step 1: Failing tests:** `Unqualified_type_resolves_schema`; `Empty_tvp_binds_zero_rows`; inline rows convert per column type (int, nvarchar(50), decimal(18,4), datetime2(7), bit, NULL); `RowsFrom_column_mismatch_is_rejected` (order and type); `RowsFrom_must_be_read_only` (classifier denial before execution); limits 100,000 rows / 32 MiB summed across params per case with exact errors; both sides of a compare receive identical rows; materialization runs before setup and setup can reference the TVP.
- [ ] **Step 2:** → FAIL. **Step 3:** Implement. **Step 4:** → PASS.
- [ ] **Step 5:** Commit `feat(083): resolve, materialize and bind table-valued parameters`.

### Task 4: Conditions, versions, labels and scripts

**Files:**
- Modify: 079's `ConditionsModelBuilder.cs`, `CaseLabel.cs`, `Repro/SqlServerReproScriptWriter.cs`, `Repro/SqlServerLiteral.cs`; version hashing (`OperationJournalDescriber.cs` hash of unexpanded text)
- Test: `ConditionsModelBuilderTests.cs`, `CaseLabelTests.cs`, `Repro/SqlServerReproScriptWriterTests.cs`, `JournalingBenchmarkTests.cs`

- [ ] **Step 1: Failing tests:** conditions record `effectiveType` `dbo.IdList READONLY` / `list of int`, counts, column list and content hash; rows/values/source text only with storeSensitive; fingerprint changes with content; labels exact (`@Ids = 500 rows (dbo.IdList)`, `@Types IN (1, 2, 3)`, `@Types IN (… 250 values)`); `List_length_is_condition_not_version`; script with stored rows emits `DECLARE @Ids dbo.IdList;` + `INSERT INTO @Ids (Id) VALUES …` in batches of 1,000 + `N'@Ids dbo.IdList READONLY'` + `@Ids = @Ids`; with only `rowsFrom` stored emits `INSERT INTO @Ids (Id) <query>` labelled `rows re-queried; data may have changed`; lists render the expanded parameters.
- [ ] **Step 2:** → FAIL. **Step 3:** Implement. **Step 4:** → PASS.
- [ ] **Step 5:** Commit `feat(083): journal and reproduce table-valued and list parameters`.

### Task 5: Documentation, observation closure, verification

- [x] **Step 1:** README parameters section and safety contract (rowsFrom classification, mutation rejection), `docs/mcp.md`, tool descriptions and capabilities; mark `docs/observations/table-valued-parameter-substitution.md` as addressed by 083; `skills/sqlharness/SKILL.md` guidance to pass TVPs/lists as parameters instead of in-batch fills. Capabilities already exposed all four configured limits, so its interface shape required no change.
- [ ] **Step 2 (controller-owned):** `just verify` → exit 0; record tested revision/result after whole-branch review.
- [ ] **Step 3 (operator-authorized):** UAT compare with a TVP from `rowsFrom` (500 ids) and an empty TVP case, plus a list matrix; confirm binding, labels, scripts run in SSMS; record without business data. No live UAT is authorized by this task.
- [ ] **Step 4:** Record the Task 5 evidence with commit `docs(083): record plan 083 evidence`. Keep Plan 083 in the in-progress queue until controller review, gate, and UAT are complete; move it to history only at final completion.
