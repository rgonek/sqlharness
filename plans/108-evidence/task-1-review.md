### Spec Compliance

- ✅ Spec compliant — the diff adds the measure and measure-set empty-variant cases and compares their results with explicit `query` across artifact ID, selected statement, repetition, parameter set, available repetitions, and plan contents (tests/SqlHarness.Tests/ArtifactReaderTests.cs:57, tests/SqlHarness.Tests/ArtifactReaderTests.cs:94). It also covers invalid nonempty values, omitted/empty compare variants, and non-plan selectors (tests/SqlHarness.Tests/ArtifactReaderTests.cs:118, tests/SqlHarness.Tests/ArtifactReaderTests.cs:137, tests/SqlHarness.Tests/ArtifactReaderTests.cs:155). The reader normalization is kind-specific and keeps the compare values explicit (src/SqlHarness.Core/ArtifactReader.cs:456).
- ⚠️ Cannot independently observe execution of the focused RED/GREEN commands from the diff. The task report provides command output showing RED at 2 failures and GREEN at 74/74; the focused suite was not rerun per reviewer instructions (task-1-report.md:18, task-1-report.md:28).

### Strengths

- The selection theory covers both measure and measure-set, including a parameter-set selection, then compares the empty mapped variant with explicit `query` while selecting statement 1 and repetition 1 (tests/SqlHarness.Tests/ArtifactReaderTests.cs:58, tests/SqlHarness.Tests/ArtifactReaderTests.cs:94).
- The reader normalizes only null/empty variants for measure kinds; nonempty values proceed to existing validation, and compare omission remains invalid (src/SqlHarness.Core/ArtifactReader.cs:456).
- Outside-diff check, needed because the test hunk omitted its header: inspected only the test theory header and confirmed its two cases are measure and measure-set (tests/SqlHarness.Tests/ArtifactReaderTests.cs:57).

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

None.

#### Minor (Nice to Have)

None.

### Assessment

**Task quality:** Approved

**Reasoning:** The narrow reader change implements the required empty mapped-variant default, and the added tests exercise its observable selection semantics and rejection boundaries. The reported focused evidence is clear; no broader gate is required for this task review.

