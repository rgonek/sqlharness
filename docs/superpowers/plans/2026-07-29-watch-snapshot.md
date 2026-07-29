# Watch and Snapshot Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add bounded read-only `watch` polling and named `snapshot` comparison commands that reduce repetitive agent output while preserving SQLHarness target, safety, redaction, and gain-accounting contracts.

**Architecture:** Keep Spectre CLI parsing in two focused command files and add public operation/report contracts in Core. Extract reusable query-result collection from `SqlHarnessModule`; implement polling, predicate evaluation, snapshot persistence, and snapshot diffing as isolated Core components injected into the module so time and disk behavior are deterministic in tests.

**Tech Stack:** .NET 10, C# 14, Spectre.Console.Cli, Microsoft.Data.SqlClient, xUnit, System.Text.Json

## Global Constraints

- Default remains **read-only**.
- `watch` and `snapshot` accept user SQL through the same pipeline as `query` (classification, bounds, params); mutation classification rejects with exit `2`.
- Use parameters instead of SQL interpolation.
- Prefer `--json` in agent skills and document PowerShell `--file` examples.
- A `watch` condition reads the first row of the first result set and supports `=`, `!=`, `<`, `<=`, `>`, and `>=`; compare numerically only when both operands parse as invariant-culture numbers.
- Exactly one of `--until` and `--until-unchanged` may be supplied; when neither is supplied, use `--until-unchanged 3`.
- `watch` returns `0` for `condition-met` or `unchanged` and `7` for `max-duration`.
- `snapshot --diff` returns `0` for identical results and `8` for differences.
- Snapshot files are locally sensitive and live under `~/.sqlharness/snapshots`.
- Existing exit codes retain their meanings: `0/2/3/4/5/6` for success, validation/safety, authentication, target mismatch, SQL execution, and local storage.
- Secrets, SQL text, parameter values, passwords, and tokens must not appear in errors, snapshot metadata, filenames, or gain records.

---

### Task 1: Define operations, reports, exit codes, and CLI parsing

**Files:**
- Modify: `src/SqlHarness.Core/Contracts.cs`
- Modify: `src/SqlHarness.Cli/SqlHarnessCli.cs`
- Create: `src/SqlHarness.Cli/Commands/WatchCommand.cs`
- Create: `src/SqlHarness.Cli/Commands/SnapshotCommand.cs`
- Modify: `tests/SqlHarness.Tests/Cli/CommandTests.cs`

**Interfaces:**
- Consumes: existing `TargetSettings.TryTarget`, `CliInput`, and `SqlHarnessCommand<TSettings>.Dispatch`.
- Produces: `SqlHarnessWatchOperation`, `SqlHarnessSnapshotOperation`, `WatchExitReason`, `SqlHarnessWatchReport`, `SnapshotVerdict`, `SqlHarnessSnapshotReport`, and exit codes `WatchMaxDuration = 7`, `SnapshotDifferences = 8`.

- [ ] **Step 1: Write failing parser and contract tests**

Add tests that dispatch both commands and assert the exact operation fields:

```csharp
[Fact]
public async Task Watch_dispatches_validated_operation_from_file()
{
    var sql = TempFile("select 1 as Value");
    try
    {
        var module = new FakeModule(Success(WatchReport()));
        var app = SqlHarnessCli.Create(module, new StringWriter());

        var exit = await app.RunAsync([
            "watch", "dev", "--file", sql, "--param", "Target:int=3",
            "--interval", "10", "--max-duration", "5m", "--until", "Value >= 3", "--json"
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessWatchOperation>(Assert.Single(module.Operations));
        Assert.Equal("select 1 as Value", operation.Sql);
        Assert.Equal(TimeSpan.FromSeconds(10), operation.Interval);
        Assert.Equal(TimeSpan.FromMinutes(5), operation.MaxDuration);
        Assert.Equal("Value >= 3", operation.Until);
        Assert.Null(operation.UntilUnchanged);
    }
    finally { File.Delete(sql); }
}

[Theory]
[InlineData("--interval", "0")]
[InlineData("--max-duration", "0s")]
[InlineData("--until-unchanged", "0")]
public async Task Watch_rejects_invalid_bounds_before_dispatch(string option, string value)
{
    var sql = TempFile("select 1");
    try
    {
        var module = new FakeModule(Success(WatchReport()));
        var exit = await SqlHarnessCli.Create(module, new StringWriter())
            .RunAsync(["watch", "dev", "--file", sql, option, value]);
        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
    }
    finally { File.Delete(sql); }
}

[Fact]
public async Task Snapshot_rejects_force_with_diff_before_dispatch()
{
    var sql = TempFile("select 1");
    try
    {
        var module = new FakeModule(Success(SnapshotReport()));
        var exit = await SqlHarnessCli.Create(module, new StringWriter())
            .RunAsync(["snapshot", "dev", "--file", sql, "--name", "before", "--diff", "--force"]);
        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
    }
    finally { File.Delete(sql); }
}
```

