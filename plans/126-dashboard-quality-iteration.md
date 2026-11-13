# Dashboard quality iteration implementation plan

> Use superpowers:subagent-driven-development. Execute the dependency graph below;
> independent file ownership permits parallel work. Track actual completion and evidence.

**Goal:** Improve all 14 dashboard quality categories to a defensible >=9.0 using the approved evidence matrix.
**Architecture:** Preserve shadcn/Base UI and current routes. Extend existing evidence projections through
Plan 125, preserve operator context, and verify actual rendered workflows with synthetic data.
**Tech stack:** .NET/C#, SQLite, React/TypeScript, Vitest, Playwright, Windows PowerShell.
**Spec:** [Approved design](../docs/superpowers/specs/2026-11-10-dashboard-quality-design.md).
**Baseline:** a274b0ecca09bd447c875849799194aea724bcd7.
**Status:** COMPLETE and integrated into main (2026-11-13). Combined product `0420529c` passes Windows verification in the destination checkout: UI570, Core4919/11skips, MCP537/4skips; Release browser67/67,13 axe scans with zero violations. [Integration](126-evidence/integration/integration.md). Original all14 category scores9.0-9.2 and native NVDA acceptance retain their recorded revisions: [scorecard](126-evidence/scorecard.md), [native NVDA](126-evidence/browser/native-accessibility/nvda-acceptance.md).

## Global constraints

- Preserve credentials, mutation policy, execution semantics, bounded output, original SQL and provenance.
- Execute all six tasks of [Plan 125](125-optimization-evidence-clarity.md), tracking its own completion.
- Unknown is not zero; policy outcomes must be recorded, not inferred from lower reads.
- English copy, existing Inter/Geist Mono and shadcn/Base UI; no dependency replacement.
- Synthetic databases/fixtures only. No real DB workloads or mutation approvals.
- Thresholds and all category criteria in the approved design remain frozen.
- Use task reviews followed by whole-branch review; Windows just verify on the final product tree.
- Keep changes isolated. Autonomous implementation does not imply pushing or publishing.

## Review focus

1. A lower-read case with slower time must preserve independent IO and recorded policy outcomes.
2. Historical missing counters, partial attribution and approximate pairs must never become exact evidence.
3. Stale route/storage state must not break navigation, wipe unrelated filters or reveal old comparison data.
4. Copy payloads and reproduction scripts must follow the exact selected side/case, even after updates.
5. Narrow windows, keyboard navigation and large bounded fixtures must preserve the core task.

## Task 1: Plan 125 backend evidence

