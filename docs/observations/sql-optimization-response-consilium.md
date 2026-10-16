# SQL optimization response contract: consilium decision record

Date: 2026-10-16. Source revision: `cd7bab8280259dc1f41679db973019120d3f0681`.

This is planning evidence, not an implemented contract or a measured performance claim. The operator requested a plan for the earlier response-slimming directions and a new independent review of additions/tweaks that improve an agent's SQL optimization workflow. The coordinator gathered context and synthesized; a fresh Codex subagent, Claude and Grok each produced a separate independent report. No database, builds or tests were run for those reviews. Reports were kept as local review artifacts; this record preserves the decisions without requiring those temporary files.

## User constraints

- MCP/CLI compatibility only where useful. Shared execution, safety and evidence semantics are valuable; identical JSON presentation is not an objective.
- Optimize the agent's whole workflow, not just the size of one response: meaningful decision, sufficient observed evidence for that decision, and direct retrieval of the next necessary detail.
- Preserve readable SQL, parameters, plans, results and target names, credential protection and persistent-effect classification.
- Planning only; no SQL product rewrites, live database access or implementation authorization.

## Prior directions retained

| Direction | Plan treatment |
|---|---|
| P1: simplify envelope/noise | Dedicated lean versioned MCP envelope; CLI remains independent; keep a cheap explicit schema version. |
| P2: artifact ids and resolved target | Round-trippable ids, no clipped filesystem paths; enough resolved server/database/engine identity to avoid ambiguity. |
| P3: honest metrics/precision | Null for unavailable, one exact elapsed source, min/median/max, availability and row-source semantics; less repeated prose. |
| P4: decision-first matrices | Verdict/reasons in case inventory; full variants/operators on demand. |
| P5: completeness/recovery | Execution coverage distinct from response paging; exact plan references and explicit clipping. |
| P6: hashes/aggregates by use | Audit hashes are not default evidence; cross-set extrema still useful if the emitted set list is incomplete. |

## Independent positions

**Codex subagent:** prioritize the link between existing measurements and the next evidence read. Proposed small evidence/gap hints, case verdicts and elapsed spread, result stability and exact operator-to-plan selectors. Coverage was broad across the manifest; no execution proof or live performance measurement.

**Claude:** prioritize avoiding false optimization acceptance. Proposed distinguishing baseline/candidate instability and schema/row/nullability differences, retaining regression verdict/reasons in the inventory, explicit partial-matrix coverage and accurate operator provenance. It also emphasized that pass means no elapsed regression, not demonstrated improvement. Some files were inspected by search/selected ranges, and PostgreSQL collection was outside its review.

**Grok:** prioritize compact case decisions and eliminating repeated detail, directly usable ids, unavailable metrics and structured matrix stop metadata. Proposed hash-derived plan identity stability and a multi-statement summary as additional discovery aids. It preferred retaining the current envelope. Native source access completed normally (`end_turn`, process exit 0), without excerpt fallback. It did not inspect the full CLI renderer, user skill or PostgreSQL plan distiller.

All positions are static reviews. Their agreement is not a test. Approximate per-field byte/runtime claims in the consultant reports are hypotheses until measured.

## Agreement and selected additions

1. Default matrix inventory must retain a per-case regression verdict and reasons. A negative elapsed delta alone does not establish correctness or a reliable optimization.
2. `artifactId` should be directly usable by the existing artifact tool. Reader and projector must agree on valid ids, including valid punctuation; empty or clipped ids are not usable evidence.
3. Missing CPU/reads evidence must not look like numeric zero. Keep precision, availability and PostgreSQL nonadditivity rather than removing fields indiscriminately.
4. Partial execution needs structured requested/completed/failed/not-executed coverage. Omitted output cases are a different concept from unexecuted parameter sets.
5. Explain observed result comparison failures by side stability and established schema/row/nullability reasons. Preserve the user's chosen comparison semantics.
6. Link a diagnostic operator to the exact saved statement/run/node only when origin is retained; bare nodeId is insufficient across statements or variants.
7. Prove gains with synthetic complete-workflow measurements and bounded agent replay; do not claim query speedups from response changes.

## Disagreements and synthesis decisions

