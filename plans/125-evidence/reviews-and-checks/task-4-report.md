# Task 4 report

Base: 50b966739cf98ead77e8f1d0c393cd2e586c33b4. Product commit: 02192bac. No live DB, deployment, installation, merge or push.

## RED

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter FullyQualifiedName~Independent_case_outcomes --no-restore` (with local .NET SDK on PATH): failed compilation CS0117 missing `RegressionReasons` and CS1061 missing `CaseOutcomes`. This was run before backend edits.
- From `src/SqlHarness.Dashboard/ui`: `npx vitest run src/components/optimization/CaseOutcomeDetails.test.tsx --maxWorkers=1 --no-isolate`: failed import resolution for `./CaseOutcomeDetails` before UI component implementation.
- Existing `ComparisonPanel.test.tsx`: 12 passed, 1 failed finding `per-table IO not available for this engine`. Task 2's prior IoComparison branch selected the per-statement empty state first and hid the per-table engine explanation. Kept both explanations; no assertion weakened.
- An attempted combined UI run including IoComparison under `--no-isolate` caused its React Query mock to contaminate ComparisonPanel. Separate groups below pass. An intermediate long-label assertion used unnormalized whitespace; corrected to assert the row header classes.

## GREEN

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter "FullyQualifiedName~OptimizationComparerTests|FullyQualifiedName~OptimizationSummaryBuilderTests|FullyQualifiedName~JournalReaderTests" --no-restore`: Passed 191, failed 0, skipped 0.
- UI: `npx vitest run src/components/optimization/CaseOutcomeDetails.test.tsx src/components/optimization/VersionTable.test.tsx src/components/optimization/ComparisonPanel.test.tsx --maxWorkers=1 --no-isolate`: 3 files passed, 19 tests passed.
- UI: `npx vitest run src/components/optimization/IoComparison.test.tsx --maxWorkers=1 --no-isolate`: 1 file passed, 17 tests passed.
- UI `npx tsc --noEmit`: exit 0. `git diff --check`: exit 0.
- Full `just verify` intentionally reserved for Task 6; no integrated-tree claim.

## Coverage and self-review

- Four synthetic cases show independent IO 3 better / 1 same and policy 3 pass / 1 fail, preserving 10 -> 30 elapsed and exact elapsed-regressed reason. Reader verifies stored reason projection.
- Missing policy, mismatched results, unsupported engine IO, all missing reads, zero baseline, direct reverse, independent and approximate pairing covered. Recorded policy is directional and never reused for non-forward pair. Other recorded result evidence remains symmetric for reversed comparisons.
- Shared case records feed counts and dialog; failures sort first then IO regressions with stable order. Two triggers open the same dialog; keyboard open/Escape/focus return, click and focus return, and wrapping class on long label covered. Absolute before/after and percent delta remain visible for reads, elapsed, CPU. Zero baseline percentage is unavailable.
- Old DTO fields and routes preserved. Added caller wiring in OptimizationSection. Task 2 engine empty-state conflict fixed in IoComparison; its test suite passed separately.
- Limits: initially counts were calculated only in the UI; this was corrected in review round 1 below; policy categories use only recorded pass/fail, all other policy statuses display unavailable. No browser layout test or full gate run. Paired cases with unavailable IO remain selectable; cases without a matching metric row on both sides remain available in the all-case drilldown. The structured count interface was corrected in review round 1 below.

## Changed files

JournalReader.cs; Optimization/OptimizationModels.cs; Optimization/OptimizationComparer.cs; ui/src/api/types.ts; optimization/OptimizationSection.tsx, VersionTable.tsx, IoComparison.tsx, CaseOutcomeDetails.tsx and CaseOutcomeDetails.test.tsx; Dashboard/JournalReaderTests.cs and OptimizationComparerTests.cs.

## Selector follow-up

- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter FullyQualifiedName~Outcomes_keep_missing_io --no-restore` failed: `Assert.Contains` did not find case `1` in comparison options.
- Implemented selectable paired cases with unavailable reads, without changing all-case metric aggregation. Updated the previous assertion to expect an `unavailable` outcome rather than omission.
- GREEN: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter "FullyQualifiedName~OptimizationComparerTests|FullyQualifiedName~OptimizationSummaryBuilderTests|FullyQualifiedName~JournalReaderTests" --no-restore`: 191 passed, 0 failed, 0 skipped. `git diff --check` passed.
- Follow-up commit: `a86de142`.

## Review round 1 — commit 11381afe

- Backend RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter FullyQualifiedName~Independent_case_outcomes --no-restore` failed with CS0246 for OutcomeCounts, ResultCounts, IoCounts and PolicyCounts, and CS1061 for missing OptimizationComparison.OutcomeCounts.
- UI RED attempt: `npx vitest run src/components/optimization/VersionTable.test.tsx --maxWorkers=1 --no-isolate` remained at RUN without results and was interrupted; thus no UI RED assertion result is claimed. Two overlapping test runs were stopped; subsequent tests run sequentially.
- GREEN: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter "FullyQualifiedName~OptimizationComparerTests|FullyQualifiedName~OptimizationSummaryBuilderTests|FullyQualifiedName~JournalReaderTests" --no-restore` — Passed 191, Failed 0, Skipped 0, duration 27 s.
- UI GREEN, each command from `src/SqlHarness.Dashboard/ui`: `npx vitest run src/components/optimization/CaseOutcomeDetails.test.tsx --maxWorkers=1 --no-isolate --reporter=verbose --testTimeout=10000` — 3/3; `npx vitest run src/components/optimization/VersionTable.test.tsx --maxWorkers=1 --no-isolate --reporter=verbose --testTimeout=10000` — 5/5; `npx vitest run src/components/optimization/ComparisonPanel.test.tsx --maxWorkers=1 --no-isolate --reporter=verbose` — 13/13. `npx tsc --noEmit` and `git diff --check` exited 0.
- Review corrections: selected-scope OutcomeCounts DTO is derived from the same CaseOutcomes records; tests cover all/single case, absent/mismatched outcomes and JSON serialization. Candidate badges consume DTO counts; baseline coverage applies identical selectedCase filtering. Results visibly distinguish differing and unavailable. Additional test verifies failure-first, then IO regression, and stable order.
- Self-review: counts are computed as a read-only property from selectedCase and case records, so cannot drift after record construction. TypeScript field remains optional for compatibility with older payloads; new server always emits it. UI shows no candidate counts until the new DTO field exists. Existing fields and direction handling unchanged. No live DB, broad gate, browser verification, install, push, merge or restart.
