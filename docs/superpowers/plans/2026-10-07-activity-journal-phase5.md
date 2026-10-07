# Activity Journal — Phase 5 (gain from the journal, retention, idle shutdown, autostart) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the activity journal the only local statistics store and make the dashboard self-managing:
- `sqlharness gain` and `sqlharness_gain` aggregate from `activity.db`. `gain.jsonl` is no longer written or read.
- Opt-in retention trims the journal.
- An autostarted dashboard (`dashboard.autoStart`) is launched detached by `mcp serve` and exits by itself after `dashboard.idleShutdownHours` without activity.

**Architecture:**
- **Gain.** `SqlHarnessModule` stops writing gain records. Its emission receipt only returns the exit code and carries the raw footprint. `JournalingModule` already records raw and emitted footprints per operation; schema v3 adds byte and line counts. A new `JournalGainStore` (the module's read-only `IGainSource`) aggregates the operations whose output was emitted, using the same buckets and token rules as before.
- **Retention.** `JournalRetention` (Core) deletes in short batches through the v2 `ON DELETE CASCADE` keys and then runs `incremental_vacuum`.
- **Dashboard housekeeping.** `DashboardHost` gets a loop that runs retention hourly and, in `--background` mode only, idle shutdown based on journal writes (`PRAGMA data_version`), HTTP requests, and open SSE streams.
- **Autostart.** `mcp serve` calls `DashboardAutostart`, which launches `sqlharness dashboard --background` detached (no inherited stdio, home directory as working directory) when no dashboard is published. It also starts one retention pass in the background.

**Tech Stack:** .NET 8, `Microsoft.Data.Sqlite` 10.0.8, ASP.NET Core (Dashboard), Spectre.Console.Cli, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-06-activity-dashboard-design.md` ("Dashboard server" autostart, idle shutdown, retention; "Implementation phases → 5"). Decisions recorded on 2026-10-07:
- `gain.jsonl` is replaced by the journal, with no migration of old data.
- A failed gain write never changes an exit code.
- With `journal.enabled: false`, `gain` reports zeros and says the journal is disabled.
- IO statistics for `query` are rejected; `measure`/`compare` exist for that.

## Global Constraints

- **Unchanged agent output.** Agent-visible output and exit codes of every command stay byte-identical, with two deliberate exceptions:
  - `gain` output gains a `journalEnabled` field (JSON) and a one-line note when it is `false` (text).
  - A gain storage failure no longer turns a successful command into exit `6`.
- **MCP stdout carries protocol frames only.** Autostart never writes to MCP stdout. The child process inherits none of the MCP server's standard handles, so the MCP client still sees EOF when the server exits.
- **Retention is off by default.** It runs only with `journal.enabled` and `journal.retention.enabled` both `true`. It never deletes an operation whose host process is still alive. It deletes in batches of at most 500 rows per transaction, so a writer never waits longer than its existing 1 s budget.
- **Idle shutdown** applies only to `sqlharness dashboard --background` (the autostarted instance). A dashboard started by hand runs until Ctrl+C.
- **Autostart is off by default** (`dashboard.autoStart: false`). An invalid `config.json` never enables it; this fail-closed behavior already exists.
- `TreatWarningsAsErrors`, `dotnet format --verify-no-changes`, and the gate parity tests stay green.
- **Both gates must be green before the work is done:** `pwsh ./scripts/verify.ps1` and `pwsh ./scripts/verify-linux.ps1`.

## Review Focus

1. **An MCP client waiting for EOF on the server's stdout** must still get it after `mcp serve` exits, even though an autostarted dashboard keeps running. The launched process must not inherit the server's stdio. Pinned by Task 5, `Unix_launch_redirects_and_closes_every_standard_stream` and `Windows_launch_uses_shell_execute_without_handle_inheritance`, plus the manual check in Task 6.
2. **Retention while an agent writes** must not make the agent's journal write time out or delete a running operation. Pinned by Task 3, `Retention_never_deletes_live_running_operations` and `Retention_deletes_in_short_transactions`.
3. **`gain` totals** must not change meaning: the same buckets, failures counted, `plan`/`schema` only in the total, and only operations whose output was emitted (MCP calls are excluded, as before). Pinned by Task 1, `Gain_buckets_match_the_previous_semantics` and `Operations_without_an_emitted_footprint_are_not_counted`.
4. **A dashboard with an open browser tab** must not idle-exit, and a manually started dashboard must never idle-exit. Pinned by Task 4, `Background_dashboard_stays_up_while_a_live_stream_is_open` and `Foreground_dashboard_never_idle_exits`.
5. **Running from `dotnet sqlharness.dll`** (development) instead of the single-file `sqlharness.exe` must launch the dashboard with the right command line. Pinned by Task 5, `Launch_command_uses_the_dll_when_hosted_by_dotnet`.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/SqlHarness.Core/Journal/JournalSchema.cs`, `ActivityJournal.cs` (modify) | Schema v3 footprint columns, emission writes |
| `src/SqlHarness.Core/GainStore.cs` (delete) → `src/SqlHarness.Core/GainReport.cs` (new) | Gain report records and `IGainSource` |
| `src/SqlHarness.Core/Journal/JournalGainStore.cs` (new) | Gain aggregate from the journal |
| `src/SqlHarness.Core/SqlHarnessModule.cs`, `SqlHarnessPaths.cs` (modify) | Drop gain writes and `GainFile` |
| `src/SqlHarness.Cli/Commands/Renderer.cs` (modify) | Disabled-journal note |
| `src/SqlHarness.Core/Journal/ProcessLiveness.cs` (moved from Dashboard) | Shared liveness check |
| `src/SqlHarness.Core/Journal/JournalRetention.cs` (new) | Retention |
| `src/SqlHarness.Dashboard/DashboardActivity.cs` (new), `DashboardServer.cs`, `DashboardHost.cs` (modify) | Activity tracking, housekeeping loop |
| `src/SqlHarness.Dashboard/DashboardAutostart.cs` (new) | Launch decision and detached launcher |
| `src/SqlHarness.Cli/Commands/DashboardCommand.cs`, `McpServeCommand.cs` (modify) | Background mode, autostart, startup retention |
| Tests under `tests/SqlHarness.Tests/{Journal,Dashboard}` and the module tests listed in Task 2 | Tests |
| `README.md`, `AGENTS.md`, spec (modify) | Documentation |

---

### Task 1: Journal footprints and the journal gain source

**Files:**
- Modify: `src/SqlHarness.Core/Journal/JournalSchema.cs`, `src/SqlHarness.Core/Journal/ActivityJournal.cs`
- Create: `src/SqlHarness.Core/GainReport.cs`, `src/SqlHarness.Core/Journal/JournalGainStore.cs`
- Modify: `src/SqlHarness.Core/GainStore.cs` (move the report records out; the file is deleted in Task 2)
- Test: `tests/SqlHarness.Tests/Journal/JournalGainStoreTests.cs`; update the version-migration test

**Interfaces:**
- Produces:
  - Schema v3: `operations.raw_bytes`, `raw_lines`, `emitted_bytes`, `emitted_lines` (INTEGER, nullable). `RecordEmission` fills all four plus the existing token columns.
  - `internal interface IGainSource { SqlHarnessGainReport Aggregate(); }` in `GainReport.cs`. `IGainStore` keeps working until Task 2 removes it.
  - `SqlHarnessGainReport.JournalEnabled` (`bool`, `init`, default `true`)
  - `internal sealed class JournalGainStore(string databasePath, Func<bool> journalEnabled) : IGainSource`
  - `internal static readonly IReadOnlySet<string> JournalGainStore.CountedOperations` = `query, compare, measure, plan, schema, ping, counts, space, watch, snapshot, qstop, indexes`

- [ ] **Step 1: Write the failing tests**

`tests/SqlHarness.Tests/Journal/JournalGainStoreTests.cs`:

```csharp
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class JournalGainStoreTests
{
    private static void Run(IActivityJournal journal, string operation, string status, long raw, long emitted, long durationMs = 10, bool emit = true)
    {
        var handle = journal.Begin(JournalTestData.Session(), new OperationStart(operation, null, null, false, null, null, null, null));
        journal.Complete(handle, new OperationEnd(status, status == "succeeded" ? 0 : 5, null, durationMs, null, null, null, null, null));
        if (emit)
            journal.RecordEmission(handle, new OutputFootprint(raw, 3), new OutputFootprint(emitted, 1));
    }

    private static (JournalTempDirectory Temp, IActivityJournal Journal) Open()
    {
        var temp = new JournalTempDirectory();
        return (temp, ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null, TimeProvider.System));
    }

    [Fact]
    public void Gain_buckets_match_the_previous_semantics()
    {
        var (temp, journal) = Open();
        using (temp)
        {
            Run(journal, "query", "succeeded", raw: 400, emitted: 40, durationMs: 7);
            Run(journal, "query", "failed", raw: 100, emitted: 20, durationMs: 3);
            Run(journal, "compare", "succeeded", raw: 800, emitted: 80);
            Run(journal, "qstop", "succeeded", raw: 40, emitted: 4);
            Run(journal, "plan", "succeeded", raw: 40, emitted: 8);
            Run(journal, "schema", "rejected", raw: 0, emitted: 4);

            var report = new JournalGainStore(temp.DatabasePath, () => true).Aggregate();

            Assert.True(report.JournalEnabled);
            Assert.Equal((6L, 2L), (report.Total.Executions, report.Total.Failures));
            Assert.Equal((2L, 1L, 10L), (report.Query.Executions, report.Query.Failures, report.Query.DurationMilliseconds));
            Assert.Equal((500L, 6L, 60L, 2L), (report.Query.RawBytes, report.Query.RawLines, report.Query.EmittedBytes, report.Query.EmittedLines));
            Assert.Equal(OutputFootprint.EstimateTokens(400) + OutputFootprint.EstimateTokens(100), report.Query.RawEstimatedTokens);
            Assert.Equal(1, report.Compare.Executions);
            Assert.Equal(1, report.QueryStoreTop.Executions);
            Assert.Equal(0, report.Measure.Executions);
            // Saved tokens are the per-execution non-negative gross, as gain.jsonl recorded them.
            Assert.Equal(SumSaved([400, 100, 800, 40, 40, 0], [40, 20, 80, 4, 8, 4]), report.Total.SavedEstimatedTokens);
        }
    }

    private static long SumSaved(long[] raw, long[] emitted) =>
        raw.Zip(emitted, (r, e) => Math.Max(OutputFootprint.EstimateTokens(r) - OutputFootprint.EstimateTokens(e), 0)).Sum();

    [Fact]
    public void Operations_without_an_emitted_footprint_are_not_counted()
    {
        var (temp, journal) = Open();
        using (temp)
        {
            Run(journal, "query", "succeeded", raw: 400, emitted: 40);
            Run(journal, "query", "succeeded", raw: 400, emitted: 40, emit: false);
            Run(journal, "gain", "succeeded", raw: 4, emitted: 4);
            Run(journal, "validate", "succeeded", raw: 4, emitted: 4);

            var report = new JournalGainStore(temp.DatabasePath, () => true).Aggregate();

            Assert.Equal(1, report.Total.Executions);
        }
    }

    [Fact]
    public void Disabled_journal_reports_zeros_and_says_so()
    {
        var (temp, journal) = Open();
        using (temp)
        {
            Run(journal, "query", "succeeded", raw: 400, emitted: 40);

            var report = new JournalGainStore(temp.DatabasePath, () => false).Aggregate();

            Assert.False(report.JournalEnabled);
            Assert.Equal(0, report.Total.Executions);
        }
    }

    [Fact]
    public void Missing_database_reports_zeros()
    {
        using var temp = new JournalTempDirectory();

        var report = new JournalGainStore(temp.DatabasePath, () => true).Aggregate();

        Assert.True(report.JournalEnabled);
        Assert.Equal(0, report.Total.Executions);
    }

    [Fact]
    public void Emission_records_bytes_and_lines()
    {
        var (temp, journal) = Open();
        using (temp)
        {
            Run(journal, "query", "succeeded", raw: 400, emitted: 40);

            var row = JournalDb.Rows(temp.DatabasePath, "SELECT raw_bytes, raw_lines, emitted_bytes, emitted_lines FROM operations").Single();

            Assert.Equal((400L, 3L, 40L, 1L), ((long)row["raw_bytes"]!, (long)row["raw_lines"]!, (long)row["emitted_bytes"]!, (long)row["emitted_lines"]!));
        }
    }
}
```

The existing migration tests assert `JournalSchema.CurrentVersion`, so they keep passing at v3. Add one assertion to `ActivityJournalBenchmarkTests.Version_1_database_migrates_to_version_2_and_keeps_rows`:

```csharp
        Assert.Contains("raw_bytes", JournalDb.Rows(temp.DatabasePath, "SELECT name FROM pragma_table_info('operations')").Select(r => (string)r["name"]!));
```

Rename that test to `Version_1_database_migrates_to_the_current_version_and_keeps_rows`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~JournalGainStoreTests|FullyQualifiedName~ActivityJournalBenchmarkTests"`
Expected: build FAILS with `The type or namespace name 'JournalGainStore' could not be found`.

- [ ] **Step 3: Schema v3 and emission writes**

`JournalSchema.cs`. Set `CurrentVersion = 3` and add:

```csharp
    internal const string Version3 = """
        ALTER TABLE operations ADD COLUMN raw_bytes INTEGER;
        ALTER TABLE operations ADD COLUMN raw_lines INTEGER;
        ALTER TABLE operations ADD COLUMN emitted_bytes INTEGER;
        ALTER TABLE operations ADD COLUMN emitted_lines INTEGER;
        """;
```

In `ActivityJournal.Migrate`, after `if (locked < 2) ...`, add:

```csharp
            if (locked < 3)
                Execute(connection, JournalSchema.Version3);
```

In `ActivityJournal.RecordEmission`, change the SQL and parameters to:

```csharp
            update.CommandText = """
                UPDATE operations SET raw_tokens = $raw, emitted_tokens = $emitted,
                    raw_bytes = $rawBytes, raw_lines = $rawLines, emitted_bytes = $emittedBytes, emitted_lines = $emittedLines,
                    updated_at = $now
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", handle.OperationId);
            update.Parameters.AddWithValue("$raw", raw is null ? DBNull.Value : raw.EstimatedTokenCount);
            update.Parameters.AddWithValue("$emitted", emitted.EstimatedTokenCount);
            update.Parameters.AddWithValue("$rawBytes", raw is null ? DBNull.Value : raw.Bytes);
            update.Parameters.AddWithValue("$rawLines", raw is null ? DBNull.Value : raw.Lines);
            update.Parameters.AddWithValue("$emittedBytes", emitted.Bytes);
            update.Parameters.AddWithValue("$emittedLines", emitted.Lines);
            update.Parameters.AddWithValue("$now", Timestamp(_time.GetUtcNow()));
```

Keep the rest of the method (connection, try/catch, `Warn()`) exactly as it is.

- [ ] **Step 4: Move the report records and add the journal source**

Create `src/SqlHarness.Core/GainReport.cs` and move these declarations from `GainStore.cs` unchanged: `SqlHarnessGainSummary` and `SqlHarnessGainReport` (including the `Empty` helper and all `init` properties). Then add to `SqlHarnessGainReport`:

```csharp
    /// <summary>False when journal.enabled is off: the counts are zeros because nothing is recorded.</summary>
    public bool JournalEnabled { get; init; } = true;
```

And append to `GainReport.cs`:

```csharp
/// <summary>Read side of the local output-savings statistics (the gain command).</summary>
internal interface IGainSource
{
    SqlHarnessGainReport Aggregate();
}
```

Make `SqlHarnessGainReport`'s private `Empty` summary `internal static` so `JournalGainStore` can reuse it. Leave `GainRecord`, `IGainStore`, and `GainStore` in `GainStore.cs` for now. Task 2 deletes them.

`src/SqlHarness.Core/Journal/JournalGainStore.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace SqlHarness.Core;

/// <summary>
/// Gain statistics from the activity journal: every operation of a counted kind whose
/// emission receipt completed (raw and emitted byte counts present). MCP tool calls
/// never complete the receipt, so they are not counted, as with the former gain.jsonl.
/// plan and schema count only toward the total.
/// </summary>
internal sealed class JournalGainStore(string databasePath, Func<bool> journalEnabled) : IGainSource
{
    internal static readonly IReadOnlySet<string> CountedOperations = new HashSet<string>(StringComparer.Ordinal)
    {
        "query", "compare", "measure", "plan", "schema", "ping", "counts", "space", "watch", "snapshot", "qstop", "indexes",
    };

    public SqlHarnessGainReport Aggregate()
    {
        if (!journalEnabled())
            return Empty() with { JournalEnabled = false };
        if (!File.Exists(databasePath))
            return Empty();

        var buckets = CountedOperations.ToDictionary(name => name, _ => new Accumulator(), StringComparer.Ordinal);
        var total = new Accumulator();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 2,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation, status, COALESCE(duration_ms, 0), raw_bytes, raw_lines, emitted_bytes, emitted_lines
            FROM operations
            WHERE raw_bytes IS NOT NULL AND emitted_bytes IS NOT NULL;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var operation = reader.GetString(0);
            if (!buckets.TryGetValue(operation, out var bucket))
                continue;
            var row = new Row(
                reader.GetString(1) == "succeeded",
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                reader.GetInt64(5),
                reader.IsDBNull(6) ? 0 : reader.GetInt64(6));
            total.Add(row);
            bucket.Add(row);
        }

        return new SqlHarnessGainReport(total.ToSummary(), buckets["query"].ToSummary(), buckets["compare"].ToSummary())
        {
            Measure = buckets["measure"].ToSummary(),
            Ping = buckets["ping"].ToSummary(),
            Counts = buckets["counts"].ToSummary(),
            Space = buckets["space"].ToSummary(),
            Watch = buckets["watch"].ToSummary(),
            Snapshot = buckets["snapshot"].ToSummary(),
            QueryStoreTop = buckets["qstop"].ToSummary(),
            Indexes = buckets["indexes"].ToSummary(),
        };
    }

    private static SqlHarnessGainReport Empty() =>
        new(SqlHarnessGainReport.Empty, SqlHarnessGainReport.Empty, SqlHarnessGainReport.Empty);

    private sealed record Row(bool Success, long DurationMilliseconds, long RawBytes, long RawLines, long EmittedBytes, long EmittedLines);

    private sealed class Accumulator
    {
        private long _executions, _failures, _duration, _rawBytes, _rawLines, _emittedBytes, _emittedLines, _rawTokens, _emittedTokens, _savedTokens;

        public void Add(Row row)
        {
            var raw = OutputFootprint.EstimateTokens(row.RawBytes);
            var emitted = OutputFootprint.EstimateTokens(row.EmittedBytes);
            _executions++;
            if (!row.Success)
                _failures++;
            _duration += Math.Max(row.DurationMilliseconds, 0);
            _rawBytes += row.RawBytes;
            _rawLines += row.RawLines;
            _emittedBytes += row.EmittedBytes;
            _emittedLines += row.EmittedLines;
            _rawTokens += raw;
            _emittedTokens += emitted;
            _savedTokens += Math.Max(raw - emitted, 0);
        }

        public SqlHarnessGainSummary ToSummary() => new(
            _executions, _failures, _duration, _rawBytes, _rawLines, _emittedBytes, _emittedLines, _rawTokens, _emittedTokens, _savedTokens);
    }
}
```

The journal's `succeeded` status already covers exit codes 0, 7, and 8, which is the former `IsGainSuccess` rule. `OutputFootprint.EstimateTokens` is the same estimator `gain.jsonl` used.

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Journal"`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SqlHarness.Core tests/SqlHarness.Tests/Journal
git commit -m "Record emitted footprints in the journal and aggregate gain from it"
```

---

### Task 2: Switch gain to the journal and remove `gain.jsonl`

**Files:**
- Delete: `src/SqlHarness.Core/GainStore.cs`, `tests/SqlHarness.Tests/GainStoreTests.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`, `src/SqlHarness.Core/SqlHarnessPaths.cs`, `src/SqlHarness.Cli/Commands/Renderer.cs`, `README.md`, `tests/SqlHarness.Tests/SqlHarnessPathsTests.cs`, `tests/SqlHarness.Mcp.Tests/McpGainTests.cs`, and the module tests listed in Step 4
- Test: `tests/SqlHarness.Tests/Journal/JournalingModuleTests.cs` (new facts), `tests/SqlHarness.Tests/Cli/GainRenderTests.cs` (new)

**Interfaces:**
- Consumes: `IGainSource`, `JournalGainStore` (Task 1).
- Produces:
  - `SqlHarnessModule` takes `IGainSource gainSource` where it took `IGainStore gainStore`, in all three constructors, at the same parameter position.
  - The receipt callback returns `outcome.ExitCode` and carries `RawFootprint`.
  - `WithReceipt(outcome, raw)` has no duration or command parameter.
  - `GainRecord`, `IGainStore`, `GainStore`, and `SqlHarnessPaths.GainFile` no longer exist.

- [ ] **Step 1: Write the failing tests**

Add to `tests/SqlHarness.Tests/Journal/JournalingModuleTests.cs`. These replace the module-level gain tests deleted in Step 4:

```csharp
    [Fact]
    public async Task Receipt_completion_records_the_emitted_footprint_once_and_keeps_the_exit_code()
    {
        var receipt = new SqlHarnessEmissionReceipt((_, _) => Task.FromResult(SqlHarnessExitCode.SqlExecution))
        {
            RawFootprint = new OutputFootprint(800, 20),
        };
        var (module, temp) = Create(new FakeModule((_, _) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, null, "failed", receipt))));
        using (temp)
        {
            var outcome = await module.ExecuteAsync(Query());
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => outcome.EmissionReceipt!.CompleteAsync(new OutputFootprint(80, 4))));

            Assert.All(results, code => Assert.Equal(SqlHarnessExitCode.SqlExecution, code));
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT raw_bytes, emitted_bytes, emitted_lines, status FROM operations").Single();
            Assert.Equal((800L, 80L, 4L, "failed"), ((long)row["raw_bytes"]!, (long)row["emitted_bytes"]!, (long)row["emitted_lines"]!, (string)row["status"]!));
        }
    }

    [Fact]
    public async Task Gain_write_failure_never_changes_the_exit_code()
    {
        var receipt = new SqlHarnessEmissionReceipt((_, _) => Task.FromResult(SqlHarnessExitCode.Success));
        var journal = new ThrowingEmissionJournal();
        var module = new JournalingModule(
            new FakeModule((_, _) => Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null, receipt))),
            () => journal,
            () => JournalTestData.Session());

        var outcome = await module.ExecuteAsync(Query());

        Assert.Equal(SqlHarnessExitCode.Success, await outcome.EmissionReceipt!.CompleteAsync(new OutputFootprint(8, 1)));
    }

    private sealed class ThrowingEmissionJournal : IActivityJournal
    {
        public JournalHandle? Begin(SessionIdentity session, OperationStart start) => new(1);
        public bool Complete(JournalHandle? handle, OperationEnd end) => true;
        public void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted) =>
            throw new IOException("disk full");
    }
