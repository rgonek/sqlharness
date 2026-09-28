# Benchmark regression decision policy (plan 06/T4, spec v1)

Status: specification only. No production code, no stubs, no exit-code changes.
Decisions here are a reviewable contract for a future implementation task.

## 1. Scope and non-goals

- This policy defines how a baseline/candidate benchmark pair maps to a domain
  verdict: `pass`, `fail` (regression), or `inconclusive`.
- The domain verdict is a payload field. It is independent of process success:
  SQL executed successfully (exit `0`) can still carry `fail` or `inconclusive`.
- Exit codes `0/2/3/4/5/6/7/8` keep their current contract. Exit `8` remains
  exclusively `snapshot --diff found differences`; this policy never redefines it.
- Metric source is an already-saved benchmark artifact, read offline via the
  existing `artifact <id> --section summary|metrics|operators` sections (added
  in 06/T2). Regression evaluation never re-runs a benchmark.
- Never claim a regression when results are technically non-equivalent or the
  measurement is incomplete. Those cases are `inconclusive` by construction
  (section 4, rules R5–R7).

## 2. Inputs

| Input | Source | Required |
|---|---|---|
| Baseline median elapsed, per-variant measured runs | artifact `metrics` section | yes |
| Candidate median elapsed, per-variant measured runs | artifact `metrics` section | yes |
| Baseline/candidate logical reads | artifact `metrics` section | yes |
| CPU time | artifact `metrics` section, SQL Server only | no (see R3) |
| Equivalence outcome (`ordered`/`multiset`/`set`) | compare report | yes, unless mode `off` (see R6) |
| Stability verdict / run spread | artifact `summary` section | yes |
| Policy version | threshold file (section 6) | yes, `regressionPolicyVersion: 1` |

Engine facts this policy inherits (see `AGENTS.md`): on PostgreSQL
`CpuTimeMs` is `0` (no CPU metric), `logicalReads` are buffer hits+reads, and
`missingIndexes` is empty. Fingerprint comparison retains at most 1,000,000
rows per measured variant run; warm-ups never participate.

## 3. Thresholds v1 (`regressionPolicyVersion: 1`)

A metric regresses only when **both** gates fire (relative AND absolute):

| Metric | Relative gate | Absolute floor | Notes |
|---|---|---|---|
| Median elapsed | candidate ≥ baseline × 1.10 (+10%) | candidate − baseline ≥ 5 ms | primary signal |
| Logical reads (median) | candidate ≥ baseline × 1.10 (+10%) | candidate − baseline ≥ 100 pages/buffers | secondary signal |
| CPU time (median, SQL Server only) | candidate ≥ baseline × 1.15 (+15%) | candidate − baseline ≥ 5 ms | corroborating; never sufficient without an elapsed regression |

Overall: `fail` requires an elapsed regression (both gates, R8) **plus**
corroboration from at least one cost signal: logical reads regressing (both
gates), or — SQL Server only — CPU time regressing (both gates). On
PostgreSQL the CPU dimension does not exist, so corroboration can only come
from reads. CPU is therefore corroborating but never sufficient: without an
elapsed regression the verdict can never be `fail`, with or without CPU.
A metric firing only one of its two gates is noise and never counts as a
regression signal (see cases N2, P3).

## 4. Decision matrix

Rules R1–R2 and R4–R9 are terminal: the first matching rule decides. R3 is
a modifier, not a terminal rule: on PostgreSQL it drops the CPU dimension and
evaluation continues at R4. A PG `unstable` sample therefore reaches R4 and
is `inconclusive`, not decided by R3.