Also test redirected stdin, explicit `--file` precedence over redirected stdin, rejection when no SQL source exists, mutual exclusion of `--until`/`--until-unchanged`, duration values without a supported suffix, missing/unsafe snapshot names, and exact mapping of exit codes 7 and 8.

- [ ] **Step 2: Run the CLI tests to verify they fail**

Run:

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~CommandTests
```

Expected: FAIL because the operation/report types and commands do not exist.

- [ ] **Step 3: Add exact public contracts and CLI adapters**

Add these contracts to `Contracts.cs`:

```csharp
public sealed record SqlHarnessWatchOperation(
    SqlTargetRequest Target, string Sql, IReadOnlyList<string> Parameters,
    int TimeoutSeconds, int MaxRows, TimeSpan Interval, TimeSpan MaxDuration,
    string? Until, int? UntilUnchanged) : SqlHarnessOperation;

public sealed record SqlHarnessSnapshotOperation(
    SqlTargetRequest Target, string Sql, IReadOnlyList<string> Parameters,
    int TimeoutSeconds, int MaxRows, string Name, bool Diff, bool Force) : SqlHarnessOperation;

public enum WatchExitReason { ConditionMet, Unchanged, MaxDuration }
public enum SnapshotVerdict { Stored, Identical, Different }

public sealed record SqlHarnessWatchPoll(
    int Poll, long ElapsedMilliseconds, string ResultHash,
    IReadOnlyList<SqlHarnessResultSetReport> ResultSets);

public sealed record SqlHarnessWatchReport(
    SqlHarnessTargetIdentityReport Target, int PollCount, long ElapsedMilliseconds,
    WatchExitReason ExitReason, IReadOnlyList<SqlHarnessWatchPoll> EmittedPolls);

public sealed record SqlHarnessSnapshotDifference(
    int ResultSet, long? Row, int? Column, string Kind);

public sealed record SqlHarnessSnapshotReport(
    SqlHarnessTargetIdentityReport Target, string Name, SnapshotVerdict Verdict,
    int DifferenceCount, IReadOnlyList<SqlHarnessSnapshotDifference> Differences);
