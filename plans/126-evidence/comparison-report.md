# Plan 126 Task 2 comparison presentation

Owner scope: comparison/optimization components, OperationPage, api/types.ts and matching tests. Coordinator additionally authorized SessionVersionDiff slots. Shared agent owns SqlDiff and ReproScriptButtons uniform copy feedback. No global CSS, Activity or router edits by this task.

## Contract agreement

Agreed with backend implementer before consuming fields. JSON uses camelCase:

- `OptimizationComparison.outcomes`, `OptimizationVersion.outcomes`: nullable `OutcomeCounts`: resultsEquivalent/resultsDifferent/resultsUnavailable, ioBetter/ioSame/ioWorse/ioUnavailable, policyPass/policyFail/policyUnavailable (counts).
- `.caseOutcomes`: `CaseOutcomeDetail[]`: caseKey, label, resultsEquivalent nullable bool, ioOutcome (better/same/worse/unavailable), policyVerdict (pass/fail/null), policyUnavailableReason, policyReasons[], readsBefore/After, elapsedBeforeMs/AfterMs, cpuBeforeMs/AfterMs. Failure-first stable ordering, original directed pair only; reversed/independent/approximate policy unavailable.
- `OptimizationVersion.metricCases`: caseKey, caseId, label, varyingFingerprint nullable, logicalReads/scans/elapsedMs/cpuMs nullable. Same-group selection matches caseKey; cross-group matches unique varyingFingerprint. Unknown contributors remain unavailable. Legacy elapsedMedianMs must not be relabelled as summed medians.
- Table IO adds nullable dataLogicalReads/physicalReads/readAheadReads/lobLogicalReads/lobPhysicalReads/lobReadAheadReads Before/After fields; existing reads remain data + LOB total.
- Statement metricAvailabilityBefore/After dictionary keys logicalReads/scans/lobLogicalReads/cpuMs/elapsedMs/grantRequestedKb/grantGrantedKb/grantMaxUsedKb/hasSpillWarning: status available/unavailable, readable reason nullable, contributingCases, selectedCases. Numeric nulls remain authoritative.
- Shared SqlDiff and SessionVersionDiff accept baselineActions/candidateActions ReactNode slots. ReproScriptButtons labelPrefix names side/version/case while preserving exact raw case mapping.

## Implemented

- Visible operation failure/cancellation diagnostics reuse describeError and render recorded errors as escaped text; no-message fallback remains explicit. Operation and comparison errors offer manual retry. Failed compares whose optimization endpoint returns 404 retain their heading, recorded diagnostics, SQL/details and partial variants, with an explicit no-eligible-successful-evidence explanation.
- Results, IO and recorded Policy dimensions remain independent. IO/Policy open the same case-detail component with failure-first stable ordering, complete before/after/delta metrics, exact recorded reasons and keyboard focus return.
- Version timing derives only from per-case medians, using selected-case sum/mean with missing contributors unavailable; Scans and CPU are visible. Active cross-condition pairs use matched varying identities, with selected/retained coverage. Other condition rows retain their own sum with a visible note. Numeric condition badges distinguish condition identity from A/B roles.
- Supported whole-batch IO columns include separate data/LOB logical, physical/read-ahead and LOB physical/read-ahead. Zero stays zero. Optional columns with no recorded values are absent; unsupported Scans is absent. Missing/partial per-side statement evidence shows metric reasons/coverage. No trustworthy statement IO replaces the empty grid while preserving statement/status/object/plan information and a Whole batch action.
- Whole-batch assessment remains truthful while confirmed partial statements remain inspectable. Verdict basis/partial-attribution explanation is local to IO.
- Removed Comparison evidence and scripts drawer and its duplicate aggregate CPU/elapsed. Kept provenance links near comparison context, moved named side/version/case script actions into SQL diff slots, retained exact side-specific raw case mappings, kept Plan navigation/downloads, and preserved PostgreSQL buffers in IO. Provenance/plan link targets have at least 24px height following coordinator browser axe finding.

## Verification

- Initial RED: five intended behavior failures plus missing shared case-details component; IO RED: two intended behavior failures.
- Additional self-review RED→GREEN regressions: partial attribution trapped statement navigation; unsupported Scans column; ambiguous cross-condition fingerprint totals; baseline Results coverage ignored selected case; object names lost in no-IO replacement; ineligible failed compare 404 hid its diagnostics/retained partial evidence.
- Latest affected tests: VersionTable/ComparisonPanel 24/24; IoComparison 16/16; ambiguity helper covered by boundary rerun 16/16 across IoComparison/helper. All observed PASS with exit 0 where final commands captured direct process exits.
- Latest typecheck (2026-11-11) exited 2 solely on coordinator-owned `components/sql-editor/monaco.tsx(278,7)`: TS2353, `originalAriaLabel` is not in `IDiffEditorOptions`. No Task2 TypeScript diagnostics were reported. Earlier typechecks passed before this separate Monaco edit; this current failure was reported to the coordinator and is not hidden by the earlier PASS.
- Scoped lint exit 0; only the two existing OptimizationSection useEffect dependency warnings remain. The new fast-refresh warning was removed by extracting versionMetrics.ts.
- Scoped git diff --check PASS.
- Final frozen all-task component scope PASS, direct exit 0: 11 files, 116/116 tests, 281.22s (2026-11-11). This includes OperationFailure plus every optimization component and SessionVersionDiff. Earlier overlapping runs that imported code before a later regression/fix are historical RED evidence, superseded by this frozen run.

## Decisions

- Ruling: the active pair supplies selected-case result counts; only the active candidate supplies IO/policy rows. Other version rows retain measurement/results coverage and do not borrow latest original directed policy for a newly selected A. Backend confirmed VersionRow outcomes refer to its original recorded pair, not current A.
- Ruling: historical DTOs without metricCases expose unavailable timing rather than relabel old median-of-medians as a sum.
- Ruling: SQL scripts live in SQL diff; Plan and provenance links remain separately reachable. Existing tests are updated to activate the destination tab rather than require removed drawer actions everywhere.

## Limitations and handoff

Actual backend/browser integration, comprehensive accessibility/zoom/contrast/performance acceptance, full Windows gate and independent task/branch review remain coordinator work. Unit/component tests do not establish the 14 quality grades or native screen-reader acceptance. No live DB, install, publication, merge or push performed. Linux unverified.

Implementation and self-review are ready for fresh task review. Current whole-UI typecheck is blocked by the separate Monaco diagnostic above; the coordinator owns that fix and the final gate. Only explicitly owned Task2 paths are included in the task commit.

## Final frozen focused test output

```text
> ui@0.0.0 test
> vitest run --run src/components/optimization src/pages/OperationPage.test.tsx src/pages/OperationFailure.test.tsx src/components/SessionVersionDiff.test.tsx


 RUN  v5.0.3 C:/Users/rgone/MultiCliProfiles/codex/account-b/worktrees/8723/sqlharness/src/SqlHarness.Dashboard/ui


 Test Files  11 passed (11)
      Tests  116 passed (116)
   Start at  08:44:57
   Duration  281.22s (import 72%, tests 12%, environment 11%, setup 5%, transform 1%)

     Import  281 modules were evaluated 1066 times · 198.36s total, 72% of tracked time
             ~146.07s faster with isolate: false — shared modules are evaluated once per worker instead of once per file
             learn more: https://vitest.dev/guide/improving-performance#test-isolation
```