| Rule | Condition | Verdict |
|---|---|---|
| R1 zero/zero | baseline median == 0 and candidate median == 0 (elapsed) | `pass` |
| R2 zero baseline | baseline median == 0, candidate median > 0 | `pass` iff candidate ≤ absolute floor (5 ms), else `inconclusive` (relative gate undefined; never `fail` from a zero baseline) |
| R3 (modifier) PG CPU | engine `postgres`: CPU dimension ignored entirely (`CpuTimeMs` is constant `0`); a policy config referencing CPU on PG marks the CPU dimension `unavailable`, never a decision input | — (no verdict; continue at R4, corroboration from reads only) |
| R4 unstable sample | stability verdict != stable, or (max − min)/median > 25% on either variant's measured runs | `inconclusive` |
| R5 incomplete measurement | fewer than 5 measured runs per variant, missing `metrics`/`summary` sections, or legacy artifact without manifest | `inconclusive` |
| R6 equivalence off/unknown | `--compare-results off`, or equivalence outcome missing | `inconclusive` |
| R7 non-equivalence | equivalence outcome is mismatch under the selected mode | `inconclusive` (never `fail`: a different result is not a slower same result) |
| R8 regression | R1–R2, R4–R7 do not fire; elapsed regresses (both gates) AND at least one corroborating signal regresses (reads both gates; or CPU both gates on SQL Server) | `fail` |
| R9 otherwise | R1–R2, R4–R8 do not fire | `pass` |

`missingIndexes` (always empty on PG) and noteworthy operators (≤10, capped)
are diagnostic context only; they never change the verdict.

## 5. Synthetic cases and expected decisions

Thresholds: elapsed +10% and +5 ms; reads +10% and +100 buffers; unstable iff
spread > 25%; minimum 5 measured runs; equivalence mode `ordered` unless noted.

| # | Baseline (elapsed med / reads med) | Candidate (elapsed med / reads med) | Extra | Expected |
|---|---|---|---|---|
| P1 | 100 ms / 1000 | 102 ms / 990 | stable, equivalent | `pass` (within noise) |
| P2 | 100 ms / 1000 | 105 ms / 1010 | stable, equivalent | `pass` (relative under 10%) |
| P3 | 500 ms / 10000 | 510 ms / 10050 | stable, equivalent | `pass` (absolute-only moves, +10 ms / +50 buffers, but neither relative gate fires → noise, no signal) |
| F1 | 100 ms / 1000 | 120 ms / 1150 | stable, equivalent | `fail` (elapsed both gates + reads corroboration, +15% / +150 buffers) |
| F2 | 200 ms / 5000 (CPU 50 ms) | 230 ms / 4900 (CPU 70 ms) | stable, equivalent (SQL Server) | `fail` (elapsed both gates + CPU corroboration, +40% / +20 ms; reads improved, which blocks nothing) |
| N1 | 100 ms / 1000 (CPU 20 ms) | 112 ms / 1005 (CPU 20 ms) | stable, equivalent (SQL Server) | `pass` (elapsed regresses but no corroboration: reads neutral, CPU flat → R9) |
| N2 | 4 ms / 50 | 8 ms / 55 | stable, equivalent | `pass` (relative-only moves: elapsed +100% but +4 ms under the floor; reads +10% but +5 buffers under the floor → noise, no signal) |
| Z1 | 0 ms / 0 | 0 ms / 0 | stable, equivalent | `pass` (R1) |
| Z2 | 0 ms / 0 | 30 ms / 200 | stable, equivalent | `inconclusive` (R2; never `fail` from zero baseline) |
| Z3 | 0 ms / 0 | 3 ms / 10 | stable, equivalent | `pass` (R2, under absolute floor) |
| G1 | 100 ms / 2000 (PG) | 118 ms / 2300 | PG, CPU `0/0`, stable, equivalent | `fail` (R3: CPU ignored, elapsed+reads decide) |
| G2 | 100 ms / 2000 (PG) | 102 ms / 2010 | PG, policy config mentions CPU | `pass`, CPU dimension `unavailable` (R3) |
| S1 | 100 ms / 1000 | 130 ms / 1300 | spread 40% on candidate | `inconclusive` (R4) despite gates firing |
| M1 | 100 ms / 1000 | 150 ms / 1500 | only 3 measured runs | `inconclusive` (R5) |
| E1 | 100 ms / 1000 | 120 ms / 1150 | `--compare-results off` | `inconclusive` (R6) despite gates firing |
| E2 | 100 ms / 1000 | 120 ms / 1150 | `ordered` mismatch (row order differs) | `inconclusive` (R7; a different result is not a regression) |
| E3 | 100 ms / 1000 | 120 ms / 1150 | `multiset` equivalent (same rows, order differs) | `fail` (equivalent under selected mode → R8) |

