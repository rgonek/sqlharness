# Plan 066: Elapsed spread resolution floor and documented cost-diagnostic thresholds

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** `git diff --stat ed44649..HEAD -- src/SqlHarness.Core/RegressionPolicy.cs tests/SqlHarness.Tests/RegressionPolicyTests.cs tests/SqlHarness.Tests/CompareRegressionOutputTests.cs tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs README.md docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md`. Adapt compatible drift; changed files alone are not a STOP condition.

**Goal:** A compare of statements whose elapsed samples differ only by whole-millisecond clock resolution gets a pass/fail verdict instead of a structural `inconclusive`, and the fixed reads/CPU diagnostic thresholds are documented where callers read the verdict contract.

**Architecture:** One absolute tolerance is added to the existing spread gate in `RegressionPolicy.IsUnstable`: a variant whose elapsed range is at most 2 ms is never unstable. The relative 0.25 rule is unchanged above that floor. The policy version moves to 2 because the same samples can now produce a different verdict. Reads/CPU logic does not change; README states their thresholds and the meaning of `not-regressed`.

**Tech Stack:** .NET 10, C#, xUnit.

**Spec:** [Benchmark regression policy](../docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md) as implemented by [plan 051](051-compare-regression-verdict.md) and documented in `README.md` ("Regression verdict"). Accepted follow-up 2026-10-14 is recorded in that spec.

## Status

- DONE 2026-10-14; merged locally to main from `feat/plan-066-regression-floor` (`36f2213` T1 floor + v2, `e85e8c5` T2 docs, `b1867da` final-review fixes, `aba5e34` T4 dashboard label shows `(policy vN)`, operator-requested). Windows `verify.ps1` on `aba5e34`: `verify: OK` (UI 163, Core 3494 passed/11 skipped, MCP 369 passed/4 skipped). Linux gate waived by the operator: the run on `aba5e34` was invalidated when another session reset the shared WSL clone to `653a222` mid-run (7 failures, all in files outside this plan). No push, no live DB.
- Planning base: `ed44649`, 2026-10-14.
- Priority: P2. Effort: S. Risk: LOW (pure policy function; one constant and one condition; documentation).
- Owner: this plan. 051 is DONE and is not reopened.
- Coordinate: plan 064 edits MCP/CLI descriptions; this plan does not touch descriptions.

## Evidence

A System1 optimization session (2026-10-14) compared two candidate pairs over a five-value matrix with seven repetitions each. The statements took a few milliseconds; samples are whole milliseconds (`elapsedWholeMillisecondsAreExact: true`). Outcome counts only (no workload values published):

| Observation | Count |
|---|---:|
| Matrix cells evaluated | 10 |
| Verdict `inconclusive` with only `unstable-elapsed` | 9 |
| Verdict `pass` | 1 |
| Inconclusive cells whose every variant range was at most 2 ms | 9 |

A range of one or two clock ticks exceeds 25 % of a median of a few milliseconds, so `IsUnstable` (`src/SqlHarness.Core/RegressionPolicy.cs:202-207`) rejects every such variant. The fail rule needs at least +5 ms (`ElapsedMinimumDeltaMilliseconds`, `:54`), so this sub-resolution noise cannot by itself decide a fail, but it blocks every pass. The agent had to explain each inconclusive cell manually.

In the same session a candidate's logical-read median rose by well over 100 % but stayed under the absolute +100 floor; its diagnostic said `not-regressed`. README (`:256`) documents the elapsed thresholds but not the reads (+10 % and +100) or CPU (+15 % and +5 ms) thresholds (`RegressionPolicy.cs:59-63`), so the label read as "no cost change". Behavior matches the code; the contract text is incomplete.

## Design decisions

1. **Absolute floor, not a new reason.** `IsUnstable` returns false when `max - min <= ElapsedSpreadFloorMilliseconds` (2 ms), otherwise the existing rule applies (relative 0.25, or any positive range at a zero median). Rejected alternative: keep `inconclusive` with a new `elapsed-below-resolution` reason — it still never decides a sub-resolution comparison.
2. **2 ms.** Two whole-ms ticks: covers one-tick jitter on each side of the true value. It stays below the 5 ms fail delta, so a floor-stable pair still needs a real +5 ms median shift to fail. Larger ranges at small medians stay unstable (a 0/0/0/0/4 sample remains `inconclusive`).
3. **Policy version 2.** The same samples can now yield `pass` instead of `inconclusive`. Artifacts keep the version they were written with; there is no re-evaluation of stored artifacts.
4. **Reads/CPU unchanged.** Statuses keep their values; only the documentation states both gates. No new response fields (bytes per matrix cell) and no status enum change (dashboard consumers).