Owner: dedicated implementer. Files: JournalReader.cs, Optimization/*.cs, associated Dashboard C# tests.
Interfaces: additive DTO fields shared with Task 2; agree names before UI implementation.

- [x] Add failing regression tests for successful-evidence eligibility, partial statement IO, all stored table IO,
  independent result/IO/policy outcomes and selected-case sum/mean of medians (Plan 125 Tasks 1-5 backend).
- [x] Run focused tests and observe intended failures.
- [x] Implement preserving existing constructors/contracts where possible; no collectors or policy rewrites.
- [x] Run focused Dashboard tests, explicit changed-C# formatting, self-review and scoped commit.
- [x] Fresh task review recorded; actionable findings fixed with regression evidence and carried into whole-branch review.

## Task 2: Plan 125 comparison presentation

Depends on Task 1 contract. Owner: dedicated implementer; files: comparison/optimization components,
OperationPage, api/types.ts, matching tests. No edits to Activity, shared copy/editor/layout or global CSS
without coordinating ownership.

- [x] Add failing tests for visible error reason, independent Results/IO/Policy with shared case drill-down,
  selected-case timing labels and a single timing summary; assert median values [12,28] -> 40/20.
- [x] Implement Plan 125 frontend tasks: supported IO columns/reasons, useful whole-batch fallback, provenance,
  named side/case script actions, remove obsolete evidence drawer after moving every capability.
- [x] Keep no-data, zero, conditions and group identity truthful; clarify condition badges versus A/B roles.
- [x] Run focused component tests, typecheck, self-review and scoped commit.
- [x] Fresh task review and regressions for fixes.

## Task 3: Activity context and responsive workflow

Owner: coordinator. Files: ActivityFeed, LivePage, router, lib/activitySearch.ts and tests, index.css.
Interface: validated ActivitySearch route state plus bounded sessionStorage restoration for expansion/scroll.

- [x] Add failing tests for search/filter/toggle round trips, browser back/forward and malformed search.
- [x] Implement bounded validated route state; preserve landing defaults and clear-filter behaviour.
- [x] Keep expansion and scroll state across detail round trips without storing SQL/credentials.
- [x] Compact filter disclosure on small screens, active-filter count/reset; wrap session names and show
  basic operation facts without mandatory horizontal scrolling. Retain efficient desktop filtering.
- [x] Run focused regressions and browser checks, commit; fresh task review.

## Task 4: Shared readability, interaction and accessibility

Owner: dedicated implementer. Files: CopyButton, SqlBlock, SqlDiff, sql-editor/monaco.tsx,
AppLayout, ErrorState and matching tests. Coordinator owns global CSS; request any necessary rules.

- [x] Add failing regressions for consistent copy confirmation/manual fallback, named copy actions,
  skip-link/focus navigation and useful retry/error states without unsafe automatic retries.
- [x] Implement unified copy feedback, clearer copy scope and bounded content-sensitive SQL frames;
  SQL 14px with readable line height, retain manual resize and output limits.
- [x] Add skip navigation, visible focus, non-colour state cues and reduced-motion support.
- [x] Run focused tests/typecheck, self-review and scoped commit; fresh task review.

## Task 5: Integration and browser quality iteration

Depends on Tasks 1-4. Files: plans/126-evidence browser harness, screenshots/metrics/report,
synthetic seed helper if needed. Use actual backend for evidence projections, interception for transient errors.

- [x] Build current UI and serve a synthetic seeded journal through actual dashboard.
- [x] Exercise all approved matrix states, including Usage, Profiles, synthetic approval, dark/light,
  keyboard/focus return, 200% zoom and 390/768/1440/1920px; measure applicable contrast and accessibility.
- [x] Seed 500 sessions/10000 operations, 1000-operation session, 50 versions/20 cases. Preserve paging/caps.
  Record 20 samples per action and the frozen 200ms/500ms responsiveness thresholds and 2s settlement.
- [x] Inspect screenshots, record defects and fix with focused regressions; repeat affected browser checks.
- [x] Grade all 14 categories against frozen criteria with remaining limitations. Any unmet criterion
  stays open; do not manufacture scores or treat absent environment evidence as a pass.

## Task 6: Final review and Windows verification

- [x] Whole-branch review against both plans and the approved spec; fix actionable findings.
- [x] Run pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef a274b0ec when C# changed.
- [x] Run Windows just verify with a unique log directory on the final product tree; inspect failures.
- [x] Update Plan 125, this plan, index and human guidance to actual outcomes and evidence.
- [x] Report exact revision, tests, per-category grades and any blocked external acceptance separately.

## Planning self-review

All design sections map to Tasks 1-6. Task 1 produces additive evidence DTOs consumed by Task 2;
Tasks 3/4 have disjoint ownership. Shared CSS is owned by the coordinator. Task 5 verifies integrated
behaviour rather than trusting API fixtures. Task 6 does not replace native/browser verification.

## Current evidence checkpoint (2026-11-11)

Implementation and scoped reviews for Tasks 1-4 are recorded in [backend report](126-evidence/backend-report.md), [comparison report](126-evidence/comparison-report.md), [Activity report](126-evidence/activity-report.md) and [whole-branch review resolution](126-evidence/final-review-resolution.md). Product revision `da51aff7` includes all actionable final-review fixes. Two regression files pass 14/14; typecheck and lint exit 0 (two pre-existing hook warnings). This earlier checkpoint is superseded by the final results below. Native NVDA is blocked by the locked Windows desktop. No all-category 9.0 claim is made.

Final product checkpoint: `5f05ca07`. [Browser acceptance](126-evidence/browser/acceptance-5f05ca07/report.md):72/72, zero runtime errors, twelve axe scans without violations, all five twenty-sample performance series within frozen limits. Native browser zoom200%:four/four routes with reflow. Final contrast samples repeated in both themes. Branch format13 C# files PASS. [Final Windows gate PASS](126-evidence/verification.md): UI543, Core4873/11skips, MCP537/4skips; exit0. Native NVDA remains pending operator-unlocked Windows desktop.

[Current category scorecard](126-evidence/scorecard.md):13 categories9.0-9.2; category15 provisionally8.5 pending native NVDA. Plan126 and the operator goal remain IN PROGRESS. Native zoom and contrast have current supplemental evidence; unlocking Windows and then completing native screen-reader workflows is the remaining acceptance dependency.

## Post-unlock completion

The operator explicitly confirmed Windows unlocked. Actual native NVDA acceptance passes17 steps, including spoken recorded-policy failure and original SQL, case/A-B changes, copy status and focus return. Category15 now9.0; all14 categories meet9.0-9.2. All source remains unchanged from Windows-verified5f05ca07, so no repeated gate is required. Earlier pending checkpoints above are historical. Plan126 is COMPLETE on the feature branch; no merge, push or installed-dashboard replacement.

Late native-test finding: ordinary CopyButton temporarily disabled the focused element. Commit85c75cb2 fixes focus preservation without allowing duplicate activation. Regression RED ->11 focused tests GREEN; five actual-browser copy checks PASS; full17-step NVDA repeat PASS including copy focus. Windows gate is being repeated on85c75cb2. Earlier completion/checkpoints are superseded until this gate passes.

## Final acceptance on85c75cb2

Repeated Windows just verify PASS, exit0,11m48s: UI544/544; Core4873/11skipped; MCP537/4skipped. Actual NVDA17/17 and five focused browser copy checks PASS, including retained focus after copy. Earlier pending/completion checkpoints above are historical and superseded. [Verification](126-evidence/verification.md), [scorecard](126-evidence/scorecard.md), [native acceptance](126-evidence/browser/native-accessibility/nvda-acceptance.md). Plan126 and the all-category goal are complete on this feature branch.
