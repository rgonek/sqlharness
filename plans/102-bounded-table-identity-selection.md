# Plan 102: Bound table identity selection work

> **For agentic workers:** use `superpowers:subagent-driven-development` or `superpowers:executing-plans` task by task. This follow-up is a plan, not authorization to implement, merge, push or use a live database.

**Goal:** Remove Plan 093's remaining Important performance finding without losing complete identities or later fitting entries.

**Architecture:** Replace repeated serialization of growing containing projections with exact incremental JSON byte accounting. Serialize each fixed containing shell once and each candidate dictionary entry once; retain the final renderer's serialized-envelope fit check as authority. Do not introduce a candidate-count cutoff that silently discards later fitting entries.

**Tech stack:** C#/.NET, System.Text.Json, xUnit, Windows verification.

**Spec:** This plan's Fix contract, together with [093's identity contract](093-identity-safe-output-clipping.md#fix-contract).

## Status and provenance

- TODO, P1; follow-up to the Sol low scoped review of `600f8b9..e7eadde`.
- Start from Plan 093 implementation `e7eaddec15fdac850ae4b00d714b59b932c9e8a8` or a verified successor containing it. Plan 093 is review-blocked; it does not need to merge first. Work on that feature branch or an isolated descendant, never silently assume main contains it.
- Finding: `SelectTableReadMaps` calls callbacks that serialize the entire containing compare/measure/artifact projection for every candidate. Large maps repeatedly serialize retained entries and can allocate near-budget responses for every rejected candidate.
- Existing correctness tests cover zero/four/2048 cell clipping and first-ranked identities at 3000/3500/5000 characters. Preserve those tests.

## Global constraints / Fix contract

1. Dictionary keys remain complete identities with unchanged values and wire shapes. Never truncate, rename, merge or overwrite keys.
2. Retain deterministic significance order and round-robin treatment of compare sides; an unfittable entry does not consume a retained-entry slot. Continue to later entries that fit.
3. Account exactly for retained map entries, JSON property-name escaping, delimiters, commas and fixed containing projection bytes. Use the same serializer options as the runtime. Count omitted entries once; rejected trials must not clip display text or inflate omission counters repeatedly.
4. After the existing significance sort, accounting work is linear in serialized source-entry bytes plus fixed containing-shell bytes; do not reserialize accumulated retained entries per candidate. Memory stays proportional to source input plus bounded selected output. One enormous key must not induce repeated full-response allocations.
5. Preserve finite response/detail caps, the existing outer-envelope reserve and final renderer byte checks. No schema bump, new public fields, broader limits, database execution or safety-policy changes.

## Files and responsibilities

- `src/SqlHarness.Core/AgentOutputProjection.cs`: selector, fixed-shell calculation and incremental entry cost. Keep helpers internal/private; do not expose performance instrumentation through response schemas.
- `tests/SqlHarness.Tests/Cli/AgentOutputTests.cs`: CLI measure/compare/artifact correctness and deterministic work-bound regressions.
- `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs`: MCP escaped-key, boundary and minimum/default/maximum-budget coverage.
- Plan 093/102, their evidence and index/history: accurate readiness and reviewed residual closure. Runtime guide changes only if user-visible semantics change.

## Review focus

- Many rejected high-ranked entries followed by fitting short entries.
- Multibyte keys and JSON escaping that differ from character counts.
- Paired compare maps and omission counts after detail retries.
- Empty maps, exact-boundary entries, and ordinary display clipping at zero.
- High cardinality at 4096, 65536 and the currently allowed maximum response bytes (verify the served maximum before freezing fixtures).

### Task 1: Reproduce and bound selection work

- [ ] Read repository instructions, plan index and verification contract; record exact branch base. Verify the reviewed selector still exists and reconcile drift.
- [ ] Freeze deterministic fixture measurements on the current implementation: high-cardinality unique maps, both mostly fitting and mostly rejected entries, paired compare and artifact metrics. No database/provider calls. Record wall time, allocated bytes, input cardinality and complete response size; measurements are not token usage.
- [ ] Add a deterministic regression for cumulative serialization work. Use existing internal test access if available; otherwise introduce the narrowest internal accounting seam alongside the fix, without a public setting or runtime output field. Assert bounded accounting bytes/work per source entry rather than a machine-dependent elapsed-time threshold.
- [ ] Include escaped quotes/backslashes/control characters and multibyte identities. Require exact identity/value attribution, truthful omission counts, later short-entry retention, valid JSON and final byte ceilings.
- [ ] Demonstrate meaningful RED for the work bound on the reviewed implementation, then implement fixed-shell plus per-entry byte accounting. Derive dictionary comma costs from retained counts and apply both compare-map additions against one containing-shell budget.
- [ ] Run existing identity suites plus the new work-bound tests GREEN. Repeat the same stress measurement once, report both results and limits, self-review and commit. Task review must assess correctness and the claimed work bound.

### Task 2: Verify and close the blocked review

- [ ] Run scoped branch formatter `-Fix`, commit its output, then direct branch formatter with saved base/log/exit. Do not manually alter EOF bytes afterward.
- [ ] Run final Windows `just verify -LogDirectory D:/temp/sqlharness-plan102-verify`; inspect any failed-stage logs. Linux remains unverified unless requested.
- [ ] Obtain independent whole-branch review of the combined Plan 093+102 range, including the previous Sol dissent and performance evidence. A scoped review of accounting alone does not replace the combined review.
- [ ] Update both plans, evidence and index/history only after review and gate results are known. Close Plan 093's remaining Important explicitly; do not infer approval from passing functional tests.

## Delivery limits

No live database or paid-agent trial is needed. Merge and push require separate authorization. If exact accounting cannot match serialization without changing public surfaces or weakening later-entry retention, report a concrete design blocker rather than adding an arbitrary candidate scan cap.
