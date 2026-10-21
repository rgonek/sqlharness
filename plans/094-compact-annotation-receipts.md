# Plan 094: Acknowledge annotation batches without echoing their full input

> **Executor:** use staged implementation, task review and whole-branch review. This is a plan, not authorization to implement, merge, push, run paid agent trials or access a live database.
>
> **Drift check first:** `git diff --stat 32adaa7..HEAD -- src/SqlHarness.Mcp tests/SqlHarness.Mcp.Tests docs/mcp`. Reconcile changed interfaces against the excerpts below; do not overwrite later features. Work in an isolated `.worktrees/plan-094` checkout.

## Status and authority

- IN PROGRESS — Task 1 and Task 2 implementation/review complete; Task 3 implementation is ready for review, with the Windows gate pending.
- Priority P1; effort M; implementation risk MED; category perf.
- Dependencies: 085 implementation is merged. Coordinate shared MCP helper edits with 095; implement 094 first.
- **Spec:** the Fix contract in this file is authoritative for this change. Preserve existing safety, scope ownership, persistent-mutation classification and approval.
- Follow `AGENTS.md`, `plans/README.md`, `docs/agents/verification.md`; MCP changes also read `docs/mcp.md`.
- Optimize correct completed tasks, including discovery, requests, responses, recovery calls and available provider usage. UTF-8 bytes and bytes/4 estimates are not measured model tokens.

## Why this matters

An agent already sent its new labels; echoing every label can make a small control operation fail a response-size check, then require resending the request in several batches. Plan 085 fixed ambiguous commits with rollback, but that safety fix did not optimize full-task cost. Keep atomicity while making the normal acknowledgement small enough for all twenty accepted items.

## Current state and conventions

- `Tools/AnnotateHandler.cs:20-25` returns `AnnotationItemResult(OperationRef, Status, Tool, Label, Keep)`.
- `ApplyAsync` checks `FitsCompleteResult(...)` inside the write transaction and throws before commit if it does not fit.
- Fixed handler `McpToolCatalog.cs:405-426` and request handler `McpRequestToolHandlers.cs:205-227` accept only `items`. Their error suggests raising a budget, but the schema offers no such argument.
- `RunAsync` resolves a null call budget to 65536 bytes, clamped by the operator ceiling; the annotation precommit check receives the process ceiling instead. They must use one effective budget.
- Preserve 085's journal-only exact membership, provider/conversation tuple, uniform unavailable response, per-item status, successful mixed batch and no reference for the annotation call itself.

## Fix contract (explicit amendment to 085 response semantics)

1. Add `responseDetail: "receipt" | "full"` to `sqlharness_annotate`, default `"receipt"`, and optional `maxResultBytes` with the existing 4096..1048576 range and process ceiling.
2. Receipt mode returns one item per input, in order. Success contains `operationRef,status,tool,keep`; omit `label` entirely. Keep full refs and tool names so a copied wrong ref remains diagnosable. Unavailable remains `{operationRef,status:"operation_not_available"}` for every unavailable category. Do not add target names or caller-scope diagnostics.
3. Full mode preserves the current successful item fields, including resulting `label`. It is an explicit choice within the existing authorized annotation surface, not a new unscoped annotation-read endpoint.
4. Every valid 1..20-item receipt, including mixed success/unavailable, repeated refs and longest eligible tool names, must fit the minimum 4096-byte model-visible budget without splitting. Prove this using actual served tool responses. If it does not, reduce redundant receipt fields with an explicit contract amendment in this plan before implementing; never hide per-item results.
5. Resolve the effective response budget once and pass that exact value through rendering and transaction precommit validation, in fixed and request scope. Keep rollback for an oversized full response: zero changed annotations and a clear error identifying effective/max allowed bytes and a supported next action. Never commit then project away results.
6. The process ceiling is operator-owned. A call cannot raise it; errors at that ceiling must not suggest a call-level value that cannot work.
7. Keep labels/control validation, batch maximum 20, initial/current label semantics, matching flag reread, journal accounting and owner fields unchanged. MCP envelope stays v3; document the conditional tool-result contract amendment.
8. Feature off remains byte-identical to the pre-change off catalog/results. Keep the complete enabled catalog within 32768 bytes; shorten duplicated descriptions instead of raising that discovery limit.

## Scope

