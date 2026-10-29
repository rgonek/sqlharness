# Dashboard screenshot detail Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Read the extracted task brief and spec. Tasks execute serially because shared UI/API/CSS files overlap.

**Goal:** Address all fifteen screenshot comments and restore circular A/B visuals while keeping truthful statistics and mutation safety.

**Architecture:** Reuse installed shadcn Base UI/Mira components and existing live feed. Add bounded read-only dashboard projections only where requested data exists in the journal; no schema changes or backfill. Keep original mutation payload authoritative and preview rendering separate.

**Tech Stack:** React/TypeScript, shadcn/Base UI/Hugeicons, C# dashboard journal queries, Vitest/xUnit, Playwright fixtures.

**Spec:** [operator screenshot requirements](../docs/superpowers/specs/2026-10-28-dashboard-detail-corrections.md). Current base `3f95e9b9`, existing worktree `C:/Users/rgone/.codex/worktrees/f735/sqlharness`.

**Status:** DONE / VERIFIED on b3e921cd (2026-10-29). Windows gate PASS: UI436, Core4774/11 skips, MCP535/4 skips; branch C# format5 PASS, browser104 PASS. Two stale test contracts corrected and independently reviewed; runtime bbda23d2 unchanged. [Evidence](121-evidence/layout-polish/screenshot-details/report.md), [gallery](121-evidence/layout-polish/screenshot-details/index.html). Local branch only, no merge/push/publication/restart.

## Global Constraints

- Existing preset/fonts/icons; consult shadcn skill and official CLI docs for changed components. Use standard variants before custom markup. User explicitly authorizes joined KPI geometry and visual A/B circles.
- Preserve unrelated edits, mutation/credential policy, approval lifecycle, paging/filter/navigation and native radio semantics.
- Read-only journal projections permitted; no migrations/collection changes/backfill; no fabricated metrics/requester/costs.
- No merge/push/publication/installed restart or live decision. Work with fake browser API.
- Commit task-owned paths after focused checks; final full gate is controller-owned.

## Review Focus

- SSE invalidation reaches loaded Session operation pages and running transitions; compact descriptions do not remove evidence access.
- A/B custom circles preserve native keyboard, focus and instance isolation; row surfaces have no nested button behavior.
- Stats filters/ranking/unknown metrics/zero denominators and multiple metric rows do not duplicate operations or fabricate totals.
- Mutation source correlation never invents requester and inline preview cannot change executed SQL or replace inside lexical literals/comments.
- Toast Deny and dialog modes preserve one-second approval guard, expiry, busy/error handling, accessible labels and mobile reflow.

## Task 1: Session, Operation, IO and Settings visual corrections

**Files:** UI SessionOperationsTable/OwnerStarButton/StatusBadge, VersionTable, IoComparison, OperationPage and associated components, SessionNoteCard, SettingsPage, scoped index.css and focused tests. Do not modify StatsPage, mutation files or backend.

- [x] Read spec items1-6,10-13,16, current mocks and source; use shadcn skill/official docs Button/Table/Tabs/Field/ToggleGroup.
- [x] Add focused regression coverage for compact row label/star/spark/running, single-extra IO column toggle, native A/B selection and distinct instances, matching Details, and preservation of Settings validation/confirmation. Reuse SSE Session tests; verify auto-refresh via real cache invalidation path, not polling assertions that mirror implementation.
- [x] Make Session descriptions one compact line without repeated target prose; retain full label/SQL/target through title or existing detail navigation. Time HH:mm:ss with full timestamp accessible. Larger Hugeicons star action, spark for keep, running Badge. Preserve flags, returnSession, internal toggle and pagination.
- [x] Place version label inline with vN. Restore visible circular A/B labels over real native radio inputs, preserving focus/keyboard and semantics. Use existing variant utilities/semantic colors rather than raw theme skins.
- [x] Expose the sole IO extra (LOB logical reads in statement view) beside Scans; keep multi-option whole-batch selection if useful. Statement disclosure inherits row background, hover and focus preserve whole-row behavior.
- [x] Align Operation tab/parameter header and Details with Session. Avoid global toolbar/overlay regressions.
- [x] Add Play/Refresh/Plus icons to Preview/Refresh/Add note; keep accessible names/workflows. Improve retention Field inset and right-align transcript clear action responsively.
- [x] Run relevant focused tests/typecheck/lint/build; self-review and commit only owned paths. Report beside brief with exact tests, commits and screenshot disposition. No full gate or plan/index changes.

## Task 2: Truthful mock-like Usage KPIs and Top SQL logical reads

**Files:** StatsPage/helpers/tests, dashboard stats DTOs/JournalReader and focused xUnit tests, UI types/fixtures as needed, affected dashboard docs. Preserve Task1 changes.