```

Extend `SqlHarnessExitCode` with `WatchMaxDuration = 7` and
`SnapshotDifferences = 8`. Register `WatchCommand` and `SnapshotCommand` in
`SqlHarnessCli.Create`.

Both commands accept exactly one SQL source through `--file` or redirected
stdin, preferring an explicit file. Use these bounds:

```csharp
const int DefaultTimeoutSeconds = 30;
const int DefaultMaxRows = 50;
static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);
static readonly TimeSpan DefaultMaxDuration = TimeSpan.FromMinutes(15);
```

Parse duration strings with a local helper accepting positive integral `s`,
`m`, or `h` suffixes and reject values whose resulting `TimeSpan` exceeds 24
hours. Validate snapshot names with `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`.

- [ ] **Step 4: Run the CLI tests**

Run the Task 1 command.

Expected: PASS.

- [ ] **Step 5: Commit the CLI and contract slice**

```powershell
git add src/SqlHarness.Core/Contracts.cs src/SqlHarness.Cli/SqlHarnessCli.cs src/SqlHarness.Cli/Commands/WatchCommand.cs src/SqlHarness.Cli/Commands/SnapshotCommand.cs tests/SqlHarness.Tests/Cli/CommandTests.cs
git commit -m "feat: add watch and snapshot command contracts"
```

### Task 2: Extract bounded reusable query collection

**Files:**
- Create: `src/SqlHarness.Core/QueryResultCollector.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`
- Create: `tests/SqlHarness.Tests/QueryResultCollectorTests.cs`
- Modify: `tests/SqlHarness.Tests/QueryTests.cs`

**Interfaces:**
- Consumes: `ISqlSession`, `ISqlReader`, `SqlExecutionCommand`, `CanonicalResultAccumulator`, and existing query report types.
- Produces: internal `CollectedQueryResult` and `QueryResultCollector.CollectAsync(ISqlSession, SqlExecutionCommand, int, IReadOnlyCollection<string>, CanonicalResultAccumulator, CancellationToken)`, reused unchanged by query, watch, and snapshot.

- [ ] **Step 1: Write collector characterization tests**

Create a fake reader with two result sets and SQL messages. Assert:

```csharp
var result = await QueryResultCollector.CollectAsync(
    session, new SqlExecutionCommand("select", [], 30), maxRows: 2,
    knownSecrets: ["secret"], raw, CancellationToken.None);

Assert.Equal(2, result.ResultSets.Count);
Assert.Equal(3, result.ResultSets[0].RowCount);
Assert.Equal(1, result.ResultSets[0].OmittedRowCount);
Assert.DoesNotContain("secret", string.Join('\n', result.Messages));
Assert.NotEmpty(result.Canonical.Hash);
```

Cover zero-column messages, multiple result sets, row truncation, unsupported
scalar failure, cancellation, and message redaction.

- [ ] **Step 2: Run collector tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~QueryResultCollectorTests
```

Expected: FAIL because `QueryResultCollector` is missing.

- [ ] **Step 3: Move collection without changing query behavior**

Move the existing bounded collection logic from `SqlHarnessModule` into:

```csharp
internal sealed record CollectedQueryResult(
    IReadOnlyList<SqlHarnessResultSetReport> ResultSets,
    IReadOnlyList<string> Messages,
    int RecordsAffected,
    CanonicalResult Canonical);

internal static class QueryResultCollector
{
    internal static Task<CollectedQueryResult> CollectAsync(
        ISqlSession session,
        SqlExecutionCommand command,
        int maxRows,
        IReadOnlyCollection<string> knownSecrets,
        CanonicalResultAccumulator raw,
        CancellationToken ct);
}
```

The helper opens and disposes the reader, captures only messages added by its
execution, drains omitted rows and remaining result sets, and completes a
per-execution canonical accumulator. Preserve the current public query report
and raw-footprint behavior exactly.

- [ ] **Step 4: Run focused collector and query regressions**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~QueryResultCollectorTests|FullyQualifiedName~QueryTests"
```

Expected: PASS.

- [ ] **Step 5: Commit the reusable collector**

```powershell
git add src/SqlHarness.Core/QueryResultCollector.cs src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/QueryResultCollectorTests.cs tests/SqlHarness.Tests/QueryTests.cs
git commit -m "refactor: extract bounded query result collector"
```

### Task 3: Parse watch predicates and evaluate stop conditions

**Files:**
- Create: `src/SqlHarness.Core/WatchCondition.cs`
- Create: `tests/SqlHarness.Tests/WatchConditionTests.cs`

**Interfaces:**
- Consumes: `SqlHarnessResultSetReport`.
- Produces: `WatchCondition.Parse(string)`, `WatchCondition.IsMet(SqlHarnessResultSetReport)`, and `WatchUnchangedTracker.Observe(string hash)`.

- [ ] **Step 1: Write predicate and unchanged-tracker tests**

```csharp
[Theory]
[InlineData("Count >= 10", 10, true)]
[InlineData("Count < 10", 10, false)]
[InlineData("Status = Completed", "Completed", true)]
[InlineData("Status != Failed", "Completed", true)]
public void Predicate_evaluates_first_row_column(
    string text, object value, bool expected)
{
    var condition = WatchCondition.Parse(text);
    Assert.Equal(expected, condition.IsMet(Result("Count", value, alternateName: "Status")));
}

