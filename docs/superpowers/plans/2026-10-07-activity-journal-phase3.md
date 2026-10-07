# Activity Journal — Phase 3 (dashboard server and read-only API) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `sqlharness dashboard` starts a loopback-only HTTP server (one per user, guarded by a lock file) that serves a token-protected, read-only JSON API and a live SSE feed over `~/.sqlharness/data/activity.db`. Running operations whose process died are reported as `abandoned`, and `watch` reports per-poll progress into the journal.

**Architecture:**
- A new `SqlHarness.Dashboard` class library (ASP.NET Core minimal API, `FrameworkReference Microsoft.AspNetCore.App`) holds the lock/singleton, the read-only `JournalReader`, the Kestrel server with its security middleware, the SSE `LiveFeed`, and `DashboardHost`, which orchestrates one `dashboard` invocation.
- The CLI gains a thin `dashboard` command that calls `DashboardHost`.
- Core gains one write path: `watch` progress, plumbed through an internal, non-serialized callback on `SqlHarnessWatchOperation` that `JournalingModule` installs.
- `abandoned` is computed at read time from process liveness, so the dashboard never writes to the journal.
- The SPA is phase 4. This phase serves a minimal embedded placeholder page.

**Tech Stack:** .NET 8, ASP.NET Core minimal APIs and Kestrel (shared framework, no new NuGet packages), `Microsoft.Data.Sqlite` 10.0.8, Spectre.Console.Cli, xUnit with real Kestrel on an ephemeral port.

**Spec:** `docs/superpowers/specs/2026-10-06-activity-dashboard-design.md` ("Dashboard server", "API", "Implementation phases → 3"). Earlier plans: `docs/superpowers/plans/2026-10-06-activity-journal-phase1.md`, `docs/superpowers/plans/2026-10-07-activity-journal-phase2.md`.

## Global Constraints

- Target framework stays `net8.0` in every project, including `SqlHarness.Dashboard`. `TreatWarningsAsErrors` is on, and `dotnet format --verify-no-changes` must pass.
- The server binds only to `127.0.0.1`. It never binds `0.0.0.0`, `::`, or `localhost` name resolution. `ASPNETCORE_URLS` and similar environment settings must not change the binding.
- Every request is rejected unless the `Host` header is exactly `127.0.0.1:<port>` or `localhost:<port>` (400).
- Every request is rejected unless it carries the session cookie (401), except the `?t=<token>` exchange. The token is 32 random bytes, base64url, compared in constant time.
- The API is read-only. Only `GET` and `HEAD` are accepted (405 otherwise), and no endpoint writes to `activity.db`. The single exception is the startup migration done through `ActivityJournal.Open`, the same code the CLI already runs.
- Responses carry `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`, and a `self`-only `Content-Security-Policy`.
- The dashboard never logs request URLs, tokens, SQL, or row content. ASP.NET logging providers are cleared.
- Agent-visible output and exit codes of every existing command stay byte-identical. The `watch` progress callback is internal, `[JsonIgnore]`, invoked best-effort, and stops after the first failed journal write.
- `dashboard` exit codes: `0` normal or already running, `2` invalid arguments, `6` local storage (lock held without a published address, bind failure, journal schema newer than the binary).
- Both gates must be green before the work is done: `pwsh ./scripts/verify.ps1` and `pwsh ./scripts/verify-linux.ps1`.

## Review Focus

