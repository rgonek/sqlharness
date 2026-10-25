# Reproducing the visual evidence

This isolated harness intercepts API requests in its own Playwright contexts. It does not seed a real journal or execute database operations. Fixtures are illustrative, not production measurements. It imports the existing UI test fixture builders and uses the actual built application components.

Requirements: Node 24, Playwright 1.64.1 and its Chromium 1248 browser. Install this harness separately from the application (`npm install` here, then `npx playwright install chromium`). Pillow is used only for side-by-side comparison sheets.

1. Serve the exported references with support.js from `docs/design/dashboard-2026-10-25` on 127.0.0.1:48121 (`python -m http.server 48121 --bind 127.0.0.1 --directory <reference-directory>`).
2. Build the actual UI, then run `node static-server.mjs <ui-dist-directory> 48122`. This server uses only Node built-ins, so it does not keep the Windows Rolldown native binding open during npm ci.
3. Run `node mock-matrix.mjs` and `node matrix.mjs after http://127.0.0.1:48122`.
4. For before evidence, use a detached checkout at 7c1b2fbf and its own built UI on 48124, then run `node matrix.mjs before-baseline http://127.0.0.1:48124`.
5. Run `node sql-click.mjs` and `python compose.py` (Pillow required).

The source frames are fixed at 1280px (Activity) and 1120px (Session 4a), with a 1px outer border removed only in comparison sheets. Browser page screenshots have deviceScaleFactor 1, light theme, and 100% zoom. Earlier captures used 1000px page heights; final normalized passes use 1400px so all graph content fits. Narrow checks use 390px. The frame content is not scaled.

Each matrix JSON records the browser viewport and concrete screenshots. Console errors fail the implementation matrix; per-table horizontal scrolling is allowed, whole-page overflow fails.

The `EventSource` shim belongs only to the isolated fixture context and prevents a deliberately finite intercepted SSE response from simulating a disconnect. Production live reconciliation is unchanged and covered by the real UI tests.

`additional.mjs` reproduces the extra isolated interaction/fault checks. `installed-check.mjs` is a delivery-specific smoke for an isolated installed dashboard at port 48125 and home `D:/temp/sqlharness-visual-installed-home`; it reads the local token privately, verifies served asset bytes against the workspace build, and never records the token. Adjust that private home when reproducing elsewhere.
