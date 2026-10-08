# Activity Journal — Phase 1 (journal core and session identity) Implementation Plan

**Status (2026-10-08):** DONE — journal on main (b262eb4, 2026-10-06; `src/SqlHarness.Core/Journal/`). Checkboxes below were not maintained during execution.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every CLI command and MCP tool call that goes through `ISqlHarnessModule` is recorded in a local SQLite journal (`~/.sqlharness/data/activity.db`), grouped into implicitly identified agent sessions. Agent-visible output and exit codes do not change.

**Architecture:** A `JournalingModule` decorator wraps `ISqlHarnessModule` in the CLI composition root and in every MCP module the host builds. It writes a `running` row before delegating, completes the row from the outcome, and adds token footprints when the emission receipt is completed. Session identity is resolved lazily. MCP uses `McpServer.ClientInfo` plus a per-process UUID. The CLI walks ancestor processes until it finds a known agent executable. Journal failures are swallowed with one stderr line per process. A new `~/.sqlharness/config.json` controls the journal and fails closed.

**Tech Stack:** .NET 8, C#, `Microsoft.Data.Sqlite` 10.0.8 (net8.0 asset, bundles `e_sqlite3`), xUnit, ModelContextProtocol 2.2.0.

**Spec:** `docs/superpowers/specs/2026-10-06-activity-dashboard-design.md`. This plan implements its "Implementation phases → 1". Phases 2–5 (metrics capture, dashboard server, SPA, autostart and retention) get their own plans.

## Global Constraints

- Target framework stays `net8.0`. `TreatWarningsAsErrors` is on, and `dotnet format --verify-no-changes` must pass.
- Agent-visible output (CLI human, `--json`, `--json-summary`, NDJSON, MCP tool results) and exit codes are byte-identical with the journal enabled or disabled.
- Journal failures never change an operation's exit code. They write at most one stderr line per process, and that line contains no SQL, values, paths, or exception text.
- Never store `--param` / `--param-set` values, passwords, tokens, connection strings, or result cells. SQL text is stored only when `journal.storeSensitive` is `true`.
- `config.json` fails closed: an invalid file behaves exactly as defaults (`journal.enabled: true`, `storeSensitive: false`, retention off, autostart off).
- MCP stdout carries protocol frames only. Journal diagnostics go to the host's stderr writer.
- Session identity uses only MCP `clientInfo` and the process tree. No environment variables, hooks, or flags.
- Owner-only file modes for `activity.db*` and its directory on Unix. Windows inherits the user-profile ACL.
- Tests touching `SQLHARNESS_HOME` use `[Collection(SqlHarnessHomeCollection.Name)]` (or the MCP test equivalent already used in that project) and restore the variable.
- Both gates must be green before the work is done: `pwsh ./scripts/verify.ps1` and `pwsh ./scripts/verify-linux.ps1`.

## Review Focus

