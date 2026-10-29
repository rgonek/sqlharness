### Spec Compliance

- ❌ Issues found: the no-pair action references are built from retained metrics, not the cases in the selected operation, so a case with a retained plan but no metric is omitted (`src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:259`, `:550`); the required no-pair plan snapshots are therefore not reliably usable. `tests/SqlHarness.Tests/Dashboard/DashboardContractTests.cs` is explicitly listed in the brief but has no diff hunk. The specifically requested same-group snapshot guard is also absent from the changed tests (`tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs:773`).
- ⚠️ The later UI integration must verify that plans can be opened when `Cases` is empty and `selectedCase` is `all`: current `src/SqlHarness.Dashboard/ui/src/components/optimization/VersionPlan.tsx:30` resolves a plan only by a selected case. TypeScript typecheck was not run successfully because dependencies are absent; it remains pending for Tasks 4/5. The full Windows gate remains pending Task 5.

### Strengths

- Both routes share subject, database, group and normalized scope filtering, and validate each version against its own group (`src/SqlHarness.Dashboard/DashboardServer.cs:477`). The comparer also rejects incompatible identity (`src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:184`).
- Pairing uses unique nonempty varying fingerprints, propagates the recorded variation flags, and limits aggregation to paired keys (`src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:197`, `:252`). Approximate results use the required copy, null operation verdict, and unavailable case outcomes (`:198`, `:218`).
- Focused tests cover cross-group routes, ambiguous and absent keys, null row counts, four-of-five coverage and aggregate values (`tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs:773`, `:793`, `:809`, `:825`; `tests/SqlHarness.Tests/Dashboard/DashboardServerTests.cs:636`; `tests/SqlHarness.Tests/Dashboard/AgentSessionsTests.cs:447`).

### Issues

#### Critical (Must Fix)

- None found.

#### Important (Should Fix)

- `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:259`: the no-pair `FullAction` calls `Snapshot(compare, variant)`, which derives case IDs only from `compare.Metrics` (`:550`). If a journaled case has a retained SQL/plan variant but no metric entry, the action loses that case even though the operation remains referenced. Preserve case references independently of metrics and add a no-pair test with a retained case and missing metrics, checking both sides.
- `tests/SqlHarness.Tests/Dashboard/DashboardContractTests.cs:1`: this explicitly listed task file has no change. Add the requested contract coverage for the new endpoint payload, including group identifiers and case rows, or get the controller to resolve the brief's file requirement.
- `tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs:773`: no dedicated same-group snapshot guard was added, despite its explicit requirement. Add a comparison asserting the pre-existing same-group semantics, including basis, equivalence and verdict, while permitting the requested additive row evidence.

#### Minor (Nice to Have)

- `tests/SqlHarness.Tests/Dashboard/DashboardServerTests.cs:636`: route tests verify basis and invalid selectors but do not assert that unrelated subject/database/scope evidence with matching hashes cannot contaminate either side. Add an adversarial identity fixture to cover the filtering that the implementation currently performs.

### Assessment

**Task quality:** Needs fixes.

**Reasoning:** Core pairing and API scoping are coherent, but the no-pair snapshot guarantee fails when metrics are absent, and two explicitly requested verification artifacts are missing. No tests were rerun; the implementer reports 106 focused tests passed, while typecheck and the full Windows gate remain pending.

Focused unchanged-code checks: inspected `OptimizationComparer.Snapshot` for the concrete risk of missing plan cases without metrics, and `VersionPlan.ActionPlan` for selection when no pairs exist. The supplied diff output was truncated in transit, so the omitted middle was retrieved separately; no git rederivation was used.