## Global Constraints

- Thresholds stay fixed constants: no flags, no configuration file (README "Thresholds are fixed").
- `RegressionPolicy.Evaluate` stays pure: no I/O, clock or logging.
- Reason order and gate order are unchanged.
- Public repository boundary: tests use synthetic samples only.

## Review Focus

1. Zero median with a range at or below the floor (`[0,0,0,0,1]`) — now stable; a zero-baseline candidate above 5 ms must still fail.
2. Range exactly at the floor (2 ms) is stable; 3 ms at a 4 ms median is unstable.
3. Large medians: the floor never makes a relatively unstable large-median variant stable unless its range is ≤ 2 ms (then the relative spread is already tiny).
4. Elapsed unavailable still suppresses the spread gate.
5. Golden outputs that print `policyVersion` (`CompareRegressionOutputTests`, `McpTokenBudgetTests`) reference the constant, not a literal; verify no literal `1` remains in docs or fixtures.

---

### Task 1: Spread resolution floor and policy version 2

**Files:**
- Modify: `src/SqlHarness.Core/RegressionPolicy.cs:46-54` (constants), `:202-207` (`IsUnstable`), class summary `:38-43`
- Test: `tests/SqlHarness.Tests/RegressionPolicyTests.cs`

**Interfaces:**
- Produces: `public const decimal RegressionPolicy.ElapsedSpreadFloorMilliseconds = 2m;` `RegressionPolicy.PolicyVersion == 2`.

