# Task 4: shared readability and interaction

Implementation and focused checks are complete in the shared isolated `dashboard-quality-9` worktree.
This is scoped component evidence, not the final Windows gate or a category score.

## Changes

- `CopyButton` preserves its existing icon-only `label`/`getText` API and adds optional visible labels,
  disabled/title/outline presentation and `scopeKey` to reset feedback when the selected payload changes.
  It announces the named payload, prevents duplicate pending requests, provides preparation errors without
  fabricating an empty payload, and offers selected manual-copy text when clipboard access fails.
  The manual-copy dialog explains the payload and returns focus to its originating action.
- `SqlBlock` and `ReproScriptButtons` use that shared behaviour. SQL copying preserves the original SQL
  even when formatting is displayed. Script copying preserves the endpoint response and exact
  operation/side/case/form binding; `labelPrefix` names selected baseline/candidate/case actions.
- `SqlDiff` adds `baselineActions` and `candidateActions` slots beside corresponding side controls,
  including embedded/over-limit views, and names its copy actions visibly. Existing SQL/diff bounds remain.
- Monaco SQL uses 14px text and 22px line height. Initial SQL frames follow content between 88px and 480px;
  manual resize remains bounded to 1200px and survives content updates. A focused frame also supports
  Alt+Up/Down resizing. SQL source reveal honours reduced motion. Bounded previews use 14px SQL text.
- `AppLayout` begins with a keyboard-visible skip link that focuses the main landmark; active navigation
  has `aria-current`. `ErrorState` supports explicit optional retry and local pending feedback. Expired
  sessions and missing records do not expose retry. No automatic retries or mutation actions were added.

## Verification

Windows, Node/npm repository environment, local synthetic component fixtures only.

1. Initial red run:

   ```text
   npm.cmd test -- src/components/CopyButton.test.tsx src/components/SqlBlock.test.tsx src/components/SqlDiff.test.tsx src/components/AppLayout.test.tsx src/components/ErrorState.test.tsx src/components/sql-editor/monaco.test.tsx src/components/ReproScriptButtons.test.tsx
   ```

   Exit 1: 11 intended failures, 18 passes, 7 files, 75.80s. Failures covered missing visible/scoped copy
   feedback, preparation-error handling, manual-copy description/focus return, SQL copy fallback,
   side-action slots, skip navigation, explicit retry, compact frame sizing and keyboard resize.

2. The same focused command after implementation: exit 0, 29/29 tests, 7 files, 58.97s.
3. Self-review found stale success feedback after a payload changed. Added the scope regression first;
   `npm.cmd test -- src/components/CopyButton.test.tsx` exited 1 with 1 intended failure and 6 passes
   (7.89s). Added `scopeKey` in the component and owned callers.
4. Full focused command above then passed: exit 0, 30/30 tests, 7 files, 49.65s.
5. Scoped oxlint found a new state-in-effect warning. Replaced the feedback reset with a conditional
   render reset; final `npm.cmd test -- src/components/CopyButton.test.tsx` passed 7/7 tests (7.20s).
6. `npm.cmd run typecheck`: final exit 0. An earlier run found only concurrent sibling edits in
   ActivityContext/OperationPage; their owners corrected those files before the successful reruns.
7. Scoped oxlint over the fourteen owned component/test files: final exit 0 with no diagnostics.
   `git diff --check`: exit 0.

Read installed shadcn/Base UI component implementations and fetched current button/dialog/alert docs
after `npm.cmd exec -- shadcn docs button dialog alert` succeeded. An initial docs call before the
coordinator's npm install completed could not find the executable; no reinstall was performed here.

## CSS coordination and remaining evidence

No global CSS was edited by this task. The coordinator owns global supporting-text/focus/reduced-motion
rules. Skip-link visibility and main/frame focus styles are local utility classes; no additional CSS
request remains.

The Monaco tests mock the editor runtime. They establish frame sizing, keyboard resize and 14px options,
not actual browser painting, native drag resize or screen-reader speech. Browser acceptance still needs
real Monaco at desktop/narrow widths, 200% zoom, theme contrast, native drag resize, keyboard navigation,
script/case round trips, clipboard denial and dialog focus return. The broader screenshot/accessibility/
performance matrix and final Windows `just verify` belong to the coordinator's subsequent acceptance.
No full gate, Linux gate, database workload, mutation approval, install/restart, merge, push or publication
was performed by this task.
