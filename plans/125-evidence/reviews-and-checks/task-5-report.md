# Task 5 implementation report

Base: `11381afe97c1dd5de9d675dd137eb9bbcd17d382`. Product commit: `08817d64` (`Consolidate optimization timing and actions`). Scope: one timing summary and consolidated actions. No database, installation, publication, restart, merge, or push.

## Changes and self-review

- Added per-case timing projection with measurement identities, including missing values; retained the legacy median-of-medians DTO field without relabeling or displaying it as a sum. The table uses comparison timing for the active pair, and per-version timings for other rows. No pair uses all cases and sum. Other conditions retain their own all-case sum; no foreign case IDs are applied. Missing timings display coverage.
- Timing aggregation uses selected direct-comparison cases even when logical reads are missing; elapsed and CPU each remain unavailable if a contributor is missing. Cross-condition active pair retains the existing matched-identity aggregation and coverage.
- Scans and separate Results, IO, Policy columns are visible. The shared dialog remains the case drill-down; its existing accessibility and ordering are preserved. The table component supplies horizontal overflow.
- Removed evidence drawer and aggregate timing repetition. Scripts moved into SQL diff; fingerprints resolve independently to the raw side-specific IDs, without silently falling back for an unmatched selected case. Provenance is shown near the comparison heading; Plan remains available. Verdict basis and attribution explanation are local to IO. PostgreSQL buffer totals remain in IO. Exported CPU summary labels its aggregation.
- Self-review checked case IDs, selected vs other-condition rows, absent values, pair/no-pair behavior, provenance and no duplicate basis in row popovers. No SQL execution or policy thresholds changed.

## Verification

All commands executed in this worktree; SDK from `%LOCALAPPDATA%\Microsoft\dotnet`; UI in `src/SqlHarness.Dashboard/ui`.

- RED on base with new test and product restored: `npx vitest run src/components/optimization/VersionTable.test.tsx --maxWorkers=1 --no-isolate`: exit 1, 1 failed / 5 passed. Expected failure: no column header `Elapsed (sum of case medians)`; old header was `Elapsed median`. Earlier startup attempts interrupted; not treated as RED.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter 'FullyQualifiedName~OptimizationSummaryBuilderTests|FullyQualifiedName~OptimizationComparerTests' -p:SkipDashboardUi=true --no-restore`: final PASS 102/102. Initial run 101/102 exposed previous expectation that timing must be unavailable with missing reads. First expectation edit accidentally targeted a different test, producing 100/102; corrected both and reran green.
- `npx vitest run src/components/optimization/ComparisonPanel.test.tsx src/components/optimization/OptimizationSection.test.tsx src/components/optimization/IoComparison.test.tsx src/components/optimization/VersionPlan.test.tsx src/components/optimization/VersionTable.test.tsx --maxWorkers=1`: PASS 60/60. Earlier 49/52 failed stale drawer/timing expectations; after updates 59/60 failed because test queried IO tab before async comparison loaded. Changed to wait for the tab and reran green.
- After markdown label and side/form/case assertions: `npx vitest run src/components/optimization/ComparisonPanel.test.tsx src/lib/optimizationMarkdown.test.ts --maxWorkers=1`: PASS 19/19.
- After row popover cleanup: `npx vitest run src/components/optimization/VersionTable.test.tsx --maxWorkers=1 --no-isolate`: PASS 8/8.
- `npm run typecheck`: PASS after final UI code change. `git diff --check` and `git diff --cached --check`: PASS (Git emitted normal LF-to-CRLF warning on test file).

## Brief checklist and limits

- [x] 12/28 -> 40 sum, 20 mean, single case 12; missing contributor unavailable for elapsed and CPU (focused backend tests). Table and drawer tests check the single summary structure.
- [x] Shared table, matched active pair, selected case, different-condition own metrics, no-pair sum, missing coverage and overflow covered at component/backend level. Existing URL-backed selector test covers query updates. A full browser end-to-end visual verification of these combinations was not run.
- [x] Scans and independent outcome columns, native table horizontal overflow and existing shared case details tested. Actual narrow viewport appearance remains unverified in browser.
- [x] SQL diff side/case and both script form request parameters, operation provenance, removed drawer and local attribution wording tested. Existing Plan component suite passed; manual plan download/navigation and separate-operation provenance visual verification were not performed.
- [x] Focused green, typecheck, scoped diff review and local commit complete. Full `just verify`, integrated tree and browser checks are explicitly assigned to Task 6 and not claimed here.

## Task 5 review fix round 1

Base `08817d64`; fix commit `f424d94e`. Review issues: selected contributor identities for non-pair group-A rows and per-side timing coverage must use the exact aggregate universe, including a completely missing metric record.

The comparison DTO now carries `TimingContributors` generated from the actual aggregate IDs and each side's nullable elapsed/CPU. Direct comparisons use declared cases, independently measured pairs use the snapshot union, and cross-condition pairs use matched varying identities only. Version timing rows carry their varying identity so same-group unpaired rows can map those matched identities correctly; ambiguous/missing matches stay unavailable. Rows in other groups continue to use their own all-case sum. Active-row coverage counts each metric on each side independently from the new projection. No-pair behavior remains based on the version's declared case timings, including null for an absent whole metric record. No changes to SQL execution, outcome policy or unrelated workflows.

RED before product changes:
- `npx vitest run src/components/optimization/VersionTable.test.tsx --maxWorkers=1 --no-isolate`: exit 1, 1 failed / 8 passed; `uses full timing contributors for other rows and per-side metric coverage` could not find `40 ms` because only the paired ID was summed.
- `$env:PATH="$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"; dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter 'FullyQualifiedName~Timing_contributors_include_missing_metric_records' -p:SkipDashboardUi=true --no-restore`: exit 1, 1 failed; `GetProperty("TimingContributors")` raised `KeyNotFoundException`.

GREEN after changes:
- `$env:PATH="$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"; dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter 'FullyQualifiedName~OptimizationComparerTests|FullyQualifiedName~OptimizationSummaryBuilderTests' -p:SkipDashboardUi=true --no-restore`: PASS 104/104, including direct missing whole record, independent union/single selection, matched cross-condition IDs, and version projection of varying ID plus missing whole record.
- `npx vitest run src/components/optimization/VersionTable.test.tsx --maxWorkers=1 --no-isolate`: PASS 11/11. Covers per-side and per-metric `1/2` and `0/2` coverage; full selected universe for other group-A rows; missing contributor renders unavailable, not partial sum; varying-key matching; foreign-group own sum; no-pair `1/2` coverage.
- `npm run typecheck`: PASS. Initial attempt failed TS6133 on an unused test lambda parameter; corrected it and reran. A subsequent attempted correction from the UI directory used an incorrect repository-relative path and produced FileNotFoundError, then was corrected; this did not change product code.
- `git diff --check` and `git diff --cached --check`: PASS. Git warned the VersionTable test's LF will be replaced by CRLF when touched; this is a warning, not a failed check.

Self-review: verified projection IDs are the exact arrays passed to timing aggregation for all three bases and selection; coverage uses separate elapsed/CPU values per side. Missing group-A timing identities produce unavailable values with full denominator; duplicate varying IDs do not match silently. Backend builder and UI tests jointly exercise a missing whole record with no pair. Full gate and browser verification remain Task 6; no DB, install, publish, restart, merge or push.
