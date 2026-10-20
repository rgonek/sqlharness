# Plan 095: Let saved-artifact reads spend an explicit bounded response budget

> **Executor:** use staged implementation, task review and whole-branch review. This is a plan, not authorization to implement, merge, push, run paid agent trials or access a live database.
>
> **Drift check first:** `git diff --stat 32adaa7..HEAD -- src/SqlHarness.Core/ArtifactReader.cs src/SqlHarness.Core/Contracts.cs src/SqlHarness.Mcp tests/SqlHarness.Tests/ArtifactReaderTests.cs tests/SqlHarness.Mcp.Tests`. Reconcile changed interfaces against the excerpts below; do not overwrite later features. Work in an isolated `.worktrees/plan-095` checkout.

## Status and authority

- TODO — plan only. Planned at `32adaa7`, 2026-10-20.
- Priority P2; effort M; implementation risk MED; category perf.
- Dependencies: 093 and 094 implementations must be merged before starting; they fix shared identity projection and target-free budget plumbing.
- **Spec:** the Fix contract in this file is authoritative for this change. Preserve existing safety, scope ownership, persistent-mutation classification and approval.
- Follow `AGENTS.md`, `plans/README.md`, `docs/agents/verification.md`; MCP changes also read `docs/mcp.md`.
- Optimize correct completed tasks, including discovery, requests, responses, recovery calls and available provider usage. UTF-8 bytes and bytes/4 estimates are not measured model tokens.

## Why this matters

An agent can enlarge the response from an expensive database call but cannot enlarge the offline read that retrieves omitted saved evidence. Fixed source-page windows can also force extra calls even when a larger response would comfortably fit. Let the agent request more useful evidence per read without turning off global memory, byte or scope bounds.

## Current state and conventions

- `McpToolCatalog.ArtifactAsync` at lines 376-403 accepts selectors/cursor/includeText/maxCellChars but no `maxResultBytes`; target-free RunAsync resolves the default 65536-byte response.
- `ArtifactReader.cs:264,431` fixes matrix-cell and noteworthy-operator windows at 32. Saved plan nodes use `MaxPlanPageSourceNodes=128` at line 181; `ReadPlan` selects that window at line 515.
- Existing projection reduces pages to what fits and must advance continuation by actual returned items. Plans 065/077 cover no-node-fit and complete reconstruction under 4096 bytes. Preserve them.
- `McpOperationMapper.MapArtifactRead`, `SqlHarnessArtifactReadOperation`, `SqlHarnessModule` and `ArtifactReader.ReadSectionWithMetadata` carry the reader request. Add optional arguments at the end or as init properties to preserve existing call sites.

## Fix contract

1. Add optional `maxResultBytes` to fixed/request `sqlharness_artifact` with the same range/default/process-ceiling semantics as DB tools. Reuse 094's shared effective-budget resolution. Increasing the operator ceiling alone must not be described as increasing a null per-call budget.
2. Add optional `pageSize` for pageable sections only: `matrix-cells,operators,statements,plan`. Accept 1..512 source items; reject other values and use with nonpageable sections. This is a bounded requested source window, not a guarantee every item fits.
3. When omitted, use 32 for matrix cells/operators and 128 for plan nodes. Statements currently load all remaining source statements; introduce a bounded default of 128 statements with honest continuation. This is an explicit source-window change, not a claim that the historical statements reader already paged at 128. Do not tune other global defaults without the whole-task evaluation in 077.
4. Propagate page size through the Core operation/mapper/reader without changing CLI defaults or artifact files. The constant 128 becomes the default, not the only allowed plan window; retain a separate maximum of 512.
5. Final JSON remains within the exact resolved budget, every emitted node/row is complete, and continuation advances by actual returned source positions. A tight budget may return fewer than pageSize; it may not skip the unreturned tail. Keep statement-specific identity, selected run/repetition, omission counts and no-node-fit truthfulness.
6. Expose max page source items and defaults in the authoritative capabilities/served interface, without leaking local paths. Keep catalog within 32768 bytes.
7. This reads existing saved evidence only. No DB connections, raw XML regeneration, automatic persistence of previously unsaved query/watch rows, authorization changes or response schema version bump.

## Scope

