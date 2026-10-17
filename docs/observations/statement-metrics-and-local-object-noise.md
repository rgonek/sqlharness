# Observation: per-statement reads missing and local-object noise in table IO

Status: collecting observations, not approved for implementation. Recorded 2026-10-15
against repository HEAD `4f1e5a1`. No implementation plan is requested.

## Observed workflow

An optimization measured one statement inside a batch that first filled three batch-local table
variables (substitutes for table-valued parameters, see
[table-valued parameters](table-valued-parameter-substitution.md)). Four `compare` calls returned
batch totals in which the fill statements were included. In two of the four comparisons, totals were
identical between variants. Deciding whether the statement under test changed required a `statements`
artifact read. That read gave per-statement CPU, elapsed time and top operators, but no logical reads.
The agent separated the work through `logicalReadsByTable` (fill and test touched partly different
tables) and plan node reads. That is manual arithmetic and does not work when they share tables.

Each `compare` case listed table IO for every temporary object instance. Table variables get a new
`#XXXXXXXX` name per repetition, so each case carried about two dozen zero-or-small entries per
variant. In the journal, about 80% of `operation_table_io` rows for these operations were such
names. Emitted bytes per `compare` call were in the low tens of kilobytes; the share attributable
to this noise was not measured.

## Verified source

- `src/SqlHarness.Core/ArtifactReader.cs:89-98` (`ArtifactStatementMetric`) carries CPU, elapsed time,
  DOP and top operators; it has no logical-read field.
- The journal schema has `operation_statements` without reads and `operation_table_io` per metric.
  Per-statement IO is therefore not stored anywhere that `artifact` can read back.
- Grant and compilation values were present in `operation_metrics` (batch level) for these operations
  but are not exposed through `artifact`. This is a follow-up to
  [plan memory and compilation evidence](plan-memory-and-compilation-evidence.md#follow-up-2026-10-15).

Classification: ux gap. Confidence: high for the missing field and the noise; the effect on token
cost is unmeasured.

## Questions for later synthesis

- Is per-statement logical read evidence needed in other sessions with multi-statement batches?
  SQL Server reports `STATISTICS IO` per statement, so collection may already be possible.
- Should table IO aggregate local temporary objects (for example one `#local` bucket per variant),
  or should the instance-unique names be kept for provenance?
- Should a benchmark be able to mark statements as setup-in-batch and exclude them from totals?

Cheapest next checks: a synthetic two-statement batch through the SQL Server diagnostics parser, to
see whether per-statement IO is already split before aggregation; and a projection test on a fixture
with many `#` table entries to measure the emitted-byte share. No live database is needed.

## Follow-up 2026-10-16 (HEAD `566628e`)

Status: collecting observations. Journal and dashboard coverage is now planned in
[plan 082](../../plans/082-statement-io-attribution.md) (with table-variable grouping in
[plan 079](../../plans/079-optimization-evidence-core.md)); the agent path (MCP output and `artifact`)
is not covered by any plan.

Observed in a read-only spike against a SQL Server test database (System1 profile; generic batches only:
two table variables, one `#temp`, reads from three permanent tables; a scalar UDF and a multi-statement
TVF; one forced parallel scan):

- Actual plans name local objects stably (`[@P]`, `[#F]`), unlike `STATISTICS IO` (`#XXXXXXXX`, new per
  execution). With `repeat = 2`, the per-name median of a table variable came out halfway between zero and
  its real reads; with three or more repetitions it would be zero.
- Summing `RunTimeCountersPerThread/@ActualLogicalReads` per object over operators and threads, then over
  statements, matched `STATISTICS IO` per object exactly in every observed case, including the parallel
  scan; summed `@ActualScans` matched the journaled scan count.
- Plans exist only for DML/SELECT statements (none for `DECLARE`, `CREATE TABLE`, `DROP`).
- Scalar UDF reads appear in neither `STATISTICS IO` nor plan counters; the plan carries
  `<UserDefinedFunction>` and `QueryTimeStats/@UdfElapsedTime`/`@UdfCpuTime`. Multi-statement TVF
  internals are invisible apart from the function operator and the scan of its return table.
- The `artifact` `plan` section returns distilled nodes without these runtime counters; confirming the
  match required reading the raw saved `.sqlplan` locally (a manual step outside the tool contract).

Verified source: `src/SqlHarness.Core/ArtifactReader.cs` (plan section projection),
`src/SqlHarness.Core/StatementMetrics.cs` (extraction caps, no reads).

Questions for later synthesis: should the agent path expose per-operator or per-statement actual reads
(artifact `plan`/`statements` sections) once 082 extracts them, and within which output budget?
Confidence: high for the observed match on these shapes; loops, cursors, spools and procedures were not
measurable (rejected by the classifier at this HEAD).
