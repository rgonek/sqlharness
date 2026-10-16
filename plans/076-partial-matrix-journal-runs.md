# Plan 076: Journal completed cells of a partial matrix comparison Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans or, when the operator selects delegation, superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a matrix `compare` stops on a failing cell, the journal records benchmark metrics for every cell that completed, just as it does for a fully successful matrix.

**Architecture:** `CompareMatrixRunner` already accumulates completed runs in a local list; pass that list into `CompareMatrixCellFailedException` as `CompletedRuns`. `SqlHarnessModule.ExecuteCompareMatrixAsync` sets `BenchmarkRuns` on both partial outcomes. `JournalingModule` already records any outcome that carries runs, so no journal change is needed.

**Tech Stack:** .NET 10, C#, xUnit, SQLite journal (Microsoft.Data.Sqlite).

**Spec:** [Observation: partial matrix comparison leaves no journal metrics](../docs/observations/partial-compare-journal-coverage.md). The operator accepted this plan during the 2026-10-15 retro. No separate spec is needed for this bounded change.

## Status

- DONE on branch `plan-076` at `57a1e7d` (2026-10-16). Not integrated into `main`. No push, installation, or publication.
- Tests: `Partial_matrix_after_sql_failure_exposes_runs_of_completed_cells_only`, `Partial_matrix_after_artifact_failure_exposes_runs_of_completed_cells`, `Matrix_failing_in_the_first_cell_exposes_no_runs`, and `Failed_exit_outcome_with_completed_runs_is_journaled`.
- Windows `just verify` exited 0 on the working tree this commit records (parent `1d9dbfe` plus `tests/SqlHarness.Tests/Journal/JournalingBenchmarkTests.cs`). Literal line: `verify: OK  stages=6  3m47s`. Stages: ui-install ok 27,8s; ui-check ok 31,1s (238 tests, 45 files); restore ok 7,7s; build ok 15,5s (0 warnings, 0 errors); test ok 1m35s (SqlHarness.Tests 3,843 passed/11 skipped; SqlHarness.Mcp.Tests 386 passed/4 skipped); format ok 49,9s (1 changed C# file). Linux was not run.

## Global Constraints

- Exit codes, `status: partial`, error text, the partial report and artifact retention stay exactly as today.
- Only runs of **completed** cells are journaled; the failing cell contributes no metrics.
- `MatrixCell` indexes on journaled runs equal the completed cells' indexes.
- Matrix parameter values never reach the journal (existing `Matrix_values_never_reach_the_database` guarantee).
- No journal schema migration, MCP contract change, new dependency, or change to `measure`.
- Do not auto-format unrelated code; run `just fix` only for files this plan changes.

## Review Focus

1. Failure in the **first** cell: no completed runs; the outcome has an empty `BenchmarkRuns` and the journal stays empty, as today (Task 1).
2. Artifact-phase failure (the observed case) and SQL-phase failure both journal completed cells (Task 1).
3. The `WriteMatrixIndex` failure branch (`LocalStorage` outcome at `SqlHarnessModule.cs:525`) also carries runs (Task 1).
4. A partial outcome with runs must not change the emission receipt or exit code seen by MCP (Task 1, existing assertions kept).
5. Journal recording of a failed-exit outcome with runs: metrics written, operation status still failed (Task 2).

## Evidence / Current state

- `src/SqlHarness.Core/CompareMatrixRunner.cs:93` appends each completed cell's runs to `runs`. On
  `CompareCellFailedException` (`:95-107`) it throws `CompareMatrixCellFailedException` with
  `reports` but not `runs`.
- `src/SqlHarness.Core/CompareMatrixRunner.cs:20-36`: the exception exposes `PartialReport`, with no runs.
- `src/SqlHarness.Core/SqlHarnessModule.cs:515-540`: both partial outcomes are created without `BenchmarkRuns`.
- `src/SqlHarness.Core/Journal/JournalingModule.cs:104-105`: metrics are recorded only when
  `outcome.BenchmarkRuns is { Count: > 0 }`.
- Observed: a partial matrix comparison with two completed cells left zero `operation_metrics`,
  `operation_statements` and `operation_table_io` rows. Successful comparisons in the same session had full rows.

## Change

`CompareMatrixCellFailedException` gains `IReadOnlyList<CompareRunArtifact> CompletedRuns`; the runner passes a copy of `runs`; both partial outcomes set `BenchmarkRuns = failed.CompletedRuns`.

## Acceptance

- New tests in `CompareMatrixTests` and `JournalingBenchmarkTests` pass; existing partial-matrix tests pass unchanged.
- `just verify` is green.

## STOP conditions

- The drift check shows the partial path was refactored: stop and update this plan.
- Journaling a failed-exit outcome breaks an existing journal contract test (for example one that asserts failed operations have no metrics). Stop and ask the operator whether that contract is intended.

## File map

| File | Responsibility |
|---|---|
| `src/SqlHarness.Core/CompareMatrixRunner.cs` | Carry completed runs in the failure exception |
| `src/SqlHarness.Core/SqlHarnessModule.cs` | Set `BenchmarkRuns` on both partial outcomes |
| `tests/SqlHarness.Tests/CompareMatrixTests.cs` | Partial outcome exposes completed runs (SQL and artifact failure, first-cell failure) |
| `tests/SqlHarness.Tests/Journal/JournalingBenchmarkTests.cs` | Failed-exit outcome with runs is journaled |
| `plans/README.md`, `plans/history.md`, this plan | Queue and final evidence |

---

### Task 1: Partial matrix outcome carries completed runs

**Files:**
- Modify: `src/SqlHarness.Core/CompareMatrixRunner.cs:11-36` (exception) and `:95-107` (throw site)
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs:515-540`
- Test: `tests/SqlHarness.Tests/CompareMatrixTests.cs`

**Interfaces:**
- Produces: `CompareMatrixCellFailedException.CompletedRuns : IReadOnlyList<CompareRunArtifact>` (never null; empty when no cell completed).

- [ ] **Step 1: Add an artifact-failure switch to the test writer**

In `CompareMatrixTests.DirectoryArtifactWriter` add a constructor parameter and throw on the Nth write:

```csharp
private readonly int? _failWriteAt;

public DirectoryArtifactWriter(int? failWriteAt = null)
{
    _failWriteAt = failWriteAt;
    Directory.CreateDirectory(Root);
}

public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target)
{
    if (_failWriteAt == Directories.Count)
        throw new UnauthorizedAccessException("Access to the staging path is denied.");
    var directory = Path.Combine(Root, "cell-" + Directories.Count.ToString(CultureInfo.InvariantCulture));
    Directory.CreateDirectory(directory);
    Directories.Add(directory);
    return directory;
}
```

Remove the old parameterless constructor (`public DirectoryArtifactWriter() => Directory.CreateDirectory(Root);`).

- [ ] **Step 2: Write the failing tests**

```csharp
[Fact]
public async Task Partial_matrix_after_sql_failure_exposes_runs_of_completed_cells_only()
{
    using var artifacts = new DirectoryArtifactWriter();
    var factory = new MatrixSessionFactory(failSqlAt: 2);

    var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix("BatchSize:int=1,20,100"));

    Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
    var runs = Assert.IsAssignableFrom<IReadOnlyList<CompareRunArtifact>>(outcome.BenchmarkRuns);
    Assert.Equal([0, 1], runs.Select(run => run.MatrixCell!.Value).Distinct().Order());
    Assert.All(runs, run => Assert.Contains(run.Variant, new[] { "baseline", "candidate" }));
}