1. **A busy or locked `activity.db`** (two agents, or a dashboard reading during a write) must not stall an agent's query for more than about 1 s or change its result. Pinned by Task 2, `Begin_returns_null_quickly_when_the_database_write_lock_is_held`.
2. **An invalid `config.json` that says `storeSensitive: true`** must not store SQL. Pinned by Task 1, `Invalid_config_never_enables_store_sensitive`.
3. **A cancelled operation** (Ctrl+C, MCP cancellation) must not leave a `running` row and must still rethrow. Pinned by Task 5, `Cancelled_operation_is_completed_as_cancelled_and_rethrown`.
4. **A process tree with a cycle, a vanished parent, or access denied** must yield an `unknown` identity rather than an exception or hang. Pinned by Task 4, `Cycle_in_parent_chain_terminates_as_unknown` and `Missing_parent_yields_unknown_keyed_by_self`.
5. **An `activity.db` that is not a database** (truncated or overwritten) must be quarantined and recreated, never crash the CLI. Pinned by Task 2, `Corrupt_database_is_quarantined_and_recreated`.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/SqlHarness.Core/SqlHarnessConfig.cs` (new) | Config records, strict loader, fail-closed defaults |
| `src/SqlHarness.Core/SqlHarnessPaths.cs` (modify) | `ConfigFile`, `ActivityDatabase` |
| `src/SqlHarness.Core/Journal/JournalModels.cs` (new) | `SessionIdentity`, `OperationStart`, `OperationEnd`, `JournalHandle`, `JournalTransport` |
| `src/SqlHarness.Core/Journal/IActivityJournal.cs` (new) | Interface plus `NullActivityJournal` |
| `src/SqlHarness.Core/Journal/ActivityJournal.cs` (new) | SQLite store: open, migrate, quarantine, begin/complete/emission |
| `src/SqlHarness.Core/Journal/JournalSchema.cs` (new) | Schema v1 DDL and version constant |
| `src/SqlHarness.Core/Journal/OwnerOnlyFiles.cs` (new) | Unix owner-only modes |
| `src/SqlHarness.Core/Journal/OperationJournalDescriber.cs` (new) | Operation → `OperationStart`, outcome → `OperationEnd` |
| `src/SqlHarness.Core/Journal/ProcessInfo.cs` (new) | `IProcessInfo`, `ProcessSnapshot`, Windows/Linux/null implementations |
| `src/SqlHarness.Core/Journal/SessionIdentities.cs` (new) | Agent classification, CLI and MCP identity |
| `src/SqlHarness.Core/Journal/JournalingModule.cs` (new) | `ISqlHarnessModule` decorator |
| `src/SqlHarness.Core/Contracts.cs` (modify) | `SqlHarnessEmissionReceipt.RawFootprint` |
| `src/SqlHarness.Core/SqlHarnessModule.cs` (modify) | Set `RawFootprint` in `WithReceipt` |
| `src/SqlHarness.Core/SqlHarness.Core.csproj`, `Directory.Packages.props` (modify) | Sqlite package |
| `src/SqlHarness.Cli/Program.cs` (modify) | CLI composition with journal |
| `src/SqlHarness.Cli/Commands/DoctorCommand.cs` (modify) | Config/journal diagnostics |
| `src/SqlHarness.Mcp/McpProcessContext.cs`, `McpHost.cs`, `Tools/McpToolCatalog.cs`, `Tools/McpRequestToolHandlers.cs` (modify) | MCP decoration and identity |
| `tests/SqlHarness.Tests/Journal/*.cs` (new) | Core tests |
| `tests/SqlHarness.Mcp.Tests/McpJournalTests.cs` (new) | MCP identity test |
| `tests/SqlHarness.Tests/Cli/DoctorJournalConfigTests.cs` (new) | Doctor config diagnostics |
| `AGENTS.md`, `README.md`, `docs/mcp.md` (modify) | Documentation |

---

### Task 1: Config file and paths

**Files:**
- Create: `src/SqlHarness.Core/SqlHarnessConfig.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessPaths.cs`
- Test: `tests/SqlHarness.Tests/Journal/SqlHarnessConfigTests.cs`

**Interfaces:**
- Produces: `SqlHarnessConfig { JournalConfig Journal; DashboardConfig Dashboard; static Default }`, `JournalConfig { bool Enabled = true; bool StoreSensitive; JournalRetentionConfig Retention }`, `SqlHarnessConfigStatus { Missing, Valid, Invalid }`, `SqlHarnessConfigLoadResult(SqlHarnessConfig Config, SqlHarnessConfigStatus Status, string? Warning)`, `SqlHarnessConfigLoader.Load()` / `Load(string path)`, `SqlHarnessPaths.ConfigFile`, `SqlHarnessPaths.ActivityDatabase`.

- [ ] **Step 1: Write the failing tests**

```csharp
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class SqlHarnessConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlharness-config-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public SqlHarnessConfigTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Missing_file_yields_defaults_without_warning()
    {
        var result = SqlHarnessConfigLoader.Load(ConfigPath);

        Assert.Equal(SqlHarnessConfigStatus.Missing, result.Status);
        Assert.Null(result.Warning);
        Assert.True(result.Config.Journal.Enabled);
        Assert.False(result.Config.Journal.StoreSensitive);
        Assert.False(result.Config.Journal.Retention.Enabled);
        Assert.False(result.Config.Dashboard.AutoStart);
        Assert.Equal(47800, result.Config.Dashboard.Port);
    }

    [Fact]
    public void Valid_file_is_read()
    {
        File.WriteAllText(ConfigPath, """
            {
              "journal": { "enabled": true, "storeSensitive": true,
                           "retention": { "enabled": true, "maxAgeDays": 7, "maxSizeMb": 100 } },
              "dashboard": { "autoStart": true, "port": 48000, "idleShutdownHours": 2 }
            }
            """);

        var result = SqlHarnessConfigLoader.Load(ConfigPath);

        Assert.Equal(SqlHarnessConfigStatus.Valid, result.Status);
        Assert.True(result.Config.Journal.StoreSensitive);
        Assert.Equal(7, result.Config.Journal.Retention.MaxAgeDays);
        Assert.Equal(48000, result.Config.Dashboard.Port);
    }

    [Fact]
    public void Partial_file_keeps_defaults_for_omitted_sections()
    {
        File.WriteAllText(ConfigPath, """{ "journal": { "storeSensitive": true } }""");

        var result = SqlHarnessConfigLoader.Load(ConfigPath);

        Assert.Equal(SqlHarnessConfigStatus.Valid, result.Status);
        Assert.True(result.Config.Journal.Enabled);
        Assert.True(result.Config.Journal.StoreSensitive);
        Assert.False(result.Config.Dashboard.AutoStart);
    }

    [Theory]
    [InlineData("""{ "journal": { "storeSensitive": true }, "unknown": 1 }""")]
    [InlineData("""{ "journal": { "storeSensitive": true, "port": 1 } }""")]
    [InlineData("""{ "journal": { "storeSensitive": true }, "dashboard": { "port": 80 } }""")]
    [InlineData("""{ "journal": { "storeSensitive": true, "retention": { "maxAgeDays": 0 } } }""")]
    [InlineData("""{ "journal": { "storeSensitive": "true" } }""")]
    [InlineData("""{ "journal": null }""")]
    [InlineData("""{ "journal": { "storeSensitive": true }, } """)]
    [InlineData("""// comment
        { "journal": { "storeSensitive": true } }""")]
    [InlineData("not json")]
    public void Invalid_config_never_enables_store_sensitive(string content)
    {
        File.WriteAllText(ConfigPath, content);

        var result = SqlHarnessConfigLoader.Load(ConfigPath);

        Assert.Equal(SqlHarnessConfigStatus.Invalid, result.Status);
        Assert.NotNull(result.Warning);
        Assert.False(result.Config.Journal.StoreSensitive);
        Assert.False(result.Config.Dashboard.AutoStart);
        Assert.True(result.Config.Journal.Enabled);
    }

    [Fact]
    public void Oversized_file_is_invalid()
    {
        File.WriteAllText(ConfigPath, "{ \"journal\": { \"storeSensitive\": true } }" + new string(' ', 70_000));

        Assert.Equal(SqlHarnessConfigStatus.Invalid, SqlHarnessConfigLoader.Load(ConfigPath).Status);
    }

    [Fact]
    public void Warning_does_not_echo_file_content_or_path()
    {
        File.WriteAllText(ConfigPath, """{ "secretish": "SQLH_CONFIG_MARKER" }""");

        var warning = SqlHarnessConfigLoader.Load(ConfigPath).Warning!;

        Assert.DoesNotContain("SQLH_CONFIG_MARKER", warning);
        Assert.DoesNotContain(_dir, warning);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~SqlHarnessConfigTests`
Expected: build FAILS with `The type or namespace name 'SqlHarnessConfigLoader' could not be found`.

- [ ] **Step 3: Implement**

`src/SqlHarness.Core/SqlHarnessPaths.cs`, add two properties next to `GainFile`:

```csharp
    public static string ConfigFile => Path.Combine(Home, "config.json");
    public static string ActivityDatabase => Path.Combine(Home, "data", "activity.db");
```

`src/SqlHarness.Core/SqlHarnessConfig.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlHarness.Core;

public sealed record JournalRetentionConfig
{
    public bool Enabled { get; init; }
    public int MaxAgeDays { get; init; } = 30;
    public int MaxSizeMb { get; init; } = 500;
}

public sealed record JournalConfig
{
    public bool Enabled { get; init; } = true;
    public bool StoreSensitive { get; init; }
    public JournalRetentionConfig Retention { get; init; } = new();
}

public sealed record DashboardConfig
{
    public bool AutoStart { get; init; }
    public int Port { get; init; } = 47800;
    public int IdleShutdownHours { get; init; } = 8;
}

/// <summary>Operator settings from <c>~/.sqlharness/config.json</c>; tool behavior only, never targets.</summary>
public sealed record SqlHarnessConfig
{
    public JournalConfig Journal { get; init; } = new();
    public DashboardConfig Dashboard { get; init; } = new();

    public static SqlHarnessConfig Default { get; } = new();
}

public enum SqlHarnessConfigStatus
{
    Missing,
    Valid,
    Invalid,
}

public sealed record SqlHarnessConfigLoadResult(
    SqlHarnessConfig Config,
    SqlHarnessConfigStatus Status,
    string? Warning);

/// <summary>
/// Strict, fail-closed reader: any unreadable, malformed, unknown-field, or
/// out-of-range file yields <see cref="SqlHarnessConfig.Default"/>, so an
/// invalid file can never enable sensitive storage or autostart.
/// </summary>
public static class SqlHarnessConfigLoader
{
    internal const int MaximumBytes = 64 * 1024;

    internal const string InvalidWarning =
        "sqlharness: config.json is invalid; using defaults (journal hash-only, no autostart, no retention).";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static SqlHarnessConfigLoadResult Load() => Load(SqlHarnessPaths.ConfigFile);

    public static SqlHarnessConfigLoadResult Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new SqlHarnessConfigLoadResult(SqlHarnessConfig.Default, SqlHarnessConfigStatus.Missing, null);
            if (new FileInfo(path).Length > MaximumBytes)
                return Invalid();

            var config = JsonSerializer.Deserialize<SqlHarnessConfig>(File.ReadAllBytes(path), Options);
            return config is not null && IsComplete(config) && IsInRange(config)
                ? new SqlHarnessConfigLoadResult(config, SqlHarnessConfigStatus.Valid, null)
                : Invalid();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return Invalid();
        }
    }

    private static SqlHarnessConfigLoadResult Invalid() =>
        new(SqlHarnessConfig.Default, SqlHarnessConfigStatus.Invalid, InvalidWarning);

    // Explicit JSON nulls bypass initializers; treat them as invalid.
    private static bool IsComplete(SqlHarnessConfig config) =>
        config.Journal is not null && config.Journal.Retention is not null && config.Dashboard is not null;

    private static bool IsInRange(SqlHarnessConfig config) =>
        config.Journal.Retention.MaxAgeDays is >= 1 and <= 3650
        && config.Journal.Retention.MaxSizeMb is >= 10 and <= 102_400
        && config.Dashboard.Port is >= 1024 and <= 65535
        && config.Dashboard.IdleShutdownHours is >= 1 and <= 168;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~SqlHarnessConfigTests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Core/SqlHarnessConfig.cs src/SqlHarness.Core/SqlHarnessPaths.cs tests/SqlHarness.Tests/Journal/SqlHarnessConfigTests.cs
git commit -m "Add fail-closed config.json loader"
```

---

### Task 2: SQLite journal store

**Files:**
- Modify: `Directory.Packages.props`, `src/SqlHarness.Core/SqlHarness.Core.csproj`
- Create: `src/SqlHarness.Core/Journal/JournalModels.cs`, `IActivityJournal.cs`, `JournalSchema.cs`, `OwnerOnlyFiles.cs`, `ActivityJournal.cs`
- Test: `tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs`, `tests/SqlHarness.Tests/Journal/JournalTestSupport.cs`

**Interfaces:**
- Consumes: `JournalConfig` (Task 1), `SqlHarnessPaths.ActivityDatabase`, `OutputFootprint` (existing, `EstimatedTokenCount`).
- Produces:
  - `enum JournalTransport { Cli, Mcp }`
  - `sealed record SessionIdentity(string SessionKey, string AgentKind, string Source, JournalTransport Transport, string? ClientName, string? ClientVersion, string? McpMode, int? AgentPid, DateTimeOffset? AgentStartedAt, int HostPid, DateTimeOffset? HostStartedAt, string? Cwd)`
  - `sealed record OperationStart(string Operation, string? Profile, IReadOnlyDictionary<string, string>? Vars, bool MutationRequested, string? SqlHash, string? CandidateSqlHash, string? SqlText, string? CandidateSqlText)`
  - `sealed record OperationEnd(string Status, int ExitCode, string? ErrorKind, long DurationMilliseconds, string? Engine, string? Server, string? Database, int? ResultSets, long? RowsReturned)`
  - `sealed record JournalHandle(long OperationId)`
  - `interface IActivityJournal { JournalHandle? Begin(SessionIdentity, OperationStart); void Complete(JournalHandle?, OperationEnd); void RecordEmission(JournalHandle?, OutputFootprint? raw, OutputFootprint emitted); }`
  - `NullActivityJournal.Instance`
  - `ActivityJournal.Open(JournalConfig config, TextWriter log)`, plus `internal ActivityJournal.Open(string path, JournalConfig config, TextWriter log, TimeProvider time)`
  - `JournalSchema.CurrentVersion = 1`

- [ ] **Step 1: Add the package**

`Directory.Packages.props`, inside the `<ItemGroup>` and in alphabetical order:

```xml
    <PackageVersion Include="Microsoft.Data.Sqlite" Version="10.0.8" />
```

`src/SqlHarness.Core/SqlHarness.Core.csproj`, in the package `<ItemGroup>`:

```xml
    <PackageReference Include="Microsoft.Data.Sqlite" />
```

Run: `dotnet restore SqlHarness.sln`
Expected: restore succeeds. **STOP** if restore reports that no `net8.0` asset exists or reports a version conflict with `SQLitePCLRaw`, and report the error instead of changing versions.

- [ ] **Step 2: Write test support and the failing tests**

`tests/SqlHarness.Tests/Journal/JournalTestSupport.cs`:

```csharp
using Microsoft.Data.Sqlite;
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

internal sealed class JournalTempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "sqlharness-journal-" + Guid.NewGuid().ToString("N"));

    public JournalTempDirectory() => Directory.CreateDirectory(Path);

    public string DatabasePath => System.IO.Path.Combine(Path, "data", "activity.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(Path, true);
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class JournalTestData
{
    internal static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    internal static SessionIdentity Session(string key = "cli:test") => new(
        key, "claude", "process-tree", JournalTransport.Cli, null, null, null,
        4242, T0.AddHours(-1), 5151, T0, "/work");

    internal static OperationStart Start(string sql = "SELECT 1") => new(
        "query", "local", new Dictionary<string, string> { ["tenant"] = "acme" }, false,
        "sha256:abc", null, sql, null);

    internal static OperationEnd End(string status = "succeeded", int exitCode = 0) => new(
        status, exitCode, null, 12, "sqlserver", "srv", "db", 1, 3);
}

internal static class JournalDb
{
    internal static List<Dictionary<string, object?>> Rows(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Raw bytes of the database and its WAL/SHM side files, for leak scans.</summary>
    internal static byte[] AllBytes(string path)
    {
        using var buffer = new MemoryStream();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            if (!File.Exists(file))
                continue;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.CopyTo(buffer);
        }

        return buffer.ToArray();
    }

    internal static bool Contains(byte[] haystack, string marker) =>
        haystack.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(marker)) >= 0;
}
```

`tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs`:

```csharp
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class ActivityJournalTests
{
    private static IActivityJournal Open(JournalTempDirectory temp, TextWriter log, bool storeSensitive = false, FixedTimeProvider? time = null) =>
        ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = storeSensitive }, log,
            time ?? new FixedTimeProvider(JournalTestData.T0));

    [Fact]
    public void Open_creates_schema_version_1_in_wal_mode()
    {
        using var temp = new JournalTempDirectory();

        Assert.IsType<ActivityJournal>(Open(temp, TextWriter.Null));

        Assert.Equal(1L, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version")[0]["user_version"]);
        Assert.Equal("wal", JournalDb.Rows(temp.DatabasePath, "PRAGMA journal_mode")[0]["journal_mode"]);
    }

    [Fact]
    public void Disabled_config_returns_null_journal_and_creates_no_file()
    {
        using var temp = new JournalTempDirectory();

        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig { Enabled = false }, TextWriter.Null, TimeProvider.System);

        Assert.Same(NullActivityJournal.Instance, journal);
        Assert.False(File.Exists(temp.DatabasePath));
    }

    [Fact]
    public void Begin_complete_and_emission_write_one_operation_row()
    {
        using var temp = new JournalTempDirectory();
        var time = new FixedTimeProvider(JournalTestData.T0);
        var journal = Open(temp, TextWriter.Null, time: time);

        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        Assert.NotNull(handle);
        var running = JournalDb.Rows(temp.DatabasePath, "SELECT * FROM operations")[0];
        Assert.Equal("running", running["status"]);
        Assert.Equal("query", running["operation"]);
        Assert.Equal("local", running["profile"]);
        Assert.Equal("{\"tenant\":\"acme\"}", running["vars_json"]);
        Assert.Equal("sha256:abc", running["sql_hash"]);
        Assert.Null(running["sql_text"]);
        Assert.Equal("2026-10-06T09:00:00.000Z", running["started_at"]);

        time.Now = JournalTestData.T0.AddSeconds(2);
        journal.Complete(handle, JournalTestData.End());
        journal.RecordEmission(handle, new OutputFootprint(400, 10), new OutputFootprint(40, 2));

        var done = JournalDb.Rows(temp.DatabasePath, "SELECT * FROM operations")[0];
        Assert.Equal("succeeded", done["status"]);
        Assert.Equal(0L, done["exit_code"]);
        Assert.Equal("srv", done["server"]);
        Assert.Equal(3L, done["rows_returned"]);
        Assert.Equal("2026-10-06T09:00:02.000Z", done["finished_at"]);
        Assert.Equal(100L, done["raw_tokens"]);
        Assert.Equal(10L, done["emitted_tokens"]);
        var session = JournalDb.Rows(temp.DatabasePath, "SELECT * FROM sessions")[0];
        Assert.Equal("claude", session["agent_kind"]);
        Assert.Equal("cli", session["transport"]);
        Assert.Equal("2026-10-06T09:00:02.000Z", session["last_seen"]);
    }

    [Fact]
    public void Sql_text_is_stored_only_when_store_sensitive()
    {
        using var hashOnly = new JournalTempDirectory();
        using var sensitive = new JournalTempDirectory();

        Open(hashOnly, TextWriter.Null).Begin(JournalTestData.Session(), JournalTestData.Start("SELECT 'SQLH_SQL_MARKER'"));
        Open(sensitive, TextWriter.Null, storeSensitive: true).Begin(JournalTestData.Session(), JournalTestData.Start("SELECT 'SQLH_SQL_MARKER'"));

        Assert.False(JournalDb.Contains(JournalDb.AllBytes(hashOnly.DatabasePath), "SQLH_SQL_MARKER"));
        Assert.True(JournalDb.Contains(JournalDb.AllBytes(sensitive.DatabasePath), "SQLH_SQL_MARKER"));
    }

    [Fact]
    public void Parallel_begins_from_one_session_share_one_session_row()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, TextWriter.Null);

        Parallel.For(0, 8, _ => Assert.NotNull(journal.Begin(JournalTestData.Session(), JournalTestData.Start())));

        Assert.Single(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM sessions"));
        Assert.Equal(8, JournalDb.Rows(temp.DatabasePath, "SELECT id FROM operations").Count);
    }

    [Fact]
    public void Begin_returns_null_quickly_when_the_database_write_lock_is_held()
    {
        using var temp = new JournalTempDirectory();
        var log = new StringWriter();
        var journal = Open(temp, log);
        using var holder = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        holder.Open();
        using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
        }

        var stopwatch = Stopwatch.StartNew();
        var first = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        var second = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        stopwatch.Stop();

        Assert.Null(first);
        Assert.Null(second);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
        var lines = log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.DoesNotContain(temp.Path, lines[0]);
    }

    [Fact]
    public void Complete_and_emission_with_null_handle_are_no_ops()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, TextWriter.Null);

        journal.Complete(null, JournalTestData.End());
        journal.RecordEmission(null, null, new OutputFootprint(1, 1));

        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM operations"));
    }

    [Fact]
    public void Corrupt_database_is_quarantined_and_recreated()
    {
        using var temp = new JournalTempDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.DatabasePath)!);
        File.WriteAllBytes(temp.DatabasePath, Enumerable.Repeat((byte)0x5A, 4096).ToArray());

        var journal = Open(temp, TextWriter.Null);

        Assert.IsType<ActivityJournal>(journal);
        Assert.NotNull(journal.Begin(JournalTestData.Session(), JournalTestData.Start()));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(temp.DatabasePath)!, "activity.db.corrupt-*"));
    }

    [Fact]
    public void Newer_schema_disables_the_journal_without_writing()
    {
        using var temp = new JournalTempDirectory();
        Open(temp, TextWriter.Null);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var log = new StringWriter();
        var journal = Open(temp, log);

        Assert.Same(NullActivityJournal.Instance, journal);
        Assert.Contains("newer", log.ToString());
        Assert.Equal(99L, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version")[0]["user_version"]);
    }

    [Fact]
    public void Database_and_directory_are_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var temp = new JournalTempDirectory();

        Open(temp, TextWriter.Null).Begin(JournalTestData.Session(), JournalTestData.Start());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(temp.DatabasePath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(temp.DatabasePath)!));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~ActivityJournalTests`
Expected: build FAILS with `The type or namespace name 'ActivityJournal' could not be found`.

- [ ] **Step 4: Implement models, interface, schema, and owner-only helper**

`src/SqlHarness.Core/Journal/JournalModels.cs`:

```csharp
namespace SqlHarness.Core;

public enum JournalTransport
{
    Cli,
    Mcp,
}

/// <summary>
/// SQLHarness-derived identity of one agent session. <see cref="SessionKey"/> is
/// computed by SQLHarness (MCP process UUID or CLI process-tree hash); it is not
/// the agent's own conversation id.
/// </summary>
public sealed record SessionIdentity(
    string SessionKey,
    string AgentKind,
    string Source,
    JournalTransport Transport,
    string? ClientName,
    string? ClientVersion,
    string? McpMode,
    int? AgentPid,
    DateTimeOffset? AgentStartedAt,
    int HostPid,
    DateTimeOffset? HostStartedAt,
    string? Cwd);

/// <summary>Operation metadata known before execution. SQL text is dropped by the journal unless storeSensitive.</summary>
public sealed record OperationStart(
    string Operation,
    string? Profile,
    IReadOnlyDictionary<string, string>? Vars,
    bool MutationRequested,
    string? SqlHash,
    string? CandidateSqlHash,
    string? SqlText,
    string? CandidateSqlText);

public sealed record OperationEnd(
    string Status,
    int ExitCode,
    string? ErrorKind,
    long DurationMilliseconds,
    string? Engine,
    string? Server,
    string? Database,
    int? ResultSets,
    long? RowsReturned);

public sealed record JournalHandle(long OperationId);
```

`src/SqlHarness.Core/Journal/IActivityJournal.cs`:

```csharp
namespace SqlHarness.Core;

/// <summary>
/// Best-effort activity journal. Implementations never throw: a failed write
/// returns a null handle or is skipped, so journaling cannot change an outcome.
/// </summary>
public interface IActivityJournal
{
    JournalHandle? Begin(SessionIdentity session, OperationStart start);

    void Complete(JournalHandle? handle, OperationEnd end);

    void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted);
}

public sealed class NullActivityJournal : IActivityJournal
{
    public static NullActivityJournal Instance { get; } = new();

    private NullActivityJournal() { }

    public JournalHandle? Begin(SessionIdentity session, OperationStart start) => null;

    public void Complete(JournalHandle? handle, OperationEnd end) { }

    public void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted) { }
}
```

`src/SqlHarness.Core/Journal/JournalSchema.cs`:

```csharp
namespace SqlHarness.Core;

internal static class JournalSchema
{
    internal const int CurrentVersion = 1;

    internal const string Version1 = """
        CREATE TABLE sessions (
            id INTEGER PRIMARY KEY,
            session_key TEXT NOT NULL UNIQUE,
            agent_kind TEXT NOT NULL,
            transport TEXT NOT NULL,
            source TEXT NOT NULL,
            client_name TEXT,
            client_version TEXT,
            mcp_mode TEXT,
            agent_pid INTEGER,
            agent_started_at TEXT,
            host_pid INTEGER NOT NULL,
            host_started_at TEXT,
            cwd TEXT,
            first_seen TEXT NOT NULL,
            last_seen TEXT NOT NULL
        );
        CREATE TABLE operations (
            id INTEGER PRIMARY KEY,
            session_id INTEGER NOT NULL REFERENCES sessions(id),
            operation TEXT NOT NULL,
            host_pid INTEGER NOT NULL,
            host_started_at TEXT,
            started_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            finished_at TEXT,
            status TEXT NOT NULL,
            exit_code INTEGER,
            error_kind TEXT,
            duration_ms INTEGER,
            profile TEXT,
            vars_json TEXT,
            engine TEXT,
            server TEXT,
            database TEXT,
            mutation_requested INTEGER NOT NULL DEFAULT 0,
            result_sets INTEGER,
            rows_returned INTEGER,
            artifact_dir TEXT,
            summary_json TEXT,
            progress_json TEXT,
            raw_tokens INTEGER,
            emitted_tokens INTEGER,
            sql_hash TEXT,
            candidate_sql_hash TEXT,
            sql_text TEXT,
            candidate_sql_text TEXT
        );
        CREATE INDEX ix_operations_session ON operations(session_id, id);
        CREATE INDEX ix_operations_updated ON operations(updated_at);
        CREATE INDEX ix_operations_status ON operations(status);
        CREATE INDEX ix_operations_sql_hash ON operations(sql_hash);
        """;
}
```

`src/SqlHarness.Core/Journal/OwnerOnlyFiles.cs`:

```csharp
namespace SqlHarness.Core;

/// <summary>Owner-only modes for journal files on Unix; Windows inherits the user-profile ACL.</summary>
internal static class OwnerOnlyFiles
{
    internal static void Directory(string path)
    {
        if (!OperatingSystem.IsWindows() && System.IO.Directory.Exists(path))
            System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static void File(string path)
    {
        if (!OperatingSystem.IsWindows() && System.IO.File.Exists(path))
            System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
```


- [ ] **Step 5: Implement `ActivityJournal`**

`src/SqlHarness.Core/Journal/ActivityJournal.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SqlHarness.Core;

/// <summary>
/// SQLite activity journal (WAL). Every public write is best-effort: failures
/// are swallowed and reported as one content-free stderr line per process.
/// Connections are unpooled and short-lived so concurrent CLI processes,
/// the MCP host, and a dashboard reader can share the file.
/// </summary>
public sealed class ActivityJournal : IActivityJournal
{
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private readonly string _path;
    private readonly bool _storeSensitive;
    private readonly TextWriter _log;
    private readonly TimeProvider _time;
    private int _warned;

    private ActivityJournal(string path, bool storeSensitive, TextWriter log, TimeProvider time)
    {
        _path = path;
        _storeSensitive = storeSensitive;
        _log = log;
        _time = time;
    }

    public static IActivityJournal Open(JournalConfig config, TextWriter log) =>
        Open(SqlHarnessPaths.ActivityDatabase, config, log, TimeProvider.System);

    internal static IActivityJournal Open(string path, JournalConfig config, TextWriter log, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(log);
        if (!config.Enabled)
            return NullActivityJournal.Instance;

        var journal = new ActivityJournal(path, config.StoreSensitive, log, time);
        try
        {
            journal.Initialize();
            return journal;
        }
        catch (JournalSchemaTooNewException)
        {
            log.WriteLine("sqlharness: activity journal schema is newer than this sqlharness; journal disabled.");
            return NullActivityJournal.Instance;
        }
        catch (Exception)
        {
            log.WriteLine("sqlharness: activity journal unavailable; continuing without it.");
            return NullActivityJournal.Instance;
        }
    }

    public JournalHandle? Begin(SessionIdentity session, OperationStart start)
    {
        try
        {
            var now = Timestamp(_time.GetUtcNow());
            using var connection = Connect();
            using var transaction = connection.BeginTransaction();
            var sessionId = UpsertSession(connection, transaction, session, now);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO operations (session_id, operation, host_pid, host_started_at, started_at, updated_at,
                    status, profile, vars_json, mutation_requested, sql_hash, candidate_sql_hash, sql_text, candidate_sql_text)
                VALUES ($session, $operation, $hostPid, $hostStarted, $now, $now,
                    'running', $profile, $vars, $mutation, $sqlHash, $candidateHash, $sqlText, $candidateText)
                RETURNING id;
                """;
            insert.Parameters.AddWithValue("$session", sessionId);
            insert.Parameters.AddWithValue("$operation", start.Operation);
            insert.Parameters.AddWithValue("$hostPid", session.HostPid);
            insert.Parameters.AddWithValue("$hostStarted", Nullable(session.HostStartedAt));
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$profile", (object?)start.Profile ?? DBNull.Value);
            insert.Parameters.AddWithValue("$vars", (object?)VarsJson(start.Vars) ?? DBNull.Value);
            insert.Parameters.AddWithValue("$mutation", start.MutationRequested ? 1 : 0);
            insert.Parameters.AddWithValue("$sqlHash", (object?)start.SqlHash ?? DBNull.Value);
            insert.Parameters.AddWithValue("$candidateHash", (object?)start.CandidateSqlHash ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sqlText", _storeSensitive ? (object?)start.SqlText ?? DBNull.Value : DBNull.Value);
            insert.Parameters.AddWithValue("$candidateText", _storeSensitive ? (object?)start.CandidateSqlText ?? DBNull.Value : DBNull.Value);
            var id = (long)insert.ExecuteScalar()!;
            transaction.Commit();
            return new JournalHandle(id);
        }
        catch (Exception)
        {
            Warn();
            return null;
        }
    }

    public void Complete(JournalHandle? handle, OperationEnd end)
    {
        if (handle is null)
            return;
        try
        {
            var now = Timestamp(_time.GetUtcNow());
            using var connection = Connect();
            using var transaction = connection.BeginTransaction();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE operations SET status = $status, exit_code = $exit, error_kind = $error, duration_ms = $duration,
                    engine = $engine, server = $server, database = $database, result_sets = $sets, rows_returned = $rows,
                    finished_at = $now, updated_at = $now
                WHERE id = $id;
                UPDATE sessions SET last_seen = $now
                WHERE id = (SELECT session_id FROM operations WHERE id = $id);
                """;
            update.Parameters.AddWithValue("$id", handle.OperationId);
            update.Parameters.AddWithValue("$status", end.Status);
            update.Parameters.AddWithValue("$exit", end.ExitCode);
            update.Parameters.AddWithValue("$error", (object?)end.ErrorKind ?? DBNull.Value);
            update.Parameters.AddWithValue("$duration", end.DurationMilliseconds);
            update.Parameters.AddWithValue("$engine", (object?)end.Engine ?? DBNull.Value);
            update.Parameters.AddWithValue("$server", (object?)end.Server ?? DBNull.Value);
            update.Parameters.AddWithValue("$database", (object?)end.Database ?? DBNull.Value);
            update.Parameters.AddWithValue("$sets", (object?)end.ResultSets ?? DBNull.Value);
            update.Parameters.AddWithValue("$rows", (object?)end.RowsReturned ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", now);
            update.ExecuteNonQuery();
            transaction.Commit();
        }
        catch (Exception)
        {
            Warn();
        }
    }

    public void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted)
    {
        if (handle is null)
            return;
        try
        {
            using var connection = Connect();
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE operations SET raw_tokens = $raw, emitted_tokens = $emitted, updated_at = $now WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", handle.OperationId);
            update.Parameters.AddWithValue("$raw", raw is null ? DBNull.Value : raw.EstimatedTokenCount);
            update.Parameters.AddWithValue("$emitted", emitted.EstimatedTokenCount);
            update.Parameters.AddWithValue("$now", Timestamp(_time.GetUtcNow()));
            update.ExecuteNonQuery();
        }
        catch (Exception)
        {
            Warn();
        }
    }

    private void Initialize()
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        OwnerOnlyFiles.Directory(directory);
        try
        {
            Migrate();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            Quarantine();
            Migrate();
        }
    }

    private void Migrate()
    {
        using var connection = Connect();
        var version = UserVersion(connection);
        if (version > JournalSchema.CurrentVersion)
            throw new JournalSchemaTooNewException();
        if (version == JournalSchema.CurrentVersion)
            return;

        // auto_vacuum only takes effect before the first table exists; WAL persists in the file.
        Execute(connection, "PRAGMA auto_vacuum = INCREMENTAL;");
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "BEGIN IMMEDIATE;");
        try
        {
            // Re-check under the write lock: a concurrent process may have migrated first.
            var locked = UserVersion(connection);
            if (locked > JournalSchema.CurrentVersion)
                throw new JournalSchemaTooNewException();
            if (locked < 1)
                Execute(connection, JournalSchema.Version1);
            Execute(connection, $"PRAGMA user_version = {JournalSchema.CurrentVersion};");
            Execute(connection, "COMMIT;");
        }
        catch
        {
            Execute(connection, "ROLLBACK;");
            throw;
        }

        ProtectFiles();
    }

    private void Quarantine()
    {
        var suffix = ".corrupt-" + _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        foreach (var side in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _path + side;
            if (File.Exists(file))
                File.Move(file, _path + suffix + side);
        }
    }

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            // Microsoft.Data.Sqlite retries SQLITE_BUSY until the command timeout;
            // 1 s is its minimum and bounds how long a locked journal can delay an agent.
            DefaultTimeout = 1,
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 250; PRAGMA foreign_keys = ON;");
        ProtectFiles();
        return connection;
    }

    private void ProtectFiles()
    {
        OwnerOnlyFiles.File(_path);
        OwnerOnlyFiles.File(_path + "-wal");
        OwnerOnlyFiles.File(_path + "-shm");
    }

    private static long UpsertSession(SqliteConnection connection, SqliteTransaction transaction, SessionIdentity session, string now)
    {
        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO sessions (session_key, agent_kind, transport, source, client_name, client_version, mcp_mode,
                agent_pid, agent_started_at, host_pid, host_started_at, cwd, first_seen, last_seen)
            VALUES ($key, $kind, $transport, $source, $clientName, $clientVersion, $mcpMode,
                $agentPid, $agentStarted, $hostPid, $hostStarted, $cwd, $now, $now)
            ON CONFLICT(session_key) DO UPDATE SET
                last_seen = excluded.last_seen,
                client_name = COALESCE(sessions.client_name, excluded.client_name),
                client_version = COALESCE(sessions.client_version, excluded.client_version)
            RETURNING id;
            """;
        upsert.Parameters.AddWithValue("$key", session.SessionKey);
        upsert.Parameters.AddWithValue("$kind", session.AgentKind);
        upsert.Parameters.AddWithValue("$transport", session.Transport == JournalTransport.Mcp ? "mcp" : "cli");
        upsert.Parameters.AddWithValue("$source", session.Source);
        upsert.Parameters.AddWithValue("$clientName", (object?)session.ClientName ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$clientVersion", (object?)session.ClientVersion ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$mcpMode", (object?)session.McpMode ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$agentPid", (object?)session.AgentPid ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$agentStarted", Nullable(session.AgentStartedAt));
        upsert.Parameters.AddWithValue("$hostPid", session.HostPid);
        upsert.Parameters.AddWithValue("$hostStarted", Nullable(session.HostStartedAt));
        upsert.Parameters.AddWithValue("$cwd", (object?)session.Cwd ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$now", now);
        return (long)upsert.ExecuteScalar()!;
    }

    private static string? VarsJson(IReadOnlyDictionary<string, string>? vars) =>
        vars is null || vars.Count == 0
            ? null
            : JsonSerializer.Serialize(new SortedDictionary<string, string>(
                vars.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal));

    private static object Nullable(DateTimeOffset? value) => value is null ? DBNull.Value : Timestamp(value.Value);

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static long UserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void Warn()
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
            _log.WriteLine("sqlharness: activity journal write failed; continuing without recording.");
    }

    private sealed class JournalSchemaTooNewException : Exception;
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~ActivityJournalTests`
Expected: all PASS.
- If `Corrupt_database_is_quarantined_and_recreated` fails with an error code other than 11 or 26, print `exception.SqliteErrorCode` in a scratch run and **STOP** to report it. Do not widen the catch to every `SqliteException`.
- If the lock test's two `Begin` calls produce two log lines, the `_warned` latch is broken.

- [ ] **Step 7: Commit**

```bash
git add Directory.Packages.props src/SqlHarness.Core/SqlHarness.Core.csproj src/SqlHarness.Core/Journal tests/SqlHarness.Tests/Journal
git commit -m "Add best-effort SQLite activity journal"
```

---

### Task 3: Operation describer

**Files:**
- Create: `src/SqlHarness.Core/Journal/OperationJournalDescriber.cs`
- Test: `tests/SqlHarness.Tests/Journal/OperationJournalDescriberTests.cs`

**Interfaces:**
- Consumes: `OperationStart`, `OperationEnd` (Task 2); existing operation and report records in `Contracts.cs`, `Artifacts.cs`, `Schema.cs`, `SqlHarnessModule.cs`.
- Produces: `internal static class OperationJournalDescriber` with `OperationStart DescribeStart(SqlHarnessOperation)`, `OperationEnd DescribeEnd(SqlHarnessOutcome, long durationMilliseconds)`, `OperationEnd Cancelled(long)`, `OperationEnd Crashed(long)`, `string Status(SqlHarnessExitCode)`, `string SqlHash(string)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class OperationJournalDescriberTests
{
    private static readonly SqlTargetRequest Target =
        new("local", new Dictionary<string, string> { ["tenant"] = "acme", ["env"] = "uat" });

    [Fact]
    public void Query_start_carries_profile_vars_hash_text_and_mutation_flag()
    {
        var operation = new SqlHarnessQueryOperation(Target, "SELECT 1", ["id:int=SQLH_PARAM_MARKER"], 30, 100, true, "db");

        var start = OperationJournalDescriber.DescribeStart(operation);

        Assert.Equal("query", start.Operation);
        Assert.Equal("local", start.Profile);
        Assert.Equal("acme", start.Vars!["tenant"]);
        Assert.True(start.MutationRequested);
        Assert.Equal(OperationJournalDescriber.SqlHash("SELECT 1"), start.SqlHash);
        Assert.StartsWith("sha256:", start.SqlHash);
        Assert.Equal(71, start.SqlHash!.Length);
        Assert.Equal("SELECT 1", start.SqlText);
        Assert.DoesNotContain("SQLH_PARAM_MARKER", System.Text.Json.JsonSerializer.Serialize(start));
    }

    [Fact]
    public void Compare_start_carries_both_variants()
    {
        var operation = new SqlHarnessCompareOperation(Target, null, "SELECT 1", "SELECT 2", [], 30, 5);

        var start = OperationJournalDescriber.DescribeStart(operation);

        Assert.Equal("compare", start.Operation);
        Assert.Equal("SELECT 1", start.SqlText);
        Assert.Equal("SELECT 2", start.CandidateSqlText);
        Assert.NotEqual(start.SqlHash, start.CandidateSqlHash);
    }

    [Fact]
    public void Unsafe_direct_start_has_no_profile()
    {
        var direct = new SqlTargetRequest(null, new Dictionary<string, string>(), "srv", "db", "integrated", UnsafeDirect: true);

        var start = OperationJournalDescriber.DescribeStart(new SqlHarnessPingOperation(direct, 5));

        Assert.Equal("ping", start.Operation);
        Assert.Null(start.Profile);
        Assert.Null(start.SqlHash);
    }

    [Fact]
    public void Target_free_operations_are_named()
    {
        Assert.Equal("gain", OperationJournalDescriber.DescribeStart(new SqlHarnessGainOperation()).Operation);
        Assert.Equal("plan", OperationJournalDescriber.DescribeStart(new SqlHarnessPlanOperation("<x/>", new OutputFootprint(0, 0))).Operation);
    }

    [Theory]
    [InlineData(SqlHarnessExitCode.Success, "succeeded")]
    [InlineData(SqlHarnessExitCode.WatchMaxDuration, "succeeded")]
    [InlineData(SqlHarnessExitCode.SnapshotDifferences, "succeeded")]
    [InlineData(SqlHarnessExitCode.Safety, "rejected")]
    [InlineData(SqlHarnessExitCode.Authentication, "failed")]
    [InlineData(SqlHarnessExitCode.TargetMismatch, "failed")]
    [InlineData(SqlHarnessExitCode.SqlExecution, "failed")]
    [InlineData(SqlHarnessExitCode.LocalStorage, "failed")]
    public void Exit_codes_map_to_statuses(SqlHarnessExitCode exitCode, string status) =>
        Assert.Equal(status, OperationJournalDescriber.Status(exitCode));

    [Fact]
    public void Query_end_reads_target_identity_and_row_counts()
    {
        var identity = new SqlHarnessTargetIdentityReport("srv", "db", "srv-actual", "db-actual", "profile");
        var resultSet = new SqlHarnessResultSetReport([], [], 7, 0);
        var report = new SqlHarnessQueryReport(identity, "read-only", [resultSet, resultSet], [], 0, 5, "hash", new OutputFootprint(0, 0));

        var end = OperationJournalDescriber.DescribeEnd(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), 42);

        Assert.Equal("succeeded", end.Status);
        Assert.Equal(0, end.ExitCode);
        Assert.Equal(42, end.DurationMilliseconds);
        Assert.Equal("sqlserver", end.Engine);
        Assert.Equal("srv-actual", end.Server);
        Assert.Equal("db-actual", end.Database);
        Assert.Equal(2, end.ResultSets);
        Assert.Equal(14, end.RowsReturned);
    }

    [Fact]
    public void Failure_end_carries_machine_error_code_only()
    {
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, "SQL safety rejection: SELECT secret");

        var end = OperationJournalDescriber.DescribeEnd(outcome, 1);

        Assert.Equal("rejected", end.Status);
        Assert.Equal(2, end.ExitCode);
        Assert.Equal(outcome.MachineError?.Code, end.ErrorKind);
        Assert.Null(end.Server);
    }

    [Fact]
    public void Cancelled_and_crashed_ends_are_failed_with_kind()
    {
        Assert.Equal(("failed", "cancelled"), (OperationJournalDescriber.Cancelled(3).Status, OperationJournalDescriber.Cancelled(3).ErrorKind));
        Assert.Equal(("failed", "unhandled_exception"), (OperationJournalDescriber.Crashed(3).Status, OperationJournalDescriber.Crashed(3).ErrorKind));
    }
}
```


- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~OperationJournalDescriberTests`
Expected: build FAILS with `The name 'OperationJournalDescriber' does not exist`.

- [ ] **Step 3: Implement**

```csharp
using System.Security.Cryptography;
using System.Text;

namespace SqlHarness.Core;

/// <summary>
/// Maps operations and outcomes to journal rows. It reads only names, scope,
/// SQL text (for hashing and opt-in storage), and target identity; it never
/// reads parameter values, result rows, or messages.
/// </summary>
internal static class OperationJournalDescriber
{
    internal static OperationStart DescribeStart(SqlHarnessOperation operation) => operation switch
    {
        SqlHarnessQueryOperation query => Start("query", query.Target, query.AllowMutation, query.Sql, null),
        SqlHarnessMeasureOperation measure => Start("measure", measure.Target, false, measure.QuerySql, null),
        SqlHarnessCompareOperation compare => Start("compare", compare.Target, false, compare.BaselineSql, compare.CandidateSql),
        SqlHarnessCompareMatrixOperation matrix => Start("compare", matrix.Target, false, matrix.BaselineSql, matrix.CandidateSql),
        SqlHarnessWatchOperation watch => Start("watch", watch.Target, false, watch.Sql, null),
        SqlHarnessSnapshotOperation snapshot => Start("snapshot", snapshot.Target, false, snapshot.Sql, null),
        SqlHarnessSchemaOperation schema => Start("schema", schema.Target, false, null, null),
        SqlHarnessPingOperation ping => Start("ping", ping.Target, false, null, null),
        SqlHarnessCountsOperation counts => Start("counts", counts.Target, false, null, null),
        SqlHarnessSpaceOperation space => Start("space", space.Target, false, null, null),
        SqlHarnessQueryStoreTopOperation qstop => Start("qstop", qstop.Target, false, null, null),
        SqlHarnessIndexesOperation indexes => Start("indexes", indexes.Target, false, null, null),
        SqlHarnessPlanOperation => TargetFree("plan"),
        SqlHarnessGainOperation => TargetFree("gain"),
        _ => TargetFree(FallbackName(operation)),
    };

    internal static OperationEnd DescribeEnd(SqlHarnessOutcome outcome, long durationMilliseconds)
    {
        var identity = TargetOf(outcome.Report);
        var (resultSets, rows) = outcome.Report is SqlHarnessQueryReport query
            ? (query.ResultSets.Count, query.ResultSets.Sum(set => set.RowCount))
            : ((int?)null, (long?)null);
        return new OperationEnd(
            Status(outcome.ExitCode),
            (int)outcome.ExitCode,
            outcome.MachineError?.Code,
            Math.Max(durationMilliseconds, 0),
            identity is null ? null : identity.Engine ?? "sqlserver",
            identity?.ActualServer,
            identity?.ActualDatabase,
            resultSets,
            rows);
    }

    internal static OperationEnd Cancelled(long durationMilliseconds) =>
        new("failed", -1, "cancelled", Math.Max(durationMilliseconds, 0), null, null, null, null, null);

    internal static OperationEnd Crashed(long durationMilliseconds) =>
        new("failed", -1, "unhandled_exception", Math.Max(durationMilliseconds, 0), null, null, null, null, null);

    internal static string Status(SqlHarnessExitCode exitCode) => exitCode switch
    {
        SqlHarnessExitCode.Success or SqlHarnessExitCode.WatchMaxDuration or SqlHarnessExitCode.SnapshotDifferences => "succeeded",
        SqlHarnessExitCode.Safety => "rejected",
        _ => "failed",
    };

    internal static string SqlHash(string sql) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();

    private static OperationStart Start(string name, SqlTargetRequest target, bool mutation, string? sql, string? candidate) =>
        new(
            name,
            target.UnsafeDirect ? null : target.Profile,
            target.Vars.Count == 0 ? null : target.Vars,
            mutation,
            sql is null ? null : SqlHash(sql),
            candidate is null ? null : SqlHash(candidate),
            sql,
            candidate);

    private static OperationStart TargetFree(string name) => new(name, null, null, false, null, null, null, null);

    private static string FallbackName(SqlHarnessOperation operation)
    {
        var name = operation.GetType().Name;
        if (name.StartsWith("SqlHarness", StringComparison.Ordinal))
            name = name["SqlHarness".Length..];
        if (name.EndsWith("Operation", StringComparison.Ordinal))
            name = name[..^"Operation".Length];
        return name.ToLowerInvariant();
    }

    private static SqlHarnessTargetIdentityReport? TargetOf(object? report) => report switch
    {
        SqlHarnessQueryReport value => value.Target,
        SqlHarnessMeasureReport value => value.Target,
        SqlHarnessMeasureSetReport value => value.Target,
        SqlHarnessCompareReport value => value.Target,
        SqlHarnessWatchReport value => value.Target,
        SqlHarnessSnapshotReport value => value.Target,
        SqlHarnessSchemaReport value => value.Target,
        SqlHarnessPingReport value => value.Target,
        SqlHarnessCountsReport value => value.Target,
        SqlHarnessSpaceReport value => value.Target,
        SqlHarnessQueryStoreTopReport value => value.Target,
        SqlHarnessIndexesReport value => value.Target,
        _ => null,
    };
}
```

If any report in `TargetOf` does not compile because its target property has a different name, open its declaration and use the actual property, which has type `SqlHarnessTargetIdentityReport`. If a report has no such property, delete its arm. `SqlHarnessCompareMatrixReport` is deliberately absent: matrix cells carry their own targets, and phase 2 handles them.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~OperationJournalDescriberTests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Core/Journal/OperationJournalDescriber.cs tests/SqlHarness.Tests/Journal/OperationJournalDescriberTests.cs
git commit -m "Describe operations and outcomes for the activity journal"
```

---

### Task 4: Process tree and session identity

**Files:**
- Create: `src/SqlHarness.Core/Journal/ProcessInfo.cs`, `src/SqlHarness.Core/Journal/SessionIdentities.cs`
- Test: `tests/SqlHarness.Tests/Journal/SessionIdentitiesTests.cs`, `tests/SqlHarness.Tests/Journal/ProcessInfoTests.cs`

**Interfaces:**
- Consumes: `SessionIdentity`, `JournalTransport` (Task 2).
- Produces:
  - `sealed record ProcessSnapshot(int Pid, int? ParentPid, string Name, DateTimeOffset? StartedAt, string? CommandLine)`
  - `interface IProcessInfo { int CurrentPid { get; } ProcessSnapshot? Get(int pid); }`
  - `static class ProcessInfo { static IProcessInfo Current { get; } }`
  - `internal static (int ParentPid, long StartTicks)? LinuxProcessInfo.ParseStat(string)`
  - `static class SessionIdentities { SessionIdentity Cli(IProcessInfo); SessionIdentity Mcp(IProcessInfo, string sessionKey, string? clientName, string? clientVersion, string mcpMode); string AgentKindFromClientName(string?); internal string? Classify(ProcessSnapshot); internal const int MaximumDepth = 16; }`

- [ ] **Step 1: Write the failing tests**

`tests/SqlHarness.Tests/Journal/SessionIdentitiesTests.cs`:

```csharp
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class SessionIdentitiesTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

    private sealed class FakeProcesses(int current, params ProcessSnapshot[] processes) : IProcessInfo
    {
        private readonly Dictionary<int, ProcessSnapshot> _byPid = processes.ToDictionary(p => p.Pid);
        public int CurrentPid => current;
        public ProcessSnapshot? Get(int pid) => _byPid.GetValueOrDefault(pid);
    }

    private static ProcessSnapshot P(int pid, int? parent, string name, string? cmd = null) =>
        new(pid, parent, name, T.AddMinutes(pid), cmd);

    [Fact]
    public void Cli_finds_claude_ancestor_through_shells()
    {
        var processes = new FakeProcesses(30,
            P(30, 20, "sqlharness.exe"), P(20, 10, "pwsh.exe"), P(10, 1, "claude.exe"), P(1, null, "explorer.exe"));

        var identity = SessionIdentities.Cli(processes);

        Assert.Equal("claude", identity.AgentKind);
        Assert.Equal("process-tree", identity.Source);
        Assert.Equal(JournalTransport.Cli, identity.Transport);
        Assert.Equal(10, identity.AgentPid);
        Assert.Equal(30, identity.HostPid);
        Assert.StartsWith("cli:", identity.SessionKey);
    }

    [Fact]
    public void Two_cli_processes_under_one_agent_share_a_session_key()
    {
        var first = SessionIdentities.Cli(new FakeProcesses(30, P(30, 20, "sqlharness"), P(20, 10, "bash"), P(10, 1, "codex")));
        var second = SessionIdentities.Cli(new FakeProcesses(31, P(31, 21, "sqlharness"), P(21, 10, "bash"), P(10, 1, "codex")));

        Assert.Equal("codex", first.AgentKind);
        Assert.Equal(first.SessionKey, second.SessionKey);
    }

    [Fact]
    public void Same_agent_pid_with_different_start_time_is_a_different_session()
    {
        var first = SessionIdentities.Cli(new FakeProcesses(30, P(30, 10, "sqlharness"), P(10, null, "claude")));
        var reused = SessionIdentities.Cli(new FakeProcesses(30, P(30, 10, "sqlharness"),
            new ProcessSnapshot(10, null, "claude", T.AddDays(1), null)));

        Assert.NotEqual(first.SessionKey, reused.SessionKey);
    }

    [Theory]
    [InlineData("node", "/usr/lib/node_modules/@anthropic-ai/claude-code/cli.js", "claude")]
    [InlineData("node.exe", "node C:\\npm\\node_modules\\@openai\\codex\\bin\\codex.js", "codex")]
    [InlineData("node", "/srv/app/server.js", null)]
    [InlineData("Claude.exe", null, "claude")]
    [InlineData("codex", null, "codex")]
    [InlineData("pwsh", null, null)]
    public void Classify_recognizes_native_and_node_hosted_agents(string name, string? cmd, string? expected) =>
        Assert.Equal(expected, SessionIdentities.Classify(new ProcessSnapshot(1, null, name, T, cmd)));

    [Fact]
    public void No_agent_yields_unknown_keyed_by_parent()
    {
        var a = SessionIdentities.Cli(new FakeProcesses(30, P(30, 20, "sqlharness"), P(20, null, "bash")));
        var b = SessionIdentities.Cli(new FakeProcesses(31, P(31, 20, "sqlharness"), P(20, null, "bash")));

        Assert.Equal("unknown", a.AgentKind);
        Assert.Equal("unknown", a.Source);
        Assert.Null(a.AgentPid);
        Assert.Equal(a.SessionKey, b.SessionKey);
    }

    [Fact]
    public void Missing_parent_yields_unknown_keyed_by_self()
    {
        var identity = SessionIdentities.Cli(new FakeProcesses(30, P(30, 999, "sqlharness")));

        Assert.Equal("unknown", identity.AgentKind);
        Assert.StartsWith("cli:", identity.SessionKey);
    }

    [Fact]
    public void Cycle_in_parent_chain_terminates_as_unknown()
    {
        var identity = SessionIdentities.Cli(new FakeProcesses(30, P(30, 20, "sqlharness"), P(20, 21, "a"), P(21, 20, "b")));

        Assert.Equal("unknown", identity.AgentKind);
    }

    [Fact]
    public void Throwing_process_info_yields_unknown()
    {
        var identity = SessionIdentities.Cli(new ThrowingProcesses());

        Assert.Equal("unknown", identity.AgentKind);
        Assert.Equal(Environment.ProcessId, identity.HostPid);
    }

    private sealed class ThrowingProcesses : IProcessInfo
    {
        public int CurrentPid => Environment.ProcessId;
        public ProcessSnapshot? Get(int pid) => throw new UnauthorizedAccessException();
    }

    [Theory]
    [InlineData("claude-code", "claude")]
    [InlineData("Claude Desktop", "claude")]
    [InlineData("codex-mcp-client", "codex")]
    [InlineData("sqlharness-mcp-tests", "other")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void Client_names_map_to_agent_kinds(string? clientName, string expected) =>
        Assert.Equal(expected, SessionIdentities.AgentKindFromClientName(clientName));

    [Fact]
    public void Mcp_identity_uses_client_info_and_records_agent_ancestor()
    {
        var processes = new FakeProcesses(30, P(30, 10, "sqlharness"), P(10, null, "claude"));

        var identity = SessionIdentities.Mcp(processes, "mcp:abc", "claude-code", "2.1.0", "fixed");

        Assert.Equal("mcp:abc", identity.SessionKey);
        Assert.Equal("claude", identity.AgentKind);
        Assert.Equal("mcp-clientinfo", identity.Source);
        Assert.Equal(JournalTransport.Mcp, identity.Transport);
        Assert.Equal("claude-code", identity.ClientName);
        Assert.Equal("fixed", identity.McpMode);
        Assert.Equal(10, identity.AgentPid);
    }

    [Fact]
    public void Mcp_identity_without_client_info_falls_back_to_process_tree_kind()
    {
        var processes = new FakeProcesses(30, P(30, 10, "sqlharness"), P(10, null, "codex"));

        var identity = SessionIdentities.Mcp(processes, "mcp:abc", null, null, "request");

        Assert.Equal("codex", identity.AgentKind);
        Assert.Equal("process-tree", identity.Source);
    }
}
```

`tests/SqlHarness.Tests/Journal/ProcessInfoTests.cs`:

```csharp
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class ProcessInfoTests
{
    [Fact]
    public void Linux_stat_parsing_handles_parentheses_in_comm()
    {
        // pid (comm) state ppid, then fields 5..21, then starttime (field 22).
        var fields = string.Join(' ', Enumerable.Range(5, 17).Select(i => i.ToString()));
        var stat = $"123 (we ird) x) S 45 {fields} 987654 rest";

        var parsed = LinuxProcessInfo.ParseStat(stat);

        Assert.Equal((45, 987654L), parsed);
    }

    [Fact]
    public void Linux_stat_parsing_rejects_garbage() => Assert.Null(LinuxProcessInfo.ParseStat("garbage"));

    [Fact]
    public void Current_process_is_visible_on_windows_and_linux()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return;

        var self = ProcessInfo.Current.Get(Environment.ProcessId);

        Assert.NotNull(self);
        Assert.Equal(Environment.ProcessId, self!.Pid);
        Assert.False(string.IsNullOrWhiteSpace(self.Name));
        Assert.NotNull(self.ParentPid);
        Assert.NotNull(self.StartedAt);
    }
}
```


- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SessionIdentitiesTests|FullyQualifiedName~ProcessInfoTests"`
Expected: build FAILS with `The type or namespace name 'IProcessInfo' could not be found`.

- [ ] **Step 3: Implement process info**

`src/SqlHarness.Core/Journal/ProcessInfo.cs`:

```csharp
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SqlHarness.Core;

public sealed record ProcessSnapshot(int Pid, int? ParentPid, string Name, DateTimeOffset? StartedAt, string? CommandLine);

public interface IProcessInfo
{
    int CurrentPid { get; }

    /// <summary>Snapshot of one process, or null when it does not exist or is not readable.</summary>
    ProcessSnapshot? Get(int pid);
}

public static class ProcessInfo
{
    public static IProcessInfo Current { get; } =
        OperatingSystem.IsWindows() ? new WindowsProcessInfo()
        : OperatingSystem.IsLinux() ? new LinuxProcessInfo()
        : new SelfOnlyProcessInfo();
}

/// <summary>Platforms without a parent-process reader (macOS): only the current process is known.</summary>
internal sealed class SelfOnlyProcessInfo : IProcessInfo
{
    public int CurrentPid => Environment.ProcessId;

    public ProcessSnapshot? Get(int pid)
    {
        if (pid != Environment.ProcessId)
            return null;
        using var self = Process.GetCurrentProcess();
        return new ProcessSnapshot(pid, null, self.ProcessName, new DateTimeOffset(self.StartTime), null);
    }
}

internal sealed class LinuxProcessInfo : IProcessInfo
{
    // USER_HZ is 100 on every mainstream Linux ABI; starttime is in USER_HZ ticks since boot.
    private const double TicksPerSecond = 100;
    private static readonly Lazy<long?> BootTimeSeconds = new(ReadBootTime);

    public int CurrentPid => Environment.ProcessId;

    public ProcessSnapshot? Get(int pid)
    {
        try
        {
            var directory = $"/proc/{pid.ToString(CultureInfo.InvariantCulture)}";
            var parsed = ParseStat(File.ReadAllText(directory + "/stat"));
            if (parsed is null)
                return null;
            var name = ReadName(directory);
            var commandLine = File.ReadAllText(directory + "/cmdline").Replace('\0', ' ').Trim();
            DateTimeOffset? started = BootTimeSeconds.Value is { } boot
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)((boot + parsed.Value.StartTicks / TicksPerSecond) * 1000))
                : null;
            return new ProcessSnapshot(pid, parsed.Value.ParentPid, name, started, commandLine.Length == 0 ? null : commandLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Parses /proc/[pid]/stat: comm may contain spaces and ')' so split after the last ')'.</summary>
    internal static (int ParentPid, long StartTicks)? ParseStat(string stat)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0)
            return null;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // fields[0] = state (field 3), fields[1] = ppid (field 4), fields[19] = starttime (field 22).
        if (fields.Length < 20
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)
            || !long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var start))
            return null;
        return (parent, start);
    }

    private static string ReadName(string directory)
    {
        try
        {
            var target = new FileInfo(directory + "/exe").LinkTarget;
            if (!string.IsNullOrEmpty(target))
                return Path.GetFileName(target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return File.ReadAllText(directory + "/comm").Trim();
    }

    private static long? ReadBootTime()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/stat"))
            {
                if (line.StartsWith("btime ", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(6).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                    return seconds;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }
}

internal sealed class WindowsProcessInfo : IProcessInfo
{
    private const uint SnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    public int CurrentPid => Environment.ProcessId;

    public ProcessSnapshot? Get(int pid)
    {
        var entry = Find(pid);
        if (entry is null)
            return null;
        DateTimeOffset? started = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            started = new DateTimeOffset(process.StartTime);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }

        // Command lines of other processes need PEB reads or WMI; node-hosted agents stay unclassified on Windows.
        return new ProcessSnapshot(pid, (int)entry.Value.th32ParentProcessID, entry.Value.szExeFile, started, null);
    }

    private static ProcessEntry32? Find(int pid)
    {
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle)
            return null;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry))
                return null;
            do
            {
                if (entry.th32ProcessID == (uint)pid)
                    return entry;
            }
            while (Process32NextW(snapshot, ref entry));
            return null;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
```

If the build reports a platform-compatibility analyzer error (CA1416) for these calls, guard the call sites with `OperatingSystem.IsWindows()` / `IsLinux()` checks. `ProcessInfo.Current` already selects the implementation that way. Add `[SupportedOSPlatform("windows")]` to `WindowsProcessInfo` and `[SupportedOSPlatform("linux")]` to `LinuxProcessInfo` only if the analyzer asks for it.

- [ ] **Step 4: Implement session identities**

`src/SqlHarness.Core/Journal/SessionIdentities.cs`:

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SqlHarness.Core;

/// <summary>
/// Implicit session identity: MCP clientInfo plus a per-process key, or for the
/// CLI the nearest known agent ancestor. Agents send nothing and are told nothing.
/// </summary>
public static class SessionIdentities
{
    internal const int MaximumDepth = 16;

    public static SessionIdentity Cli(IProcessInfo processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var walk = Walk(processes);
        string key;
        if (walk.Agent is { } agent)
            key = Key("cli", walk.AgentKind!, agent.Pid, agent.StartedAt);
        else if (walk.Parent is { } parent)
            key = Key("cli", "unknown", parent.Pid, parent.StartedAt);
        else
            key = Key("cli", "unknown", processes.CurrentPid, walk.Self?.StartedAt);

        return new SessionIdentity(
            key,
            walk.AgentKind ?? "unknown",
            walk.AgentKind is null ? "unknown" : "process-tree",
            JournalTransport.Cli,
            null,
            null,
            null,
            walk.Agent?.Pid,
            walk.Agent?.StartedAt,
            processes.CurrentPid,
            walk.Self?.StartedAt,
            CurrentDirectory());
    }

    public static SessionIdentity Mcp(IProcessInfo processes, string sessionKey, string? clientName, string? clientVersion, string mcpMode)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var walk = Walk(processes);
        var fromClient = AgentKindFromClientName(clientName);
        var useClient = fromClient != "unknown";
        return new SessionIdentity(
            sessionKey,
            useClient ? fromClient : walk.AgentKind ?? "unknown",
            useClient ? "mcp-clientinfo" : walk.AgentKind is null ? "unknown" : "process-tree",
            JournalTransport.Mcp,
            string.IsNullOrWhiteSpace(clientName) ? null : clientName,
            string.IsNullOrWhiteSpace(clientVersion) ? null : clientVersion,
            mcpMode,
            walk.Agent?.Pid,
            walk.Agent?.StartedAt,
            processes.CurrentPid,
            walk.Self?.StartedAt,
            CurrentDirectory());
    }

    public static string AgentKindFromClientName(string? clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName))
            return "unknown";
        if (clientName.Contains("claude", StringComparison.OrdinalIgnoreCase))
            return "claude";
        if (clientName.Contains("codex", StringComparison.OrdinalIgnoreCase))
            return "codex";
        return "other";
    }

    internal static string? Classify(ProcessSnapshot process)
    {
        var name = Path.GetFileNameWithoutExtension(process.Name).ToLowerInvariant();
        switch (name)
        {
            case "claude":
                return "claude";
            case "codex":
                return "codex";
            case "node" or "bun" when process.CommandLine is { } commandLine:
                var normalized = commandLine.Replace('\\', '/');
                if (normalized.Contains("@anthropic-ai/claude-code", StringComparison.OrdinalIgnoreCase))
                    return "claude";
                if (normalized.Contains("@openai/codex", StringComparison.OrdinalIgnoreCase))
                    return "codex";
                return null;
            default:
                return null;
        }
    }

    private static Walked Walk(IProcessInfo processes)
    {
        ProcessSnapshot? self = null;
        ProcessSnapshot? parent = null;
        try
        {
            self = processes.Get(processes.CurrentPid);
            var visited = new HashSet<int> { processes.CurrentPid };
            var next = self?.ParentPid;
            for (var depth = 0; depth < MaximumDepth && next is { } pid && visited.Add(pid); depth++)
            {
                var current = processes.Get(pid);
                if (current is null)
                    break;
                parent ??= current;
                if (Classify(current) is { } kind)
                    return new Walked(self, parent, current, kind);
                next = current.ParentPid;
            }
        }
        catch (Exception)
        {
            // Unreadable process tree: identity degrades to unknown, never fails the operation.
        }

        return new Walked(self, parent, null, null);
    }

    private static string Key(string prefix, string kind, int pid, DateTimeOffset? started)
    {
        var material = string.Create(CultureInfo.InvariantCulture, $"{kind}|{pid}|{started?.ToUnixTimeMilliseconds()}");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return prefix + ":" + hash[..32];
    }

    private static string? CurrentDirectory()
    {
        try
        {
            return Environment.CurrentDirectory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record Walked(ProcessSnapshot? Self, ProcessSnapshot? Parent, ProcessSnapshot? Agent, string? AgentKind);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SessionIdentitiesTests|FullyQualifiedName~ProcessInfoTests"`
Expected: all PASS on Windows. Also run under WSL via the Linux gate in Task 7. `Current_process_is_visible_on_windows_and_linux` exercises the real implementation on each OS.

- [ ] **Step 6: Commit**

```bash
git add src/SqlHarness.Core/Journal/ProcessInfo.cs src/SqlHarness.Core/Journal/SessionIdentities.cs tests/SqlHarness.Tests/Journal/SessionIdentitiesTests.cs tests/SqlHarness.Tests/Journal/ProcessInfoTests.cs
git commit -m "Resolve implicit agent session identity from MCP client info and process tree"
```

---

### Task 5: Journaling decorator and CLI composition

**Files:**
- Modify: `src/SqlHarness.Core/Contracts.cs` (`SqlHarnessEmissionReceipt`), `src/SqlHarness.Core/SqlHarnessModule.cs` (`WithReceipt`), `src/SqlHarness.Cli/Program.cs`
- Create: `src/SqlHarness.Core/Journal/JournalingModule.cs`
- Test: `tests/SqlHarness.Tests/Journal/JournalingModuleTests.cs`, `tests/SqlHarness.Tests/Journal/JournalContractTests.cs`

**Interfaces:**
- Consumes: `IActivityJournal`, `ActivityJournal.Open` (Task 2), `OperationJournalDescriber` (Task 3), `SessionIdentities.Cli`, `ProcessInfo.Current` (Task 4), `SqlHarnessConfigLoader.Load` (Task 1).
- Produces: `public sealed class JournalingModule(ISqlHarnessModule inner, Func<IActivityJournal> journal, Func<SessionIdentity> session) : ISqlHarnessModule`, and `SqlHarnessEmissionReceipt.RawFootprint` (`OutputFootprint?`, internal init).

- [ ] **Step 1: Write the failing tests**

`tests/SqlHarness.Tests/Journal/JournalingModuleTests.cs`:

```csharp
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class JournalingModuleTests
{
    private static readonly SqlTargetRequest Target = new("local", new Dictionary<string, string> { ["tenant"] = "acme" });

    private sealed class FakeModule(Func<SqlHarnessOperation, CancellationToken, Task<SqlHarnessOutcome>> behavior) : ISqlHarnessModule
    {
        public int Calls;
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return behavior(operation, ct);
        }
    }

    private static SqlHarnessQueryOperation Query(string sql = "SELECT 1") =>
        new(Target, sql, ["id:nvarchar=SQLH_PARAM_MARKER"], 30, 100, false, null)
        {
            TypedParameters = [new SqlHarnessParameterInput("other", "nvarchar", "SQLH_PARAM_MARKER")],
        };

    private static (JournalingModule Module, JournalTempDirectory Temp) Create(FakeModule inner, bool storeSensitive = false)
    {
        var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = storeSensitive }, TextWriter.Null, TimeProvider.System);
        return (new JournalingModule(inner, () => journal, () => JournalTestData.Session()), temp);
    }

    [Fact]
    public async Task Successful_operation_is_recorded_and_outcome_is_unchanged()
    {
        var expected = new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
        var (module, temp) = Create(new FakeModule((_, _) => Task.FromResult(expected)));
        using (temp)
        {
            var outcome = await module.ExecuteAsync(Query());

            Assert.Same(expected, outcome);
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT status, exit_code, operation FROM operations").Single();
            Assert.Equal("succeeded", row["status"]);
            Assert.Equal("query", row["operation"]);
        }
    }

    [Fact]
    public async Task Rejected_operation_keeps_exit_code_and_is_recorded_as_rejected()
    {
        var (module, temp) = Create(new FakeModule((_, _) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, "SQL safety rejection: nope"))));
        using (temp)
        {
            var outcome = await module.ExecuteAsync(Query());

            Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
            Assert.Equal("rejected", JournalDb.Rows(temp.DatabasePath, "SELECT status FROM operations").Single()["status"]);
        }
    }

    [Fact]
    public async Task Cancelled_operation_is_completed_as_cancelled_and_rethrown()
    {
        var (module, temp) = Create(new FakeModule((_, ct) => Task.FromCanceled<SqlHarnessOutcome>(new CancellationToken(true))));
        using (temp)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.ExecuteAsync(Query()));

            var row = JournalDb.Rows(temp.DatabasePath, "SELECT status, error_kind FROM operations").Single();
            Assert.Equal("failed", row["status"]);
            Assert.Equal("cancelled", row["error_kind"]);
        }
    }

    [Fact]
    public async Task Unexpected_exception_is_completed_and_rethrown()
    {
        var (module, temp) = Create(new FakeModule((_, _) => throw new InvalidOperationException("boom")));
        using (temp)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => module.ExecuteAsync(Query()));

            Assert.Equal("unhandled_exception", JournalDb.Rows(temp.DatabasePath, "SELECT error_kind FROM operations").Single()["error_kind"]);
        }
    }

    [Fact]
    public async Task Journal_or_identity_failure_never_changes_the_outcome()
    {
        var expected = new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
        var module = new JournalingModule(
            new FakeModule((_, _) => Task.FromResult(expected)),
            () => throw new IOException("disk"),
            () => throw new UnauthorizedAccessException());

        Assert.Same(expected, await module.ExecuteAsync(Query()));
    }

    [Fact]
    public async Task Emission_receipt_is_wrapped_and_records_tokens()
    {
        var innerCompleted = 0;
        var receipt = new SqlHarnessEmissionReceipt((_, _) =>
        {
            Interlocked.Increment(ref innerCompleted);
            return Task.FromResult(SqlHarnessExitCode.Success);
        })
        {
            RawFootprint = new OutputFootprint(800, 20),
        };
        var (module, temp) = Create(new FakeModule((_, _) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null, receipt))));
        using (temp)
        {
            var outcome = await module.ExecuteAsync(Query());
            var exit = await outcome.EmissionReceipt!.CompleteAsync(new OutputFootprint(80, 4));

            Assert.Equal(SqlHarnessExitCode.Success, exit);
            Assert.Equal(1, innerCompleted);
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT raw_tokens, emitted_tokens FROM operations").Single();
            Assert.Equal(200L, row["raw_tokens"]);
            Assert.Equal(20L, row["emitted_tokens"]);
        }
    }

    [Fact]
    public async Task Sensitivity_gate_holds_in_raw_database_bytes()
    {
        var ok = new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
        var (hashOnly, hashTemp) = Create(new FakeModule((_, _) => Task.FromResult(ok)));
        var (sensitive, sensitiveTemp) = Create(new FakeModule((_, _) => Task.FromResult(ok)), storeSensitive: true);
        using (hashTemp)
        using (sensitiveTemp)
        {
            await hashOnly.ExecuteAsync(Query("SELECT 'SQLH_SQL_MARKER'"));
            await sensitive.ExecuteAsync(Query("SELECT 'SQLH_SQL_MARKER'"));

            var hashBytes = JournalDb.AllBytes(hashTemp.DatabasePath);
            var sensitiveBytes = JournalDb.AllBytes(sensitiveTemp.DatabasePath);
            Assert.False(JournalDb.Contains(hashBytes, "SQLH_SQL_MARKER"));
            Assert.False(JournalDb.Contains(hashBytes, "SQLH_PARAM_MARKER"));
            Assert.True(JournalDb.Contains(sensitiveBytes, "SQLH_SQL_MARKER"));
            Assert.False(JournalDb.Contains(sensitiveBytes, "SQLH_PARAM_MARKER"));
        }
    }

    [Fact]
    public async Task Journal_and_identity_are_resolved_once_per_module()
    {
        var journalOpens = 0;
        var identities = 0;
        var module = new JournalingModule(
            new FakeModule((_, _) => Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null))),
            () => { Interlocked.Increment(ref journalOpens); return NullActivityJournal.Instance; },
            () => { Interlocked.Increment(ref identities); return JournalTestData.Session(); });

        await module.ExecuteAsync(Query());
        await module.ExecuteAsync(Query());

        Assert.Equal(1, journalOpens);
        Assert.Equal(1, identities);
    }
}
```

`tests/SqlHarness.Tests/Journal/JournalContractTests.cs`. This test drives the real CLI twice, once over a plain module and once over a journaling module, and requires byte-identical stdout and exit codes:

```csharp
using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

[Collection(SqlHarnessHomeCollection.Name)]
public sealed class JournalContractTests : IDisposable
{
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly JournalTempDirectory _temp = new();

    public JournalContractTests() => Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _temp.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        _temp.Dispose();
    }

    public static TheoryData<string[]> Commands => new()
    {
        new[] { "gain", "--json" },
        new[] { "ping", "missing-profile", "--json" },
        new[] { "query", "missing-profile", "--json", "--file", "does-not-exist.sql" },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task Cli_output_and_exit_code_are_identical_with_and_without_journal(string[] args)
    {
        var (plainExit, plainOut) = await Run(new SqlHarnessModule(), args);
        var journal = ActivityJournal.Open(new JournalConfig(), TextWriter.Null);
        var (journalExit, journalOut) = await Run(
            new JournalingModule(new SqlHarnessModule(), () => journal, () => SessionIdentities.Cli(ProcessInfo.Current)), args);

        Assert.Equal(plainExit, journalExit);
        Assert.Equal(plainOut, journalOut);
    }

    [Fact]
    public async Task Plan_command_output_is_identical_and_is_journaled()
    {
        var plan = Path.Combine(AppContext.BaseDirectory, "Fixtures", "operators.sqlplan");
        var args = new[] { "plan", plan, "--json" };

        var (plainExit, plainOut) = await Run(new SqlHarnessModule(), args);
        var journal = ActivityJournal.Open(new JournalConfig(), TextWriter.Null);
        var (journalExit, journalOut) = await Run(
            new JournalingModule(new SqlHarnessModule(), () => journal, () => SessionIdentities.Cli(ProcessInfo.Current)), args);

        Assert.Equal(plainExit, journalExit);
        Assert.Equal(plainOut, journalOut);
        var row = JournalDb.Rows(SqlHarnessPaths.ActivityDatabase, "SELECT operation, emitted_tokens FROM operations").Single();
        Assert.Equal("plan", row["operation"]);
        Assert.NotNull(row["emitted_tokens"]);
    }

    private static async Task<(int Exit, string Output)> Run(ISqlHarnessModule module, string[] args)
    {
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output, new StringReader(string.Empty), stdinRedirected: false,
            planStdin: new MemoryStream(), mcpError: TextWriter.Null).RunAsync(args);
        return (exit, output.ToString());
    }
}
```


- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~JournalingModuleTests|FullyQualifiedName~JournalContractTests"`
Expected: build FAILS with `The type or namespace name 'JournalingModule' could not be found` and `'SqlHarnessEmissionReceipt' does not contain a definition for 'RawFootprint'`.

- [ ] **Step 3: Add `RawFootprint` to the receipt and set it**

`src/SqlHarness.Core/Contracts.cs`, inside `SqlHarnessEmissionReceipt` after the constructor:

```csharp
    /// <summary>Pre-projection footprint known to the module; the journal records it alongside the emitted footprint.</summary>
    internal OutputFootprint? RawFootprint { get; init; }
```

`src/SqlHarness.Core/SqlHarnessModule.cs`, in `WithReceipt`, change the receipt construction to set it. The lambda body stays unchanged:

```csharp
        var receipt = new SqlHarnessEmissionReceipt((emitted, _) =>
        {
            // ...existing body unchanged...
        })
        {
            RawFootprint = raw,
        };
```

Run `grep -rn "new SqlHarnessEmissionReceipt" src` and confirm this is the only construction site. If there are others, set `RawFootprint` there too when a raw footprint is in scope.

- [ ] **Step 4: Implement the decorator**

`src/SqlHarness.Core/Journal/JournalingModule.cs`:

```csharp
using System.Diagnostics;

namespace SqlHarness.Core;

/// <summary>
/// Records every operation in the activity journal around an inner module.
/// The inner outcome is returned unchanged except that its emission receipt is
/// wrapped to also record token footprints; journal or identity failures are
/// swallowed so they can never change output or exit codes.
/// </summary>
public sealed class JournalingModule : ISqlHarnessModule
{
    private readonly ISqlHarnessModule _inner;
    private readonly Lazy<IActivityJournal?> _journal;
    private readonly Lazy<SessionIdentity?> _session;

    public JournalingModule(ISqlHarnessModule inner, Func<IActivityJournal> journal, Func<SessionIdentity> session)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(session);
        _journal = new Lazy<IActivityJournal?>(() => Try(journal), LazyThreadSafetyMode.ExecutionAndPublication);
        _session = new Lazy<SessionIdentity?>(() => Try(session), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
        RunAsync(operation, () => _inner.ExecuteAsync(operation, ct));

    public Task<SqlHarnessOutcome> ExecuteWatchNdjsonAsync(
        SqlHarnessWatchOperation operation,
        TextWriter writer,
        CancellationToken ct = default) =>
        RunAsync(operation, () => _inner.ExecuteWatchNdjsonAsync(operation, writer, ct));

    private async Task<SqlHarnessOutcome> RunAsync(SqlHarnessOperation operation, Func<Task<SqlHarnessOutcome>> run)
    {
        var journal = _journal.Value;
        var handle = Begin(journal, operation);
        var stopwatch = Stopwatch.StartNew();
        SqlHarnessOutcome outcome;
        try
        {
            outcome = await run();
        }
        catch (OperationCanceledException)
        {
            Complete(journal, handle, OperationJournalDescriber.Cancelled(stopwatch.ElapsedMilliseconds));
            throw;
        }
        catch (Exception)
        {
            Complete(journal, handle, OperationJournalDescriber.Crashed(stopwatch.ElapsedMilliseconds));
            throw;
        }

        Complete(journal, handle, OperationJournalDescriber.DescribeEnd(outcome, stopwatch.ElapsedMilliseconds));
        if (journal is null || handle is null || outcome.EmissionReceipt is not { } inner)
            return outcome;

        var wrapped = new SqlHarnessEmissionReceipt(async (emitted, ct) =>
        {
            var exitCode = await inner.CompleteAsync(emitted, ct);
            journal.RecordEmission(handle, inner.RawFootprint, emitted);
            return exitCode;
        })
        {
            RawFootprint = inner.RawFootprint,
        };
        return outcome with { EmissionReceipt = wrapped };
    }

    private JournalHandle? Begin(IActivityJournal? journal, SqlHarnessOperation operation)
    {
        if (journal is null || _session.Value is not { } session)
            return null;
        try
        {
            return journal.Begin(session, OperationJournalDescriber.DescribeStart(operation));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Complete(IActivityJournal? journal, JournalHandle? handle, OperationEnd end)
    {
        try
        {
            journal?.Complete(handle, end);
        }
        catch (Exception)
        {
            // IActivityJournal implementations do not throw; this guards third-party implementations.
        }
    }

    private static T? Try<T>(Func<T> factory) where T : class
    {
        try
        {
            return factory();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
```

`outcome with { ... }` copies the computed `MachineError` backing field, so machine errors are preserved. The contract test proves it.

- [ ] **Step 5: Wire the CLI composition root**

`src/SqlHarness.Cli/Program.cs`:

```csharp
using SqlHarness.Cli;
using SqlHarness.Core;

var config = SqlHarnessConfigLoader.Load();
if (config.Warning is not null)
    Console.Error.WriteLine(config.Warning);

var module = new JournalingModule(
    new SqlHarnessModule(),
    () => ActivityJournal.Open(config.Config.Journal, Console.Error),
    () => SessionIdentities.Cli(ProcessInfo.Current));

return await SqlHarnessCli.Create(module).RunAsync(args);
```

The journal and the identity are lazy, so `--help`, `capabilities`, `doctor`, `validate`, and `mcp serve` never open the database or walk processes. `mcp serve` builds its own modules in Task 6.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Journal"`
Expected: all PASS.

Then run the whole non-integration suite to catch contract drift:

Run: `dotnet test SqlHarness.sln --filter "FullyQualifiedName!~Integration"`
Expected: all PASS. If any existing CLI test now fails because stderr contains `activity journal` text, that test runs with an unwritable `SQLHARNESS_HOME`. **STOP** and report which test, rather than suppressing the warning.

- [ ] **Step 7: Commit**

```bash
git add src/SqlHarness.Core/Contracts.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Core/Journal/JournalingModule.cs src/SqlHarness.Cli/Program.cs tests/SqlHarness.Tests/Journal
git commit -m "Journal every CLI operation without changing output"
```

---

### Task 6: MCP wiring

**Files:**
- Modify: `src/SqlHarness.Mcp/McpProcessContext.cs`, `src/SqlHarness.Mcp/McpHost.cs`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`, `src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs`
- Test: `tests/SqlHarness.Mcp.Tests/McpJournalTests.cs`

**Interfaces:**
- Consumes: `JournalingModule`, `ActivityJournal.Open`, `SessionIdentities.Mcp`, `ProcessInfo.Current`, `SqlHarnessConfigLoader.Load`.
- Produces: `McpProcessContext.DecorateModules(Func<ISqlHarnessModule, ISqlHarnessModule>)` and `McpProcessContext.Decorate(ISqlHarnessModule)`. Every module the MCP host builds goes through `Decorate`.

- [ ] **Step 1: Write the failing test**

`tests/SqlHarness.Mcp.Tests/McpJournalTests.cs` drives the real host over pipes, the same way `McpLifecycleTests.Eof_on_stdin_shuts_the_host_down_cleanly` does, under the `McpScopeHome` collection that isolates `SQLHARNESS_HOME`:

```csharp
using System.IO.Pipelines;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp;

namespace SqlHarness.Mcp.Tests;

[Collection("McpScopeHome")]
public sealed class McpJournalTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-journal-" + Guid.NewGuid().ToString("N"));

    public McpJournalTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        SqliteConnection.ClearAllPools();
        Directory.Delete(_home, true);
    }

    public static TheoryData<string> Modes => new() { "fixed", "request" };

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Tool_call_records_session_from_client_info(string mode)
    {
        var options = mode == "fixed"
            ? new McpServerOptions { Profile = "mcp-t5" }
            : new McpServerOptions { RequestScope = true, AllowedProfiles = ["sample-a"] };
        Func<IReadOnlyDictionary<string, TargetProfile>> profiles = () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-t5"] = new TargetProfile("mcp-unreachable.invalid", "reportdb", new Dictionary<string, string>(), "integrated"),
            ["sample-a"] = new("server-a.invalid", "database-a", new Dictionary<string, string> { ["tenant"] = "^a$" }, "integrated"),
        };
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var log = new StringWriter();
        var hostTask = McpHost.RunAsync(options, clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), log, profiles, cts.Token);

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "claude-code", Version = "9.9.9" },
                ProtocolVersion = McpHost.PinnedProtocolVersion,
            },
            NullLoggerFactory.Instance,
            cts.Token))
        {
            var result = await client.CallToolAsync("sqlharness_gain", new Dictionary<string, object?>(), cancellationToken: cts.Token);
            Assert.NotEqual(true, result.IsError);
        }

        await clientToServer.Writer.CompleteAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask.WaitAsync(Budget, cts.Token));

        var database = Path.Combine(_home, "data", "activity.db");
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.agent_kind, s.transport, s.source, s.client_name, s.client_version, s.mcp_mode, s.session_key, o.operation, o.status
            FROM operations o JOIN sessions s ON s.id = o.session_id
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("claude", reader.GetString(0));
        Assert.Equal("mcp", reader.GetString(1));
        Assert.Equal("mcp-clientinfo", reader.GetString(2));
        Assert.Equal("claude-code", reader.GetString(3));
        Assert.Equal("9.9.9", reader.GetString(4));
        Assert.Equal(mode, reader.GetString(5));
        Assert.StartsWith("mcp:", reader.GetString(6));
        Assert.Equal("gain", reader.GetString(7));
        Assert.Equal("succeeded", reader.GetString(8));
        Assert.False(reader.Read());
        Assert.DoesNotContain("activity journal", log.ToString());
    }
}
```


- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlHarness.Mcp.Tests --filter FullyQualifiedName~McpJournalTests`
Expected: FAIL because `activity.db` does not exist (SqliteException `unable to open database file`).

- [ ] **Step 3: Add decoration to the process context**

`src/SqlHarness.Mcp/McpProcessContext.cs`. Add a field and two members, and route `CreateModule` through them:

```csharp
    private Func<ISqlHarnessModule, ISqlHarnessModule> _decorator = static module => module;

    /// <summary>Installs the host's module decorator (activity journal); call before wiring tools.</summary>
    public void DecorateModules(Func<ISqlHarnessModule, ISqlHarnessModule> decorator) =>
        _decorator = decorator ?? throw new ArgumentNullException(nameof(decorator));

    public ISqlHarnessModule Decorate(ISqlHarnessModule module) => _decorator(module);

    /// <summary>Composition for target-free tools; it performs no connection.</summary>
    public ISqlHarnessModule CreateModule() => Decorate(new SqlHarnessModule(() => Profiles));
```

Replace the existing one-line `CreateModule` with the version above.

`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`, in `CreateTools(McpProcessContext process, ...)`, change the fixed-mode line to:

```csharp
        if (!process.RequestScope)
            return CreateTools(process.FixedScope!, moduleFactory?.Invoke(process.FixedScope!) ?? process.Decorate(process.FixedScope!.CreateModule()), process.Gate, clock, hostShutdown);
```

`src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs`, line 23, change the default factory:

```csharp
    private readonly Func<McpScope, ISqlHarnessModule> _moduleFactory = moduleFactory ?? (scope => process.Decorate(scope.CreateModule()));
```

Confirm the primary-constructor parameter is named `process`. If it is not, use its actual name.

- [ ] **Step 4: Install the decorator in the host**

`src/SqlHarness.Mcp/McpHost.cs`, in the second `RunAsync` overload, insert immediately before `Tools.McpToolCatalog.Wire(...)`:

```csharp
        // Activity journal: content-free stderr diagnostics only; stdout stays protocol-only.
        var config = SqlHarnessConfigLoader.Load();
        if (config.Warning is not null)
            log.WriteLine(config.Warning);
        var journal = new Lazy<IActivityJournal>(
            () => ActivityJournal.Open(config.Config.Journal, log),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var sessionKey = "mcp:" + Guid.NewGuid().ToString("N");
        var mcpMode = process.RequestScope ? "request" : "fixed";
        ModelContextProtocol.Server.McpServer? running = null;
        process.DecorateModules(module => new JournalingModule(
            module,
            () => journal.Value,
            () => SessionIdentities.Mcp(ProcessInfo.Current, sessionKey, running?.ClientInfo?.Name, running?.ClientInfo?.Version, mcpMode)));
```

Then, right after `await using var server = ModelContextProtocol.Server.McpServer.Create(...);`, add:

```csharp
            running = server;
```

Session identity resolves lazily inside the first tool call, which is after `initialize`, so `ClientInfo` is populated. A module created per request (request mode) resolves identity again. That is a short process walk, and the session key stays the same.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SqlHarness.Mcp.Tests`
Expected: all PASS, including the existing stderr-leak and stdio tests. If `McpStderrLeakRegressionTests` fails because of the config warning line, check that the test home has no `config.json`. The warning appears only for an invalid file.

- [ ] **Step 6: Commit**

```bash
git add src/SqlHarness.Mcp tests/SqlHarness.Mcp.Tests/McpJournalTests.cs
git commit -m "Journal MCP tool calls with client-info session identity"
```

---

### Task 7: Doctor diagnostics, documentation, and gates

**Files:**
- Modify: `src/SqlHarness.Cli/Commands/DoctorCommand.cs`, `AGENTS.md`, `README.md`, `docs/mcp.md`
- Test: `tests/SqlHarness.Tests/Cli/DoctorJournalConfigTests.cs` (new)

**Interfaces:**
- Consumes: `SqlHarnessConfigLoader.Load`, `SqlHarnessPaths.ActivityDatabase`.
- Produces: `doctor --json` additive fields `configFilePresent`, `configValid`, `journalEnabled`, `journalStoreSensitive`, `activityJournalPresent`.

- [ ] **Step 1: Write the failing doctor test**

`tests/SqlHarness.Tests/Cli/DoctorJournalConfigTests.cs`:

```csharp
using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

[Collection(SqlHarnessHomeCollection.Name)]
public sealed class DoctorJournalConfigTests : IDisposable
{
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sqlharness-doctor-" + Guid.NewGuid().ToString("N"));

    public DoctorJournalConfigTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        Directory.Delete(_home, true);
    }

    [Fact]
    public async Task Doctor_reports_invalid_config_as_fail_closed()
    {
        File.WriteAllText(Path.Combine(_home, "config.json"), """{ "journal": { "storeSensitive": true }, "bogus": 1 }""");
        var output = new StringWriter();

        var exitCode = await SqlHarnessCli.Create(new NoopModule(), output).RunAsync(["doctor", "--json"]);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.True(root.GetProperty("configFilePresent").GetBoolean());
        Assert.False(root.GetProperty("configValid").GetBoolean());
        Assert.True(root.GetProperty("journalEnabled").GetBoolean());
        Assert.False(root.GetProperty("journalStoreSensitive").GetBoolean());
        Assert.False(root.GetProperty("activityJournalPresent").GetBoolean());
    }

    [Fact]
    public async Task Doctor_reports_missing_config_as_valid_defaults()
    {
        var output = new StringWriter();

        await SqlHarnessCli.Create(new NoopModule(), output).RunAsync(["doctor", "--json"]);

        using var document = JsonDocument.Parse(output.ToString());
        Assert.False(document.RootElement.GetProperty("configFilePresent").GetBoolean());
        Assert.True(document.RootElement.GetProperty("configValid").GetBoolean());
    }

    private sealed class NoopModule : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~DoctorJournalConfigTests`
Expected: FAIL with `KeyNotFoundException` for `configFilePresent`.

- [ ] **Step 3: Implement**

In `DoctorCommand.ExecuteAsync`, before building `report`:

```csharp
        var config = SqlHarnessConfigLoader.Load();
```

Add these properties to the anonymous `report` object after `profileDirectoryPresent`:

```csharp
            configFilePresent = config.Status != SqlHarnessConfigStatus.Missing,
            configValid = config.Status != SqlHarnessConfigStatus.Invalid,
            journalEnabled = config.Config.Journal.Enabled,
            journalStoreSensitive = config.Config.Journal.StoreSensitive,
            activityJournalPresent = File.Exists(SqlHarnessPaths.ActivityDatabase),
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~DoctorJournalConfigTests|FullyQualifiedName~DoctorCommandTests"`
Expected: PASS.

- [ ] **Step 5: Documentation**

`AGENTS.md`, under **Safety contract**, add one bullet after the bullet about locally sensitive artifacts:

```markdown
- Every CLI command and MCP tool call is recorded in a local activity journal (`~/.sqlharness/data/activity.db`, owner-only on Unix): operation, scope, target identity, status, timings, token footprints, and a SHA-256 hash of the SQL. Session identity is implicit (MCP `clientInfo`, or the CLI's nearest `claude`/`codex` ancestor process); agents send nothing extra. SQL text is stored only with `journal.storeSensitive: true` in `~/.sqlharness/config.json`; parameter values, secrets, and result cells are never stored. An invalid `config.json` falls back to defaults (hash-only). Journal failures never change output or exit codes. Treat `activity.db` as locally sensitive.
```

`README.md`: add a short `### Activity journal` subsection near the existing gain/artifacts documentation with the same facts, plus this config example:

```json
{
  "journal": { "enabled": true, "storeSensitive": false }
}
```

Also state that `retention` and `dashboard` keys are accepted but have no effect until later releases.

`docs/mcp.md`: add one sentence in the stdout/stderr section: "Tool calls are recorded in the local activity journal with the client's `clientInfo` name and version; journal diagnostics go to stderr only, and journal failures never change a tool result."

- [ ] **Step 6: Run both gates**

Run: `pwsh ./scripts/verify.ps1`
Expected: restore, build (`-warnaserror`), test, and format all succeed. If `format` fails, run `dotnet format SqlHarness.sln` and include the result in this task's commit.

Run: `pwsh ./scripts/verify-linux.ps1`
Expected: all four stages succeed in WSL. This exercises `LinuxProcessInfo` and the owner-only modes for real. If `Current_process_is_visible_on_windows_and_linux` fails on Linux, print the `/proc/self/stat` the test saw and **STOP** to report it.

- [ ] **Step 7: Commit**

```bash
git add src/SqlHarness.Cli/Commands/DoctorCommand.cs tests/SqlHarness.Tests/Cli/DoctorJournalConfigTests.cs AGENTS.md README.md docs/mcp.md
git commit -m "Report journal config in doctor and document the activity journal"
```

---

## Not in this phase

These items are deferred to the next plans. Do not implement them here.

- **Phase 2:** `operation_metrics`, `operation_table_io`, `plans`, `operation_plans` (schema v2 migration); extended `StatisticsIoParser`; plan and EXPLAIN metric extraction; `artifact_dir` and `summary_json`; matrix-cell targets.
- **Phase 3:** `sqlharness dashboard`, the lock file, auth, API, SSE, `abandoned` detection, and `watch` `progress_json`.
- **Phase 4:** React/shadcn SPA and the `ui` gate stage.
- **Phase 5:** autostart from `mcp serve`, idle shutdown, retention.
