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
