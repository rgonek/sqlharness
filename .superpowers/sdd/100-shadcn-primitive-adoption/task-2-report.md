# Plan 100 Task 2 Report

- Branch: `plan-100`
- Starting revision: `01c2176f5fa2d87bbf60b49ef410295307b713bd`
- UI root: `src/SqlHarness.Dashboard/ui`

## Changes

- Replaced the session and internal-operation checkboxes with controlled Base UI `Checkbox` primitives and per-instance IDs connected to visible `Label` elements.
- Replaced the three textareas with the generated `Textarea`, retaining values, limits, rows, read-only state, refs, focus selection, and note save/cancel behavior.
- Replaced named-operation search and operation owner note fields with `Input`; named-operation search keeps its trimmed query behavior and has a visible label/id association.
- Replaced only SQL diff mode radios with the generated controlled `RadioGroup` and `RadioGroupItem`. VersionTable radios remain native.
- Adapted checkbox, search, and diff tests. Added coverage that multiple diff radio groups remain independent; existing ArrowRight and stored-mode checks remain active.

## Verification

- `npm exec -- vitest run src/components/SqlDiff.test.tsx src/components/SessionVersionDiff.test.tsx src/components/CopyButton.test.tsx src/pages/SessionsPage.test.tsx src/pages/OperationsPage.test.tsx src/pages/LivePage.test.tsx src/pages/AgentSessionPage.test.tsx src/pages/NamedOperationsPage.test.tsx src/pages/OperationPage.test.tsx` — passed, 9 files and 80 tests. Raw log: `.superpowers/sdd/100-shadcn-primitive-adoption/scratch/task-2/focused-vitest-final.log`.
- `npm run typecheck` — passed. Raw log: `.superpowers/sdd/100-shadcn-primitive-adoption/scratch/task-2/typecheck.log`.
- `npm run lint` — passed. Raw log: `.superpowers/sdd/100-shadcn-primitive-adoption/scratch/task-2/lint.log`.
- `git diff --check` — passed.

The first focused run exposed a stale test query: `type="search"` has the `searchbox` role, while the old plain input had the `textbox` role. The test now queries the searchbox and the full focused run passes. An earlier test edit also asserted the formatting checkbox after that option correctly hid the controls when formatting expanded past the diff limit; that misplaced assertion was removed before implementation verification.

Task 1's baseline `npm run check` recorded two unrelated localhost:3000 `ECONNREFUSED` AggregateErrors and the existing Node `DEP0190` warning. The Task 2 focused tests, typecheck, and lint did not emit those diagnostics; they remain documented here as baseline-only diagnostics. No full UI suite or `just verify` was run for this task.

## Files

- `src/SqlHarness.Dashboard/ui/src/components/CopyButton.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/InternalOperationsToggle.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/SessionNoteCard.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/SqlDiff.tsx`
- `src/SqlHarness.Dashboard/ui/src/components/SqlDiff.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/AgentSessionPage.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/LivePage.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/NamedOperationsPage.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/NamedOperationsPage.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/OperationPage.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/OperationsPage.test.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/SessionsPage.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/SessionsPage.test.tsx`

## Concerns

None identified in the scoped implementation or checks. Independent review is delegated to the coordinating agent after this report.

## Fix round 1 — agent session note coverage

Added `agent session note drafts cancel cleanly and save the current value after a retry` in `src/SqlHarness.Dashboard/ui/src/pages/AgentSessionPage.test.tsx`. It verifies the fixture note opens with `maxlength=10000` and `rows=5`, cancel discards a draft and reopening restores the saved value, a failed save retains the edited draft and exposes the alert, and a retry sends `{ note: "saved owner note" }` and renders the updated fixture response.

- `npm exec -- vitest run src/pages/AgentSessionPage.test.tsx` — passed, 1 file / 6 tests. Raw output: `.superpowers/sdd/100-shadcn-primitive-adoption/scratch/task-2/agent-note-vitest.log`.
- `npm run typecheck` — passed. Raw output: `.superpowers/sdd/100-shadcn-primitive-adoption/scratch/task-2/agent-note-typecheck.log`.
- `npm run lint` — passed. Raw output: `.superpowers/sdd/100-shadcn-primitive-adoption/scratch/task-2/agent-note-lint.log`.
