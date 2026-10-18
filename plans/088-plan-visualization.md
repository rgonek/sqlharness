# Plan 088: SQL Server plan visualization with SQL ↔ operator linking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** plans 080 and 087 must be merged. `git diff --stat 5261846..HEAD -- src/SqlHarness.Core/Journal src/SqlHarness.Core/PlanIdentity.cs src/SqlHarness.Core/PlanDistiller.cs src/SqlHarness.Dashboard`. Take the journal schema version and dashboard component names from the merged code.

**Goal:** Reliable per-repetition plan storage with statement spans, a server-side plan graph, a graphical SQL Server plan viewer and two-way linking between the executed SQL and plan operators at statement and table-reference level.

**Architecture:** The journal stores plan documents by content and links each repetition and statement to its document and to a ScriptDom span of the executed text. The dashboard builds a renderer-independent `PlanGraph` and a source map on read; the UI renders the graph (renderer chosen in Task 0) beside a Monaco viewer and shares one selection state.

**Tech Stack:** .NET 10, Microsoft.SqlServer.TransactSql.ScriptDom (already referenced), Microsoft.Data.Sqlite, xUnit; React 19, Monaco, Vitest; renderer per Task 0.

**Spec:** [docs/superpowers/specs/2026-10-17-plan-visualization-design.md](../docs/superpowers/specs/2026-10-17-plan-visualization-design.md)

## Status and dependencies

- IMPLEMENTED — Tasks 0–7 are complete on `plan-088`; the final Windows branch gate passed. Task reviews for Tasks 0–6 are approved; Task 7 review and the Sol low whole-branch review remain pending. See [verification evidence](088-verification-evidence.md). Planning base `5261846`. Depends on 080 and 087. Coordinate with 082: per-statement IO should consume this plan's statement occurrences if 088 lands first; otherwise 088 adapts to 082's statement model.
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
- [x] Confirm 080 and 087 merged; drift check; run `just verify` baseline; record base. Base: `272d5dbb5f721536f85355b925273507e22de67b`; 080 merge `42a1e365b14843e301e8db3922ca9204fb41fc85`; 087 merge `c4de0da03878be7d7ed230ba90a4e8d2e1b92c4c`. The targeted diff from `5261846` contains the expected 080 dashboard/journal and 087 measurement-fingerprint changes; `JournalSchema.CurrentVersion` is 16. The baseline gate failed in the MCP test stage; details are in the Task 0 report.
- [x] Build two throwaway prototypes outside the shipped bundle: React SVG + `d3-zoom` with an own right-to-left tree layout, and `@xyflow/react`. Inputs: a 95-node synthetic `PlanGraph` and generated 250/1000-node trees.
- [x] Measure compressed bundle increment (lazy chunk), first render, highlight of 20 external node ids, click latency, pan/zoom smoothness; check license of any icon set considered (vscode-mssql icons). Neither prototype uses an icon set.
- [x] Record numbers and the choice below; delete prototypes. Commit `docs(088): record renderer decision`.

## Renderer decision

Choose `@xyflow/react` 12.12.0. At the expected 95-node scale, both candidates stayed close to 16.7 ms per animation frame for pan and wheel zoom, and React Flow's 169 ms median first render remained acceptable for a deferred viewer. In the 1000-node stress tree, React Flow kept pan at 16.7 ms median / 16.8 ms p95 with no dropped frames, while the SVG renderer reached 49.9 / 83.4 ms and dropped 8 frames per 600 ms sample. React Flow also kept wheel zoom to 33.3 / 33.5 ms versus 66.7 / 83.5 ms for SVG. The React SVG prototype used a custom right-to-left layout with `x = graph.width - 90 - depth * 220`. The tradeoff is 43.82 KB more compressed lazy payload (60.26 KB versus 16.44 KB). The full benchmark and method are in [the Task 0 report](088-evidence/task-0-report.md).

