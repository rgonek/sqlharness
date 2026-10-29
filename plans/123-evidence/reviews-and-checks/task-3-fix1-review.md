### Fix-round verdict: Approved

- **ADDRESSED — missing-metric plan case references:** `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:259` now constructs the no-pair action from journaled cases, independently of metrics; `:265` retains both case IDs and their mappings. `tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs:809` exercises missing metrics on both sides and checks IDs, mappings and operation references.
- **ADDRESSED — payload contract test:** `tests/SqlHarness.Tests/Dashboard/DashboardContractTests.cs:22` asserts the serialized approximate basis, group identifiers and labels, case labels, nullable row count and explicit variation flags.
- **ADDRESSED — dedicated same-group guard:** `tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs:838` checks the existing measured-together basis, operation, equivalence, outcome, reads and policy verdict alongside additive case rows.
- **New breakage in fix diff:** None found.
- **Outside scope, nonblocking for this review:** Task 4 must make no-pair plan references accessible in UI when `Cases` is empty and `selectedCase` is `all`. TypeScript typecheck and full Windows verification remain pending as previously reported.
- **Verification:** Did not rerun reported tests. The implementer reports the focused comparer and contract test command passed 57 tests with no failures or skips; no full gate was run.
