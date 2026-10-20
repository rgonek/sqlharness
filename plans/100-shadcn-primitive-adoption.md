# Plan 100: shadcn Primitive Adoption

> **For agentic workers:** Use superpowers:subagent-driven-development or superpowers:executing-plans task-by-task. Obtain independent task review and whole-branch review before completion.

**Goal:** Adopt appropriate shadcn controls across the full current dashboard inventory while preserving behavior and explicit semantic exceptions.
**Architecture:** CLI-sourced, project-owned primitives; Select for current pickers, Collapsible for independent disclosures, multiple Accordion for the compare group, and existing Input/Button/Table for standard surfaces.
**Tech Stack:** React, TypeScript, Tailwind v4, shadcn/base-nova, Base UI, Vitest/Testing Library and userEvent.
**Spec:** [revised design](../docs/superpowers/specs/2026-10-18-shadcn-cli-and-primitive-adoption-design.md).
**Depends on:** [CLI restoration implementation](099-shadcn-cli-restoration.md) actually integrated, not merely its plan or branch ancestry.
**Status:** revised 2026-10-20; plan only.

## Global constraints and working convention

- Read spec, plans/README.md and docs/agents/verification.md. Implement in an isolated worktree; preserve unrelated work.
- Paths below are relative to src/SqlHarness.Dashboard/ui unless explicitly repository-root paths.
- Keep Base UI/base-nova/neutral. Use installed stable CLI from the predecessor, with reviewed registry diffs; no silent latest refresh or blanket overwrites.
- components/ui is owned code and can receive reviewed changes. Prefer defaults and composition; no exact old-size requirement.
- Preserve labels, domain identities, persisted preferences, resets, approval policy, callbacks and request contracts. No backend/SQL/live writes.
- No generic picker wrapper or form framework just for this migration. Simple Label/id is sufficient; Field is optional where help/error composition warrants it.
- Existing behavior tests are adapted only when markup/API changes. Add focused tests for identified behavioral hazards; no tests merely mirroring trivial Input/Button substitutions.
- Each task: run the named focused Vitest files with npm exec -- vitest run <paths>, run npm run typecheck and npm run lint, obtain independent review and resolve findings, then commit explicit files. Run the whole UI suite at integration points and final just verify; do not label scoped checks a full gate.
- Current inventory is 19 select, 11 details, 3 textarea, 3 checkbox, 4 radio, 2 text input, 9 button and 8 table source sites outside tests/components/ui. Refresh and classify drift before execution; a stale count never licenses silently skipping new controls.

## Review focus

1. Encoded values displayed instead of labels before popup opening: Tasks 4–7 test initial and updated readable text plus domain identity.
2. Null/undefined/empty-string confusion and disabled option bypass: Task 5 tests all states and both axis reset paths.
3. Selection reset chains lost in OptimizationSection or PlanWorkbench: Tasks 6–7 assert all resulting selections/requests.
4. Collapse unmounts editors or exposes hidden descendants: Task 3 tests hidden accessibility, independent nesting and preserved state after reopen.
5. Tooltip/dialog trigger composition loses focus or creates nested buttons: Task 8 tests hover/focus, activation, Escape and focus return.
6. Dense tables/buttons lose long content, native group semantics or approval safety: Tasks 9–10 test browser layout, table headers and retained behavior.

### Task 1: Add only the missing primitives

**Files:** src/components/ui/{checkbox,select,textarea,radio-group,accordion,collapsible}.tsx; package.json/package-lock.json and src/index.css only if the reviewed CLI diff requires changes.
**Interfaces:** Supplies the named exports from actual generated source. Do not assume Radix prop names, radio item names or future exports.

