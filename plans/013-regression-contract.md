# Regression contract — field map, decision order and invariants

> **2026-10-11:** the decision thresholds here are reused by [051](051-compare-regression-verdict.md). Everything about reading saved artifacts offline (run-count inference, legacy producer proof, `regress` command) is superseded.

Status: PLANNED; contract revised 2026-10-11 on HEAD 729d88f. The function is not implemented.

Historical mapping HEAD (state before implementation, not the current HEAD): `39451fcabc7c3eac184e0370bdef7196c7e1e683`. This is not the plan's audit base (`8aa01f8`).

The decision order is in the "Decision order" section. It replaces first-match from section 4 of the specification. The field maps below do not change.

The "Invariants" section preserves the offline and evidence boundaries. The decision order includes the correction of the unreleased time and cost policy.

This file preserves the historical map of the seven facts from the SHA given above, records the rule order and enumerates the invariants that this order does not change. It is not an implementation plan. It adds no commands, stubs or capabilities. Section 2 of `docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md` only names the inputs the policy wants to use. The rule order from section 4 of that specification is not carried over here: it is replaced.

Names from the plan: CompareReport to `SqlHarnessCompareReport`, MetricReport to `BenchmarkMetricReport`. Type and member names are in source spelling.

## Offline read

`ArtifactReader.ReadSection` (`src/SqlHarness.Core/ArtifactReader.cs`) reads only `manifest.json` and `report.json`. It does not open `runs.jsonl`, plans or SQL. The v1 manifest kinds are `compare`, `measure` and `measure-set`. `SqlHarnessCompareMatrixReport` is not an artifact kind; a matrix cell carries a separate `SqlHarnessCompareReport` written by `CompareCellRunner`.

The `summary` section returns the result of `BenchmarkSummaryProjector.Project` (`src/SqlHarness.Core/BenchmarkSummary.cs`): for compare `CompareBenchmarkSummary`, for measure `MeasureBenchmarkSummary`, for measure-set `MeasureSetBenchmarkSummary`. The `metrics` section returns `ArtifactMetricsSection`. The `operators` section carries none of the seven facts.

`CompareArtifactWriter` (`src/SqlHarness.Core/Artifacts.cs`) serializes the whole report into `report.json` with the `ArtifactDirectoryPublisher.JsonOptions` options (`JsonSerializerDefaults.Web`: camelCase). An enum without a converter is written as a number. `runs.jsonl` carries `RunMetadata`: variant, repetition number, three `long` numbers and `ResultHash`. It carries neither `BenchmarkRunMetrics` nor `BenchmarkMetricReport`.

`present` means that the saved artifact has that member. `unknown` means that it does not. Zero, an empty list, a null neighbouring member and specification text are not that member. I did not read artifacts from `~/.sqlharness` nor other user files. The save path below is a code path, not a disk inspection.

## Map

| Fact | Member | `report.json` | summary | metrics | Status |
|---|---|---|---|---|---|
| Run count | `SqlHarnessCompareReport.MeasuredRunCount` | yes | no | no | `present` in the report file; the selective read does not expose it |
| Spread | none | no | no | no | `unknown` |
| Time availability | `BenchmarkMetricReport.ElapsedTimeAvailability` | when `MetricReport` is not null | the same copy | the same copy | `present` or `unknown`, as below |
| CPU availability | `BenchmarkMetricReport.CpuTimeAvailability` | when `MetricReport` is not null | the same copy | the same copy | `present` or `unknown`, as below |
| Logical reads availability | `BenchmarkMetricReport.LogicalReadsAvailability` | when `MetricReport` is not null | the same copy | the same copy | `present` or `unknown`, as below |
| Compare pair equivalence | `SqlHarnessCompareReport.Equivalence` (`ResultEquivalenceReport`) | yes | yes | yes, compare only | `present` |
| Compare pair stability | none | no | no | no | `unknown` |

### Run count

