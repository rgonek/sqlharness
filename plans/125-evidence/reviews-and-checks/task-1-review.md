### Spec Compliance

- ❌ Issues found: cancellation status is excluded from visible diagnostics (`src/SqlHarness.Dashboard/ui/src/pages/OperationPage.tsx:52`); a statement metadata record can make an otherwise unmeasured attempt eligible (`src/SqlHarness.Dashboard/Optimization/OptimizationEvidenceEligibility.cs:11`); the required reader and badge test files have no changes, and tests do not establish that ineligible attempts alone yield no pair or that retained history survives journal projection (`tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs:10`, `src/SqlHarness.Dashboard/ui/src/pages/OperationPage.test.tsx:29`).
- ⚠️ The diff does not establish end-to-end retained interrupted history through JournalReader or successful policy-failed direct comparison; the controller should verify these with focused tests. The reported tests were not rerun.

### Strengths

- `src/SqlHarness.Dashboard/Optimization/OptimizationSummaryBuilder.cs:35` and `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:32` share eligibility and reserve frozen version ordinals, while ignoring policy verdict for eligibility.
- `src/SqlHarness.Dashboard/ui/src/pages/OperationPage.tsx:53` reuses the existing diagnostic contract and renders the stored message as a text node; the test checks markup is not executed (`src/SqlHarness.Dashboard/ui/src/pages/OperationPage.test.tsx:29`).
- `tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs:10` checks that each of five ineligible statuses cannot supersede an existing eligible direct pair.

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- `src/SqlHarness.Dashboard/Optimization/OptimizationEvidenceEligibility.cs:11`: any statement record qualifies as usable measurement, even if it contains only identity/text/plan metadata with no recorded runtime measurement. JournalReader builds statement evidence independently of runtime counters (`src/SqlHarness.Dashboard/JournalReader.cs:607`, `:630`); such historical all-null metric rows can create versions and select a newer direct pair, displacing actual evidence. Check for an actual usable recorded measurement within statements, respecting attribution/provenance as appropriate, and cover all-null/metadata-only historical rows plus a real zero measurement.
- `src/SqlHarness.Dashboard/ui/src/pages/OperationPage.tsx:52`: only `failed` and `rejected` statuses display the prominent reason, while `cancelled` is explicitly an ineligible status in the contract and tests. A cancelled record with a reason or message has no prominent explanation. Include cancelled records and test its rendering, as well as rejection and absence of a message.
- `tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs:10`: the test checks an interrupted operation cannot override an existing pair, but never checks the required case where ineligible attempts are the only source of pair evidence. `JournalReaderTests.cs` and `StatusBadge.test.tsx` also have no corresponding hunks despite being listed in the task. Add focused coverage for absent pair evidence, journal-retained interrupted metrics/history, and the existing badge error-description contract; add a successful policy-failed direct-pair assertion. This tests the requested end-to-end boundaries rather than only synthetic UI projections.

#### Minor (Nice to Have)

- `task-1-report.md` reports nine npm audit findings, including one critical, from setup. Their relationship to this change is not established; controller should retain the evidence and assess it in the appropriate dependency workflow.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** Shared status filtering is a sound direction, but metadata-only statement evidence can still supply optimization versions and pairs, and cancelled records lack the required prominent explanation. Required boundary coverage is incomplete.

**Focused outside-diff checks:** `JournalReader.cs:585-650` and `OptimizationModels.cs:90-127` checked whether statement records necessarily contain usable runtime measurements; `StatusBadge.tsx` and `errors.ts` checked the reused diagnostic contract. The review package output was truncated, so the missing product-diff region was read separately. No tests or git commands were run.