```

`Create`, `FakeModule`, and `Query` are the existing helpers in this class. If `Create` has a different shape, use it the way the class's existing `Emission_receipt_is_wrapped_and_records_tokens` test does.

`tests/SqlHarness.Tests/Cli/GainRenderTests.cs`:

```csharp
using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class GainRenderTests
{
    private sealed class GainModule(SqlHarnessGainReport report) : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null));
    }

    private static SqlHarnessGainReport Report(bool enabled) =>
        new(SqlHarnessGainReport.Empty, SqlHarnessGainReport.Empty, SqlHarnessGainReport.Empty) { JournalEnabled = enabled };

    [Fact]
    public async Task Text_output_notes_a_disabled_journal()
    {
        var output = new StringWriter();

        await SqlHarnessCli.Create(new GainModule(Report(false)), output).RunAsync(["gain"]);

        Assert.Contains("activity journal is disabled", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_output_carries_journal_enabled()
    {
        var output = new StringWriter();

        await SqlHarnessCli.Create(new GainModule(Report(true)), output).RunAsync(["gain", "--json"]);

        Assert.Contains("\"journalEnabled\": true", output.ToString(), StringComparison.Ordinal);
    }
}
```

If the CLI's JSON output is not indented, assert `"journalEnabled":true` instead. Check one existing `gain --json` test for the format.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~GainRenderTests|FullyQualifiedName~JournalingModuleTests"`
Expected: `Text_output_notes_a_disabled_journal` FAILS (no note yet). The `JournalingModuleTests` additions may already pass, because the decorator was best-effort since phase 1. That is fine; they guard the behavior after the module change.

- [ ] **Step 3: Switch the module, paths, and renderer**

`src/SqlHarness.Core/SqlHarnessModule.cs`:
- Replace the field `private readonly IGainStore _gainStore;` with `private readonly IGainSource _gainSource;`. In every constructor, change the parameter `IGainStore gainStore` to `IGainSource gainSource` at the same position and assign `_gainSource`.
- In the public `SqlHarnessModule(Func<...> loadProfiles)` constructor, replace `new GainStore(),` with:
  ```csharp
            new JournalGainStore(SqlHarnessPaths.ActivityDatabase, () => SqlHarnessConfigLoader.Load().Config.Journal.Enabled),
  ```
- In `ExecuteAsync`, the gain branch calls `_gainSource.Aggregate()`. Keep its `try/catch` mapping to `LocalStorage`, because a failed read is still a local storage error.
- Replace `WithReceipt` with:
  ```csharp
    /// <summary>
    /// Attaches the emission receipt: it carries the raw footprint for the activity journal
    /// (JournalingModule records raw and emitted footprints) and returns the outcome's exit code.
    /// </summary>
    private static SqlHarnessOutcome WithReceipt(SqlHarnessOutcome outcome, OutputFootprint raw)
    {
        var receipt = new SqlHarnessEmissionReceipt((_, _) => Task.FromResult(outcome.ExitCode))
        {
            RawFootprint = raw,
        };
        return outcome with { EmissionReceipt = receipt };
    }
  ```
  Update every call site (`grep -n "WithReceipt(" src/SqlHarness.Core/SqlHarnessModule.cs`) to `WithReceipt(outcome, rawFootprint)`, dropping the elapsed-milliseconds argument and the trailing command string (`"compare"`, `"measure"`, `"watch"`, `"snapshot"`, `"schema"`, …). Keep each call site's other code unchanged.
- Delete `IsGainSuccess`.

Delete `src/SqlHarness.Core/GainStore.cs` (everything left in it: `GainRecord`, `IGainStore`, `GainStore`).

`src/SqlHarness.Core/SqlHarnessPaths.cs`: delete the `GainFile` property. In `tests/SqlHarness.Tests/SqlHarnessPathsTests.cs`, delete the two `GainFile` assertions (lines 45 and 70) and nothing else.

`src/SqlHarness.Cli/Commands/Renderer.cs`, in the `SqlHarnessGainReport` branch, before the first `WriteLine`:

```csharp
            if (!gain.JournalEnabled)
                output.WriteLine("The activity journal is disabled (journal.enabled: false in config.json); gain has nothing to count.");
```

The JSON path serializes `JournalEnabled` automatically as `journalEnabled`.

`README.md`, line 271. Replace `Every command except \`gain\` records metadata-only raw and emitted byte counts in \`~/.sqlharness/data/gain.jsonl\`; SQL text, result values, plans, messages, and secrets are not recorded there.` with:

```markdown
`gain` aggregates the activity journal (`~/.sqlharness/data/activity.db`): every CLI command except `gain` whose output was rendered contributes its raw and emitted byte counts (MCP tool calls are not counted). With `journal.enabled: false` it reports zeros and says the journal is disabled.
```

Delete line 309 (`- Gain records: ...gain.jsonl`). An existing `gain.jsonl` file is left on disk and ignored.

- [ ] **Step 4: Update the module tests**

Run `dotnet build SqlHarness.sln`. The compiler lists every site that used `IGainStore`, `GainRecord`, or `gain.Records`. Fix them with these rules:

| Old pattern | New pattern |
|---|---|
| `private sealed class FakeGainStore : IGainStore` with `Records`/`Append` (and the variants `FakeGain`, `CapturingGainStore`, `NullGainStore`) | `: IGainSource` with only `public SqlHarnessGainReport Aggregate() => …` (keep its existing body or `throw new NotSupportedException()`). Delete `Records`, `Append`, and any append-failure constructor parameter. |
| `var record = Assert.Single(gain.Records); Assert.Equal(X, record.RawBytes); Assert.Equal(Y, record.RawLines);` | `var raw = outcome.EmissionReceipt!.RawFootprint!; Assert.Equal(X, raw.Bytes); Assert.Equal(Y, raw.Lines);` using the `outcome` of the same call |
| `Assert.True(Assert.Single(rich.Records).RawBytes > Assert.Single(lean.Records).RawBytes)` | Compare `richOutcome.EmissionReceipt!.RawFootprint!.Bytes > leanOutcome.EmissionReceipt!.RawFootprint!.Bytes` |
| `Assert.Equal("counts", Assert.Single(gain.Records).Command)` and other `record.Command` asserts | Delete the assert. Operation naming is pinned by `OperationJournalDescriberTests`. |
| `Assert.Empty(gain.Records)` before receipt completion (deferral checks) | Delete the assert |
| `record.Success`, `record.EmittedBytes`, `record.EmittedLines`, `record.DurationMilliseconds` asserts | Delete. Emission and status recording are pinned by the `JournalingModuleTests` added in Step 1. |
| A test whose purpose is that a throwing gain store maps the receipt to `LocalStorage` | Delete the test |

Delete these tests outright (the behavior was removed on purpose):
- `QueryTests.Receipt_maps_gain_storage_failure_without_leaking_exception`
- `CompareTests.Compare_receipt_reports_local_storage_when_gain_store_fails`
- `IndexesTests.Indexes_gain_append_failure_completes_as_local_storage`
- `QueryStoreTopTests.Qstop_gain_append_failure_completes_as_local_storage`
- `SpaceTests.Space_receipt_maps_gain_storage_failure_to_local_storage`
- `PlanCommandTests.Plan_gain_write_failure_returns_local_storage_after_rendering_valid_output`
- `QueryTests.Gain_is_deferred_until_receipt_completion_and_receives_exact_emitted_footprint`. Its remaining checks (no token or SQL text in what is recorded) are covered by the journal sensitivity tests.

Keep every test whose subject is the raw footprint itself (for example `ExecuteReader_open_failure_keeps_zero_raw_footprint`, `Reader_failure_receipt_preserves_exact_partial_raw_rows_and_messages`, `Watch_gain_receipt_sums_raw_footprint_across_polls`), rewritten by the table. Rename any kept test whose name says `gain` to say `raw_footprint` or `receipt` (for example `Watch_receipt_sums_raw_footprint_across_polls`).

Delete `tests/SqlHarness.Tests/GainStoreTests.cs`. Its aggregate semantics are covered by `JournalGainStoreTests`.

`tests/SqlHarness.Mcp.Tests/McpGainTests.cs`. Rewrite `One_execution_writes_one_gain_record_despite_double_complete` as `One_execution_counts_once_in_gain_despite_double_complete`. Wrap the module in a journal so the receipt completion is recorded:

```csharp
    [Fact]
    public async Task One_execution_counts_once_in_gain_despite_double_complete()
    {
        var inner = new SqlHarnessModule(() => new Dictionary<string, TargetProfile>(StringComparer.Ordinal));
        var journal = ActivityJournal.Open(new JournalConfig(), TextWriter.Null);
        var module = new JournalingModule(inner, () => journal, () => SessionIdentities.Cli(ProcessInfo.Current));
        var operation = new SqlHarnessQueryOperation(
            new SqlTargetRequest("missing-profile", new Dictionary<string, string>()),
            "SELECT 1", [], 30, 50, false, null);
        var outcome = await module.ExecuteAsync(operation);
        var emitted = McpResultAdapter.EmittedFootprint(McpResultAdapter.Adapt(outcome, "sqlharness_query"));

        var first = await outcome.EmissionReceipt!.CompleteAsync(emitted);
        var second = await outcome.EmissionReceipt!.CompleteAsync(emitted);

        Assert.Equal(first, second);
        var gain = Assert.IsType<SqlHarnessGainReport>((await inner.ExecuteAsync(new SqlHarnessGainOperation())).Report);
        Assert.Equal(1, gain.Total.Executions);
        Assert.Equal(emitted.Bytes, gain.Total.EmittedBytes);
    }
```

The class already isolates `SQLHARNESS_HOME` (see its `Dispose`), so `ActivityJournal.Open(config, log)` and the module's `JournalGainStore` both use the temporary home. Add the `using` lines the file needs.

Finally:

```bash
grep -rn "GainRecord\|IGainStore\|GainFile\|gain\.jsonl\|\.Records\b" src tests --include=*.cs
```

Expected: no matches, except unrelated `Records` properties (for example `RecordingModule`) that do not refer to gain.

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet build SqlHarness.sln -warnaserror` and `dotnet test SqlHarness.sln --filter "FullyQualifiedName!~Integration"`
Expected: build clean, all tests PASS. `JournalContractTests` still proves byte-identical CLI output with the journal on and off. If an MCP host test fails, rerun it alone before blaming this change. MCP host tests have a known intermittent handshake flake under full-suite load on Windows.

- [ ] **Step 6: Commit**

```bash
git add -A src tests README.md
git commit -m "Aggregate gain from the activity journal and stop writing gain.jsonl"
```

---

### Task 3: Retention

**Files:**
- Move: `src/SqlHarness.Dashboard/ProcessLiveness.cs` → `src/SqlHarness.Core/Journal/ProcessLiveness.cs` (namespace `SqlHarness.Core`)
- Create: `src/SqlHarness.Core/Journal/JournalRetention.cs`
- Test: `tests/SqlHarness.Tests/Journal/JournalRetentionTests.cs`

**Interfaces:**
- Consumes: v2 cascades (`operation_metrics` → `operations`; `operation_table_io`/`operation_plans` → `operation_metrics`), `JournalSchema.CurrentVersion`, `IProcessInfo`.
- Produces:
  - `internal sealed class ProcessLiveness` (same members, now in Core; the Dashboard keeps using it through its existing `InternalsVisibleTo`)
  - `public sealed record RetentionResult(bool Ran, int DeletedOperations, int DeletedPlans, int DeletedSessions)`
  - `public static class JournalRetention { RetentionResult Run(string databasePath, JournalConfig journal, IProcessInfo processes, TimeProvider time); const int BatchSize = 500; }`

- [ ] **Step 1: Move `ProcessLiveness`**

```bash
git mv src/SqlHarness.Dashboard/ProcessLiveness.cs src/SqlHarness.Core/Journal/ProcessLiveness.cs
```

Change its namespace to `SqlHarness.Core`. Add `using SqlHarness.Core;` where Dashboard files and tests now fail to find it. Then add one method, used by retention:

```csharp
    /// <summary>True when the host process of a running row still exists (pid plus start time).</summary>
    internal bool IsRunningAlive(long hostPid, string? hostStartedAt) => Status("running", hostPid, hostStartedAt) == "running";
```

Run: `dotnet build SqlHarness.sln -warnaserror` and `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Dashboard"`
Expected: PASS (pure move).

- [ ] **Step 2: Write the failing retention tests**

```csharp
using Microsoft.Data.Sqlite;
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class JournalRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private sealed class Processes(params int[] alive) : IProcessInfo
    {
        public int CurrentPid => Environment.ProcessId;
        public ProcessSnapshot? Get(int pid) => alive.Contains(pid) ? new ProcessSnapshot(pid, null, "p", null, null) : null;
    }

    private static JournalConfig Config(bool enabled = true, int maxAgeDays = 30, int maxSizeMb = 500) =>
        new() { Retention = new JournalRetentionConfig { Enabled = enabled, MaxAgeDays = maxAgeDays, MaxSizeMb = maxSizeMb } };

    private static JournalHandle Seed(JournalTempDirectory temp, DateTimeOffset at, int hostPid = 5151, bool complete = true, string key = "cli:a", BenchmarkJournalRecord? benchmark = null)
    {
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = true }, TextWriter.Null, new FixedTimeProvider(at));
        var session = JournalTestData.Session(key) with { HostPid = hostPid };
        var handle = journal.Begin(session, JournalTestData.Start())!;
        if (complete)
            journal.Complete(handle, JournalTestData.End());
        if (benchmark is not null)
            journal.RecordBenchmark(handle, benchmark);
        return handle;
    }

    private static BenchmarkJournalRecord Benchmark(string hash) => new(
        [new JournalVariantMetrics("measure", null, null, 1, null, null, null, null, null, null, null, null, null, 0, false, false, 0, [], null,
            [new JournalTableIo("T", 1, null, null, null, null, null, null, null, 0)], [new JournalPlanLink(1, 0, hash)])],
        [new JournalPlanDocument(hash, "showplan-xml", "<ShowPlanXML/>")]);

    private static long Count(JournalTempDirectory temp, string table) =>
        (long)JournalDb.Rows(temp.DatabasePath, $"SELECT COUNT(*) AS n FROM {table}")[0]["n"]!;

    [Fact]
    public void Disabled_retention_does_nothing()
    {
        using var temp = new JournalTempDirectory();
        Seed(temp, Now.AddDays(-400));

        var result = JournalRetention.Run(temp.DatabasePath, Config(enabled: false), new Processes(), new FixedTimeProvider(Now));

        Assert.False(result.Ran);
        Assert.Equal(1, Count(temp, "operations"));
    }

    [Fact]
    public void Old_operations_their_metrics_plans_and_empty_sessions_are_deleted()
    {
        using var temp = new JournalTempDirectory();
        var hashOld = new string('A', 64);
        var hashKept = new string('B', 64);
        Seed(temp, Now.AddDays(-40), key: "cli:old", benchmark: Benchmark(hashOld));
        Seed(temp, Now.AddDays(-1), key: "cli:new", benchmark: Benchmark(hashKept));

        var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(), new FixedTimeProvider(Now));

        Assert.True(result.Ran);
        Assert.Equal((1, 1, 1), (result.DeletedOperations, result.DeletedPlans, result.DeletedSessions));
        Assert.Equal(1, Count(temp, "operations"));
        Assert.Equal(1, Count(temp, "operation_metrics"));
        Assert.Equal(1, Count(temp, "operation_table_io"));
        Assert.Equal(1, Count(temp, "operation_plans"));
        Assert.Equal(hashKept, JournalDb.Rows(temp.DatabasePath, "SELECT hash FROM plans").Single()["hash"]);
        Assert.Equal("cli:new", JournalDb.Rows(temp.DatabasePath, "SELECT session_key FROM sessions").Single()["session_key"]);
    }

    [Fact]
    public void Retention_never_deletes_live_running_operations()
    {
        using var temp = new JournalTempDirectory();
        Seed(temp, Now.AddDays(-40), hostPid: 100, complete: false, key: "cli:live");
        Seed(temp, Now.AddDays(-40), hostPid: 200, complete: false, key: "cli:dead");

        var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(100), new FixedTimeProvider(Now));

        Assert.Equal(1, result.DeletedOperations);
        Assert.Equal("cli:live", JournalDb.Rows(temp.DatabasePath,
            "SELECT s.session_key FROM operations o JOIN sessions s ON s.id = o.session_id").Single()["session_key"]);
    }

    [Fact]
    public void Size_cap_deletes_oldest_first_until_under_the_limit()
    {
        using var temp = new JournalTempDirectory();
        var big = new string('x', 64 * 1024);
        for (var i = 0; i < 400; i++)
        {
            var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = true }, TextWriter.Null, new FixedTimeProvider(Now.AddMinutes(-400 + i)));
            journal.Begin(JournalTestData.Session(), JournalTestData.Start(big + i));
        }

        // Rows are "running" from a dead pid (5151 is not alive here), so they count as abandoned and deletable.
        var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 3650, maxSizeMb: 10), new Processes(), new FixedTimeProvider(Now));

        Assert.True(result.DeletedOperations > 0);
        var remaining = JournalDb.Rows(temp.DatabasePath, "SELECT MIN(id) AS lo FROM operations")[0]["lo"];
        Assert.True((long)remaining! > 1, "the oldest operations go first");
        var used = JournalDb.Rows(temp.DatabasePath, "SELECT (page_count - freelist_count) * page_size AS used FROM pragma_page_count, pragma_freelist_count, pragma_page_size")[0]["used"];
        Assert.True((long)used! <= 10L * 1024 * 1024);
    }

    [Fact]
    public void Retention_deletes_in_short_transactions()
    {
        using var temp = new JournalTempDirectory();
        for (var i = 0; i < JournalRetention.BatchSize * 2 + 10; i++)
            Seed(temp, Now.AddDays(-40).AddSeconds(i));
        var writes = 0;

        var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(), new FixedTimeProvider(Now),
            onBatchCommitted: () => writes++);

        Assert.Equal(JournalRetention.BatchSize * 2 + 10, result.DeletedOperations);
        Assert.True(writes >= 3, $"expected at least 3 batches, saw {writes}");
    }

    [Fact]
    public void Missing_or_newer_database_is_skipped()
    {
        using var temp = new JournalTempDirectory();
        Assert.False(JournalRetention.Run(temp.DatabasePath, Config(), new Processes(), new FixedTimeProvider(Now)).Ran);

        Seed(temp, Now.AddDays(-40));
        using (var c = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99;";
            cmd.ExecuteNonQuery();
        }

        Assert.False(JournalRetention.Run(temp.DatabasePath, Config(), new Processes(), new FixedTimeProvider(Now)).Ran);
        Assert.Equal(1, Count(temp, "operations"));
    }
}
```

`Run` has an internal overload with an `Action? onBatchCommitted` hook for the batch test. The public overload passes `null`.

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~JournalRetentionTests`
Expected: build FAILS with `The name 'JournalRetention' does not exist`.

- [ ] **Step 4: Implement**

`src/SqlHarness.Core/Journal/JournalRetention.cs`:

```csharp
using System.Globalization;

using Microsoft.Data.Sqlite;

namespace SqlHarness.Core;

public sealed record RetentionResult(bool Ran, int DeletedOperations, int DeletedPlans, int DeletedSessions)
{
    internal static RetentionResult Skipped { get; } = new(false, 0, 0, 0);
}

/// <summary>
/// Opt-in journal retention: operations older than maxAgeDays, then unreferenced plans and
/// empty sessions, then the oldest operations while the used size exceeds maxSizeMb, and
/// finally an incremental vacuum. A running operation is deleted only when its host process
/// is gone (abandoned). Work is done in short BEGIN IMMEDIATE batches so concurrent journal
/// writers (1 s budget) are never starved. Never throws.
/// </summary>
public static class JournalRetention
{
    public const int BatchSize = 500;

    /// <summary>Operations removed per size-cap step before the used size is measured again.</summary>
    public const int SizeStep = 100;

    public static RetentionResult Run(string databasePath, JournalConfig journal, IProcessInfo processes, TimeProvider time) =>
        Run(databasePath, journal, processes, time, onBatchCommitted: null);

    internal static RetentionResult Run(string databasePath, JournalConfig journal, IProcessInfo processes, TimeProvider time, Action? onBatchCommitted)
    {
        if (!journal.Enabled || !journal.Retention.Enabled || !File.Exists(databasePath))
            return RetentionResult.Skipped;
        try
        {
            using var connection = Connect(databasePath);
            if (Scalar(connection, "PRAGMA user_version;") != JournalSchema.CurrentVersion)
                return RetentionResult.Skipped;

            var liveness = new ProcessLiveness(processes);
            var cutoff = time.GetUtcNow().AddDays(-journal.Retention.MaxAgeDays).UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

            var operations = DeleteOperations(connection, liveness, "AND started_at < $cutoff", cutoff, int.MaxValue, onBatchCommitted);
            var (plans, sessions) = DeleteOrphans(connection, cutoff);

            var limit = (long)journal.Retention.MaxSizeMb * 1024 * 1024;
            while (UsedBytes(connection) > limit)
            {
                var deleted = DeleteOperations(connection, liveness, string.Empty, cutoff, SizeStep, onBatchCommitted);
                var (morePlans, moreSessions) = DeleteOrphans(connection, cutoff: null);
                operations += deleted;
                plans += morePlans;
                sessions += moreSessions;
                if (deleted == 0)
                    break;
            }

            Execute(connection, "PRAGMA incremental_vacuum;");
            Execute(connection, "PRAGMA wal_checkpoint(PASSIVE);");
            return new RetentionResult(true, operations, plans, sessions);
        }
        catch (Exception)
        {
            return RetentionResult.Skipped;
        }
    }

    /// <summary>Deletes oldest-first in batches; returns the number of operations deleted (at most <paramref name="max"/>).</summary>
    private static int DeleteOperations(SqliteConnection connection, ProcessLiveness liveness, string extraFilter, string cutoff, int max, Action? onBatchCommitted)
    {
        var deleted = 0;
        while (deleted < max)
        {
            var candidates = new List<(long Id, string Status, long Pid, string? Started)>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText = $"""
                    SELECT id, status, host_pid, host_started_at FROM operations
                    WHERE 1 = 1 {extraFilter} ORDER BY id LIMIT $limit;
                    """;
                select.Parameters.AddWithValue("$cutoff", cutoff);
                select.Parameters.AddWithValue("$limit", BatchSize);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                    candidates.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }

            var ids = candidates
                .Where(row => row.Status != "running" || !liveness.IsRunningAlive(row.Pid, row.Started))
                .Select(row => row.Id)
                .Take(max - deleted)
                .ToArray();
            if (ids.Length == 0)
                return deleted;

            Execute(connection, "BEGIN IMMEDIATE;");
            try
            {
                Execute(connection, $"DELETE FROM operations WHERE id IN ({string.Join(",", ids)});");
                Execute(connection, "COMMIT;");
            }
            catch
            {
                Execute(connection, "ROLLBACK;");
                throw;
            }

            deleted += ids.Length;
            onBatchCommitted?.Invoke();
            if (candidates.Count < BatchSize)
                return deleted;
        }

        return deleted;
    }

    private static (int Plans, int Sessions) DeleteOrphans(SqliteConnection connection, string? cutoff)
    {
        Execute(connection, "BEGIN IMMEDIATE;");
        try
        {
            int plans;
            using (var deletePlans = connection.CreateCommand())
            {
                deletePlans.CommandText = "DELETE FROM plans WHERE hash NOT IN (SELECT plan_hash FROM operation_plans);";
                plans = deletePlans.ExecuteNonQuery();
            }

            int sessions;
            using (var deleteSessions = connection.CreateCommand())
            {
                deleteSessions.CommandText = """
                    DELETE FROM sessions
                    WHERE NOT EXISTS (SELECT 1 FROM operations o WHERE o.session_id = sessions.id)
                      AND ($cutoff IS NULL OR last_seen < $cutoff);
                    """;
                deleteSessions.Parameters.AddWithValue("$cutoff", (object?)cutoff ?? DBNull.Value);
                sessions = deleteSessions.ExecuteNonQuery();
            }

            Execute(connection, "COMMIT;");
            return (plans, sessions);
        }
        catch
        {
            Execute(connection, "ROLLBACK;");
            throw;
        }
    }

    private static long UsedBytes(SqliteConnection connection) =>
        (Scalar(connection, "PRAGMA page_count;") - Scalar(connection, "PRAGMA freelist_count;")) * Scalar(connection, "PRAGMA page_size;");

    private static SqliteConnection Connect(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 250; PRAGMA foreign_keys = ON;");
        return connection;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
```

Notes for the implementer:
- `DELETE FROM operations` cascades through `operation_metrics` to `operation_table_io` and `operation_plans`, because `foreign_keys = ON` is set on this connection. `plans` rows are removed by `DeleteOrphans`.
- A batch of only live running rows returns early (`ids.Length == 0`). Under the size cap that stops the loop, which is correct: retention cannot delete live work.
- The size-cap loop removes `SizeStep` (100) operations per step and then measures again, so it trims to just under the cap instead of emptying the table. It treats an abandoned running row like any other. In `Size_cap_deletes_oldest_first_until_under_the_limit`, all 400 rows (about 25 MB) are abandoned (pid 5151 is not alive), so roughly 300 are deleted and the oldest go first.

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~JournalRetentionTests|FullyQualifiedName~Dashboard"`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add -A src/SqlHarness.Core src/SqlHarness.Dashboard tests/SqlHarness.Tests
git commit -m "Add opt-in journal retention"
```

---

### Task 4: Dashboard housekeeping: hourly retention and background idle shutdown

**Files:**
- Create: `src/SqlHarness.Dashboard/DashboardActivity.cs`
- Modify: `src/SqlHarness.Dashboard/DashboardServer.cs`, `src/SqlHarness.Dashboard/DashboardHost.cs`, `src/SqlHarness.Cli/Commands/DashboardCommand.cs`
- Test: `tests/SqlHarness.Tests/Dashboard/DashboardHousekeepingTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed class DashboardActivity` with `void Touch()`, `void StreamOpened()`, `void StreamClosed()`, `int OpenStreams`, `DateTimeOffset LastRequest`
  - `DashboardServerOptions.Activity` (`internal DashboardActivity? Activity { get; init; }`) and `RunningDashboard.Activity`
  - `DashboardHostOptions` gains:
    - `TimeSpan? IdleShutdown { get; init; }` (null = never)
    - `TimeSpan HousekeepingInterval { get; init; } = TimeSpan.FromMinutes(1)`
    - `TimeSpan RetentionInterval { get; init; } = TimeSpan.FromHours(1)`
    - `TimeProvider Time { get; init; } = TimeProvider.System`
  - `DashboardCommand` passes `IdleShutdown = Background ? idleShutdownHours : null` and silences output in background mode.

- [ ] **Step 1: Write the failing tests**

`tests/SqlHarness.Tests/Dashboard/DashboardHousekeepingTests.cs`:

```csharp
using System.Net;

using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class DashboardHousekeepingTests
{
    private sealed class NoBrowser : IBrowserLauncher
    {
        public void Open(Uri uri) { }
    }

    private static DashboardHostOptions Options(TempHome home, TimeSpan? idle, SqlHarnessConfig? config = null) =>
        new(home.Path, home.DatabasePath,
            new SqlHarnessConfigLoadResult(config ?? SqlHarnessConfig.Default, SqlHarnessConfigStatus.Missing, null),
            OpenBrowser: false, Quiet: true, TextWriter.Null, TextWriter.Null,
            new FakeProcesses().Alive(Environment.ProcessId, null), new NoBrowser())
        {
            PortOverride = 0,
            IdleShutdown = idle,
            HousekeepingInterval = TimeSpan.FromMilliseconds(50),
            LivePollInterval = TimeSpan.FromMilliseconds(50),
        };

    [Fact]
    public async Task Background_dashboard_exits_after_the_idle_window()
    {
        using var home = new TempHome();

        var exit = await DashboardHost.RunAsync(Options(home, TimeSpan.FromMilliseconds(400)), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, exit);
        using var again = DashboardLock.TryAcquire(home.Path);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Background_dashboard_stays_up_while_a_live_stream_is_open()
    {
        using var home = new TempHome();
        RunningDashboard? server = null;
        var started = new TaskCompletionSource();
        var run = DashboardHost.RunAsync(Options(home, TimeSpan.FromMilliseconds(400)) with
        {
            Started = s => { server = s; started.SetResult(); },
        }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = server!.BaseUri, Timeout = Timeout.InfiniteTimeSpan };
        await client.GetAsync($"/?t={server.Token}");
        var stream = await client.GetAsync("/api/live", HttpCompletionOption.ResponseHeadersRead);

        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        Assert.False(run.IsCompleted, "an open live stream keeps the dashboard up");

        stream.Dispose();
        client.Dispose();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Journal_writes_keep_the_background_dashboard_up()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var run = DashboardHost.RunAsync(Options(home, TimeSpan.FromMilliseconds(600)), CancellationToken.None);

        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            seed.Operation(JournalSeed.Session("cli:busy"));
        }

        Assert.False(run.IsCompleted, "journal writes count as activity");
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Foreground_dashboard_never_idle_exits()
    {
        using var home = new TempHome();
        using var cts = new CancellationTokenSource();

        var run = DashboardHost.RunAsync(Options(home, idle: null), cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(800));

        Assert.False(run.IsCompleted);
        cts.Cancel();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Housekeeping_runs_retention()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("cli:old"));
        var config = SqlHarnessConfig.Default with
        {
            Journal = new JournalConfig { Retention = new JournalRetentionConfig { Enabled = true, MaxAgeDays = 1 } },
        };
        using var cts = new CancellationTokenSource();
        var options = Options(home, idle: null, config) with
        {
            RetentionInterval = TimeSpan.FromMilliseconds(100),
            Time = new Journal.FixedTimeProvider(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)),
        };

        var run = DashboardHost.RunAsync(options, cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(800));
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Empty(Journal.JournalDb.Rows(home.DatabasePath, "SELECT id FROM operations"));
    }
}
```

`JournalSeed` writes at `2026-10-07`. The fixed retention clock of `2026-12-01` with `MaxAgeDays = 1` makes that row old.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~DashboardHousekeepingTests`
Expected: build FAILS with `'DashboardHostOptions' does not contain a definition for 'IdleShutdown'`.

- [ ] **Step 3: Activity tracking in the server**

`src/SqlHarness.Dashboard/DashboardActivity.cs`:

```csharp
namespace SqlHarness.Dashboard;

/// <summary>Request and live-stream activity, used by the background idle shutdown.</summary>
internal sealed class DashboardActivity(TimeProvider time)
{
    private int _openStreams;
    private long _lastRequestTicks = time.GetUtcNow().UtcTicks;

    internal int OpenStreams => Volatile.Read(ref _openStreams);

    internal DateTimeOffset LastRequest => new(Interlocked.Read(ref _lastRequestTicks), TimeSpan.Zero);

    internal void Touch() => Interlocked.Exchange(ref _lastRequestTicks, time.GetUtcNow().UtcTicks);

    internal void StreamOpened()
    {
        Interlocked.Increment(ref _openStreams);
        Touch();
    }

    internal void StreamClosed()
    {
        Interlocked.Decrement(ref _openStreams);
        Touch();
    }
}
```

`src/SqlHarness.Dashboard/DashboardServer.cs`:
- In `DashboardServerOptions`, add `internal DashboardActivity? Activity { get; init; }`.
- In `RunningDashboard`, add a constructor parameter and property `internal DashboardActivity Activity { get; }`. Pass `options.Activity ?? new DashboardActivity(TimeProvider.System)` from `StartAsync` (create it once before the port loop, so the same instance is used by `Build` and `RunningDashboard`).
- In `Build`, take the activity as a parameter and, immediately after the security middleware line (`app.Use((context, next) => DashboardSecurity.InvokeAsync(...))`), add:
  ```csharp
        // Only authenticated requests reach here, so probes from other local processes do not keep the dashboard alive.
        app.Use((context, next) =>
        {
            activity.Touch();
            return next(context);
        });
  ```
- In the `/api/live` handler, wrap the `await LiveFeed.StreamAsync(...)` call:
  ```csharp
            activity.StreamOpened();
            try
            {
                await LiveFeed.StreamAsync(context, reader, options.LivePollInterval, stream.Token);
            }
            finally
            {
                activity.StreamClosed();
            }
  ```

- [ ] **Step 4: The housekeeping loop in the host**

`src/SqlHarness.Dashboard/DashboardHost.cs`:
- Add to `DashboardHostOptions`:
  ```csharp
    /// <summary>Exit after this long without journal writes, requests or open live streams; null never (foreground).</summary>
    public TimeSpan? IdleShutdown { get; init; }

    public TimeSpan HousekeepingInterval { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan RetentionInterval { get; init; } = TimeSpan.FromHours(1);

    public TimeProvider Time { get; init; } = TimeProvider.System;
  ```
- Create the activity before starting the server, `var activity = new DashboardActivity(options.Time);`, and pass it in `new DashboardServerOptions(...) { LivePollInterval = ..., Activity = activity }`.
- Replace the `await Task.Delay(Timeout.Infinite, stop.Token);` block with:
  ```csharp
                try
                {
                    await HousekeepAsync(options, reader, activity, stop.Token);
                }
                catch (OperationCanceledException)
                {
                }
  ```
- Add:
  ```csharp
    /// <summary>
    /// Runs retention at start and every RetentionInterval, and in background mode returns
    /// once nothing happened for IdleShutdown: no journal commit (PRAGMA data_version on a
    /// held read-only connection), no authenticated request, and no open live stream.
    /// </summary>
    private static async Task HousekeepAsync(DashboardHostOptions options, JournalReader reader, DashboardActivity activity, CancellationToken ct)
    {
        var journal = options.Config.Config.Journal;
        var lastRetention = DateTimeOffset.MinValue;
        var lastJournalChange = options.Time.GetUtcNow();
        long? lastVersion = null;
        Microsoft.Data.Sqlite.SqliteConnection? watch = null;
        try
        {
            while (true)
            {
                var now = options.Time.GetUtcNow();
                if (journal.Enabled && journal.Retention.Enabled && now - lastRetention >= options.RetentionInterval)
                {
                    lastRetention = now;
                    await Task.Run(() => JournalRetention.Run(options.DatabasePath, journal, options.Processes, options.Time), ct);
                }

                watch ??= reader.OpenReadOnly();
                if (watch is not null)
                {
                    using var command = watch.CreateCommand();
                    command.CommandText = "PRAGMA data_version;";
                    var version = (long)command.ExecuteScalar()!;
                    if (lastVersion is not null && version != lastVersion)
                        lastJournalChange = now;
                    lastVersion = version;
                }

                if (options.IdleShutdown is { } idle && activity.OpenStreams == 0)
                {
                    var lastActivity = new[] { lastJournalChange, activity.LastRequest }.Max();
                    if (now - lastActivity >= idle)
                        return;
                }

                await Task.Delay(options.HousekeepingInterval, ct);
            }
        }
        finally
        {
            watch?.Dispose();
        }
    }
  ```

Retention writes to the journal and bumps `data_version` for this connection, which keeps the dashboard up for one more window. That is harmless: retention runs at most hourly.

`DashboardActivity`'s `LastRequest` uses wall time from the injected `TimeProvider`. The idle test uses `TimeProvider.System`, so request and loop clocks agree.

`src/SqlHarness.Cli/Commands/DashboardCommand.cs`. Load the config once into a local and pass it. In background mode, write nothing: the launcher closes the standard streams, so any write would fail.

```csharp
    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var config = SqlHarnessConfigLoader.Load();
        var quiet = settings.Background;
        return DashboardHost.RunAsync(
            new DashboardHostOptions(
                SqlHarnessPaths.Home,
                SqlHarnessPaths.ActivityDatabase,
                config,
                OpenBrowser: !settings.NoOpen && !settings.Background,
                Quiet: quiet,
                quiet ? TextWriter.Null : Console.Out,
                quiet ? TextWriter.Null : Console.Error,
                ProcessInfo.Current,
                new SystemBrowserLauncher())
            {
                IdleShutdown = settings.Background ? TimeSpan.FromHours(config.Config.Dashboard.IdleShutdownHours) : null,
            },
            ct);
    }
```

If the current command already builds `DashboardHostOptions` with more properties set (phase 3 or 4 additions), keep them and add `IdleShutdown` and the quiet writers.

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Dashboard"`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SqlHarness.Dashboard src/SqlHarness.Cli tests/SqlHarness.Tests/Dashboard
git commit -m "Run hourly retention in the dashboard and idle-exit a background dashboard"
```

---

### Task 5: Autostart from `mcp serve`

**Files:**
- Create: `src/SqlHarness.Dashboard/DashboardAutostart.cs`
- Modify: `src/SqlHarness.Cli/Commands/McpServeCommand.cs`
- Test: `tests/SqlHarness.Tests/Dashboard/DashboardAutostartTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record DashboardLaunchCommand(string FileName, IReadOnlyList<string> Arguments)` with `static DashboardLaunchCommand For(string processPath, string? entryAssemblyPath)`
  - `public interface IDashboardLauncher { void Launch(DashboardLaunchCommand command, string workingDirectory); }` and `public sealed class DetachedDashboardLauncher : IDashboardLauncher` with `internal static ProcessStartInfo StartInfo(DashboardLaunchCommand command, string workingDirectory, bool windows)`
  - `public enum AutostartResult { Disabled, AlreadyRunning, Launched, Failed }`
  - `public static class DashboardAutostart { AutostartResult TryStart(string home, SqlHarnessConfig config, IProcessInfo processes, IDashboardLauncher launcher, DashboardLaunchCommand command); }`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Diagnostics;

using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class DashboardAutostartTests
{
    private sealed class RecordingLauncher(Exception? failure = null) : IDashboardLauncher
    {
        public List<(DashboardLaunchCommand Command, string WorkingDirectory)> Launches { get; } = [];
        public void Launch(DashboardLaunchCommand command, string workingDirectory)
        {
            if (failure is not null)
                throw failure;
            Launches.Add((command, workingDirectory));
        }
    }

    private static readonly DashboardLaunchCommand Command = DashboardLaunchCommand.For("/opt/sqlharness/sqlharness", null);

    private static SqlHarnessConfig AutoStart(bool enabled) =>
        SqlHarnessConfig.Default with { Dashboard = new DashboardConfig { AutoStart = enabled } };

    [Fact]
    public void Disabled_autostart_launches_nothing()
    {
        using var home = new TempHome();
        var launcher = new RecordingLauncher();

        Assert.Equal(AutostartResult.Disabled, DashboardAutostart.TryStart(home.Path, AutoStart(false), new FakeProcesses(), launcher, Command));
        Assert.Empty(launcher.Launches);
    }

    [Fact]
    public void Running_dashboard_is_not_launched_again()
    {
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(4242, null, 47800, "tok"));
        var launcher = new RecordingLauncher();

        var result = DashboardAutostart.TryStart(home.Path, AutoStart(true), new FakeProcesses().Alive(4242, null), launcher, Command);

        Assert.Equal(AutostartResult.AlreadyRunning, result);
        Assert.Empty(launcher.Launches);
    }

    [Fact]
    public void Missing_dashboard_is_launched_in_the_home_directory()
    {
        using var home = new TempHome();
        var launcher = new RecordingLauncher();

        Assert.Equal(AutostartResult.Launched, DashboardAutostart.TryStart(home.Path, AutoStart(true), new FakeProcesses(), launcher, Command));
        var launch = Assert.Single(launcher.Launches);
        Assert.Equal(home.Path, launch.WorkingDirectory);
        Assert.Equal(["dashboard", "--background"], launch.Command.Arguments);
    }

    [Fact]
    public void Launch_failure_is_reported_not_thrown()
    {
        using var home = new TempHome();

        Assert.Equal(AutostartResult.Failed,
            DashboardAutostart.TryStart(home.Path, AutoStart(true), new FakeProcesses(), new RecordingLauncher(new InvalidOperationException("x")), Command));
    }

    [Theory]
    [InlineData("C:\\tools\\sqlharness.exe", "", "C:\\tools\\sqlharness.exe")]
    [InlineData("/usr/local/bin/sqlharness", null, "/usr/local/bin/sqlharness")]
    public void Launch_command_uses_the_executable_when_self_hosted(string processPath, string? entry, string fileName)
    {
        var command = DashboardLaunchCommand.For(processPath, entry);

        Assert.Equal(fileName, command.FileName);
        Assert.Equal(["dashboard", "--background"], command.Arguments);
    }

    [Theory]
    [InlineData("C:\\Program Files\\dotnet\\dotnet.exe", "D:\\src\\bin\\sqlharness.dll")]
    [InlineData("/usr/share/dotnet/dotnet", "/src/bin/sqlharness.dll")]
    public void Launch_command_uses_the_dll_when_hosted_by_dotnet(string processPath, string entry)
    {
        var command = DashboardLaunchCommand.For(processPath, entry);

        Assert.Equal(processPath, command.FileName);
        Assert.Equal([entry, "dashboard", "--background"], command.Arguments);
    }

    [Fact]
    public void Windows_launch_uses_shell_execute_without_handle_inheritance()
    {
        var start = DetachedDashboardLauncher.StartInfo(
            DashboardLaunchCommand.For("C:\\Program Files\\dotnet\\dotnet.exe", "D:\\my src\\sqlharness.dll"), "C:\\home", windows: true);

        // ShellExecuteEx never inherits handles, so the MCP server's stdio pipe stays private.
        Assert.True(start.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.Equal("\"D:\\my src\\sqlharness.dll\" dashboard --background", start.Arguments);
        Assert.Equal("C:\\home", start.WorkingDirectory);
    }

    [Fact]
    public void Unix_launch_redirects_and_closes_every_standard_stream()
    {
        var start = DetachedDashboardLauncher.StartInfo(DashboardLaunchCommand.For("/usr/local/bin/sqlharness", null), "/home/u/.sqlharness", windows: false);

        // Redirected streams replace fds 0-2 in the child; .NET marks its other fds close-on-exec.
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardInput);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Equal(["dashboard", "--background"], start.ArgumentList);
        Assert.Equal("/home/u/.sqlharness", start.WorkingDirectory);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~DashboardAutostartTests`
Expected: build FAILS with `The type or namespace name 'DashboardAutostart' could not be found`.

- [ ] **Step 3: Implement**

`src/SqlHarness.Dashboard/DashboardAutostart.cs`:

```csharp
using System.Diagnostics;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

public sealed record DashboardLaunchCommand(string FileName, IReadOnlyList<string> Arguments)
{
    private static readonly string[] DashboardArguments = ["dashboard", "--background"];

    /// <summary>
    /// The command that runs this sqlharness again: the single-file executable itself, or
    /// <c>dotnet sqlharness.dll</c> during development (process path is the dotnet host).
    /// </summary>
    public static DashboardLaunchCommand For(string processPath, string? entryAssemblyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(processPath);
        var hostedByDotnet = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        return hostedByDotnet && !string.IsNullOrEmpty(entryAssemblyPath)
            ? new DashboardLaunchCommand(processPath, [entryAssemblyPath, .. DashboardArguments])
            : new DashboardLaunchCommand(processPath, DashboardArguments);
    }

    public static DashboardLaunchCommand Current() =>
        For(Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown."),
            System.Reflection.Assembly.GetEntryAssembly()?.Location);
}

public interface IDashboardLauncher
{
    void Launch(DashboardLaunchCommand command, string workingDirectory);
}

/// <summary>
/// Starts the dashboard as an independent process that holds none of this process's
/// standard handles: an MCP client must still see EOF on the server's stdout when the
/// server exits. Windows uses ShellExecuteEx (no handle inheritance, hidden window);
/// Unix redirects stdin/stdout/stderr to pipes this process closes immediately.
/// </summary>
public sealed class DetachedDashboardLauncher : IDashboardLauncher
{
    public void Launch(DashboardLaunchCommand command, string workingDirectory)
    {
        var windows = OperatingSystem.IsWindows();
        using var process = Process.Start(StartInfo(command, workingDirectory, windows))
            ?? throw new InvalidOperationException("The dashboard process did not start.");
        if (!windows)
        {
            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();
        }
    }

    internal static ProcessStartInfo StartInfo(DashboardLaunchCommand command, string workingDirectory, bool windows)
    {
        if (windows)
        {
            return new ProcessStartInfo(command.FileName)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = string.Join(" ", command.Arguments.Select(Quote)),
                WorkingDirectory = workingDirectory,
            };
        }

        var start = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in command.Arguments)
            start.ArgumentList.Add(argument);
        return start;
    }

    private static string Quote(string argument) =>
        argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0 ? argument : "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

public enum AutostartResult
{
    Disabled,
    AlreadyRunning,
    Launched,
    Failed,
}

public static class DashboardAutostart
{
    /// <summary>
    /// Launches a background dashboard when dashboard.autoStart is on and none is published
    /// for this home. Never throws and never waits for the dashboard: a dashboard that loses
    /// the lock race exits on its own, and a later mcp serve retries.
    /// </summary>
    public static AutostartResult TryStart(string home, SqlHarnessConfig config, IProcessInfo processes, IDashboardLauncher launcher, DashboardLaunchCommand command)
    {
        if (!config.Dashboard.AutoStart)
            return AutostartResult.Disabled;
        try
        {
            if (DashboardLock.ReadRunning(home, processes) is not null)
                return AutostartResult.AlreadyRunning;
            Directory.CreateDirectory(home);
            launcher.Launch(command, home);
            return AutostartResult.Launched;
        }
        catch (Exception)
        {
            return AutostartResult.Failed;
        }
    }
}
```

The working directory is the SQLHarness home, so the dashboard process never pins the agent's project directory (which matters on Windows, where a process's current directory cannot be deleted).

