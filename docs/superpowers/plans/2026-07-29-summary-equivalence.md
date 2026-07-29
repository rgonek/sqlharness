# Summary and Equivalence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add explicit technical-equivalence modes and a bounded JSON summary while preserving full JSON and artifact detail.

**Architecture:** Capture canonical row fingerprints separately from raw-footprint hashing, compare measured runs through a focused `ResultComparer`, and project complete benchmark reports through a `BenchmarkSummaryProjector`. CLI output selection becomes an enum so full JSON, compact JSON, and text cannot conflict.

**Tech Stack:** .NET 8, C# 12, SHA-256 canonical fingerprints, System.Text.Json, Spectre.Console.Cli, xUnit 2.

## Global Constraints

- `ordered` is the default result-comparison mode.
- Result values never appear in equivalence diagnostics or artifacts.
- Directional comparison retains at most 1,000,000 row fingerprints per measured variant run.
- Every measured run participates; warm-ups do not.
- `--json` remains the full report; `--json-summary` is mutually exclusive.
- Compact output contains at most ten noteworthy operators.
- Technical equivalence and domain equivalence remain distinct.
- Do not modify or commit `docs/spec-watch-snapshot.md` or `out/`.

---

### Task 1: Capture bounded canonical comparison signatures

**Files:**
- Create: `src/SqlHarness.Core/ResultEquivalence.cs`
- Modify: `src/SqlHarness.Core/CanonicalResults.cs`
- Test: `tests/SqlHarness.Tests/ResultEquivalenceTests.cs`
- Test: `tests/SqlHarness.Tests/CanonicalResultsTests.cs`

**Interfaces:**
- Produces: `public enum ResultComparisonMode { Ordered, Multiset, Set, Off }`
- Produces: `internal sealed record CanonicalComparisonResult(string SchemaHash, IReadOnlyList<string> OrderedRows)`
- Produces: `internal sealed class CanonicalComparisonAccumulator`
- Produces: `internal const int MaximumComparedRows = 1_000_000`

- [ ] **Step 1: Write failing canonical-signature tests**

Cover schema, result-set boundaries, row order, duplicate preservation, and the
limit:

```csharp
[Fact]
public void Comparison_capture_preserves_order_and_duplicates()
{
    using var capture = new CanonicalComparisonAccumulator();
    capture.BeginResultSet([new(0, "Id", "System.Int32", false)]);
    capture.AddRow([1]);
    capture.AddRow([1]);
    capture.AddRow([2]);
    capture.EndResultSet();

    var result = capture.Complete();

    Assert.Equal(3, result.OrderedRows.Count);
    Assert.Equal(result.OrderedRows[0], result.OrderedRows[1]);
    Assert.NotEqual(result.OrderedRows[1], result.OrderedRows[2]);
}
```

Add a test with the same rows under different column metadata and a test that
injects a small constructor limit and expects
`SqlHarnessSafetyException("Result comparison exceeds the 1000000-row limit.")`.

- [ ] **Step 2: Run the focused tests and confirm failure**

```powershell
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~ResultEquivalenceTests|FullyQualifiedName~CanonicalResultsTests" --no-restore
```

Expected: FAIL because the capture type does not exist.

- [ ] **Step 3: Implement canonical schema and row fingerprints**

Reuse the exact scalar preparation/serialization rules from
`CanonicalResultAccumulator`; extract the scalar writer into an internal helper
instead of creating a second representation. Hash:

- result-set ordinal plus ordered column metadata into `SchemaHash`;
- result-set ordinal plus canonical row values into one SHA-256 hex row
  fingerprint.

Store only fingerprint strings. Increment the row count before adding and fail
when it exceeds the configured limit. Do not store values or messages.

- [ ] **Step 4: Rerun the focused tests**

Run the Step 2 command.

Expected: PASS and unchanged canonical result hashes.

- [ ] **Step 5: Commit canonical comparison capture**

```powershell
git add src/SqlHarness.Core/ResultEquivalence.cs src/SqlHarness.Core/CanonicalResults.cs tests/SqlHarness.Tests/ResultEquivalenceTests.cs tests/SqlHarness.Tests/CanonicalResultsTests.cs
git commit -m "feat: capture bounded result signatures"
```

### Task 2: Implement the four equivalence modes