[Fact]
public void Unchanged_tracker_stops_after_three_consecutive_unchanged_polls()
{
    var tracker = new WatchUnchangedTracker(3);
    Assert.False(tracker.Observe("A"));
    Assert.False(tracker.Observe("A"));
    Assert.False(tracker.Observe("A"));
    Assert.True(tracker.Observe("A"));
}
```

Also cover operator precedence (`<=` before `<`), invariant decimal parsing,
text comparison, missing first row, missing/duplicate column names, `NULL`,
invalid predicate syntax, and resetting the unchanged counter after a changed
hash. “Three unchanged polls” means three polls after the last changed result;
the initial poll establishes the baseline.

- [ ] **Step 2: Run tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~WatchConditionTests
```

Expected: FAIL because the watch condition types are missing.

- [ ] **Step 3: Implement the pure condition components**

Use this shape:

```csharp
internal sealed record WatchCondition(string Column, WatchOperator Operator, string Operand)
{
    internal static WatchCondition Parse(string text);
    internal bool IsMet(SqlHarnessResultSetReport firstResultSet);
}

internal enum WatchOperator { Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual }

internal sealed class WatchUnchangedTracker(int requiredUnchangedPolls)
{
    internal bool Observe(string hash);
}
```

Resolve the column case-insensitively and require exactly one match. Reject a
missing result set, missing row, missing column, duplicate column, and `NULL`
condition value with `SqlHarnessSafetyException`. Use `decimal.TryParse` with
`NumberStyles.Number` and `CultureInfo.InvariantCulture` on both sides;
otherwise only `=` and `!=` are valid text comparisons.

- [ ] **Step 4: Run watch condition tests**

Run the Task 3 test command.

Expected: PASS.

- [ ] **Step 5: Commit condition evaluation**

```powershell
git add src/SqlHarness.Core/WatchCondition.cs tests/SqlHarness.Tests/WatchConditionTests.cs
git commit -m "feat: add watch stop condition evaluation"
```

### Task 4: Implement deterministic watch orchestration

**Files:**
- Create: `src/SqlHarness.Core/WatchRunner.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`
- Create: `tests/SqlHarness.Tests/WatchTests.cs`

**Interfaces:**
- Consumes: `QueryResultCollector`, `WatchCondition`, `WatchUnchangedTracker`, resolved targets, SQL safety, and parameters.
- Produces: `IWatchClock`, `SystemWatchClock`, and `WatchRunner.ExecuteAsync(SqlHarnessWatchOperation, ResolvedTarget, IReadOnlyList<SqlHarnessParameter>, IReadOnlyCollection<string>, CancellationToken)` returning `SqlHarnessOutcome`.

- [ ] **Step 1: Write runner tests with fake time and session**

Cover: first poll emitted in full; repeated identical polls omitted; a changed
poll emitted; predicate match exits `0`; unchanged threshold exits `0`;
deadline exits `7`; each poll uses the same connected session; cancellation
interrupts the delay; and query/safety/auth/target failures preserve existing
exit mapping.

```csharp
[Fact]
public async Task Watch_emits_first_and_changed_polls_only()
{
    var clock = new FakeWatchClock();
    var session = FakeSession.WithScalarPolls(1, 1, 2, 2);
    var outcome = await Module(session, clock).ExecuteAsync(
        Watch(untilUnchanged: 1));

    var report = Assert.IsType<SqlHarnessWatchReport>(outcome.Report);
    Assert.Equal([1, 3], report.EmittedPolls.Select(p => p.Poll));
    Assert.Equal(WatchExitReason.Unchanged, report.ExitReason);
}
```