The carrier of a compare pair is `SqlHarnessCompareReport.MeasuredRunCount` (`src/SqlHarness.Core/Artifacts.cs`). The current `CompareCellRunner.RunAsync` (`src/SqlHarness.Core/CompareCellRunner.cs`) inserts `runs.Count` there. The `runs` list collects baseline and candidate for repetitions from 1 to `request.Repeat`. The call with number 0 is a warm-up and does not enter this list. `Repetitions` gets `request.Repeat`. The type does not link these two numbers. `MeasuredRunCount` is not the run count of a single variant: on this path the list contains both variants.

`CompareBenchmarkSummary` copies `Repetitions` and has no `MeasuredRunCount`. `ArtifactMetricsSection` and `ArtifactNamedMetrics` have neither of these numbers. `CompareDistribution` (`Min`, `Median`, `Max`) has no sample counter either. The samples from which a variant's runs could be counted lie in `runs.jsonl`, and `ArtifactReader` does not read that file.

There is no separate "variant run count" member. The status of that number is: `unknown`. `Repetitions` without a recognized producer version and invariant evidence is not its substitute. The conditional legacy inference is defined by gate 1; the current manifest does not identify the producer version.

Measure: `SqlHarnessMeasureReport.MeasuredRunCount` is `runs.Count` after the 1..`Repeat` loop (`src/SqlHarness.Core/SqlHarnessModule.cs`). The summary copies only `Repetitions`. Measure-set: `SqlHarnessMeasureSetReport.MeasuredRunCount` is `repeat * sets.Count`, and `MeasureParameterSetReport.Repetitions` is `repeat` (`src/SqlHarness.Core/MeasureParameterSets.cs`). `MeasureSetBenchmarkSummary` has neither `Repeat` nor `MeasuredRunCount`. Confirmation of the projection: test `Measure_set_json_summary_projects_medians_labels_and_capped_operators_without_values_or_plans` expects the absence of the `measuredRunCount` and `repeat` properties in the JSON summary.

### Spread

In `src/SqlHarness.Core` there is no spread type or member. Status: `unknown`.

The absence of a saved spread does not block evaluation: gate 4 explicitly enumerates the time dispersion from the saved distributions; it does not reconstruct the raw samples:

- `CompareDistribution.Min`, `Median`, `Max` on the variant's `CpuTimeMilliseconds`, `ElapsedTimeMilliseconds` and `LogicalReads`. The summary copies them into `BenchmarkVariantSummary`, metrics into `ArtifactNamedMetrics`.
- `Distribution.From` (`src/SqlHarness.Core/Diagnostics.cs`) writes the median as `long`. With an even number of samples it truncates the average of the two middle ones to an integer. There is no fractional median in `CompareDistribution`.
- `runs.jsonl` has `long` samples per run. The selective reader does not return them.

### Time, CPU and logical reads availability

The common carrier is `BenchmarkMetricReport` (`src/SqlHarness.Core/Artifacts.cs`):

- `ElapsedTimeAvailability`
- `CpuTimeAvailability`
- `LogicalReadsAvailability`

The source strings are `BenchmarkMetricReport.Measured` (`"measured"`) and `BenchmarkMetricReport.Unavailable` (`"unavailable"`). Zero in `CompareDistribution` is neither of these strings. On PostgreSQL `CpuTimeMilliseconds` equal to 0 is not evidence that CPU was measured.

`CompareVariantReport.MetricReport` is `BenchmarkMetricReport?` and has `JsonIgnore` when null. `BenchmarkReports.CreateVariantReport` (`src/SqlHarness.Core/BenchmarkRunner.cs`) sets it from `BenchmarkMetricReport.FromArtifacts`. `BenchmarkSummaryProjector.ProjectVariant` copies the object into `BenchmarkVariantSummary.MetricReport`. `ArtifactReader.Of` copies it into `ArtifactNamedMetrics.MetricReport`. `HasMissingMembers` does not require `MetricReport`: a report without that property passes the read, and the copy stays null. The `ArtifactReaderTests.CompareReport` fixture writes the variant exactly that way and reads the sections.

