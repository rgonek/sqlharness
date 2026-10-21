# Plan 098: Keep ordinary session history reachable through bounded pages

> **Executor:** use staged implementation, task review and whole-branch review. This is a plan, not authorization to implement, merge, push, run paid agent trials or access a live database.
>
> **Drift check first:** `git diff --stat 32adaa7..HEAD -- src/SqlHarness.Dashboard/JournalReader.cs src/SqlHarness.Dashboard/DashboardServer.cs src/SqlHarness.Dashboard/ui/src`. Reconcile changed interfaces against the excerpts below; do not overwrite later features. Work in an isolated `.worktrees/plan-098` checkout.

## Status and authority

- DONE on feature branch `plan-098` — implementation and all task reviews plus Sol low whole-branch review approved; Windows-verified at `effc0bfbf72d0d548f3ec8945f431e041111d91d`. Integration and push are pending separate authorization. Planned at `32adaa7`, 2026-10-20. See [verification evidence](098-verification-evidence.md).
- Priority P2; effort M; implementation risk MED; category bug.
- Dependencies: No semantic predecessor. Coordinate 089 first where practical because both edit JournalReader/DashboardServer/query hooks; 089 remains responsible for agent-session membership pagination.
- **Spec:** the Fix contract in this file is authoritative for this change. Preserve existing safety, scope ownership, persistent-mutation classification and approval.
- Follow `AGENTS.md`, `plans/README.md`, `docs/agents/verification.md`; MCP changes also read `docs/mcp.md`.
- Optimize correct completed tasks, including discovery, requests, responses, recovery calls and available provider usage. UTF-8 bytes and bytes/4 estimates are not measured model tokens.

## Why this matters

The ordinary session page presents only the newest 500 retained operations and has no continuation. Refreshing cannot recover older rows. A page-size limit should bound one response while keeping the remaining history reachable.

## Current state and conventions

- `JournalReader.cs:39` sets `SessionOperationLimit=500`; `Session(long id)` selects `ORDER BY o.id DESC LIMIT $limit` at lines 423-426 and returns a fixed operation list.
- `ui/src/pages/SessionPage.tsx` labels the table "Newest first, up to 500." It reads `useSession`, not an infinite operation query.
- `api/queries.ts` already has a `useSessionCompares` infinite query and generic `Page<T>` shape. Use its cache/cursor conventions and 085 Named operations' failed-page preservation pattern.
- `ui/src/live/useLiveFeed.ts:71` invalidates `queryKeys.session(operation.sessionId)` on an operation event. Its prefix is `["session", id]`; a disconnected new top-level operations key would miss that invalidation.
- Plan 089 fixes the separate agent-session combined stream, membership filtering and child deduplication. Do not implement or replace those semantics here.

## Fix contract

1. Add `GET /api/sessions/{id}/operations?limit=500&cursor=<id>`, returning `Page<OperationSummary>` as `{items,nextCursor}`. Unknown session returns 404. Malformed/nonpositive cursor returns 400. Default page size 500, clamp valid numeric requested sizes to 1..500 using existing dashboard conventions.
2. Inside one read snapshot, restrict to the exact ordinary session first, then exclusive descending `operation.id < cursor`, then limit+1. nextCursor is the last returned ID only when another eligible row exists.
3. Keep the existing session-detail response shape, initial cap and full summary counts for compatibility. Derive the new endpoint from journal rows, not the already truncated `Session(id).Operations` list.
4. UI uses the paginated endpoint as the authoritative operation stream. Load more appends, preserves loaded rows and offers inline retry after later-page errors. Reset cache/rows when session ID changes.
5. Keep the internal-operation toggle's current meaning. Never stop paging merely because a page contains only hidden internal operations; preserve the ability to load older visible ones.
6. New higher IDs must not duplicate older pages. Retention between requests can remove rows but must not cause invalid access/cursor loops. Cross-request snapshot stability is not promised.
7. Preserve dashboard authentication, readable labels/SQL handling, operation annotation controls and current liveness/compare/Optimization semantics. Define the infinite-query key as `[...queryKeys.session(id), "operations"]` so existing session-prefix live invalidation also refreshes this stream. Keep SessionDetail and infinite-page cache entries distinct. This endpoint is read-only and has no transcript or authorization changes.

## Scope

