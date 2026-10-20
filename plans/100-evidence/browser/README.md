# Browser evidence

The PNGs are screenshots from cached Chromium 1228; paired light/dark captured outputs hold the measured results and runner assertions. The runner files use `PLAYWRIGHT_CORE_PATH`, `CHROMIUM_PATH`, `THEME`, and `UI_FIXTURE_URL` where needed. The archived TSX/HTML fixtures render the production components or routed pages with local fixture data and intercepted API requests.

- Tasks 3–5 cover compare disclosures, nested reopen state, editor sizing, reduced motion, the version picker keyboard/typeahead/escape lifecycle, and open/close/reopen behavior for all dimension disclosures and filters.
- Task 7 covers a long PlanWorkbench case label and popup bounds, then clicks the source-match button with a pointer in the real PlanWorkbench layout. The runner asserts the selected node, source-match rule and SQL source-span highlight.
- Task 8 covers StatusBadge hover/focus, keyboard dialog activation, Escape focus return, badge isolation and dialog bounds at 320px.
- The actual-pages fixture renders SettingsPage and StatsPage through the app router. At 320px, Settings document width is 320px and the approval-sound trigger is 256px wide; the long Statistics profile trigger is 288px and stays within the viewport. The StatsPage width fix is in the tested revision.
- The VersionTable fixture preserves two independent native radio groups at 320px, including arrow-key selection. Task 10's earlier table/dialog fixture and its selected raw evidence are under [task-artifacts](../task-artifacts/task-10/).

For reruns, place the matching fixture files in the UI root under the `task11-*.html` / `task11-*.tsx` names referenced by the runner, start the UI Vite server on `127.0.0.1:4173`, and set `UI_FIXTURE_URL` to the matching route. The actual-pages runner additionally fulfills the shell's live-feed EventSources with empty fixture streams. Remove only those temporary UI-root fixture files after the run.
