# Backend Task 1 evidence

Status: backend implementation committed at `9dba583c`, ready for task review;
no full verification claim.

Ownership: JournalReader, Optimization C# projections, associated Dashboard C# tests.
The comparison implementer agreed additive DTO names before consuming them.

## Observed red checks

- Explicit project command (`dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj`)
  with focused filter observed 13 expected failures: five statuses each for summary
  and comparison eligibility, sum-of-case-medians timing, confirmed statement inside
  partial batch, and structured independent outcomes. Exit 1.
- Reader full-IO/policy-reasons regression then failed on a null physical counter
  despite a stored synthetic value of 3. Exit 1.
- An initial directory-form `dotnet test tests/SqlHarness.Tests --no-restore` returned
  exit 0 without running tests; it is not counted as evidence. Subsequent commands
  name the csproj and show the test count.
- Further observed red regressions covered explicit statement eligibility still
  upgrading the batch verdict, baseline self-comparison metrics, recorded physical
  counters hidden by temporary-row collapse (including zero), absent tables turning
  unsupported historical physical counters into zero, and repeated case measurement
  fingerprints crashing the summary. Each received a focused fix and green coverage.

## Implementation scope

- Shared successful-evidence eligibility; retain interrupted evidence in reader/history.
- Additive full nullable table IO, independent per-statement availability, case timing
  records, outcome counts and case drill-down evidence, including exact stored reasons.
- Strict attribution still owns changed-statements verdicts; an individually confirmed
  subset does not upgrade the batch.
- Default version timing uses sum of case medians; per-case records support UI sum/mean
  and selected-case summaries. Missing contributors remain unavailable.
- Completed legacy cross-group observations with no metrics retain a navigation shell
  and unknown case rows only. They create no measured values or exact policy/verdict.
- Explicitly recorded detailed IO keeps temporary/zero-read rows inspectable. Missing
  tables imply zero only when that metric's recorded support is established.
- Version summaries preserve separate raw case timings when case fingerprints repeat;
  ambiguous identity does not borrow an arbitrary case's recorded policy.

## Scoped verification

- Embedded-UI Dashboard suite: **574 passed, 3 platform skips, 577 total**, exit 0.
  Command: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore
  --filter 'FullyQualifiedName~Dashboard' -v minimal`.
  [Output](backend-dashboard.log). This preceded the final repeated-identity fix.
- Final backend-focused suite after that fix: **186 passed**, exit 0.
  Command: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore
  -p:SkipDashboardUi=true --filter 'FullyQualifiedName~Dashboard.Optimization|FullyQualifiedName~Dashboard.JournalReaderTests|FullyQualifiedName~Agent_optimization' -v minimal`.
  [Output](backend-focused.log). UI asset tests are outside this narrower filter.
- Earlier all-Dashboard run with `SkipDashboardUi=true`: 564 passed, 4 failures,
  3 skips. One asset-embedding failure was expected from that flag; three routing
  fixtures lacked measurements. Those fixtures now explicitly seed synthetic
  measured comparisons and retain their original route/version-label assertions.
- One overlapping focused build encountered Windows testhost output locks while the
  all-Dashboard suite ran. It later proceeded and observed the intended red tests;
  subsequent builds/tests were serialized. No lock retry was treated as a pass.
- `pwsh -NoProfile ./scripts/format-changed.ps1 -Fix`: **10 owned C# files**, exit 0,
  `format: OK`. [Output](backend-format-working.log).
- On committed `9dba583c`, `pwsh -NoProfile ./scripts/format-changed.ps1
  -Mode branch -BaseRef a274b0ec`: **10 C# files**, exit 0, `format: OK`.
  [Output](backend-format-branch.log).
- No collectors, database execution, policy thresholds, schemas or UI files changed
  by this task. Constructors remain compatible; additions are in DTO properties.

Self-review checked status eligibility versus policy failure, frozen-label gaps,
partial attribution versus whole-batch verdicts, unknown versus zero, original A/B
policy direction, case identity collisions and selected-case timing coverage. A fresh
reviewer must still assess the implementation; these checks are not task approval.

Full gate, browser evidence and fresh task review are owned by the coordinator and
remain pending. No database workload, mutation approval, push or publication performed.