`src/SqlHarness.Cli/Commands/McpServeCommand.cs`. In `ExecuteAsync`, after all argument validation succeeded and immediately before `var options = new SqlHarness.Mcp.McpServerOptions`, add:

```csharp
        // Side effects of starting an MCP server; neither touches stdout nor delays the handshake.
        var config = SqlHarnessConfigLoader.Load().Config;
        if (DashboardAutostart.TryStart(SqlHarnessPaths.Home, config, ProcessInfo.Current, new DetachedDashboardLauncher(), DashboardLaunchCommand.Current())
            is AutostartResult.Failed)
            console.Error.WriteLine("sqlharness-mcp: dashboard autostart failed; start it with: sqlharness dashboard");
        _ = Task.Run(() => JournalRetention.Run(SqlHarnessPaths.ActivityDatabase, config.Journal, ProcessInfo.Current, TimeProvider.System));
```

Add `using SqlHarness.Dashboard;` and `using SqlHarness.Core;` if missing. The config warning for an invalid file is still printed once, by `McpHost`.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~DashboardAutostartTests` and then `dotnet test tests/SqlHarness.Mcp.Tests`
Expected: PASS. MCP tests run with the default config (`autoStart: false`), so no process is launched. Rerun any MCP host test that fails alone before suspecting this change (known flake).

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Dashboard/DashboardAutostart.cs src/SqlHarness.Cli/Commands/McpServeCommand.cs tests/SqlHarness.Tests/Dashboard/DashboardAutostartTests.cs
git commit -m "Autostart a detached background dashboard from mcp serve"
```

