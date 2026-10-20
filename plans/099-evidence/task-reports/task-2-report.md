# Plan 099 — Task 2 report

## Changes

- Replaced `@import "./styles/shadcn-tailwind.css";` with `@import "shadcn/tailwind.css";` in `src/SqlHarness.Dashboard/ui/src/index.css`.
- Deleted `src/SqlHarness.Dashboard/ui/src/styles/shadcn-tailwind.css`.
- Kept the Tailwind, animation, font, dark variant, responsive tab rule, theme tokens, application styles, and CSS variables unchanged. No component, backend, database, package, or lockfile changes.

## Package and source inventory

- Installed package metadata reports `shadcn@4.22.1` and maps `./tailwind.css` to `./dist/tailwind.css` (the package version and export were read from `src/SqlHarness.Dashboard/ui/node_modules/shadcn/package.json`). The stylesheet was inspected at `src/SqlHarness.Dashboard/ui/node_modules/shadcn/dist/tailwind.css`.
- Compared with the deleted vendored 4.21.4 source at `HEAD`, the exported 4.22.1 source retains the same nine custom variants: `data-open`, `data-closed`, `data-checked`, `data-unchecked`, `data-selected`, `data-disabled`, `data-active`, `data-horizontal`, and `data-vertical`.
- Both sources define the same keyframe names: `accordion-down`, `accordion-up`, `scroll-fade-reveal-t/b/s/e`, and `tw-shimmer`. Both also define the same `no-scrollbar`, `scroll-fade*`, and `shimmer*` utility families. No existing UI source references those utility families or accordion keyframes. Existing component classes use the state/orientation variants; `animate-in`, `animate-out`, `fade-in-0`, and `zoom-in-95` are supplied by the separate `tw-animate-css` import.
- These source-level version/format differences did not change the generated styles used by the app: all emitted CSS artifacts matched the saved Task 1 baseline byte for byte.

## Build and emitted CSS comparison

Command from `src/SqlHarness.Dashboard/ui`: `npm.cmd run build` — exit 0; Vite 8.3.3 transformed 3,925 modules and built in 2.69s. The existing large JavaScript chunk warning remains.

The following baseline artifacts from `.superpowers/sdd/099-shadcn-cli-restoration/scratch/baseline/css/assets/` were compared with the build output under `src/SqlHarness.Dashboard/ui/dist/assets/`. SHA-256 values were computed with `Get-FileHash -Algorithm SHA256` on both files.

| Artifact | Baseline/current bytes | Baseline/current SHA-256 | Result |
| --- | ---: | --- | --- |
| `index-D0saNgLL.css` | 75,949 / 75,949 | `57D50072FFCC5FD9B2D54D4724730563DB47BD573A7435B1D90E09DA656B860D` / same | Byte-identical |
| `monaco-DjpsE8to.css` | 99,095 / 99,095 | `797AA85D5570A0EBBB047093F58E9ADC13945078495425B27133073E5B15A9DA` / same | Byte-identical |
| `PlanGraphView-DLioOiRN.css` | 15,413 / 15,413 | `B0CB7BD6FEC5951C9C8F5808821438206604B1629A912C96009AC427F38E7D69` / same | Byte-identical |

Manual emitted-selector inspection (`rg` against `dist/assets/index-D0saNgLL.css`) confirmed checked and unchecked switch colors, disabled pointer/cursor/opacity styles, open/closed dialog and dropdown animation/opacity styles, active tab background/text styles, and horizontal/vertical orientation selectors. The exported styles still produce the component variants that were present in the baseline.

## UI check

Command from `src/SqlHarness.Dashboard/ui`: `npm.cmd run check` — exit 0. `scripts/check.mjs` completed its typecheck, Vitest, and oxlint children. Vitest reported **62 test files passed, 331 tests passed, duration 257.31s**. The log contains the existing two `ECONNREFUSED` diagnostics for `::1:3000` and `127.0.0.1:3000`; neither caused a failed test or nonzero command result. Full output is in `.superpowers/sdd/099-shadcn-cli-restoration/scratch/task2-check.log`.

This completion resolves the earlier Task 1 uncertainty about a missing Vitest summary. The repo’s current `vite.config.ts` sets the single-worker `forks` pool and records that this Windows runner previously stalled with `vmThreads`; this run used that existing configuration without changing source or imposing the earlier 120-second timeout.

