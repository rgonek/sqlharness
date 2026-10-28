# Dashboard default-shadcn alignment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Read the task brief and linked spec; retain prior dirty hover work.

**Goal:** Resolve the six dashboard audit findings while minimizing divergence from default shadcn and explain residual mock differences.

**Architecture:** Keep existing Base UI/Mira components and backend contracts. Correct form/selection semantics, introduce a context-specific compact operations presentation, and consolidate only the audited layout overrides.

**Tech Stack:** React, TypeScript, shadcn base-mira/Base UI, Tailwind v4, Hugeicons, Vitest, Playwright fixtures.

**Spec:** [approved scope](../docs/superpowers/specs/2026-10-28-dashboard-default-alignment.md), derived from the operator's explicit instruction and [audit](121-evidence/layout-polish/mock-audit-20261028/report.md).

## Global Constraints

- Prefer existing default shadcn components/variants; preserve preset, font and icons.
- Preserve pagination/filter/retry/navigation, annotation, mutation and credential policy; no backend expansion or backfill.
- Preserve prior dirty hover changes and evidence. Commit only task-owned paths after focused checks; full gate applies to final product tree.
- No merge, push, publication, installed dashboard restart or real mutation decision.
- Worktree: `C:/Users/rgone/.codex/worktrees/f735/sqlharness`.
- Read `shadcn` skill, CLI info/docs and official API content before modifying relevant components. Check installed components first.

## Review Focus

- Settings label click, local and server validation errors, invalid values must not save; descriptions/errors remain accessible.
- Exclusive A/B selection across table rows; correct table HTML and keyboard behavior without cross-session group leakage.
- Compact operation links retain return context; empty/internal/error/paged sessions retain existing workflows and useful flags.
- Long titles, profile identifiers and mobile widths do not overflow the document.
- CSS consolidation does not remove explicit operator decisions or force default internals into custom pixel skins.

## Task 1: Correct Settings and A/B selection semantics

**Files:** `src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx`, `src/SqlHarness.Dashboard/ui/src/components/optimization/VersionTable.tsx`, related focused tests, optionally a small dedicated A/B selector helper if justified.

- [ ] Read the spec, audit F1/F2 and existing test/API patterns; consult official Field/Switch/RadioGroup docs using the shadcn skill.
- [ ] Add meaningful failing tests for visible switch label click, description/error linkage and invalid-number no-save behavior; exclusive A/B keyboard selection across versions.
- [ ] Compose Settings with installed Field/FieldLabel/FieldDescription/FieldError and explicit IDs/ARIA relationships. Preserve descriptions, restart tags, sound controls, save/confirmation rules and responsive layout. Avoid blanket typography/color overrides.
- [ ] Replace independent Toggle A/B with exclusive groups. Prefer default RadioGroup if valid composition across columns is feasible; otherwise native radios are permitted for valid table markup. Preserve circular intent without custom reimplementation of radio keyboard logic. Name each group and each version unambiguously. Do not insert div wrappers inside table structure or use invalid nested group wrappers. Document the chosen default-vs-mock tradeoff.
- [ ] Run focused tests, typecheck and lint, inspect diff, commit only task-owned source/test files. Write report to the brief-adjacent task-1-report.md including changed paths, tests and tradeoffs. Do not run full gate or change plan/index.

## Task 2: Align Session/Profiles presentation and simplify audited CSS

**Files:** SessionPage.tsx, ProfilesPage.tsx, OperationTable.tsx or a new SessionOperationsTable.tsx, index.css, related tests; optional generated breadcrumb.tsx via official CLI if needed. Paths are under `src/SqlHarness.Dashboard/ui/src/`.

- [ ] Read spec, audit F3–F6 and task-1 report. Consult official Card/Table/Breadcrumb/Alert/InputGroup docs using the shadcn skill.
- [ ] Add focused regression coverage for compact session operations links/return context, internal filtering and load-more preservation; readable known profile values with unknown fallback. Reuse existing tests where they already pin behavior.
- [ ] Replace broad Session operations presentation with compact mock-like columns using default Table: ID/time/transport/type/description/status/duration/reads and useful existing flags/keep workflow as supported by current data. Omit duplicated owner/session/target context, preserve links and details access, pagination/loading/error/retry and show-internal. No invented SQL or metrics. Use CardHeader/CardTitle/CardDescription/CardAction as useful; no forced empty subcomponents.
- [ ] Add readable TLS/mutation/writer labels and accurate brief explanations using current data; preserve raw unknown values and explicit no-approval warning. No policy changes. Use default shadcn feedback where semantically appropriate.
- [ ] Align Session container with mock max-width 1200px and 24px desktop inset while retaining mobile reflow. Add Activity/session breadcrumb with default shadcn component if available (install explicitly from @shadcn if absent) or a semantically correct simple nav if registry unavailable, documenting limitation.
- [ ] Replace the visually stripped Session heading Card with semantic header and consolidate audited CSS selectors, removing redundant/!important skins where possible. Favor default Card geometry for real cards instead of matching custom mock internals. Preserve approved Switch/Conversations/hover and do not clean unrelated styles.
- [ ] Run focused tests, typecheck/lint and UI build; inspect diff; commit task-owned paths only. Write report to brief-adjacent task-2-report.md including exact residual differences and recommendations. Do not run full gate or change plan/index.

## Task 3: Controller verification and delivery

- [ ] Independent spec/quality review after each task; fix actionable findings before proceeding.
- [ ] Fresh desktop/mobile fixture screenshots and interactions for all affected views and mutation safety controls; compare to mocks. No live decisions.
- [ ] Independent final review of combined changes and residual tradeoff report.
- [ ] Run `just verify`, inspect actual output and retain logs. No Linux gate. Preserve unrelated shutdown-test failure evidence; never call an isolated retry a full pass.
- [ ] Update this plan, Plan 121/index and write `121-evidence/layout-polish/default-alignment/report.md` with coverage and a per-area table: mock difference, default choice, reason, whether custom parity is worth it. Include data limitations and deferred backend issues separately.