**Files:**
- Modify: `src/SqlHarness.Core/ResultEquivalence.cs`
- Modify: `src/SqlHarness.Core/Artifacts.cs`
- Test: `tests/SqlHarness.Tests/ResultEquivalenceTests.cs`
- Test: `tests/SqlHarness.Tests/ContractsTests.cs`

**Interfaces:**
- Produces: `public sealed record ResultEquivalenceReport(ResultComparisonMode Mode, bool? Equivalent, long? DifferingPositions, long? BaselineOnlyCount, long? CandidateOnlyCount)`
- Produces: `internal static ResultEquivalenceReport ResultComparer.Compare(ResultComparisonMode mode, IReadOnlyList<CanonicalComparisonResult> baseline, IReadOnlyList<CanonicalComparisonResult> candidate)`
- Preserves: `SqlHarnessCompareReport.ResultsEquivalent` as a nullable compatibility projection
- Produces: `SqlHarnessCompareReport.Equivalence`

- [ ] **Step 1: Write the mode truth-table tests**

Use `A,A,B` versus `B,A,A`:

```csharp
[Theory]
[InlineData(ResultComparisonMode.Ordered, false)]
[InlineData(ResultComparisonMode.Multiset, true)]
[InlineData(ResultComparisonMode.Set, true)]
public void Modes_apply_order_and_duplicate_semantics(ResultComparisonMode mode, bool expected)
{
    var report = ResultComparer.Compare(mode, [Capture("A", "A", "B")], [Capture("B", "A", "A")]);
    Assert.Equal(expected, report.Equivalent);
}
```

Also cover:

- `A,A,B` versus `A,B`: multiset false, set true;
- ordered order-only change: positive `DifferingPositions`, directional counts 0;
- multiset missing duplicate: `BaselineOnlyCount=1`;
- schema mismatch;
- multiple repetitions where only the final run changes;
- `off`: all result fields except `Mode` are null.

- [ ] **Step 2: Confirm mode tests fail**

Run:

```powershell
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~ResultEquivalenceTests|FullyQualifiedName~ContractsTests" --no-restore
```

Expected: FAIL for missing reports/comparer.

- [ ] **Step 3: Implement deterministic comparisons**

For `ordered`, compare schema hashes and sequences by index; count differing
positions plus trailing positions. Independently build frequency dictionaries
to compute directional multiset counts.

For `multiset`, compare schema hashes and fingerprint counts. For `set`, compare
schema hashes and `HashSet<string>`. Across repetitions, require every result in
both variants to equal the first baseline under the selected mode. Return the
maximum observed directional and positional counts from baseline/candidate
pairs.

- [ ] **Step 4: Replace the boolean report contract**

Preserve the existing JSON property and add the detailed contract:

```csharp
public sealed record SqlHarnessCompareReport(
    SqlHarnessTargetIdentityReport Target,
    int Repetitions,
    int MeasuredRunCount,
    bool? ResultsEquivalent,
    CompareVariantReport Baseline,
    CompareVariantReport Candidate,
    string? ArtifactDirectory)
{
    public ResultEquivalenceReport Equivalence { get; init; } =
        new(ResultComparisonMode.Ordered, ResultsEquivalent, 0, 0, 0);
}
```

Update JSON contract tests to assert camel-case `equivalence.mode`,
`equivalence.equivalent`, and nullable fields for `off`. For the default ordered
mode, assert the existing `resultsEquivalent` property still has the same
boolean value. For `off`, it is `null`.

- [ ] **Step 5: Rerun tests and commit**

Run the Step 2 command, expect PASS, then:

```powershell
git add src/SqlHarness.Core/ResultEquivalence.cs src/SqlHarness.Core/Artifacts.cs tests/SqlHarness.Tests/ResultEquivalenceTests.cs tests/SqlHarness.Tests/ContractsTests.cs
git commit -m "feat: add explicit result equivalence modes"
```

### Task 3: Wire equivalence through compare execution and CLI

**Files:**
- Modify: `src/SqlHarness.Core/Contracts.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`
- Modify: `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs`
- Modify: `src/SqlHarness.Cli/Commands/Renderer.cs`
- Modify: `tests/SqlHarness.Tests/CompareTests.cs`
- Modify: `tests/SqlHarness.Tests/Cli/CommandTests.cs`