When `MetricReport` is null, all three availability facts are `unknown`. UNPROVEN until the variant in `report.json` has a non-null `metricReport`, and the summary or metrics section preserves the corresponding `*Availability`.

When `MetricReport` is not null, the fact is `present` and both reads preserve the same object. The origin of the string that the code actually writes:

- SQL Server, ordinary run. `SqlServerDialect.TruncatedMetricsOrNull` (`src/SqlHarness.Core/Dialect/SqlServerDialect.cs`) returns null when `omittedMessageCount <= 0`. If every run has a null `CompareRunArtifact.Metrics`, `FromWholeMilliseconds` writes all three strings as `measured`. This is a written string, not a read of zero. `BenchmarkRunMetrics` themselves are not in `runs.jsonl`.
- SQL Server, truncated messages (`omittedMessageCount > 0`). The same helper writes all three as `unavailable`. `FromRunMetrics` yields `unavailable` when any run has the given string `unavailable`.
- PostgreSQL, `PostgresBenchmark.ParseStats` (`src/SqlHarness.Core/Postgres/PostgresBenchmark.cs`). `CpuTimeAvailability` is always `unavailable`. The returned `CpuTimeMs` is 0. The text `BenchmarkMetricText.PostgresCpuUnavailable` states plainly that this zero is not a measured zero; the text goes to `Warnings`, but the availability fact is the string, not a warning. Time is `measured` only when the plan has both `Planning Time` and `Execution Time`; otherwise `unavailable`, and the whole milliseconds are 0. `LogicalReadsAvailability` is `measured` only when the root buffer counters of the plan are present; otherwise `unavailable`, and `logicalReads` 0 is not a measured zero (`BenchmarkMetricText.PostgresBuffersMissing`).

Neighbouring members that are not availability: `ElapsedTimeMillisecondsExact`, `ElapsedWholeMillisecondsAreExact`, `PlanningTimeMilliseconds`, `ExecutionTimeMilliseconds`, `LogicalReadsSource`. A positive time below one millisecond can yield `CompareDistribution` equal to 0 with `ElapsedTimeAvailability` = `measured` and `ElapsedWholeMillisecondsAreExact` = false. `SqlHarnessTargetIdentityReport.Engine` is on the report and in the summary (`Target`) and is omitted from JSON when null. The absence of `Engine` does not decide CPU availability.

Measure-set has a second, different carrier: `MeasureCrossSetSummary.CpuTimeAvailability`, `ElapsedTimeAvailability`, `LogicalReadsAvailability`. The initializer of each of them is `measured`. `Summarize` treats a missing `MetricReport` (`?.`) as "not unavailable", and on unavailable inserts placeholder zeros and a comment that these zeros are not a ranking. The measure-set summary copies the whole `CrossSetSummary`. Metrics copies the set's `MetricReport`, not that cross object. This is not the carrier of a compare pair.

### Equivalence

Carrier of the compare pair:

- `SqlHarnessCompareReport.ResultsEquivalent` (`bool?`)
- `SqlHarnessCompareReport.Equivalence` of type `ResultEquivalenceReport` (`src/SqlHarness.Core/ResultEquivalence.cs`): `Mode` (`ResultComparisonMode`: `Ordered`, `Multiset`, `Set`, `Off`), `Equivalent`, `DifferingPositions`, `BaselineOnlyCount`, `CandidateOnlyCount`

`CompareCellRunner` inserts the result of `ResultComparer.Compare` into `Equivalence` and `equivalence.Equivalent` into `ResultsEquivalent` before writing. The `Off` mode returns `Equivalent` and the three counters as null. In JSON `Mode` is a number.

The compare summary copies the whole `Equivalence` and has no separate `ResultsEquivalent`. Compare metrics inserts `report.Equivalence` into `ArtifactMetricsSection.Equivalence`. Measure and measure-set metrics leave null; test `MeasureAndMeasureSet_AllSectionsRead` checks this. The measure summary contains no `equivalence` property (test `BenchmarkSummaryTests`).

