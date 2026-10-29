# Plan 123 verification

Product revision: `6aa51e26`. Task 5 and whole-branch reviews are archived; scoped re-review of the final fix remains pending. No merge, push, installation, Linux verification, live journal or database access.

- Browser fixture: PASS, synthetic only. [Screenshot](session8-shared-table.png), [harness](session8-fixture.mjs), [log](session8-browser.log). Real dashboard UI displays v0–v3 in one table, condition badges A/B, rows 10 and missing `—`, and after clicking B v2 the URL has `groupB=fp1`; the approximate banner and 10 → 12 rows are shown. Page errors: 0.
- `pwsh -NoProfile ./scripts/format-changed.ps1 -Mode branch -BaseRef f5497956`: PASS, 29 changed C# files, exit 0; [log](reviews-and-checks/branch-format-pass.log). Initial check failed whitespace and final newline; [failure](reviews-and-checks/branch-format-final.log), [fix](reviews-and-checks/branch-format-fix.log). All eight formatted files compare equal after removal of whitespace to `06043305`.
- Full Windows gate: PASS on `d3d3c068`, exit 0, 11m09s: UI 446 passed (71 files); Core 4792 passed / 11 skipped; MCP 535 passed / 4 skipped; build passed. Gate format skipped (clean working tree); branch format checked separately. First attempt failed ui-check because Vitest discovered incomplete dependency tests under `node_modules-incomplete-123`; [log](reviews-and-checks/windows-gate/ui-check.log). That directory was moved intact from the UI directory to the worktree root after verifying both paths and an absent destination. No cleanup deletion was retried.
- MCP source diff from `f5497956`: empty.

Browser diagnostics: first navigation timed out while Vite optimized; the broad API interception also captured `/src/api/` modules. After restricting it to root `/api/`, missing fixture `profiles` and `elapsed` fields produced React boundary errors. The final interaction clicks the visible label rather than the visually hidden radio. The retained diagnostic logs document each attempt. These were fixture defects, not product changes. Vite returned 200 for HTML and `/src/main.tsx`; successful rendered UI confirms module loading.

Reproduction from repository root: start `node node_modules/vite/bin/vite.js --host 127.0.0.1 --port 48139 --strictPort` in `src/SqlHarness.Dashboard/ui`; set `PLAYWRIGHT_MODULE` to a local Playwright package.json (this run: `D:/temp/sqlharness-visual-tools/node_modules/playwright/package.json`); run `node plans/123-evidence/session8-fixture.mjs`. Stop the isolated Vite afterwards.

## Final review fix verification

- Full Windows gate: PASS on product `6aa51e26`, exit 0, 10m49s; build passed, UI 448 passed (71 files), Core 4792 passed / 11 skipped, MCP 535 passed / 4 skipped. UI install skipped (lockfile unchanged); gate format skipped (0 changed C# files). [Summary](reviews-and-checks/final-fix-gate.log) and [stage logs](reviews-and-checks/final-fix-gate/). Existing exhaustive-deps and DEP0190 warnings remain as triaged in the whole-branch review.
- Focused tests: 19 passed across ComparisonPanel and optimizationMarkdown; initial RED: 2 failed, 17 passed. [RED](reviews-and-checks/final-fix-red.log), [GREEN](reviews-and-checks/final-fix-green.log).
- Refreshed synthetic browser fixture: PASS, including A/B conditions labels, copied full group identities, zero page errors; [log](reviews-and-checks/final-fix-browser.log), [screenshot](session8-shared-table.png). No live journal validation.
