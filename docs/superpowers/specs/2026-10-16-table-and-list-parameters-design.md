# Table-valued and list parameters (stage D) — design

Date: 2026-10-16. Plan: 083 (to be written from this design). Depends on plans 077 (MCP schemas) and 079
(conditions model, reproduction scripts, case labels). Origin: consilium round 2 (five application repositories),
`docs/observations/table-valued-parameter-substitution.md`, owner's decisions.

## Intent

Application SQL passes lists as table-valued parameters of user-defined table types (TVPs in all five surveyed
repositories, often multi-column, sometimes empty) and as Dapper list expansions (`IN @Ids`). SQLHarness binds only
scalar parameters, so agents substitute `DECLARE @x TABLE` plus fill statements inside the measured batch. That changes
compilation (a TVP is sniffed with its row count; a batch-local table variable is not) and pollutes measured reads
with fill work (op 203). Stage D lets agents pass these inputs the way the application does.

## Owner's decisions

- TVP rows can be given **inline** or **from a query** (both forms).

## Goals

1. **`tvp` parameter kind**: `{ name, type: "<table type name>", rows: [[…], …] }` or
   `{ name, type, rowsFrom: "<read-only SELECT>" }` (exactly one of `rows` / `rowsFrom`). SQLHarness resolves the
   type from the catalog (`sys.table_types` + `sys.columns`; unqualified names through the session's default
   schema), stores the schema-qualified name, builds the rowset in column order with the type's column types and
   nullability, and binds it as `SqlDbType.Structured` with `TypeName` — the application's wire form. An empty rowset
   (`rows: []`) is a valid, distinct case.
2. **`list` parameter kind** (Dapper expansion): `{ name, type, values: […] }` or `{ name, type, valuesFrom: "<read-only
   SELECT>" }` (first column). In the SQL text, `IN @Name` (Dapper syntax, with or without parentheses) expands like
   Dapper: `IN (@Name1, @Name2, …, @NameN)`; an empty list expands to Dapper's empty form
   `IN (SELECT @Name WHERE 1 = 0)` with `@Name` bound as typed NULL. No list padding (Dapper default).
3. **Both kinds everywhere parameters exist**: fixed parameters, parameter sets, matrix cells (e.g. a small list vs a
   large list as cases), `measure`, `compare`, `query`, and setup references.
4. **`rowsFrom` / `valuesFrom` execution**: per case, on the case's connection, after connect and before setup; the
   query must pass the read-only classifier; it is not measured; its result is materialized once and bound
   identically for baseline and candidate (same data on both sides). Result columns must match the type by count and
   position with compatible types, else a validation error naming the mismatch.
5. **Conditions model and fingerprint (plan 079)**: new parameter kinds `tvp` / `list` with `effectiveType`
   (`dbo.IdList READONLY`, `list of int`), row/value count, column list, and a content hash; values/rows and the
   `rowsFrom` text are stored only with `storeSensitive`; the fingerprint includes type, count and content hash. Case
   labels: `@Ids = 500 rows (dbo.IdList)`, `@Types IN (1, 2, 3)` (long lists: `@Types IN (… 250 values)`).
6. **Version identity**: SQL versions hash the **unexpanded** text (as written with `IN @Name`), so list length is a
   condition, not a new version; the expanded text is what executes and what scripts reproduce.
7. **Reproduction scripts (plan 079)**: TVP → `DECLARE @Ids dbo.IdList; INSERT INTO @Ids (cols) VALUES …;` then
   `EXEC sys.sp_executesql N'…', N'@Ids dbo.IdList READONLY, …', @Ids = @Ids, …`; with only `rowsFrom` stored →
   `INSERT INTO @Ids (cols) <rowsFrom query>` labelled `rows re-queried; data may have changed`; without stored rows
   → placeholder and `Incomplete — values not stored`. Lists → the expanded parameter list. Large inline rowsets in
   scripts are emitted in batches of 1,000 rows per `INSERT … VALUES`.

## Non-goals

- `DECLARE @x <named table type>` inside measured SQL (stays denied; TVPs come as parameters).
- PostgreSQL arrays/composite types.
- Dapper literal replacements (`{=Value}`), `InListStringSplitCount`, list padding.
- Bulk-copy fixtures (`SqlBulkCopy` into `#temp`) — separate future idea.

## Anti-goals

- Binding a TVP with a shape different from its table type (silent column reordering or type coercion).
- `rowsFrom` executing anything but a classified read-only query, or being counted in measurements.
- Treating different list lengths as different SQL versions.

## Constraints

- Coordinate MCP schema changes with plan 077's envelope; tool descriptions own the argument contract; capabilities
  report limits. CLI parity through parameter-set files (`.sqljson`) and an equivalent CLI input (builder's choice
  of flag), per the repository's "MCP–CLI compatibility only where useful" rule.
- Limits: see "Calls settled", item 2; at most 8 TVP/list parameters per call.
- Journal storage of rows respects plan 079's conditions size bound and omission recording.

## Calls settled after the builder check (my calls)

1. **Tools.** `tvp`/`list` are accepted by `query`, `measure`, `compare`, setup, `watch` and `snapshot` (same
   read-only path). `mutation` rejects them explicitly with a validation error; tool descriptions and capabilities
   say so.
2. **Limits per case, summed over all `tvp`/`list` parameters:** at most 100,000 rows/values and 32 MiB
   materialized; rowsets are materialized per case and released after the case. Inline `rows`/`values` are bounded
   by the existing process request limit reported in capabilities. After list expansion a command may not exceed
   2,100 parameters (SQL Server's limit); exceeding it is a validation error naming the list.
3. **Dry run stays offline** (README contract unchanged). Dry run validates syntax, parameter kinds, inline row
   arity and classification on a representative expansion (one element per list), and reports
   `table type shape not verified offline` / `list length not known offline` where applicable. Type resolution,
   column matching and `rowsFrom`/`valuesFrom` execution happen after connect and before setup; their errors are
   validation errors of that case.

## Left to the builder

- Exact MCP/JSON schema shape and CLI flag; how `rows` encode NULL, dates, binary (reuse scalar literal rules).
- Catalog lookup caching per connection; error texts.
- Dapper expansion edge cases (parameter name prefixes, `IN@Ids` without space, repeated use of the same list),
  pinned by tests against Dapper's documented behavior.
