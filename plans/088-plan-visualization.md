# Plan 088: SQL Server plan visualization with SQL ↔ operator linking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** plans 080 and 087 must be merged. `git diff --stat 5261846..HEAD -- src/SqlHarness.Core/Journal src/SqlHarness.Core/PlanIdentity.cs src/SqlHarness.Core/PlanDistiller.cs src/SqlHarness.Dashboard`. Take the journal schema version and dashboard component names from the merged code.

**Goal:** Reliable per-repetition plan storage with statement spans, a server-side plan graph, a graphical SQL Server plan viewer and two-way linking between the executed SQL and plan operators at statement and table-reference level.

**Architecture:** The journal stores plan documents by content and links each repetition and statement to its document and to a ScriptDom span of the executed text. The dashboard builds a renderer-independent `PlanGraph` and a source map on read; the UI renders the graph (renderer chosen in Task 0) beside a Monaco viewer and shares one selection state.

**Tech Stack:** .NET 10, Microsoft.SqlServer.TransactSql.ScriptDom (already referenced), Microsoft.Data.Sqlite, xUnit; React 19, Monaco, Vitest; renderer per Task 0.

**Spec:** [docs/superpowers/specs/2026-10-17-plan-visualization-design.md](../docs/superpowers/specs/2026-10-17-plan-visualization-design.md)

## Status and dependencies

- TODO. Planning base `5261846`. Depends on 080 and 087. Coordinate with 082: per-statement IO should consume this plan's statement occurrences if 088 lands first; otherwise 088 adapts to 082's statement model.
- Priority P2; effort L; risk MEDIUM (undocumented `TableReferenceId`, Showplan schema drift).

## Global Constraints

- SQL Server only; `PlanGraph` and source-map contracts carry an `engine` and must not assume Showplan field names in the UI.
- No backward compatibility: replace `plans`/`operation_plans`; one journal schema step; old rows are not migrated.
- Gate: `journal.storeSensitive` covers SQL text, plan documents and occurrences; otherwise `plans not stored` in dashboard and capabilities.
- Test fixtures are synthetic or hand-written Showplan/SQL that reproduce the spike cases; no UAT plan, SQL or parameter value is committed.
- Do not reformat surrounding code. Commits `feat(088): …`; `just verify`.

## Review Focus

1. Two repetitions with equal `QueryPlanHash` and different actual rows → two documents, each repetition shows its own actuals → Task 1 `Repetitions_keep_their_own_documents`.
2. Two statements with equal shape and different text (StatementId 4/5 case) → distinct documents and spans → Task 1 `Equal_shape_statements_do_not_collide`.
3. Statement longer than 4000 characters → span covers the full ScriptDom statement, `match_kind = prefix` → Task 2 `Truncated_statement_text_pairs_by_prefix`.
4. `StatementText` with ` option (PLAN PER VALUE(...))` and `<Dispatcher>` → `match_kind = psp_suffix` → Task 2 `Psp_suffix_is_stripped`.
5. Batch with DECLARE / CREATE TABLE between plan-bearing statements → pairing stays aligned → Task 2 `Planless_statements_do_not_shift_pairing`.
6. CTE referenced twice (`cards a JOIN cards b`, inner alias `cs`, TRI 1/2) → node TRI 1 ↔ `a` + CTE body, TRI 2 ↔ `b` → Task 4 `Cte_instances_resolve_by_table_reference_id`.
7. View-internal `Partners [P]` (TRI 1) and outer `Partners P` (TRI 2) → outer node exact, view node `inferred via View_…` → Task 4 `View_internal_alias_does_not_bind_to_outer_alias`.
8. Inline TVF internal alias `CI` colliding with an APPLY alias `CI` on another table → TVF node `inferred via fn_…`, APPLY node exact → Task 4 `Module_alias_collision_uses_table_name`.
9. SQL fragment with no surviving operator → `no operators` state, no invented link → Task 4 `Eliminated_branch_has_no_link`.
10. `storeSensitive` false → API and UI show `plans not stored`, no 500 → Task 5 `Plans_not_stored_is_explicit`.

