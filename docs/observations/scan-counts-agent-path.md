# Observation: per-table scan counts reach the journal but not the agent

Status: collecting observations, not approved for implementation. Recorded 2026-10-16 against
repository HEAD `566628e`.

## Observed workflow

During a read-only spike the agent needed per-table scan counts to compare them with plan
`ActualScans`. `measure` MCP output carries `logicalReadsByTable` only; the saved artifact
(`report.json`, `runs.jsonl`) has per-table logical reads but no scan counts. The value existed only in
the journal (`operation_table_io.scan_count`), which the agent read through the dashboard API — a path
the operator skill reserves for the operator. Reviewers in the owner's workflow routinely ask for scan
counts next to logical reads.

## Verified source

- `src/SqlHarness.Core/Diagnostics.cs` (`TableIoCounters` includes `ScanCount`).
- Journal: `operation_table_io` stores scan counts; MCP projection and artifact files do not expose them.
- Plan 079 changes journal/dashboard only and leaves MCP output and artifacts unchanged by design
  (coordination with plan 077).

Classification: ux gap. Confidence: high for the missing field on the agent path; how often agents need
it is not recorded.

## Questions for later synthesis

- Should `logicalReadsByTable` become per-table `{ logicalReads, scans }` (or a sibling map), within the
  MCP output budget?
- Should the artifact `metrics` section expose per-table scans?

Cheapest next check: a projection test on a fixture with table IO to measure the byte cost of adding
scans.
