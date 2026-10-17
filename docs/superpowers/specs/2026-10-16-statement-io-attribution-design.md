# Per-statement IO attribution (stage C) — design

Date: 2026-10-16. Plan: 082 (to be written from this design). Depends on plans 079 (journal v-next, message
numbers, streaming IO), 080 (comparison panel) and 081 (loops/transactions admitted). Origin: consilium rounds 1–2,
UAT spikes of 2026-10-16 (two rounds), owner's decision that the default IO view shows changed statements with the
whole-batch total always visible.

## Intent

A measured batch often contains statements that are not the subject of the optimization: data preparation
(`INSERT INTO @ids SELECT TOP (@n) …`), staging into `#temp`, DDL on temps. Their reads are identical on both sides
and drown the real change (op 203: 11,910 reads per side, unchanged, while the rewritten statement changed its plan).
Stage C attributes logical reads and scans to individual statements and objects, recognizes statements that are the
same on both sides, and lets the comparison focus on what changed — without ever hiding the batch total.

## Evidence from the spikes (SQL Server, Azure SQL, UAT)

- Actual plans (`SET STATISTICS XML`) carry per-operator `RunTimeCountersPerThread` with `ActualLogicalReads` and
  `ActualScans`. Summed over all threads and all operators of an object within a statement, then over statements,
  they matched `STATISTICS IO` **exactly** per object, including table variables, `#temp`, real tables and a DOP-2
  parallel scan (sum of per-thread reads; scans = sum of per-thread scans = STATISTICS IO scan count).
- Plans name objects stably (`[@P]`, `[#F]`, `[Partners]`), unlike `STATISTICS IO`'s per-execution `#XXXXXXXX` names.
- Plans exist only for DML/SELECT statements; `DECLARE`, `CREATE TABLE`, `DROP` have none.
- Scalar UDF reads appear in neither STATISTICS IO nor plan counters; the plan has `<UserDefinedFunction>` and
  `QueryTimeStats UdfElapsedTime/UdfCpuTime` (e.g. 28 of 29 ms). Multi-statement TVF internals are invisible; the plan
  shows a `TableValuedFunction` operator plus a scan of its return table with its executions.
- Not yet observed (needs a spike once plan 081 admits them): `WHILE` loops (plan documents per iteration or per
  statement?), cursors, spools/worktables with non-zero reads, `CREATE INDEX` on `#temp` inside the batch.

## Goals

1. **Plan-sourced attribution.** For each measured run, per statement occurrence: object → logical reads, scans
   (sum over threads and operators), plus statement CPU/elapsed (existing) and UDF time. Store in the journal per run
   and aggregate across runs by median.
2. **Reconciliation.** Per object and in total: `attributed` (from plans) vs `observed` (STATISTICS IO, by message
   number). Difference shown as an `unattributed` row. Each statement's attribution has a status: `confirmed`
   (reconciles), `partial` (plan missing or counters absent for some operators), `unavailable` (no plans).
3. **Statement identity.** Key = hash of normalized statement text (comments stripped, whitespace collapsed, case of
   keywords not changed) + occurrence index among equal keys + execution count (loops). Ordinal is display order only.
4. **Shared statements.** A statement whose key exists on both sides **and** whose reads are the same by the stage B
   rule (not ≥ 1% and ≥ 10 pages apart) is `shared` and collapsed; same key with different reads is
   `same text, different work` and stays visible; keys on one side only are `only in baseline` / `only in candidate`.
5. **Default view (owner's decision).** The comparison panel's per-table table defaults to **changed statements**
   (everything except `shared`), with a visible line `whole batch: X → Y reads (shared statements: Z reads)` and a
   toggle `Changed statements | Whole batch | By statement`. Markdown copy follows the selected view and states it.
6. **By-statement view.** Rows = statements (changed first, sorted by absolute reads change), each with short
   statement text, reads/scans before → after, execution count, status, plan link for that statement; expandable to
   its objects.
7. **Hidden work flags.** Scalar UDF: `UDF time 97% of statement` (from `UdfElapsedTime`); MSTVF:
   `multi-statement function: internal reads not visible (N executions)`. Shown on the statement and next to the
   reads headline.
8. **Limits.** No silent truncation: raise the 128-statement extraction cap and bound by bytes instead, recording
   `omitted statements: N (their reads remain in the batch total)`.

## Non-goals

- `EXEC`/procedure frames (EXEC stays denied, plan 081).
- PostgreSQL (shows `per-statement IO not available for this engine`).
- MCP/artifact exposure of per-statement IO (agent path; coordinate with plan 077 later).
- Choosing or rewriting statements automatically.

## Anti-goals

- A focused (changed-statements) number without the whole-batch total next to it.
- Calling a statement `shared` when its work differs.
- Reporting attributed numbers as confirmed when they do not reconcile.

## Constraints

- Journal-only storage extension (plan 079's version or the next free); breaking changes acceptable as in 079.
- Same better/worse rule and labels as stage B; same Markdown/screen parity.
- Plan XML is already captured per measured run (artifacts); journal stores only the extracted counters.

## Calls settled after the builder check

1. **Verdict basis (owner's decision, option a).** When attribution is `confirmed` on both sides of a case, the case
   outcome (better/worse/same), Mixed, worst case, the version-table outcome and the sessions-list Optimization
   column use **changed statements** (everything except `shared`) with the stage B thresholds; otherwise they use the
   whole batch. Every verdict is labelled with its basis (`changed statements` / `whole batch`), and the whole-batch
   numbers stay visible next to it. This amends plan 080's comparer.
2. **Statement status by object reconciliation (my call).** STATISTICS IO is per object for the whole batch; a
   statement is `confirmed` when every object it touches reconciles (plan sum = observed, per object, in that run);
   otherwise `partial`; `unavailable` without plans. No message-to-statement ordering is attempted. "By message
   number" means the engine statistics filter from plan 079 (IO 3615), not a per-statement sequence.
3. **`shared` is per case; `All cases` collapses only statements shared in every case** (my call, fail closed);
   statements present in only some cases (IF branches) are never collapsed and show `not executed in this case`.
4. **`shared` requires equal work, not only equal reads** (my call): same key on both sides, reads within the stage B
   margin, equal scans, and the same plan hash for that statement; otherwise `same text, different work`.
5. **Temp objects.** The by-statement view and per-table changed view use plan names (`@P`, `#F`) when the statement
   is `confirmed`; reconciliation for table variables happens at group level (sum of plan table-variable objects vs
   stage A's `table variables` group); the whole-batch view keeps stage A's grouping.
6. **Fail closed on partial data** (my call): if either side of a case is `partial` or `unavailable`, nothing is
   collapsed, the default view falls back to `Whole batch` with the note `per-statement attribution incomplete`, and
   the verdict basis is whole batch.
7. **Additive numbers** (my call): per-statement and per-object breakdowns come from the run whose batch total is
   the median (lower median for even counts), so changed + shared = whole batch exactly in the headline line
   `whole batch: X → Y reads (shared statements: Z)`; run ranges remain available in per-run details.
8. **Measure operations** also get the by-statement view, hidden-work flags and reconciliation on their operation
   page (no comparison).

## Left to the builder

- Spike after plan 081 lands: loop, cursor, spool/worktable with reads, `CREATE INDEX` on `#temp`; adjust identity
  (execution counts vs repeated plan documents) and reconciliation from what SQL Server actually emits.
- Normalization details and text-hash migration of `operation_statements`.
- Byte bound for statement extraction.
