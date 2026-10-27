# Plan 121: Dashboard design layout

Original delivery: DONE; locally integrated into main at `fc83e808`; `just publish fast` completed and installed binary verified. No push. Original base `443f468e`; current main `05a414a4` incorporated before the final gate.

## Approved references

The operator requested implementation of the Claude Design export and selected
Activity 3a (the 1c session feed and 3a filters), Operation v2, and Session
Optimization turn 4 including its final IO tab. Settings uses the six-section
export. Those historical references are available in Git at commit
`95eb43b7a705146df261673470f037c62b872612`, under
`docs/design/dashboard-2026-10-25`. The current reference set is
[docs/design/dashboard-2026-10-27](../docs/design/dashboard-2026-10-27/README.md);
it is not the reference set used for this plan's original acceptance.

## Scope and constraints

Implement the layouts in the current React dashboard. Preserve live reconciliation,
uncapped running operations, history pagination, exact case/parameter-set plan
links, SQL readability, operator mutation approval and settings confirmations.
Do not invent metrics, stored results or approval history from reference fixtures.
Preserve the operator's dirty main documents. The operator subsequently authorized local merge into main and `just publish fast` after green verification. Do not push.

## Work

1. Import the selected references and record the version decisions.
2. Consolidate navigation and implement Activity's session feed and filters.
3. Implement operation tabs and the session SQL diff / IO / Plan workbench.
4. Regroup settings into Database, Mutations, Journal & privacy, Agents,
   Dashboard and Model prices.
5. Verify interactions, review the changes and run the required Windows
   `just verify` gate on the final product tree.

## Verification

See [verification](121-evidence/verification.md), [implementation](121-evidence/implementation.md) and [reviews](121-evidence/reviews.md). The original delivery lacked browser acceptance. The follow-up below supersedes that limitation with actual screenshot evidence.
## Visual correction follow-up (2026-10-25)

Status: DONE; visually accepted, final Windows gate passed, locally merged into main at `2fdfc492` and published. Branch `dashboard-visual-fidelity`, base `7c1b2fbf`. No push.
The previous DONE status describes delivery, not visual acceptance.

- [x] Render local exports with support.js and capture selected source screens.
- [x] Run the worktree's Vite app at 127.0.0.1:48122 with explicitly isolated API fixtures; capture before states.
- [x] Correct ActivityFeed/ActivityOperations and shared AppLayout geometry; verify filters, counts and expansion.
- [x] Correct SettingsPage into six cards with two-column rows; preserve save queues and confirmations.
- [x] Correct OperationPage headers, metric strips and tab panels for measure/query/mutation/compare.
- [x] Correct SessionPage, VersionTable, ComparisonPanel/IoComparison and PlanWorkbench geometry.
- [x] Re-capture matched states, inspect full views and readable details; verify narrow layouts and console.
- [x] Run focused checks and final just verify, review, integrate locally preserving dirty main, publish fast and verify installed artifact. No push.

Final Windows gate: UI 384, Core 4718, MCP 535 passed; local xUnit collections sequential. See [follow-up verification](121-evidence/visual-verification.md).

Evidence: `121-evidence/visual/`; reproducible capture uses Playwright with browser-only API interception. Fixtures never reach databases or the installed journal. Missing journal result cells and statement memory remain explicitly unavailable.

Visual acceptance: [design QA](../design-qa.md), [44-state screenshot gallery](121-evidence/visual/index.html), and [follow-up review](121-evidence/visual-review.md). The operator explicitly chose unavailable-data labels without new contracts.

Follow-up delivery: [integration](121-evidence/visual-integration.json), [publication](121-evidence/visual-publication.json). Installed UI assets and four browser screens verified.

## shadcn correction follow-up (2026-10-25)

Status: IMPLEMENTED AND VERIFIED on `dashboard-shadcn-fixes`, base `0b589b40`, in a new worktree. The operator supplied 19 concrete screenshots and requested standard shadcn composition without modifying primitive sources. Default Select alignment is preserved and documented.

Implementation, independent review and 23-state browser QA are recorded in [verification](121-evidence/shadcn/verification.md) and the [gallery](121-evidence/shadcn/index.html). Final Windows gate passed: UI 384, Core 4718, MCP 535; output-only sequential xUnit collections as documented. The operator subsequently authorized publication from this branch and `sqlharness dashboard --restart` after verification. No merge or push.

Delivery: locally published from `08d50998` and ran `sqlharness dashboard --restart`; installed binary and two served UI assets verified. Worktree retained; no merge or push. [Receipt](121-evidence/shadcn/delivery.json).

## Controls, scroll and Mira follow-up (2026-10-26)