---

### Task 0: Drift check and renderer benchmark
- [ ] Confirm 080 and 087 merged; drift check; `just verify` baseline; record base.
- [ ] Build two throwaway prototypes outside the shipped bundle: React SVG + `d3-zoom` with an own right-to-left tree layout, and `@xyflow/react`. Inputs: a 95-node synthetic `PlanGraph` and generated 250/1000-node trees.
- [ ] Measure compressed bundle increment (lazy chunk), first render, highlight of 20 external node ids, click latency, pan/zoom smoothness; check license of any icon set considered (vscode-mssql icons).
- [ ] Record numbers and the choice in this plan (`## Renderer decision`); delete prototypes. Commit `docs(088): record renderer decision`.

### Task 1: Content-addressed plan documents and occurrences
**Files:** `src/SqlHarness.Core/Journal/JournalSchema.cs` (replace `plans`/`operation_plans` with `plan_documents(sha256 PK, shape_hash, format, raw_size, gz)` and `plan_occurrences(metric_id, repetition, document_ordinal, statement_index, document_sha, …)`; `CurrentVersion` +1; `JournalContractTests` pin), `JournalBenchmarkBuilder.cs` (~`:115-125` document dedup by content), `BenchmarkJournalRecord.cs`, `ActivityJournal.cs` (~`:480-541` writes, gate, per-operation byte budget with `omitted` occurrences), `JournalRetention.cs` (~`:190` orphan cleanup), `src/SqlHarness.Core/PlanIdentity.cs` (shape hash kept as a named shape identity), `src/SqlHarness.Dashboard/JournalReader.cs` (`Plan` ~`:566`); tests in `tests/SqlHarness.Tests` journal suites.
- [ ] Failing tests: `Repetitions_keep_their_own_documents`; `Equal_shape_statements_do_not_collide`; identical bytes dedup to one document; budget overflow writes `omitted` occurrences; retention removes unreferenced documents; gate off writes no documents.
- [ ] FAIL → implement → PASS. Commit `feat(088): content-addressed plan documents`.

### Task 2: Statement spans from the executed text
**Files:** new `src/SqlHarness.Core/Plans/StatementSpanPairer.cs` (ScriptDom over executed text, reuse the parser setup in `Dialect/SqlServerDocument.cs`), call site where occurrences are built (Task 1), occurrence columns `span_start`, `span_length`, `match_kind`, `statement_id`, `statement_comp_id`; `StatementMetrics.cs` (~`:89-91` statement hash from the paired span text, not truncated `StatementText`).
- [ ] Failing tests: exact; `Psp_suffix_is_stripped`; normalized whitespace (CRLF vs LF) without altering literals; `Truncated_statement_text_pairs_by_prefix`; two statements sharing a 3999-character prefix → `ambiguous`; `Planless_statements_do_not_shift_pairing`; unparsable batch → `unmatched` for all, documents still stored.
- [ ] FAIL → implement → PASS. Commit `feat(088): pair plan statements with executed SQL spans`.

### Task 3: PlanGraph builder
**Files:** new `src/SqlHarness.Core/Plans/PlanGraph.cs`, `PlanGraphBuilder.cs` (Showplan → nodes with own `Object`, predicates, defined values, estimated/actual rows and executions, per-thread counters summed, warnings, `TableReferenceId`; edges with rows and estimated size; property tree served separately), using the existing `PlanDistiller` safety settings and limits; tests with synthetic Showplan fixtures under `tests/SqlHarness.Tests/Fixtures/Plans/`.
- [ ] Failing tests: own-object extraction ignores descendant RelOps; Key Lookup TRI `-1`; parallel per-thread counters summed; unknown operator rendered generically with properties; size bound truncates the property tree with an explicit marker, never the node list silently; over-limit document → explicit error state.
- [ ] FAIL → implement → PASS. Commit `feat(088): build plan graphs`.