- [ ] **Step 2: Run watch tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~WatchTests
```

Expected: FAIL because the runner is missing.

- [ ] **Step 3: Implement the clock and polling loop**

```csharp
internal interface IWatchClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

internal sealed class WatchRunner(
    ISqlSessionFactory sessions, IWatchClock clock)
{
    internal Task<SqlHarnessOutcome> ExecuteAsync(
        SqlHarnessWatchOperation operation,
        ResolvedTarget target,
        IReadOnlyList<SqlHarnessParameter> parameters,
        IReadOnlyCollection<string> knownSecrets,
        CancellationToken ct);
}
```

Connect once, check the deadline before starting the next delay, execute each
poll with the operation timeout and max-row bound, and retain only the previous
hash plus emitted poll reports. Add every poll’s canonical footprint to the raw
gain footprint; emitted gain is still completed by the CLI receipt.

Validate target, SQL safety, parameter references, predicate syntax, and bounds
before authentication. Add module constructor injection for `IWatchClock`,
defaulting to `SystemWatchClock`.

- [ ] **Step 4: Run watch and query regression tests**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~WatchTests|FullyQualifiedName~QueryTests|FullyQualifiedName~CommandTests"
```

Expected: PASS.

- [ ] **Step 5: Commit watch orchestration**

```powershell
git add src/SqlHarness.Core/WatchRunner.cs src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/WatchTests.cs
git commit -m "feat: execute bounded watch polling"
```

### Task 5: Persist canonical snapshots atomically

**Files:**
- Modify: `src/SqlHarness.Core/SqlHarnessPaths.cs`
- Create: `src/SqlHarness.Core/SnapshotStore.cs`
- Create: `tests/SqlHarness.Tests/SnapshotStoreTests.cs`
- Modify: `tests/SqlHarness.Tests/SqlHarnessPathsTests.cs`

**Interfaces:**
- Consumes: `SqlHarnessResultSetReport`, `OutputFootprint`, and the validated snapshot label.
- Produces: `SnapshotDocument`, `ISnapshotStore`, `SnapshotStore.Save(string, SnapshotDocument, bool)`, and `SnapshotStore.Load(string)`.

- [ ] **Step 1: Write path, round-trip, overwrite, and failure tests**

```csharp
[Fact]
public void Snapshot_round_trip_preserves_typed_cells_and_shape()
{
    var store = new SnapshotStore(temp.Path);
    var document = SnapshotFixture.Document(
        columns: [("Id", "System.Int32")],
        rows: [[1], [2]]);

    store.Save("before-import", document, force: false);
    Assert.Equal(document, store.Load("before-import"));
}

[Fact]
public void Existing_snapshot_requires_force_and_original_remains_intact()
{
    var store = new SnapshotStore(temp.Path);
    var original = SnapshotFixture.Document(rows: [[1]]);
    var replacement = SnapshotFixture.Document(rows: [[2]]);
    store.Save("before", original, force: false);

    Assert.Throws<IOException>(() => store.Save("before", replacement, force: false));
    Assert.Equal(original, store.Load("before"));
}
```

Also cover invalid names, missing labels, malformed/version-mismatched JSON,
write failure cleanup, force replacement, nested-path attempts, and no SQL or
parameter text in the persisted document.

- [ ] **Step 2: Run store tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~SnapshotStoreTests|FullyQualifiedName~SqlHarnessPathsTests"
```

Expected: FAIL because the snapshot path and store are missing.

- [ ] **Step 3: Add the versioned store**

Add `SqlHarnessPaths.SnapshotsDir`. Persist:

```csharp
internal sealed record SnapshotDocument(
    int Version,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SnapshotResultSet> ResultSets,
    string ResultHash);

internal sealed record SnapshotResultSet(
    IReadOnlyList<SqlHarnessColumnReport> Columns,
    IReadOnlyList<IReadOnlyList<SnapshotScalar>> Rows,
    long RowCount);

internal sealed record SnapshotScalar(string Type, bool IsNull, JsonElement Value);

