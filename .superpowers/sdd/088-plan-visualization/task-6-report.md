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
- `npm run build` completed successfully before the final Monaco readiness guard was added. Vite reported its bundle-size advisory: the app entry and Monaco chunks exceed 500 kB; the plan graph remains a separate lazy chunk. This build was not rerun after the small readiness/callback effect change.

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

Component tests mock the React Flow canvas and Monaco adapter boundary. The direct Monaco test covers callback and decoration lifecycle behavior, but an actual browser render of the final workbench was not performed. Task 7 still owns user-facing documentation, dead-path cleanup, the complete `just verify` gate, and final plan history bookkeeping. Task-level review of Task 6 remains pending.
