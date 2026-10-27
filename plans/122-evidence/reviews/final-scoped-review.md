# Plan 122 final scoped re-review

Range: `b30a11e1..a1e5cdb1` (fix `73189eae`, format `a1e5cdb1`). Reviewed only prior findings and new breakage introduced by this fix wave, using the scoped diff, current affected code and final-fix report. No suites rerun, product changes or subagents.

## Finding verdicts

- **Important: contradictory memory retention copy — ADDRESSED.** `src/SqlHarness.Dashboard/ui/src/components/optimization/IoDetails.tsx:20` retains the batch aggregate label and values while removing the false claim that statement memory is never retained. `IoComparison.test.tsx:123` renders the actual caller and asserts both preserved batch labeling and absence of obsolete guidance. This does not claim availability for historical sessions or equate batch medians with selected-run grants.
- **Minor: concurrent old-key resume — ADDRESSED.** `tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs:55` performs eight parallel resumes of the migrated legacy key, asserts every returned handle, and checks all nine legacy operation rows remain unmarked while a fresh key receives version 1. The schema-21 fixture now also removes the four schema-25 grant/spill columns before migration; this repairs an invalid test fixture rather than weakening product migration behavior. Existing historical-row equality assertions remain.
- **Minor: false truncation flag marker absence — ADDRESSED.** `src/SqlHarness.Dashboard/ui/src/components/ActivityOperations.test.tsx:29` explicitly checks that the truncation indicator is absent for the supplied false flags.
- **Minor: measured-after-only grants/spill — ADDRESSED.** `src/SqlHarness.Dashboard/ui/src/components/optimization/IoComparison.test.tsx:112` renders shared StatementMetrics with measured mode, distinct before/after sentinel values, and verifies after-only grants/spill plus absence of before values/arrows.
- **Lint: effect-driven stale column pruning — ADDRESSED.** `src/SqlHarness.Dashboard/ui/src/components/optimization/IoComparison.tsx:29` tracks the previous view and performs a guarded same-component state adjustment on prop transition. The guard converges after one rerender, while visibleColumns immediately suppresses unsupported columns. Entering statement view removes unsupported selections and retains LOB logical; the existing external-rerender switch test checks they do not return on switching back. The removed effect no longer causes the reported set-state-in-effect warning; final-fix report records clean typecheck/lint.

## New breakage in fix diff

None identified. The extra C# diff consists of formatting/import ordering and a static test local function that captures no state. The migration-fixture correction preserves the old-session assertions and now permits the actual full migration path to execute. No runtime mutation policy, classification, credential handling, persistence contract or Vitest worker configuration changed.

## Evidence boundary and corrected diagnosis

The fix report records both minimal forks and threads passing, focused UI checks, two session-evidence tests, typecheck/lint and a final branch-format check. The controller subsequently reports normal final-product UI verification passed **406 tests / 69 files**, and browser evidence passed **14 states** with desktop/mobile inspection. Build passed; remaining .NET/format gate stages were still running at review time. I did not independently rerun those checks and do not claim a completed full gate.

The first stopped gate was interrupted before buffered test output appeared. A persistent worker hang was not established; earlier wording describing a reproduced hang is superseded. The implementation correctly avoids changing worker pool/timeouts based on that unsupported diagnosis.

## Declined to judge

- Final whole-gate success: controller-owned execution still in progress; favorable source review is not its result.
- Incremental final AST-check cost and actual SQLite storage growth: accepted measurement limitation intentionally retained, with no new performance claim.
- Live provider acceptance, Linux, installation, merge and push: outside this fix scope and authorization.
- Untouched behavior already covered by the whole-branch review: not reopened without a concrete new regression; the scoped diff introduces none identified.

## Assessment

**All targeted findings addressed; no new Critical/Important breakage. Spec compliance and fix quality: APPROVED.** The whole-branch review approval is restored for final product `a1e5cdb1`, subject to completion of the required final Windows gate and accurate closeout evidence. Remaining AST/storage measurement caveat is nonblocking and disclosed. This verdict authorizes no merge or publication.