internal interface ISnapshotStore
{
    void Save(string name, SnapshotDocument document, bool force);
    SnapshotDocument Load(string name);
}
```

Use version `1`, camel-case JSON, and atomic write to
`<root>/<label>.json.tmp-<guid>` followed by `File.Move`. With `--force`,
replace the destination only after the complete temporary file is flushed.
Resolve and verify the final full path remains directly under the configured
root. Convert supported values with the same invariant type distinctions used
by `CanonicalResultAccumulator`.

- [ ] **Step 4: Run store and path tests**

Run the Task 5 test command.

Expected: PASS.

- [ ] **Step 5: Commit snapshot persistence**

```powershell
git add src/SqlHarness.Core/SqlHarnessPaths.cs src/SqlHarness.Core/SnapshotStore.cs tests/SqlHarness.Tests/SnapshotStoreTests.cs tests/SqlHarness.Tests/SqlHarnessPathsTests.cs
git commit -m "feat: persist named canonical snapshots"
```

### Task 6: Diff snapshot result shapes, rows, and cells

**Files:**
- Create: `src/SqlHarness.Core/SnapshotDiffer.cs`
- Create: `tests/SqlHarness.Tests/SnapshotDifferTests.cs`

**Interfaces:**
- Consumes: two `SnapshotDocument` instances.
- Produces: `SnapshotDiffResult` with a verdict and bounded, value-free `SqlHarnessSnapshotDifference` entries.

- [ ] **Step 1: Write exact diff tests**

```csharp
[Fact]
public void Changed_cell_reports_location_without_values()
{
    var before = SnapshotFixture.Document(rows: [[1, "secret-before"]]);
    var after = SnapshotFixture.Document(rows: [[1, "secret-after"]]);

    var diff = SnapshotDiffer.Compare(before, after);

    var change = Assert.Single(diff.Differences);
    Assert.Equal(new SqlHarnessSnapshotDifference(0, 0, 1, "cell-changed"), change);
    Assert.DoesNotContain("secret", JsonSerializer.Serialize(diff), StringComparison.Ordinal);
}
```

Cover identical documents, result-set count changes, column count/name/type/nullability
changes, added/removed rows, duplicate rows, row order, multiple cell changes, and
the 100-difference emission cap while retaining the full `DifferenceCount`.
Column-shape changes must return a validation/safety failure instead of a normal
`Different` verdict, as required by the spec.

- [ ] **Step 2: Run differ tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~SnapshotDifferTests
```

Expected: FAIL because `SnapshotDiffer` is missing.

- [ ] **Step 3: Implement deterministic positional diffing**

```csharp
internal sealed record SnapshotDiffResult(
    int DifferenceCount,
    IReadOnlyList<SqlHarnessSnapshotDifference> Differences);

internal static class SnapshotDiffer
{
    internal static SnapshotDiffResult Compare(
        SnapshotDocument baseline, SnapshotDocument candidate);
}
```

Compare result sets and rows in order. Count one difference for each added or
removed row and one for each changed cell. Do not include cell values. Throw
`SqlHarnessSafetyException("Snapshot result column shape changed.")` before
producing a diff when column metadata differs.

- [ ] **Step 4: Run differ tests**

Run the Task 6 test command.

Expected: PASS.

- [ ] **Step 5: Commit snapshot diffing**

```powershell
git add src/SqlHarness.Core/SnapshotDiffer.cs tests/SqlHarness.Tests/SnapshotDifferTests.cs
git commit -m "feat: compare canonical snapshots"
```

### Task 7: Execute snapshot capture and diff through the module

**Files:**
- Create: `src/SqlHarness.Core/SnapshotRunner.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`
- Create: `tests/SqlHarness.Tests/SnapshotTests.cs`

**Interfaces:**
- Consumes: `QueryResultCollector`, `ISnapshotStore`, and `SnapshotDiffer`.
- Produces: `SnapshotRunner.ExecuteAsync(SqlHarnessSnapshotOperation, ResolvedTarget, IReadOnlyList<SqlHarnessParameter>, IReadOnlyCollection<string>, CancellationToken)` and complete module handling of `SqlHarnessSnapshotOperation`.

