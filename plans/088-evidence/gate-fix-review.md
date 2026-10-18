### Spec Compliance

- ✅ Spec compliant: both former whole-artifact expectations now include the exact SQL supplied to `Run`, while retaining the non-null and null `ParameterSet` assertions (`tests/SqlHarness.Tests/BenchmarkRunTests.cs:34-65`, `:158-181`). The changed tests verify that the SQL value and `ExecutedSql` property name stay out of Web and default JSON; the prior serialized-artifact and writer equality checks remain (`:173-194`). The review package contains only this test file, with no production change.
- ⚠️ The full `just verify` result was outside this fix's original review package. Task 7 subsequently passed the full Windows branch gate; see [verification evidence](../088-verification-evidence.md).

### Strengths

- The in-memory equality assertions preserve strong artifact coverage and now match the runner's assignment of `ParameterSet` and exact input SQL (`tests/SqlHarness.Tests/BenchmarkRunTests.cs:64-65`, `:169-170`; `src/SqlHarness.Core/BenchmarkRunner.cs:89`).
- Both serializer configurations retain baseline JSON equality and explicit omission checks for SQL and null `ParameterSet` (`tests/SqlHarness.Tests/BenchmarkRunTests.cs:171-181`).

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- None.

#### Minor (Nice to Have)

- None.

### Assessment

**Task quality:** Approved

**Reasoning:** The fix updates stale test expectations without weakening the surrounding assertions or changing product behavior. The reported 65 focused tests are meaningful for this scope; the full gate remains a separate Task 7 requirement.
