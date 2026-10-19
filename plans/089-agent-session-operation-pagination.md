# Plan 089: Reachable agent-session operations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Execute task-by-task with scoped review and final whole-branch review.

**Goal:** Make every retained operation eligible for an agent-session detail reachable, including an old exact member behind 500 newer unassigned operations.

**Architecture:** Add a bounded, keyset-paginated operations endpoint scoped to an agent session. Page the combined eligible stream across all children, not each child's truncated detail. Keep membership semantics and full counts independent of page boundaries. The dashboard offers Load more.

**Tech Stack:** .NET 10, SQLite, xUnit; React, TanStack Query, Vitest.

**Spec:** [Fix contract below](#fix-contract), amending the operation-list reachability of [084 design](../docs/superpowers/specs/2026-10-17-agent-sessions-design.md).

## Status and dependencies

- TODO. Depends on the actual Plan 084 implementation being merged into main. Planning product base: `3f11525`; originating Sol Important finding is recorded in [084 evidence](084-verification-evidence.md).
- User requested a plan, not implementation. Priority P1.

## Fix contract

- Eligible operations are exact/inferred members of the requested key plus unassigned operations from its child sessions. Never include exact/inferred members of another conversation. Deduplicate by operation ID.
- `GET /api/agent-sessions/{kind}/{id}/operations?limit=500&cursor=<id>` returns `Page<AgentSessionOperation>` with `{items,nextCursor}`. Default limit 500, clamp to 1–500. Descending operation ID, exclusive `id < cursor`, limit+1 lookahead, nextCursor is the last returned ID only when more eligible rows exist. Nonpositive/malformed cursors return 400; unknown key returns 404. Reuse existing matching-off gating.
- Apply membership, cursor and limit in that order, inside one read snapshot per request. Do not fetch `Session(childId).Operations` as the page source. Bound operation projection to limit+1 rows; metadata resolution may retain the existing journal-wide membership scan.
- Full summary counts and member recency remain independent of pages. New higher IDs do not duplicate previously loaded rows. Retention may remove rows between requests; no persistent cross-request snapshot or guarantee under membership reclassification is introduced.
- Keep the existing detail response for compatibility, but cap its initial operations globally at 500 using the same selector. The UI uses the new endpoint as the authoritative operation stream.
- Load more appends pages, preserves membership markers and internal-operation filtering, disables during fetch, and offers retry after failure. Stop when nextCursor is null. A failed subsequent page must retain already loaded rows.
- No journal migration, transcript reads, authorization changes, mutation policy changes or MCP changes.

## Global Constraints

- Membership values: `exact`, `inferred`, `unassigned`; inferred never feeds Optimization history or authorization.
- Matching remains opt-in; turning it off restores per-session views immediately.
- Bounded output; labels and SQL remain readable. Never prioritize exact rows by silently dropping unassigned rows.
- Windows `just verify` on final product tree; Linux only if explicitly requested. No merge/push implied by implementation.

## Review Focus

1. One exact A plus 500 trailing unassigned rows: A is reachable on page two.
2. 500 newer exact B rows in a shared child: none appear in A's pages.
3. Multiple children and page boundaries: descending global order, no duplicates, correct terminal cursor.
4. New inserts or retention between pages: stable exclusive cursor and no crashes.
5. Matching off or failed next page: no cached aggregate leakage or loss of loaded rows.

### Task 1: Reader and HTTP pagination

**Files:** `src/SqlHarness.Dashboard/JournalReader.cs`, `DashboardServer.cs`; tests `tests/SqlHarness.Tests/Dashboard/AgentSessionsTests.cs`, `DashboardServerTests.cs`, `DashboardContractTests.cs`.

**Interfaces:** `JournalReader.AgentSessionOperations(AgentSessionKey key, int? limit = null, long? cursor = null)` returns `Page<AgentSessionOperation>?`; null means unknown key. Share the bounded selector with `AgentSession` without changing its response shape. Route is specified in Fix contract.

- [ ] Add failing tests: `Trailing_unassigned_rows_do_not_make_exact_members_unreachable` (501 eligible rows, pages of 500 and 1), `Foreign_members_do_not_consume_page_capacity`, `Agent_operation_pages_merge_children_once`, `Cursor_survives_new_rows_and_retention`, and route cursor/limit/404 cases.
- [ ] Run `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~AgentSessionsTests|FullyQualifiedName~DashboardServerTests|FullyQualifiedName~DashboardContractTests"`; retain meaningful RED output.
- [ ] Implement the selector, route and global initial detail cap; keep counts and recency unchanged. Validate JSON contract with real route tests.
- [ ] Rerun focused tests GREEN; commit `fix(089): page agent-session operations after membership selection`.

### Task 2: Dashboard paging and completion evidence

**Files:** `src/SqlHarness.Dashboard/ui/src/api/queries.ts`, `api/types.ts`, `pages/AgentSessionPage.tsx`, `pages/AgentSessionPage.test.tsx`; `plans/089-verification-evidence.md`, plan/index/history.

**Interfaces:** `useAgentSessionOperations(key: AgentSessionKey, enabled = true)` uses `useInfiniteQuery` and the Task 1 endpoint. Cache key includes agent kind and conversation ID. `Page<AgentSessionOperation>` matches the existing page contract.

- [ ] Add failing UI tests for Load more reaching the old exact row, subsequent-page failure/retry retaining rows, global newest-first ordering, visible membership, internal-row toggle, and matching-off invalidation/redirect.
- [ ] Implement the hook and button; replace the misleading fixed-cap description with loaded-state feedback. Keep annotation and Optimization requests unchanged.
- [ ] Run `npm --prefix src/SqlHarness.Dashboard/ui run check` GREEN; review the task and whole branch, fixing findings through the selected workflow.
- [ ] Run branch formatting with a retained direct exit/log, then Windows `just verify` with unique logs. Record page sizes/cursors, test revision, counts and remaining boundaries; update plan/index/history. Commit `docs(089): record operation pagination verification`.

## Plan self-review

All five review-focus cases map to Tasks 1–2. The endpoint is additive, preserves the detail shape and existing membership policy, and fixes reachability through paging rather than expanding an unbounded response. Implementation must recheck current interfaces after integration.