Source: `src/SqlHarness.Dashboard/JournalReader.cs`, `DashboardServer.cs`, `DashboardModels.cs` only if an existing Page type cannot be reused; UI `api/queries.ts`, `api/types.ts`, `pages/SessionPage.tsx`.
Tests: `tests/SqlHarness.Tests/Dashboard/DashboardServerTests.cs`, `DashboardContractTests.cs`, `JournalReaderTests.cs` if present or a focused new `SessionOperationPaginationTests.cs`; UI `pages/SessionPage.test.tsx`, fixtures, and a new `live/useLiveFeed.test.tsx` only if needed to exercise the existing event handler. `live/useLiveFeed.ts` itself stays unchanged through prefix-key reuse.
Docs: README dashboard section. No MCP changes, journal migrations, agent membership resolver or Plan 089 selector redesign.

### Task 1: Add reader and route continuation tests

- [x] Create 1001 retained operations with distinct IDs; pages 500, 500, 1 recover all exactly once in descending order.
- [x] Verify another session's rows do not consume capacity or appear; summary counts remain full.
- [x] Cover bad cursors/limits, unknown session, new inserts and retention between reads, exact final-page nextCursor=null, and empty known session.
- [x] Add the bounded reader method and authenticated GET route, preserving existing SessionDetail contracts.
- **Verify:** `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~SessionOperationPaginationTests|FullyQualifiedName~DashboardServerTests|FullyQualifiedName~DashboardContractTests"` progresses RED to GREEN.

### Task 2: Wire infinite UI history and recovery

- [x] Add `useSessionOperations(id)` with child key `[...queryKeys.session(id), "operations"]`, exclusive cursor and terminal-null handling; use the shared Page type.
- [x] Test Load more, later-page failure/retry without disappearing rows, disabled pending button, cached navigation to another session, and pages consisting only of hidden internal rows.
- [x] After multiple pages load, simulate the existing live operation-event/session-prefix invalidation. Require running/completed status and a new operation to become visible without duplicate IDs, losing loaded rows on a transient refetch error, or confusing SessionDetail with infinite-query cache data.
- [x] Show loaded-state feedback instead of claiming the entire history is capped at 500. Keep the independent session compares/Optimization hooks unchanged.
- **Verify:** `npm --prefix src/SqlHarness.Dashboard/ui test -- src/pages/SessionPage.test.tsx` (plus the new live-feed test if created) and `npm --prefix src/SqlHarness.Dashboard/ui run check` exit 0.

### Task 3: Record reachability and complete verification

- [x] Record page sizes/cursors, source row counts and no-duplicate/no-gap assertions. This is a dashboard reachability fix, not measured agent-token savings.
- [x] Review the combined diff against any now-merged 089 changes; reuse shared helpers without changing agent membership behavior.
- **Verify:** Common verification plus affected UI build `npm --prefix src/SqlHarness.Dashboard/ui run build` exit 0.

## Maintenance notes

Ordinary sessions and agent conversations are different objects. The endpoint scopes only to a stored session ID. A future live-feed refresh can prepend newly observed IDs separately, but may not invalidate existing exclusive continuation ordering.

## Common verification and delivery

Before changing source in the isolated implementation checkout, record `$plan098Base = git rev-parse HEAD` and persist that exact SHA in evidence. On resume, reload the saved SHA rather than redefining it after commits. This implementation base can be newer than the planning drift-check base when predecessors have landed.

Run focused tests during iteration; retain the regression's meaningful RED and GREEN. Run affected suites once when ready, then scoped task review and independent whole-branch review. No live database is needed for acceptance.

Before the final gate, normalize only this implementation branch's changed C# files with `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef $plan098Base -Fix`. Commit code, then run `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef $plan098Base` and retain its direct exit code and log. Run `just verify -LogDirectory D:/temp/sqlharness-plan098-verify`; expected exit 0 on the final product tree. Inspect failed stage logs before fixing or rerunning. Do not run Linux verification unless requested.

Record the tested revision/tree, commands, actual results, output bytes, call counts and evidence limits in `plans/098-verification-evidence.md`. Update this plan, `plans/README.md` and `plans/history.md` on completion. Use `fix(098): ...`, `feat(098): ...` and `docs(098): ...` commits as appropriate; integration/publication needs separate authorization.

## STOP conditions

Report a concrete design blocker if the live interfaces invalidate the Fix contract, a required safety/ownership invariant cannot be preserved, or implementation needs unrelated source changes. Ordinary drift, expected regression failures and already authorized task-local fixes are not reasons to restart the design or broaden scope.

## Done criteria

- [x] All task-specific regressions below pass and each task has spec/quality review.
- [x] Served schemas, descriptions and capabilities agree with runtime behavior; affected human docs are updated without duplicating interface contracts into skills.
- [x] Direct branch formatting and final Windows `just verify` exit 0; final whole-branch review is approved.
- [x] Evidence distinguishes deterministic fixture measurements from actual-agent or live-database results.
- [x] Changes stay within the listed source/test surfaces plus this plan, its evidence, plan index/history and explicitly listed docs.
