# Dashboard visual QA — 2026-10-25

final result: passed

This is visual acceptance of the selected layouts within the operator-approved existing-data scope. It is not a claim that illustrative mock measurements are production data or that missing journal fields were implemented. The operator explicitly chose to label missing data without expanding contracts.

## Visual truth and rendered implementation

- Activity: `docs/design/dashboard-2026-10-25/Activity.dc.html`, frame **3a**.
- Operation: `docs/design/dashboard-2026-10-25/Operation v2.dc.html`, Measure / Query / Mutate / Compare.
- Session: `docs/design/dashboard-2026-10-25/Session Optimization.dc.html`, turn **4**, frame **4a**, SQL diff / IO / Plan. Its IO tab takes precedence over the earlier separate IO file.
- Settings: `docs/design/dashboard-2026-10-25/Settings.dc.html`, all six sections.
- References were rendered with their real `support.js` over loopback HTTP. [Reference identity](plans/121-evidence/visual/reference-identity.json) confirms the repo copies and source export have identical normalized text, including support.js. Only line endings differ.
- Implementation: the actual React dashboard in worktree `dashboard-visual-fidelity`, first through its Vite development server and finally through the built `ui/dist` bundle. Browser-context API fixtures are explicitly isolated; no database results were seeded, no target database writes were made, and no mutation approval was supplied.
- Tooling: Playwright 1.64.1 and its Chromium 1248 runtime. Computer-use inventory was empty; the operator explicitly approved this fallback and installation.

## Capture normalization

[Evidence gallery](plans/121-evidence/visual/index.html) links every primary state. [Mock matrix](plans/121-evidence/visual/matrix-mock.json), [before matrix](plans/121-evidence/visual/matrix-before-baseline.json), and [after matrix](plans/121-evidence/visual/matrix-after.json) record concrete screenshot paths and browser viewports.

Final passes use 1280 × 1400 CSS px for Activity, Operation and Settings; the session app is 1120 × 1400. Source Session 4a is a fixed 1120px content frame on a wider canvas. Source Activity/Session element screenshots include a 1px outer border (1282px / 1122px); comparison sheets remove this border to match 1280px / 1120px app content. Source page-level Operation captures omit only the external type-preview chooser. There is no content scaling. Device scale factor is 1, light theme, browser zoom 100%. Earlier 1000px-high captures are retained as iteration history. Final narrow checks use 390 × 1400; wide tables scroll inside their own containers.

The before matrix runs the original `7c1b2fbf` UI from a detached baseline checkout, with the same isolated fixture builders and states. Final screenshots use the built current UI, not a previously installed dashboard. Numeric values, IDs, SQL length, plan topology and session counts differ from the illustrative mock; these are data differences.

## Full views and readable details

| Screen / state | Full-view pair | Focused evidence |
|---|---|---|
| Activity 3a | [comparison](plans/121-evidence/visual/compare-activity-default.png) | [header, filters and first rows](plans/121-evidence/visual/detail-activity-default.png) |
| Operation measure | [SQL and plan](plans/121-evidence/visual/compare-operation-measure-sql-and-plan.png) | [header and metric strip](plans/121-evidence/visual/detail-operation-measure-sql-and-plan.png) |
| Operation query | [Results](plans/121-evidence/visual/compare-operation-query-results.png) | Header/metrics/tab text is readable in the full pair; unavailable cells are explicitly explained. |
| Operation mutation | [Approval](plans/121-evidence/visual/compare-operation-mutation-approval.png) | Header/metrics/tab text is readable in the full pair; the existing operator approval interface remains linked. |
| Operation compare | [SQL diff](plans/121-evidence/visual/compare-operation-compare-sql-diff.png) | Metric strip, contextual parameters and both diff sides are readable in the full pair. |
| Session 4a SQL diff | [comparison](plans/121-evidence/visual/compare-session-sql-diff.png) | Controls, A/B and version labels are readable at native width. |
| Session 4a IO | [By statement](plans/121-evidence/visual/compare-session-io.png), [Whole batch](plans/121-evidence/visual/compare-session-io-whole.png) | Additional-column and statement-detail screenshots are linked from the gallery. |
| Session 4a Plan | [comparison](plans/121-evidence/visual/compare-session-plan.png) | [session header and versions](plans/121-evidence/visual/detail-session-plan.png); operator and SQL-click states are also saved. |
| Settings | [full page](plans/121-evidence/visual/compare-settings-full.png) | [Database](plans/121-evidence/visual/compare-settings-database.png), [Mutations](plans/121-evidence/visual/compare-settings-mutations.png), plus all six individual section captures in the gallery. |

The source and implementation images were opened together in these combined comparison sheets. Focused crops were used because the dense full-page settings and session views would otherwise make small text and control dimensions hard to judge.

## Comparison history and fixes