The initializer of `Equivalence`, when the code does not overwrite it, is `Ordered`, `Equivalent` copied from `ResultsEquivalent` and three counters 0. Test `Compare_report_json_projects_results_equivalent_and_equivalence_for_ordered_default` serializes exactly those zeros. `HasMissingMembers` rejects null, so the absence of a property in JSON is not a read rejection: the initializer stays. A zero counter does not prove that `ResultComparer` counted no differences, nor that the property was in the file.

Status of the compare pair: `present`. The object is on the report and in both sections.

UNPROVEN for the origin of the zeros: the evidence is the `equivalence` property written by `CompareCellRunner` after `ResultComparer.Compare`. The selective read does not distinguish the initializer from a written `Ordered` with zeros. I did not open artifacts from disk.

Measure and measure-set have no `Equivalence`. The equivalence status for these kinds is: `unknown`. UNPROVEN until such a report writes a `ResultEquivalenceReport` and the summary or metrics section exposes it. `ResultsStable` is not this fact.

### Stability

`SqlHarnessCompareReport` and `CompareBenchmarkSummary` have neither `ResultsStable` nor any other stability verdict member. The status of compare pair stability: `unknown`.

The absence of that member does not block gate 4, which enumerates the time dispersion. Equivalence at gate 3 separately checks the conformity of results.

Measure has a different fact, with a different name and a different meaning. `SqlHarnessMeasureReport.ResultsStable` is set as the equality of a single `ResultHash` across measured runs (`SqlHarnessModule`). The summary exposes `MeasureBenchmarkSummary.ResultsStable`. Metrics has no such member.

Measure-set: `MeasureParameterSetReport.ResultsStable` is the same `ResultHash` equality inside the set (`MeasureParameterSetReportProjector.ProjectSet`). When the digests differ, the set's saved `ResultHash` is null. The summary copies `MeasureParameterSetSummary.ResultsStable` and does not copy `ResultHash`. Metrics does not copy `ResultsStable`. `runs.jsonl` has `ResultHash` per run; the selective reader does not return it.

None of these measure members is a compare pair stability verdict and none is a time spread.

## Decision order

Evaluation works offline for a single compare pair. The verdict means the outcome of the time regression check, not an improvement of all costs. The rules of this section replace the conflicting rules of the older specification, including the mandatory corroboration.

Change of 2026-10-11 (HEAD 729d88f): a stable 100 ms → 1000 ms at constant reads and CPU must yield `fail`. Costs are diagnostics; their absence or improvement does not invalidate the time measurement. The thresholds remain unchanged.

### Gate 1. Completeness and run count

- A readable measure, measure-set or rollup input: `inconclusive`. A matrix cell written as compare is an ordinary pair. Do not add a new manifest kind for rollup; the current reader rejects it.
- Compare without summary or metrics: `inconclusive`.
- Prefer the saved `MeasuredRunCount` per variant. Both must be at least 5; the warm-up does not count. A current 0 is a number, not an absence.
- Legacy: deriving the counter may be applied only for a recognized producer version and schema whose documented and tested invariant of a successful write guarantees exactly `Repetitions` runs on each variant and a total `MeasuredRunCount = 2 * Repetitions`. Check a positive integer, field consistency and a minimum of 5.
- manifestVersion 1 alone, an even number, or the presence of Repetitions do not identify the producer. Today's manifest does not carry a producer version; there is currently no evidence for a general inference of old artifacts. Without recognition, or on conflicting counters: `inconclusive`, without an automatic `/ 2`.
- Source of the number in the result: `recorded`, `inferred-known-producer` (with the producer identifier and the invariant) or `unknown`. Do not overwrite the artifact with inferred data.

An unknown id, a missing manifest and an unreadable report remain ArtifactReader errors without a verdict.

### Gate 2. Time availability

`MetricReport` and the exact token `ElapsedTimeAvailability = measured` (ordinal comparison) are required on both variants. Missing, null, unavailable, empty or a foreign token yields `inconclusive`. Zero does not prove availability.

