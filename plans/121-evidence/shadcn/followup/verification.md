# Dashboard follow-up corrections and Mira migration

Branch/worktree: `dashboard-shadcn-fixes`, correction base `15790eb9`. Main remains untouched. Operator authorized local publication and dashboard restart after completion.

## Requested corrections

- Align Details with the session subtitle using flex alignment.
- Keep shadcn Toggle for the per-cell A/B pickers; explicitly authorized call-site classes make them circular and selected B darker than selected A.
- Preserve scroll on URL-backed session/agent-session and operation comparison choices (`resetScroll: false`).
- Retain comparison geometry through pending requests without exposing or copying placeholder evidence. Controls stay mounted/focusable, case/aggregate popovers close before navigation, and evidence alone is hidden/inert until fresh data arrives. Placeholders are restricted to the same owning session, agent-session identity pair or operation.
- Restore horizontal section separation using shadcn Separator.
- Explain Select comparison and reveal the complete truncated Activity summaries with shadcn Tooltip, including keyboard access and explicit description associations.
- At the operator's explicit follow-up request, use shadcn RadioGroup for mutually exclusive diff, IO, mutation mode, case and aggregate choices. The application-level SegmentedRadioGroup composes unchanged RadioGroup/RadioGroupItem/Label sources; independent filters and stars remain Toggle.

The original skill summary's broad ToggleGroup rule was insufficient justification for replacing the previous valid RadioGroup. The detailed forms reference explicitly recommends RadioGroup for single choice from a few options. See [shadcn RadioGroup](https://ui.shadcn.com/docs/components/base/radio-group), [ToggleGroup](https://ui.shadcn.com/docs/components/base/toggle-group), [WAI-ARIA radio pattern](https://www.w3.org/WAI/ARIA/apg/patterns/radio/).

## Evidence before Mira

- RED: IO view navigation reset scroll `396 -> 0`. `resetScroll: false` alone still moved `414 -> 210` because a transient panel removal shortened the page.
- GREEN: [browser result](results.json), [gallery](index.html). Ten captured states; delayed-response scroll remains `415 -> 415 -> 415`, IO controls retain focus, stale IO evidence and Copy summary are inaccessible while pending, version changes preserve scroll, operation comparison preserves scroll, A/B are circular with distinct selected backgrounds, Details centers align, separators exist, RadioGroup arrow/Space behavior is correct, both tooltips support hover/focus, mobile session/settings have no document overflow.
- Focused tests: 8 files, 53 tests passed, including a new single-choice keyboard/non-deselection regression.
- TypeScript and lint passed.
- Independent review found and resolved pending-focus, portalled-selector and stale-copy issues.
- Full final gate remains pending until the operator's additionally requested Mira/Zinc migration is complete.

## Additional requested preset migration

The operator requested Mira with Zinc after these corrections. Existing style is Base UI `base-nova`. Migration strategy (merge local customizations versus overwrite generated components) has been requested according to the shadcn skill; preview/preparation only until answered. The final product will receive a fresh full gate and local delivery verification after migration.