## Browser fixture

- Created a disposable home at `.superpowers/sdd/099-shadcn-cli-restoration/scratch/browser-home`. The dashboard backend started on `127.0.0.1:47801` because the default 47800 port was already occupied. Its `/api/settings` response and the Vite-served application both returned HTTP 200. No real home, live database, or SQL connection was used.
- Started Vite at `127.0.0.1:5199`, with `SQLHARNESS_DASHBOARD_URL` pointed at the disposable backend and `SQLHARNESS_DASHBOARD_TOKEN` read from that home’s generated dashboard metadata. Opened the UI in Herdr and also drove the page using cached Chromium/Playwright.
- On the real backend’s Settings page, inspected nine switches in light and dark themes: checked and unchecked states had the expected theme colors. There was no disabled switch in that page, so disabled behavior was confirmed from emitted CSS selectors only.
- Exercised the theme dropdown in both themes and opened the existing “Clear matched data?” dialog without confirming its action. Open-dialog CSS had `data-open`, opacity 1, and the dark popover background.
- For sessions, Playwright supplied one synthetic `SessionSummary` response (id 8801, display title `CSS fixture`, note preview `Fixture note for tooltip`) through route overrides for `/api/agents` and `/api/sessions`; all other API requests remained on the isolated dashboard backend. This fixture made the existing Active/Archived and All/Claude Code tabs, session row, and note tooltip visible without writing journal data.
- Confirmed selected tabs expose `data-active` and `aria-selected="true"`. Keyboard ArrowRight moved focus from Active to Archived; `:focus-visible` was true and the computed outline was a 3px ring. The horizontal app separator was 1px tall and full width in both themes. No existing page renders a vertical separator, so that orientation was checked in emitted CSS only.
- Hovering the fixture note showed `Fixture note for tooltip` with `data-open` and the expected foreground/background contrast in light and dark themes. Page error collection remained empty.
- With `prefers-reduced-motion: reduce` emulated, `matchMedia` reported true but the open tooltip retained its 0.15s `enter` animation. The emitted stylesheet has a reduced-motion override for `.shimmer`, not a general override for the current tooltip animation. This is the pre-existing baseline behavior and was not changed by this stylesheet-only task.

Screenshots, all under `.superpowers/sdd/099-shadcn-cli-restoration/scratch/`:

- `browser-live.png` — dashboard overview from the real disposable backend.
- `browser-theme-menu-light.png`, `browser-dropdown-dark.png` — open theme dropdown in light and dark themes.
- `browser-settings-light.png`, `browser-settings-light-final.png`, `browser-settings-dark.png` — Settings and switch states in both themes.
- `browser-dialog-dark.png` — open confirmation dialog in dark theme.
- `browser-tabs-dark.png` — active/archived tabs and synthetic session row in dark theme.
- `browser-tooltip-light.png`, `browser-tooltip-dark.png` — open fixture note tooltip in both themes.

## Self-review and outstanding review

- Product commit: `dd8b9e9` (`ui: restore shadcn stylesheet export`), containing only the import replacement and stylesheet deletion.
- `git diff` contains only the required `index.css` import replacement and deletion of the vendored stylesheet; package files and application contracts are unchanged.
- No brittle CSS byte-count or upstream-string test was added. CSS equivalence is evidenced by the actual production build artifacts.
- The 150ms reduced-motion tooltip animation and the lack of live component instances for disabled switches/vertical separators are recorded above as scope/coverage limits. They do not indicate a stylesheet difference from baseline.
- Independent task review is still pending. The controller’s task instruction prohibited subagents; the implementation is committed for review by the controller/reviewer before the plan task is marked complete.

## Evidence files

- Build: `.superpowers/sdd/099-shadcn-cli-restoration/scratch/task2-build.log`
- UI check: `.superpowers/sdd/099-shadcn-cli-restoration/scratch/task2-check.log`
- Baseline CSS and hashes: `.superpowers/sdd/099-shadcn-cli-restoration/scratch/baseline/css/` and `css-sha256.txt`
- Browser screenshots: the files listed above in `.superpowers/sdd/099-shadcn-cli-restoration/scratch/`