**Interfaces:**
- Changes: `SqlHarnessCompareOperation(..., int Repeat, ResultComparisonMode CompareResults = ResultComparisonMode.Ordered)`
- Consumes: `CanonicalComparisonAccumulator`
- Consumes: `ResultComparer.Compare(...)`

- [ ] **Step 1: Add failing CLI and module tests**

Assert:

```csharp
Assert.Equal(
    ResultComparisonMode.Multiset,
    operation.CompareResults);
```

after parsing `--compare-results multiset`. Add invalid-mode coverage with exit
code 2 and no module dispatch. In `CompareTests`, create differing-order fake
results and assert ordered false, multiset true, and off null.

- [ ] **Step 2: Run focused tests**

```powershell
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CompareTests|FullyQualifiedName~CommandTests" --no-restore
```

Expected: FAIL for missing operation option and capture.

- [ ] **Step 3: Capture measured results only**

Extend `CollectedCompare` and `CollectedCompareRun` with
`CanonicalComparisonResult`. In `CollectCompareAsync`, feed result-set metadata
and normalized rows to both the existing canonical accumulator and the new
comparison accumulator. Warm-up calls may collect and discard comparison
signatures; only entries added to the measured `runs` list reach
`ResultComparer`.

Replace `allHashes.Length == 1` with:

```csharp
var equivalence = ResultComparer.Compare(
    compare.CompareResults,
    baselineRuns.Select(run => run.Comparison).ToArray(),
    candidateRuns.Select(run => run.Comparison).ToArray());
```

- [ ] **Step 4: Parse and render the mode**

Add:

```csharp
[CommandOption("--compare-results <MODE>")]
[DefaultValue("ordered")]
public string CompareResults { get; set; } = "ordered";
```

Parse with an ordinal-ignore-case closed switch before dispatch. Render text as:

```text
Technical equivalence (ordered): False; baseline-only: 0; candidate-only: 0; differing positions: 2
```

For `off`, render `Technical equivalence: off`.

- [ ] **Step 5: Run focused and complete tests**

```powershell
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CompareTests|FullyQualifiedName~CommandTests" --no-restore
dotnet test -c Release
```

Expected: PASS.

- [ ] **Step 6: Commit execution wiring**

```powershell
git add src/SqlHarness.Core/Contracts.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs src/SqlHarness.Cli/Commands/Renderer.cs tests/SqlHarness.Tests/CompareTests.cs tests/SqlHarness.Tests/Cli/CommandTests.cs
git commit -m "feat: expose compare result modes"
```

### Task 4: Add metrics needed by compact summaries

**Files:**
- Modify: `src/SqlHarness.Core/Artifacts.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`
- Test: `tests/SqlHarness.Tests/CompareTests.cs`
- Test: `tests/SqlHarness.Tests/MeasureTests.cs`

**Interfaces:**
- Produces: `CompareVariantReport.LogicalReadsByTable`
- Produces: `public sealed record BenchmarkClassificationReport(string Setup, string Query)` for measure
- Produces: `public sealed record CompareClassificationReport(string Setup, string Baseline, string Candidate)` for compare
- Produces: `public sealed record BenchmarkParameterReport(string Name, string Type, int? Size, byte? Precision, byte? Scale)`
- Produces: `SqlHarnessMeasureReport.Parameters` and `SqlHarnessCompareReport.Parameters`

- [ ] **Step 1: Write failing distribution and classification tests**

Use three runs with table reads `1,5,9` and assert:

```csharp
Assert.Equal(
    new CompareDistribution(1, 5, 9),
    report.Baseline.LogicalReadsByTable["dbo.Orders"]);
```

Assert compare classification is `session-local` for a `#temp` setup and
`read-only` for baseline/candidate. Assert measure uses the corresponding two
labels. Assert parameter metadata includes `@AsOfDate` and `datetime2` but does
not contain its value.

- [ ] **Step 2: Run benchmark tests and confirm failure**

```powershell
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CompareTests|FullyQualifiedName~MeasureTests" --no-restore
```

- [ ] **Step 3: Preserve totals and add distributions**

Keep `TotalLogicalReadsByTable` for full-JSON compatibility. Add an init property:

```csharp
public IReadOnlyDictionary<string, CompareDistribution> LogicalReadsByTable { get; init; }
    = new Dictionary<string, CompareDistribution>();
```

Group table names across runs, treating a missing table in a run as zero, and
compute min/median/max for every observed table.