[Fact]
public async Task Partial_matrix_after_artifact_failure_exposes_runs_of_completed_cells()
{
    using var artifacts = new DirectoryArtifactWriter(failWriteAt: 2);

    var outcome = await Module(new MatrixSessionFactory(), artifacts).ExecuteAsync(Matrix("BatchSize:int=1,20,100"));

    Assert.Equal(SqlHarnessExitCode.LocalStorage, outcome.ExitCode);
    var partialReport = Assert.IsType<SqlHarnessCompareMatrixReport>(outcome.Report);
    Assert.Equal([0, 1], partialReport.Cells.Select(cell => cell.Index));
    Assert.Equal([0, 1], outcome.BenchmarkRuns!.Select(run => run.MatrixCell!.Value).Distinct().Order());
}

[Fact]
public async Task Matrix_failing_in_the_first_cell_exposes_no_runs()
{
    using var artifacts = new DirectoryArtifactWriter();
    var factory = new MatrixSessionFactory(failSqlAt: 0);

    var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix("BatchSize:int=1,20"));

    Assert.Null(outcome.Report);
    Assert.True(outcome.BenchmarkRuns is null || outcome.BenchmarkRuns.Count == 0);
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CompareMatrixTests.Partial_matrix"`
Expected: both `Partial_matrix_*` tests FAIL (`BenchmarkRuns` is null). The first-cell test passes already; it guards against a regression.

