# Dashboard quality iteration design

Status: APPROVED for autonomous implementation by the operator on 2026-11-10.
Baseline: `a274b0ecca09bd447c875849799194aea724bcd7`, inspected 2026-11-10.
Request: iterate until each of the user's 14 dashboard categories scores at least 9.0/10.

## Intent and completion

Make the existing dashboard readable, efficient and trustworthy for an operator inspecting agent sessions,
comparing SQL and reviewing mutation requests. Preserve the established shadcn/Base UI visual language,
English product copy, Inter/Geist Mono typography and current routes. This is a quality programme across
the existing product, not a replacement application.

The previous 7.2 score is a preliminary expert assessment, not a usability study. A score is not a test
result. Freeze the criteria below before changing the product; do not raise scores by weakening criteria,
redefining scope or omitting difficult states. Each 9.0 needs observed evidence, remaining limitations and
an explicit rationale. Untested categories remain unverified; a successful build alone cannot establish 9.0.

## Approaches

1. **Proposed: incremental improvement of existing components and evidence flows.** Fix reproducible
   workflow defects, then improve comparison presentation, responsive layout and shared interaction
   states. Reuse existing backend projections and Plan 125 wherever they own the required meaning.
   This preserves familiar navigation and permits regression checks after each increment.
2. **Visual restyling alone.** Lower implementation cost, but cannot resolve lost filters, ambiguous
   evidence, inconsistent copy behaviour or accessibility. Does not meet the requested scope.
3. **Full dashboard redesign.** Could change navigation and composition radically, but discards the
   established design and adds migration risk without evidence that replacement is needed.

The incremental approach is the agent's recommendation, not yet an operator-approved selection.

## Observed baseline

- Browser: actual frontend built from current main, served locally with intercepted synthetic API data.
- Examined Activity, session optimization, compare operation and Settings at 1440px; Activity at 390px.
- Reproduced Activity search reset after Activity -> session -> Activity.
- At 390px the page itself fits, but filter rows occupy much of the initial viewport; operation rows
  scroll horizontally and session names are heavily truncated.
- Compare detail displays several identical copy icons and repeats some evidence context.
- SQL editor uses 12px text and 19px line height; short SQL still occupies a large editor frame.
- Existing code distinguishes unavailable evidence, differing conditions and provisional comparisons.
- Profiles, Usage, mutation dialogs, dark mode, full keyboard/screen-reader paths and large datasets
  were not fully exercised by the initial assessment. They must be examined before assigning final scores.

## Proposed changes

### Navigation and operator context

Preserve Activity search, time range, facets, toggles and expansion across detail navigation and browser
back/forward. Prefer validated route search state for shareable selections; keep expansion and scroll
restoration separate. Use bounded state and tolerate storage unavailability. Return links must restore
the originating view and its selection, including session/agent-session comparison state.

### Comparison and evidence

Use [Plan 125](../../../plans/125-optimization-evidence-clarity.md) as the authoritative design for failed
attempt eligibility, available statement/table IO, independent Results/IO/Policy outcomes, selected-case
timing aggregation and consolidation of SQL actions. Its direction is already recorded, but its older
implementation status is explicitly not approval. Approval of this design selects all six Plan 125 tasks,
in their existing dependency order, plus the quality work described here. Keep Plan 125's status and
evidence separate; do not mark it complete for a partial delivery.

Show the active A/B pair, conditions and provenance close to the result. Make gains, regressions,
equivalence and coverage scannable without hiding mixed outcomes behind a green aggregate. Do not add a
second aggregate CPU/elapsed summary that conflicts with Plan 125's single summary location.

Keep unavailable, zero, error, partial and approximate evidence distinct. Explain unfamiliar terms at
the decision point; use unambiguous labels for condition groups versus baseline/candidate selection.

### Typography, layout and interaction

Use shared type/spacing tokens and component variants. Start with 14px SQL text and readable 13px or
larger supporting text, then verify actual density; pixel sizes are a proposal, not evidence of quality.
Use tabular numerals and explicit units. Adjust editor height to content within finite bounds and retain
manual resizing and long-document limits.

Make copy actions name their payload: baseline SQL, candidate SQL, diff, summary or reproduction script.
Use one shared success/failure/manual-copy behaviour. Preserve exact side/case bindings and offer scripts
where the SQL workflow needs them. Remove duplicated actions and migrate every capability before removing
its previous entry point.

On narrow screens, provide a compact filter entry point with active-filter count and clear/reset access,
while keeping search and important status visible. Session titles should wrap where needed. Present the
key operation facts without forcing horizontal scrolling for the basic task; retain local, labelled
scrolling for genuinely wide analytical tables and SQL. Keep desktop filtering efficient.

Provide a skip link, visible keyboard focus, meaningful accessible names, keyboard-operable controls,
dialog focus return and non-colour status cues. Honour reduced motion. Verify zoom/reflow and theme contrast.
Show local loading/progress, success, error, retry and disconnected/stale states where the operator acts.

## Acceptance matrix

The numbering follows the user's list, which omits 14.