Add classification records as init properties on benchmark reports so existing
constructor call sites remain source-compatible. Add `Parameters` as another
init property. Populate both from safety decisions and parsed parameters without
SQL text or parameter values.

- [ ] **Step 4: Rerun tests and commit**

Run the Step 2 command, expect PASS, then:

```powershell
git add src/SqlHarness.Core/Artifacts.cs src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/CompareTests.cs tests/SqlHarness.Tests/MeasureTests.cs
git commit -m "feat: expose compact benchmark metrics"
```

### Task 5: Project and render bounded JSON summaries

**Files:**
- Create: `src/SqlHarness.Core/BenchmarkSummary.cs`
- Modify: `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs`
- Modify: `src/SqlHarness.Cli/Commands/Renderer.cs`
- Test: `tests/SqlHarness.Tests/BenchmarkSummaryTests.cs`
- Test: `tests/SqlHarness.Tests/Cli/CommandTests.cs`
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `skills/sqlharness/SKILL.md`

**Interfaces:**
- Produces in CLI: `internal enum OutputMode { Text, Json, JsonSummary }`
- Produces: `public static class BenchmarkSummaryProjector`
- Produces: `BenchmarkSummaryProjector.Project(SqlHarnessCompareReport report)`
- Produces: `BenchmarkSummaryProjector.Project(SqlHarnessMeasureReport report)`
- Changes: `Renderer.Render(SqlHarnessOutcome outcome, OutputMode mode, OutputCaptureWriter output)`

- [ ] **Step 1: Write failing projector bounds tests**

Build reports containing 30 operators, full warnings, and table distributions.
Assert the projection contains target, classification, equivalence,
CPU/elapsed/reads distributions, table reads, warnings, and artifact directory.
Assert:

```csharp
Assert.True(summary.NoteworthyOperators.Count <= 10);
Assert.DoesNotContain("PlanXmls", json);
Assert.DoesNotContain("ResultHash", json);
Assert.DoesNotContain("runs", json, StringComparison.OrdinalIgnoreCase);
```

Operators qualify when present on only one side or when
`HasWarnings || HasSpill || HasImplicitConversion`; order them by warning first,
then physical operation and object using ordinal comparison.

- [ ] **Step 2: Add failing CLI mutual-exclusion tests**

Invoke compare with both flags and assert exit 2, no module operation, and:

```text
Choose only one of --json or --json-summary.
```

Assert `--json-summary` serializes the projected object while `--json` still
serializes the full report including complete operators.

- [ ] **Step 3: Run focused tests**

```powershell
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~BenchmarkSummaryTests|FullyQualifiedName~CommandTests" --no-restore
```

Expected: FAIL for missing projection/output mode.

- [ ] **Step 4: Implement projection and output selection**

Add `JsonSummary` to benchmark settings, validate mutual exclusion before file
reads and module dispatch, and pass an `OutputMode` to `Dispatch`.

In `Renderer`, project only successful measure/compare reports in summary mode:

```csharp
var rendered = outcome.Report switch
{
    SqlHarnessCompareReport compare => BenchmarkSummaryProjector.Project(compare),
    SqlHarnessMeasureReport measure => BenchmarkSummaryProjector.Project(measure),
    _ => outcome.Report,
};
```

Errors retain the existing bounded error envelope.

- [ ] **Step 5: Document summary and domain verification**

Add exact examples for `--compare-results multiset` and `--json-summary`.
Document:

- `ordered` default and all four modes;
- meaning of directional counts;
- order-only mismatch detection;
- one-million-row comparison bound;
- technical equivalence versus ticket-specific domain equivalence;
- two-direction `EXCEPT` for set semantics and grouped counts when duplicates
  matter;
- ordering ties and missing-history checks.

- [ ] **Step 6: Run the stage gate**

```powershell
dotnet test -c Release
dotnet build -c Release --no-restore
git diff --check
```

Expected: PASS, successful build, clean diff check.

- [ ] **Step 7: Commit summary and documentation**

```powershell
git add src/SqlHarness.Core/BenchmarkSummary.cs src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs src/SqlHarness.Cli/Commands/Renderer.cs tests/SqlHarness.Tests/BenchmarkSummaryTests.cs tests/SqlHarness.Tests/Cli/CommandTests.cs README.md AGENTS.md skills/sqlharness/SKILL.md
git commit -m "feat: add compact benchmark JSON"
```