Source: `src/SqlHarness.Core/ArtifactReader.cs`, `AgentOutputProjection.cs` for cursor/returned-window handling, `Contracts.cs`, `SqlHarnessModule.cs`, `Capabilities.cs`; `src/SqlHarness.Mcp/McpOperationMapper.cs`, `Tools/McpToolCatalog.cs`, `Tools/McpRequestToolHandlers.cs`, `McpResultAdapter.cs` for already shared budget handling.
Tests: `tests/SqlHarness.Tests/ArtifactReaderTests.cs`, `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs`, `McpToolSchemaTests.cs`, `McpOperationMapperTests.cs` if present, `AgentOptimizationWorkflowTests.cs` and fixture host.
Docs: `docs/mcp/artifacts.md`, `docs/mcp/tools.md`, `docs/mcp/runtime.md`, relevant README artifact section. No benchmark artifact schema migration.

### Task 1: Add served-budget and source-window regressions

- [ ] At a 256 KiB process ceiling, prove a saved-artifact call requesting more than 64 KiB can return more than 64 KiB of useful evidence while staying below its request/ceiling. Compare the old default to the explicit larger request.
- [ ] Test pageSize 1, default, 512, 0 and 513; unknown/foreign artifact refusal must remain content-free regardless selectors/budget.
- [ ] Prepare at least 513 plan nodes and a synthetic matrix inventory of more than 32 entries (reader-level fixture; do not lift real execution's 32-case ceiling). Reconstruct all items under small and large budgets.
- **Verify:** `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpTokenBudgetTests"` and `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter FullyQualifiedName~ArtifactReaderTests` show intended RED.

### Task 2: Propagate bounded page requests and exact response budgets

- [ ] Add optional fields through both MCP schemas/unknown-key allowlists and the Core reader operation. Preserve old method callers and CLI behavior.
- [ ] Replace fixed source-window use with validated defaults/request values; use existing byte-fit accounting to derive returned cursor advancement.
- [ ] Test no item fits, final page, includeText variants, multiple repetitions, inserts not applicable to immutable artifacts, and partial legacy evidence.
- **Verify:** Task 1 commands GREEN; existing Plan 065/077 continuation and byte-budget tests remain green.

### Task 3: Demonstrate useful aggregation, not just bigger JSON

- [ ] For the same saved 513-node evidence completion, record calls and total request/response bytes with default windows versus explicit pageSize/budget. A larger response should reduce calls where it actually fits; record any total-byte increase and do not call that token savings.
- [ ] Exercise actual SDK tool calls with zero DB connections, not only direct projector calls. Record catalog bytes and defaults compatibility.
- **Verify:** `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter FullyQualifiedName~AgentOptimizationWorkflowTests` GREEN, then Common verification.

## Maintenance notes

Do not confuse the source-window bound with the final wire budget. If larger pages increase CPU from repeatedly parsing artifacts, record that before tuning defaults; this plan does not introduce an unbounded cache or a new artifact index.

## Common verification and delivery

Before changing source in the isolated implementation checkout, record `$plan095Base = git rev-parse HEAD` and persist that exact SHA in evidence. On resume, reload the saved SHA rather than redefining it after commits. This implementation base can be newer than the planning drift-check base when predecessors have landed.

Run focused tests during iteration; retain the regression's meaningful RED and GREEN. Run affected suites once when ready, then scoped task review and independent whole-branch review. No live database is needed for acceptance.

Before the final gate, normalize only this implementation branch's changed C# files with `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef $plan095Base -Fix`. Commit code, then run `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef $plan095Base` and retain its direct exit code and log. Run `just verify -LogDirectory D:/temp/sqlharness-plan095-verify`; expected exit 0 on the final product tree. Inspect failed stage logs before fixing or rerunning. Do not run Linux verification unless requested.

Record the tested revision/tree, commands, actual results, output bytes, call counts and evidence limits in `plans/095-verification-evidence.md`. Update this plan, `plans/README.md` and `plans/history.md` on completion. Use `fix(095): ...`, `feat(095): ...` and `docs(095): ...` commits as appropriate; integration/publication needs separate authorization.

## STOP conditions

Report a concrete design blocker if the live interfaces invalidate the Fix contract, a required safety/ownership invariant cannot be preserved, or implementation needs unrelated source changes. Ordinary drift, expected regression failures and already authorized task-local fixes are not reasons to restart the design or broaden scope.

## Done criteria

- [ ] All task-specific regressions below pass and each task has spec/quality review.
- [ ] Served schemas, descriptions and capabilities agree with runtime behavior; affected human docs are updated without duplicating interface contracts into skills.
- [ ] Direct branch formatting and final Windows `just verify` exit 0; final whole-branch review is approved.
- [ ] Evidence distinguishes deterministic fixture measurements from actual-agent or live-database results.
- [ ] Changes stay within the listed source/test surfaces plus this plan, its evidence, plan index/history and explicitly listed docs.
