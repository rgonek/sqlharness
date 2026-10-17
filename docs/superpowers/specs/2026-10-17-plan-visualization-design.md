# SQL Server plan visualization with SQL ↔ operator linking — design

Date: 2026-10-17. Plan: 088. Depends on plans 080 (Optimization dashboard) and 087 (journal schema step). Decided in a
consilium (Opus + Codex consultants, orchestrator spikes on BSI UAT); spike data stayed outside the repository.

## Intent

The dashboard shows SQL Server plans only as an indented operator table (`PlanTree.tsx`). For complex batches
(multi-statement, CTEs, APPLY, views) users cannot quickly find which part of the plan belongs to which part of the
query. Goal: an SSMS-like graphical plan where selecting SQL highlights operators and clicking an operator highlights
SQL. SQL Server first; the model must not block PostgreSQL later. No backward compatibility.

## Evidence (UAT spikes, 2026-10-17)

1. **Full actual plans are captured, but the journal copy is unreliable.** measure/compare collect `STATISTICS XML`
   per statement; the journal keeps raw gzip only with `journal.storeSensitive` (default false). Documents are keyed by
   `PlanIdentity.Hash` (plan *shape*, `QueryPlanHash`) with `INSERT OR IGNORE`: the first run's actuals are kept forever,
   and two statements with equal shape collide (GetCardsWithStatuses batch: StatementId 4 and 5 share
   `QueryPlanHash 0x026A85B9E1A796E3`, different text).
2. **No operator source positions exist in Showplan.** Anchors: `Object` (table, index, `Alias`,
   `TableReferenceId`), `ColumnReference` aliases, `ScalarString`, `DefinedValues`, `NodeId`.
3. **`StatementText` is cut at 3999 characters by SQL Server** (also in the server's cached plan;
   `sys.dm_exec_query_stats` offsets show 4162 / 5846 characters). SQLHarness reads XML whole. Under Parameter
   Sensitive Plan optimization the server appends ` option (PLAN PER VALUE(...))` and a `<Dispatcher>` element, so
   `StatementText` is not always a substring of the submitted text.
4. **Statement pairing works from the submitted text.** 19/19 statements of a 22 KB batch paired to ScriptDom statement
   starts; `StatementCompId` is not a usable ordinal (CREATE TABLE consumes one, DECLARE does not).
5. **`TableReferenceId` (TRI)** appears only when one base object is referenced more than once in a statement and
   numbers instances in FROM order after CTE and view expansion (not text order); `-1` marks a Key Lookup belonging to
   its sibling seek. A CTE referenced twice exposes the CTE-internal alias with TRI 1/2, not the outer aliases.
   Undocumented: treated as disambiguation evidence, never as a source offset.
6. **Modules:** inline TVF and non-indexed view operators carry the module's internal aliases (which can collide with
   query aliases, e.g. `CI`); the invocation alias never appears. MSTVF shows `Table-valued function` + `Table Scan`
   with the invocation alias. Non-inlined scalar UDF: `UserDefinedFunction` inside a Compute Scalar, no body plan.
   Inlined scalar UDF disappears (`ContainsInlineScalarTsqlUdfs="1"`). Branches removed at compile
   (`OPTION (RECOMPILE)` with NULL parameters) have no operators.
7. **Sizes:** largest per-statement plan in wind/cz Query Store (120 days) has 95 operators; the 19-statement batch
   has 206 operators, 887 KB raw / 75 KB gzip. Other databases not surveyed (authentication failure).
8. **Estimated plans are unavailable:** `SET SHOWPLAN_XML` is rejected by the safety classifier and must be alone in
   its batch.
9. **Libraries:** `html-query-plan` (XSLT, no selection API), `azdataGraph` (mxGraph, not on npm; vscode-mssql moved to
   React Flow), PerformanceStudio (Avalonia/Blazor), AntV G6 (7.6 MB, canvas) do not fit. Candidates: React-rendered
   SVG + `d3-zoom` with our own layout, or `@xyflow/react`. vscode-mssql's React Flow plan viewer is a reference
   design (icon license to be checked).

## Decisions

1. **Plan documents are content-addressed.** Key = SHA-256 of the raw UTF-8 document; the shape hash stays a separate
   column for plan-change detection. Every repetition links to its own document. A per-operation byte budget records
   explicit `omitted` occurrences instead of dropping silently.
2. **Statement occurrences** record, per document statement: executed-text span, `match_kind`
   (`exact`, `psp_suffix`, `normalized`, `prefix`, `ambiguous`, `unmatched`) and server ids. Pairing happens at write
   time from the executed text (after SQLHarness transformations) with ScriptDom: sequential search, anchored at
   ScriptDom statement starts, end from the AST. DMV offsets are not used.
3. **One gate** (`journal.storeSensitive`) covers SQL text, plans and occurrences. Without it the dashboard and
   capabilities state `plans not stored`.
4. **Server-side `PlanGraph`.** .NET parses Showplan into nodes (own object/predicate/properties, actual vs estimated,
   per-thread counters summed, warnings, TRI), edges (rows, size) and a lazily served property tree. The UI never parses
   XML. Bounds: existing parse limits; graph responses are size-bounded instead of clipping predicates.
5. **Source map computed on read** from executed text + document, versioned by algorithm. MVP links: statement level
   and table references. Table-reference match is `exact` only when exactly one reference instance fits after CTE
   expansion or TRI settles it; plan objects absent from the text attach to the single unresolved module reference in
   scope as `inferred via <module>`, else `ambiguous`; text with no surviving operator is styled `no operators`.
6. **Renderer chosen by measurement** in plan Task 0: React SVG + `d3-zoom` (own SSMS-like right-to-left tree layout)
   vs `@xyflow/react`, on a 95-node fixture and synthetic 250/1000-node trees: compressed bundle increment, first
   render, selection latency, pan/zoom. `PlanGraph` stays renderer-independent; D3 never owns the DOM.
7. **SQL side uses the lazy Monaco viewer** on the exact executed text (decorations, reveal, selection events), not
   `SqlBlock` (which formats and would shift offsets).
8. **Confidence in the UI:** exact = solid, inferred = dashed/lighter, ambiguous = list of candidates, no operators =
   neutral; tooltip names the rule (for example `alias pc, TableReferenceId 2 → CTE futureStatusChangesCount`).

## Non-goals (follow-up plan)

Inference rules for object-less operators (joins, filters, windows, aggregates, sorts, optimizer-introduced
operators), expression lineage, module definition fetching and expansion (`OBJECT_DEFINITION`, cache by
`(database, object_id, modify_date)`), estimated plans, `query` plan capture, plan comparison side by side, PostgreSQL
`PlanGraph`. Regex or JS T-SQL parsing, XSLT/mxGraph rendering and DMV-based offsets are rejected.
