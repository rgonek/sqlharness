# Observation: telemetry plan documents clipped in `query` cells

Status: collecting observations, not approved for implementation. Recorded 2026-10-20 against
repository HEAD `f928da0d`. No implementation plan is requested.

## Observed workflow

During an optimization (unmapped system label), the agent used SQL Server Query Store only to rank
candidate statements, then tried to read the stored plan of the chosen statement with a `query` over
the Query Store plan catalog view, cast to text, with `maxCellChars` 4096. The plan document was about
23 thousand characters; the response reported the cell as clipped to 4096 characters, and the
response also reported one omitted item. The clipped XML could not be distilled. The agent
dropped it and relied on the actual plan saved by a following `measure`, which answered the question
in this session. Cost: one call with an unusable cell; no retry.

## Verified source

- `src/SqlHarness.Mcp/McpLimits.cs:53-54`: the largest per-cell limit an operator may configure is
  4096 characters (`AgentOutputLimits.MaximumCellCharacters`).
- `sqlharness_plan` accepts an inline `content` document or a `file` under an operator input root. A
  plan returned as a `query` cell cannot reach it without manual transcription, and a clipped cell
  cannot reach it at all.

Classification: provisional ux gap. Confidence: high for the clipping and the missing path; low for
how much it matters, because a measured actual plan replaced it here. Recurrence is unknown (one
occurrence). Related: the [2026-10-20 report entry](../superpowers/specs/2026-10-07-agent-session-report-draft.md)
and the Query Store prioritization notes in
[write-statement and index maintenance evidence](write-statement-and-index-maintenance-evidence.md).

## Questions for later synthesis

- Do other sessions need a stored (estimated or last-known) plan when the statement cannot be
  measured, for example writes or statements with unavailable parameters?
- Would a bounded path from a catalog plan into plan distillation be worth its surface, or is
  measuring the statement always the better route?

Cheapest next check: search later sessions' observations for attempts to read stored plans through
`query`, and note whether a measurement could replace them.