1. **Another local web page or process** probing `http://127.0.0.1:<port>/api/...` must not read history, whether without the cookie, with a foreign `Host` header (DNS rebinding), or by POSTing. Pinned by Task 4, `Requests_without_cookie_or_with_foreign_host_are_rejected` and `Only_get_endpoints_exist`.
2. **A running operation whose CLI process was killed** must show as `abandoned` in lists, detail, stats, and the live feed, while a running operation of a live process stays `running`. Pinned by Task 3, `Dead_running_operations_are_reported_abandoned`, and Task 5, `Live_feed_reports_a_running_operation_turning_abandoned`.
3. **Two `sqlharness dashboard` invocations** must result in one server. The second prints and opens the first one's URL and exits `0`, and a stale `dashboard.json` left by a crashed server must not be trusted. Pinned by Task 2, `Stale_info_file_is_not_trusted`, and Task 6, `Second_invocation_reuses_the_running_dashboard`.
4. **The configured port being taken** by another program must not fail the dashboard. It moves to the next free port and publishes that port. Pinned by Task 4, `Busy_preferred_port_falls_back_to_the_next_free_port`.
5. **A locked or busy journal during a long `watch`** must not slow the watch on every poll. After the first failed progress write, progress recording stops. Pinned by Task 1, `Progress_recording_stops_after_the_first_failed_write`.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/SqlHarness.Core/Contracts.cs` (modify) | `SqlHarnessWatchOperation.Progress` |
| `src/SqlHarness.Core/WatchRunner.cs` (modify) | Invoke progress per completed poll |
| `src/SqlHarness.Core/Journal/JournalModels.cs`, `IActivityJournal.cs`, `ActivityJournal.cs`, `JournalingModule.cs` (modify) | `WatchProgress`, `RecordWatchProgress`, wiring |
| `src/SqlHarness.Core/SqlHarness.Core.csproj` (modify) | `InternalsVisibleTo SqlHarness.Dashboard` |
| `src/SqlHarness.Dashboard/SqlHarness.Dashboard.csproj` (new) | Class library, ASP.NET shared framework |
| `src/SqlHarness.Dashboard/DashboardLock.cs` (new) | Lock file plus published endpoint |
| `src/SqlHarness.Dashboard/ProcessLiveness.cs` (new) | `abandoned` computation |
| `src/SqlHarness.Dashboard/DashboardModels.cs` (new) | API DTO records |
| `src/SqlHarness.Dashboard/JournalReader.cs` (new) | Read-only queries |
| `src/SqlHarness.Dashboard/DashboardSecurity.cs` (new) | Host gate, token exchange, cookie auth, headers, method gate |
| `src/SqlHarness.Dashboard/DashboardServer.cs` (new) | Kestrel, endpoints, port fallback |
| `src/SqlHarness.Dashboard/LiveFeed.cs` (new) | SSE |
| `src/SqlHarness.Dashboard/BrowserLauncher.cs` (new) | Opens the URL |
| `src/SqlHarness.Dashboard/DashboardHost.cs` (new) | One `dashboard` invocation end to end |
| `src/SqlHarness.Dashboard/wwwroot/index.html` (new) | Placeholder page until phase 4 |
| `src/SqlHarness.Cli/Commands/DashboardCommand.cs` (new), `SqlHarnessCli.cs`, `SqlHarness.Cli.csproj` (modify) | CLI wiring |
| `tests/SqlHarness.Tests/SqlHarness.Tests.csproj` (modify), `tests/SqlHarness.Tests/Dashboard/*.cs` (new) | Tests |
| `README.md`, `AGENTS.md`, spec (modify) | Documentation |

---

### Task 1: `watch` progress in the journal

**Files:**
- Modify: `src/SqlHarness.Core/Contracts.cs`, `src/SqlHarness.Core/WatchRunner.cs`, `src/SqlHarness.Core/Journal/JournalModels.cs`, `IActivityJournal.cs`, `ActivityJournal.cs`, `JournalingModule.cs`
- Test: a new fact in `tests/SqlHarness.Tests/WatchTests.cs`; `tests/SqlHarness.Tests/Journal/JournalWatchProgressTests.cs` (new)

**Interfaces:**
- Produces:
  - `public sealed record WatchProgress(int Polls, int ChangedPolls, long ElapsedMilliseconds)` (in `JournalModels.cs`)
  - `SqlHarnessWatchOperation`: `[JsonIgnore] internal Action<WatchProgress>? Progress { get; init; }`
  - `IActivityJournal`: `bool RecordWatchProgress(JournalHandle? handle, WatchProgress progress) => false;` (default interface method)
  - `operations.progress_json` = `{"polls":n,"changedPolls":k,"elapsedMs":x}`

- [ ] **Step 1: Write the failing tests**

In `tests/SqlHarness.Tests/WatchTests.cs`, add next to `Watch_emits_first_and_changed_polls_only` (same helpers):

```csharp
    [Fact]
    public async Task Watch_reports_progress_after_every_completed_poll()
    {
        var clock = new FakeWatchClock();
        var session = FakeSession.WithScalarPolls(1, 1, 2, 2, 2);
        var progress = new List<WatchProgress>();

        var outcome = await Module(session, clock).ExecuteAsync(
            Watch(untilUnchanged: 2) with { Progress = progress.Add });

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal([1, 2, 3, 4, 5], progress.Select(p => p.Polls));
        Assert.Equal([1, 1, 2, 2, 2], progress.Select(p => p.ChangedPolls));
    }

    [Fact]
    public async Task Watch_progress_callback_failure_does_not_change_the_outcome()
    {
        var clock = new FakeWatchClock();
        var session = FakeSession.WithScalarPolls(1, 1);

        var outcome = await Module(session, clock).ExecuteAsync(
            Watch(untilUnchanged: 1) with { Progress = _ => throw new InvalidOperationException("journal") });

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(2, Assert.IsType<SqlHarnessWatchReport>(outcome.Report).PollCount);
    }
```

`tests/SqlHarness.Tests/Journal/JournalWatchProgressTests.cs`:

```csharp
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class JournalWatchProgressTests
{
    private static readonly SqlTargetRequest Target = new("local", new Dictionary<string, string>());

    private sealed class ProgressModule(int polls) : ISqlHarnessModule
    {
        public SqlHarnessWatchOperation? Received;

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Received = (SqlHarnessWatchOperation)operation;
            for (var poll = 1; poll <= polls; poll++)
                Received.Progress?.Invoke(new WatchProgress(poll, 1, poll * 10));
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        }
    }

    private sealed class CountingJournal : IActivityJournal
    {
        public int ProgressCalls;
        public JournalHandle? Begin(SessionIdentity session, OperationStart start) => new(1);
        public bool Complete(JournalHandle? handle, OperationEnd end) => true;
        public void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted) { }
        public bool RecordWatchProgress(JournalHandle? handle, WatchProgress progress)
        {
            ProgressCalls++;
            return false;
        }
    }

    private static SqlHarnessWatchOperation Watch() =>
        new(Target, "SELECT 1", [], 30, 10, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), null, 3);

    [Fact]
    public async Task Progress_is_written_while_running_and_kept_after_completion()
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null, TimeProvider.System);
        var inner = new ProgressModule(polls: 3);

        await new JournalingModule(inner, () => journal, () => JournalTestData.Session()).ExecuteAsync(Watch());

        Assert.NotNull(inner.Received!.Progress);
        var row = JournalDb.Rows(temp.DatabasePath, "SELECT status, progress_json FROM operations").Single();
        Assert.Equal("succeeded", row["status"]);
        Assert.Equal("""{"polls":3,"changedPolls":1,"elapsedMs":30}""", row["progress_json"]);
    }

    [Fact]
    public async Task Progress_recording_stops_after_the_first_failed_write()
    {
        var journal = new CountingJournal();

        await new JournalingModule(new ProgressModule(polls: 5), () => journal, () => JournalTestData.Session()).ExecuteAsync(Watch());

        Assert.Equal(1, journal.ProgressCalls);
    }

    [Fact]
    public async Task Disabled_journal_installs_no_progress_callback()
    {
        var inner = new ProgressModule(polls: 1);

        await new JournalingModule(inner, () => NullActivityJournal.Instance, () => JournalTestData.Session()).ExecuteAsync(Watch());

        Assert.Null(inner.Received!.Progress);
    }

    [Fact]
    public async Task Non_watch_operations_are_passed_through_unchanged()
    {
        SqlHarnessOperation? received = null;
        var query = new SqlHarnessQueryOperation(Target, "SELECT 1", [], 30, 10, false, null);
        var inner = new LambdaModule(operation => received = operation);
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null, TimeProvider.System);

        await new JournalingModule(inner, () => journal, () => JournalTestData.Session()).ExecuteAsync(query);

        Assert.Same(query, received);
    }

    private sealed class LambdaModule(Action<SqlHarnessOperation> onExecute) : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            onExecute(operation);
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        }
    }
}
```

The positional order of `SqlHarnessWatchOperation` is `(Target, Sql, Parameters, TimeoutSeconds, MaxRows, Interval, MaxDuration, Until, UntilUnchanged, HistoryLimit = 100)` (`Contracts.cs` line ~194). `Watch()` above follows it.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~JournalWatchProgressTests|FullyQualifiedName~Watch_reports_progress|FullyQualifiedName~Watch_progress_callback"`
Expected: build FAILS with `The type or namespace name 'WatchProgress' could not be found`.

- [ ] **Step 3: Implement the model, operation property, and runner call**

`src/SqlHarness.Core/Journal/JournalModels.cs`, append:

```csharp
/// <summary>Progress of a running watch after one completed poll; counts only, never result data.</summary>
public sealed record WatchProgress(int Polls, int ChangedPolls, long ElapsedMilliseconds);
```

`src/SqlHarness.Core/Contracts.cs`, inside `SqlHarnessWatchOperation` (next to `TypedParameters`):

```csharp
    /// <summary>
    /// Per-poll progress for the activity journal. Internal and never serialized; the
    /// runner invokes it best-effort after each completed poll, so a throwing callback
    /// cannot change the watch outcome.
    /// </summary>
    [JsonIgnore]
    internal Action<WatchProgress>? Progress { get; init; }
```

`src/SqlHarness.Core/WatchRunner.cs`, in `RunPollLoopAsync`. Immediately after the change-detection `if` block (the one that calls `sink.OnChanged`) and before `if (condition is not null)`, add:

```csharp
            ReportProgress(operation, poll, totalChangedPolls, elapsed);
```

Add the helper to `WatchRunner`:

```csharp
    private static void ReportProgress(SqlHarnessWatchOperation operation, int poll, int changedPolls, long elapsedMilliseconds)
    {
        if (operation.Progress is not { } progress)
            return;
        try
        {
            progress(new WatchProgress(poll, changedPolls, elapsedMilliseconds));
        }
        catch (Exception)
        {
            // Journal progress is best-effort; it never changes polling or the outcome.
        }
    }
```

- [ ] **Step 4: Implement the journal write and the module wiring**

`IActivityJournal.cs`, add to the interface:

```csharp
    /// <summary>Latest progress of a running watch. Returns false when not written; callers stop reporting then.</summary>
    bool RecordWatchProgress(JournalHandle? handle, WatchProgress progress) => false;
```

`ActivityJournal.cs`:

```csharp
    public bool RecordWatchProgress(JournalHandle? handle, WatchProgress progress)
    {
        if (handle is null || progress is null)
            return false;
        try
        {
            using var connection = Connect();
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE operations SET progress_json = $progress, updated_at = $now
                WHERE id = $id AND status = 'running';
                """;
            update.Parameters.AddWithValue("$id", handle.OperationId);
            update.Parameters.AddWithValue("$progress", string.Create(CultureInfo.InvariantCulture,
                $$"""{"polls":{{progress.Polls}},"changedPolls":{{progress.ChangedPolls}},"elapsedMs":{{progress.ElapsedMilliseconds}}}"""));
            update.Parameters.AddWithValue("$now", Timestamp(_time.GetUtcNow()));
            update.ExecuteNonQuery();
            return true;
        }
        catch (Exception)
        {
            Warn();
            return false;
        }
    }
```

Add `using System.Globalization;` if the file does not already have it.

`JournalingModule.cs`. Change the delegate shape so the module can hand the inner module a decorated watch operation:

```csharp
    public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
        RunAsync(operation, effective => _inner.ExecuteAsync(effective, ct));

    public Task<SqlHarnessOutcome> ExecuteWatchNdjsonAsync(
        SqlHarnessWatchOperation operation,
        TextWriter writer,
        CancellationToken ct = default) =>
        RunAsync(operation, effective => _inner.ExecuteWatchNdjsonAsync((SqlHarnessWatchOperation)effective, writer, ct));
```

In `RunAsync`, change the parameter to `Func<SqlHarnessOperation, Task<SqlHarnessOutcome>> run`. After `var handle = Begin(journal, operation);` add:

```csharp
        var effective = journal is not null && handle is not null && operation is SqlHarnessWatchOperation watch
            ? watch with { Progress = ProgressRecorder(journal, handle) }
            : operation;
```

Replace `outcome = await run();` with `outcome = await run(effective);`. Add the helper:

```csharp
    private static Action<WatchProgress> ProgressRecorder(IActivityJournal journal, JournalHandle handle)
    {
        var enabled = true;
        return progress =>
        {
            if (!enabled)
                return;
            try
            {
                // One failed write (busy or broken journal) stops progress for this watch,
                // so a locked journal cannot add its timeout to every poll.
                enabled = journal.RecordWatchProgress(handle, progress);
            }
            catch (Exception)
            {
                enabled = false;
            }
        };
    }
```

`Begin` returns `null` for a disabled journal (`NullActivityJournal`), so no callback is installed then.

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Watch|FullyQualifiedName~Journal"`
Expected: all PASS.

Run: `dotnet test SqlHarness.sln --filter "FullyQualifiedName!~Integration"`
Expected: all PASS. MCP watch tests compare mapped operations, and the journaling decorator is not in their composition, so they are unaffected.

- [ ] **Step 6: Commit**

```bash
git add src/SqlHarness.Core tests/SqlHarness.Tests/WatchTests.cs tests/SqlHarness.Tests/Journal/JournalWatchProgressTests.cs
git commit -m "Record watch progress in the activity journal"
```

---

### Task 2: Dashboard project and singleton lock

**Files:**
- Create: `src/SqlHarness.Dashboard/SqlHarness.Dashboard.csproj`, `src/SqlHarness.Dashboard/DashboardLock.cs`
- Modify: `SqlHarness.sln`, `src/SqlHarness.Core/SqlHarness.Core.csproj`, `tests/SqlHarness.Tests/SqlHarness.Tests.csproj`
- Test: `tests/SqlHarness.Tests/Dashboard/DashboardLockTests.cs`, `tests/SqlHarness.Tests/Dashboard/DashboardTestSupport.cs`

**Interfaces:**
- Consumes: `IProcessInfo`, `ProcessSnapshot`, `OwnerOnlyFiles` (Core, internal; visible through the new `InternalsVisibleTo`).
- Produces:
  - `public sealed record DashboardEndpoint(int Pid, DateTimeOffset? StartedAt, int Port, string Token)` with `Uri BaseUri` (`http://127.0.0.1:{Port}/`) and `Uri OpenUri` (`{BaseUri}?t={Token}`)
  - `public sealed class DashboardLock : IDisposable` with `static DashboardLock? TryAcquire(string home)`, `void Publish(DashboardEndpoint endpoint)`, `static DashboardEndpoint? ReadRunning(string home, IProcessInfo processes)`, `static Task<DashboardEndpoint?> WaitForRunningAsync(string home, IProcessInfo processes, TimeSpan timeout, CancellationToken ct)`
  - Test support: `FakeProcesses : IProcessInfo` with `Alive(int pid, DateTimeOffset? startedAt)`.

- [ ] **Step 1: Create the project**

`src/SqlHarness.Dashboard/SqlHarness.Dashboard.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="SqlHarness.Tests" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\SqlHarness.Core\SqlHarness.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="wwwroot\index.html" LogicalName="SqlHarness.Dashboard.wwwroot.index.html" />
  </ItemGroup>

</Project>
```

`src/SqlHarness.Dashboard/wwwroot/index.html` (placeholder until phase 4):

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>SQLHarness dashboard</title>
</head>
<body>
  <h1>SQLHarness dashboard</h1>
  <p>The API is available under <code>/api</code>. The interface ships in a later release.</p>
</body>
</html>
```

`src/SqlHarness.Core/SqlHarness.Core.csproj`, add to the `InternalsVisibleTo` item group:

```xml
    <InternalsVisibleTo Include="SqlHarness.Dashboard" />
```

`tests/SqlHarness.Tests/SqlHarness.Tests.csproj`, add:

```xml
    <ProjectReference Include="..\..\src\SqlHarness.Dashboard\SqlHarness.Dashboard.csproj" />
```

Run:

```bash
dotnet sln SqlHarness.sln add src/SqlHarness.Dashboard/SqlHarness.Dashboard.csproj
dotnet build SqlHarness.sln -warnaserror
```

Expected: build succeeds and `dotnet sln SqlHarness.sln list` shows the project under `src`. **STOP** if restore or build reports a package downgrade or conflict warning (NU1605/NU1608) between `ModelContextProtocol`'s `Microsoft.Extensions.*` dependencies and the ASP.NET shared framework, and report the exact message. Do not suppress the warning.

- [ ] **Step 2: Write test support and the failing lock tests**

`tests/SqlHarness.Tests/Dashboard/DashboardTestSupport.cs`:

```csharp
using SqlHarness.Core;

namespace SqlHarness.Tests.Dashboard;

internal sealed class FakeProcesses : IProcessInfo
{
    private readonly Dictionary<int, ProcessSnapshot> _alive = new();

    public int CurrentPid => Environment.ProcessId;

    public FakeProcesses Alive(int pid, DateTimeOffset? startedAt)
    {
        _alive[pid] = new ProcessSnapshot(pid, null, "proc", startedAt, null);
        return this;
    }

    public void Clear() => _alive.Clear();

    public ProcessSnapshot? Get(int pid) => _alive.GetValueOrDefault(pid);
}

internal sealed class TempHome : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sqlharness-dash-" + Guid.NewGuid().ToString("N"));

    public TempHome() => Directory.CreateDirectory(Path);

    public string DatabasePath => System.IO.Path.Combine(Path, "data", "activity.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
            // A just-stopped server may still release a handle; temp cleanup is best-effort.
        }
    }
}
```

`tests/SqlHarness.Tests/Dashboard/DashboardLockTests.cs`:

```csharp
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class DashboardLockTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Second_acquire_fails_while_the_first_is_held()
    {
        using var home = new TempHome();

        using var first = DashboardLock.TryAcquire(home.Path);
        var second = DashboardLock.TryAcquire(home.Path);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void Lock_is_released_on_dispose()
    {
        using var home = new TempHome();

        DashboardLock.TryAcquire(home.Path)!.Dispose();

        using var again = DashboardLock.TryAcquire(home.Path);
        Assert.NotNull(again);
    }

    [Fact]
    public void Published_endpoint_is_read_back_while_its_process_lives()
    {
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(4242, Started, 47801, "tok"));

        var running = DashboardLock.ReadRunning(home.Path, new FakeProcesses().Alive(4242, Started.AddMilliseconds(400)));

        Assert.Equal(new DashboardEndpoint(4242, Started, 47801, "tok"), running);
        Assert.Equal(new Uri("http://127.0.0.1:47801/?t=tok"), running!.OpenUri);
    }

    [Fact]
    public void Stale_info_file_is_not_trusted()
    {
        using var home = new TempHome();
        using (var held = DashboardLock.TryAcquire(home.Path)!)
            held.Publish(new DashboardEndpoint(4242, Started, 47801, "tok"));
        File.WriteAllText(Path.Combine(home.Path, "dashboard.json"),
            """{"pid":4242,"startedAt":"2026-10-07T08:00:00+00:00","port":47801,"token":"tok"}""");

        Assert.Null(DashboardLock.ReadRunning(home.Path, new FakeProcesses()));
        Assert.Null(DashboardLock.ReadRunning(home.Path, new FakeProcesses().Alive(4242, Started.AddHours(1))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"pid":1,"port":99999,"token":"x"}""")]
    [InlineData("""{"pid":1,"port":47800,"token":""}""")]
    public void Malformed_info_file_reads_as_not_running(string content)
    {
        using var home = new TempHome();
        File.WriteAllText(Path.Combine(home.Path, "dashboard.json"), content);

        Assert.Null(DashboardLock.ReadRunning(home.Path, new FakeProcesses().Alive(1, null)));
    }

    [Fact]
    public void Dispose_removes_the_published_info_file()
    {
        using var home = new TempHome();
        var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(1, null, 47800, "tok"));

        held.Dispose();

        Assert.False(File.Exists(Path.Combine(home.Path, "dashboard.json")));
    }

    [Fact]
    public async Task Wait_for_running_returns_null_after_the_timeout()
    {
        using var home = new TempHome();

        var endpoint = await DashboardLock.WaitForRunningAsync(home.Path, new FakeProcesses(), TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Null(endpoint);
    }

    [Fact]
    public void Info_file_is_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;

        held.Publish(new DashboardEndpoint(1, null, 47800, "tok"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(home.Path, "dashboard.json")));
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~DashboardLockTests`
Expected: build FAILS with `The type or namespace name 'DashboardLock' could not be found`.

- [ ] **Step 4: Implement**

`src/SqlHarness.Dashboard/DashboardLock.cs`:

```csharp
using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

public sealed record DashboardEndpoint(int Pid, DateTimeOffset? StartedAt, int Port, string Token)
{
    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    public Uri OpenUri => new($"http://127.0.0.1:{Port}/?t={Token}");
}

/// <summary>
/// One dashboard per SQLHarness home. The server holds an exclusive lock on
/// <c>dashboard.lock</c> for its whole lifetime (FileShare.None; an advisory
/// flock on Unix) and publishes its endpoint in <c>dashboard.json</c>
/// (owner-only), which other processes read because the lock file itself is
/// unreadable while held on Windows.
/// </summary>
public sealed class DashboardLock : IDisposable
{
    internal const string LockFileName = "dashboard.lock";
    internal const string InfoFileName = "dashboard.json";
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly FileStream _lock;
    private readonly string _infoPath;
    private bool _published;

    private DashboardLock(FileStream lockStream, string infoPath)
    {
        _lock = lockStream;
        _infoPath = infoPath;
    }

    public static DashboardLock? TryAcquire(string home)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        Directory.CreateDirectory(home);
        try
        {
            var stream = new FileStream(Path.Combine(home, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            OwnerOnlyFiles.File(Path.Combine(home, LockFileName));
            return new DashboardLock(stream, Path.Combine(home, InfoFileName));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Publish(DashboardEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var temp = _infoPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(endpoint, Json));
        OwnerOnlyFiles.File(temp);
        File.Move(temp, _infoPath, overwrite: true);
        _published = true;
    }

    public static DashboardEndpoint? ReadRunning(string home, IProcessInfo processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        try
        {
            var path = Path.Combine(home, InfoFileName);
            if (!File.Exists(path))
                return null;
            var endpoint = JsonSerializer.Deserialize<DashboardEndpoint>(File.ReadAllText(path), Json);
            if (endpoint is null || endpoint.Port is < 1 or > 65535 || string.IsNullOrEmpty(endpoint.Token))
                return null;
            var process = processes.Get(endpoint.Pid);
            if (process is null)
                return null;
            if (endpoint.StartedAt is { } published && process.StartedAt is { } actual
                && (actual - published).Duration() > StartTolerance)
                return null;
            return endpoint;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static async Task<DashboardEndpoint?> WaitForRunningAsync(string home, IProcessInfo processes, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            if (ReadRunning(home, processes) is { } endpoint)
                return endpoint;
            if (DateTimeOffset.UtcNow >= deadline)
                return null;
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
    }

    public void Dispose()
    {
        if (_published)
        {
            try
            {
                File.Delete(_infoPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        _lock.Dispose();
    }
}
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~DashboardLockTests`
Expected: all PASS on Windows. The Linux gate in Task 7 proves the advisory-lock behavior.

- [ ] **Step 6: Commit**

```bash
git add SqlHarness.sln src/SqlHarness.Dashboard src/SqlHarness.Core/SqlHarness.Core.csproj tests/SqlHarness.Tests/SqlHarness.Tests.csproj tests/SqlHarness.Tests/Dashboard
git commit -m "Add dashboard project with a per-home singleton lock"
```

---

### Task 3: Read-only journal reader

**Files:**
- Create: `src/SqlHarness.Dashboard/ProcessLiveness.cs`, `DashboardModels.cs`, `JournalReader.cs`
- Test: `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs`, `tests/SqlHarness.Tests/Dashboard/JournalSeed.cs`

**Interfaces:**
- Consumes: the v2 schema (phases 1–2), `ActivityJournal` (to seed data in tests), `JournalSchema.CurrentVersion`, `PlanDistiller.Distill`, `PostgresPlanDistiller.Distill`.
- Produces:
  - `internal sealed class ProcessLiveness(IProcessInfo processes)` with `string Status(string stored, long hostPid, string? hostStartedAt)`
  - DTOs (public, in `DashboardModels.cs`): `Page<T>`, `SessionSummary`, `OperationSummary`, `SessionDetail`, `OperationDetail`, `VariantDetail`, `Spread`, `TableIoRow`, `PlanLinkRow`, `PostgresBuffers`, `StoredPlan`, `DashboardStats` and its row records, `SessionQuery`, `OperationQuery`, `StatsQuery`
  - `public sealed class JournalReader(string databasePath, IProcessInfo processes)` with:
    - `long? SchemaVersion()`
    - `Page<SessionSummary> Sessions(SessionQuery)`
    - `SessionDetail? Session(long id)`
    - `Page<OperationSummary> Operations(OperationQuery)`
    - `OperationDetail? Operation(long id)`
    - `StoredPlan? Plan(string hash)`
    - `DashboardStats Stats(StatsQuery)`
    - `IReadOnlyList<OperationSummary> OperationsUpdatedSince(SqliteConnection connection, string cursor, int limit)`
    - `IReadOnlyList<SessionSummary> SessionsSeenSince(SqliteConnection connection, string cursor, int limit)`
    - `IReadOnlyList<OperationSummary> RunningOperations(SqliteConnection connection)`
    - `SqliteConnection? OpenReadOnly()`
  - Constants: `JournalReader.DefaultLimit = 50`, `MaximumLimit = 200`, `SessionOperationLimit = 500`, `TopLimit = 20`; `OperationSummary` flag rule `OverGranted`: granted ≥ 1024 KB and max used × 4 < granted.

- [ ] **Step 1: Write the seed helper and the failing tests**

`tests/SqlHarness.Tests/Dashboard/JournalSeed.cs` writes through the real journal, so reader and writer can never drift:

```csharp
using SqlHarness.Core;
using SqlHarness.Tests.Journal;

namespace SqlHarness.Tests.Dashboard;

internal sealed class JournalSeed
{
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly IActivityJournal _journal;

    public JournalSeed(string databasePath, bool storeSensitive = false) =>
        _journal = ActivityJournal.Open(databasePath, new JournalConfig { StoreSensitive = storeSensitive }, TextWriter.Null, _time);

    public static readonly DateTimeOffset HostStarted = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    public static SessionIdentity Session(string key, string agent = "claude", int hostPid = 5151) => new(
        key, agent, "process-tree", JournalTransport.Cli, null, null, null, 4242, HostStarted.AddMinutes(-5), hostPid, HostStarted, "/work");

    public JournalHandle Operation(
        SessionIdentity session, string operation = "query", string status = "succeeded", int exitCode = 0,
        string sql = "SELECT 1", long durationMs = 10, BenchmarkJournalRecord? benchmark = null, bool complete = true)
    {
        var handle = _journal.Begin(session, new OperationStart(operation, "local", new Dictionary<string, string> { ["tenant"] = "acme" },
            false, OperationJournalDescriber.SqlHash(sql), null, sql, null))!;
        _time.Now = _time.Now.AddSeconds(1);
        if (complete)
        {
            _journal.Complete(handle, new OperationEnd(status, exitCode, status == "rejected" ? "safety" : null, durationMs,
                "sqlserver", "srv", "db", 1, 3, 100, null, null));
            _journal.RecordEmission(handle, new OutputFootprint(400, 1), new OutputFootprint(40, 1));
        }

        if (benchmark is not null)
            _journal.RecordBenchmark(handle, benchmark);
        _time.Now = _time.Now.AddSeconds(1);
        return handle;
    }

    public static BenchmarkJournalRecord Benchmark(long readsMedian = 5, long physical = 0, long granted = 4096, long used = 256) =>
        new(
        [
            new JournalVariantMetrics("measure", null, null, 3, new CompareDistribution(10, 12, 14), new CompareDistribution(8, 9, 10),
                new CompareDistribution(readsMedian, readsMedian, readsMedian), 4096, granted, used, 4, 12, 10, 1, true, true, 1,
                [new JournalWait("PAGEIOLATCH_SH", 40, 7)], null,
                [new JournalTableIo("Orders", readsMedian, 1, physical, 0, 0, 0, 0, 0, physical > 0 ? 1 : 0)],
                [new JournalPlanLink(1, 0, new string('A', 64))]),
        ],
        [new JournalPlanDocument(new string('A', 64), "showplan-xml", Journal.PlanMetricsExtractorTests.ActualPlan)]);
}
```


`tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs`:

```csharp
using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class JournalReaderTests
{
    private static JournalReader Reader(TempHome home, FakeProcesses? processes = null) =>
        new(home.DatabasePath, processes ?? new FakeProcesses());

    [Fact]
    public void Missing_database_reads_as_empty()
    {
        using var home = new TempHome();
        var reader = Reader(home);

        Assert.Null(reader.SchemaVersion());
        Assert.Empty(reader.Sessions(new SessionQuery()).Items);
        Assert.Empty(reader.Operations(new OperationQuery()).Items);
        Assert.Equal(0, reader.Stats(new StatsQuery()).Statuses.Sum(s => s.Count));
    }

    [Fact]
    public void Sessions_are_newest_first_with_counts_and_keyset_paging()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var a = JournalSeed.Session("cli:a", "claude");
        var b = JournalSeed.Session("cli:b", "codex");
        seed.Operation(a);
        seed.Operation(a, status: "failed", exitCode: 5);
        seed.Operation(b, status: "rejected", exitCode: 2);

        var reader = Reader(home);
        var first = reader.Sessions(new SessionQuery(Limit: 1));
        var second = reader.Sessions(new SessionQuery(Limit: 1, Cursor: first.NextCursor));

        Assert.Equal("codex", Assert.Single(first.Items).AgentKind);
        Assert.Equal(1, first.Items[0].Rejected);
        var claude = Assert.Single(second.Items);
        Assert.Equal((2, 1), (claude.Operations, claude.Failed));
        Assert.Null(second.NextCursor);
        Assert.Equal("claude", Assert.Single(reader.Sessions(new SessionQuery(Agent: "claude")).Items).AgentKind);
    }

    [Fact]
    public void Dead_running_operations_are_reported_abandoned()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:live", hostPid: 100), complete: false);
        seed.Operation(JournalSeed.Session("cli:dead", hostPid: 200), complete: false);
        var processes = new FakeProcesses().Alive(100, JournalSeed.HostStarted.AddMilliseconds(300));
        var reader = Reader(home, processes);

        var statuses = reader.Operations(new OperationQuery()).Items.ToDictionary(o => o.SessionId, o => o.Status);

        Assert.Contains("running", statuses.Values);
        Assert.Contains("abandoned", statuses.Values);
        Assert.Single(reader.Operations(new OperationQuery(Status: "abandoned")).Items);
        Assert.Single(reader.Operations(new OperationQuery(Status: "running")).Items);
        var stats = reader.Stats(new StatsQuery());
        Assert.Equal(1, stats.Statuses.Single(s => s.Key == "abandoned").Count);
        Assert.Equal(1, stats.Statuses.Single(s => s.Key == "running").Count);
    }

    [Fact]
    public void Reused_pid_with_a_different_start_is_abandoned()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("cli:x", hostPid: 100), complete: false);

        var reader = Reader(home, new FakeProcesses().Alive(100, JournalSeed.HostStarted.AddHours(2)));

        Assert.Equal("abandoned", Assert.Single(reader.Operations(new OperationQuery()).Items).Status);
    }

    [Fact]
    public void Operation_detail_has_metrics_table_io_waits_and_plan_links()
    {
        using var home = new TempHome();
        var handle = new JournalSeed(home.DatabasePath).Operation(
            JournalSeed.Session("cli:m"), operation: "measure", benchmark: JournalSeed.Benchmark(readsMedian: 50, physical: 3, granted: 8192, used: 512));

        var detail = Reader(home).Operation(handle.OperationId)!;

        Assert.Equal("measure", detail.Operation.Operation);
        Assert.Equal("acme", detail.Vars!["tenant"]);
        Assert.Null(detail.SqlText);
        var variant = Assert.Single(detail.Variants);
        Assert.Equal(new Spread(50, 50, 50), variant.LogicalReads);
        Assert.Equal(8192, variant.GrantGrantedKb);
        Assert.Equal("PAGEIOLATCH_SH", variant.Waits!.Value[0].GetProperty("waitType").GetString());
        var table = Assert.Single(variant.TableIo);
        Assert.Equal(("Orders", 3L, 1), (table.Table, table.PhysicalReads, table.ColdRuns));
        var plan = Assert.Single(variant.Plans);
        Assert.False(plan.Stored);
        Assert.Equal(50, detail.Operation.LogicalReadsMedian);
        Assert.True(detail.Operation.HasSpill);
        Assert.True(detail.Operation.ColdCache);
        Assert.True(detail.Operation.OverGranted);
    }

    [Fact]
    public void Sensitive_journal_exposes_sql_text_and_stored_plans()
    {
        using var home = new TempHome();
        var handle = new JournalSeed(home.DatabasePath, storeSensitive: true)
            .Operation(JournalSeed.Session("cli:s"), operation: "measure", sql: "SELECT 42", benchmark: JournalSeed.Benchmark());
        var reader = Reader(home);

        var detail = reader.Operation(handle.OperationId)!;
        var stored = reader.Plan(new string('A', 64))!;

        Assert.Equal("SELECT 42", detail.SqlText);
        Assert.True(Assert.Single(Assert.Single(detail.Variants).Plans).Stored);
        Assert.Equal("showplan-xml", stored.Format);
        Assert.Equal(Journal.PlanMetricsExtractorTests.ActualPlan, stored.Document);
        Assert.Null(reader.Plan(new string('B', 64)));
    }

    [Fact]
    public void Operations_filter_by_session_status_and_operation_and_page()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var a = JournalSeed.Session("cli:a");
        for (var i = 0; i < 5; i++)
            seed.Operation(a, operation: i % 2 == 0 ? "query" : "ping");
        var reader = Reader(home);
        var sessionId = reader.Sessions(new SessionQuery()).Items.Single().Id;

        var page = reader.Operations(new OperationQuery(SessionId: sessionId, Operation: "query", Limit: 2));
        var rest = reader.Operations(new OperationQuery(SessionId: sessionId, Operation: "query", Limit: 2, Cursor: page.NextCursor));

        Assert.Equal(2, page.Items.Count);
        Assert.Single(rest.Items);
        Assert.True(page.Items[0].Id > page.Items[1].Id);
        Assert.All(page.Items.Concat(rest.Items), o => Assert.Equal("query", o.Operation));
    }

    [Fact]
    public void Stats_aggregate_days_statuses_sql_tables_waits_tokens_and_targets()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var s = JournalSeed.Session("cli:a");
        seed.Operation(s, sql: "SELECT 1", durationMs: 10);
        seed.Operation(s, sql: "SELECT 1", durationMs: 30);
        seed.Operation(s, operation: "measure", sql: "SELECT 2", durationMs: 100, benchmark: JournalSeed.Benchmark(readsMedian: 70, physical: 2));
        seed.Operation(s, status: "rejected", exitCode: 2, sql: "DELETE x");

        var stats = Reader(home).Stats(new StatsQuery());

        Assert.Equal(4, Assert.Single(stats.OperationsPerDay).Count);
        Assert.Equal("2026-10-07", stats.OperationsPerDay[0].Day);
        Assert.Equal(1, stats.Statuses.Single(x => x.Key == "rejected").Count);
        Assert.Equal(1, stats.ExitCodes.Single(x => x.Key == "2").Count);
        var top = stats.TopSqlByCount[0];
        Assert.Equal((OperationJournalDescriber.SqlHash("SELECT 1"), 2, 40L), (top.SqlHash, top.Count, top.TotalDurationMs));
        Assert.Equal(OperationJournalDescriber.SqlHash("SELECT 2"), stats.TopSqlByDuration[0].SqlHash);
        Assert.Equal(("Orders", 210L), (stats.TopTablesByLogicalReads[0].Table, stats.TopTablesByLogicalReads[0].LogicalReads));
        Assert.Equal(("PAGEIOLATCH_SH", 120d), (stats.TopWaits[0].WaitType, stats.TopWaits[0].TotalWaitMs));
        Assert.Equal(1, stats.SpillOperations);
        Assert.Equal(1, stats.ColdCacheOperations);
        Assert.Equal((400L, 40L), (stats.Tokens.Raw, stats.Tokens.Emitted));
        Assert.Equal(("local", "db", 4), (stats.Targets[0].Profile, stats.Targets[0].Database, stats.Targets[0].Count));
    }

    [Fact]
    public void Stats_respect_the_time_window()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("cli:a"));

        var stats = Reader(home).Stats(new StatsQuery(From: new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero)));

        Assert.Empty(stats.OperationsPerDay);
    }

    [Fact]
    public void Newer_schema_is_reported()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        Assert.Equal(99, Reader(home).SchemaVersion());
    }
}
```

`TotalWaitMs` = Σ(`averageWaitMs` × `runs`) = 40 × 3 = 120. Table reads use the same estimate of total work: median × runs = 70 × 3 = 210.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~JournalReaderTests`
Expected: build FAILS with `The type or namespace name 'JournalReader' could not be found`.

- [ ] **Step 3: Implement liveness and DTOs**

`src/SqlHarness.Dashboard/ProcessLiveness.cs`:

```csharp
using System.Globalization;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

/// <summary>
/// Read-time <c>abandoned</c> detection: a <c>running</c> row whose host process
/// (pid plus start time, because pids are reused) no longer exists. Platforms
/// without a process reader (macOS, <see cref="SelfOnlyProcessInfo"/>) report
/// rows as stored rather than guessing.
/// </summary>
internal sealed class ProcessLiveness(IProcessInfo processes)
{
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);
    private readonly Dictionary<(long Pid, string? Started), bool> _cache = new();

    internal string Status(string stored, long hostPid, string? hostStartedAt) =>
        stored == "running" && !IsAlive(hostPid, hostStartedAt) ? "abandoned" : stored;

    private bool IsAlive(long pid, string? startedAt)
    {
        if (processes is SelfOnlyProcessInfo)
            return true;
        if (_cache.TryGetValue((pid, startedAt), out var known))
            return known;

        ProcessSnapshot? snapshot;
        try
        {
            snapshot = pid is > 0 and <= int.MaxValue ? processes.Get((int)pid) : null;
        }
        catch (Exception)
        {
            snapshot = null;
        }

        var alive = snapshot is not null
            && (startedAt is null
                || snapshot.StartedAt is null
                || !DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stored)
                || (snapshot.StartedAt.Value - stored).Duration() <= StartTolerance);
        _cache[(pid, startedAt)] = alive;
        return alive;
    }
}
```

`src/SqlHarness.Dashboard/DashboardModels.cs`:

```csharp
using System.Text.Json;

namespace SqlHarness.Dashboard;

public sealed record Page<T>(IReadOnlyList<T> Items, long? NextCursor);

public sealed record SessionQuery(
    string? Agent = null, string? Transport = null, DateTimeOffset? From = null, DateTimeOffset? To = null,
    long? Cursor = null, int Limit = JournalReader.DefaultLimit);

public sealed record OperationQuery(
    long? SessionId = null, string? Status = null, string? Operation = null, DateTimeOffset? From = null,
    DateTimeOffset? To = null, long? Cursor = null, int Limit = JournalReader.DefaultLimit);

public sealed record StatsQuery(DateTimeOffset? From = null, DateTimeOffset? To = null);

public sealed record SessionSummary(
    long Id, string SessionKey, string AgentKind, string Transport, string Source, string? ClientName,
    string? ClientVersion, string? McpMode, string? Cwd, string FirstSeen, string LastSeen,
    int Operations, int Failed, int Rejected, int Running, int Abandoned);

public sealed record OperationSummary(
    long Id, long SessionId, string AgentKind, string Operation, string Status, int? ExitCode, string? ErrorKind,
    string StartedAt, string UpdatedAt, string? FinishedAt, long? DurationMs, string? Profile, string? Engine,
    string? Server, string? Database, bool MutationRequested, string? SqlHash, long? RowsReturned,
    long? LogicalReadsMedian, bool HasSpill, bool ColdCache, bool OverGranted, JsonElement? Progress);

public sealed record SessionDetail(SessionSummary Session, IReadOnlyList<OperationSummary> Operations);

public sealed record Spread(long Min, long Median, long Max);

public sealed record TableIoRow(
    string Table, long LogicalReads, long? ScanCount, long? PhysicalReads, long? PageServerReads, long? ReadAheadReads,
    long? LobLogicalReads, long? LobPhysicalReads, long? LobReadAheadReads, int ColdRuns);

public sealed record PlanLinkRow(int Repetition, int Ordinal, string Hash, bool Stored);

public sealed record PostgresBuffers(long? SharedHit, long? SharedRead, long? SharedDirtied, long? SharedWritten, long? TempRead, long? TempWritten);

public sealed record VariantDetail(
    int Ordinal, string Variant, string? ParameterSet, int? MatrixCell, int Runs,
    Spread? ElapsedMs, Spread? CpuMs, Spread? LogicalReads,
    long? GrantRequestedKb, long? GrantGrantedKb, long? GrantMaxUsedKb, int? Dop, long? CompileTimeMs, long? CompileCpuMs,
    int SpillCount, bool HasWarnings, bool HasImplicitConversion, int MissingIndexCount,
    JsonElement? Waits, PostgresBuffers? Postgres, IReadOnlyList<TableIoRow> TableIo, IReadOnlyList<PlanLinkRow> Plans);

public sealed record OperationDetail(
    OperationSummary Operation, SessionSummary Session, IReadOnlyDictionary<string, string>? Vars,
    string? CandidateSqlHash, string? SqlText, string? CandidateSqlText, long? RawTokens, long? EmittedTokens,
    string? ArtifactDirectory, JsonElement? Summary, IReadOnlyList<VariantDetail> Variants);

public sealed record StoredPlan(string Hash, string Format, string Document);

public sealed record DayAgentCount(string Day, string AgentKind, int Count);

public sealed record KeyCount(string Key, int Count);

public sealed record SqlHashStat(string SqlHash, int Count, long TotalDurationMs, long MaxDurationMs);

public sealed record TableReadStat(string Table, long LogicalReads, int Operations);

public sealed record WaitStat(string WaitType, double TotalWaitMs);

public sealed record TargetStat(string? Profile, string? Database, int Count);

public sealed record TokenStat(long Raw, long Emitted);

public sealed record DashboardStats(
    IReadOnlyList<DayAgentCount> OperationsPerDay,
    IReadOnlyList<KeyCount> Statuses,
    IReadOnlyList<KeyCount> ExitCodes,
    IReadOnlyList<KeyCount> Operations,
    IReadOnlyList<SqlHashStat> TopSqlByCount,
    IReadOnlyList<SqlHashStat> TopSqlByDuration,
    IReadOnlyList<TableReadStat> TopTablesByLogicalReads,
    IReadOnlyList<WaitStat> TopWaits,
    IReadOnlyList<TargetStat> Targets,
    TokenStat Tokens,
    int SpillOperations,
    int ColdCacheOperations);
```

- [ ] **Step 4: Implement the reader**

`src/SqlHarness.Dashboard/JournalReader.cs`:

```csharp
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

/// <summary>
/// Read-only queries over activity.db. Every call opens its own short-lived,
/// unpooled read-only connection; a missing database reads as empty. Running
/// rows are reported as <c>abandoned</c> when their host process is gone.
/// </summary>
public sealed class JournalReader(string databasePath, IProcessInfo processes)
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 200;
    public const int SessionOperationLimit = 500;
    public const int TopLimit = 20;
    internal const long OverGrantMinimumKb = 1024;

    private const string OperationColumns = """
        o.id, o.session_id, s.agent_kind, o.operation, o.status, o.exit_code, o.error_kind, o.started_at, o.updated_at,
        o.finished_at, o.duration_ms, o.profile, o.engine, o.server, o.database, o.mutation_requested, o.sql_hash,
        o.rows_returned, o.progress_json, o.host_pid, o.host_started_at,
        (SELECT m.logical_reads_median FROM operation_metrics m WHERE m.operation_id = o.id ORDER BY m.ordinal LIMIT 1) AS reads_median,
        EXISTS (SELECT 1 FROM operation_metrics m WHERE m.operation_id = o.id AND m.spill_count > 0) AS has_spill,
        EXISTS (SELECT 1 FROM operation_metrics m JOIN operation_table_io t ON t.metric_id = m.id
                WHERE m.operation_id = o.id AND t.cold_runs > 0) AS cold_cache,
        EXISTS (SELECT 1 FROM operation_metrics m WHERE m.operation_id = o.id AND m.grant_granted_kb >= 1024
                AND m.grant_max_used_kb IS NOT NULL AND m.grant_max_used_kb * 4 < m.grant_granted_kb) AS over_granted
        """;

    private const string SessionColumns = """
        s.id, s.session_key, s.agent_kind, s.transport, s.source, s.client_name, s.client_version, s.mcp_mode, s.cwd,
        s.first_seen, s.last_seen,
        (SELECT COUNT(*) FROM operations o WHERE o.session_id = s.id) AS ops,
        (SELECT COUNT(*) FROM operations o WHERE o.session_id = s.id AND o.status = 'failed') AS failed,
        (SELECT COUNT(*) FROM operations o WHERE o.session_id = s.id AND o.status = 'rejected') AS rejected
        """;

    public long? SchemaVersion()
    {
        using var connection = OpenReadOnly();
        return connection is null ? null : Scalar<long>(connection, "PRAGMA user_version;", []);
    }

    /// <summary>Read-only, unpooled connection; null when the database does not exist yet.</summary>
    public SqliteConnection? OpenReadOnly()
    {
        if (!File.Exists(databasePath))
            return null;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 2,
        }.ToString());
        connection.Open();
        return connection;
    }

    public Page<SessionSummary> Sessions(SessionQuery query)
    {
        using var connection = OpenReadOnly();
        if (connection is null)
            return new Page<SessionSummary>([], null);
        var limit = Clamp(query.Limit);
        var rows = Query(connection, $"""
            SELECT {SessionColumns} FROM sessions s
            WHERE ($agent IS NULL OR s.agent_kind = $agent)
              AND ($transport IS NULL OR s.transport = $transport)
              AND ($from IS NULL OR s.last_seen >= $from)
              AND ($to IS NULL OR s.first_seen < $to)
              AND ($cursor IS NULL OR s.id < $cursor)
            ORDER BY s.id DESC LIMIT $limit;
            """,
            [("$agent", query.Agent), ("$transport", query.Transport), ("$from", Iso(query.From)), ("$to", Iso(query.To)),
             ("$cursor", query.Cursor), ("$limit", limit + 1)],
            ReadSessionRow);
        var page = rows.Take(limit).ToArray();
        return new Page<SessionSummary>(WithRunning(connection, page), rows.Count > limit ? page[^1].Id : null);
    }

    public SessionDetail? Session(long id)
    {
        using var connection = OpenReadOnly();
        if (connection is null)
            return null;
        var session = Query(connection, $"SELECT {SessionColumns} FROM sessions s WHERE s.id = $id;", [("$id", id)], ReadSessionRow)
            .FirstOrDefault();
        if (session is null)
            return null;
        var liveness = new ProcessLiveness(processes);
        var operations = Query(connection, $"""
            SELECT {OperationColumns} FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE o.session_id = $id ORDER BY o.id DESC LIMIT $limit;
            """, [("$id", id), ("$limit", SessionOperationLimit)], reader => ReadOperation(reader, liveness));
        return new SessionDetail(WithRunning(connection, [session])[0], operations);
    }

    public Page<OperationSummary> Operations(OperationQuery query)
    {
        using var connection = OpenReadOnly();
        if (connection is null)
            return new Page<OperationSummary>([], null);
        var limit = Clamp(query.Limit);
        // abandoned is computed, not stored: filter running rows in SQL, then by liveness here.
        var storedStatus = query.Status is "abandoned" ? "running" : query.Status;
        var liveness = new ProcessLiveness(processes);
        var rows = Query(connection, $"""
            SELECT {OperationColumns} FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE ($session IS NULL OR o.session_id = $session)
              AND ($status IS NULL OR o.status = $status)
              AND ($operation IS NULL OR o.operation = $operation)
              AND ($from IS NULL OR o.started_at >= $from)
              AND ($to IS NULL OR o.started_at < $to)
              AND ($cursor IS NULL OR o.id < $cursor)
            ORDER BY o.id DESC LIMIT $limit;
            """,
            [("$session", query.SessionId), ("$status", storedStatus), ("$operation", query.Operation),
             ("$from", Iso(query.From)), ("$to", Iso(query.To)), ("$cursor", query.Cursor), ("$limit", limit + 1)],
            reader => ReadOperation(reader, liveness));
        var page = rows.Take(limit).ToArray();
        var next = rows.Count > limit ? page[^1].Id : (long?)null;
        if (query.Status is "running" or "abandoned")
            page = page.Where(row => row.Status == query.Status).ToArray();
        return new Page<OperationSummary>(page, next);
    }

    public OperationDetail? Operation(long id)
    {
        using var connection = OpenReadOnly();
        if (connection is null)
            return null;
        var liveness = new ProcessLiveness(processes);
        var summary = Query(connection, $"""
            SELECT {OperationColumns} FROM operations o JOIN sessions s ON s.id = o.session_id WHERE o.id = $id;
            """, [("$id", id)], reader => ReadOperation(reader, liveness)).FirstOrDefault();
        if (summary is null)
            return null;

        var extra = Query(connection, """
            SELECT vars_json, candidate_sql_hash, sql_text, candidate_sql_text, raw_tokens, emitted_tokens, artifact_dir, summary_json
            FROM operations WHERE id = $id;
            """, [("$id", id)], reader => (
                Vars: reader.IsDBNull(0) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(0)),
                CandidateHash: NullableString(reader, 1),
                SqlText: NullableString(reader, 2),
                CandidateText: NullableString(reader, 3),
                Raw: NullableLong(reader, 4),
                Emitted: NullableLong(reader, 5),
                Artifact: NullableString(reader, 6),
                Summary: Json(NullableString(reader, 7)))).Single();
        var session = Query(connection, $"SELECT {SessionColumns} FROM sessions s WHERE s.id = $id;",
            [("$id", summary.SessionId)], ReadSessionRow).Single();

        return new OperationDetail(
            summary,
            WithRunning(connection, [session])[0],
            extra.Vars,
            extra.CandidateHash,
            extra.SqlText,
            extra.CandidateText,
            extra.Raw,
            extra.Emitted,
            extra.Artifact,
            extra.Summary,
            Variants(connection, id));
    }

    public StoredPlan? Plan(string hash)
    {
        using var connection = OpenReadOnly();
        if (connection is null)
            return null;
        return Query(connection, "SELECT hash, format, gz FROM plans WHERE hash = $hash;", [("$hash", hash)], reader =>
        {
            using var gzip = new GZipStream(new MemoryStream((byte[])reader.GetValue(2)), CompressionMode.Decompress);
            using var text = new StreamReader(gzip, Encoding.UTF8);
            return new StoredPlan(reader.GetString(0), reader.GetString(1), text.ReadToEnd());
        }).FirstOrDefault();
    }

    public DashboardStats Stats(StatsQuery query)
    {
        using var connection = OpenReadOnly();
        if (connection is null)
            return new DashboardStats([], [], [], [], [], [], [], [], [], new TokenStat(0, 0), 0, 0);
        (string, object?)[] window = [("$from", Iso(query.From)), ("$to", Iso(query.To))];
        const string inWindow = "($from IS NULL OR o.started_at >= $from) AND ($to IS NULL OR o.started_at < $to)";

        var perDay = Query(connection, $"""
            SELECT substr(o.started_at, 1, 10) AS day, s.agent_kind, COUNT(*) FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE {inWindow} GROUP BY day, s.agent_kind ORDER BY day, s.agent_kind;
            """, window, r => new DayAgentCount(r.GetString(0), r.GetString(1), r.GetInt32(2)));

        var liveness = new ProcessLiveness(processes);
        var statuses = Query(connection, $"""
            SELECT o.status, COUNT(*) FROM operations o WHERE {inWindow} AND o.status <> 'running' GROUP BY o.status;
            """, window, r => new KeyCount(r.GetString(0), r.GetInt32(1))).ToList();
        var running = Query(connection, $"""
            SELECT o.host_pid, o.host_started_at FROM operations o WHERE {inWindow} AND o.status = 'running';
            """, window, r => liveness.Status("running", r.GetInt64(0), NullableString(r, 1)));
        foreach (var group in running.GroupBy(status => status))
            statuses.Add(new KeyCount(group.Key, group.Count()));

        var exitCodes = Query(connection, $"""
            SELECT CAST(o.exit_code AS TEXT), COUNT(*) FROM operations o WHERE {inWindow} AND o.exit_code IS NOT NULL
            GROUP BY o.exit_code ORDER BY o.exit_code;
            """, window, r => new KeyCount(r.GetString(0), r.GetInt32(1)));
        var operations = Query(connection, $"""
            SELECT o.operation, COUNT(*) FROM operations o WHERE {inWindow} GROUP BY o.operation ORDER BY COUNT(*) DESC, o.operation;
            """, window, r => new KeyCount(r.GetString(0), r.GetInt32(1)));

        const string sqlStats = "SELECT o.sql_hash, COUNT(*) AS n, COALESCE(SUM(o.duration_ms), 0) AS total, COALESCE(MAX(o.duration_ms), 0) FROM operations o";
        var byCount = Query(connection, $"""
            {sqlStats} WHERE {inWindow} AND o.sql_hash IS NOT NULL GROUP BY o.sql_hash ORDER BY n DESC, total DESC, o.sql_hash LIMIT {TopLimit};
            """, window, ReadSqlStat);
        var byDuration = Query(connection, $"""
            {sqlStats} WHERE {inWindow} AND o.sql_hash IS NOT NULL GROUP BY o.sql_hash ORDER BY total DESC, n DESC, o.sql_hash LIMIT {TopLimit};
            """, window, ReadSqlStat);

        var tables = Query(connection, $"""
            SELECT t.table_name, SUM(t.logical_reads * m.runs), COUNT(DISTINCT o.id)
            FROM operation_table_io t JOIN operation_metrics m ON m.id = t.metric_id JOIN operations o ON o.id = m.operation_id
            WHERE {inWindow} GROUP BY t.table_name ORDER BY 2 DESC, t.table_name LIMIT {TopLimit};
            """, window, r => new TableReadStat(r.GetString(0), r.GetInt64(1), r.GetInt32(2)));

        var waits = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (json, runs) in Query(connection, $"""
            SELECT m.waits_json, m.runs FROM operation_metrics m JOIN operations o ON o.id = m.operation_id
            WHERE {inWindow} AND m.waits_json IS NOT NULL;
            """, window, r => (r.GetString(0), r.GetInt32(1))))
        {
            using var document = JsonDocument.Parse(json);
            foreach (var wait in document.RootElement.EnumerateArray())
            {
                var type = wait.GetProperty("waitType").GetString() ?? "UNKNOWN";
                waits[type] = waits.GetValueOrDefault(type) + wait.GetProperty("averageWaitMs").GetDouble() * runs;
            }
        }

        var targets = Query(connection, $"""
            SELECT o.profile, o.database, COUNT(*) FROM operations o WHERE {inWindow}
            GROUP BY o.profile, o.database ORDER BY COUNT(*) DESC LIMIT {TopLimit};
            """, window, r => new TargetStat(NullableString(r, 0), NullableString(r, 1), r.GetInt32(2)));
        var tokens = Query(connection, $"""
            SELECT COALESCE(SUM(o.raw_tokens), 0), COALESCE(SUM(o.emitted_tokens), 0) FROM operations o WHERE {inWindow};
            """, window, r => new TokenStat(r.GetInt64(0), r.GetInt64(1))).Single();
        var spills = Scalar<long>(connection, $"""
            SELECT COUNT(DISTINCT o.id) FROM operations o JOIN operation_metrics m ON m.operation_id = o.id
            WHERE {inWindow} AND m.spill_count > 0;
            """, window);
        var cold = Scalar<long>(connection, $"""
            SELECT COUNT(DISTINCT o.id) FROM operations o JOIN operation_metrics m ON m.operation_id = o.id
            JOIN operation_table_io t ON t.metric_id = m.id WHERE {inWindow} AND t.cold_runs > 0;
            """, window);

        return new DashboardStats(
            perDay,
            statuses.OrderBy(s => s.Key, StringComparer.Ordinal).ToArray(),
            exitCodes,
            operations,
            byCount,
            byDuration,
            tables,
            waits.OrderByDescending(w => w.Value).ThenBy(w => w.Key, StringComparer.Ordinal).Take(TopLimit)
                .Select(w => new WaitStat(w.Key, w.Value)).ToArray(),
            targets,
            tokens,
            (int)spills,
            (int)cold);
    }

    public IReadOnlyList<OperationSummary> OperationsUpdatedSince(SqliteConnection connection, string cursor, int limit)
    {
        var liveness = new ProcessLiveness(processes);
        return Query(connection, $"""
            SELECT {OperationColumns} FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE o.updated_at >= $cursor ORDER BY o.updated_at, o.id LIMIT $limit;
            """, [("$cursor", cursor), ("$limit", limit)], reader => ReadOperation(reader, liveness));
    }

    public IReadOnlyList<SessionSummary> SessionsSeenSince(SqliteConnection connection, string cursor, int limit) =>
        WithRunning(connection, Query(connection, $"""
            SELECT {SessionColumns} FROM sessions s WHERE s.last_seen >= $cursor ORDER BY s.last_seen, s.id LIMIT $limit;
            """, [("$cursor", cursor), ("$limit", limit)], ReadSessionRow).ToArray());

    public IReadOnlyList<OperationSummary> RunningOperations(SqliteConnection connection)
    {
        var liveness = new ProcessLiveness(processes);
        return Query(connection, $"""
            SELECT {OperationColumns} FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE o.status = 'running' ORDER BY o.id;
            """, [], reader => ReadOperation(reader, liveness));
    }

    internal static string? MaxTimestamp(SqliteConnection connection) =>
        Scalar<string?>(connection, "SELECT MAX(t) FROM (SELECT MAX(updated_at) AS t FROM operations UNION ALL SELECT MAX(last_seen) FROM sessions);", []);

    private IReadOnlyList<VariantDetail> Variants(SqliteConnection connection, long operationId)
    {
        var rows = new List<VariantDetail>();
        foreach (var metric in Query(connection, """
            SELECT id, ordinal, variant, parameter_set, matrix_cell, runs,
                elapsed_ms_min, elapsed_ms_median, elapsed_ms_max, cpu_ms_min, cpu_ms_median, cpu_ms_max,
                logical_reads_min, logical_reads_median, logical_reads_max,
                grant_requested_kb, grant_granted_kb, grant_max_used_kb, dop, compile_time_ms, compile_cpu_ms,
                spill_count, has_warnings, has_implicit_conversion, missing_index_count, waits_json,
                pg_shared_hit, pg_shared_read, pg_shared_dirtied, pg_shared_written, pg_temp_read, pg_temp_written
            FROM operation_metrics WHERE operation_id = $id ORDER BY ordinal;
            """, [("$id", operationId)], r => new
            {
                Id = r.GetInt64(0),
                Detail = new VariantDetail(
                    r.GetInt32(1), r.GetString(2), NullableString(r, 3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetInt32(5),
                    SpreadAt(r, 6), SpreadAt(r, 9), SpreadAt(r, 12),
                    NullableLong(r, 15), NullableLong(r, 16), NullableLong(r, 17), r.IsDBNull(18) ? null : r.GetInt32(18),
                    NullableLong(r, 19), NullableLong(r, 20),
                    r.GetInt32(21), r.GetInt64(22) != 0, r.GetInt64(23) != 0, r.GetInt32(24),
                    Json(NullableString(r, 25)),
                    r.IsDBNull(26) ? null : new PostgresBuffers(NullableLong(r, 26), NullableLong(r, 27), NullableLong(r, 28),
                        NullableLong(r, 29), NullableLong(r, 30), NullableLong(r, 31)),
                    [], []),
            }))
        {
            var tableIo = Query(connection, """
                SELECT table_name, logical_reads, scan_count, physical_reads, page_server_reads, read_ahead_reads,
                    lob_logical_reads, lob_physical_reads, lob_read_ahead_reads, cold_runs
                FROM operation_table_io WHERE metric_id = $id ORDER BY logical_reads DESC, table_name;
                """, [("$id", metric.Id)], r => new TableIoRow(
                    r.GetString(0), r.GetInt64(1), NullableLong(r, 2), NullableLong(r, 3), NullableLong(r, 4), NullableLong(r, 5),
                    NullableLong(r, 6), NullableLong(r, 7), NullableLong(r, 8), r.GetInt32(9)));
            var plans = Query(connection, """
                SELECT p.repetition, p.ordinal, p.plan_hash, EXISTS (SELECT 1 FROM plans s WHERE s.hash = p.plan_hash)
                FROM operation_plans p WHERE p.metric_id = $id ORDER BY p.repetition, p.ordinal;
                """, [("$id", metric.Id)], r => new PlanLinkRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetInt64(3) != 0));
            rows.Add(metric.Detail with { TableIo = tableIo, Plans = plans });
        }

        return rows;
    }

    private SessionSummary[] WithRunning(SqliteConnection connection, SessionSummary[] sessions)
    {
        if (sessions.Length == 0)
            return sessions;
        var liveness = new ProcessLiveness(processes);
        var ids = string.Join(",", sessions.Select(s => s.Id.ToString(CultureInfo.InvariantCulture)));
        var running = Query(connection, $"""
            SELECT session_id, host_pid, host_started_at FROM operations WHERE status = 'running' AND session_id IN ({ids});
            """, [], r => (Session: r.GetInt64(0), Status: liveness.Status("running", r.GetInt64(1), NullableString(r, 2))));
        return sessions.Select(s => s with
        {
            Running = running.Count(r => r.Session == s.Id && r.Status == "running"),
            Abandoned = running.Count(r => r.Session == s.Id && r.Status == "abandoned"),
        }).ToArray();
    }

    private static SessionSummary ReadSessionRow(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), NullableString(r, 5),
        NullableString(r, 6), NullableString(r, 7), NullableString(r, 8), r.GetString(9), r.GetString(10),
        r.GetInt32(11), r.GetInt32(12), r.GetInt32(13), 0, 0);

    private static OperationSummary ReadOperation(SqliteDataReader r, ProcessLiveness liveness) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
        liveness.Status(r.GetString(4), r.GetInt64(19), NullableString(r, 20)),
        r.IsDBNull(5) ? null : r.GetInt32(5), NullableString(r, 6), r.GetString(7), r.GetString(8), NullableString(r, 9),
        NullableLong(r, 10), NullableString(r, 11), NullableString(r, 12), NullableString(r, 13), NullableString(r, 14),
        r.GetInt64(15) != 0, NullableString(r, 16), NullableLong(r, 17), NullableLong(r, 21),
        r.GetInt64(22) != 0, r.GetInt64(23) != 0, r.GetInt64(24) != 0, Json(NullableString(r, 18)));

    private static SqlHashStat ReadSqlStat(SqliteDataReader r) =>
        new(r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3));

    private static Spread? SpreadAt(SqliteDataReader r, int index) =>
        r.IsDBNull(index) ? null : new Spread(r.GetInt64(index), r.GetInt64(index + 1), r.GetInt64(index + 2));

    private static JsonElement? Json(string? text)
    {
        if (text is null)
            return null;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static string? NullableString(SqliteDataReader r, int index) => r.IsDBNull(index) ? null : r.GetString(index);

    private static long? NullableLong(SqliteDataReader r, int index) => r.IsDBNull(index) ? null : r.GetInt64(index);

    private static int Clamp(int limit) => Math.Clamp(limit, 1, MaximumLimit);

    private static string? Iso(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static List<T> Query<T>(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters, Func<SqliteDataReader, T> read)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
            rows.Add(read(reader));
        return rows;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? default! : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }
}
```


`OperationColumns` column indexes used by `ReadOperation`:

| index | column |
|---|---|
| 0–18 | `o.id` … `o.progress_json` in the listed order |
| 19, 20 | `host_pid`, `host_started_at` |
| 21 | `reads_median` |
| 22 | `has_spill` |
| 23 | `cold_cache` |
| 24 | `over_granted` |

Keep the two in sync if you reorder anything.

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~JournalReaderTests`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SqlHarness.Dashboard tests/SqlHarness.Tests/Dashboard
git commit -m "Read sessions, operations, metrics, plans and stats from the journal"
```

---

### Task 4: Kestrel server, security, and API endpoints

**Files:**
- Create: `src/SqlHarness.Dashboard/DashboardSecurity.cs`, `src/SqlHarness.Dashboard/DashboardServer.cs`
- Test: `tests/SqlHarness.Tests/Dashboard/DashboardServerTests.cs`

**Interfaces:**
- Consumes: `JournalReader` and the DTOs (Task 3), `LiveFeed.StreamAsync` (Task 5). In this task, map `/api/live` to a stub returning 501. Task 5 replaces it.
- Produces:
  - `public sealed record DashboardServerOptions(string DatabasePath, int PreferredPort, IProcessInfo Processes)` with `TimeSpan LivePollInterval { get; init; } = TimeSpan.FromSeconds(1)` and `string? Token { get; init; }` (tests only)
  - `public sealed class RunningDashboard : IAsyncDisposable` with `int Port`, `string Token`, `Uri BaseUri`, `Uri OpenUri`, `internal IReadOnlyList<(string Route, IReadOnlyList<string> Methods)> Endpoints`
  - `public static class DashboardServer` with `Task<RunningDashboard> StartAsync(DashboardServerOptions options, CancellationToken ct)`, `const int PortAttempts = 20`
  - `internal static class DashboardSecurity` with `const string CookieName = "sqlharness_dashboard"`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class DashboardServerTests
{
    internal static async Task<(RunningDashboard Server, HttpClient Client)> StartAuthenticated(TempHome home)
    {
        var server = await DashboardServer.StartAsync(new DashboardServerOptions(home.DatabasePath, 0, new FakeProcesses()), CancellationToken.None);
        var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
        var client = new HttpClient(handler) { BaseAddress = server.BaseUri };
        var exchange = await client.GetAsync($"/?t={server.Token}");
        Assert.Equal(HttpStatusCode.Redirect, exchange.StatusCode);
        return (server, client);
    }

    [Fact]
    public async Task Token_exchange_sets_a_strict_http_only_cookie_and_redirects_without_the_token()
    {
        using var home = new TempHome();
        await using var server = await DashboardServer.StartAsync(new DashboardServerOptions(home.DatabasePath, 0, new FakeProcesses()), CancellationToken.None);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server.BaseUri };

        var response = await client.GetAsync($"/sessions?t={server.Token}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sessions", response.Headers.Location!.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains(DashboardSecurity.CookieName + "=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Requests_without_cookie_or_with_foreign_host_are_rejected()
    {
        using var home = new TempHome();
        var (server, client) = await StartAuthenticated(home);
        await using var _ = server;
        using var anonymous = new HttpClient { BaseAddress = server.BaseUri };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/?t=wrong")).StatusCode);

        using var rebinding = new HttpRequestMessage(HttpMethod.Get, "/api/sessions");
        rebinding.Headers.Host = $"attacker.example:{server.Port}";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(rebinding)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sessions")).StatusCode);
        using var viaLocalhost = new HttpRequestMessage(HttpMethod.Get, "/api/sessions");
        viaLocalhost.Headers.Host = $"localhost:{server.Port}";
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(viaLocalhost)).StatusCode);
    }

    [Fact]
    public async Task Only_get_endpoints_exist()
    {
        using var home = new TempHome();
        var (server, client) = await StartAuthenticated(home);
        await using var _ = server;

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/api/sessions", null)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.DeleteAsync("/api/operations/1")).StatusCode);
        Assert.All(server.Endpoints, endpoint => Assert.Equal(["GET"], endpoint.Methods));
        Assert.Contains(server.Endpoints, endpoint => endpoint.Route == "/api/live");
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        using var home = new TempHome();
        var (server, client) = await StartAuthenticated(home);
        await using var _ = server;

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Api_serves_journal_data_and_validates_input()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath, storeSensitive: true);
        var handle = seed.Operation(JournalSeed.Session("cli:a"), operation: "measure", benchmark: JournalSeed.Benchmark());
        var (server, client) = await StartAuthenticated(home);
        await using var _ = server;

        using var sessions = JsonDocument.Parse(await client.GetStringAsync("/api/sessions?limit=10"));
        Assert.Equal("claude", sessions.RootElement.GetProperty("items")[0].GetProperty("agentKind").GetString());

        using var detail = JsonDocument.Parse(await client.GetStringAsync($"/api/operations/{handle.OperationId}"));
        Assert.Equal("measure", detail.RootElement.GetProperty("operation").GetProperty("operation").GetString());

        var raw = await client.GetAsync($"/api/plans/{new string('A', 64)}");
        Assert.Equal("application/xml", raw.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", raw.Content.Headers.ContentDisposition!.DispositionType);

        using var distilled = JsonDocument.Parse(await client.GetStringAsync($"/api/plans/{new string('A', 64)}?view=distilled"));
        Assert.True(distilled.RootElement.TryGetProperty("statements", out _));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/operations/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plans/{new string('B', 64)}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/plans/not-a-hash")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/stats?from=yesterday")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/operations?limit=abc")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/stats?from=2026-10-01T00:00:00Z")).StatusCode);
    }

    [Fact]
    public async Task Unknown_non_api_paths_serve_the_spa_entry_and_unknown_api_paths_are_404()
    {
        using var home = new TempHome();
        var (server, client) = await StartAuthenticated(home);
        await using var _ = server;

        Assert.Equal("text/html", (await client.GetAsync("/sessions/42")).Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/nope")).StatusCode);
    }

    [Fact]
    public async Task Busy_preferred_port_falls_back_to_the_next_free_port()
    {
        using var home = new TempHome();
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var busy = ((IPEndPoint)blocker.LocalEndpoint).Port;

        await using var server = await DashboardServer.StartAsync(new DashboardServerOptions(home.DatabasePath, busy, new FakeProcesses()), CancellationToken.None);

        Assert.NotEqual(busy, server.Port);
        Assert.InRange(server.Port, busy + 1, busy + DashboardServer.PortAttempts);
    }

    [Fact]
    public async Task Environment_urls_do_not_change_the_loopback_binding()
    {
        using var home = new TempHome();
        var saved = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:5999");
        try
        {
            await using var server = await DashboardServer.StartAsync(new DashboardServerOptions(home.DatabasePath, 0, new FakeProcesses()), CancellationToken.None);

            Assert.Equal("127.0.0.1", server.BaseUri.Host);
            Assert.NotEqual(5999, server.Port);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", saved);
        }
    }
}
```

The `Busy_preferred_port...` test can be flaky if `busy + 1` is also taken. The assertion allows any port within the attempt window, which keeps it stable on a normal machine.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~DashboardServerTests`
Expected: build FAILS with `The type or namespace name 'DashboardServer' could not be found`.

- [ ] **Step 3: Implement the security middleware**

`src/SqlHarness.Dashboard/DashboardSecurity.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;

namespace SqlHarness.Dashboard;

/// <summary>
/// Loopback dashboard protection: exact Host allowlist (DNS rebinding), GET-only,
/// one-time ?t= token exchange into an HttpOnly SameSite=Strict cookie, and
/// cookie authentication on every other request. Tokens are compared in constant time.
/// </summary>
internal static class DashboardSecurity
{
    internal const string CookieName = "sqlharness_dashboard";
    private const string TokenQuery = "t";

    internal static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static async Task InvokeAsync(HttpContext context, Func<Task> next, string token, Func<int> port)
    {
        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
        headers.ContentSecurityPolicy =
            "default-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'";

        var host = context.Request.Host.Value;
        var allowedPort = port();
        if (allowedPort == 0
            || !(string.Equals(host, $"127.0.0.1:{allowedPort}", StringComparison.Ordinal)
                 || string.Equals(host, $"localhost:{allowedPort}", StringComparison.OrdinalIgnoreCase)))
        {
            await Reject(context, StatusCodes.Status400BadRequest, "Invalid host.");
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.Headers.Allow = "GET, HEAD";
            await Reject(context, StatusCodes.Status405MethodNotAllowed, "Method not allowed.");
            return;
        }

        if (context.Request.Query.TryGetValue(TokenQuery, out var supplied))
        {
            if (!Matches(supplied.ToString(), token))
            {
                await Reject(context, StatusCodes.Status401Unauthorized, "Open the dashboard with: sqlharness dashboard");
                return;
            }

            context.Response.Cookies.Append(CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = false,
                Path = "/",
                IsEssential = true,
            });
            var query = new QueryBuilder(context.Request.Query
                .Where(pair => pair.Key != TokenQuery)
                .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string>(pair.Key, value ?? string.Empty))));
            context.Response.Redirect(context.Request.PathBase + context.Request.Path + query.ToQueryString());
            return;
        }

        if (!context.Request.Cookies.TryGetValue(CookieName, out var cookie) || !Matches(cookie, token))
        {
            await Reject(context, StatusCodes.Status401Unauthorized, "Open the dashboard with: sqlharness dashboard");
            return;
        }

        await next();
    }

    private static bool Matches(string? supplied, string token) =>
        supplied is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(token));

    private static Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message);
    }
}
```

`FixedTimeEquals` returns false immediately on a length mismatch. That leaks only the length of a 43-character public-format token, which is acceptable.

- [ ] **Step 4: Implement the server**

`src/SqlHarness.Dashboard/DashboardServer.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

public sealed record DashboardServerOptions(string DatabasePath, int PreferredPort, IProcessInfo Processes)
{
    public TimeSpan LivePollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Fixed token for tests; production generates a fresh random token per start.</summary>
    internal string? Token { get; init; }
}

public sealed class RunningDashboard : IAsyncDisposable
{
    private readonly WebApplication _app;

    internal RunningDashboard(WebApplication app, int port, string token)
    {
        _app = app;
        Port = port;
        Token = token;
    }

    public int Port { get; }

    public string Token { get; }

    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    public Uri OpenUri => new($"http://127.0.0.1:{Port}/?t={Token}");

    internal IReadOnlyList<(string Route, IReadOnlyList<string> Methods)> Endpoints =>
        _app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => (
                "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/'),
                (IReadOnlyList<string>)(endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).ToArray()))
            .ToArray();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public static partial class DashboardServer
{
    public const int PortAttempts = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<RunningDashboard> StartAsync(DashboardServerOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var token = options.Token ?? DashboardSecurity.NewToken();
        var candidates = options.PreferredPort == 0
            ? [0]
            : Enumerable.Range(options.PreferredPort, PortAttempts).Where(port => port <= 65535).ToArray();
        Exception? last = null;
        foreach (var candidate in candidates)
        {
            var boundPort = 0;
            var app = Build(options, token, candidate, () => boundPort);
            try
            {
                await app.StartAsync(ct);
            }
            catch (Exception exception) when (IsAddressInUse(exception))
            {
                last = exception;
                await app.DisposeAsync();
                continue;
            }

            boundPort = BoundPort(app);
            return new RunningDashboard(app, boundPort, token);
        }

        throw new IOException("No free loopback port for the dashboard.", last);
    }

    private static WebApplication Build(DashboardServerOptions options, string token, int port, Func<int> boundPort)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(DashboardServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        // Explicit Listen overrides ASPNETCORE_URLS/--urls; the dashboard is loopback-only by construction.
        builder.WebHost.UseUrls();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Loopback, port);
        });
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

        var app = builder.Build();
        var reader = new JournalReader(options.DatabasePath, options.Processes);
        app.Use((context, next) => DashboardSecurity.InvokeAsync(context, () => next(context), token, boundPort));

        var api = app.MapGroup("/api");
        api.MapGet("/sessions", (string? agent, string? transport, string? from, string? to, long? cursor, int? limit) =>
            TryWindow(from, to, out var window)
                ? Results.Json(reader.Sessions(new SessionQuery(agent, transport, window.From, window.To, cursor, limit ?? JournalReader.DefaultLimit)), Json)
                : BadRequest("from/to must be ISO-8601 timestamps."));
        api.MapGet("/sessions/{id:long}", (long id) =>
            reader.Session(id) is { } detail ? Results.Json(detail, Json) : Results.NotFound());
        api.MapGet("/operations", (long? session, string? status, string? operation, string? from, string? to, long? cursor, int? limit) =>
            TryWindow(from, to, out var window)
                ? Results.Json(reader.Operations(new OperationQuery(session, status, operation, window.From, window.To, cursor, limit ?? JournalReader.DefaultLimit)), Json)
                : BadRequest("from/to must be ISO-8601 timestamps."));
        api.MapGet("/operations/{id:long}", (long id) =>
            reader.Operation(id) is { } detail ? Results.Json(detail, Json) : Results.NotFound());
        api.MapGet("/plans/{hash}", (string hash, string? view) => Plan(reader, hash, view));
        api.MapGet("/stats", (string? from, string? to) =>
            TryWindow(from, to, out var window)
                ? Results.Json(reader.Stats(new StatsQuery(window.From, window.To)), Json)
                : BadRequest("from/to must be ISO-8601 timestamps."));
        api.MapGet("/live", (HttpContext context) => LiveFeed.StreamAsync(context, reader, options.LivePollInterval, context.RequestAborted));
        api.MapGet("/{**rest}", () => Results.NotFound());

        app.MapGet("/", () => Index());
        app.MapGet("/{**path}", () => Index());
        return app;
    }

    private static IResult Plan(JournalReader reader, string hash, string? view)
    {
        if (!PlanHash().IsMatch(hash))
            return BadRequest("Plan hash must be 64 hexadecimal characters.");
        if (reader.Plan(hash.ToUpperInvariant()) is not { } plan)
            return Results.NotFound();
        if (string.Equals(view, "distilled", StringComparison.Ordinal))
        {
            try
            {
                var distilled = plan.Format == "explain-json"
                    ? Core.Postgres.PostgresPlanDistiller.Distill(plan.Document)
                    : PlanDistiller.Distill(plan.Document);
                return Results.Json(distilled, Json);
            }
            catch (Exception exception) when (exception is SqlHarnessSafetyException or System.Xml.XmlException or JsonException or InvalidOperationException)
            {
                return Results.UnprocessableEntity();
            }
        }

        var (contentType, extension) = plan.Format == "explain-json"
            ? ("application/json", ".explain.json")
            : ("application/xml", ".sqlplan");
        return Results.File(System.Text.Encoding.UTF8.GetBytes(plan.Document), contentType, plan.Hash.ToLowerInvariant() + extension);
    }

    private static IResult Index()
    {
        using var stream = typeof(DashboardServer).Assembly.GetManifestResourceStream("SqlHarness.Dashboard.wwwroot.index.html")
            ?? throw new InvalidOperationException("Dashboard index is missing.");
        using var text = new StreamReader(stream);
        return Results.Content(text.ReadToEnd(), "text/html; charset=utf-8");
    }

    private static IResult BadRequest(string message) => Results.Json(new { error = message }, Json, statusCode: StatusCodes.Status400BadRequest);

    private static bool TryWindow(string? from, string? to, out (DateTimeOffset? From, DateTimeOffset? To) window)
    {
        window = (null, null);
        if (!TryTimestamp(from, out var start) || !TryTimestamp(to, out var end))
            return false;
        window = (start, end);
        return true;
    }

    private static bool TryTimestamp(string? value, out DateTimeOffset? timestamp)
    {
        timestamp = null;
        if (string.IsNullOrEmpty(value))
            return true;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return false;
        timestamp = parsed;
        return true;
    }

    private static int BoundPort(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Uri(address).Port;
    }

    private static bool IsAddressInUse(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AddressInUseException)
                return true;
            if (current is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse or System.Net.Sockets.SocketError.AccessDenied })
                return true;
        }

        return false;
    }

    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlanHash();
}
```

Notes for the implementer:
- `LiveFeed` does not exist until Task 5. For this task, add a minimal `src/SqlHarness.Dashboard/LiveFeed.cs` containing `internal static class LiveFeed { internal static Task StreamAsync(HttpContext context, JournalReader reader, TimeSpan interval, CancellationToken ct) { context.Response.StatusCode = StatusCodes.Status501NotImplemented; return Task.CompletedTask; } }`. Task 5 replaces its body.
- Minimal API query binding returns 400 automatically for `limit=abc`, which the test expects.
- The catch-all `/{**path}` maps GET only, so `Only_get_endpoints_exist` holds. The method gate in the middleware answers 405 before routing for any other verb.
- `AccessDenied` is included because Windows reports a port reserved by Hyper-V/WinNAT as access denied rather than in use.

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~DashboardServerTests`
Expected: all PASS. If `Endpoints` reports a route with no methods (`[]`), it is an endpoint mapped without `MapGet`. Find and convert it; do not relax the test.

- [ ] **Step 6: Commit**

```bash
git add src/SqlHarness.Dashboard tests/SqlHarness.Tests/Dashboard/DashboardServerTests.cs
git commit -m "Serve the read-only dashboard API on loopback with token cookie auth"
```

---

### Task 5: Live feed (SSE)

**Files:**
- Modify: `src/SqlHarness.Dashboard/LiveFeed.cs`
- Test: `tests/SqlHarness.Tests/Dashboard/LiveFeedTests.cs`

**Interfaces:**
- Consumes: `JournalReader.OpenReadOnly`, `OperationsUpdatedSince`, `SessionsSeenSince`, `RunningOperations`, `MaxTimestamp` (Task 3).
- Produces: `GET /api/live` as `text/event-stream` with events `session` and `operation` (data = the summary DTO as camelCase JSON), a `: connected` comment on open, and a `: ping` comment every 15 s. `internal const int BatchLimit = 500`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;

using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class LiveFeedTests
{
    private static async Task<(string Event, JsonElement Data)> NextEventAsync(StreamReader stream, string name, CancellationToken ct)
    {
        string? currentEvent = null;
        while (await stream.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
                currentEvent = line["event: ".Length..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && currentEvent == name)
                return (currentEvent, JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone());
        }

        throw new EndOfStreamException();
    }

    private static async Task<(RunningDashboard Server, HttpClient Client, StreamReader Stream)> Connect(TempHome home, FakeProcesses processes)
    {
        var server = await DashboardServer.StartAsync(
            new DashboardServerOptions(home.DatabasePath, 0, processes) { LivePollInterval = TimeSpan.FromMilliseconds(50) },
            CancellationToken.None);
        var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new System.Net.CookieContainer() };
        var client = new HttpClient(handler) { BaseAddress = server.BaseUri, Timeout = Timeout.InfiniteTimeSpan };
        await client.GetAsync($"/?t={server.Token}");
        var response = await client.GetAsync("/api/live", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var stream = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal(": connected", await stream.ReadLineAsync());
        return (server, client, stream);
    }

    [Fact]
    public async Task New_and_completed_operations_are_pushed()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:old"));
        var (server, client, stream) = await Connect(home, new FakeProcesses().Alive(5151, JournalSeed.HostStarted));
        await using var _ = server;
        using var __ = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var handle = seed.Operation(JournalSeed.Session("cli:new"), operation: "ping");

        var (_, data) = await NextEventAsync(stream, "operation", timeout.Token);
        Assert.Equal(handle.OperationId, data.GetProperty("id").GetInt64());
        Assert.Equal("ping", data.GetProperty("operation").GetString());
    }

    [Fact]
    public async Task Live_feed_reports_a_running_operation_turning_abandoned()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var processes = new FakeProcesses().Alive(5151, JournalSeed.HostStarted);
        seed.Operation(JournalSeed.Session("cli:run"), complete: false);
        var (server, client, stream) = await Connect(home, processes);
        await using var _ = server;
        using var __ = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        processes.Clear();

        var (_, data) = await NextEventAsync(stream, "operation", timeout.Token);
        Assert.Equal("abandoned", data.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Feed_waits_for_a_database_that_does_not_exist_yet()
    {
        using var home = new TempHome();
        var (server, client, stream) = await Connect(home, new FakeProcesses());
        await using var _ = server;
        using var __ = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var handle = new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("cli:late"));

        var (_, data) = await NextEventAsync(stream, "operation", timeout.Token);
        Assert.Equal(handle.OperationId, data.GetProperty("id").GetInt64());
    }
}
```


- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~LiveFeedTests`
Expected: FAIL. The stub answers 501, so the content-type assertion fails.

- [ ] **Step 3: Implement**

Replace `src/SqlHarness.Dashboard/LiveFeed.cs`:

```csharp
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace SqlHarness.Dashboard;

/// <summary>
/// Server-sent events over the journal. One read-only connection stays open for
/// the stream because PRAGMA data_version only changes for commits made by other
/// connections; rows are queried only when it changes. Running rows are re-checked
/// each tick so a dead process turns into an <c>abandoned</c> event without any write.
/// </summary>
internal static class LiveFeed
{
    internal const int BatchLimit = 500;
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task StreamAsync(HttpContext context, JournalReader reader, TimeSpan interval, CancellationToken ct)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await Comment(context, "connected", ct);

        SqliteConnection? connection = null;
        try
        {
            string? operationCursor = null;
            string? sessionCursor = null;
            var sentAtOperationCursor = new HashSet<(long, string)>();
            var sentAtSessionCursor = new HashSet<(long, string)>();
            var runningStatus = new Dictionary<long, string>();
            long? lastVersion = null;
            var sawMissingDatabase = false;
            var lastBeat = DateTimeOffset.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                if (connection is null)
                {
                    connection = reader.OpenReadOnly();
                    if (connection is null)
                    {
                        sawMissingDatabase = true;
                    }
                    else
                    {
                        // A database created after connect streams everything; an existing one streams only news.
                        var existing = sawMissingDatabase ? null : JournalReader.MaxTimestamp(connection);
                        operationCursor = existing ?? string.Empty;
                        sessionCursor = existing ?? string.Empty;
                        foreach (var running in reader.RunningOperations(connection))
                            runningStatus[running.Id] = running.Status;
                    }
                }

                if (connection is not null)
                {
                    var version = DataVersion(connection);
                    if (version != lastVersion)
                    {
                        lastVersion = version;
                        foreach (var session in reader.SessionsSeenSince(connection, sessionCursor!, BatchLimit))
                        {
                            if (!Advance(ref sessionCursor, sentAtSessionCursor, session.Id, session.LastSeen))
                                continue;
                            await Send(context, "session", session, ct);
                        }

                        foreach (var operation in reader.OperationsUpdatedSince(connection, operationCursor!, BatchLimit))
                        {
                            if (!Advance(ref operationCursor, sentAtOperationCursor, operation.Id, operation.UpdatedAt))
                                continue;
                            if (operation.Status is "running" or "abandoned")
                                runningStatus[operation.Id] = operation.Status;
                            else
                                runningStatus.Remove(operation.Id);
                            await Send(context, "operation", operation, ct);
                        }
                    }

                    // Liveness changes produce no journal write, so check running rows every tick.
                    foreach (var running in reader.RunningOperations(connection))
                    {
                        if (runningStatus.TryGetValue(running.Id, out var known) && known == running.Status)
                            continue;
                        runningStatus[running.Id] = running.Status;
                        await Send(context, "operation", running, ct);
                    }
                }

                if (DateTimeOffset.UtcNow - lastBeat >= Heartbeat)
                {
                    lastBeat = DateTimeOffset.UtcNow;
                    await Comment(context, "ping", ct);
                }

                await Task.Delay(interval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected or the server is stopping.
        }
        finally
        {
            connection?.Dispose();
        }
    }

    /// <summary>
    /// Keyset cursor on (timestamp, id): rows with the same millisecond timestamp as the
    /// cursor are de-duplicated by id, so equal timestamps are neither lost nor repeated.
    /// </summary>
    private static bool Advance(ref string? cursor, HashSet<(long, string)> sentAtCursor, long id, string timestamp)
    {
        var comparison = string.CompareOrdinal(timestamp, cursor);
        if (comparison < 0)
            return false;
        if (comparison > 0)
        {
            cursor = timestamp;
            sentAtCursor.Clear();
        }

        return sentAtCursor.Add((id, timestamp));
    }

    private static long DataVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version;";
        return (long)command.ExecuteScalar()!;
    }

    private static async Task Send<T>(HttpContext context, string name, T data, CancellationToken ct)
    {
        await context.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, Json)}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }

    private static async Task Comment(HttpContext context, string text, CancellationToken ct)
    {
        await context.Response.WriteAsync($": {text}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }
}
```

On connect to an existing database, `Advance` sees rows at exactly the max timestamp once, as news. That is harmless: the client merges by id.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~LiveFeedTests|FullyQualifiedName~DashboardServerTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Dashboard/LiveFeed.cs tests/SqlHarness.Tests/Dashboard
git commit -m "Stream journal changes and abandoned operations over SSE"
```

---

### Task 6: `DashboardHost` and the `dashboard` CLI command

**Files:**
- Create: `src/SqlHarness.Dashboard/BrowserLauncher.cs`, `src/SqlHarness.Dashboard/DashboardHost.cs`, `src/SqlHarness.Cli/Commands/DashboardCommand.cs`
- Modify: `src/SqlHarness.Cli/SqlHarness.Cli.csproj`, `src/SqlHarness.Cli/SqlHarnessCli.cs`, `tests/SqlHarness.Tests/Cli/DoctorJournalConfigTests.cs`
- Test: `tests/SqlHarness.Tests/Dashboard/DashboardHostTests.cs`

**Interfaces:**
- Consumes: everything above, `SqlHarnessConfigLoader`, `ActivityJournal.Open`, `JournalSchema.CurrentVersion`, `SqlHarnessPaths`.
- Produces:
  - `public interface IBrowserLauncher { void Open(Uri uri); }` and `public sealed class SystemBrowserLauncher : IBrowserLauncher`
  - `public sealed record DashboardHostOptions(string Home, string DatabasePath, SqlHarnessConfigLoadResult Config, bool OpenBrowser, bool Quiet, TextWriter Output, TextWriter Error, IProcessInfo Processes, IBrowserLauncher Browser)` with `int? PortOverride { get; init; }`, `TimeSpan LivePollInterval { get; init; } = TimeSpan.FromSeconds(1)`, `Action<RunningDashboard>? Started { get; init; }`
  - `public static class DashboardHost { Task<int> RunAsync(DashboardHostOptions options, CancellationToken ct); }`
  - CLI: `sqlharness dashboard [--no-open] [--background]`

- [ ] **Step 1: Write the failing tests**

```csharp
using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class DashboardHostTests
{
    private sealed class RecordingBrowser : IBrowserLauncher
    {
        public List<Uri> Opened { get; } = [];
        public void Open(Uri uri) => Opened.Add(uri);
    }

    private static DashboardHostOptions Options(TempHome home, StringWriter output, StringWriter error, RecordingBrowser browser,
        bool openBrowser = true, bool quiet = false, SqlHarnessConfigLoadResult? config = null) =>
        new(home.Path, home.DatabasePath, config ?? new SqlHarnessConfigLoadResult(SqlHarnessConfig.Default, SqlHarnessConfigStatus.Missing, null),
            openBrowser, quiet, output, error, new FakeProcesses().Alive(Environment.ProcessId, null), browser)
        {
            PortOverride = 0,
        };

    [Fact]
    public async Task Run_starts_publishes_prints_opens_and_stops_on_cancel()
    {
        using var home = new TempHome();
        var output = new StringWriter();
        var browser = new RecordingBrowser();
        using var cts = new CancellationTokenSource();
        RunningDashboard? started = null;

        var run = DashboardHost.RunAsync(Options(home, output, new StringWriter(), browser) with { Started = s => { started = s; cts.Cancel(); } }, cts.Token);
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, exit);
        Assert.NotNull(started);
        Assert.Contains(started!.OpenUri.ToString(), output.ToString());
        Assert.Equal([started.OpenUri], browser.Opened);
        Assert.True(File.Exists(home.DatabasePath));
        Assert.False(File.Exists(Path.Combine(home.Path, "dashboard.json")));
    }

    [Fact]
    public async Task Background_mode_prints_nothing_and_opens_nothing()
    {
        using var home = new TempHome();
        var output = new StringWriter();
        var browser = new RecordingBrowser();
        using var cts = new CancellationTokenSource();

        var exit = await DashboardHost.RunAsync(
            Options(home, output, new StringWriter(), browser, openBrowser: false, quiet: true) with { Started = _ => cts.Cancel() },
            cts.Token).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Empty(browser.Opened);
    }

    [Fact]
    public async Task Second_invocation_reuses_the_running_dashboard()
    {
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        var endpoint = new DashboardEndpoint(Environment.ProcessId, null, 47811, "existing-token");
        held.Publish(endpoint);
        var output = new StringWriter();
        var browser = new RecordingBrowser();

        var exit = await DashboardHost.RunAsync(Options(home, output, new StringWriter(), browser), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(endpoint.OpenUri.ToString(), output.ToString());
        Assert.Equal([endpoint.OpenUri], browser.Opened);
        Assert.False(File.Exists(home.DatabasePath));
    }

    [Fact]
    public async Task Held_lock_without_a_published_address_exits_with_local_storage()
    {
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        var error = new StringWriter();

        var exit = await DashboardHost.RunAsync(Options(home, new StringWriter(), error, new RecordingBrowser()), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal((int)SqlHarnessExitCode.LocalStorage, exit);
        Assert.Contains("dashboard", error.ToString());
    }

    [Fact]
    public async Task Newer_journal_schema_refuses_to_serve()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var error = new StringWriter();

        var exit = await DashboardHost.RunAsync(Options(home, new StringWriter(), error, new RecordingBrowser()), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal((int)SqlHarnessExitCode.LocalStorage, exit);
        Assert.Contains("newer", error.ToString());
        Assert.False(File.Exists(Path.Combine(home.Path, "dashboard.json")));
    }

    [Fact]
    public async Task Disabled_journal_still_serves_without_creating_the_database()
    {
        using var home = new TempHome();
        using var cts = new CancellationTokenSource();
        var config = new SqlHarnessConfigLoadResult(
            SqlHarnessConfig.Default with { Journal = new JournalConfig { Enabled = false } }, SqlHarnessConfigStatus.Valid, null);

        var exit = await DashboardHost.RunAsync(
            Options(home, new StringWriter(), new StringWriter(), new RecordingBrowser(), config: config) with { Started = _ => cts.Cancel() },
            cts.Token).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, exit);
        Assert.False(File.Exists(home.DatabasePath));
    }
}
```

`Held_lock_without_a_published_address...` waits for the 5 s `WaitForRunningAsync` timeout. Keep that timeout as a constant `DashboardHost.ExistingInstanceWait = TimeSpan.FromSeconds(5)` so the test stays bounded.

In `tests/SqlHarness.Tests/Cli/DoctorJournalConfigTests.cs`, rename `Only_the_mcp_branch_prints_its_own_config_warning` to `Branches_that_load_config_print_their_own_warning` and add the case:

```csharp
    [InlineData(new[] { "dashboard" }, true)]
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~DashboardHostTests|FullyQualifiedName~DoctorJournalConfigTests"`
Expected: build FAILS with `The type or namespace name 'DashboardHost' could not be found`.

- [ ] **Step 3: Implement the browser launcher**

`src/SqlHarness.Dashboard/BrowserLauncher.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;

namespace SqlHarness.Dashboard;

public interface IBrowserLauncher
{
    void Open(Uri uri);
}

/// <summary>Opens the default browser; failures are ignored because the URL is also printed.</summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public void Open(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        try
        {
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
            {
                start = new ProcessStartInfo(uri.ToString()) { UseShellExecute = true };
            }
            else
            {
                start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
                start.ArgumentList.Add(uri.ToString());
            }

            using var _ = Process.Start(start);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException or PlatformNotSupportedException)
        {
        }
    }
}
```

- [ ] **Step 4: Implement the host**

`src/SqlHarness.Dashboard/DashboardHost.cs`:

```csharp
using SqlHarness.Core;

namespace SqlHarness.Dashboard;

public sealed record DashboardHostOptions(
    string Home,
    string DatabasePath,
    SqlHarnessConfigLoadResult Config,
    bool OpenBrowser,
    bool Quiet,
    TextWriter Output,
    TextWriter Error,
    IProcessInfo Processes,
    IBrowserLauncher Browser)
{
    /// <summary>Overrides config dashboard.port; 0 binds an ephemeral port (tests).</summary>
    public int? PortOverride { get; init; }

    public TimeSpan LivePollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Test hook invoked once the server is listening and published.</summary>
    public Action<RunningDashboard>? Started { get; init; }
}

/// <summary>
/// One <c>sqlharness dashboard</c> invocation: reuse a running dashboard of this
/// home, or acquire the lock, migrate the journal, serve on loopback, publish the
/// endpoint, and run until cancelled.
/// </summary>
public static class DashboardHost
{
    public static readonly TimeSpan ExistingInstanceWait = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(DashboardHostOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Config.Warning is not null)
            options.Error.WriteLine(options.Config.Warning);

        using var held = DashboardLock.TryAcquire(options.Home);
        if (held is null)
        {
            var existing = await DashboardLock.WaitForRunningAsync(options.Home, options.Processes, ExistingInstanceWait, ct);
            if (existing is null)
            {
                options.Error.WriteLine("sqlharness: another dashboard holds the lock but has not published its address.");
                return (int)SqlHarnessExitCode.LocalStorage;
            }

            Announce(options, existing.OpenUri);
            return (int)SqlHarnessExitCode.Success;
        }

        if (options.Config.Config.Journal.Enabled)
            _ = ActivityJournal.Open(options.DatabasePath, options.Config.Config.Journal, TextWriter.Null, TimeProvider.System);
        var reader = new JournalReader(options.DatabasePath, options.Processes);
        if (reader.SchemaVersion() is { } version && version > JournalSchema.CurrentVersion)
        {
            options.Error.WriteLine("sqlharness: the activity journal schema is newer than this sqlharness; upgrade sqlharness to open the dashboard.");
            return (int)SqlHarnessExitCode.LocalStorage;
        }

        RunningDashboard running;
        try
        {
            running = await DashboardServer.StartAsync(
                new DashboardServerOptions(options.DatabasePath, options.PortOverride ?? options.Config.Config.Dashboard.Port, options.Processes)
                {
                    LivePollInterval = options.LivePollInterval,
                },
                ct);
        }
        catch (IOException)
        {
            options.Error.WriteLine("sqlharness: the dashboard could not bind a loopback port.");
            return (int)SqlHarnessExitCode.LocalStorage;
        }

        await using (running)
        {
            var self = options.Processes.Get(Environment.ProcessId);
            held.Publish(new DashboardEndpoint(Environment.ProcessId, self?.StartedAt, running.Port, running.Token));
            Announce(options, running.OpenUri);
            options.Started?.Invoke(running);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return (int)SqlHarnessExitCode.Success;
    }

    private static void Announce(DashboardHostOptions options, Uri uri)
    {
        if (!options.Quiet)
        {
            options.Output.WriteLine($"SQLHarness dashboard: {uri}");
            options.Output.Flush();
        }

        if (options.OpenBrowser)
            options.Browser.Open(uri);
    }
}
```


- [ ] **Step 5: Implement the CLI command**

`src/SqlHarness.Cli/SqlHarness.Cli.csproj`, add:

```xml
  <ItemGroup>
    <ProjectReference Include="..\SqlHarness.Dashboard\SqlHarness.Dashboard.csproj" />
  </ItemGroup>
```

`src/SqlHarness.Cli/Commands/DashboardCommand.cs`:

```csharp
using System.ComponentModel;

using Spectre.Console.Cli;

using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Cli.Commands;

[Description("Serve the local activity dashboard on 127.0.0.1.")]
public sealed class DashboardCommand : AsyncCommand<DashboardCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--no-open")]
        [Description("Print the URL without opening a browser.")]
        public bool NoOpen { get; set; }

        [CommandOption("--background")]
        [Description("Serve quietly without opening a browser (used by autostart).")]
        public bool Background { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct) =>
        DashboardHost.RunAsync(
            new DashboardHostOptions(
                SqlHarnessPaths.Home,
                SqlHarnessPaths.ActivityDatabase,
                SqlHarnessConfigLoader.Load(),
                OpenBrowser: !settings.NoOpen && !settings.Background,
                Quiet: settings.Background,
                Console.Out,
                Console.Error,
                ProcessInfo.Current,
                new SystemBrowserLauncher()),
            ct);
}
```

`src/SqlHarness.Cli/SqlHarnessCli.cs`:
- Register the command after `doctor`: `c.AddCommand<DashboardCommand>("dashboard").WithDescription("Serve the local activity dashboard on 127.0.0.1.");`
- Extend `PrintsOwnConfigWarning` so both branches that load config themselves are covered:

  ```csharp
        return args.Count > 0 && (string.Equals(args[0], "mcp", StringComparison.Ordinal)
            || string.Equals(args[0], "dashboard", StringComparison.Ordinal));
  ```

  Update its XML doc to mention the dashboard.

`dashboard` is an operator command, not an agent command, so it is deliberately absent from `capabilities`, the same as `mcp`.

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Dashboard|FullyQualifiedName~DoctorJournalConfigTests"`
Expected: all PASS.

Run: `dotnet run --project src/SqlHarness.Cli -- dashboard --help`
Expected: help text listing `--no-open` and `--background`.

- [ ] **Step 7: Commit**

```bash
git add src/SqlHarness.Dashboard src/SqlHarness.Cli tests/SqlHarness.Tests
git commit -m "Add the sqlharness dashboard command"
```

---

### Task 7: Documentation, release check, and gates

**Files:**
- Modify: `README.md`, `AGENTS.md`, `docs/superpowers/specs/2026-10-06-activity-dashboard-design.md`

- [ ] **Step 1: Documentation**

`README.md`: add a `### Dashboard` subsection after `### Activity journal`:

````markdown
### Dashboard

```powershell
sqlharness dashboard            # serve on 127.0.0.1 and open the browser
sqlharness dashboard --no-open  # print the URL only
```

One dashboard runs per SQLHarness home; a second invocation opens the running one. It listens only on `127.0.0.1` (default port `47800`, next free port when taken), and the printed URL carries a one-time token that becomes an HttpOnly cookie. The API under `/api` is read-only: sessions, operations, metrics, stored plans (with `journal.storeSensitive`), statistics, and a live feed. Running operations whose process ended show as `abandoned`. The data it serves is the activity journal and is locally sensitive. Press Ctrl+C to stop it.
````

`AGENTS.md`: under **Safety contract**, after the activity-journal bullet, add:

```markdown
- `sqlharness dashboard` is an operator tool, not an agent command: agents do not start it or call its API. It serves the activity journal read-only on `127.0.0.1` behind a per-start token cookie.
```

Spec: in "Dashboard server", replace the `abandoned` description with: "`abandoned` is computed at read time (API, statistics, live feed) when a running row's `(host_pid, host_started_at)` no longer exists; the dashboard does not write to the journal. On platforms without a process reader (macOS) rows are reported as stored." Also add one line: "`watch` writes `progress_json` after each completed poll (`polls`, `changedPolls`, `elapsedMs`); progress recording stops for that watch after the first failed journal write."

- [ ] **Step 2: Single-file release check**

Run:

```powershell
dotnet publish src/SqlHarness.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -o out/phase3-check
./out/phase3-check/sqlharness.exe dashboard --help
```

Expected: publish succeeds (the ASP.NET shared framework is included by self-contained publish), and the help prints. Note the size of `sqlharness.exe` in the commit message body. Delete `out/phase3-check` afterwards. **STOP** and report if publish fails or the binary cannot start the command. Do not change `release.yml` in this phase.

- [ ] **Step 3: Run both gates**

Run: `pwsh ./scripts/verify.ps1`
Expected: restore, build (`-warnaserror`), test, and format succeed. If `format` fails, run `dotnet format SqlHarness.sln` and include the changes.

Run: `pwsh ./scripts/verify-linux.ps1`
Expected: all four stages succeed in WSL. This proves the advisory lock (`DashboardLockTests`), owner-only `dashboard.json`, and real `/proc` liveness.

- [ ] **Step 4: Commit**

```bash
git add README.md AGENTS.md docs/superpowers/specs/2026-10-06-activity-dashboard-design.md
git commit -m "Document the dashboard and computed abandoned status"
```

---

## Not in this phase

- **Phase 4:** React/shadcn (Base UI) SPA replacing `wwwroot/index.html`, embedding `dist/`, and the `ui` gate stage. The SPA consumes exactly the API above. The CSP already allows `self` scripts and inline styles for Recharts.
- **Phase 5:** `dashboard.autoStart` (detached `sqlharness dashboard --background` from `mcp serve`), idle shutdown (`dashboard.idleShutdownHours`, SSE client count plus `data_version`), and retention.
- Doctor reporting of a running dashboard, and a `--port` flag. The config key covers port selection.