Three fresh-page samples per renderer and size; values below are medians across samples. Pan and zoom columns show median / p95 frame interval in ms and the median number of intervals over 25 ms per 600 ms synthetic interaction. Click is the median dispatch-to-handler time across 25 clicks per sample. First render is measured from React root render to the renderer's first layout effect.

| Nodes | Renderer | First render | Highlight 20 ids | Click | Pan ms (median / p95; drops) | Wheel zoom ms (median / p95; drops) |
|---:|---|---:|---:|---:|---|---|
| 95 | React SVG + `d3-zoom` | 98.9 | 9.2 | 0.3 | 16.7 / 16.8; 1 | 16.7 / 16.8; 0 |
| 95 | `@xyflow/react` | 168.8 | 10.7 | 0.3 | 16.7 / 16.8; 0 | 16.7 / 16.8; 0 |
| 250 | React SVG + `d3-zoom` | 126.7 | 12.9 | 0.3 | 16.7 / 16.9; 1 | 16.7 / 33.4; 10 |
| 250 | `@xyflow/react` | 220.0 | 13.1 | 0.3 | 16.7 / 16.8; 0 | 16.7 / 16.8; 0 |
| 1000 | React SVG + `d3-zoom` | 178.3 | 40.2 | 0.4 | 49.9 / 83.4; 8 | 66.7 / 83.5; 8 |
| 1000 | `@xyflow/react` | 384.0 | 43.0 | 0.4 | 16.7 / 16.8; 0 | 33.3 / 33.5; 13 |

The Vite production lazy chunks, excluding the shared application entry, gzip to 16.44 KB for the D3 renderer and 57.70 KB JavaScript + 2.56 KB CSS (60.26 KB total) for React Flow. The 95-node first-render cost is about 70 ms higher with React Flow; this is the intentional cost for substantially steadier interaction as plans grow. The stress tree exceeds the largest observed 95-operator single-statement plan, but demonstrates that the renderer will tolerate larger combined or future graphs better.

