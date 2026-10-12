# Plan 051: `compare` reports a latency regression verdict computed from its own samples

> Read current source before editing. Inspect drift since `cadde67` in the scope below and adapt implementation details within this intent. A changed file alone is not a STOP condition. Supersedes plan 035 and the offline-reader parts of the 013 design.

## Status

- Status: DONE (branch `feat/plan-051-regression-verdict`; verify.ps1 green at f21d4f0; verify-linux.ps1 skipped by user decision 2026-10-12)
- Priority: P2
- Effort: M
- Risk: LOW
- Depends on: 046 (DONE)
- Planned at: `cadde67`, 2026-10-11

## Why this matters

Agents read two timing distributions and guess whether a candidate regressed. Plan 013 designed the decision rules; plan 035 placed them in a new offline `regress <artifact-id>` command. Most of that design's complexity comes from deciding *after the fact* from a saved artifact: the per-variant run count is not stored, medians are truncated to whole milliseconds, there is no spread field, and legacy artifacts need a "producer invariant" proof or stay inconclusive.

At the end of `compare` every raw sample is in memory. Deciding there removes the run-count, spread, median-truncation and legacy problems, needs no new command, and puts the verdict into the output agents already read (`--json-summary`). An offline `regress` can be added later if a CI pipeline ever needs it; it would then read the stored verdict.

## Decision rules (normative, carried over from `plans/013-regression-contract.md` "Kolejność decyzji")

Evaluated per compare pair (each matrix cell is a pair). Output `verdict` is `pass`, `fail` or `inconclusive`, with `reasons` (stable codes).

1. **Enough runs**: fewer than 5 measured runs per variant → `inconclusive` (`too-few-runs`). Warm-ups do not count.
2. **Elapsed available**: both variants must have elapsed `measured`; otherwise `inconclusive` (`elapsed-unavailable`).
3. **Equivalence**: `--compare-results off`, or `Equivalent` not `true` under the selected policy (default or `--compare-nullability`) → `inconclusive` (`results-not-equivalent` / `results-not-compared`). Never a performance `fail`.
4. **Spread**: per variant, `(max - min) / median` over raw elapsed samples (decimal, exact median) above 0.25 → `inconclusive` (`unstable-elapsed`). A median of 0 with max > 0 → `inconclusive`.
5. **Threshold on medians** (exact median, ms):
   - baseline > 0: `fail` iff candidate ≥ baseline × 1.10 **and** candidate − baseline ≥ 5 ms; else `pass`;
   - baseline 0: candidate ≤ 5 ms → `pass`, above → `fail`.
6. **Cost diagnostics** (never change the verdict): `logicalReads` (≥ +10 % and ≥ +100) and `cpu` (≥ +15 % and ≥ +5 ms) reported as `regressed`, `not-regressed` or `unavailable`. PostgreSQL CPU is always `unavailable`.

Thresholds are constants in one place (`RegressionPolicy`), reported as `policyVersion: 1`. No thresholds file, no CLI threshold flags.

## Current state

- `src/SqlHarness.Core/CompareCellRunner.cs` collects `runs` for both variants and writes `SqlHarnessCompareReport` (`Artifacts.cs`).
- `BenchmarkSummary.cs` projects `--json-summary`; `ArtifactReader` reads saved sections.
- Elapsed availability lives in `BenchmarkMetricReport`; equivalence in `ResultEquivalenceReport` (with 046's `compareNullability`).
- The journal stores per-variant medians for successful compares (`Journal/`).

## Scope

In scope: new `src/SqlHarness.Core/RegressionPolicy.cs` (pure function over raw samples + metric availability + equivalence), `CompareCellRunner.cs`, `Artifacts.cs` (`Regression` property on the compare report), `BenchmarkSummary.cs`, `ArtifactReader.cs` (summary section copies it), CLI text output for compare, MCP compare result (it reuses the summary), tests, README/AGENTS compare section, `Capabilities.cs` if it describes compare output.

Out of scope: a `regress` command, an MCP tool, any change to exit codes, reading `runs.jsonl`, thresholds configuration.

## Steps

1. RED tests for `RegressionPolicy` as a data-driven `[Theory]`: 100 → 1000 ms with equal reads/CPU → `fail`; 100 → 105 → `pass`; 0 → 5 → `pass`; 0 → 6 → `fail`; 4 runs → `inconclusive`; spread 0.26 → `inconclusive`, exactly 0.25 → passes the gate; elapsed unavailable; equivalence off / false / strict-nullability mismatch; PostgreSQL CPU `unavailable`; reads regressed while elapsed passes → `pass` with `logicalReads: regressed`.
2. Implement `RegressionPolicy.Evaluate`. No I/O.
3. Call it in `CompareCellRunner` after equivalence; store `Regression { Verdict, Reasons, PolicyVersion, Elapsed, LogicalReads, Cpu }` on the report. Artifacts written before this change simply lack the property; readers treat that as "no verdict" without inference.
4. Project it into `--json-summary` and the MCP compare result (bounded: verdict, reasons, three diagnostics with medians and deltas). CLI text: one line `Regression: fail (elapsed +900 ms, +900 %)`.
5. Docs: one short paragraph in the compare section. State that exit code stays 0 for a valid compare whatever the verdict, and that the verdict is a latency check, not domain equivalence.

## Verification

- `dotnet build SqlHarness.sln -warnaserror` — 0 warnings.
- `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~Regression|FullyQualifiedName~Compare|FullyQualifiedName~BenchmarkSummary|FullyQualifiedName~ArtifactReader"` and `dotnet test tests/SqlHarness.Mcp.Tests --no-build --filter "FullyQualifiedName~Compare|FullyQualifiedName~McpTokenBudget"` — pass.
- Final `scripts/verify.ps1` → `verify: OK`; `scripts/verify-linux.ps1` → `verify-linux: OK`.

## Done criteria

- [x] Every rule above has a test row.
- [x] `compare --json-summary` and MCP compare carry the verdict; matrix cells each carry their own.
- [x] No new command, MCP tool or exit code.
- [x] Plans 035 and 013 marked superseded in `plans/README.md`.
- [x] Windows gate passes (`verify.ps1`: 3773 + 385 tests, format OK). Linux gate (`verify-linux.ps1`) skipped by user decision.

## STOP conditions

- The MCP compare result exceeds its default 16384 B budget in existing budget tests after adding the verdict; report sizes before trimming other fields.