- [ ] **Step 1: Write the failing tests** (add next to `Spread_above_a_quarter_is_unstable`, using the file's existing `Run`, `Variant`, `Flat` helpers)

```csharp
    [Fact]
    public void Range_within_the_resolution_floor_is_stable_at_small_medians()
    {
        // median 1, range 1 -> relative spread 1.0, but within 2 ms
        var report = Run(Variant([1m, 1m, 2m, 1m, 1m, 1m, 1m]), Variant([1m, 1m, 1m, 2m, 1m, 1m, 1m]));

        Assert.Equal(RegressionPolicy.Pass, report.Verdict);
        Assert.Empty(report.Reasons);
    }

    [Fact]
    public void Range_exactly_at_the_floor_is_stable()
    {
        var report = Run(Variant([1m, 1m, 3m, 1m, 1m]), Variant(Flat(1m)));

        Assert.Equal(RegressionPolicy.Pass, report.Verdict);
    }

    [Fact]
    public void Range_above_the_floor_and_above_a_quarter_is_unstable()
    {
        // median 4, range 3 -> 0.75 and above 2 ms
        var report = Run(Variant([4m, 4m, 7m, 4m, 4m]), Variant(Flat(4m)));

        Assert.Equal(RegressionPolicy.Inconclusive, report.Verdict);
        Assert.Equal([RegressionPolicy.ReasonUnstableElapsed], report.Reasons);
    }

    [Fact]
    public void Zero_median_within_the_floor_is_stable()
    {
        var report = Run(Variant([0m, 0m, 0m, 0m, 1m]), Variant(Flat(0m)));

        Assert.Equal(RegressionPolicy.Pass, report.Verdict);
    }

    [Fact]
    public void Floor_stable_variants_still_fail_on_a_real_regression()
    {
        // both ranges 1 ms; candidate median +9 ms and x10
        var report = Run(Variant([1m, 1m, 2m, 1m, 1m]), Variant([10m, 10m, 11m, 10m, 10m]));

        Assert.Equal(RegressionPolicy.Fail, report.Verdict);
        Assert.Equal([RegressionPolicy.ReasonElapsedRegressed], report.Reasons);
    }

    [Fact]
    public void Policy_version_is_two()
    {
        Assert.Equal(2, RegressionPolicy.PolicyVersion);
    }
```

Keep `Zero_median_with_a_positive_maximum_is_unstable` (`[0,0,0,0,4]`, range 4 > floor) unchanged; it must still pass.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~RegressionPolicyTests"`
Expected: the five new spread/version tests FAIL (`Inconclusive` instead of `Pass`; version 1); existing tests pass.

- [ ] **Step 3: Implement**

In `RegressionPolicy.cs` change `PolicyVersion` to `2` and add after `MaxElapsedSpread`:

```csharp
    /// <summary>
    /// An elapsed range (max - min) at or below this many ms never makes a variant unstable:
    /// whole-millisecond samples jitter by a tick or two regardless of the median.
    /// </summary>
    public const decimal ElapsedSpreadFloorMilliseconds = 2m;
```

Replace `IsUnstable`:

```csharp
    private static bool IsUnstable(IReadOnlyList<decimal> samples)
    {
        var median = Median(samples);
        var range = samples.Max() - samples.Min();
        if (range <= ElapsedSpreadFloorMilliseconds)
            return false;
        return median == 0m || range / median > MaxElapsedSpread;
    }
```

(`median == 0m` with `range > floor` is unstable, as before for any positive range.) Update the class summary sentence about the spread gate to mention the 2 ms floor.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~RegressionPolicyTests|FullyQualifiedName~CompareRegressionOutputTests"` and `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~McpTokenBudgetTests"`
Expected: PASS. If a golden JSON fixture contains a literal `"policyVersion":1`, update it to `2` (search: `rg -n "policyVersion\"?: ?1" tests`).

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Core/RegressionPolicy.cs tests/SqlHarness.Tests/RegressionPolicyTests.cs
git commit -m "Regression policy v2: 2 ms elapsed spread floor"
```

### Task 2: Contract documentation

**Files:**
- Modify: `README.md:256` (Regression verdict paragraph)
- Modify: `docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md` (section 3 note already added at planning; mark it implemented)

- [ ] **Step 1: README edits** in the "Regression verdict" paragraph:
  - `policyVersion: 1` → `policyVersion: 2`.
  - Replace "and the elapsed spread ((max - min) / median) is at most 0.25 for both" with "and each variant's elapsed spread is acceptable: a range (max - min) of at most 2 ms, or otherwise (max - min) / median at most 0.25 (a zero median with a range above 2 ms is unstable)".
  - After "Reads and CPU are cost diagnostics and never change the verdict;" insert: "a cost diagnostic is `regressed` only when the candidate median is at least 1.10 times the baseline and at least 100 logical reads higher (reads), or at least 1.15 times and at least 5 ms higher (CPU); `not-regressed` means one of those two gates did not fire, so read `delta` and `deltaPercent` for the size of the change;".
  - After "Artifacts written before this version have no `regression`." append: "Artifacts keep the `policyVersion` they were written with; version 1 had no 2 ms spread floor."
- [ ] **Step 2: Verify** `rg -n "policyVersion" README.md docs skills` shows no remaining `1` for the current version except the historical sentence.
- [ ] **Step 3: Spec** — change the 2026-10-14 follow-up note status from "accepted" to "implemented in <commit>".
- [ ] **Step 4: Commit**

```bash
git add README.md docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md
git commit -m "Document regression v2 spread floor and cost thresholds"
```

### Task 3: Gates and plan status

- [ ] Run the repository's full Windows and Linux gates (`just verify` or the commands in AGENTS.md). Expected: green; record counts.
- [ ] Update this plan's Status and its row in `plans/README.md` to DONE with commits and gate results.

## Acceptance

- Synthetic sub-resolution samples yield `pass`; a ≥ +5 ms, ≥ ×1.10 shift with floor-stable samples yields `fail`; ranges above 2 ms keep the 0.25 rule.
- `policyVersion` is 2 in new compare output; stored artifacts are not rewritten.
- README states reads/CPU thresholds and the meaning of `not-regressed`.
- No change to response fields, status values, reason codes or their order.

## STOP conditions

- Drift changed `IsUnstable` semantics or reason ordering: stop and re-plan.
- A consumer (dashboard, journal summary) compares `policyVersion` to a literal 1: stop and record the consumer before bumping.
