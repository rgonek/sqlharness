# Plan 104 resolution review

Date: 2026-10-24
Checkout: `D:\Dev\sqlharness\.worktrees\plan-104`
Scope: read-only review of `resolution-proposal.md`, Plan 104 Contract/STOP, and the current capture, benchmark, journal, and dashboard consumers. No product files or tests were changed.

## Verdict

**Approve the ambiguity fallback as the correct outcome of the current STOP; do not approve the proposal's typed-provenance implementation as written.** The proposal correctly rejects both the journal's `#`-shape classifier and the assumption that a suffix is an exhaustive discriminator. It does not, however, resolve the misleading benchmark output: it leaves unknown names in the zero-filled per-name distributions. Its `ObjectKind.Unknown`/`TableVariable` scaffold has no current capture producer that can ever set `TableVariable`; hand-constructed typed fixtures would test only downstream arithmetic, not source classification. The proposal also omits affected dashboard consumers that independently collapse `#8hex` names.

No implementation from the current capture inputs can satisfy Plan 104's original generated-variable median assertion while also preserving the explicit-name collision contract. Proceed only with a separately scoped ambiguity-reporting fallback, and keep positive table-variable aggregation blocked until a real provenance source is identified. Do not mark the original median repair complete under the fallback.

## Contract and evidence

Plan 104's Contract requires positively identified counters, forbids heuristic matching, and requires a documented ambiguity when generated names cannot be distinguished from explicit names. Its STOP condition therefore supports suppressing an ambiguous object distribution while preserving observed values and batch totals; it does not support labeling unknown counters as `table variables`.

The source confirms the blocker:

- `Diagnostics.cs` normalizes the STATISTICS IO table spelling before either parser returns it. The current `#XXXXXXXX` candidate is unchanged by that normalizer, so its exact spelling and per-run reads are already available in `LogicalReadsByTable`; the optional internal suffix stripped by `TempTableKey.Normalize` is a separate shape and does not identify object kind.
- `BenchmarkRunner.cs` builds each per-name distribution over the union of observed keys and inserts `0` when a key is absent. Retaining the raw `#XXXXXXXX` keys alone therefore still produces misleading zero medians.
- `JournalBenchmarkBuilder.cs` maps every matching name to `table variables`, then also fills absent per-run keys with zero. Removing only the classifier is insufficient: it would leave the same zero-fill under each raw name.
- `src/SqlHarness.Dashboard/ui/src/lib/tableIoDelta.ts` has a second `#8hex` fold to `table variables`; `prMarkdown.test.ts` currently asserts the grouped presentation. Changing only Core would leave exported/dashboard comparisons asserting the same unsafe meaning.
- Journal rows are persisted as per-table medians. `JournalTableIo.LogicalReads` and the SQLite `operation_table_io.logical_reads` column are non-nullable, and `JournalReader` reads that value as required. A truthful unavailable state needs an explicit variant status/count and a visible dashboard explanation, or a narrow data-model change; a missing row or numeric zero alone does not communicate ambiguity.

The cited Microsoft documentation supports the limitation: `CREATE TABLE` documents an internal suffix and full `tempdb.sys.objects` name for local temp tables in the stated concurrent stored-procedure/module case; it does not specify STATISTICS IO output. `SET STATISTICS IO` defines `Table` only as the table name. `tempdb` contains both temporary tables and table variables. These sources provide no positive per-counter type mapping. See [CREATE TABLE, Temporary tables](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-table-transact-sql?view=sql-server-ver17#temporary-tables), [SET STATISTICS IO, Output](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-statistics-io-transact-sql?view=sql-server-ver16#output), and [tempdb database](https://learn.microsoft.com/en-us/sql/relational-databases/databases/tempdb-database?view=sql-server-ver16).

## Viable fallback boundary

Create a separate implementation plan for **ambiguity-aware local-object IO reporting**. Keep its success statement limited to truthful retention and availability; keep the original cross-run table-variable aggregation explicitly blocked.

1. **Capture and totals:** retain the exact reported `#XXXXXXXX` spelling and its per-run reads. Do not add an object-kind field that has no producer. Preserve total batch logical reads and LOB addition unchanged. Existing readers must continue to accept old artifacts, whose names remain unknown evidence.
2. **Benchmark summaries:** keep exact ambiguous keys in per-run artifacts and `TotalLogicalReadsByTable`, but omit their cross-run entries from `LogicalReadsByTable`. Add a variant-level availability/warning and count of distinct ambiguous reported names, so omission is explicit. Keep whole-variant `LogicalReads` distributions and all stable-key behavior unchanged. Do not synthesize an aggregate row or zero-valued per-name distribution.
3. **Journal and consumers:** remove regex aggregation from `JournalBenchmarkBuilder`; omit ambiguous `#8hex` per-name median rows while retaining the variant's total reads and an explicit attribution status/count. Persist/read that metadata and display the unavailable/ambiguous status in the dashboard. Remove the independent `tableIoDelta.ts`/export folding behavior and replace tests that expect `table variables` with collision and status assertions. Keep exact per-run names/read values in the run artifacts; the journal's aggregate status must not imply that it retains per-run counter rows.
4. **Regression evidence:** cover an ambiguous generated-shaped name and an explicit `#ABCDEF12` with identical spelling, different `#8hex` names across repetitions, stable permanent names, absent permanent objects, multiple names in one run, LOB totals, legacy artifact reads, clipped/agent projections, journal persistence/readback, and dashboard/export text. Assert that totals are unchanged, ambiguous per-name distributions are absent and explicitly marked, and no consumer emits `table variables` from name shape alone.

The proposed “observed-only” distribution is not necessary for this fallback and would still require a sample-count/availability contract to stop readers treating a one-observation median as an object median. If chosen later, it must be labeled as an exact-name observation statistic, include observed-run count, and state that cross-run object attribution is unavailable.

## Remaining blocker and scope limit

The fallback does not meet the original Task 1 assertion that generated table variables with different names aggregate to median 100. That assertion remains unimplementable from current captured fields. The smallest next step for the original goal is a source-backed capture investigation that finds and validates a positive mapping from each STATISTICS IO counter row to an object kind (including SQL Server version behavior), then adds that provenance at the producer and carries it through artifacts. Plan totals, SQL text matching, suffix presence, and a hand-authored `TableVariable` fixture do not provide that mapping.

**Disposition:** accepted as a reviewed STOP resolution and as the basis for a separately scoped fallback; current proposal requires the revisions above before it is executable. Original Plan 104 remains blocked for generated-variable median repair.
