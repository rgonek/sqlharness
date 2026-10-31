### Finding Verdicts

- **Missing structured outcome counts** — ADDRESSED. The selected-scope DTO counts derive from the same case records (`src/SqlHarness.Dashboard/Optimization/OptimizationModels.cs:151`); the UI consumes them (`ui/src/components/optimization/CaseOutcomeDetails.tsx:9`). Tests assert all-case, selected-case and serialized counts (`tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs:20`).
- **Baseline coverage ignores selected-case scope** — ADDRESSED. Baseline coverage uses the selected case and projected total (`ui/src/components/optimization/VersionTable.tsx:34`), with a focused component assertion (`VersionTable.test.tsx:56`).
- **Results mismatch and unavailable have identical visible labels** — ADDRESSED. Both nonzero categories are displayed (`CaseOutcomeDetails.tsx:17`).
- **Missing regression and stable-order sort assertions** — ADDRESSED. The test asserts failure, two regressions and two same outcomes in deterministic order (`CaseOutcomeDetails.test.tsx:38`).

### New Breakage in the Fix Diff

- None found.

### Out-of-Scope Observations

- None.

### Verdict

**Fix round:** All findings addressed, no new Critical/Important breakage.

The report names the covering tests and records passing counts, plus TypeScript and diff checks. The interrupted UI RED attempt is explicitly disclosed; tests were not rerun in this review.
