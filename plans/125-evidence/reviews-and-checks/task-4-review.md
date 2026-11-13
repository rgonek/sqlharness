### Spec Compliance

- ❌ Issues found: the required structured outcome counts are absent from the dashboard DTO; only per-case records are exposed (`src/SqlHarness.Dashboard/Optimization/OptimizationModels.cs:143`, `:215`; `ui/src/api/types.ts:200`). The UI derives counts locally (`ui/src/components/optimization/CaseOutcomeDetails.tsx:10`), so this is an interface-contract omission even though its badges and table use the same records.
- ⚠️ The brief lists `OptimizationSummaryBuilder.cs`, `VersionTable.test.tsx`, and `ComparisonPanel.test.tsx`, but the task range changes none of them. The diff cannot demonstrate new integration coverage for the caller and selected-case baseline behavior; the implementer's report says the existing suites were run.

### Strengths

- Per-case recorded results, IO and policy are independent, with preserved recorded reasons and the exact direct operation's snapshots (`OptimizationComparer.cs:43`, `:54`, `:695`; `JournalReader.cs:2057`). The four-case test explicitly checks three IO improvements alongside one elapsed-regressed policy failure (`tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs:10`).
- Reversed, independent and approximate pairs suppress directional recorded policy while retaining justified IO evidence (`OptimizationComparer.cs:146`, `:234`, `:709`; `OptimizationComparerTests.cs:27`, `:590`, `:1006`). The shared dialog preserves reads, elapsed, CPU and reasons and sorts failures first (`ui/src/components/optimization/CaseOutcomeDetails.tsx:15`, `:23`).
- Tests exercise both triggers, keyboard close and focus return, zero baseline and long-label wrapping classes (`CaseOutcomeDetails.test.tsx:6`, `:20`); selected paired cases with missing reads remain available (`OptimizationComparer.cs:76`, `:91`).

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- `OptimizationModels.cs:143`, `:215`; `ui/src/api/types.ts:200`: the explicit task interface requires structured outcome counts plus case evidence, but the response contains only case evidence. Computing counts only in React leaves other DTO consumers without the required interface and duplicates the counting contract if another consumer needs it. Project structured counts for the selected scope from the same case records, preserve existing fields and test consistency with the detail cases.
- `ui/src/components/optimization/VersionTable.tsx:35`: baseline coverage always counts every outcome, while the adjacent candidate counts and table filter to `comparison.selectedCase` (`CaseOutcomeDetails.tsx:10`). Selecting one case can therefore display, for example, baseline `4/4 measured` alongside a one-case candidate, misrepresenting the scope of the active comparison. Apply the same selected-case scope to baseline coverage and add a VersionTable integration assertion.

#### Minor (Nice to Have)

- `ui/src/components/optimization/CaseOutcomeDetails.tsx:18`: `Results 0/1` has the same visible text for a mismatch and an unavailable result; the distinction exists only in the title. Show nonzero differing and unavailable categories visibly so the independent result dimension is interpretable without hovering.
- `CaseOutcomeDetails.test.tsx:6`: the failure-first test covers failure versus a same-IO pass but does not establish that IO regressions sort ahead of non-regressions, or stable order within equal categories. Add explicit assertions for those cases as required by the brief.

### Assessment

**Task quality:** Needs fixes.

**Reasoning:** Directional policy and per-case evidence are separated correctly, but the explicitly required structured-count interface is missing and baseline coverage disagrees with a selected-case comparison.

**Focused outside-diff check:** `OptimizationComparer.cs:43-62` confirms the direct-pair snapshots come from the direct operation; this addressed the risk of combining policy with another operation's metrics. No tests were rerun. Review artifact only written; no product files, index, HEAD or branch were modified.