- [ ] **Step 4: Carry completed runs in the exception**

In `CompareMatrixRunner.cs`, extend the exception:

```csharp
internal IReadOnlyList<CompareRunArtifact> CompletedRuns { get; }

internal CompareMatrixCellFailedException(int index, string? parameterName, string? parameterType, string setName, CompareCellFailedException inner, IReadOnlyList<CompareMatrixCellReport> completedCells, IReadOnlyList<CompareRunArtifact> completedRuns, OutputFootprint rawFootprint)
    : base(null, Unwrap(inner))
{
    // existing assignments unchanged
    CompletedRuns = completedRuns.ToArray();
}
```

At the throw site pass `runs` after `reports`:

```csharp
throw new CompareMatrixCellFailedException(
    index,
    run.ParameterName,
    run.ParameterType,
    set.Name,
    failed,
    reports,
    runs,
    new OutputFootprint(bytes, lines));
```

- [ ] **Step 5: Set `BenchmarkRuns` on both partial outcomes**

In `SqlHarnessModule.cs` (`:524-526` and `:538-540`):

```csharp
return WithReceipt(
    new SqlHarnessOutcome(SqlHarnessExitCode.LocalStorage, partialReport, SecretRedactor.Redact(exception, knownSecrets))
    {
        BenchmarkRuns = failed.CompletedRuns,
    },
    rawFootprint);
```

```csharp
return WithReceipt(
    new SqlHarnessOutcome(exitCode, failed.PartialReport, FormatMatrixCellError(failed, knownSecrets))
    {
        BenchmarkRuns = failed.CompletedRuns,
    },
    rawFootprint);
```

- [ ] **Step 6: Run the matrix tests**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CompareMatrixTests"`
Expected: all PASS, including the existing `Matrix_failure_keeps_partial_cells_and_never_starts_later_values` and `Matrix_stops_on_sql_failure_and_keeps_the_earlier_artifact_directory`.

- [ ] **Step 7: Commit**

```bash
git add src/SqlHarness.Core/CompareMatrixRunner.cs src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/CompareMatrixTests.cs
git commit -m "Keep completed matrix runs on partial compare outcomes"
```

### Task 2: Journal records a partial outcome's completed runs

**Files:**
- Test: `tests/SqlHarness.Tests/Journal/JournalingBenchmarkTests.cs`
- Modify (only if the test fails): `src/SqlHarness.Core/Journal/JournalingModule.cs:103-105`

**Interfaces:**
- Consumes: `SqlHarnessOutcome.BenchmarkRuns` populated on partial outcomes (Task 1).

- [ ] **Step 1: Write the test**

```csharp
[Fact]
public async Task Failed_exit_outcome_with_completed_runs_is_journaled()
{
    var outcome = BenchmarkOutcome(matrixCell: 0) with { ExitCode = SqlHarnessExitCode.LocalStorage };

    using var temp = await RunAsync(outcome, storeSensitive: false);

    Assert.Equal(0L, JournalDb.Rows(temp.DatabasePath, "SELECT matrix_cell FROM operation_metrics").Single()["matrix_cell"]);
    Assert.NotEqual("succeeded", JournalDb.Rows(temp.DatabasePath, "SELECT status FROM operations").Single()["status"]);
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~JournalingBenchmarkTests"`
Expected: PASS without source changes (`JournalingModule.cs:104` does not check the exit code). If it fails because the exit code blocks recording, apply the STOP condition before changing `JournalingModule`.

- [ ] **Step 3: Full gate**

Run: `just fix` then `just verify`
Expected: green.

- [ ] **Step 4: Commit and record evidence**

```bash
git add tests/SqlHarness.Tests/Journal/JournalingBenchmarkTests.cs
git commit -m "Test journaling of partial compare runs"
```

Set this plan's status to DONE with the test names and the verify result. Move the queue entry from `plans/README.md` to `plans/history.md`. Add a dated "Resolved by plan 076" follow-up to `docs/observations/partial-compare-journal-coverage.md`.
