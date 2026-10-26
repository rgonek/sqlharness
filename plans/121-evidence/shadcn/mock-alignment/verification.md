# Mock alignment verification

Worktree: `dashboard-shadcn-fixes`, base `1226f1ec`. Product commits: `dbc01eb6`, final `a685e942`. Main is untouched; no merge/push. Existing operator authorization covers local publication and dashboard restart after successful verification.

## Changes and review

- Keep segmented RadioGroup presentation for diff, as explicitly chosen by the operator.
- Replace the two pressed plan Buttons with a single RadioGroup, soft 28px segmented treatment and shadcn A/B Badges. Its selectors share the desktop toolbar; mobile wraps without overflow.
- Keep generated shadcn primitives unchanged. Compose Tabs line styling at application usage sites: 44px row, 14px labels, flush underline, metadata beside the tabs.
- Portal the existing comparison toolbar to its owning Optimization header, preserving pending-state protections and selector behavior.
- Fix the actual JSON contract: PairBasis had serialized numerically although all frontend comparisons expected named strings. An enum-scoped converter now emits the existing frontend contract. No global serializer, telemetry, persistence or database behavior changes.
- Filter/deduplicate context labels; no empty separators or repeated SameSql/ConditionsDiffer labels. Share the basis formatter with copied Markdown.
- Correct the misleading per-statement metrics tooltip. CPU/elapsed are persisted but not exposed by the current comparison model; do not claim all collection is missing.

Independent read-only review: no findings on the implementation, pending/portal behavior, accessibility, serializer scope or final deduplication. The data inventory was checked against the original reports and current code; no findings.

## Evidence

- Original mock exports run over loopback with their support.js, rather than inferred from screenshots alone.
- [Before](before/results.json): 19 captured states, covering mock Activity 3a, Settings, four Operation types and Session 4a diff/IO/Plan, plus app counterparts and mobile Plan.
- [After](after/results.json): 10 app states. Plan RadioGroup keyboard selection and non-deselection, tab underline geometry, desktop/mobile and no page errors.
- [Focused comparison](focused/results.json): all three Session tabs, actual mock and app panels both **1088 CSS px**, no scaling. The initial gallery preserves native-width full-reference/pre-change history; the focused pairs are the width-normalized comparison.
- [Edge cases](edge/results.json): SameSql and ConditionsDiffer metadata appears once, no empty separators or console/page errors; ConditionsDiffer cannot expose IO evidence.
- [Prior interaction regression](interactions/results.json): ten states, delayed response scroll `321 -> 321 -> 321`, version/operation navigation, focus/stale-copy protections, tooltip hover/focus, radio behavior, A/B circles and mobile overflow. Original delivery artifacts remain preserved in their own folder.
- [Radio RED](checks/radio-red.log): original plan Buttons fail the radio requirement; final focused UI GREEN is **16/16 tests** in four files ([log](checks/ui-focused.log)).
- [Wire contract RED](checks/basis-red.log): all four enum cases serialized as Number. [GREEN](checks/basis-green.log): four pass after converter. C# changed-file formatting passed with `just fix`.

[Gallery](index.html). [Data inventory and simplification recommendations](data-audit.md).

## Final gate and delivery

An earlier gate on dbc01eb6 passed its build, then was cancelled before completion to remove repeated labels/duplicate React keys in SameSql/ConditionsDiffer. It is not claimed as a passing gate. A second build-only attempt on 67370ca1 was cancelled to compact and align the Plan toolbar after equal-width visual inspection. Neither cancelled attempt is a passing gate. The final full Windows gate runs on a685e942. The established ignored, output-only xUnit runner configuration uses sequential collections in each project; projects still run concurrently. No filtered tests or source timeout changes.

Final Windows gate passed on a685e942: exit 0, five stages, 19m18s. UI 386/386 (68 files), TypeScript/lint passed; Core 4722 passed / 11 skipped; MCP 535 passed / 4 skipped; build zero warnings/errors. Gate format stage skipped zero working-tree C# files; the two committed C# changes had passed just fix before commit. [Final logs](final-gate/summary.log), [source receipt](source-receipt.json).

Final candidate UI stage passed: 68 files / 386 tests, TypeScript and lint; build zero warnings/errors. Core/MCP also passed; final gate results are recorded above.

Read-only check against the actual installed journal API before deployment confirmed basis was numeric 0, rather than only a fixture discrepancy: [live before](live-basis-before.json). The post-restart GET confirmed the named string MeasuredTogether for the retained compare, recording only ID/basis type/value, never SQL, target names or credentials.

## Existing editor ResizeObserver notification

Vite surfaced a native window error during Monaco resize. A separate window-error probe (stronger than Playwright pageerror/console channels) reproduced exactly two ResizeObserver loop completed with undelivered notifications events both in the final production dist and in the previous installed production UI, using identical Plan A/B switches and 1280/768/390/1120px widths. [Current](resize-result.json), [baseline](resize-baseline.json). Both probes intentionally exit nonzero when any window error exists; neither is reported as a clean runtime pass. The ordinary 10-state interaction/visual check and metadata edge checks also passed against production dist without Vite. No visual/interaction regression was observed; this pre-existing editor issue is recorded, not suppressed or changed as part of the mock-alignment request.

## Local delivery

Published from a685e942 with just publish fast (exit 0), then sqlharness dashboard --restart (exit 0). Dashboard origin http://127.0.0.1:47800/, PID 38328. Installed EXE SHA-256 equals the worktree publish artifact, and the running process uses that installed executable. All 20 served assets, including fonts and workers, match ui/dist byte-for-byte. Three installed UI fixture screens passed, including the revised Plan toolbar. Actual journal API GET now returns basis as string MeasuredTogether, whereas the previous installed binary returned number 0. No target SQL was executed.

[Delivery](delivery.json), [restart](restart.json), [served assets](installed-assets.json), [live API after](live-basis-after.json), [installed screens](installed/results.json), [publication log](publication.log). Product/test trees remain identical to the verified candidate; only documentation/evidence changed afterward. Worktree retained; no merge/push, main's unrelated edits untouched.
