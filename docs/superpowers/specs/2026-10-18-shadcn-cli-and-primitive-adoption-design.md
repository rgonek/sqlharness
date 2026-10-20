# Design: shadcn CLI and primitive adoption

**Created:** 2026-10-18; **revised:** 2026-10-20.
**Status:** implemented and verified on feature branch `plan-100` at product revision `3caacc3`; all task and whole-branch reviews approved. Locally integrated into `main` at `74f248c` with the verified product tree unchanged.
**Scope:** dashboard UI, build dependencies, contributor documentation and evidence. No backend, MCP, database, mutation approval policy or data-contract changes.

## Outcome and revised decisions

Adopt current stable shadcn tooling and accessible owned component source. Replace hand-styled standard controls while preserving domain behavior. Prefer component defaults and composition over reproducing every old height, border and padding.

This revision supersedes the original frozen-4.22.0, immutable-generated-source, exact-CSS-size and accordion-for-every-disclosure requirements. The owner requested current generally available patterns and a critical review of repository conventions.

1. Keep Base UI/base-nova, neutral theme and the existing React/Vite/Tailwind stack. This is supported, not a universal preference over Radix. Do not introduce Radix accidentally; no ecosystem-wide dependency ban is implied.
2. Resolve the latest stable shadcn release once at execution start, install as a devDependency, and record its version/lockfile. The npm latest tag returned **4.22.1 on 2026-10-20**. Exclude beta/rc/canary. Use the installed CLI throughout both plans; do not silently re-resolve latest between tasks.
3. Registry source can change independently of CLI versions. Preserve generation date, preview and committed source; the lockfile alone does not pin registry output. Preview changes, review existing-file diffs, and never blanket overwrite, init, apply or add --all.
4. Import shadcn/tailwind.css normally and remove the vendored workaround after checking variant/utility coverage. devDependency means build tooling, not proof of advisory unreachability. Refresh audits; no hard-coded advisory allowlist or audit fix --force.
5. components/ui is project-owned source, explicitly customizable under shadcn's open-code model. Start from CLI output and review edits. Prefer variants/className/render for local needs; shared fixes can edit shared source. No wrapper merely to evade a prohibition on editing generated code.
6. Accept default shadcn control styling while preserving usable widths, overflow, dense data layouts, focus visibility and both themes. No pixel parity with hand-styled controls and no unrelated theme redesign.
7. Native HTML is legitimate. shadcn itself emits native elements. Keep number/file input behavior, links, labels, fieldsets and specialized semantic structures. Completion means a classified inventory, not zero native tags.
8. Simple Label/id is sufficient for simple controls. Field is appropriate for grouped label/help/error content, not mandatory everywhere. No form framework or TanStack Table solely for styling.
9. These decisions supersede conflicting generated-file/no-style rules in the 2026-10-06/07/08 dashboard designs/plans and Plan 071 for this work. Keep historical results intact; update current contributor guidance and link this supersession.

## Sources and evidence boundaries

Primary sources checked on 2026-10-20:

