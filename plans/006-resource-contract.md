# 006 — equivalence resource contract (result comparison)

Purpose: an explicit budget for the whole result-comparison operation (`compare`), binding
for the implementation (T3) and capabilities (T5). Status: defined, to be
enforced. Base: `df0e2bf`. Ruling R2 from the ledger: this document
binds tasks T3/T5 — the numbers and rules below must be implemented
exactly as written.

## 1. Per-run envelope (existing, preserved)

`CanonicalComparisonAccumulator.MaximumComparedRows == 1_000_000` rows
per single measured run. Exceeding it throws `SqlHarnessSafetyException`
(fails closed) — data is never truncated.

## 2. Total budget per cell (new, to be enforced in T3)

`MaxUniqueComparisonFingerprintsPerCell == 2_000_000` (2 × the per-run limit).
Counts UNIQUE stored fingerprints within a single compare cell
(baseline + candidate, all measured runs). Shared representations
of identical runs are counted once — deduplication is rewarded, not
penalized. Histogram entries fit within the same counter
(at most one entry per unique fingerprint).

## 3. Accounting

Every measured run with `captureComparison == true` registers its
rows against the cell budget. Warm-up (repetition 0) and mode
`ResultComparisonMode.Off` register nothing (they store no fingerprints).

## 4. Rejection without truncation

Exhausting the budget throws `SqlHarnessSafetyException`. The message contains
the words `comparison budget` and both numbers: used and limit. It NEVER returns
`Equivalent == true` on truncated data — rejection = exception, no report
(fail closed).

## 5. Presentation vs equivalence

Presentation limits (`--max-rows` query, agent/MCP output budgets) truncate
only what is displayed; they never feed the equivalence path. Equivalence
always sees the full capture up to the per-run limit.

## 6. Matrix

The budget is reset per cell — each matrix value gets a fresh
`2_000_000` budget. Completed cells keep their artifacts on a later
cell's error (the existing `CompareMatrixCellFailedException.PartialReport`);
the first error stops the run.

## 7. Deadline / cancellation

The cancellation token passed to `CompareCellRunner.RunAsync` / `ResultComparer`
MUST be observed in the CPU phases (pair loops, histogram building)
at bounded work intervals. Cancellation = `OperationCanceledException`.

## 8. Numbers in capabilities (T5 preview)

Capabilities advertise: `comparisonRowCapPerRun = 1000000`,
`comparisonUniqueFingerprintBudgetPerCell = 2000000`.

## 9. Upper range of inputs

`repeat` up to 100 → up to 200 measured runs and up to 10 000
baseline×candidate pairs is the ALLOWED input range, not a completion guarantee —
the budget may reject earlier (rule 4).
