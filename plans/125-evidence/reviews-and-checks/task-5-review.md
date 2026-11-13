### Spec Compliance

- ❌ Issues found: the version table can omit a selected contributor for unpaired rows and reports misleading timing coverage when a case has no metric on one side (`VersionTable.tsx:40`, `VersionTable.tsx:48`). This conflicts with design contract 4 (`plans/125-optimization-evidence-clarity.md:80-90`).
- ⚠️ Plan downloads, actual narrow-screen usability, and the full integrated/browser gate cannot be established from the task diff or reported focused tests; Task 6 owns these checks. Existing Plan code was inspected only for the named risk that script case resolution might diverge from plan case resolution: `VersionPlan.tsx:26-34` uses the same explicit-case/no-fallback behavior.

### Strengths

- Direct timing aggregation now considers cases even if logical reads are absent, and keeps a missing timing unavailable (`OptimizationComparer.cs:114-118`; `OptimizationComparerTests.cs:1153-1184`).
- SQL diff actions bind mapped raw case IDs independently per side, and provenance stays visible near the comparison (`ComparisonPanel.tsx:25-40,95,114`). The new action test checks both script forms and both sides (`ComparisonPanel.test.tsx:127-146`).
- Foreign-condition unpaired rows use their own case timings rather than applying the active pair's case ID, and are visibly labeled (`VersionTable.tsx:38-44,62`; `VersionTable.test.tsx:70-80`).

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

1. `src/SqlHarness.Dashboard/ui/src/components/optimization/VersionTable.tsx:40-44`: For an unpaired row in group A, “all cases” uses `comparison.cases`, which the comparer constructs from `pairedIds` (`OptimizationComparer.cs:76-78,88`). If a direct comparison declares two cases but one side has no metric for the second, the active pair correctly aggregates over both declared cases (`OptimizationComparer.cs:114-118`), while another group-A row silently sums just the first case even if it has timings for both. The column still says “sum of case medians”, so it presents a partial sum as the selected all-case result. Provide the full selected contributor identity set for the table, including missing-side cases, and cover this asymmetric case in a component test.
2. `src/SqlHarness.Dashboard/ui/src/components/optimization/VersionTable.tsx:46-50,62`: Active-row coverage uses `caseOutcomes`, which excludes cases without metrics on both sides, while timing aggregation uses the direct operation's declared cases or the union of independent snapshots (`OptimizationComparer.cs:76-78,114-118`). For two declared cases with one missing on the other side, a timing may show unavailable with “1/1 cases” rather than the actual “1/2 cases”; the other side can also be misreported. Derive each side's available/total coverage from the same contributor identities and metric availability as its aggregate, including cases missing entirely from a snapshot. Test missing whole metric records, not only null timing fields on existing records.

#### Minor (Nice to Have)

- `task-5-report.md:19`: The reported diff checks emitted an LF-to-CRLF warning. This is test-output noise, not evidence of a failed check; the reviewer contract asks for reported warnings to be recorded.

### Assessment

**Task quality: Needs fixes.** The aggregation and action consolidation are largely aligned with the contract, but the case universe used by table rows and coverage is inconsistent with the aggregate and can mislead users about incomplete evidence. Reported suites were not rerun; browser and full-gate verification remain with Task 6.