- [x] Read spec items7-9 and mock Usage KPI/formulas. Inspect retained stats and operation_metrics schema before deciding aggregation. Use shadcn Card/Table/Separator/RadioGroup docs.
- [x] Test failed/rejected percentage including zero operations, real distinct session count if added, missing vs zero logical reads, multiple metric rows avoiding operation count/time fanout, top logical-reads ranking outside previous count/time top lists, and all existing scope/range/profile/dimension filters. Existing populated old-session rows remain on original contracts; no writes/backfill.
- [x] Add bounded read-only logical-reads projection per SQL group and a real top-by-reads query/list. Define aggregation clearly (recorded representative metric medians are not total physical executions); include coverage/unavailable behavior so partial/missing data is not shown as complete zero. No schema changes or N+1 per-row calls.
- [x] Add Logical reads column and ordering to Top SQL. KPI joined section uses standard Card/Separator/layout as far as possible, useful real secondary info and failed+rejected/total percentage; never use mock constants, fake sessions, chat tokens or estimated price without source. Measured workflow output may use actual recorded estimated tokens with clearly scoped labeling, but no invented model costs.
- [x] Match KPI content hierarchy: failed/rejected counts remain the main value, percentage is secondary as in the mock. When retained workflow-output token estimates exist, they can be the output KPI's main value with an explicit estimated/measured-tool-output label and bytes as secondary detail; no implication of total chat usage/cost. Keep session subtitle concise (actual session count without internal query jargon).
- [x] Remove the Detailed usage and workflow accounting disclosure AND its UI contents, keeping underlying APIs untouched. Remove unused imports/helpers only where no longer used; do not remove unrelated dimensions/table views.
- [x] Update affected authoritative dashboard contract/docs for additive fields and aggregation semantics. Run focused .NET stats tests and UI Stats tests/typecheck/lint/build. Commit only owned paths; report exact semantics/coverage/tests. No full gate or plan/index edits.

## Task 3: Mutation toast, requester context and SQL display modes

**Files:** ApprovalToast/Dialog and tests, mutation UI store/types, small SQL display helper or additive backend projection, MutationEndpoints/Broker pending view and JournalReader/types/tests if exact context projection needed. Preserve Stats changes in shared files. Do not change Core execution/classification/digest semantics or authorization.

- [x] Read spec items14-15 and mutation mock1b; use shadcn Toast/Dialog/RadioGroup/Button docs. Inspect actual pending DTO, prepared SessionKey/OperationRef and existing journal linkage. Source actor/session only from the retained journal correlation; do not guess from other pending/live operations. Preserve unknown state honestly.
- [x] Add focused tests for toast SQL preview/Review/Deny, deny pending/busy/error/expiry, dialog original SQL/parameters/digest preservation, actor/link known/unknown, modes and 1s/Enter guards. Fake API only, no real decisions.
- [x] Toast uses existing Base Toast with plain prominent environment, compact target/SQL, Review and Deny. Use existing postMutationDecision path; no auto-approval/denial. Keep errors visible and do not falsely dismiss failed requests.
- [x] Dialog header replaces generic Review prose with target/context, including actual agent label and session link when available. Move presentation without changing policy alerts or classification.
- [x] Add Values inline / As sent using standard single-choice control, preserving existing formatting/copy affordances as useful. Keep original SQL/parameters/digest immutable. Reuse trusted literal/token machinery if available; otherwise implement a small tested dialect-aware display renderer with safe fallback. Cover quoted strings/comments/identifiers, nested block comments, SQL Server @@ variables, parameter name prefixes, escaping/null/bool/numeric/text and PostgreSQL dollar quoting. Unknown/ambiguous values never get a misleading preview; keep As sent accessible and explain limitation. Display mode never influences submit payload.
- [x] Update affected dashboard-only DTO/docs if needed; no MCP startup/request-target changes, credential reads or DB writes. Run focused .NET mutation/context tests and UI mutation tests/typecheck/lint/build; self-review and commit owned paths. Report actor provenance/preview limits and exact tests. No full gate or plan/index edits.

## Task 4: Controller verification and report

- [x] Independent spec/quality task reviews, then bounded combined review and unified fix wave if needed.
- [x] Fresh desktop/mobile fixture screenshots and interactions for all sixteen requirements; verify no app errors/overflow, safely mocked mutation decisions, screenshot disposition report with live-refresh answer and data limits.
- [x] Final just verify on final product tree; inspect failures, retain exact logs and source identity. No Linux/live acceptance claim.
- [x] Update plan/Plan121/index/history and report at `121-evidence/layout-polish/screenshot-details/report.md`; commit reviewable evidence. Keep branch locally without integration/publication.