### Task 4: Source map (statement and table-reference links)
**Files:** new `src/SqlHarness.Core/Plans/SourceMapBuilder.cs` (ScriptDom scopes: statements, CTE definitions expanded per reference, derived tables, APPLY, subqueries, DML target; `NamedTableReference` instances numbered in expanded FROM order; module references = views and functions in FROM), `SourceLink` (node id ↔ span, kind `statement|table_reference`, state `exact|inferred|ambiguous|none`, rule text).
- [ ] Failing tests (synthetic batches reproducing the spike cases): `Cte_instances_resolve_by_table_reference_id`; same table without alias in outer FROM and IN subquery (TRI 1/2); same alias on two tables in derived tables (table name decides, no TRI); `View_internal_alias_does_not_bind_to_outer_alias`; `Module_alias_collision_uses_table_name`; MSTVF `Table-valued function` + `Table Scan` ↔ invocation; non-inlined scalar UDF call ↔ Compute Scalar containing `UserDefinedFunction`; `Eliminated_branch_has_no_link`; seek + Key Lookup both link to one reference.
- [ ] FAIL → implement → PASS. Commit `feat(088): map plan operators to SQL spans`.

### Task 5: Dashboard API and capabilities
**Files:** `src/SqlHarness.Dashboard/DashboardServer.cs` (replace `/api/plans/{hash}`: `GET /api/operations/{id}/plans` manifest of variant/case/repetition/statement/span/document/shape/match kind; `GET /api/plan-documents/{sha}/graph`; `GET /api/plan-documents/{sha}/nodes/{nodeId}/properties`; `GET /api/operations/{id}/source-map?variant&parameterSet&repetition`; raw `.sqlplan` download), `JournalReader.cs`, `DashboardModels.cs`, `src/SqlHarness.Core/Capabilities.cs` (~`:195` plan storage state); tests in dashboard server suites.
- [ ] Failing tests: manifest per repetition; graph and properties bounded; source map returns executed text and links; `Plans_not_stored_is_explicit`; invalid sha → 400; missing → 404.
- [ ] FAIL → implement → PASS. Commit `feat(088): plan graph and source-map API`.

### Task 6: Plan viewer with two-way linking
**Files:** UI `src/SqlHarness.Dashboard/ui/src/api/types.ts`, `queries.ts`; new `components/plan/PlanGraphView.tsx` (renderer from Task 0, lazy chunk, own layout, icons, cost %, estimated vs actual rows, warning badge, edge width by `log(rows)`, collapse subtrees, fit to selection), `PlanPropertiesPanel.tsx`, `PlanStatementSwitcher.tsx`, `PlanSourceView.tsx` (Monaco from `components/sql-editor/monaco.tsx` / `SqlEditors.tsx`, decorations, reveal, selection events), `PlanWorkbench.tsx` (shared selection state); replace `PlanTree` use in `VariantPanel.tsx`; theme tokens for dark mode; tests next to components.
- [ ] Failing tests: click node → its spans decorated and revealed; select SQL → overlapping links' nodes highlighted and fitted; statement switch keeps the batch view; `exact`/`inferred`/`ambiguous`/`none` styles and rule tooltip; keyboard selection; `plans not stored` and `unmatched` states; dark theme.
- [ ] FAIL → implement → PASS (`npm --prefix src/SqlHarness.Dashboard/ui test`, `ui-check`). Commit `feat(088): graphical plan viewer with SQL linking`.

### Task 7: Documentation and verification
- [ ] README (plan viewer, storage gate, what linking covers and what it does not), `docs/mcp.md` / capabilities text if the plan-storage state is exposed; remove `PlanTree`-only paths no longer used; `plans/README.md`; `just verify`; queue → history; commit `docs(088): record plan 088 evidence`.

## Follow-up (separate plan)

Inference rules for object-less operators and expression lineage; module definition fetch and expansion; estimated plans; `query` plan capture; side-by-side plan comparison; PostgreSQL `PlanGraph`.
