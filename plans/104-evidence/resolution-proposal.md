# Plan 104 blocker resolution proposal

Date: 2026-10-24
Checkout: `D:\Dev\sqlharness\.worktrees\plan-104`

## Decision

Resolve the preflight as an **ambiguity fallback**. Do not classify an IO row as a table variable from a bare `#` plus eight hexadecimal characters. The supplied sources do not establish that STATISTICS IO always reports local temporary tables with SQL Server's internal unique suffix, so suffix presence cannot currently serve as an exhaustive positive discriminator. Retain exact ambiguous identities for benchmark distributions and mark them unclassified; only aggregate rows carrying explicit typed provenance. This preserves reads and avoids merging an explicit `#ABCDEF12` into an aggregate.

This is a defensible resolution of the STOP condition, but it narrows the plan's desired median repair: under current capture evidence, generated bare-name rows remain per-run identities and their cross-run per-name distribution can still be zero. A separate, reviewed capture/provenance change would be required to positively classify these rows before aggregation.

## Evidence and boundary

The parser currently calls `TempTableKey.Normalize` before constructing both dictionary keys and `TableIoCounters`. That helper strips an internal-looking `__<12 hex>` suffix. The raw spelling is therefore irretrievably lost before benchmark and journal aggregation. `BenchmarkReports.CreateVariantReport` computes per-name distributions from `LogicalReadsByTable`; `JournalBenchmarkBuilder.NormalizeName` maps every exact `#<8 hex>` match to `table variables`, without type evidence.

Microsoft's `CREATE TABLE` documentation says that when a local temp table is created in a concurrently executable stored procedure/module, SQL Server appends a unique suffix to distinguish session instances and that the full `tempdb.sys.objects` name contains it. This documents catalog naming for that stated condition; it does **not** promise that STATISTICS IO output always carries that full name, or establish a relation between an IO row and a plan object. The `SET STATISTICS IO` documentation describes the table output as a table name but gives no temporary-object naming or kind guarantee. Table variables also reside in tempdb, so the database location alone does not establish kind.

Sources: [CREATE TABLE (Transact-SQL), Temporary tables](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-table-transact-sql?view=sql-server-ver17#temporary-tables); [SET STATISTICS IO (Transact-SQL)](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-statistics-io-transact-sql?view=sql-server-ver16#output); [tempdb database](https://learn.microsoft.com/en-us/sql/relational-databases/databases/tempdb-database?view=sql-server-ver16).

The contract should distinguish: (a) positive type provenance, safe to aggregate; (b) ambiguous local-object observation, retain identity and do not aggregate; and (c) known permanent object, preserve exact identity. Unknown must never become a table-variable label or zero-read evidence.

## Bounded source changes for a follow-up implementation plan

1. In `Diagnostics.cs`, retain the parser-reported table spelling alongside any display/session-normalized name before calling `TempTableKey.Normalize`. Introduce an internal object-kind/provenance field on `TableIoCounters` with a compatibility default of `Unknown`; do not infer `TableVariable` from name shape. Preserve `StatisticsIo.LogicalReads` totals and LOB addition exactly.
2. In `BenchmarkRunner.cs`, carry typed identity through the per-run aggregation and distribution path. Aggregate counters by a collision-safe typed key only for `TableVariable` evidence. For `Unknown` `#...` rows, use the raw reported identity per run; do not make up zero or a common bucket. Keep existing stable permanent keys and totals unchanged.
3. In `JournalBenchmarkBuilder.cs`, remove `TableVariableName` shape-based rewriting. Aggregate only `TableVariable`-typed counters, using an explicit aggregate identity that cannot collide with a real object. Preserve unknown local-object keys as reported. Legacy artifacts lacking the new field are `Unknown`, and their names remain readable without reinterpretation.
4. Carry enough typed/raw provenance through `Artifacts.cs` and saved benchmark metrics for downstream consumers to apply the same rule. Existing saved artifacts remain historical and are not retroactively reclassified. Keep serialization additive/optional so old artifacts deserialize; if adding a public-facing field changes contracts, update the affected interface surfaces as required by `AGENTS.md`.
5. Do not infer type by reconciling sums against a plan's object totals or by parsing SQL text for a matching declaration: current data has no direct mapping from a particular STATISTICS IO counter row to a plan object, and duplicate/indirect references make guessed joins unsound.

Whether a future capture layer can emit positive `TableVariable` provenance is explicitly unresolved. It needs a separate source-backed investigation (for example, a documented diagnostic event or a trustworthy per-counter association), plus SQL Server-version coverage before enabling automatic classification. A raw suffix may be recorded as evidence for that investigation; it must not be treated as proof that all unsuffixed `#<8 hex>` rows are variables.

## Revised task assertions

- **Task 1: classify and retain ambiguity.** Add paired fixtures with identical reported name `#ABCDEF12`: one `TableVariable` typed row and one `Unknown` explicit/ambiguous row. Across repetitions, only the typed rows combine into the aggregate. Unknown rows retain their per-run exact identity and never appear as `table variables`. Include a raw suffixed temp name, stable permanent name, absent permanent object, multiple typed variables in one run, escaped/long keys, and detailed LOB counters. Assert total logical reads including LOB stay invariant. Do not assert that a real SQL Server emits either form for every temp table.
- **Task 2: unify safe aggregation.** Use one typed identity/grouping helper in benchmark and journal paths. Test typed aggregation, unknown retention, a real name colliding with the aggregate display label, legacy artifact deserialization as `Unknown`, and unchanged historical exact names. Remove all positive-classification assertions based only on regex shape.
- **Task 3: consumers and compatibility.** Verify measure, named-set measure, compare, artifacts, output projection, clipping, legacy reading, and totals preserve type/raw provenance. Assert ambiguous identities are not relabeled or merged and missing entries are not converted into a shared aggregate. Update contract descriptions only if an emitted field or user-visible meaning changes.

The plan's success statement must be amended: it can guarantee safe aggregation of positively typed counters and explicit retention of ambiguity. It cannot claim that currently ambiguous generated table-variable IO is repaired until a supported positive provenance source is implemented and validated. Do not mark this deferred classification work as complete.

## Tradeoffs and rejected options

- Keeping the current regex is rejected: it merges an explicit user-created `#ABCDEF12` with engine-generated table-variable observations.
- Treating “has `__<12 hex>`” as a complete inverse classifier is rejected absent documentation that STATISTICS IO always emits the suffixed catalog name. The current code's ability to strip that suffix shows it has seen the shape, not that the shape is exhaustive or an IO-kind contract.
- SQL/plan text matching is rejected for this bounded change because available captured artifacts do not link an IO counter row to an individual plan object; plan totals only support reconciliation, not identity.
- Assigning a shared `ambiguous local objects` bucket is rejected because it would merge unrelated explicit temp tables and destroy readable identities. Ambiguity means retaining exact observations, not grouping them.
