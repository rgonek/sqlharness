# Implementation plan of the offline regression decider (013/T4)

> **2026-10-11:** the decision thresholds here are reused by [051](051-compare-regression-verdict.md). Everything about reading saved artifacts offline (run-count inference, legacy producer proof, `regress` command) is superseded.

Status: **PLANNED**. Revision 2026-10-11 on HEAD `729d88f`. It implements no commands, stubs or capabilities. Behavior authority: `plans/013-regression-contract.md`; that contract replaces the conflicting rules of the older `docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md`, especially the mandatory corroboration.

## Goal and current state

A stable measurement of 100 → 1000 ms is a time regression at constant or unavailable reads/CPU. `fail` concerns the time after the completeness, availability, equivalence and stability gates. `pass` is not a guarantee of improvement of all costs.

`CompareCellRunner` writes the combined MeasuredRunCount of both variants and Repetitions, but not a per-variant counter. ArtifactReader reads manifest/report, not runs.jsonl. Manifest v1 does not identify the producer version. PostgreSQL CPU is unavailable. Distributions write long Min/Median/Max; the median of an even count may be truncated. Preserve these boundaries.

## Scope of the future implementation

New files:

- `src/SqlHarness.Core/RegressionPolicy.cs`: v1 only, time thresholds +10% AND 5ms, reads +10% AND100, CPU +15% AND5ms, minimum5, spread25%.
- `src/SqlHarness.Core/RegressionDecider.cs`: a pure function, no I/O, with the order and zero cases from the contract.
- `src/SqlHarness.Cli/Commands/RegressCommand.cs`: offline `regress <artifact-id> --policy v1 --json`.
- `tests/SqlHarness.Tests/RegressionDeciderTests.cs` and `tests/SqlHarness.Tests/Cli/RegressCommandTests.cs`.

Existing files:

- `src/SqlHarness.Core/Artifacts.cs`: optional `int? MeasuredRunCount` init on CompareVariantReport; JsonIgnore when null.
- `src/SqlHarness.Core/BenchmarkSummary.cs`: the same on BenchmarkVariantSummary; ProjectVariant copies.
- `src/SqlHarness.Core/ArtifactReader.cs`: the same on ArtifactNamedMetrics; Of copies. Add a public read of the verified manifest through the existing id/link/size checks. HasMissingMembers does not require the new number.
- `src/SqlHarness.Core/BenchmarkRunner.cs`: CreateVariantReport sets the number to runs.Count of the received list; the warm-up does not count.
- `src/SqlHarness.Cli/SqlHarnessCli.cs`: registration next to ArtifactCommand.
- Existing ArtifactReader, BenchmarkSummary, benchmark and CLI capabilities tests plus `Capabilities.cs`, README.md, AGENTS.md and the documentation skill only in the final phase of advertising the already existing command.

Out of scope: MCP, DB, thresholds from the file, XML/SQL plans, changes to the existing data comparison and reading runs.jsonl. The feature status remains PLANNED until the implementation of plan035.

## Counters and legacy compatibility

Write an optional per-variant number. A missing key deserializes to null; a current 0 stays 0. Do not change the types of existing fields and do not write a spread. Null does not make the artifact unreadable.

The source of the number in the result is `recorded`, `inferred-known-producer` or `unknown`. Inference requires a recognized producer/schema and a tested success invariant: equal variant counters = Repetitions, total counter =2*Repetitions, warm-up excluded. Check the consistency of both fields; `/2` alone or manifestVersion1 is not enough. Do not add a fictitious producer to older artifacts. Present legacy without identification gives inconclusive. If the implementer finds real producer metadata, they document the concrete version and the evidence and add the corresponding fixture; without it they do not run the inference.

## Decider

Implement the contract: kind/sections/counters → time availability → equivalence → distributions and time spread → zeros/thresholds. Costs are optional and do not affect the verdict. The instability gate uses decimal and `(Max-Min)/Median >0.25`; it requires `0 <= Min <= Median <= Max`. 0,0,0 passes, median0/Max>0 does not pass.