Note on F2 vs N1: both pairs show an elapsed regression with neutral-or-better
reads, and only the corroborating signal separates them — F2 fails because CPU
corroborates (+40% / +20 ms), N1 passes with CPU flat and reads neutral.
Elapsed alone never fails (P3: absolute-only moves are noise); a single firing
gate never counts as a signal (N2: relative-only; P3: absolute-only).

## 6. Noise policy, repeat counts, threshold versioning

- Minimum 5 measured runs per variant; CI default 7. Warm-ups excluded from
  every statistic (as today). Verdict uses medians, never means.
- Multi-set (`--param-set`) measurements rotate on one shared session and
  share the plan cache (order-dependent timings); regression verdicts apply
  only to single-scenario `compare` pairs, never across parameter sets and
  never across `--matrix` cells (each cell is its own pair with a fresh
  connection; first failure stops the run, completed cells keep artifacts).
- Re-run rule: an `inconclusive` caused by R4 (instability) may be retried
  once with `--repeat` raised; two consecutive unstable verdicts stay
  `inconclusive` — do not keep re-running until green.
- Thresholds are versioned. The evaluating input carries
  `regressionPolicyVersion`; v1 is defined in section 3. Any threshold change
  bumps the version; saved artifacts keep the version they were evaluated
  with. CI pins the version explicitly; `latest` is not a valid selector.

## 7. CI integration proposal (no exit-code changes)

Recommended: a new **offline command** (option B), decided in product review
before implementation:

- Option A (not recommended): `compare --regression-policy v1` flag. Rejected
  rationale: couples measurement with judgment; tempts exit-code signalling.
- Option B (recommended): `regress <artifact-id> --policy v1 --json`, offline
  like `artifact` (no target, no connection, no rebenchmark). It reads the
  saved artifact's `summary`/`metrics` sections, applies this policy, and
  prints `{ "verdict": "pass|fail|inconclusive", "rule": "R1,R2,R4..R9",
  "regressionPolicyVersion": 1, ... }` with process exit `0` on success.
  R3 never appears as `rule`: it is a modifier, not a verdict.
  SQL/execution failures surface as exits `2/5/6` per the existing contract;
  a domain `fail` never changes the exit code — CI gates on the `verdict`
  field, not on process exit.
- Both options leave exit `8` untouched (`snapshot --diff` only).

## 8. Domain verdict vs SQL success

| Process exit | Meaning | Domain `verdict` field |
|---|---|---|
| `0` | SQL executed, report/artifact written | any of `pass` / `fail` / `inconclusive` |
| `2/3/4/5/6` | validation / auth / target / SQL / storage failure | absent (no verdict; nothing to judge) |
| `7` | `watch` deadline | not applicable |
| `8` | `snapshot --diff` differences | not applicable; never a regression verdict |

## 9. Implementation plan (future files; not created by this task)

1. `src/SqlHarness.Core/RegressionPolicy.cs` — threshold set + version
   (`regressionPolicyVersion`), deserialization and version pinning.
2. `src/SqlHarness.Core/RegressionDecider.cs` — rules R1–R9 over artifact
   `summary`/`metrics` projections; pure function, no I/O.
3. `src/SqlHarness.Cli/Commands/RegressCommand.cs` — offline `regress`
   command (option B); CLI registration alongside `artifact`.
4. `tests/SqlHarness.Tests/RegressionDeciderTests.cs` — one test per row of
   the section 5 table (P1–E3, now including P3) plus version-pinning tests.
   Fixtures carry CPU medians wherever the verdict depends on them (F2, N1).
5. `tests/SqlHarness.Tests/Cli/RegressCommandTests.cs` — offline tests:
   unknown id/section, legacy artifact, exit codes unchanged, verdict field
   present with exit `0` on `fail`.
6. `Capabilities.cs` + docs (`AGENTS.md`, `README.md`, skill) — advertise
   `regress` only after real implementation lands (same rule as 06/T5).

Review focus for the implementer: unstable sample / unavailable metric paths
(R3 modifier, R4–R5) must stay `inconclusive`, never `pass` or `fail`.