---

### Task 6: Documentation, spec, gates, and manual autostart check

**Files:**
- Modify: `README.md`, `AGENTS.md`, `docs/mcp.md`, `docs/superpowers/specs/2026-10-06-activity-dashboard-design.md`

- [ ] **Step 1: Documentation**

`README.md`, `### Dashboard` subsection. Replace the sentence saying `retention` and `dashboard` keys "have no effect until later releases" with:

````markdown
Optional behavior in `~/.sqlharness/config.json`:

```json
{
  "journal": { "retention": { "enabled": true, "maxAgeDays": 30, "maxSizeMb": 500 } },
  "dashboard": { "autoStart": true, "idleShutdownHours": 8 }
}
```

- `journal.retention` deletes operations older than `maxAgeDays` (and the oldest ones while the database exceeds `maxSizeMb`), then unused plans and empty sessions. It never deletes an operation that is still running. It runs when the dashboard starts, every hour while it runs, and when `mcp serve` starts. Off by default.
- `dashboard.autoStart` makes every `mcp serve` start `sqlharness dashboard --background` when no dashboard is running for this home. The background dashboard exits after `idleShutdownHours` without journal writes, requests, or open browser tabs; the next `mcp serve` starts it again. A dashboard started by hand never idle-exits. Off by default. Open the running dashboard with `sqlharness dashboard`.
````