- [ ] Confirm predecessor implementation: installed shadcn version, shadcn/tailwind.css import, no vendored file, lockfile and evidence. Record execution revision and generation date.
- [ ] From UI root run npm exec -- shadcn add checkbox select textarea radio-group accordion collapsible --dry-run. Inspect proposed dependency/file changes; use add with --diff for existing files. Do not add Field merely to standardize labels.
- [ ] Apply only reviewed additions without --overwrite. If a shared file truly needs updating, review its callers and include its focused checks rather than silently retaining or overwriting it.
- [ ] Read exports, value callback types, render composition, mounting and animation APIs. Confirm Base UI imports and working aliases/lucide transforms; no accidental Radix, nonexistent IconPlaceholder or duplicated primitive package.
- [ ] Run npm run check and npm run build. Record source/diff provenance and any dependency changes. Review and commit; do not assert CSS must be unchanged because unused source may add classes.

### Task 2: Checkbox, Textarea, Input and diff RadioGroup

**Modify:** src/components/{InternalOperationsToggle,SqlDiff,SessionNoteCard,CopyButton}.tsx; src/pages/{SessionsPage,AgentSessionPage,NamedOperationsPage,OperationPage}.tsx.
**Tests:** src/components/{SqlDiff,SessionVersionDiff,CopyButton}.test.tsx; src/pages/{SessionsPage,OperationsPage,LivePage,AgentSessionPage,NamedOperationsPage,OperationPage}.test.tsx.
**Interfaces:** Checkbox checked/onCheckedChange boolean; Textarea/Input retain native ref/event contracts; RadioGroup uses actual generated exports and controlled DiffMode.

- [ ] Convert the three checkboxes with unique per-instance IDs and associated labels. Keep wording and preference storage; do not accidentally create tri-state product behavior.
- [ ] Convert all three textareas, including Agent session note. Preserve readOnly, maxLength, rows, ref, onFocus select-all, draft/save/cancel and errors.
- [ ] Convert named-operation search and owner note to Input. Use a visible Label/id association, preserving query trimming and note behavior.
- [ ] Convert only SqlDiff radios to a real RadioGroup, preserving its accessible name and controlled preference. Retain VersionTable native groups.
- [ ] Adapt affected tests; pin label-click toggle, two rendered diff instances remaining independent, ArrowRight selecting Unified and persisted mode behavior. Preserve tests for clipboard manual fallback/select-all and agent-note save/cancel.
- [ ] Run the listed focused suites, typecheck/lint, review and commit.

### Task 3: Independent Collapsibles and multiple compare Accordion

**Modify:** src/components/{CompareDetail,TableIoComparison}.tsx; src/components/optimization/{OptimizationSection,StatementRows}.tsx; src/pages/{OperationPage,SessionPage,ProfileDimensionsPanel}.tsx; primitive source only if a reviewed API passthrough fix is necessary.
**Tests:** src/components/CompareDetail.test.tsx; src/components/optimization/{OptimizationSection,StatementRows}.test.tsx; src/pages/{OperationPage,SessionPage,StatsPage}.test.tsx.
**Interfaces:** Three CompareDetail items in Accordion multiple; eight independent Collapsible source sites; keepMounted through installed Base UI API.

- [ ] Replace CompareDetail's three details with one Accordion multiple, stable item values and initially closed panels. Choose heading levels matching page hierarchy. Preserve all summary text.
- [ ] Replace OperationPage's three details, SessionPage metadata, TableIoComparison More, OptimizationSection Unassigned and StatementRows Objects with independent Collapsibles.
- [ ] Use keyed Collapsible defaultOpen for each dimension breakdown. Retained dimensions retain user-open state; newly appearing dimensions start open. Do not invent a shared controlled open-set.
- [ ] Preserve descendant mounting with keepMounted at the actual supported root/content location. Closed content must be hidden and unfocusable; no hiddenUntilFound side effect. Avoid extra Button triggers where the generated component already renders one.
- [ ] Rewrite structural details/summary assertions into user interactions and expanded/hidden outcomes. Add cases: all compare panels can stay open; every dimension starts open; closing one leaves others open; add/remove dimensions preserves retained choices.
- [ ] Add reopen-state coverage for SQL diff mode and nested More/Objects. Keep content mounted without exposing closed descendants to role queries or keyboard tab order. Confirm nested toggles do not close parent.
- [ ] Run focused suites/typecheck/lint. Browser-check editor layout after reveal, independent nested state, keyboard activation and reduced motion. Review and commit.