Source: `src/SqlHarness.Mcp/Tools/AnnotateHandler.cs`, `McpToolCatalog.cs`, `McpRequestToolHandlers.cs`, `src/SqlHarness.Mcp/McpResultAdapter.cs` for shared budget plumbing only; `McpOperationMapper.cs` or Core capabilities only if a real advertised limit changes. Tests: `AnnotateToolTests.cs`, `McpToolSchemaTests.cs`, `McpTokenBudgetTests.cs`, `McpOperationAnnotationWiringTests.cs` and fixture host files under `tests/SqlHarness.Mcp.Tests`.
Docs: `docs/mcp/tools.md`, `docs/mcp/runtime.md`, `docs/mcp/artifacts.md` only if shared helper wording changes, `README.md`, and an append-only amendment in `plans/085-operation-annotations.md`/its design spec. Do not rewrite historical 085 evidence.

Out of scope: retention pins, authorization changes, larger input batches, automatic splitting, schemaVersion bump, label length changes or SQL execution.

### Task 1: Freeze the compact contract with failing real-tool tests

- [x] Exercise both fixed and request served annotation tools with twenty 120-code-point multibyte labels, a 4096-byte process cap, and default receipt mode. Require twenty complete item outcomes and persisted changes, not an error/retry.
- [x] At a 65536-byte cap, request full detail and verify complete resulting labels. At 4096, oversized full detail must reject and preserve all prior labels/keep values.
- [x] Verify call-level budget is clamped, unsupported values rejected before writes, unknown/expired/foreign outcomes are uniform, and annotations-off schemas reject new arguments.
- **Verify:** `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~AnnotateToolTests|FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpOperationAnnotationWiringTests"` has meaningful RED.

### Task 2: Implement receipts and single-source budget resolution

- [x] Extend both handler signatures, schema options/allowlists and target-free RunAsync budget plumbing. Preserve optional defaults at unrelated call sites.
- [x] Project the complete authorized item list to the selected response shape before the precommit fit check; return that same shape afterward.
- [x] Test duplicates/omitted fields/empty-label clear/idempotency and invalid-call journaling behavior using existing 085 conventions.
- **Verify:** Task 1 command GREEN; `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter FullyQualifiedName~McpTokenBudgetTests` GREEN.

### Task 3: Measure task cost and amend interfaces

- [x] Compare the old full-echo/rejection/split workflow against one receipt call for the same twenty successful updates. Record serialized input/output bytes and number of calls, separately from model tokens. Include actual error and resubmitted input in the old workflow total.
- [x] Freeze off-mode golden bytes and on-mode catalog size. Make one explicit full-detail example and one operator-ceiling error example available through served descriptions/docs.
- [x] Record the intended response change as a new amendment to 085; do not claim historical full-echo evidence applied to receipts.
- **Verify:** focused MCP tests above plus the real workflow fixture tests pass; Common verification completes.

## Maintenance notes

The compact response is a control receipt, not a lossy benchmark result. Do not reintroduce label echoes by default to satisfy a generic serializer. Plan 095 should reuse the resolved-budget helper added here rather than fork it.

## Common verification and delivery

Before changing source in the isolated implementation checkout, record `$plan094Base = git rev-parse HEAD` and persist that exact SHA in evidence. On resume, reload the saved SHA rather than redefining it after commits. This implementation base can be newer than the planning drift-check base when predecessors have landed.

Run focused tests during iteration; retain the regression's meaningful RED and GREEN. Run affected suites once when ready, then scoped task review and independent whole-branch review. No live database is needed for acceptance.

Before the final gate, normalize only this implementation branch's changed C# files with `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef $plan094Base -Fix`. Commit code, then run `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef $plan094Base` and retain its direct exit code and log. Run `just verify -LogDirectory D:/temp/sqlharness-plan094-verify`; expected exit 0 on the final product tree. Inspect failed stage logs before fixing or rerunning. Do not run Linux verification unless requested.

Record the tested revision/tree, commands, actual results, output bytes, call counts and evidence limits in `plans/094-verification-evidence.md`. Update this plan, `plans/README.md` and `plans/history.md` on completion. Use `fix(094): ...`, `feat(094): ...` and `docs(094): ...` commits as appropriate; integration/publication needs separate authorization.

## STOP conditions

Report a concrete design blocker if the live interfaces invalidate the Fix contract, a required safety/ownership invariant cannot be preserved, or implementation needs unrelated source changes. Ordinary drift, expected regression failures and already authorized task-local fixes are not reasons to restart the design or broaden scope.

## Done criteria

- [ ] All task-specific regressions below pass and each task has spec/quality review.
- [ ] Served schemas, descriptions and capabilities agree with runtime behavior; affected human docs are updated without duplicating interface contracts into skills.
- [ ] Direct branch formatting and final Windows `just verify` exit 0; final whole-branch review is approved.
- [ ] Evidence distinguishes deterministic fixture measurements from actual-agent or live-database results.
- [ ] Changes stay within the listed source/test surfaces plus this plan, its evidence, plan index/history and explicitly listed docs.
