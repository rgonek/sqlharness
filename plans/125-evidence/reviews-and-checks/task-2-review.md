### Spec Compliance

- ❌ Issues found: absence is treated as proved despite duplicate statement identities (`src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:531`); historical execution counts are newly gated by unrelated evidence-version eligibility (`src/SqlHarness.Dashboard/JournalReader.cs:771`, `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:413`); execution availability can contradict the displayed value (`src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:413`). These violate independent, honest metric availability and the binding ruling.
- ⚠️ Browser/live database and whole-plan integration verification are outside this task; the implementer reports focused tests and typecheck passing, not those broader checks. The report records LF/CRLF warnings from `git diff --check`.

### Strengths

- `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:296` retains whole-batch fallback for incomplete attribution and rejects duplicate occurrence keys; the partial-batch regression asserts the fallback alongside retained confirmed counters (`tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs:209`).
- `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:375` checks object kind and duplicate identity before projecting object counters; `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:480` carries case coverage and distinguishes confirmed nonexecution provenance.
- `src/SqlHarness.Dashboard/ui/src/components/optimization/IoComparison.tsx:44` retains statement text and plan links with a Whole batch action when IO is unavailable; focused tests cover the empty and mixed states (`src/SqlHarness.Dashboard/ui/src/components/optimization/IoComparison.test.tsx:145`).
- The ruling to retain historically confirmed reads/scans while gating runtime/grants/LOB is consistent with the design's instruction to carry existing eligibility and independently assess each metric (`plans/125-optimization-evidence-clarity.md:47`). The reader continues gating CPU/grants and LOB independently of historical IO (`src/SqlHarness.Dashboard/JournalReader.cs:613`, `src/SqlHarness.Dashboard/JournalReader.cs:679`).

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:531`: `ProvesAbsence` checks only the stored batch attribution and omissions. `JournalReader.cs:635` sets attribution to confirmed when every row has confirmed status, even if `(Key, Occurrence)` occurs twice; `CompareCase` correctly rejects that collision, but a different statement absent on that side is nevertheless projected as zero with confirmed-nonexecution provenance (`OptimizationComparer.cs:537`, `OptimizationComparer.cs:493`). A collision makes complete identity coverage untrustworthy, so require unique occurrence identities as well as complete attribution before inferring absence. Add a regression with a duplicated key plus a different statement present on the opposite side.
- `src/SqlHarness.Dashboard/JournalReader.cs:771` and `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:413`: executions previously came from the recorded `s.executions` independently of dashboard evidence version; both projections now suppress them when `EvidenceEligible` is false, and the comparison also suppresses them unless both sides have equal counts. Historical recorded execution counts and asymmetric counts therefore disappear, contrary to independent per-side availability and the ruling that existing version eligibility gates runtime/grants/LOB, not all metrics. Preserve eligible historical execution counts under their existing identity/confirmation rules and expose separate side values if needed, retaining the existing scalar compatibly.
- `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:413` and `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:421`: when both execution counts exist but differ, or only one side has a count, `Executions` is null while `RuntimeAvailabilityPair` declares the recorded side(s) available. No numeric execution value is present for those sides in the DTO (`src/SqlHarness.Dashboard/Optimization/OptimizationModels.cs:171`), so clients cannot present the advertised values. Add per-side values and matching UI, or accurately mark a nonrepresentable scalar unavailable; test asymmetric and unequal counts.

#### Minor (Nice to Have)

- `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:361`: the `only-candidate`/`only-baseline` branches precede `unprovedAbsence`, so a statement entirely missing on one side with partial/omitted evidence is labeled “only in” the other side instead of unmatched. The values remain null, but the status overstates what is known. Prefer unmatched whenever the absence is unproved.
- `src/SqlHarness.Dashboard/ui/src/components/optimization/StatementRows.tsx:85`: the no-IO early return skips `HiddenWorkFlag`, although the ordinary row renders it at line 100. Preserve hidden-work notices in the empty state.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** Confirmed subsets and verdict fallback are largely well separated, but an identity collision can manufacture a zero and execution availability either suppresses historical evidence or advertises values absent from the DTO. The focused test evidence does not cover these cases; no tests were rerun during review.

**Focused checks outside diff:** For the named risk of false nonexecution, checked the reader's attribution construction at `JournalReader.cs:635`; for the named risk of historic eligibility drift, checked `LoadStoredStatements` at `JournalReader.cs:679` and its SQL projection. Read plan section 2 to evaluate the controller ruling. The review package output was truncated, so its omitted portion was read separately.