For a positive elapsed baseline: fail when candidate >=baseline*1.10 AND delta>=5ms. For baseline0: 0/0 pass, 0/candidate<=5 pass, 0/candidate>5 fail with a null percentage. The exact properties do not replace the long median. Diagnose reads/CPU according to the contract, preserving unavailable PG CPU and a null percentage at zero. An invalid cost has only inconclusive diagnostics.

## Command and result

The id argument is a directory name as in ArtifactCommand, not an arbitrary path. `--policy v1 --json` is mandatory, without profile/var/unsafe-direct. A bad policy (also V1/latest/v2), a missing id or json: exit2 without a verdict. Regression does not take an ISqlHarnessModule.

Use the public read of the verified manifest: measure/measure-set gives inconclusive before the section read; compare without summary/metrics gives inconclusive. When the sections are there, read via ReadSection. `artifact --section` still rejects a missing section. Do not add a rollup kind; the test of that shape concerns only the pure decider.

A successful JSON contains `verdict`, `regressionPolicyVersion:1`, `metricOutcomes` and `runCountEvidence`. metricOutcomes has elapsed/logicalReads/cpu with `outcome`, medians, `absoluteDelta`, `relativeDelta` (null at baseline0). A missing metric has no fictitious medians/deltas: null. At a gate inconclusive do not evaluate costs on unverified input: return their inconclusive or unavailable according to availability. runCountEvidence has baseline/candidate: count nullable, source, and only for inference producer and invariant. Add a stable `reason` for a gate non-pass; do not use the historical R1–R9 as the order.

A successful evaluation has exit0, also on fail/inconclusive. There is no exit8 and no artifact change. Pass through ArtifactReadException.ExitCode. IOException/UnauthorizedAccessException outside the reader: exit6. A missing manifest, a broken report, traversal/link, a bad version/kind remain the existing reader errors (exit2 without a verdict); IO storage exit6. Unknown id: `Unknown artifact id.`

## CI example

```powershell
$raw = sqlharness regress $artifactId --policy v1 --json
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$verdict = (ConvertFrom-Json -InputObject $raw).verdict
if ($verdict -ne 'pass') { Write-Error "regression verdict is $verdict"; exit 1 }
```

## Tests

The tests are future; the documentation revision does not run them. Patterns: ArtifactCommandTests (synthetic SQLHARNESS_HOME), ArtifactReaderTests, BenchmarkSummaryTests. The CLI fake module throws at ExecuteAsync, to detect an attempt to reach the DB. A complete pair: compare with both sections, saved count5 per variant, time measured, Ordered Equivalent true, Min=Median=Max, reads/CPU measured and constant as long as the row does not change.