- [ ] **Step 1: Write module-level snapshot tests**

Test capture/store, force overwrite, identical diff exit `0`, changed diff exit
`8`, shape change exit `2`, missing snapshot exit `6`, SQL failure exit `5`,
auth failure exit `3`, target mismatch exit `4`, redaction, and gain receipt
behavior.

```csharp
[Fact]
public async Task Snapshot_diff_returns_eight_and_only_compact_locations()
{
    var store = new FakeSnapshotStore(SnapshotFixture.Document(rows: [[1]]));
    var outcome = await Module(FakeSession.WithRows([2]), store)
        .ExecuteAsync(Snapshot(diff: true));

    Assert.Equal(SqlHarnessExitCode.SnapshotDifferences, outcome.ExitCode);
    var report = Assert.IsType<SqlHarnessSnapshotReport>(outcome.Report);
    Assert.Equal(SnapshotVerdict.Different, report.Verdict);
    Assert.Single(report.Differences);
}
```

- [ ] **Step 2: Run snapshot tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~SnapshotTests
```

Expected: FAIL because module dispatch is not implemented.

- [ ] **Step 3: Implement snapshot orchestration**

Validate bounds, label, target, SQL safety, parameters, and references before
authentication. Connect once and execute once. Reject a captured result with
any `OmittedRowCount > 0` using:

```text
Snapshot result exceeded --max-rows; raise the explicit bound or narrow the query.
```

For capture, store the complete bounded result and return `Stored`. For diff,
load before SQL execution so a missing/corrupt baseline maps to local storage
without opening a connection; then execute, compare, and return `Identical` or
`Different`. Inject `ISnapshotStore` into `SqlHarnessModule`, defaulting to
`SnapshotStore(SqlHarnessPaths.SnapshotsDir)`.

- [ ] **Step 4: Run snapshot, query, and CLI regressions**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~SnapshotTests|FullyQualifiedName~QueryTests|FullyQualifiedName~CommandTests"
```

Expected: PASS.

- [ ] **Step 5: Commit snapshot orchestration**

```powershell
git add src/SqlHarness.Core/SnapshotRunner.cs src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/SnapshotTests.cs
git commit -m "feat: execute snapshot capture and diff"
```

### Task 8: Render reports and extend gain accounting

**Files:**
- Modify: `src/SqlHarness.Cli/Commands/Renderer.cs`
- Modify: `src/SqlHarness.Core/GainStore.cs`
- Modify: `tests/SqlHarness.Tests/Cli/CommandTests.cs`
- Modify: `tests/SqlHarness.Tests/GainStoreTests.cs`

**Interfaces:**
- Consumes: watch/snapshot reports and deferred emission receipts.
- Produces: stable text/JSON output and `Watch`/`Snapshot` gain summaries.

- [ ] **Step 1: Write rendering and aggregation tests**

Assert text output contains only changed watch polls plus one final summary;
snapshot text is exactly a one-line verdict plus bounded locations; JSON uses
the report contracts; and legacy gain JSONL remains readable.

```csharp
Assert.Equal(1, gain.Watch.Executions);
Assert.Equal(1, gain.Snapshot.Executions);
Assert.DoesNotContain("unchanged-poll-row-value", output.ToString());
Assert.Contains("Exit reason: unchanged", output.ToString());
```

Append one successful record for each command before making these assertions.

- [ ] **Step 2: Run rendering and gain tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~CommandTests|FullyQualifiedName~GainStoreTests"
```

Expected: FAIL because rendering and gain scopes are absent.

- [ ] **Step 3: Add compact output and gain scopes**

Render watch polls with poll number, elapsed milliseconds, columns, and rows,
then:

```text
Polls: <count>; elapsed: <milliseconds> ms; exit reason: <condition-met|unchanged|max-duration>
```

Render snapshot as:

```text
Snapshot <name>: <stored|identical|N differences>
```

Extend `SqlHarnessGainReport` with `Watch` and `Snapshot` properties and accept
both names in `GainStore.Validate` and aggregation. Preserve backward
compatibility for existing JSONL records.

- [ ] **Step 4: Run rendering, gain, watch, and snapshot tests**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~CommandTests|FullyQualifiedName~GainStoreTests|FullyQualifiedName~WatchTests|FullyQualifiedName~SnapshotTests"
```

