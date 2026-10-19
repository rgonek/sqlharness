# Plan 084: Agent sessions across MCP/CLI sessions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Dependency:** follows plan 087 (measure observations in Optimization), in addition to plans 079 and 080. **Drift check (run first):** dependencies must be merged. `git diff --stat 6c37cd7..HEAD -- src/SqlHarness.Dashboard src/SqlHarness.Core/Journal`. Use 079's `FirstRetainedVersion` rule and 080's `CompareHistoryBuilder`/`OptimizationSummaryBuilder` names.

**Goal:** When transcript matching is on, group operations into agent sessions (conversations) across MCP/CLI sessions, link Codex immediately and Claude near-live, and make the dashboard and Optimization section work per agent session.

**Architecture:** Membership is derived per operation (`exact` link, `inferred`, `unassigned`) over existing `operation_agent_links` plus Codex `agent_session_id`; the dashboard matcher becomes event-driven and also freezes version labels. Agent-session annotations get their own table keyed by `(agent_kind, conversation_id)`. With matching off nothing changes.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, xUnit; React + Vitest.

**Spec:** [docs/superpowers/specs/2026-10-17-agent-sessions-design.md](../docs/superpowers/specs/2026-10-17-agent-sessions-design.md)

## Status and dependencies

- TODO. Planning base `6c37cd7`. Depends on 079 (journal floor), 080 (Optimization) and 087 (measure observations; treat them like compare observations in agent-session history and frozen labels). Plan 085 depends on this plan.
- Priority P2; effort L; risk MEDIUM.

## Global Constraints

- Membership values: `exact`, `inferred`, `unassigned`; inferred never feeds Optimization history or authorization.
- Inferred: unlinked operation in an MCP/CLI session whose exact links all point to one conversation and which lies between two exact links to it (leading/trailing unlinked operations stay `unassigned`); CLI session only when all MCP sessions sharing its `agent_pid` + `agent_started_at` point to one conversation.
- Codex/OpenCode: conversation from `operations.agent_session_id`; Claude: from transcript tool-use ids (existing matcher).
- Subagents: Claude folds by transcript `sessionId`; Codex subagent threads separate, labelled `subagent of unknown parent`, until the Task 0 spike says otherwise.
- Event-driven matching: on `PRAGMA data_version` change, debounce 1.5 s, targeted cycle; 60 s sweep stays.
- Version labels chronological per `(context, subject, scope)`; frozen by the matcher once all earlier operations of the context have final membership; UI note text `versions may renumber while matching catches up`.
- `Clear matched data` removes links and frozen labels, keeps agent-session annotations; retention removes agent-session annotations with the last operation.
- Header count text: `{e} exact · {i} inferred · {u} unassigned`; top-level label for unlinked sessions: `not matched`.
- Commits `feat(084): …`; `just verify`.

## Review Focus

1. Late Claude link for an early operation must not renumber frozen labels → Task 4 `Frozen_labels_survive_late_links`.
2. One MCP session linked to two conversations → operations split, unlinked ones `unassigned` under both, counted once → Task 2 `Shared_session_splits_and_counts_once`.
3. Turning matching off immediately restores per-session lists and scopes → Task 5 `Matching_off_restores_session_view`.
4. Codex thread whose index entry was read before its first operation → linked (cursor bug) → Task 1 `Codex_links_without_index_timing`.
5. `Clear matched data` keeps owner agent-session titles/stars → Task 3 `Clear_matched_keeps_agent_annotations`.
6. Owner-confirmed: `/new` in an agent keeps the same MCP session for the next conversation; until its first link lands, its operations must not be inferred into the previous conversation → Task 2 `New_conversation_in_same_session_not_inferred_into_previous`.

---

### Task 0: Drift check and Codex subagent spike
- [x] Confirm 079/080/087 merged; drift check; `just verify` baseline. Evidence: 079, 080, and 087 commits and required `FirstRetainedVersion` (18), `CompareHistoryBuilder`, and `OptimizationSummaryBuilder` are present on `main`; drift from planning base `6c37cd7` is 114 changed journal/dashboard files (8,128 insertions, 1,488 deletions), with no conflicting required interface. Baseline passed on `63fd52faf15aad531e5163268f8f50f15327bdd1`.
- [x] Spike (local, read-only, journal counts and one Codex subagent run by the owner): does `agent_thread_source` or any captured metadata carry a parent thread id? Result: unknown-parent evidence unavailable. The local journal has 11 operations, 0 with `agent_session_id` or `agent_thread_source`; the capture allowlist stores `thread_source` but no parent-thread identifier, and the operations schema has no parent-thread column (`parent_operation_ref` is an operation reference). Use the specified `subagent of unknown parent` fallback; no additional fold rule for Task 2. See `.superpowers/sdd/084-agent-sessions/task-0-report.md`.

### Task 1: Matcher fixes and event-driven cycles
**Files:** `src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs` (~`:232-313`), `src/SqlHarness.Dashboard/DashboardHost.cs` (~`:225-291`, housekeeping loop), `src/SqlHarness.Dashboard/LiveFeed.cs` (shared `data_version` signal); tests `tests/SqlHarness.Tests/Dashboard/TranscriptMatcherTests.cs` (create if absent), `DashboardHousekeepingTests.cs`.
**Interfaces:** `TranscriptMatcher.RunTargetedAsync(IReadOnlyCollection<long> operationIds, CancellationToken ct)`; Codex upsert `agent_conversations(agent_kind='codex'|'opencode', conversation_id=agent_session_id)` with null title, title filled by the index scan later.
- [x] Failing test: `Codex_links_without_index_timing` reproduced the skipped Codex thread after the session-index cursor had advanced; focused matcher/housekeeping tests then passed. Added fake-clock `Journal_change_triggers_targeted_cycle_within_debounce` (1.5 s), `Sweep_still_runs_every_60s`, and verified the existing read-budget test.
- [x] `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~TranscriptMatcher|FullyQualifiedName~DashboardHousekeeping"` → RED (28 passed, 1 failed on the new cursor regression) → GREEN (31 passed).
- [x] Commit `feat(084): link Codex immediately and match Claude on journal changes`.

