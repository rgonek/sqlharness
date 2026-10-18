# Task 6 implementation report: graphical plan viewer and SQL linking

## Result

Implemented the dashboard plan workbench against Task 5's manifest, source-map, graph, and property contracts. The old `/api/plans/{hash}` client path is removed. The renderer-neutral graph is rendered through the pinned lazy `@xyflow/react` 12.12.0 viewer; SQL stays byte-for-byte equal to the source map's `executedText` so all ranges remain tied to captured offsets.

The workbench selects a case, repetition, document, and statement. Its source-map request carries the displayed variant, parameter set, repetition, and case ID. It preserves the whole SQL batch when switching statements, while filtering the graph to that statement. Graph and selection keys combine document SHA, ordinal, and stable graph `node.id`; `nativeNodeId` is display-only. `PlanChanges` now uses renderer-neutral graph data and requires an exact baseline/candidate occurrence when multiple repetitions or documents are available.

## API and interaction contracts

- `useOperationPlans` reads the operation manifest; `usePlanSourceMap` sends the exact occurrence selector; graph and property calls use the manifest's raw document SHA and stable graph node ID. No UI XML parsing or Showplan-specific field assumptions were added.
- Clicking an operator selects its source-map spans, decorates them in the read-only Monaco editor, and reveals the first mapped span. Selecting SQL highlights every overlapping source-linked operator and fits the graph to those nodes. The graph lays operators out right-to-left, uses cost share, estimated/actual rows, warnings, logarithmic edge width, collapsible subtrees, and selection fitting.
- Exact, inferred, ambiguous, and none links have visible confidence treatments. Rules remain available in tooltips/properties; ambiguous candidates can be selected. Graph nodes support pointer and Enter/Space selection. Theme-aware styles cover the graph and source decorations.
- Workbench API failures render explicit 400, 404, 409, 413, and 422 states with retry for manifest, source-map, and graph query failures. `plans not stored`, omitted occurrences, unmatched statements, empty operator graphs, and source-map build errors have explicit states; `PlanChanges` displays explicit errors without inventing fallback data.
- The `PlanChanges` panel retains its prior comparison role but exposes occurrence selectors. It never picks the first same-shape or repeated document when the occurrence is not unique; the user must choose an exact baseline and candidate occurrence.

## RED/GREEN and verification

- Initial focused `VariantPanel` and `PlanChanges` tests were red before the UI/API adaptation (four failures in each focused file), exercising the absent graphical viewer and removed plan endpoint behavior.
- The direct Monaco lifecycle test first failed because mount ordering registered the cursor listener twice. The implementation now waits for the editor-ready state before installing decorations/listeners and updates the callback ref in an effect, eliminating the duplicate listener and render-time ref read.
- GREEN focused lifecycle command: `npx vitest run src/components/sql-editor/PlanSqlViewer.test.tsx --reporter=dot` — **1 test passed**. It asserts exact decoration/reveal offsets, the latest callback after rerender, and listener disposal on unmount.
- `npm run typecheck` — passed. `npx oxlint src/components/sql-editor/monaco.tsx src/components/sql-editor/PlanSqlViewer.test.tsx` — passed.
- Final `npm run check` — exit 0; **58 test files and 298 tests passed**, with lint and typecheck successful. Full-suite duration: 228.94 seconds.
- The initial Task 6 build completed before the final Monaco readiness guard was added. The final review-fix build is recorded below. Vite reports the app entry and Monaco chunks exceed 500 kB; the plan graph remains a separate lazy chunk.

The Vitest run prints two non-failing `AggregateError` diagnostics. Each contains `ECONNREFUSED` to `::1:3000` and `127.0.0.1:3000`, with stack frames only at Node's `internalConnectMultiple` / `afterConnectMultiple`; no caller or test name is present. They occurred in earlier full-suite runs as well. I checked the changed fixture tests: their `stubFetch` route table returns a local 404 for missing API routes, and the changed test files contain no literal `localhost:3000` or direct `fetch()` call. The requesting code remains unidentified, so these runs are not described as having pristine console output.

`npm run check` also emits Node `[DEP0190] DeprecationWarning` because `scripts/check.mjs` starts a child process with `shell: true`; that script is unchanged in this task. There were no failing assertions or type/lint errors in the final run.

## Files changed

- API contracts and query client: `api/client.ts`, `api/queries.ts`, `api/types.ts`, with client tests.
- Plan viewer components: `components/plan/PlanGraphView.tsx`, `PlanPropertiesPanel.tsx`, `PlanStatementSwitcher.tsx`, `PlanSourceView.tsx`, `PlanWorkbench.tsx`, and `PlanErrorState.tsx`.
- Existing UI integration: `VariantPanel.tsx`, `PlanChanges.tsx`, `CompareDetail.tsx`, `OperationPage.tsx`, `SqlEditors.tsx`, `sql-editor/monaco.tsx`, `lib/sqlDiff.ts`, and `index.css`.
- Focused and adapted tests: `VariantPanel.test.tsx`, `PlanChanges.test.tsx`, `CompareDetail.test.tsx`, `OperationPage.test.tsx`, and `sql-editor/PlanSqlViewer.test.tsx`.
- Dependency pin: UI `package.json` and `package-lock.json`.
- Plan tracking: `plans/088-plan-visualization.md` and `plans/README.md`.