Reads and CPU are not required. The diagnostics of a given metric are available only when both variants have its measured token. Otherwise its outcome is `unavailable`. With Engine = postgres CPU is always unavailable, even for a synthetic measured token. A missing Engine does not mean postgres; do not guess from zero.

### Gate 3. Equivalence

Off, missing Equivalence, null Equivalent or an undefined mode enum: `inconclusive`. Equivalent false with Ordered, Multiset or Set: `inconclusive`, never a performance fail. Only true with one of those three modes passes on. The difference counters do not replace the equivalence outcome. The reader still rejects an unreadable object without a verdict.

### Gate 4. Time stability

For both variants require non-negative `Min <= Median <= Max`. Invalid distribution: inconclusive. For Median > 0 count `((decimal)Max - Min) / Median`; a result > 0.25 gives inconclusive, exactly 0.25 passes. The triple 0,0,0 passes as no observed dispersion of whole milliseconds. A median of 0 with Max > 0 gives inconclusive. Do not read runs.jsonl and do not call the time dispersion the stability of the result values.

### After the gates: zeros and thresholds

Time is evaluated on the saved median of whole milliseconds; the exact fields do not silently replace it.

| Baseline / candidate | Outcome |
|---|---|
| 0 / 0 | pass |
| 0 / positive <= 5 ms | pass — the floor is preserved for the quantization of short measurements |
| 0 / > 5 ms | fail on the basis of the absolute threshold; the relative change is undefined (null) |
| baseline > 0 / candidate >= 0 | fail if and only if candidate >= baseline * 1.10 AND candidate - baseline >= 5 ms; otherwise pass |

0 → 5 ms is an explicit exception to the positive-baseline threshold >= 5; 0 → 6 ms gives fail. Zeros do not bypass gates 1–4. Negative or inconsistent distributions are rejected earlier.

### Cost diagnostics

Report `metricOutcomes.elapsed`, `.logicalReads`, `.cpu` as `regressed`, `not-regressed`, `unavailable` or `inconclusive`, with medians, absolute and relative delta (null when baseline is 0). Elapsed reflects the verdict; at a gate of uncertainty it is inconclusive, and with unavailable time it is unavailable. The verdict comes exclusively from the time after the gates.

- Reads with baseline > 0: both thresholds candidate >= baseline * 1.10 and delta >= 100.
- CPU with baseline > 0: both thresholds candidate >= baseline * 1.15 and delta >= 5 ms.
- An available cost with baseline 0: 0/0 is not-regressed; a positive candidate is regressed with delta >= the metric floor, otherwise not-regressed; percentage null.
- Negative or inconsistent cost distribution: only its diagnostics are inconclusive; it does not block time.
- Growing costs without time regression do not give fail, but are visible as regressed. Operators and missingIndexes do not change the verdict.
- Use decimal before subtracting and multiplying, without overflowing long.

## Invariants

- Offline, without a DB target, connection, re-benchmark, SQL, plans and runs.jsonl.
- Minimum of 5 measured runs per variant, equivalence before the thresholds, time spread >25% gives inconclusive.
- Availability is a token; PostgreSQL CPU remains unavailable.
- For a positive baseline both thresholds apply together. The zero-baseline exceptions are defined above.
- A successful evaluation gives exit 0 also on fail/inconclusive; CI gates on the verdict. An argument/read error does not carry a verdict.
- Exit 8 remains snapshot --diff.
- regressionPolicyVersion = 1: this is a correction of the unreleased PLANNED function.

The full acceptance matrix is in plans/013-regression-implementation.md.

## Integration with the equivalence policy (plan 046)

Before implementing the decider, implement plan046. The default Equivalent evaluates the data and does not treat the allowNull change alone as a mismatch. The explicit --compare-nullability / compareNullability switches to the strict policy; its Equivalent result is still gate3. The decider uses the saved result of the selected policy, does not recompute the data and does not bypass a real mismatch. Integration test: identical data and a different allowNull pass gate3 under the default policy; under strict they give inconclusive. An older saved mismatch remains inconclusive, without reinterpretation of unknown data.