| Id | Input | Expectation |
|---|---|---|
| Z1 | 0/0, triples0 | pass |
| Z2 | 0/30, stable | fail, percentage null |
| Z3 | 0/3 | pass |
| Z-boundary | 0/5 and0/6 | pass and fail |
| negative | Negative median of either variant | inconclusive |
| invalid-distribution | Negative Min, Min>Median or Median>Max | inconclusive |
| C-mismatch-zero | 0/0,0/3,0/30 with Equivalent false | all inconclusive |
| C-off-zero | 0/0 and Off | inconclusive |
| C-equivalence-null | Null Equivalent with Ordered; decider also null object | inconclusive; null object in file = reader error2 |
| undefined-mode | Unknown enum with0/0 or100/120 and Equivalent true | inconclusive |
| C-shape | Missing summary or metrics | inconclusive exit0 |
| C-count | Missing number without a known producer or count4 | inconclusive |
| C-elapsed | MetricReport null or time token unavailable/empty/foreign | inconclusive; elapsed unavailable |
| C-cost-availability | 100/1000 with reads/CPU unavailable, empty or a foreign token | fail; the corresponding costs unavailable |
| C-unstable-zero | 0/3, candidate spread>25% | inconclusive |
| C-undefined-zero | Median0 and Max1 | inconclusive |
| spread-boundary | Min90 Median100 Max115 | passes (25%) |
| C-first-match | Count unknown and mismatch | the reason of the first gate |
| C-kind | measure/measure-set/rollup in the decider | inconclusive; only the first two via CLI |
| P1 | Time100/102 | pass |
| P2 | Time100/105 | pass; the absolute threshold alone |
| P3 | Time500/510 | pass |
| F1 | Time100/120, reads1000/1150 | fail; both outcomes regressed |
| F2 | Time200/230, reads falling, CPU50/70 | fail; CPU regressed |
| N1 | Time100/112, costs constant | fail; costs not-regressed |
| latency-only | Time100/1000, reads/CPU constant | fail |
| elapsed-thresholds | Time50/55 and4/8 | fail (exactly both thresholds) and pass (delta4ms) |
| cost-only | Time100/102, reads1000/1500 and CPU20/40 | pass, costs regressed |
| G1 | PG100/118, CPU unavailable | fail |
| G2 | PG100/102, CPU unavailable | pass |
| pg-cpu-measured | PG200/230, CPU token measured and50/70 | fail; CPU unavailable despite the token |
| engine-absent | Engine null, time200/230, CPU measured | fail; CPU diagnostic according to the token |
| S1 | Candidate Min100 Median130 Max152 | inconclusive (>25%) |
| M1 | Count3 on both | inconclusive |
| E1 | Off with100/120 | inconclusive |
| E2 | Equivalent false with100/120 | inconclusive |
| E3 | Multiset Equivalent true with100/120 | fail |
| cost-zero | Reads0/99 and0/100; CPU0/4 and0/5 | not-regressed/regressed; percentage null |
| cost-invalid | Time100/120, cost negative or inconsistent | fail, only the cost inconclusive |
| legacy-missing-count | Count null, Repetitions5, total10, manifestv1 without producer version | inconclusive; no automatic /2 |
| legacy-proven | Fixture of a recognized producer with invariant evidence; consistent5/10 | count5 inferred-known-producer; normal time verdict |
| legacy-inconsistent | Recognized producer, Repetitions5,total9 or12 | inconclusive |
| count-zero | Saved number0 | source recorded, inconclusive; not null |
| count-json | Key absent/0/new writer | null/0/count copied to both sections |
| unknown-id | Unknown id/traversal/link | error2 without verdict |
| missing-manifest | Directory without a manifest | error2 without verdict |
| unreadable | Unreadable report or null Equivalence | error2 without verdict |
| storage | IOException | error6 without verdict |
| CLI-options | Missing id/json, policy other than v1 | error2 without verdict |

legacy-proven is a test of the decider's conditional branch with an explicit description of the evidence, not a CLI permission to accept any v1. If no recognizable version of real legacy exists, the CLI must return unknown; do not fabricate a version.

The CLI test additionally checks metricOutcomes, runCountEvidence, reason, fail with exit0, no SQL I/O, and a manifest with only operators: regress inconclusive exit0, artifact summary error2.

## Verification of the future implementation

- `dotnet build SqlHarness.sln -warnaserror` → exit0, no warnings.
- `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~RegressionDeciderTests|FullyQualifiedName~RegressCommandTests|FullyQualifiedName~ArtifactReader|FullyQualifiedName~BenchmarkSummary"` → all pass.
- `pwsh -NoProfile -File scripts/verify.ps1` and `pwsh -NoProfile -File scripts/verify-linux.ps1` → both gates exit0; Windows ends with `verify: OK`, the Linux wrapper ends with `verify-linux: OK`; check the full result and the absence of FAILED. These scripts do not print VERIFICATION PASS.

## Evidence boundaries and maintenance

There is no new runtime/live/platform evidence; this is a plan. We do not reconstruct the median from runs.jsonl. Rollup currently has no readable manifest. Without a producer version the counters of all legacy artifacts cannot be proven. A synthetic PG+CPU measured case checks the decider's robustness.

Drift requires reading the current symbols and adapting the mechanics within the boundaries of this contract. Stop only on a step that requires a conflicting product decision, unassured producer evidence, or going beyond authorization. A file change alone is not a reason to STOP.

## Integration with the equivalence policy (plan 046)

Before implementing the decider, implement plan046. The default Equivalent evaluates the data and does not treat the allowNull change alone as a mismatch. The explicit --compare-nullability / compareNullability switches to the strict policy; its Equivalent result is still gate3. The decider uses the saved result of the selected policy, does not recompute the data and does not bypass a real mismatch. Integration test: identical data and a different allowNull pass gate3 under the default policy; under strict they give inconclusive. An older saved mismatch remains inconclusive, without reinterpretation of unknown data.