`AGENTS.md`, in the dashboard bullet under **Safety contract**, append: "With `dashboard.autoStart`, `mcp serve` launches it detached (`--background`) without touching MCP stdout; agents still never call its API."

`docs/mcp.md`, in the stdout/stderr section, add: "With `dashboard.autoStart: true`, startup also launches `sqlharness dashboard --background` as a detached process that inherits none of the server's standard handles, and starts one journal retention pass in the background; neither writes to stdout."

Spec `docs/superpowers/specs/2026-10-06-activity-dashboard-design.md`:
- **Non-goals:**
  - Replace "IO/plan statistics for `query`. Enabling them would change its execution, so it needs a separate decision." with "IO/plan statistics for `query`: rejected (2026-10-07); `measure` and `compare` are the measurement commands."
  - Remove "Migrating `gain.jsonl` into SQLite."
- **Decisions table**, add the row: `| Gain statistics | Aggregated from the journal (operations with an emitted footprint); gain.jsonl is no longer written or read; no migration of old data; gain write failures never change an exit code |`.
- **Dashboard server:**
  - Change the idle shutdown sentence to: "Idle shutdown applies to `--background` (autostarted) only: the server exits after `idleShutdownHours` with no journal commits (`PRAGMA data_version`), no authenticated requests, and no open live streams."
  - Change "never `running`" in the retention list to "never a running operation whose host process is alive (abandoned rows are deleted like finished ones)".
  - Add: "Autostart launches the dashboard detached: ShellExecute with a hidden window on Windows, closed redirected stdio on Unix, the SQLHarness home as working directory."

