# Task 1 backend review

Reviewed 2026-11-10/11. Product: `9dba583c` against its parent; evidence update:
`45a7e54c`. Scope: Plan 125 backend Tasks 1-5, Plan 126 Task 1 and the approved
`docs/superpowers/specs/2026-11-10-dashboard-quality-design.md`. Uncommitted sibling
UI work was excluded. No product changes, builds, database workloads or full gates
were performed by this reviewer.

## Spec: changes required

### P1: Colliding measurement fingerprints still select arbitrary comparison evidence

Location: `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:625-635`
(`Snapshot`, particularly `GroupBy(...).ToDictionary(... group.Last())`), consumed by
case options, aggregate metrics and action references at lines 75-85 and 168-178.
For independent measures this also reaches
`src/SqlHarness.Dashboard/Optimization/OptimizationOutcomes.cs:13-26`, where the
ambiguity check only consults a direct comparison and therefore cannot detect the
collision.

Two distinct cases with the same `MeasurementFingerprint` are reduced to the last
metric. The summary's new `MetricCases` correctly retains both cases, but selecting
that version pair silently loses the first case and its script binding. The direct
pair's new structured outcomes refuse a policy/IO verdict, yet its case option and
whole-batch numbers still report the arbitrary last case. An independently measured
pair goes further: its new structured IO count reports an improvement as well.
This violates Plan 125's complete contributing-case coverage and honest independent
outcomes, and the approved design's prohibition on fabricated verdicts. Repeated
identities are an explicitly supported regression state in the added summary test;
the comparison path is missing the corresponding protection.

Reproduction (both runs executed successfully against the existing Release DLLs
using PowerShell/.NET reflection and JSON deserialization, without rebuilding):

1. Construct successful evidence for subject `orders`, scope `whole`, database `db`,
   SQL Server, conditions fingerprint `f`, hashes `h0` and `h1`.
2. Add cases `0`/`first` and `1`/`second`, both with measurement fingerprint `repeat`.
3. Baseline case metrics: reads `[100, 500]`, elapsed `[12, 28]`. Candidate metrics:
   reads `[900, 50]`, elapsed `[10, 20]`. Table rows may be empty. Set direct case
   equivalence to true, with original policy verdicts pass/fail respectively.
4. Build `OptimizationSummaryBuilder.Build`, then call
   `OptimizationComparer.Compare(group, evidence, "v0", "v1", "all", "sum", "whole")`.
5. Repeat as two successful `IsMeasure = true` operations, one per hash, each with
   the same two case identities and its own `measure` variant metrics.

Observed direct-pair result:

```text
Version timing summary:      40 -> 30
Comparison timing:           28 -> 20
WholeBatch:                  500 -> 50
Cases:                       repeat / second / better
CoverageLabel:               empty
Structured IO/policy:        unavailable (one collapsed case)
BeforeAction.CaseIds:        ["1"]
```

Observed independent-pair result:

```text
WholeBatch:                  500 -> 50
Outcomes.IoBetter:           1
Outcomes.IoUnavailable:      0
CaseOutcomes[0].IoOutcome:   better
BeforeAction.CaseIds:        ["1"]
```

The actual complete reads are `600 -> 950`, a regression, rather than the reported
`500 -> 50` improvement. The all-case script reference drops case `0` entirely.

Required regression coverage: exercise both direct and independent comparisons
with this collision, plus the selected duplicate key. Either preserve distinct
case occurrences with proven pairing, or mark ambiguous comparison metrics/outcomes
unavailable with explicit coverage. Never silently select the last metric. Keep all
original case IDs available for the all-case SQL/script action; a single-case action
must not resolve an ambiguous fingerprint to an arbitrary case. Verify consistency
between version-table metrics, comparison metrics, case options and outcome counts.

## Standards

No separate material standards or security finding. Credential, mutation-policy,
execution and persistence code are outside this product diff. The shared status
predicate correctly distinguishes operation success from recorded policy failure;
the focused fixtures cover frozen label gaps. The partial-statement path retains
the stricter whole-batch verdict fallback, nullable table fields remain nullable,
and recorded policy reasons are read rather than reconstructed from IO changes.

## Evidence and limits

- Reviewed the full owned product diff, reader projection, eligibility predicate,
  comparer, summary builder, outcome projection and associated test changes.
- Directly reproduced the finding above against existing Release assemblies; these
  were focused synthetic projection calls, not a test-suite/full-gate pass.
- Implementer evidence reports **186 focused tests passed** after the final summary
  collision fix. The earlier **574 Dashboard tests passed / 3 skipped** predates that
  final fix and is not final-tree all-Dashboard evidence. This reviewer did not rerun
  either suite or treat earlier evidence as current full verification.
- Plan 125 frontend/integrated acceptance, Plan 126 category grading and final
  Windows verification remain separate work. Linux/live acceptance was not run.

Summary: **Spec: 1 material finding (P1); Standards: 0 material findings. Backend
task approval withheld pending the collision fix and focused regression evidence.**