Expected: PASS.

- [ ] **Step 5: Commit output and accounting**

```powershell
git add src/SqlHarness.Cli/Commands/Renderer.cs src/SqlHarness.Core/GainStore.cs tests/SqlHarness.Tests/Cli/CommandTests.cs tests/SqlHarness.Tests/GainStoreTests.cs
git commit -m "feat: render and account for watch snapshots"
```

### Task 9: Synchronize public and agent documentation

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `skills/sqlharness/SKILL.md`
- Modify: `tests/SqlHarness.Tests/SmokeTests.cs`
- Modify: `tests/SqlHarness.Tests/ContractsTests.cs`

**Interfaces:**
- Consumes: final CLI syntax and exit-code behavior.
- Produces: synchronized help/docs examples and contract coverage.

- [ ] **Step 1: Add failing documentation contract assertions**

Require all three documents to contain `watch`, `snapshot`, `--until-unchanged`,
`--diff`, exit codes `7` and `8`, the snapshots directory sensitivity warning,
and PowerShell `--file` examples. Extend help smoke tests to require both command
names.

- [ ] **Step 2: Run documentation contract tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~SmokeTests|FullyQualifiedName~ContractsTests"
```

Expected: FAIL on missing command/help/documentation strings.

- [ ] **Step 3: Update the synchronized docs**

Document these examples verbatim:

```powershell
sqlharness watch prod-eu --var tenant=acme --var env=uat --file .\queries\progress.sql --param target:int=1000 --until "Imported >= 1000" --interval 30 --max-duration 45m --json
sqlharness snapshot prod-eu --var tenant=acme --var env=uat --file .\queries\coverage.sql --name before-import --json
sqlharness snapshot prod-eu --var tenant=acme --var env=uat --file .\queries\coverage.sql --name before-import --diff --json
```

State that snapshots may contain sensitive result data, `--force` is required
to replace one, `--diff` never prints cell values, and exit `8` means a valid
comparison found differences rather than an execution failure.

- [ ] **Step 4: Run documentation tests and inspect help**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~SmokeTests|FullyQualifiedName~ContractsTests"
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -- --help
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -- watch --help
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -- snapshot --help
```

Expected: tests PASS and help lists the documented options without secrets.

- [ ] **Step 5: Commit documentation**

```powershell
git add README.md AGENTS.md skills/sqlharness/SKILL.md tests/SqlHarness.Tests/SmokeTests.cs tests/SqlHarness.Tests/ContractsTests.cs
git commit -m "docs: document watch and snapshot workflows"
```

### Task 10: Run the complete verification gate

**Files:**
- Verify only; modify only the files owned by the failing task if a gate exposes a defect.

**Interfaces:**
- Consumes: all previous task deliverables.
- Produces: a clean build, complete test pass, valid help, and a reviewable branch.

- [ ] **Step 1: Run formatting verification**

```powershell
dotnet format .\SqlHarness.sln --verify-no-changes
```

Expected: exit `0`.

- [ ] **Step 2: Build release configuration**

```powershell
dotnet build .\SqlHarness.sln -c Release
```

Expected: `Build succeeded`, zero errors.

- [ ] **Step 3: Run the complete test suite**

```powershell
dotnet test .\SqlHarness.sln -c Release --no-build
```

Expected: all tests pass, zero failed.

- [ ] **Step 4: Run offline CLI smoke checks**

```powershell
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- --help
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- watch --help
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- snapshot --help
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- gain --json
```

Expected: all commands exit `0`; help exposes the documented contracts and
`gain --json` includes `watch` and `snapshot` summaries.

- [ ] **Step 5: Inspect the final branch**

```powershell
git status --short
git log --oneline --decorate -12
git diff --check HEAD~10..HEAD
```

Expected: clean working tree, the ten focused commits above, and no whitespace
errors.