| Category | Evidence required to justify at least 9.0 |
|---|---|
| 1 Typography | Readable real screenshots at desktop/narrow widths and 200% zoom; SQL, units and numeric columns remain legible; no clipped essential text. |
| 2 Hierarchy | On successful, mixed, failed and approximate comparisons the pair, conclusion, limitation and next action are visible without opening secondary details. |
| 3 Spacing | Shared spacing/type rules work across Activity, sessions, operations, Usage, Profiles, Settings and approvals; long labels and errors preserve alignment. |
| 4 Layout/density | At 390/768/1440/1920px no unintended page overflow; compact filters and useful initial content; short SQL avoids a disproportionately empty frame. |
| 5 Colour/contrast | Measured WCAG AA text contrast and applicable non-text contrast in both themes; status and deltas have text/symbol cues; focus remains visible. |
| 6 Components | Shared buttons/fields/tabs/status/copy behaviours; equivalent actions behave consistently across all inspected surfaces. |
| 7 Information/navigation | Session/operation/agent-session and Usage drill-down paths have clear destinations and return paths with preserved relevant state. |
| 8 Workflow | Search/facets/selection survive detail round trips and back/forward; A/B, case and aggregate updates remain correct; copy/reproduction require no unnecessary navigation. |
| 9 Discoverability | Expand, filter, copy and detail actions have clear names, visible hover/focus/selected states and predictable placement; basic actions work without tooltip discovery. |
| 10 Data/SQL | Zero/null/long values, baseline zero, large SQL and wide tables remain usable; A/B labels and units are explicit; copying preserves the intended original content. |
| 11 Trust | Direct/independent/approximate, missing/partial evidence and mixed IO/policy outcomes are accurately distinguished; no fabricated counters or verdicts. |
| 12 Feedback | Exercise pending, update, empty, rejected, timeout, failed, expired session, disconnect/reconnect and copy/save failure; local next steps and stale-data handling are clear. |
| 13 Language | Concise English terminology consistent across headings, tables, dialogs and errors; terms needed for decisions have contextual explanations. |
| 15 Accessibility/resilience | Keyboard end-to-end workflows, focus return, accessible names, reduced motion, 200% zoom/reflow, contrast measurements, automated accessibility scan and representative large fixtures; disclose limits of screen-reader/performance coverage. |

## Iteration and verification

After approval, write an implementation plan and work in an isolated branch/worktree. Establish focused
baseline checks. Use behaviour regressions for state preservation, evidence semantics and keyboard flows;
do not add tests that only mirror CSS. Review each task, then the full branch, fixing findings before
claiming readiness.

Use anonymous synthetic fixtures and local interception/seeding. At least one integration fixture must
seed a synthetic journal and serve it through the actual dashboard backend, exercising journal -> DTO ->
UI for Plan 125's mixed outcomes, missing/zero IO, and selected-case sum/mean aggregation. Intercepted API
responses alone cannot establish those contracts; reserve interception for isolated views and transient
network states. Cover direct compare, independent measure,
cross-condition compare, missing/zero values, one policy regression despite lower reads, partial IO,
long labels, large bounded SQL, empty history, profile editing and a synthetic approval request. No real
mutation approval or database workload is part of this design. Add Usage and Profiles fixtures rather than
grading their empty states as representative. Capture screenshots and the exact exercised assertions.

Freeze a large synthetic journal of 500 sessions and 10,000 operations, including a 1,000-operation
session, 50 versions and a 20-case comparison. Preserve existing server/client paging and caps: a large
stored journal is not permission to render every record. Test search typing, a facet toggle, session
expansion, A/B selection and case changes, plus successive history pages. On this host's local release
build, after warm-up, take 20 samples per action: local typing/filter/expand input-to-paint p95 <= 200ms;
A/B or case-change settlement p95 <= 500ms after response data is available; no action causes a main-thread
task exceeding 200ms. Initial local route settlement target is <= 2 seconds after the required responses,
with loading feedback during delays. Exclude intentional simulated network latency and first Monaco
download from local CPU measurements, recording their separate loading behaviour. Revisit these proposed
thresholds only with explicit operator agreement, never silently to obtain 9.0.

Record dataset sizes, render/interaction timings, browser version and the test host; report observations
without claiming production-scale performance. Automated accessibility checks supplement keyboard and
screen-reader checks; they do not replace them. If a required environment is unavailable, mark that evidence
pending and do not claim every category reached 9.0.

Repeat: implement -> focused checks -> browser inspection -> task review -> update category evidence.
When all categories meet their frozen criteria, complete whole-branch review and run Windows `just verify`
on the final product tree, as required by [verification](../../agents/verification.md). Inspect failed-stage
logs before fixes. Run explicit changed-C# formatting if backend code changes. Keep each gate's logs in a
unique directory. Update Plan 125 and the plan index with actual work/evidence when applicable.

## Boundaries

Preserve SQL execution, resolved-credential protection, mutation policy, retention, bounded output and
comparison eligibility/provenance invariants. Do not reinterpret unknown measurements as zero. Existing
synthetic evidence is historical context, not proof for the changed tree.

No installation, dashboard restart, live database writes, mutation approvals, merge, push or publication
is requested here. Keep the resulting branch reviewable. Linux remains unverified unless separately run.
Do not commit the proposal merely to satisfy a skill convention; commit only within the subsequently
selected delivery workflow.
