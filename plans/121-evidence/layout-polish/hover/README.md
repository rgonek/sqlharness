# Session and statement row hover

Operator follow-up (2026-10-28): show background feedback across the clickable Activity session header and IO statement row, matching the mock's row interaction.

Activity uses `hover:bg-muted/50`. IO retains its resting background and uses `hover:bg-muted`, including when its disclosure is expanded: the shared TableRow `has-aria-expanded` background otherwise hides the hover feedback. Existing buttons, links and disclosure handlers are unchanged. Semantic theme tokens also support dark mode.

Verification: build, TypeScript, lint and UI 408/408 passed. Six browser hover/reset checks passed in light and dark themes with zero console/page errors. Full Windows gate FAILED in 23m02s: Core 4770 passed, 11 skipped, one unchanged shutdown test failed with ObjectDisposedException when reading RunningDashboard.Stopping after the host disposed IServiceProvider. MCP 535 passed, 4 skipped. The same shutdown test then passed alone (1/1); that diagnostic does not make the full gate green. Formatting stage was not reached. No backend code or test was changed. The initial Windows gate failed before tests because the previous relocated dependency tree could not resolve Vite/rolldown and other UI packages. Its logs are retained at `D:/temp/sqlharness-hover-gate-20261028`. Dependencies were restored from the unchanged lockfile into `D:/temp/sqlharness-hover-deps-20261028/node_modules`. The previous junction is preserved under ignored `artifacts/node_modules-handoff-junction`, still pointing to its original target.

The new full gate writes to `D:/temp/sqlharness-hover-gate-20261028-restored`. Browser checks use synthetic API fixtures and submit no mutation decisions. No publication, restart, merge or push is included in this follow-up.