- [ ] **Step 2: Gates**

```powershell
pwsh ./scripts/verify.ps1
pwsh ./scripts/verify-linux.ps1
```

Expected: both `OK`. On a failure confined to MCP host tests, rerun those tests alone first (known flake).

- [ ] **Step 3: Manual autostart check (Windows, then WSL)**

```powershell
$env:SQLHARNESS_HOME = Join-Path ([IO.Path]::GetTempPath()) "sqlharness-autostart"
New-Item -ItemType Directory -Force $env:SQLHARNESS_HOME | Out-Null
'{ "dashboard": { "autoStart": true } }' | Set-Content (Join-Path $env:SQLHARNESS_HOME "config.json")
# Any closed profile works; the server exits on stdin EOF right after startup.
'' | dotnet run --project src/SqlHarness.Cli -- mcp serve --request-scope --allow-profile <a profile in your targets.json>
Start-Sleep -Seconds 3
Get-Content (Join-Path $env:SQLHARNESS_HOME "dashboard.json")
dotnet run --project src/SqlHarness.Cli -- dashboard --no-open
```

Expected:
- `mcp serve` exits promptly on EOF. The pipeline returns, which proves no inherited stdout held the pipe open.
- `dashboard.json` exists with a live pid.
- The second command prints the running dashboard's URL instead of starting another one.

