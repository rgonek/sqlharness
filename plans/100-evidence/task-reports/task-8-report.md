# Task 8 evidence — StatusBadge trigger composition

Worktree: `C:\Users\rgone\MultiCliProfiles\codex\account-b\worktrees\cf54\sqlharness`
Branch: `plan-100`
Base revision: `d3dbd543654664430fc4e29b7b594e4707fc73da`

## Change

Moved the uncontrolled Dialog root around the Tooltip trigger and DialogContent. Composed `TooltipTrigger` with Base UI `DialogTrigger` using the installed `render` API, so one button owns both interactions and Base UI supplies the dialog popup relationship. Removed manual open state. Retained failed/rejected badge text and tiers, tooltip explanation, dialog title/description, stored error text, and missing-message text.

Added focused assertions for keyboard focus showing the tooltip, Enter opening the dialog, `aria-controls` linking the active trigger to the dialog, Escape closing and restoring focus, one button per badge, and two badges opening only their own content.

## TDD evidence

Before the component change, the new lifecycle test failed because the manually handled button had no `aria-controls` attribute, while the dialog had an ID. This demonstrated the missing Base UI trigger relationship. The original click/tooltip tests and the additional behavior tests passed after composition.

Initial red command from `src/SqlHarness.Dashboard/ui`:

```text
npm exec -- vitest run src/components/StatusBadge.test.tsx
Test Files  1 failed (1)
Tests       1 failed | 8 passed (9)
Failure     expected aria-controls="_r_r_"; received null
```

## Verification

Final focused command from `src/SqlHarness.Dashboard/ui`:

```text
npm exec -- vitest run src/components/StatusBadge.test.tsx
Test Files  1 passed (1)
Tests       9 passed (9)
Exit code   0
```

Also from the UI root:

```text
npm run typecheck  -> exit 0
npm run lint       -> exit 0
```

`git diff --check` passed. These are task-scoped checks, not the Plan 100 full UI suite or repository `just verify` gate.

## Browser observation

Used the disposable `` with the actual `StatusBadge` component, cached Chromium 1228, and Playwright Core from `D:/Dev/CivicLens/frontend/node_modules/playwright-core/index.mjs`. The temporary Vite config, fixture, script, and logs all remain under this plan scratch directory; the fixture makes no backend or database requests.

Final browser output:

```json
{
  "initialButtons": 2,
  "tooltipAfterHover": true,
  "tooltipAfterFocus": true,
  "keyboardOpen": true,
  "escapeFocusReturn": true,
  "badgeIsolation": true,
  "controlledPopupRelation": true
}
```

The open dialog associates to its originating button with `aria-controls`; Escape closes it and restores focus to that same button. The second badge opens only its own error content. Earlier fixture runs logged a missing favicon while the fixture had no icon; the final fixture supplies an inline empty icon and the final browser run emitted no browser or page errors.

## Review and scope

Reviewed the final diff and component tree: both triggers and the content share the same uncontrolled Dialog root; composition renders a single button; no copied Radix `asChild` usage or nested button exists. Only `StatusBadge.tsx` and `StatusBadge.test.tsx` are product changes. No full UI suite, final repository gate, merge, push, backend, or live write was performed.

Commit: `92370a67b856f87d693bfb4bf72badb3e925f0a9` (`Compose StatusBadge tooltip and dialog trigger`).
