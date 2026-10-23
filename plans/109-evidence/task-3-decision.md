# Plan 109 Task 3 — decisions and implementation handoffs

Status: **research complete on `f6044c839890f92b4f42d942ef8a1719cbf71b94`**. This decision uses the frozen SDK capture and deterministic Task 2 projections in [`task-1-sdk-baseline.json`](task-1-sdk-baseline.json) and [`task-2-candidate-study.json`](task-2-candidate-study.json). Measurements are UTF-8 bytes visible in tool responses. No tokenizer, live database, paid model, or live-agent acceptance was used.

## Decisions

| Candidate | Decision | Measured equivalent evidence | Decision basis and boundary |
| --- | --- | --- | --- |
| (a) Existing interfaces with accurate selectors and budgets | **Accept as current workflow guidance; no implementation plan** | Candidate revision review: 7,603 bytes per revision (6 calls); named-set attribution: 10,974 bytes (3 calls); late assertions: 11,469 bytes (1 call). All responses fit their requested budgets with no continuation or omitted items. | Existing exact artifact, variant, statement, repetition, page-size, row and byte selectors work for the captured tasks. Keep evidence identities distinct. These are unlike evidence scopes and must not be pooled into a global ranking. |
| (b) Compact per-statement CPU/elapsed discovery without operators | **Accept; Plan 114** | 9,781 bytes versus 14,168 bytes for the same 32 records and artifact/variant/set/ordinal/hash/CPU/elapsed fields; 4,387 fewer bytes (31.0%). | Equivalent only for CPU/elapsed discovery. Operators and other statement metrics are intentionally absent. The summary has no repetition identity and cannot stand in for repetition-specific saved-plan evidence. |
| (c) Precise artifact metrics pointers for named-set measurements | **Defer named-set value selection; reject pointer-only reduction; no implementation plan** | Values: 1,195 bytes versus 1,211 bytes (16 fewer, 1.3%) with equivalent metric values for all four sets. Pointers only: 534 bytes versus 1,211 bytes, but not equivalent because metric values are absent until dereferenced. | A second dereference would restore the omitted evidence, and the current request needs all four sets. The study does not show a useful one-set workload. Revisit if a concrete caller needs only a subset and measure the whole retrieval sequence. |
| (d) Relevant-result-set selection for assertion queries | **Accept; Plan 115** | 646 bytes versus 11,469 bytes for the same two assertion result sets and success/database/query-hash/source-count evidence; 10,823 fewer bytes (94.4%). | The 96-row first set and unneeded envelope detail are omitted. Preserve source ordinals and distinguish valid empty success, error, and missing/incomplete results. The result-set selector does not imply the query executed fewer rows. |
| (e) Identify setup-in-batch statements separately from the measured subject | **Reject a new in-batch mode or input rewrite; retain existing separate setup input** | No equivalent byte comparison: the current separate setup input is preserved as supplied; prepending `SET NOCOUNT ON;` changes the measured SQL. The fixture expects four setup invocations but records that none were observed. | The TVP alternative also changes the scalar parameter and compiled SQL shape. No attributable counters support subtracting setup work from whole-batch measurements. Do not claim setup was measured separately. |

The byte/4 figures in Task 2 are rough estimates only. Candidate projections were measured before a runtime output budget; their clipping behavior was not exercised. These are synthetic output comparisons, not measured model-token savings or live acceptance.

## Evidence and reproduction

The frozen fixture and the commands, expected results, and observed results are recorded in [`task-3-reproduction.md`](task-3-reproduction.md). Task 1 uses the real offline MCP SDK workflow host and records zero database connection attempts. Task 2 renders deterministic test-only projections from that retained response. Supporting task reports remain in the local SDD workspace; the authoritative machine-readable outputs are the two JSON files above.

## Review-focus assessment

1. **Older artifacts and absent engine-specific fields — applicable, constrained but not implemented here.** Existing evidence uses nullable metric values and availability fields; missing values must remain unavailable, never become zero. Plan 114 requires legacy artifacts to remain readable and preserves null/availability semantics. Task 3 adds no reader behavior.
2. **Variant, statement, parameter-set, and repetition identity — applicable and assessed.** The two candidate revisions have distinct artifact IDs; statement ordinals 0–2 share hashes while ordinal 3 differs. No cross-artifact reuse is allowed. Any saved-plan evidence remains keyed by artifact ID, variant, parameter set, statement ordinal/hash, and repetition. Plan 114 summaries are discovery-only because they have no repetition field.
3. **Minimum budgets, escaped/multibyte text, and continuations — applicable to future implementation; not measured by this study.** Captured baseline responses had no clipping, continuation, or omitted items. Candidate renderers did not apply runtime budgets. Plans 114 and 115 must retain existing bounded-response behavior, report truthful clipping/omissions, and test minimum budgets plus long escaped/multibyte content and continuation. This is an explicit limitation of the byte comparison.
4. **Errors and negative results — applicable and assessed by the Task 2 boundary fixtures.** A structured error remains `status=error`; valid empty output remains successful with database identity and zero source result sets; absent result data remains incomplete. Plan 115 must preserve these distinctions when selecting result sets.
5. **No live acceptance or model-token claim — applicable and satisfied as a reporting constraint.** This is offline synthetic research only. No live database, deployment, tokenizer, paid-agent, or actual-agent acceptance was performed. UTF-8 bytes and byte/4 estimates are not token measurements.

## Accepted implementation plans

- [Plan 114](../114-compact-statement-discovery.md) pins an opt-in operator-free statement summary while preserving the current default `statements` response and legacy artifact reading.
- [Plan 115](../115-query-result-set-selection.md) pins optional query result-set selection while preserving default all-set behavior and truthful original ordinals/omissions.

Neither plan authorizes implementation, merge, push, live database access, or paid-agent runs. Implementation remains a separate task authorization.
