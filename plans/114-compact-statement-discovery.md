# Plan 114: Add compact per-statement CPU and elapsed discovery

> **Status:** accepted research follow-up from Plan 109; TODO. This file is an implementation handoff, not authorization to implement, merge, push, use a live database, or run paid-agent trials.

## Evidence and goal

Plan 109's offline synthetic study measured 9,781 UTF-8 bytes for 32 compact statement records versus 14,168 bytes for equivalent CPU/elapsed discovery from the current statements output, a reduction of 4,387 bytes (31.0%). The compact projection preserves artifact, variant, parameter set, statement ordinal/hash, CPU and elapsed. It omits top operators and other statement metrics. See [decision evidence](109-evidence/task-3-decision.md) and [reproduction record](109-evidence/task-3-reproduction.md).

Add an opt-in saved-artifact read for per-statement CPU/elapsed discovery without operator summaries. Keep exact saved-artifact evidence available through the current `statements` and `plan` reads.

## Contract

1. Add an explicit opt-in selector for compact statement discovery. Existing `section=statements` requests, with omitted new arguments, retain their current JSON shape and include `TopOperators` as today.
2. For each compact record preserve `artifactId`, `variant`, nullable `parameterSet`, nullable `matrixCell`, `statementOrdinal`, `statementHash`, nullable `cpuTimeMilliseconds`, and nullable `elapsedTimeMilliseconds`. Preserve original source ordering and exact identity values.
3. Do not include operator summaries, SQL text, plans, or unrelated statement metrics in the compact response. Label the response/section distinctly so consumers cannot mistake discovery data for a full statement record.
4. Missing CPU/elapsed values remain null and carry the existing availability/evidence-gap semantics. Older artifacts without newer engine-specific metrics remain readable; absence is never zero.
5. A compact record has no repetition identity and is discovery-only. It must not be accepted as reusable evidence for a particular repetition or replace exact plan selection. Reuse of saved evidence remains keyed by artifact ID, variant, parameter set, statement ordinal/hash and repetition.
6. Apply existing page-size, continuation and effective byte-budget rules. Continuation advances by actual emitted source records. Minimum budgets, multibyte/escaped content and a record-too-large case must report truthful clipping/omission; never emit a partial identity or treat missing records as measured zero.
7. Keep existing Core and MCP callers source-compatible. Add optional arguments at the end or introduce a distinct section value without changing the default; update both fixed/request MCP contracts, capabilities and MCP docs. No artifact file or envelope schema migration is proposed.

## Scope and implementation checks

Inspect `ArtifactReader.cs`, `AgentOutputProjection.cs`, `McpOperationMapper.cs`, both MCP tool schemas/handlers, capabilities and `docs/mcp/artifacts.md`/`docs/mcp/tools.md`. Reuse the current saved-artifact owner/target checks and bounded projection. Add focused tests for legacy artifacts, absent fields, multiple variants/sets/statements, exact ordering/identities, minimum byte budget, Unicode/escaped values, clipping and continuation. Demonstrate that default `statements` output is byte/schema compatible for a frozen fixture and that compact output does not expose plan or SQL data.

The final evaluation must compare complete task retrieval bytes, not just record size, and must keep call count separate. UTF-8 bytes and byte/4 estimates are not tokenizer or model-savings measurements. No live-agent acceptance is required by this plan.