### Task 4: Straightforward Select pickers with readable values

**Modify:** src/components/{SessionVersionDiff,CompareDetail}.tsx; src/pages/{SettingsPage,StatsPage}.tsx.
**Tests:** src/components/{SessionVersionDiff,CompareDetail}.test.tsx; src/pages/{SettingsMutations,SettingsPage,StatsPage}.test.tsx.
**Interfaces:** Seven Select sites; explicit items label mapping; controlled values/callbacks preserve existing domain types.

- [x] Convert from/to versions, case/side, approval sound/default mutation mode and statistics profile. Use current Base UI generated API, not Radix snippets.
- [x] Provide items or a formatted SelectValue so initial closed triggers show human labels, never encoded version/hash JSON. Keep exact option identities, order and conditional/disabled behavior.
- [x] Put id/label association on the interactive trigger. Resolve Statistics profile's competing labels into one consistent visible/accessible name and deliberately update affected tests.
- [x] Use standard component sizes and responsive widths; add layout classes only where usability needs them. No h-9 parity requirement.
- [x] Replace selectOptions/fireEvent.change with trigger-open then option-click. Use screen for portal options. Assert initial/updated label and actual downstream case/version/preference, not just label text. Distinguish duplicate visible labels by identity without first-match hacks.
- [x] Cover persisted/initial values before opening the popup and missing/empty option transitions. Preserve number-input assertions in SettingsPage.
- [x] Run focused suites/typecheck/lint. Browser-check keyboard arrows, Enter/Escape, typeahead, focus return, long labels and viewport overflow. Review and commit.

### Task 5: Dimension filters and matrix controls

**Modify:** src/pages/ProfileDimensionsPanel.tsx.
**Tests:** src/pages/StatsPage.test.tsx (add a dedicated src/pages/ProfileDimensionsPanel.test.tsx only if isolating callbacks makes these assertions clearer).
**Interfaces:** Five Select sites; domain selected: string | null | undefined and onSelect retain their meanings; matrix axis/limit/metric contracts unchanged.

  - [x] Convert the dimension filter with explicit UI sentinel "all:" for undefined. Missing stays "missing:"; concrete stays "value:<value>", including empty concrete string "value:". Keep valueOption/parseValueOption; map only "all:" back to undefined. Domain null must not be used as both missing and unselected.
  - [x] Keep dynamic dimension labels and unique IDs; show All values/missing/value labels before first opening.
  - [x] Test selecting concrete value, missing, concrete empty string and All values produces four distinct domain outcomes/requests. Pin controlled callback behavior without weakening request assertions.
  - [x] Convert matrix limit, row, column and metric. Preserve numeric limit conversion, disabled column choice matching current row and both axis-swap/reset paths.
  - [x] Test disabled options cannot be selected by pointer/keyboard; test both axis changes and matrix metric results. Keep filter changes and matrix-cell selections working.
  - [x] Run focused suites/typecheck/lint, verify narrow layout, review and commit.

### Task 6: Optimization group and scope Selects

**Modify:** src/components/optimization/OptimizationSection.tsx.
**Tests:** src/components/optimization/{OptimizationSection,ComparisonPanel}.test.tsx.
**Interfaces:** Two Select sites preserve dynamic subject-based names, conditional rendering and search/navigation updates.

