# Plan 093: Preserve metric identity under output clipping

> **Executor:** use staged implementation, task review and whole-branch review. This is a plan, not authorization to implement, merge, push, run paid agent trials or access a live database.
>
> **Drift check first:** `git diff --stat 32adaa7..HEAD -- src/SqlHarness.Core/AgentOutputProjection.cs tests/SqlHarness.Tests/Cli/AgentOutputTests.cs tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs`. Reconcile changed interfaces against the excerpts below; do not overwrite later features. Work in an isolated `.worktrees/plan-093` checkout.

## Status and authority

- IN PROGRESS — Tasks 1–2 implemented; Task 2 specification and quality reviews approved. Windows gate and whole-branch review pending. Planned at `32adaa7`, 2026-10-20.
- Priority P1; effort S; implementation risk MED; category bug.
- Dependencies: The existing benchmark/artifact projection must be present. No new-plan predecessor.
- **Spec:** the Fix contract in this file is authoritative for this change. Preserve existing safety, scope ownership, persistent-mutation classification and approval.
- Follow `AGENTS.md`, `plans/README.md`, `docs/agents/verification.md`; MCP changes also read `docs/mcp.md`.
- Optimize correct completed tasks, including discovery, requests, responses, recovery calls and available provider usage. UTF-8 bytes and bytes/4 estimates are not measured model tokens.

## Why this matters

A presentation limit can currently turn a successful benchmark into a response error. Reducing text must not merge distinct table identities, lose their IO attribution, or encourage rerunning SQL just to obtain a renderable result.

## Current state and conventions

- `src/SqlHarness.Core/AgentOutputProjection.cs:386-387` projects artifact metrics with `ToDictionary(pair => Clip(pair.Key)!, ...)`.
- The local `ClipVariant` at line 570 repeats `ToDictionary(p => Clip(p.Key)!, ...)` for benchmark summaries.
- `ClipText` at line 893 returns a prefix. With two names `dbo.Orders` and `dbo.OrderLines`, a short prefix collides; a valid `maxCellChars=0` maps every nonempty name to the empty string.
- `AgentOutputLimits.cs` defaults cells to 2048 characters and permits up to 4096. MCP accepts 0 as well. The output adapter calls the projection after execution, without a collision-recovery path.
- Existing `BySignificance` orders table metrics by median/max/name. Match the existing budget-fit/omission accounting rather than making a new renderer.

## Fix contract

1. Preserve complete dictionary keys in every emitted `LogicalReadsByTable` map. The existing map wire shape and values stay intact. This is an identity exception to cell clipping, not a larger overall response budget.
2. If a complete key/value entry cannot fit, omit that entry through existing detail/budget selection and count it truthfully. Never truncate keys, combine distinct entries, overwrite an entry, invent suffixes, or emit apparently complete partial identifiers.
3. Keep deterministic significance ordering, complete-entry accounting and max-result byte ceilings for both CLI and MCP. A zero cell limit still clips ordinary display text to zero; it does not erase identities.
4. Preserve query row/schema alignment, scope/credential rules, current schema versions and all other projection shapes. Do not alter the 2048/4096 defaults or rewrite all clipping as part of this fix.

## Scope

Source: `src/SqlHarness.Core/AgentOutputProjection.cs`; add an explanatory comment to `AgentOutputLimits.cs` only if needed. Tests: `tests/SqlHarness.Tests/Cli/AgentOutputTests.cs`, `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs`, `tests/SqlHarness.Tests/ArtifactReaderTests.cs` where an actual artifact fixture is needed. Docs: the clipping paragraph in `docs/mcp/runtime.md`.

Out of scope: annotation receipt design, numeric budget defaults, SQL execution, journal migrations and new transport schemas.

### Task 1: Reproduce identity collisions through real response renderers

- [x] Add regressions for two distinct table names sharing a prefix, maxCellChars 0 and 4, and ordinary 2048. Cover measure, both compare sides, measure-set summaries and artifact metrics where those shapes expose table maps.
- [x] Use real `CompareDistribution` values with different medians; assert full original names retain their own values. Test a long name under a 4096-byte response, with truthful omission if necessary.
- [x] Confirm the pre-fix failure is duplicate-key/projection failure, not a malformed fixture.
- **Verify:** `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~AgentOutputTests|FullyQualifiedName~ArtifactReaderTests"` and `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter FullyQualifiedName~McpTokenBudgetTests` produce the intended RED before implementation.

### Task 2: Keep identity keys intact and prove bounded rendering

- [x] Replace both clipped-key dictionary constructions with complete-key selection, reusing significance ordering.
- [x] Keep actual retained-entry omission accounting correct across rejected fit attempts; do not count an omitted display clip on a key now emitted in full.
- [x] Add assertions that output is valid JSON, stays within budget, and never reports success with wrong table-to-metric attribution.
- [x] Add a normal-input regression demonstrating no new response fields or schema bump.
- **Verify:** focused commands and both affected suites are GREEN; direct branch formatting passed. Final Windows gate and reviews remain pending; see [verification evidence](093-verification-evidence.md).

## Maintenance notes

New map/dictionary projections must treat keys as identity. Review every future use of `Clip(...)` inside `ToDictionary`; preserving keys does not remove the global output cap. Plan 095 will reuse this safe renderer for larger artifact pages.

## Common verification and delivery

Before changing source in the isolated implementation checkout, record `$plan093Base = git rev-parse HEAD` and persist that exact SHA in evidence. On resume, reload the saved SHA rather than redefining it after commits. This implementation base can be newer than the planning drift-check base when predecessors have landed.

Run focused tests during iteration; retain the regression's meaningful RED and GREEN. Run affected suites once when ready, then scoped task review and independent whole-branch review. No live database is needed for acceptance.

Before the final gate, normalize only this implementation branch's changed C# files with `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef $plan093Base -Fix`. Commit code, then run `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef $plan093Base` and retain its direct exit code and log. Run `just verify -LogDirectory D:/temp/sqlharness-plan093-verify`; expected exit 0 on the final product tree. Inspect failed stage logs before fixing or rerunning. Do not run Linux verification unless requested.

Record the tested revision/tree, commands, actual results, output bytes, call counts and evidence limits in `plans/093-verification-evidence.md`. Update this plan, `plans/README.md` and `plans/history.md` on completion. Use `fix(093): ...`, `feat(093): ...` and `docs(093): ...` commits as appropriate; integration/publication needs separate authorization.

## STOP conditions

Report a concrete design blocker if the live interfaces invalidate the Fix contract, a required safety/ownership invariant cannot be preserved, or implementation needs unrelated source changes. Ordinary drift, expected regression failures and already authorized task-local fixes are not reasons to restart the design or broaden scope.

## Done criteria

- [ ] All task-specific regressions below pass and each task has spec/quality review.
- [ ] Served schemas, descriptions and capabilities agree with runtime behavior; affected human docs are updated without duplicating interface contracts into skills.
- [ ] Direct branch formatting and final Windows `just verify` exit 0; final whole-branch review is approved.
- [ ] Evidence distinguishes deterministic fixture measurements from actual-agent or live-database results.
- [ ] Changes stay within the listed source/test surfaces plus this plan, its evidence, plan index/history and explicitly listed docs.