- [shadcn open code and customization](https://ui.shadcn.com/docs)
- [CLI preview and diff](https://ui.shadcn.com/docs/cli)
- [Base UI render composition](https://base-ui.com/react/handbook/composition)
- [Select](https://ui.shadcn.com/docs/components/base/select), [Base UI Select label/value mapping](https://base-ui.com/react/components/select)
- [Combobox](https://ui.shadcn.com/docs/components/base/combobox), [Native Select](https://ui.shadcn.com/docs/components/base/native-select)
- [Collapsible](https://ui.shadcn.com/docs/components/base/collapsible), [Accordion](https://ui.shadcn.com/docs/components/base/accordion)
- [Base UI Collapsible](https://base-ui.com/react/components/collapsible), [Base UI Accordion](https://base-ui.com/react/components/accordion)
- [Field](https://ui.shadcn.com/docs/components/base/field)

These establish supported patterns, not universal community consensus. npm registry query: npm view shadcn dist-tags --json. A read-only 4.22.1 add dry-run for checkbox/select/textarea/radio-group/accordion/collapsible/field reported seven new files, label unchanged, separator overwrite and cn dependency. Therefore do not assume add never changes existing files. Field is optional and excluded from the initial adoption batch.

Old 2026-10-18 audit counts, byte counts and generation observations are historical, not acceptance criteria. Recount source sites and refresh external facts when execution starts.

## Complete inventory

Paths below are relative to src/SqlHarness.Dashboard/ui/src. Counts exclude tests and components/ui and refer to source sites, not rendered instances.

| Existing element | Sites | Decision |
|---|---:|---|
| select | 19 | shadcn Select |
| checkbox input | 3 | Checkbox |
| textarea | 3 | Textarea |
| SqlDiff radio input | 2 | RadioGroup items |
| VersionTable radio input | 2 | Retain native independent A/B groups across rows |
| details/summary | 11 | CompareDetail: 3 Accordion items; remaining 8: Collapsible |
| text input | 2 | Existing Input |
| bare button | 9 | 7 Button actions, 1 retained graph node surface, 1 composed StatusBadge trigger |
| table | 8 | 7 Table-family conversions; specialized matrix retained |

### All 19 pickers

| File | Count | Contract |
|---|---:|---|
| pages/ProfileDimensionsPanel.tsx | 5 | dimension filter; matrix limit/row/column/metric |
| pages/SettingsPage.tsx | 2 | approval sound; mutation default mode |
| pages/StatsPage.tsx | 1 | profile |
| components/CompareDetail.tsx | 2 | case; side |
| components/SessionVersionDiff.tsx | 2 | from/to version, encoded version/hash identity |
| components/optimization/OptimizationSection.tsx | 2 | conditions group; scope |
| components/PlanChanges.tsx | 1 | occurrence |
| components/plan/PlanWorkbench.tsx | 3 | case; repetition; plan document |
| components/plan/PlanStatementSwitcher.tsx | 1 | statement |

Use explicit items/label mapping or formatted SelectValue, including before first popup opening. Never show raw JSON, fingerprints or encoded dimension values as selected text. Retain stable IDs and explicit numeric conversions. Treat nullable callbacks deliberately; no non-null assertions hiding empty-list transitions.

Dimension filter meanings remain undefined = All values, null = missing, string = concrete value (including empty string). Keep valueOption/parseValueOption domain encoding and use an explicit UI All-values sentinel distinct from missing and concrete values.

Preserve disabled matrix choices, axis swaps, conditional picker visibility, group/scope resets and every workbench selection reset. Give each trigger one clear accessible name and associated visible label. Preserve established names where useful; do not retain competing labels just to satisfy old tests.

Select is the deliberate choice for these current single-value pickers. Combobox is a supported alternative when a real fixture demonstrates a search need for long user-defined lists; no arbitrary threshold or automatic component switching. NativeSelect is also legitimate shadcn; native semantics are not themselves a bug.

### Forms, buttons and tables

- Checkbox: pages/SessionsPage.tsx, components/InternalOperationsToggle.tsx, components/SqlDiff.tsx. Boolean callbacks, unique per-instance IDs, labels and persisted preferences remain.
- Textarea: components/SessionNoteCard.tsx, components/CopyButton.tsx, pages/AgentSessionPage.tsx. Preserve refs, rows, maxLength, readOnly, focus-select-all, draft/save/cancel/errors.
- Input: pages/NamedOperationsPage.tsx search; pages/OperationPage.tsx owner note. Preserve callbacks and accessible names.
- RadioGroup: components/SqlDiff.tsx only; preserve arrow selection, per-instance isolation and DiffMode persistence.
- VersionTable native radios remain: two independent name-based groups across rows. Do not create isolated RadioGroup items, apply radiogroup to table/tbody, or use display:contents tricks. Preserve valid table and keyboard semantics.
- Button: NamedOperationsPage retry/load-more (outline); PlanGraphView fit-selection (outline) and subtree expand/collapse (ghost/dense); PlanErrorState retry (outline); PlanPropertiesPanel source-match action (wrapping content); ProfileDimensionsPanel matrix cell action (ghost, full-width numeric alignment).
- Retain PlanGraphView's rich multi-line operator-node native button as a domain surface with existing keyboard/event/React Flow behavior.
- StatusBadge: Dialog root contains Tooltip and DialogTrigger. Compose TooltipTrigger render with DialogTrigger onto one button; use Button via render only if styling requires it. No nested buttons. Preserve tooltip, dialog, focus return and Escape; remove manual open state after root/trigger structure is correct.
- Table conversions: pages/OperationPage.tsx phase timings; pages/SettingsPage.tsx matching coverage; components/optimization/VersionTable.tsx; components/optimization/StatementRows.tsx (2); components/optimization/ComparisonPanel.tsx IO; mutations/ApprovalDialog.tsx parameter preview.
- Use TableHead scope=row where required; retain colSpan, names, links, missing metrics and scroll containment. Table emits an overflow wrapper: remove redundant wrappers and test containment.
- Keep ProfileDimensionsPanel's matrix table native: sticky row headers, separate borders and specialized scrolling justify it. Preserve row/column headers.
- ApprovalDialog is presentation only. No approval dispatch/policy changes or self-approval.
- Do not replace anchors with buttons, every div with Card/ScrollArea, or every title with Tooltip. No toast, pagination, chart or form-framework expansion.

### Disclosures and retained state

- CompareDetail: one Accordion, explicit multiple, three items initially closed.
- ProfileDimensionsPanel: independent keyed Collapsible defaultOpen per dimension. New dimensions start open; retained keys preserve user choices. No shared controlled open-set is needed.
- OperationPage: three independent Collapsibles.
- SessionPage, TableIoComparison, optimization/OptimizationSection, optimization/StatementRows: one Collapsible each.

Use installed Base UI keepMounted on content/root as appropriate to preserve descendant state like native details. Closed descendants must be hidden, unfocusable and absent from the accessibility tree. DOM retention and accessibility visibility are separate requirements. Do not enable hiddenUntilFound incidentally.

Nested More/Objects remain independent. Closing/reopening must retain SQL diff mode, case, plan/editor state and nested expansion. Verify editor sizing on reveal. Use upstream animations; check reduced motion rather than implementing hand-written height transitions.

## Verification and delivery

Use role/name and observable-outcome tests. Select interactions open the trigger and choose via screen (portal); assert readable initial and updated labels plus actual domain selection. Duplicate labels need unique identity or popup scoping, not first-match queries. Retain toHaveValue for genuine inputs. Replace details/summary/open assertions with expanded/hidden behavior and reopen-state checks.

Both implementation plans require task review, whole-branch review and final Windows just verify on the final product tree under [verification](../../agents/verification.md). Linux only when requested. Browser evidence covers both themes, keyboard navigation, narrow layouts, popup overflow, retained state, editor reveal, reduced motion and dialog focus.

Record CLI/version/registry provenance, actual audit diff, classified inventories, focused tests, gate revision, reviews and browser evidence in named evidence files under plans/. No fixed CSS size, advisory count or pristine-console claim.

1. [CLI restoration](../../../plans/099-shadcn-cli-restoration.md): tooling/import/current contributor workflow.
2. [Primitive adoption](../../../plans/100-shadcn-primitive-adoption.md): full inventory; requires CLI implementation integrated first.

Index: plans/README.md. Document revision does not authorize implementation, integration, push or live database interaction.