- [ ] Convert conditions group/scope using explicit labels and current controlled API. Keep their existing groups.length/scopes.length guards.
- [ ] Preserve the group-change scope reset and all associated clearing of A/B/case/aggregation selections. Read current handlers; do not infer the reset list solely from this document.
- [ ] Test changing to a group with different scopes selects the correct first scope and clears dependent state. Test scope changes, one-option hidden controls and dynamic accessible names.
- [ ] Preserve VersionTable's independent native A/B radio semantics and table-scoped assertions.
- [ ] Run focused suites/typecheck/lint, review and commit.

### Task 7: Previously omitted plan pickers

**Modify:** src/components/PlanChanges.tsx; src/components/plan/{PlanWorkbench,PlanStatementSwitcher}.tsx.
**Tests:** src/components/PlanChanges.test.tsx; create src/components/plan/PlanWorkbench.test.tsx for the picker reset chain. Reuse existing operation/compare integration fixtures where they already exercise plan selection.
**Interfaces:** Five Select sites; occurrence/document keys and numeric statement/repetition identities unchanged.

- [x] Convert occurrence, case, repetition, plan document and statement. Preserve conditional rendering, All plan documents option and labels for repeated/versioned occurrences.
- [x] Supply initial-value label mapping for all keys; document hashes may be part of existing descriptive labels but encoded keys must not leak as the entire selected text.
- [x] Preserve every current case/repetition/document/statement callback reset: selected node, SQL selection/reveal and downstream document/statement selection as applicable. Do not normalize handlers into one generic reset if their behaviors differ.
- [x] Add tests selecting each picker and asserting resulting document/statement/viewer state, including repeated statement IDs across documents, empty/omitted plans and changed available options. Pin case/repetition reset chains with an actual selected node/SQL span before change.
- [x] Run focused suites/typecheck/lint and relevant OperationPage/CompareDetail cases. Browser-check long labels and popup sizing. Review and commit.

### Task 8: Correct tooltip/dialog trigger composition

**Modify:** src/components/StatusBadge.tsx.
**Tests:** src/components/StatusBadge.test.tsx.
**Interfaces:** Same StatusFields props; one composed button triggers the uncontrolled Dialog and Tooltip.

- [ ] Move the Dialog root to contain both the tooltip/trigger and DialogContent. Compose TooltipTrigger render with DialogTrigger on one element, following Base UI composition. No sibling trigger outside its Dialog context, nested button or copied Radix asChild pattern.
- [ ] Remove manual open state only after correct root ownership. Preserve error/cancel content, badge tiers, accessible trigger/dialog names and effective dialog popup semantics.
- [ ] Test hover and focus show tooltip; Enter/click opens dialog; Escape closes it and returns focus to the same trigger. Check one effective button and multiple badges remaining isolated.
- [ ] Run focused tests/typecheck/lint, browser-check focus/tooltip lifecycle, review and commit.

### Task 9: Standard actions with Button; explicit domain exception

**Modify:** src/pages/{NamedOperationsPage,ProfileDimensionsPanel}.tsx; src/components/plan/{PlanGraphView,PlanErrorState,PlanPropertiesPanel}.tsx.
**Tests:** src/pages/{NamedOperationsPage,StatsPage}.test.tsx; src/components/plan/PlanGraphView.test.tsx; relevant existing OperationPage/CompareDetail plan fixtures.
**Interfaces:** Seven bare action buttons adopt existing Button; the rich graph operator-node button remains native.

- [ ] Convert named-operation retry/load-more, fit-selection, subtree expand/collapse, plan-error retry, source-match action and matrix-cell action according to the spec's variants/layout choices.
- [ ] Keep type=button, disabled/pending, aria labels/pressed state, title/source hints, event stopPropagation and navigation/callback behavior. Source-match content must wrap; numeric matrix cells keep alignment.
- [ ] Keep the rich multi-line operator-node button with a documented domain-layout exception; do not turn it into an undersized single-line action.
- [ ] Reuse behavior tests for load-more/retry/pending, matrix selection, fit-selection disabled and subtree toggles. Add a test only for a previously uncovered behavioral hazard, not each mechanical Button substitution.
- [ ] Run focused tests/typecheck/lint; browser-check React Flow pointer/keyboard interactions and dense/narrow layouts. Review and commit.

