# Dashboard layout follow-up QA

final result: passed

Scope: the operator's Activity, session header/conversations/note, statement-row interaction, mutation notification/dialog, Profiles and Usage corrections. This is visual acceptance of that follow-up, not a new whole-dashboard redesign. Full build/test status is recorded in `plans/121-evidence/layout-polish/README.md`.

## References and capture

Updated the existing `docs/design/dashboard-2026-10-25` exports in place from `D:\Dev\sqlharness-design-changes\2`; Git retains the previous versions. Used current Activity, Session, Profiles, Usage and Mutation Approval proposal 1b. The operator confirmed Conversations metadata on the right. Activity retains the detailed filters explicitly shown in the request.

Captured references and the built application at 1280 × 900 CSS pixels, deviceScaleFactor 1, light theme. Focused comparisons use equal scaling and remove only browser/canvas framing. Narrow application captures use 390 × 900. The application uses intercepted fixture APIs; no real database mutation or operator approval was submitted.

Gallery: [reference/application pairs](plans/121-evidence/layout-polish/index.html). [Browser checks](plans/121-evidence/layout-polish/interaction-results.json).

## Iteration findings and fixes

- P2: Activity mixed control sizes. Removed local `h-8`/`sm` differences; the final Time, Agent, Directory, Target, Starred and Include archived controls all measure 28px tall with 12px text. Removed the loaded-history summary and redundant footer/internal/status controls; expansion is now in the Named row.
- P2: Session and statement disclosures only exposed narrow click targets. Noninteractive parts of the complete rows now toggle expansion; nested links, stars and disclosure buttons retain their own behavior. Both pointer actions and existing keyboard-accessible disclosure buttons remain available.
- P2: Selected stars had a selected surface. The icon now carries the selected state without a background. The directory shares available width with the title, bringing it left at desktop and intermediate widths.
- P2: Session metadata was too prominent and its chevron did not indicate opening. The subtitle uses muted text; the chevron rotates upward when Details opens. Conversations are title, GUID, and right-aligned agent/count on one wrapping row.
- P2: The note used an unnecessary card/title and displayed extra actions after saving. The editor is textarea/Save/Cancel with the requested placeholder; the saved note is a single grey, clickable strip. An independent review identified an accessible-name override; the corrected button exposes the note content and keeps edit guidance in its title.
- P2: Profiles and Usage retained the older flat-table/chart-first layouts. Profiles now uses a searchable sidebar with connection/mutation/variable sections. Usage uses the reference width, scope/range header, five-part KPI strip, Agents, sortable Top SQL, tables/waits and dimensions. These sections compose the installed Card/Header/Title/Description/Content components; custom card borders were removed. Existing charts and full workflow accounting remain reachable in the details disclosure.
- P2: Mutation notification/dialog lacked the reference hierarchy. Notifications now use the installed standard shadcn Base Mira Toast, with target/environment/countdown and Review; they are retained until the request resolves, cannot be swiped away, and are hidden while the approval dialog is open. The standard Dialog uses a compact 720px layout, context, warning, SQL, parameters and expiry/action footer.
- P2: Terminal mutation status could be obscured by the positioned session table after moving the pending notifications to the standard Toast. Restored its previous z-40 layer; an elementFromPoint check confirms the terminal status is above the table.
- P3 optical correction: the standard Mira switch used a fractional 16.6px track. The sole geometry change is a 16px track; every tested 14px thumb now has exactly 1px above and below it.

The final pairs and focused captures were opened and visually compared after correction. No unresolved P0/P1/P2 issue remains within the requested appearance/interaction scope and the data constraints below.

## Required fidelity surfaces

- Typography: retained the installed Inter/Geist Mono shadcn Mira preset, rather than replacing its typography with custom Geist component styles. Checked muted metadata, title/GUID ordering, control sizes and table wrapping.
- Spacing/layout: checked equal control heights, expansion placement, directory columns, note strip, Profiles sidebar/detail widths, Usage maximum width and KPI/section rhythm, and mutation dialog width/footer. At 390px all four tested pages have no document-level horizontal overflow; dense tables retain local scrolling.
- Colors/tokens: semantic muted, background, border and destructive tokens; transparent selected session stars; even switch thumb insets. Existing dark tokens remain in use; visual acceptance was for light theme.
- Assets/icons: reused the application logo and installed Hugeicons set. No raster replacement UI or newly invented illustration assets were introduced.
- Content: real data fields remain readable. Missing evidence is not replaced with mock measurements. Save/Cancel and mutation decision labels remain clear.

## Expected differences

- Per-agent model/session/token/cost values, SQL text/read counts/last-run statistics, profile use counts and parsed variable choices are absent from the existing statistics/profile contracts. The UI uses recorded aggregates, readable rule text and explicit unavailable states. No backend or persistence contract was expanded for this visual request.
- Profiles links to the general Usage overview, and the existing default profile scope remains unchanged. It does not claim that general navigation is selected-profile usage.
- The pending toast exposes target, environment and countdown. The review dialog retains exact submitted SQL and its parameter table, formatting/copy tools, expiry checks, the one-second approval delay and Enter guard. The reference's values-inline SQL mode, agent/session labels and TTL progress fraction are not synthesized from fields absent from the pending-request contract. The exported mock's external Monaco viewer remained in its loading state; the comparison therefore verifies the dialog's chrome and SQL-region geometry, while the application displays readable SQL.
- Additional recorded session facts remain in Details. Its extra facts can make that disclosure taller than the mock; the requested subtitle, chevron and conversations row were checked separately.

## Verification

The browser matrix passes 28 checks with zero console/page errors, including selected-star transparency, whole-row interactions, independent disclosure behavior, Details arrow, note save/cancel/re-edit/accessibility, profile search/selection, Usage sorting/range, switch geometry, and 390px overflow checks. Mutation capture verifies open/close without submitting a decision. Unit and complete repository gates are recorded separately with their actual outcomes.

Final repository gate: PASS, UI 408; Core/Dashboard/CLI 4771 passed/11 existing skips; MCP 535 passed/4 existing skips. Core collections were serialized using the ignored Debug runner settings documented in the evidence README; no timeout or test selection changed.
