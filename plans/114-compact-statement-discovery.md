# Plan 114: Add compact per-statement CPU and elapsed discovery

> **Status:** Integration candidate on `plan-114`, merged with `main` at `51ee23bcc1dcc44355c1a364fbfd6584c0a06dcd`. Integration and catalog follow-up source reviews approved; the combined Windows gate and branch formatter passed. Awaiting controller review and local-main fast-forward; this is not yet integrated into the primary checkout.

## Evidence and goal

Plan 109's offline synthetic study estimated a 31.0% reduction in its test-only projections: 9,781 UTF-8 bytes for 32 compact statement records versus 14,168 bytes for equivalent CPU/elapsed discovery from the current statements output. The compact projection preserves artifact, variant, parameter set, statement ordinal/hash, CPU and elapsed. It omits top operators and other statement metrics. This is not an estimate of complete API savings: the future response schema and MCP envelope add fields. Before making a savings claim, compare complete bounded task responses as required by the final evaluation below. See [decision evidence](109-evidence/task-3-decision.md) and [reproduction record](109-evidence/task-3-reproduction.md).

Add an opt-in saved-artifact read for per-statement CPU/elapsed discovery without operator summaries. Keep exact saved-artifact evidence available through the current `statements` and `plan` reads.

## Contract

1. The request selector is the existing required MCP `section` string with the exact new allowed value `statement-summary`. `id` remains required. Existing optional `cursor` and `pageSize` keep their current types and bounds; `maxResultBytes` remains nullable integer 4096..1048576, and `maxCellChars` remains nullable integer 0..4096. Add `statement-summary` to both fixed/request tool schemas and the section validator. Do not add a separate boolean or alias.
2. The successful MCP v3 envelope is unchanged (`schemaVersion`, `status`, `result`; optional `error`, `truncation`, and `operationRef`). Its `result` has exactly `artifactId: string`, `artifactKind: string`, `statements: array`, `omittedStatements: integer`, and optional `continuation: integer`. Each `statements` item has exactly `variant: string`, optional nullable `parameterSet: string`, nullable `matrixCell: integer`, `statementOrdinal: integer`, `statementHash: string`, nullable `cpuTimeMilliseconds: integer`, and nullable `elapsedTimeMilliseconds: integer`. These use the existing lower-camel JSON policy.
3. Existing `section=statements` requests retain their current JSON shape, selectors, and inclusion of `topOperators`; this new section is opt-in and does not change that response. A null `parameterSet` follows current null omission behavior; absent CPU/elapsed metrics serialize as null and remain unavailable.
4. Do not include operator summaries, SQL text, plans, or unrelated statement metrics in the compact response. The distinct `statement-summary` section marks the projection as discovery data.
5. Missing CPU/elapsed values remain null and carry the existing availability/evidence-gap semantics. Older artifacts without newer engine-specific metrics remain readable; absence is never zero.
6. A compact record has no repetition identity and is discovery-only. It must not be accepted as reusable evidence for a particular repetition or replace exact plan selection. Reuse of saved evidence remains keyed by artifact ID, variant, parameter set, statement ordinal/hash and repetition.
7. Apply existing page-size, continuation and effective byte-budget rules. Continuation advances by actual emitted source records. Minimum budgets, multibyte/escaped content and a record-too-large case must report truthful clipping/omission; never emit a partial identity or treat missing records as measured zero.
8. Keep existing Core callers and the existing `statements` response source-compatible. Update both fixed/request MCP allowed values, capabilities and MCP docs. No artifact file or envelope schema migration is proposed.

## Scope and implementation checks

Inspect `ArtifactReader.cs`, `AgentOutputProjection.cs`, `McpOperationMapper.cs`, both MCP tool schemas/handlers, capabilities and `docs/mcp/artifacts.md`/`docs/mcp/tools.md`. Reuse the current saved-artifact owner/target checks and bounded projection. Add focused tests for legacy artifacts, absent fields, multiple variants/sets/statements, exact ordering/identities, minimum byte budget, Unicode/escaped values, clipping and continuation. Demonstrate that default `statements` output is byte/schema compatible for a frozen fixture and that compact output does not expose plan or SQL data.

The final evaluation must compare complete task retrieval bytes, not just record size, and must keep call count separate. UTF-8 bytes and byte/4 estimates are not tokenizer or model-savings measurements. No live-agent acceptance is required by this plan.
