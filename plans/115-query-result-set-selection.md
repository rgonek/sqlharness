# Plan 115: Select relevant query result sets

> **Status:** accepted research follow-up from Plan 109; TODO. This file is an implementation handoff, not authorization to implement, merge, push, use a live database, or run paid-agent trials.

## Evidence and goal

Plan 109's deterministic projection retained the success status, target database, query hash, source result-set count, original ordinals and both assertion result sets in 646 UTF-8 bytes. The existing query response for the same fixture used 11,469 bytes because it also included an unrelated 96-row first set and envelope detail. This is a 10,823-byte (94.4%) reduction for that assertion check only. See [decision evidence](109-evidence/task-3-decision.md) and [reproduction record](109-evidence/task-3-reproduction.md).

Allow a caller to retrieve selected result sets from the already executed query response. This is an output-selection feature; it does not promise less SQL execution or row materialization.

## Contract

1. Add an optional selector of zero-based source result-set ordinals to both fixed and request `sqlharness_query` MCP schemas and handlers. Omitted selector preserves the existing behavior and JSON shape: return the existing prefix under current output bounds.
2. When selected, preserve each set's original `Ordinal`, schema, rows, row count and existing row/column/cell clipping metadata. Preserve query status, resolved target database, query hash where available, messages, warnings and records affected.
3. The selected response must state the source result-set count, selected ordinals, and omitted-result-set count or ordinals truthfully. A selection that skips an earlier set cannot be represented as the old prefix; use additive optional metadata or a distinct response projection while keeping existing consumers compatible. No response envelope version bump unless compatibility review proves one necessary.
4. Reject negative, duplicate, or out-of-range ordinals with a clear input error. Preserve caller order only if explicitly specified by the contract; otherwise normalize to source order and document it. Bound the selector length by the source result-set limit and the existing response budget.
5. Distinguish valid success with zero result sets, a successful query whose selected sets are empty, a structured error, and missing/incomplete result evidence. A missing result collection must never become a successful empty check. Selection must not make execution errors disappear.
6. Omitted selector behavior, query execution, database scope, row/cell limits and safety checks remain unchanged. The selector only limits response projection after execution.
7. Keep fixed/request MCP schema, unknown-key allowlists, served descriptions, capabilities and `docs/mcp/tools.md` in agreement. Do not put interface contracts only in README or user-facing skills.
8. Preserve bounded output and truthful clipping/omission under the minimum result budget, including long escaped/multibyte values. A selection that cannot fit must report omission/continuation accurately; do not return a partial set as complete.

## Scope and implementation checks

Inspect `AgentCompactQuery` and query projection in `AgentOutputProjection.cs`, the query operation/mapper, `McpToolCatalog.cs`, `McpRequestToolHandlers.cs`, schemas and capabilities. Add focused offline MCP tests for omitted selector compatibility, first/middle/last and non-prefix selections, empty selection, invalid/duplicate/out-of-range input, error and missing-output boundaries, and output-budget clipping. Compare full-task UTF-8 retrieval bytes with the frozen Plan 109 fixture, retaining source ordinals. Assert that query execution still occurs once and receives the original SQL and parameters.

UTF-8 bytes and byte/4 estimates are not tokenizer or model-savings measurements. No live database or live-agent acceptance is authorized or required by this plan.