### Task 2: Membership model
**Files:** create `src/SqlHarness.Dashboard/AgentSessions/AgentSessionMembership.cs`; modify `src/SqlHarness.Dashboard/JournalReader.cs`; tests `tests/SqlHarness.Tests/Dashboard/AgentSessionMembershipTests.cs`.
**Interfaces:** `sealed record AgentSessionKey(string AgentKind, string ConversationId)`; `enum Membership { Exact, Inferred, Unassigned }`; `static IReadOnlyList<OperationMembership> AgentSessionMembership.Resolve(IReadOnlyList<OperationIdentityRow> ops, IReadOnlyList<LinkRow> links)` with `sealed record OperationMembership(long OperationId, long SessionId, AgentSessionKey? Key, Membership Membership)`.
- [x] Failing tests: single-conversation MCP session → unlinked operations between exact links inherit `Inferred`; `Shared_session_splits_and_counts_once`; `New_conversation_in_same_session_not_inferred_into_previous`; CLI inferred only when all same-process MCP sessions agree; Codex by `agent_session_id`; Claude subagent lines fold into parent; Codex subagent separate with label. Added the JournalReader membership-input query fixture.
- [x] FAIL → implement → PASS. Focused membership tests passed 7/7; commit `feat(084): derive agent-session membership`.

### Task 3: Agent-session annotations and API
**Files:** `src/SqlHarness.Core/Journal/JournalSchema.cs` (step `agent_session_annotations(agent_kind, conversation_id, title, note, starred, archived, updated_at, PRIMARY KEY(agent_kind, conversation_id))`, `compare_version_labels(context_kind, context_key, subject, scope, sql_hash, label, PRIMARY KEY(...))`; next free version per 079), `src/SqlHarness.Dashboard/SessionAnnotations.cs` patterns, `DashboardServer.cs` (routes before `/{**rest}`), `DashboardModels.cs`; tests `JournalContractTests.cs`, `DashboardServerTests.cs`, `SessionAnnotationsTests.cs`.
**Interfaces:** `GET /api/agent-sessions?…` (list with children and counts), `GET /api/agent-sessions/{kind}/{id}`, `PATCH /api/agent-sessions/{kind}/{id}/annotation`; `GET /api/sessions` gains `matching: bool` to tell the UI which list to render.
- [x] Failing tests: list shape with children; title order owner → conversation → newest child; `Clear_matched_keeps_agent_annotations`; retention deletes annotations with the last operation; PATCH validation mirrors session annotations. Added shared-child unassigned duplication and subagent fallback coverage.
- [x] FAIL → implement → PASS. Focused API tests passed 6/6 and journal contract test passed; commit `feat(084): agent-session annotations and API`.

### Task 4: Optimization per agent session with frozen labels
**Files:** `src/SqlHarness.Dashboard/CompareHistoryBuilder.cs` (scope-key parameter), 080's `OptimizationSummaryBuilder.cs`/`OptimizationComparer.cs`, matcher freeze step in `TranscriptMatcher.cs`; tests `CompareHistoryBuilderTests.cs`, `OptimizationSummaryBuilderTests.cs`.
**Interfaces:** `CompareHistoryBuilder.Build(…, ContextKey context)` where `ContextKey` = agent session (exact members) or MCP session; `GET /api/agent-sessions/{kind}/{id}/optimization` and `/optimization/compare` mirroring 080's session endpoints.
- [x] Failing tests: versions across two MCP sessions of one conversation share one history with chronological labels; `Frozen_labels_survive_late_links`; inferred/CLI compares absent from agent-session history but present in their child session; renumbering note shown while unfrozen. Added tests for partial frozen prefixes and unknown-child pending links.
- [x] FAIL → implement → PASS. Focused optimization, matcher, and agent-session tests passed 89/89; commit `feat(084): optimization history per agent session`.

### Task 5: Dashboard UI
**Files:** `ui/src/pages/SessionsPage.tsx`, `ui/src/components/SessionTable.tsx`, `ui/src/pages/SessionPage.tsx` (agent-session variant), router, `ui/src/api/*`, Settings coverage panel in `ui/src/pages/SettingsPage.tsx`; tests alongside.
- [x] Failing tests: matching on → agent rows expandable to MCP/CLI children, `not matched` rows; agent page header counts text; membership markers; `Matching_off_restores_session_view`; Settings coverage counts by agent kind and transport and last cycle time. Added a matching-off route/cache regression and an agent-context SQL version route test.
- [x] `npm --prefix src/SqlHarness.Dashboard/ui test` FAIL → implement → PASS; build. Full suite passed 61 files / 319 tests; standalone Vite build passed. See Task 5 report. Commit `feat(084): agent sessions in the dashboard`.

### Task 6: Documentation and verification
- [ ] README (agent sessions, membership, matching latency, subagents), `plans/README.md`; `just verify`; queue → history; commit `docs(084): record plan 084 evidence`.
