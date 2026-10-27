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

The operator chose merging existing customizations and subsequently authorized Mira's default font and icons. Product commit `61167ae0` migrates Base UI `base-nova` to `base-mira`, Zinc base/theme/chart, Inter sans and Hugeicons; Geist Mono remains for SQL. CLI-resolved preset: `bdvw9WhU`. CLI init, base-color migration and icon migration were followed by fresh CLI-generated Mira sources for all 26 installed primitives. All primitive sources match the generated candidates except the existing Accordion heading-level extension. Three Accordion call sites supply a keyboard focus ring because the generated Mira trigger has none. No Lucide imports/dependency or Geist sans dependency remain.

The title-edit placeholder now uses an invisible Button of the same variant/size as the edit action, so Mira's smaller button dimensions cannot move the remaining actions. Original app-specific geometry, semantic SQL colors and explicitly requested A/B circle/contrast classes are retained.

Mira browser QA passed ten states, with delayed session refresh scroll `403 -> 403 -> 403` and operation scroll `277 -> 277`. Three further states verified 950-character titles at 1280, 768 and 390 pixels: no overflow, stable action coordinates, computed Inter font. [Mira gallery](mira/index.html), [results](mira/results.json), [title geometry](mira/title-results.json). The adapted original screenshot regression also passed 23 states, including popovers, checkbox facets, hover, IO selection contrast and default 28px Switch. [Regression gallery](mira/original-regression/index.html). Typecheck/lint passed; independent final read-only review has no remaining findings.

Mira's compact typography and 28px Switch are preset defaults. shadcn Select selected-item alignment is preserved, rather than treated as a defect. Final Windows gate passed on 3f87a261: UI 385, Core 4718 passed / 11 skipped, MCP 535 passed / 4 skipped; build has zero warnings/errors, format skipped because no C# changed. Five stages, exit 0, 15m09s. The existing output-only sequential xUnit configuration was used; no test filters or source timeout changes. [Final logs](mira/final-gate/summary.log), [source identity](mira/source-receipt.json). Authorized local publication and restart follow.

## Final-gate Select assertion correction

The first Mira gate on 61167ae0 built successfully and passed 384/385 UI tests. One global text query matched both the selected Case combobox value and the still-closing Select option. Commit 3f87a261 scopes the assertion to the Case combobox; request and URL assertions remain unchanged. The focused OperationPage suite passed 30/30; independent review approved this test-only correction. No application behavior changed. [Failed-stage logs](mira/gate-select-query-failure/ui-check.log) are retained. Full gate retry passed on 3f87a261.

## Local delivery

Published locally from 3f87a261 with just publish fast (exit 0), then sqlharness dashboard --restart (exit 0). The previous endpoint file was absent before restart, so no old running PID is asserted. Dashboard is available at http://127.0.0.1:47800/, PID 12596, using the installed executable. Installed and published EXE SHA-256 match. All 20 served assets, including Inter/Geist Mono fonts and editor worker, match ui/dist byte-for-byte. Three installed-binary fixture browser screens passed, Inter loaded, no page errors. Browser API fixtures do not reach journals/databases. Endpoint credentials stayed in memory and were not recorded.

[Delivery receipt](mira/delivery.json), [asset hashes](mira/installed-assets.json), [restart](mira/restart.json), [publication log](mira/publication.log), [installed browser result](mira/installed/results.json). Source tree remains the final-gate tree; only evidence/docs changed after verification. Worktree/branch preserved, no merge or push; main's unrelated edits untouched.