Stop the background dashboard (`Stop-Process -Id <pid from dashboard.json>`), then remove the temporary home and unset `SQLHARNESS_HOME`. Repeat the same steps inside WSL from the gate clone with `dotnet run`.

- [ ] **Step 4: Commit**

```bash
git add README.md AGENTS.md docs/mcp.md docs/superpowers/specs/2026-10-06-activity-dashboard-design.md
git commit -m "Document retention, idle shutdown, autostart and journal-based gain"
```

---

## As built

Accepted deviations from the tasks above:

- **Tasks 1–2:** `JournalGainStore` returns zeros (with `journalEnabled: true`) for a journal below schema v3 or one without the `operations` table or footprint columns, instead of failing. Stopwatch locals left unused by the receipt change were dropped.
- **Task 3:** `ProcessLiveness` moved to Core with a `failedLookupIsAlive` flag. Retention passes `true`: a lookup that throws keeps the running row. `LinuxProcessInfo` maps unreadable `/proc` entries to "not found", so they still read as dead (documented limitation); macOS (`SelfOnlyProcessInfo`) treats every row as alive. Retention deletes abandoned running rows (host process gone) like finished ones and never a live one; this refines the spec's "never `running`". Deletes page by id past live running rows. Every delete (operations, orphan plans, orphan sessions) runs in short `BEGIN IMMEDIATE` batches of at most 500 rows. `incremental_vacuum(1024)` runs in short steps and is skipped when `auto_vacuum` is not `INCREMENTAL`; existing databases are not converted. A run that fails midway reports the counts it already committed.
- **Task 4:** Idle decisions use an injectable `TimeProvider`. A `data_version` read error reconnects on the next pass instead of stopping the dashboard. The dashboard's own retention commits are not counted as activity. Ctrl+C waits for an in-flight retention pass (short batches).
- **Task 5:** Launch command detection splits the process path on both `/` and `\`. IL3000 is suppressed for the single-file `Assembly.Location` read. Autostart and the startup retention pass run from an `McpHost` `started` hook after startup validation (an invalid startup configuration never launches anything) and before the transport starts. The launcher is injectable (`IDashboardLauncher`); `McpDashboardAutostartTests` pins silent stdout. Known limitation: the autostarted dashboard is not placed in a new session or process group, so Ctrl+C/SIGHUP to the MCP client's process group (or closing its console on Windows) also stops it; the next `mcp serve` relaunches it. Documented in `README.md`, `AGENTS.md`, `docs/mcp.md`, and the spec.
- **Task 6:** `verify-linux.ps1` (WSL) was not run; `verify.ps1` ran on a Linux container, exercising the same stages on Linux. The manual autostart check ran on Linux only (framework-dependent `dotnet sqlharness.dll` and a linux-x64 single-file publish); the Windows check was not run.

## Not in this phase

These remain separate projects, each needing its own spec:
- Correlating sessions with Claude/Codex transcripts.
- Mutation notifications and approval (see `2026-09-26-windows-approval-tray-design.md`).
- Graphical plan diagrams.
- UI restyling.
