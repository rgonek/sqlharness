# Dashboard shadcn corrections

Branch: `dashboard-shadcn-fixes`, base `0b589b40`. Implementation and evidence stay in the new worktree. No merge or push. The operator additionally authorized local publication from this branch and `sqlharness dashboard --restart` after verification. Delivery pending.

## Findings and resolutions

| Screenshot | Finding and correction |
| --- | --- |
| 1 | Switch source already matched shadcn; application CSS changed its track dimensions without changing thumb travel. Removed those overrides, including custom thumb/background colours. |
| 2 | Handwritten mode buttons stretched with their descriptive text. Replaced with standard outline ToggleGroup; independent, bounded hint. |
| 3 | Native checkbox in a handwritten details popup. Facets now compose Popover, FieldSet/FieldGroup/FieldLabel and Checkbox. IO extra columns use the same primitives. No application native checkbox/radio/select markup remains. |
| 4 | Optimization summary rendered every entry as a badge. One truncated text summary now retains complete text in its title. |
| 5 | Custom search wrapper suppressed the input border and shadow. Uses InputGroup/InputGroupInput/InputGroupAddon, restoring the group's focus ring. |
| 6 | CSS explicitly forced transparent button backgrounds. Removed it; chevron uses ghost Button, star uses Toggle. |
| 7, 8 | Ellipsis manually hid statuses in native details. Five known statuses now remain visible, with standard Toggle controls. |
| 9 | Edit input was capped at max-w-xl and CardTitle's grid min-content width could grow with long titles. Flexible input, min-w-0 CardTitle and reserved pencil space keep star/archive fixed. Tested 950 characters, including narrow viewports. |
| 10 | Details trigger now composes CollapsibleTrigger with a ghost Button and ChevronDown. |
| 11 | Case selector and copy control independently used absolute positioning against the card. Normal flex toolbar prevents overlap and wraps. |
| 12, 13 | The actual shadcn variant is `line`, not `inline`. Preserved its active underline, removed application-wide border and typography/padding overrides; added layout spacing to notices. |
| 14 | Native details was clipped by the table scroll container. Standard portaled Popover now escapes that container and respects viewport collision handling. |
| 15 | Eight-pixel Checkbox/Label gap is normal. The excessive gap came from CSS padding every diff-controls label. Removed that CSS; default Checkbox and Label are unchanged. |
| 16 | Both custom Button choices had similar muted backgrounds. Standard outline ToggleGroup on an unfilled surface makes its default pressed state visible. |
| 17 | Statement expansion uses ghost Button; plan links retain real hrefs with semantic hover/focus styling. |
| 18 | Default Select aligns the selected item over its trigger (`alignItemWithTrigger=true`). This is documented behavior, preserved. Added missing SelectGroup at dashboard call sites. |
| 19 | A/B were custom labels around hidden native radios. Standard outline Toggle controls now render their labels and pressed state. Keyboard interaction is standard Tab/Space; selection remains controlled and exclusive per side. |

Sources: [Select alignment](https://ui.shadcn.com/docs/components/base/select#align-item-with-trigger), [line tabs](https://ui.shadcn.com/docs/components/base/tabs#line), [ToggleGroup](https://ui.shadcn.com/docs/components/base/toggle-group), [InputGroup](https://ui.shadcn.com/docs/components/base/input-group), [Switch](https://ui.shadcn.com/docs/components/base/switch).

New components were installed with the shadcn CLI from `@shadcn`, using the existing Base UI / base-nova configuration. Existing primitives were not overwritten; generated components are unmodified. The CLI dry run identified the pre-existing separator difference and it was explicitly skipped.

## Review

Independent code review identified one P2: upstream ToggleGroup consumes vertical orientation without forwarding it. Resolved through horizontal wrapping composition; generated source stays unchanged. Follow-up review found no further concrete issues.

## Verification

- Initial UI baseline: 67 files / 384 tests passed (before the corrective test updates).
- Browser reproduction failed before the fix: title editing moved the star from x=1192/y=183 to x=600/y=87. The next reproduction exposed the parent grid min-content width; CardTitle min-w-0 resolved it.
- Playwright: 23 states passed at 1280, 768 and 390 pixels, 950-character title, stable title action geometry, distinct IO pressed backgrounds, 32px default Switch, portaled popover within viewport, no whole-page overflow and no page errors. See [gallery](index.html) and [machine-readable result](results.json).
- First full gate attempt failed at dependency installation because Vite held the native rolldown module open. The simultaneous focused run consequently lacked packages; that run is invalid verification evidence. Closed the worktree Vite process before retrying installation/gate.
- Gate retry: build passed; UI 383/384 passed. Corrected the remaining native-summary test query and an unsupported Testing Library type option. A focused follow-up exposed duplicate accessible names on wrapped facet checkboxes; explicit Checkbox/FieldLabel associations and aria-hidden counts resolved it. The focused filter test passes. Cancelled the stale gate and restarted on the corrected product tree. Final gate passed: 5 stages, 14m26s; UI 384/384, Core 4718 passed / 11 skipped, MCP 535 passed / 4 skipped. Build 0 warnings/errors; formatting skipped because no C# changed. See [logs](final-gate/summary.log) and [source identity](candidate-trees.json).
- Individual registry comparisons confirm Toggle, Popover, ToggleGroup and InputGroup are identical to the CLI output. Field differs only by the CLI-generated `"use client"` directive, harmless in this Vite app; no source was hand-edited. Multi-item CLI dry-run output was inconsistent, so per-file evidence is retained.

Reproduce browser checks with the harness dependencies installed, this worktree's Vite at 127.0.0.1:48126, then `node plans/121-evidence/visual/harness/shadcn-check.mjs`. The fixture interceptor never reaches the installed journal or databases. Finish browser checks before a dependency-reinstalling gate on Windows.

## Full-gate timing failure

The parallel-collection gate passed the entire UI suite (384/384), typecheck/lint and MCP (535 passed, 4 skipped), but Core had 4717 passed / 1 failed / 11 skipped. `PidPublicationError_PreservesOriginalWhenMarkerWriteFails` exceeded its existing five-second wait under contention. The unchanged test passed isolated (1/1, no skips); see [evidence](process-test-isolated-pass.log) and [failed parallel logs](gate-parallel-failure/).

Following the existing [Plan 121 verification procedure](../visual-verification.md), the final retry uses ignored output-only xUnit runner files in both projects: `parallelizeTestCollections=false`, `maxParallelThreads=1`, `diagnosticMessages=true`. Projects still run concurrently. No test filters, source/timeout changes or tracked runner-policy changes were introduced. Final retry passed: `just verify -LogDirectory D:/temp/shadcn-final-sequential-gate`, exit 0, 5 stages, 14m26s. Core 4718 passed / 11 skipped; MCP 535 passed / 4 skipped; UI 384 passed, lint/typecheck passed. See [final logs](final-gate/).