The prototypes use no icon set. The official [vscode-mssql repository license](https://github.com/microsoft/vscode-mssql/blob/main/LICENSE.txt) is MIT, and its [third-party notices](https://github.com/microsoft/vscode-mssql/blob/main/ThirdPartyNotices.txt) list additional licenses. No specific upstream icon asset was copied or evaluated for reuse.

### Task 1: Content-addressed plan documents and occurrences
**Files:** `src/SqlHarness.Core/Journal/JournalSchema.cs` (replace `plans`/`operation_plans` with `plan_documents(sha256 PK, shape_hash, format, raw_size, gz)` and `plan_occurrences(metric_id, repetition, document_ordinal, statement_index, document_sha, …)`; schema v17; `JournalContractTests` pin), `JournalBenchmarkBuilder.cs` (~`:115-125` document dedup by content), `BenchmarkJournalRecord.cs`, `ActivityJournal.cs` (~`:480-541` writes, gate, 16 MiB per-operation raw UTF-8 byte budget with explicit `omitted` occurrences), `JournalRetention.cs` (~`:190` orphan cleanup), `src/SqlHarness.Core/PlanIdentity.cs` (shape hash kept as a named shape identity), `src/SqlHarness.Dashboard/JournalReader.cs` (`Plan` ~`:566`); tests in `tests/SqlHarness.Tests` journal suites.
- [x] Failing tests: `Repetitions_keep_their_own_documents`; `Equal_shape_statements_do_not_collide`; identical bytes dedup to one document; budget overflow writes `omitted` occurrences; retention removes unreferenced documents; gate off writes no documents or occurrences.
- [x] FAIL → implement → PASS. Commit `feat(088): content-addressed plan documents`.

### Task 2: Statement spans from the executed text
**Files:** new `src/SqlHarness.Core/Plans/StatementSpanPairer.cs` (ScriptDom over executed text, reuse the parser setup in `Dialect/SqlServerDocument.cs`), call site where occurrences are built (Task 1), occurrence columns `span_start`, `span_length`, `match_kind`, `statement_id`, `statement_comp_id`; `StatementMetrics.cs` (~`:89-91` statement hash from the paired span text, not truncated `StatementText`).
- [x] Failing tests: exact; `Psp_suffix_is_stripped`; normalized whitespace (CRLF vs LF) without altering literals; `Truncated_statement_text_pairs_by_prefix`; two statements sharing a 3999-character prefix → `ambiguous`; `Planless_statements_do_not_shift_pairing`; unparsable batch → `unmatched` for all, documents still stored.
- [x] FAIL → implement → PASS. Commit `feat(088): pair plan statements with executed SQL spans`.
- [x] Review-fix round: shared ordered pairing across documents; token-boundary-preserving normalization; repeated candidates stay `ambiguous` absent an ordering anchor; PSP suffix markers inside comments/literals are preserved; persisted spans assert exact offsets/lengths; paired full-span hashes feed journal and artifact statement metrics. Focused RED/GREEN evidence and SQL execution callsite trace are in the [Task 2 report](088-evidence/task-2-report.md). Commit `feat(088): fix ordered statement pairing identities`.

### Task 3: PlanGraph builder
**Files:** new `src/SqlHarness.Core/Plans/PlanGraph.cs`, `PlanGraphBuilder.cs` (Showplan → nodes with own `Object`, predicates, defined values, estimated/actual rows and executions, per-thread counters summed, warnings, `TableReferenceId`; edges with rows and estimated size; property tree served separately), using the existing `PlanDistiller` safety settings and limits; tests with synthetic Showplan fixtures under `tests/SqlHarness.Tests/Fixtures/Plans/`.
- [x] Failing tests: own-object extraction ignores descendant RelOps; Key Lookup TRI `-1`; parallel per-thread counters summed; unknown operator rendered generically with properties; predicates remain uncut; property-tree UTF-8 size bound adds an explicit truncation marker while preserving the complete node list; node, graph-response, and document limits return explicit errors.
- [x] FAIL → implement → PASS. Focused RED/GREEN evidence and contract/limit decisions are in the [Task 3 report](088-evidence/task-3-report.md). Commit `feat(088): build plan graphs`.

### Task 4: Source map (statement and table-reference links)
**Files:** new `src/SqlHarness.Core/Plans/SourceMapBuilder.cs` (ScriptDom scopes: statements, CTE definitions expanded per reference, derived tables, APPLY, subqueries, DML target; `NamedTableReference` instances numbered in expanded FROM order; module references = views and functions in FROM), `SourceLink` (node id ↔ span, kind `statement|table_reference`, state `exact|inferred|ambiguous|none`, rule text).
- [x] Failing tests (synthetic batches reproducing the spike cases): `Cte_instances_resolve_by_table_reference_id`; same table without alias in outer FROM and IN subquery (TRI 1/2); same alias on two tables in derived tables (table name decides, no TRI); `View_internal_alias_does_not_bind_to_outer_alias`; `Module_alias_collision_uses_table_name`; MSTVF `Table-valued function` + `Table Scan` ↔ invocation; non-inlined scalar UDF call ↔ Compute Scalar containing `UserDefinedFunction`; `Eliminated_branch_has_no_link`; seek + Key Lookup both link to one reference.
- [x] FAIL → implement → PASS. Focused RED/GREEN evidence and conservative mapping decisions are recorded in the [Task 4 report](088-evidence/task-4-report.md). Commit `feat(088): map plan operators to SQL spans`.
- [x] Review-fix round 1: visit expanded FROM sources before SELECT-list subqueries to preserve Showplan TRI order; constrain module inference to an established ScriptDom scope and return ambiguous/none when scope cannot be established; assert distinct CTE invocation/body, view/outer table, and inline-TVF/APPLY spans. Focused evidence is in the [Task 4 report](088-evidence/task-4-report.md). Commit `feat(088): correct conservative source mapping`.
- [x] Review-fix round 2: remove graph-distance scope inference because plan topology cannot establish SQL lexical scope; infer a possible module only when the statement has one ScriptDom query scope, otherwise preserve ambiguity. Add a regression where the nearest directly matched graph neighbor is in the outer query while the module candidates span nested scopes. Focused evidence is in the [Task 4 report](088-evidence/task-4-report.md). Commit `feat(088): restrict module scope inference`.

### Task 5: Dashboard API and capabilities
**Files:** `src/SqlHarness.Dashboard/DashboardServer.cs` (replace `/api/plans/{hash}`: `GET /api/operations/{id}/plans` manifest of variant/case/repetition/statement/span/document/shape/match kind; `GET /api/plan-documents/{sha}/graph`; `GET /api/plan-documents/{sha}/nodes/{nodeId}/properties`; `GET /api/operations/{id}/source-map?variant&parameterSet&repetition`; raw `.sqlplan` download), `JournalReader.cs`, `DashboardModels.cs`, `src/SqlHarness.Core/Capabilities.cs` (~`:195` plan storage state); tests in dashboard server suites.
- [x] Failing tests: manifest per repetition; graph and properties bounded; source map returns exact executed text and document-scoped links; runtime `storeSensitive` off hides previously stored SQL/plans/occurrences; invalid SHA → 400; missing → 404; complete manifest/source-map responses → explicit 413 when over their byte bounds.
- [x] FAIL → implement → PASS. Focused RED/GREEN evidence, response contracts and bounds, and reader/config semantics are in the [Task 5 report](088-evidence/task-5-report.md). Commit `feat(088): plan graph and source-map API`.

### Task 6: Plan viewer with two-way linking
**Files:** UI `src/SqlHarness.Dashboard/ui/src/api/types.ts`, `queries.ts`; new `components/plan/PlanGraphView.tsx` (renderer from Task 0, lazy chunk, own layout, icons, cost %, estimated vs actual rows, warning badge, edge width by `log(rows)`, collapse subtrees, fit to selection), `PlanPropertiesPanel.tsx`, `PlanStatementSwitcher.tsx`, `PlanSourceView.tsx` (Monaco from `components/sql-editor/monaco.tsx` / `SqlEditors.tsx`, decorations, reveal, selection events), `PlanWorkbench.tsx` (shared selection state); replace `PlanTree` use in `VariantPanel.tsx`; theme tokens for dark mode; tests next to components.
- [x] Failing tests: click node → its spans decorated and revealed; select SQL → overlapping links' nodes highlighted and fitted; statement switch keeps the batch view; `exact`/`inferred`/`ambiguous`/`none` styles and rule tooltip; keyboard selection; `plans not stored` and `unmatched` states; dark theme. Direct Monaco lifecycle coverage verifies exact source ranges, callback replacement, and listener cleanup.
- [x] FAIL → implement → PASS (`npm --prefix src/SqlHarness.Dashboard/ui test`, `ui-check`). See the [Task 6 report](088-evidence/task-6-report.md). Commit `feat(088): graphical plan viewer with SQL linking`.
- [x] Scoped review-fix round 1: separate document canvas regions, resolve statement ownership through graph metadata, label status per visible document, add React Flow edge handles and pointer support. RED/GREEN and browser evidence are in the [Task 6 report](088-evidence/task-6-report.md); Sol low scoped re-review approved.

### Task 7: Documentation and verification
- [x] README describes the graphical plan viewer, `journal.storeSensitive` gate, and the statement/table-reference linking scope and limits; MCP runtime docs describe the capabilities storage state.
- [x] Remove the unused `PlanTree` component and test; retain `DistilledPlan` because the Plan changes comparison still uses it.
- [x] Update the plan index/history and retain permanent evidence in [plans/088-verification-evidence.md](088-verification-evidence.md).
- [x] Run the final Windows `just verify` gate; exact result and boundaries are recorded in the evidence file.
- [x] Commit `docs(088): record plan 088 evidence`.

## Follow-up (separate plan)

Inference rules for object-less operators and expression lineage; module definition fetch and expansion; estimated plans; `query` plan capture; side-by-side plan comparison; PostgreSQL `PlanGraph`.