| Issue | Opinions | Selected direction |
|---|---|---|
| Envelope | Claude accepts slimming; Grok would retain it; Codex emphasizes semantic reasons. | Keep the earlier accepted slimming direction, but retain schemaVersion and stable status/error semantics; do not force the CLI shape to follow MCP. |
| Global evidence readiness | Codex proposed `evidence.sufficient` and `nextReads`. | No universal sufficient boolean: result correctness, timing validity and diagnosis need different evidence. Only proven, executable artifact references/continuations; no guessed next read to an unavailable plan. |
| Reasons for non-equivalence | Claude proposed one prioritized mismatchReason including order-only. | Nullable side stability/schema diagnostics and bounded multiple reasons. No order-only inference from aggregate counters, no automatic ordered-to-multiset change. |
| Case elapsed spread | Codex wants spread at case level; Grok would leave it in details. | Preserve small elapsed ranges alongside case verdicts; measure paging/total-byte tradeoff and amend if it worsens the workflow. No raw per-run arrays. |
| Plan hash stability | Grok proposed planIdentityStable; Claude made it conditional. | Defer. XML/identity hash stability is not plan-shape stability or a prerequisite for trusting measured latency. Reuse the existing shape evidence and its gaps. |
| Operator linking | Codex proposes exact selectors; Claude adds statement/node origin; Grok starts with statementOrdinal. | Preserve exact origin and emit bounded callable refs only if available; no invented matches from aggregate operators. |
| Dominant-statement default summary | Grok proposes statementCoverage/dominant for multi-statement plans. | Defer default summary until workflow evidence shows discovery cost. First make statements and their operator links reliable on demand; a median dominant statement is not a measured whole-batch bottleneck verdict. |
| Operation references | Codex would keep them for provenance; Claude removes them from model text; Grok retains them. | Keep journal attribution internally; no default model text solely for a reference that no callable tool accepts. Artifact ids/selectors remain the retrieval path. |

Do not adopt suggestions that classify every different row result as a domain bug in the candidate when baseline instability has been observed. Do not require fetching every plan just because a plan-stability flag is null. A new field must identify an agent decision/action and distinguish measured, derived and unavailable evidence.

## Current-state boundaries

- Plan 065 is DONE: retain once-only clipping accounting and saved-plan paging, not a duplicate implementation task.
- Plans 066 and 067 are implemented: preserve regression policy v2 and current budgets/cell limits.
- Plan 068 deliberately keeps phase timings in the journal/dashboard; do not add them to every MCP response.
- Plan 076 is DONE: completed partial-matrix runs are journaled. New coverage fields do not reopen that fix.
- Plan 063 remains TODO and exclusively owns missing-index overlap/catalog inventory. No new DB capture in this response plan.

## Acceptance evidence to collect during implementation

Reuse existing journal workflows and call/byte totals rather than repeating agent/database runs. Read-only inspection of the local journal confirmed agent-conversation links, producer/artifact links and fully populated emitted-byte measurements for several relevant sessions. The [portable anonymous export](../../plans/evidence/077-workflow-baseline.json) contains four sessions, eleven compare/artifact workflows and forty calls with measured output costs and producer/read relationships. It excludes real ids, timestamps, SQL, parameters, targets and result/plan payloads; the executor needs no access to the originating machine.

The export is historical cost evidence, not a replayable response fixture: source binary revision, response budgets and original payloads are unavailable. Build explicitly synthetic fixtures for the recorded patterns and render the same fixtures before/after the change. Never report the difference between historical real payload costs and new synthetic payload costs as savings.

The operator selected actual request-based evaluation: the baseline/candidate comparison must drive a local test-only MCP server through the SDK client and real tool calls, keeping production handlers, adapter, serialization, output budgets and offline artifact reads. Synthetic database-facing evidence keeps inputs controlled; it is not a live SQL Server measurement. Direct projector tests alone do not prove the end-to-end contract.

Separately, fresh agents receive the optimization task and the version's served interface/skill, choose their own calls and conclude without access to fixture oracles or other runs. Three paired runs for each of three task families (no-gain/invalid comparison, partial matrix, multi-statement diagnosis) separate behavioral variation from deterministic protocol-byte measurements. Report all trials with bounded calls/time and compare costs only at equivalent evidence completeness. Real database integration remains a separately authorized optional lane. Plan 077 pins the protocol and behavioral evaluation procedure and portable host/fixture paths.

Then test unchanged/no-gain pass, lower reads with slower elapsed, unstable baseline, schema differences, comparison off, unavailable CPU/reads, fractional milliseconds, partial matrices, bounded inventory pages, colliding operator ids, legacy/scope-mismatched artifacts and deep/clipped output. Assert both preserved decision quality and the exact additional evidence retrieved. Separate whole-session cost from compare-plus-artifact workflows and discovery from producer/follow-up cost; label estimated tokens as estimates. Do not infer unrecorded discovery/input/final-chat cost from raw bytes.

Implementation and exact task interfaces are in [Plan 077](../../plans/077-agent-sql-optimization-evidence.md). No product behavior changed when this record was written.