| Earlier finding | Correction | Post-fix evidence |
|---|---|---|
| P1: Activity used separate title/internal-control/filter/search rows, a bordered box, table headings, and tall two-line operation rows. | Unified heading/search/facets, pill filters, session grid and compact operation feed; default Last 7 days; contextual counts and expansion controls. Partial-history counts remain explicitly labelled loaded/shown. | Activity before/after pair and `activity-app-iteration1.png`. |
| P1: Settings repeated headings and put controls below descriptions; section boundaries differed from the reference. | Six bordered cards, a 200px section rail, right-aligned switches/number fields and row dividers. Numeric boxes were measured as 106 × 34 rendered pixels, rather than copying content-box CSS widths into border-box controls. Browser-local tags now sit beside row titles. | Settings full/section pairs and `settings-app-iteration1.png`. |
| P1: Operation headers mixed annotations, notes and facts into a large card, and metric strips were missing their shared border/dividers. | Breadcrumb, compact title/actions/metadata, five measure metrics or four type-specific metrics, and one bordered tab workbench. Stored SQL remains reachable if plan loading fails. | All four operation pairs; `operation-plan-error-fallback-after.png`. |
| P2: Compare return/copy/parameter controls created extra rows above its panel. | Return navigation moved to the panel tab header, parameters and basis to its context rows, SQL reuse below the panel. Full baseline/candidate copying remains available even above diff limits. | Final Operation compare pair, following the earlier `operation-compare-app-before.png`. |
| P1: Session versions, nested boxes and supporting prose pushed the comparison far below the selected 4a structure. | One optimization card, compact version table, dedicated delta columns, A/B circles, keep/agent labels, inline comparison context and shared SQL diff / IO / Plan panel. Provenance remains in each row's details menu. | Session before/final pairs. |
| P2: Tabs stretched across the entire row and their active underline spanned large columns. | Intrinsic-width tab labels and compact spacing. | Session and operation full pairs. |
| P2: Plan SQL and graph panes were too tall, and fit-to-view enlarged cards beyond the reference. | Session SQL 250px, operation SQL 300px, graph 250px, 170px operator cards and capped initial fit zoom. Legend/help text is in normal flow, fixing an overlap found on an intermediate screenshot. | Final Plan and measure pairs. |
| P2: Clicking SQL with a caret did not select linked operators; only a non-empty selection worked. | Bounded one-character caret mapping; API cursor events are ignored. | `session-plan-sql-click-after.png`; RED then GREEN regression logs. |

## Required fidelity surfaces

- **Fonts/typography:** Geist Variable for UI text and bundled Geist Mono Variable for SQL and identifiers. Checked 20px page titles, 14px tables/body, 12–13px metadata and 12px/19px SQL editor rhythm. Wrapping and truncation were inspected in the focused pairs and at 390px.
- **Spacing/layout rhythm:** Checked outer frame crop, content widths, 16px workbench padding, 28px pills, 32px facet controls, version row density, metric dividers, card radii and graph proportions. The final pass caught and corrected Compare's misplaced top controls.
- **Colors/tokens:** White/neutral surfaces, grey dividers and metadata, blue running state, amber stars, violet keep marks, and green reductions match the selected visual language. Existing dark-theme support is retained; this visual acceptance pass is for light theme.
- **Images/icons:** The supplied SQLHarness vector mark is retained. The screens use ordinary UI icons, not illustrative raster assets; Lucide icons and real interactive graph nodes remain crisp at 1×. No fake measurement imagery or bitmap replacement UI was introduced.
- **Copy/content:** Checked navigation, row labels, tabs and control descriptions. Missing fields are explicitly unavailable; SQL search is not falsely advertised in Activity. Guidance about approvals, retention, restart requirements and partial loaded history remains reachable.

## Interaction, console and responsive checks

The final 44-state implementation matrix completed with **zero console/page errors**. It covers Activity expanded/collapsed groups, multi-agent selection, search, Named, archived inclusion; every available operation tab for measure/query/mutation/compare; session A/B changes, side-by-side/unified diff, formatting toggle, IO scopes and additional columns; Plan operator selection; the complete Settings page and six sections, section navigation, a saved numeric field, and the sensitive-data confirmation with Cancel.

[Additional interactions](plans/121-evidence/visual/additional-interactions.json) cover Directory/Target multi-select, empty activity, the custom-audio file chooser (cancelled without upload), and an intentionally injected plan-manifest HTTP404 with readable stored-SQL fallback. The injected 404 is an expected error-state test, not an unexplained production console error. SQL-click highlighting was also verified directly in Chromium.

At 390px, Activity, Settings, Operation and Session were captured and inspected. Persistent controls remain usable. The page's scrollWidth does not exceed clientWidth; dense tables and the graph keep local scrolling/panning. Settings cards and the section navigation wrap rather than widening the document.

## Expected differences and follow-up polish

- The operator approved unavailable states for Activity SQL previews/search, query result cells, statement memory/CPU/elapsed/spills and other fields absent from the existing journal. No values were invented and no persistence/API contract was expanded. Best-selection status is not inferred from a synthetic mock badge.
- Session 4a's older eight-link global navigation is superseded by the four-link navigation shared by the selected Activity/Operation/Settings references. This is shared application chrome, not an alternate session design.
- Existing exact case/repetition/document controls, copy actions, diagnostic/provenance menus and theme control remain available. These produce small utility-control differences from the mock; they preserve the requested existing behavior and evidence access. Other retained versions can be made A/B in the version table before inspecting their plan.
- P3: optional utility labels/icons and positive IO-delta color can receive further polish. They do not obscure values, change the major layout, or block a core task. No actionable P0/P1/P2 visual findings remain within the approved existing-data scope.

## Completion checklist

- [x] Render selected references and the actual app before layout changes.
- [x] Save before, corrected and matched-state browser screenshots.
- [x] Open combined full-view and focused comparisons and fix material drift.
- [x] Verify key interactions, investigate errors and inspect a narrow viewport.
- [x] Preserve real unavailable/error states and existing safety/data contracts.
- [x] Finish the final `just verify` gate, record its exact product tree, then integrate and publish locally.

Delivery: [verification](plans/121-evidence/visual-verification.md), [integration](plans/121-evidence/visual-integration.json), [publication](plans/121-evidence/visual-publication.json). The installed binary served matching JS/CSS bytes and four captured screens with no browser errors; its isolated process was then stopped.
