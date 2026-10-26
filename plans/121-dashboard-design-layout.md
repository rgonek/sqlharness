# Plan 121: Dashboard design layout

Original delivery: DONE; locally integrated into main at `fc83e808`; `just publish fast` completed and installed binary verified. No push. Original base `443f468e`; current main `05a414a4` incorporated before the final gate.

## Approved references

The operator requested implementation of the Claude Design export and selected
Activity 3a (the 1c session feed and 3a filters), Operation v2, and Session
Optimization turn 4 including its final IO tab. Settings uses the six-section
export. The selected references are in
[docs/design/dashboard-2026-10-25](../docs/design/dashboard-2026-10-25/README.md).

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

Status: IN PROGRESS on the same isolated `dashboard-shadcn-fixes` worktree. Fixes for the six additional screenshots and explicit RadioGroup request have passed ten browser states and 53 focused tests. The operator additionally requested Mira/Zinc after these corrections; preset strategy clarification is pending. Full final gate and authorized local publication/restart follow migration. [Evidence](121-evidence/shadcn/followup/verification.md), [gallery](121-evidence/shadcn/followup/index.html).
