### Spec Compliance

- ❌ Issues found: the required end-to-end assertion that repetitions are not multiplied is missing. The reader fixture declares three repetitions (`tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs:991`) and asserts a single projected row (`:279`, `:306`), while the comparison test constructs unrelated in-memory case representatives (`tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs:1064`) and never passes that stored evidence through the comparer. Thus the requested reader-to-comparison sum/mean proof with repetitions is absent.
- ⚠️ The existing representative persistence and PostgreSQL reader behavior are outside the diff; the focused checks below support their use but do not establish a full journal-to-UI integration run.

### Strengths

- `src/SqlHarness.Dashboard/JournalReader.cs:555` and `:600` project the stored nullable fields using matching ordinals; `Optimization/OptimizationComparer.cs:716` carries each through both sides and the existing case aggregation.
- `tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs:1059` checks sum and rounded mean, data plus LOB, unknown and recorded zero; `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:128` retains batch totals when shared reads are unavailable.
- `src/SqlHarness.Dashboard/ui/src/components/optimization/IoComparison.tsx:27` respects explicitly null DTO fields, makes options conditional on available values and excludes whole-batch physical counters from statement view. The PostgreSQL empty state and controls have an explicit UI test (`IoComparison.test.tsx:180`).

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs:279` and `tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs:1064`: the separate fixtures do not prove that stored three-repetition evidence survives reader and comparer exactly once in sum and mean. Add a synthetic journal fixture with multiple cases and distinct values for every stored field, zero and null contributors, then pass reader output to the comparer and assert both aggregation modes and no multiplication. This is explicitly required by Task 3 and is central to its evidence contract.

#### Minor (Nice to Have)

- None.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The implementation appears consistent with the projection and nullability requirements, but the explicitly required end-to-end representative/repetition regression is not demonstrated by the supplied tests. The implementer's reported focused runs and typecheck were not rerun; full verification remains deferred to integration.

**Focused outside-diff checks:** `OptimizationComparer.cs:786-809` verifies aggregation uses each selected case once and preserves null contributors; `JournalReaderTests.cs:991-994` verifies the fixture declares three repetitions. `plans/125-optimization-evidence-clarity.md:67-76,162-173` supplies the binding design and test requirement. No test was rerun and no product files, index or HEAD were changed.