Status: IMPLEMENTED, VERIFIED AND LOCALLY PUBLISHED on the same isolated `dashboard-shadcn-fixes` worktree. Fixes for the six additional screenshots and explicit RadioGroup request have passed ten browser states and 53 focused tests. The operator approved merging Mira/Zinc with local customizations, including default Inter and Hugeicons. Migration is implemented, 36 Mira browser states passed, and independent review is clean. Final Windows gate passed on 3f87a261: UI 385, Core 4718, MCP 535. Published locally from this branch, dashboard restarted, installed EXE and 20 UI assets verified; three installed browser states passed. No merge or push. [Evidence](121-evidence/shadcn/followup/verification.md), [gallery](121-evidence/shadcn/followup/index.html).

## Mock alignment and data inventory (2026-10-26)

Status: VERIFIED and LOCALLY PUBLISHED from dashboard-shadcn-fixes at a685e942. Windows just verify passed: UI 386, Core 4722, MCP 535; dashboard restarted, installed EXE/20 assets and three screens verified; actual API basis string confirmed. No merge/push. Original references rendered alongside the Mira application. Comparison tabs/context and header actions aligned to 4a; Plan A/B uses segmented RadioGroup with differentiated Badge labels. Fixed numeric PairBasis JSON contradicting the existing frontend string contract. No shadcn primitive changes or new telemetry/retention. [Data inventory and trim recommendations](121-evidence/shadcn/mock-alignment/data-audit.md), [browser gallery](121-evidence/shadcn/mock-alignment/index.html).

## Standard plan radios and advanced options (2026-10-26)

Verified at aaa346e0 in dashboard-shadcn-fixes: Windows gate passed (UI 386, Core 4722, MCP 535). Delivery follows the expanded scope below. Latest explicit operator decision overrides the previous segmented Plan picker: use visible standard shadcn RadioGroup with styled A/B Badge labels. Repetition moved under Advanced; repeated unmatched notices replaced by one short Advanced note; single-statement batch hint removed; document popup width corrected. Browser checks passed with 16 documents / 4 repetitions at 1280 and 390px, including resize overflow correction. [Evidence](121-evidence/shadcn/plan-options/verification.md), [gallery](121-evidence/shadcn/plan-options/index.html).

## Session information and SQL editor height (2026-10-27)

Implemented at 58a29997 in dashboard-shadcn-fixes. Conversations moved into top Details, Add note before Optimization, all Monaco modes vertically resizable. Redundant Select comparison and repeated explanatory text removed; meaningful evidence limitations retained. Focused 39 tests and browser pointer-resize checks passed. Windows gate passed on 58a29997: UI 386, Core 4722, MCP 535. Published locally, dashboard restarted (PID 11820, port 47800); installed EXE/20 assets and browser resize/placement checks passed. No merge/push. [Information audit](121-evidence/shadcn/ui-information/information-audit.md), [verification](121-evidence/shadcn/ui-information/verification.md), [gallery](121-evidence/shadcn/ui-information/index.html).

## Audit recommendations and joined Plan controls (2026-10-27)

Implemented at bafcd2a5 on dashboard-shadcn-fixes. Latest screenshots select joined Plan radio segments matching Side by side / Unified with A/B Badge labels. Blank metadata and separators omitted; all UI information-audit recommendations applied or confirmed already satisfied, including missing-evidence summaries, removal of unsupported KPIs and accessible full hashes. TypeScript/lint/build, 92 focused tests and ten browser screenshot states passed; final review clean. Windows gate passed on bafcd2a5: UI 388, Core 4722, MCP 535; locally published/restarted and 20 assets/ten installed states verified. The operator rejected the cramped Plan A/B appearance; the visual follow-up below supersedes that control. [Verification](121-evidence/shadcn/audit-recommendations/verification.md), [gallery](121-evidence/shadcn/audit-recommendations/index.html).

## Plan A/B visual close-up correction (2026-10-27)

Verified and locally published at ecd3f0ae: Plan-only balanced segment widths/spacing and equal 16px circular A/B labels. Actual 2x closeups shown before publication; ten browser states pass, final review clean. Final Windows gate passed (UI 388, Core 4722, MCP 535), dashboard restarted (PID 17804, port 47800); EXE/20 assets and ten installed states verified. No merge/push. [Before/after](121-evidence/shadcn/audit-recommendations/plan-control/index.html), [verification](121-evidence/shadcn/audit-recommendations/plan-control/verification.md).

## Local main integration (2026-10-27)

All dashboard-shadcn-fixes follow-ups are locally integrated into main at 3a0f0549, retaining Plan 122 documentation from main. Exact merged Windows gate passed: UI 388, Core 4722 (11 skipped), MCP 535 (4 skipped), 14m30s. Initial unchanged Journal lock timing failure was retained, isolated three times successfully and followed by a full unchanged-tree green retry; no timeout/test/product workaround. Whole-branch source review and two-file C# branch format passed. README bytes and original local documentation deltas preserved; backup outside repo. No push. Dirty feature worktree retained. [Integration evidence](121-evidence/shadcn/integration/verification.md).
