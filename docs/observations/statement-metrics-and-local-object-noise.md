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

## Follow-up 2026-10-20

Status: collecting observations, not approved for implementation. Recorded against repository HEAD
`f928da0d`.

Observed in an optimization session (unmapped system label) on a static four-statement read batch.
One `measure` call with three parameter sets returned per-set batch totals for CPU, elapsed time and
logical reads, plus noteworthy operators, but no per-table reads. One artifact `statements` read gave
per-statement operators and executions, again without reads. The agent attributed the batch reads to
tables by multiplying operator executions by index depth (from a separate catalog `query`) and
reported the split and the share of work as if measured. The saved artifact `metrics` section carries
measured per-table reads, but the agent did not read it; nothing in the `measure` response points there.
The attribution was corrected in the retro.

Verified source:

- `src/SqlHarness.Core/AgentBenchmarkOutput.cs:67-83` (`AgentMeasureSetItem`) and `:238-260` (`Set`)
  project CPU, elapsed and total logical reads per parameter set; there is no per-table field.
- `src/SqlHarness.Core/ArtifactReader.cs:43-53` (`ArtifactNamedMetrics`) carries
  `LogicalReadsByTable` for the `metrics` section, so the evidence existed in the saved artifact.

Classification: ux gap (discoverability for parameter-set measurements) plus agent slip (the
`metrics` section was available). Confidence: high for the missing response field; whether a pointer or
the field itself would have prevented the inference is a hypothesis. Token cost was not measured.

Questions for later synthesis:

- Should the parameter-set `measure` response carry per-table reads like single measurements, or name
  the artifact section that holds them?
- Across sessions, how often is a read split inferred from operator counts instead of read from saved
  evidence? This is one occurrence.

Cheapest next check: project one existing parameter-set artifact through the agent output path and
compare its fields with the `metrics` section of the same artifact.

## Follow-up 2026-10-20: hidden work and attribution status on the agent path

Status: collecting observations, not approved for implementation. Recorded against repository HEAD
`a063e75c`.

Observed in an optimization session (unmapped system label) on a static multi-statement read batch
whose dominant statement joins a multi-statement table-valued function. One `measure` (four parameter
sets) and one `compare` (four sets) succeeded. Their responses listed the function operator among
noteworthy operators and an implicit-conversion warning, but nothing said that the function's internal
work was missing from the reported logical reads. The batch showed low reads with comparatively high
CPU. The agent told the user that baseline reads were "probably understated" as an unlabeled hypothesis.
During the retro, a bounded read-only journal check showed that SQLHarness had already recorded this:
for the dominant statement, every measured set and every baseline case has `hidden_work_json` of kind
`multi-statement-tvf`, and all journaled statements of both operations have attribution status
`partial`. The dashboard renders this as `multi-statement function: internal reads not visible`; the
MCP responses did not carry it.

To find the dominant statement the agent also read the artifact `statements` section once. That one
response was the largest of the investigation, slightly over half of the emitted bytes of all journaled
investigation calls. It returned top operators for every statement of the first sets and omitted the
remaining statements behind a continuation, which the agent did not follow. The decision needed only
per-statement CPU/elapsed across sets. As a result, one later claim ("the other statements cost about
zero") covered two of the four sets; it was corrected in the retro.

Verified source:

- `src/SqlHarness.Core/StatementMetrics.cs:350-358` builds `HiddenWork` entries (`scalar-udf`,
  `multi-statement-tvf`); `src/SqlHarness.Core/Journal/ActivityJournal.cs:561-588` stores them.
- `src/SqlHarness.Core/ArtifactReader.cs:103-112` (`ArtifactStatementMetric`) has CPU, elapsed, DOP and
  top operators only: no hidden work, attribution status or reads.
- Dashboard only: `src/SqlHarness.Dashboard/ui/src/components/optimization/HiddenWorkFlag.tsx`.

Classification: ux gap (agent-path parity with evidence the tool already computes), plus agent slip for
the unfollowed continuation. Confidence: high for the missing fields and the journal records; whether a
flag in the `measure`/`compare` response would have replaced the hypothesis and the large artifact read
is a hypothesis. Recurrence: first observation of hidden work on the agent path; the 2026-10-16 spike
above saw the same invisibility at engine level.

Existing coverage: plan 082 (journal/dashboard only, DONE); plan 095 (artifact page budgets, TODO) and
plan 077 (lean JSON) address response size, not these fields.

Questions for later synthesis:

- Should `measure`/`compare` responses carry a compact hidden-work flag and attribution status per
  statement, or only when the batch total is `partial`?
- Is a per-statement CPU/elapsed summary across sets (without operators) a recurring need in
  multi-statement batches, compared with the current `statements` page?

Cheapest next check: render the `statements` section and the agent `measure` output for one synthetic
artifact with a multi-statement TVF fixture (plan 082 fixtures) and compare which fields reach the agent.