## Self-review and limits

The renderer receives graph contracts only. Links and selections are scoped by raw document SHA plus ordinal, and API lookups use stable `node.id`; native node IDs are never used as API keys. SQL editor ranges always derive from the exact source-map text. The dashboard does not store or modify SQL or plan data, and no live database writes were performed.

Component tests mock the React Flow canvas and Monaco adapter boundary. The direct Monaco test covers callback and decoration lifecycle behavior. A synthetic Chrome smoke rendered the actual `PlanGraphView` and `PlanSourceView` components through Vite; it did not run the full dashboard against a live API or perform UAT. Task 7 still owns user-facing documentation, dead-path cleanup, the complete `just verify` gate, and final plan history bookkeeping. Task-level review of Task 6 remains pending.

## Scoped re-review fix round 1 (base `7d173fe`)

### Findings fixed

- `PlanGraphView` laid out each document from row zero. `planGraphLayout.ts` now offsets each tree by the completed previous document bounding box and a fixed inter-document gap. Its regression test checks actual node coordinates from the renderer-neutral layout function.
- `PlanWorkbench` inferred statement ownership by parsing the numeric prefix of a node ID. `planSourceLinks.ts` now resolves the document-scoped graph `node.id` against graph node metadata and uses its explicit `statementIndex`; IDs are treated as opaque. If graph metadata is unavailable for a link, the workbench uses a unique manifest occurrence when one exists and leaves multi-occurrence ownership unresolved.
- Unmatched occurrences, source-map errors, and omission notices now follow the displayed document/statement occurrences and include document labels instead of using the first occurrence in the batch.
- The browser smoke exposed two React Flow rendering issues: custom nodes had no source/target handles, and React Flow set the custom card subtree to `pointer-events:none` because the nodes are neither draggable nor selectable. Node cards now provide invisible source/target handles and re-enable pointer events on the card, preserving the non-draggable canvas while allowing pointer and keyboard selection.

### RED/GREEN evidence

- Against the reviewed layout behavior with the document offset temporarily removed, `npx vitest run src/components/plan/PlanGraphView.test.tsx --reporter=dot` failed at the real-coordinate assertion: `expected 77 to be greater than 205`. Restoring the offset made the test pass.
- Against the reviewed numeric-prefix parser, `npx vitest run src/components/plan/planSourceLinks.test.ts --reporter=dot` failed because it produced `[0]` instead of `[4, 1, 0]` for opaque stable IDs. Restoring metadata lookup made the test pass.
- Final focused command: `npx vitest run src/components/VariantPanel.test.tsx src/components/plan/planSourceLinks.test.ts src/components/plan/PlanGraphView.test.tsx --reporter=dot` — **3 files and 7 tests passed**. The VariantPanel test mock now provides the React Flow `Handle` and `Position` exports; its unmatched-state assertion checks the new per-document label.
- `npm run typecheck` and scoped `npx oxlint` on the touched UI and tests both passed.

### Browser evidence and limits

- A temporary Vite fixture rendered the real `PlanGraphView` and `PlanSourceView` with two synthetic graph documents. Headless Chrome 155 and the built-in CDP WebSocket reported `layout=pass;first=4;second=3;gap=99`, measured from actual React Flow node bounding boxes. It rendered 10 edge path elements for the five synthetic graph edges.
- CDP pointer input hit the graph button (`elementFromPoint` hit the button; computed pointer events were `auto`) and the graph selection callback returned `0:first-path-0`. CDP mouse input and Ctrl+A in the mounted Monaco editor returned SQL selection `0:50`; three source decorations were present.
- The first smoke run found the pointer-events and missing-handle defects. The temporary fixture initially omitted application CSS, which caused a parent-size warning; the repeat imported `index.css`. After the fixes, the Vite output had no repeated React Flow handle/container warnings. The smoke used synthetic component data and did not exercise the workbench API against a running dashboard or perform UAT.

### Final verification

- Final `npm run check` — exit 0; **60 test files and 301 tests passed**, with typecheck and lint passing. Duration: 301.52 seconds. The run printed two non-failing `AggregateError` diagnostics with `ECONNREFUSED` to `::1:3000` and `127.0.0.1:3000`; both stacks only identify Node `internalConnectMultiple` / `afterConnectMultiple`, and no caller/test was named. The source of these attempts remains unidentified. Node also printed the existing `[DEP0190]` warning from `scripts/check.mjs` using `shell: true`.
- Final `npm run build` — exit 0; the graph remains a separate lazy chunk (`PlanGraphView-BZjLv50g.js`, 175.53 kB). Vite reports the existing app-entry and Monaco chunks exceed the 500 kB advisory threshold.
- Review-fix round 1 is implemented and awaits scoped re-review. Task 7 still owns the full `just verify` gate and final plan history bookkeeping.