### Task 10: Ordinary tables; preserve semantic matrix and radio groups

**Modify:** src/pages/{OperationPage,SettingsPage}.tsx; src/components/optimization/{VersionTable,StatementRows,ComparisonPanel}.tsx; src/mutations/ApprovalDialog.tsx.
**Tests:** src/pages/{OperationPage,SettingsPage}.test.tsx; src/components/optimization/{OptimizationSection,StatementRows,ComparisonPanel}.test.tsx; src/mutations/Approval.test.tsx.
**Interfaces:** Seven Table-family conversions retain native table semantics; matrix table and VersionTable native radios remain explicit exceptions.

- [ ] Convert phase timing, coverage, versions, both statement/object IO, comparison IO and approval parameter tables using existing Table/TableHeader/TableBody/TableRow/TableHead/TableCell.
- [ ] Preserve scope=row via TableHead in tbody, column headers, colSpan, accessible names, unknown metrics, all parameter values and links. Do not add sorting/filtering/TanStack Table solely for this change.
- [ ] Account for Table's overflow wrapper; avoid redundant nested scrolling. Long SQL/parameters must remain readable and selectable.
- [ ] Retain VersionTable's two independent radio name groups and keyboard behavior. Preserve native matrix sticky/separate-border table; do not force radiogroup containers into table rows.
- [ ] Run existing table and approval tests. Assert A/B selection remains independent and approval parameter display/actions retain their existing policy behavior using fixtures only; never supply approval or mutate a live target.
- [ ] Browser-check long values, many columns, row headers, sticky matrix scrolling and approval dialog containment. Run focused tests/typecheck/lint, review and commit.

### Task 11: Inventory closure, integrated review and Windows gate

**Files:** README.md if affected; repository-root plans/100-verification-evidence.md, plans/README.md and plans/history.md when work lands.
**Interfaces:** Evidence proves the revised spec and records exceptions without claiming native HTML is forbidden.

- [ ] Re-scan production TSX outside tests/components/ui for select/details/summary/textarea/input/button/table and classify every remaining source site. Expected explicit exceptions: two VersionTable radio sites, one rich graph button and one matrix table. Native markup inside owned primitives, legitimate labels/anchors/fieldsets and library output remain valid.
- [ ] Compare inventory against the spec and identify any changes from intervening work. Unplanned standard controls must be converted or explicitly reviewed as exceptions, not silently omitted.
- [ ] Inspect dependency/source diffs for accidental primitive-library mixing, unreviewed overwrites and unrelated changes. Confirm local shadcn version and stylesheet export remain correct. Record registry source generation evidence.
- [ ] Run npm run check, npm run build and capture full/production npm audit results. Explain actual dependency/advisory changes; no fixed count or emitted CSS size requirement.
- [ ] Complete browser fixture walk-through in both themes and narrow layouts: labels, keyboard selection/typeahead, popup collision/scroll, all-open compare/dimensions, nested reopen state, editor sizing, radio groups, reduced motion and StatusBadge focus return. Record real observations/screenshots and outstanding gaps.
- [ ] Obtain whole-branch independent review against the spec and repository requirements; fix blockers. Task reviews alone are insufficient for cross-task state behavior.
- [ ] From repository root run just verify on the final product tree and require exit 0. Record exact revision/logs and diagnostics. Follow docs/agents/verification.md for reruns/reuse; Linux only when requested.
- [ ] Write evidence with inventory before/after, exceptions, dependency provenance, review results, focused checks, full gate and browser results. Update index/history accurately; no placeholder numeric evidence paths, automatic merge or push.
- [ ] Check documentation links and git diff --check, then commit only task files. Completion requires all acceptance evidence; final report distinguishes implementation, verification, local integration and publication.
