using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public class WatchTests
{
    private const string Token = "fake-access-token-never-emit";

    [Fact]
    public async Task Watch_emits_first_and_changed_polls_only()
    {
        // until-unchanged 2: baseline + one repeat is not enough; change resets; next repeat exits.
        // Polls: 1, 1, 2, 2, 2 → emit 1 and 3, exit Unchanged.
        var clock = new FakeWatchClock();
        var session = FakeSession.WithScalarPolls(1, 1, 2, 2, 2);
        var outcome = await Module(session, clock).ExecuteAsync(
            Watch(untilUnchanged: 2));

        var report = Assert.IsType<SqlHarnessWatchReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal([1, 3], report.EmittedPolls.Select(p => p.Poll));
        Assert.Equal(WatchExitReason.Unchanged, report.ExitReason);
        Assert.Equal(5, report.PollCount);
        Assert.Equal(2, report.EmittedPolls.Count);
        Assert.Equal(1, Assert.Single(report.EmittedPolls[0].ResultSets[0].Rows[0]));
        Assert.Equal(2, Assert.Single(report.EmittedPolls[1].ResultSets[0].Rows[0]));
    }

    [Fact]
    public async Task Watch_predicate_match_exits_condition_met()
    {
        var clock = new FakeWatchClock();
        var session = FakeSession.WithScalarPolls(1, 1, 2);
        var outcome = await Module(session, clock).ExecuteAsync(
            Watch(until: "Value >= 2"));

        var report = Assert.IsType<SqlHarnessWatchReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(WatchExitReason.ConditionMet, report.ExitReason);
        Assert.Equal([1, 3], report.EmittedPolls.Select(p => p.Poll));
        Assert.Equal(3, report.PollCount);
    }

    [Fact]
    public async Task Watch_unchanged_threshold_of_one_exits_after_first_repeat()
    {
        var clock = new FakeWatchClock();
        var session = FakeSession.WithScalarPolls(5, 5);
        var outcome = await Module(session, clock).ExecuteAsync(
            Watch(untilUnchanged: 1));

        var report = Assert.IsType<SqlHarnessWatchReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(WatchExitReason.Unchanged, report.ExitReason);
        Assert.Equal([1], report.EmittedPolls.Select(p => p.Poll));
        Assert.Equal(2, report.PollCount);
    }

    [Fact]
    public async Task Watch_deadline_exits_max_duration_with_code_seven()
    {
        var clock = new FakeWatchClock();
        // Always-changing values so unchanged never fires; max duration ends the loop.
        var session = FakeSession.WithScalarPolls(1, 2, 3, 4, 5, 6, 7, 8);
        var outcome = await Module(session, clock).ExecuteAsync(
            Watch(
                untilUnchanged: 100,
                interval: TimeSpan.FromSeconds(30),
                maxDuration: TimeSpan.FromSeconds(60)));

        var report = Assert.IsType<SqlHarnessWatchReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.WatchMaxDuration, outcome.ExitCode);
        Assert.Equal(WatchExitReason.MaxDuration, report.ExitReason);
        // T0 poll1, delay→T30 poll2, delay→T60 poll3, then deadline before next delay.
        Assert.Equal(3, report.PollCount);
        Assert.Equal([1, 2, 3], report.EmittedPolls.Select(p => p.Poll));
        Assert.Equal(2, clock.Delays.Count);
    }

    [Fact]
    public async Task Watch_uses_one_connected_session_for_every_poll()
    {
        var clock = new FakeWatchClock();
        var session = FakeSession.WithScalarPolls(1, 1);
        var outcome = await Module(session, clock).ExecuteAsync(
            Watch(untilUnchanged: 1));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(1, session.ConnectCount);
        Assert.Equal(2, session.Commands.Count);
        Assert.All(session.Commands, command => Assert.Equal("SELECT 1 AS Value", command.Sql));
    }

    [Fact]
    public async Task Watch_cancellation_interrupts_delay()
    {
        var clock = new FakeWatchClock { BlockDelay = true };
        var session = FakeSession.WithScalarPolls(1, 2, 3);
        using var cts = new CancellationTokenSource();
        clock.OnDelay = () => cts.Cancel();

        var outcome = await Module(session, clock).ExecuteAsync(
            Watch(untilUnchanged: 100, interval: TimeSpan.FromSeconds(30)),
            cts.Token);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Single(session.Commands);
        Assert.Single(clock.Delays);
    }

    [Fact]
    public async Task Watch_rejects_mutation_sql_before_authentication()
    {
        var azure = new FakeAzureCli(Token);
        var session = FakeSession.WithScalarPolls(1);
        var outcome = await Module(session, new FakeWatchClock(), azure: azure).ExecuteAsync(
            Watch(sql: "DELETE FROM dbo.T", untilUnchanged: 1));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.ConnectCount);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Watch_rejects_unreferenced_parameter_before_authentication()
    {
        var azure = new FakeAzureCli(Token);
        var session = FakeSession.WithScalarPolls(1);
        var outcome = await Module(session, new FakeWatchClock(), azure: azure).ExecuteAsync(
            Watch(untilUnchanged: 1) with { Parameters = ["ClinetId:int=42"] });

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.ConnectCount);
    }

    [Fact]
    public async Task Watch_rejects_invalid_predicate_before_authentication()
    {
        var azure = new FakeAzureCli(Token);
        var session = FakeSession.WithScalarPolls(1);
        var outcome = await Module(session, new FakeWatchClock(), azure: azure).ExecuteAsync(
            Watch(until: "not-a-predicate"));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.ConnectCount);
    }

    [Fact]
    public async Task Watch_rejects_invalid_bounds_before_authentication()
    {
        var azure = new FakeAzureCli(Token);
        var session = FakeSession.WithScalarPolls(1);
        var outcome = await Module(session, new FakeWatchClock(), azure: azure).ExecuteAsync(
            Watch(untilUnchanged: 1, interval: TimeSpan.Zero));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.ConnectCount);
    }

    [Fact]
    public async Task Watch_target_mismatch_maps_to_four_without_user_sql()
    {
        var session = FakeSession.WithScalarPolls(1);
        var outcome = await Module(
                session,
                new FakeWatchClock(),
                connectFailure: new SqlTargetMismatchException($"mismatch {Token}"))
            .ExecuteAsync(Watch(untilUnchanged: 1));

        Assert.Equal(SqlHarnessExitCode.TargetMismatch, outcome.ExitCode);
        Assert.Empty(session.Commands);
        Assert.DoesNotContain(Token, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watch_authentication_failure_maps_to_three()
    {
        var azure = new FakeAzureCli(Token, new AzureCliException($"not logged in {Token}"));
        var session = FakeSession.WithScalarPolls(1);
        var outcome = await Module(session, new FakeWatchClock(), azure: azure).ExecuteAsync(
            Watch(untilUnchanged: 1));

        Assert.Equal(SqlHarnessExitCode.Authentication, outcome.ExitCode);
        Assert.Empty(session.Commands);
        Assert.DoesNotContain(Token, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watch_sql_failure_maps_to_five()
    {
        var session = FakeSession.WithScalarPolls(1);
        session.ExecuteFailure = new TimeoutException($"timeout {Token}");
        var outcome = await Module(session, new FakeWatchClock()).ExecuteAsync(
            Watch(untilUnchanged: 1));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.DoesNotContain(Token, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watch_gain_receipt_sums_raw_footprint_across_polls()
    {
        var gain = new FakeGainStore();
        var clock = new FakeWatchClock();
        var session = FakeSession.WithScalarPolls(1, 1);
        var outcome = await Module(session, clock, gain: gain).ExecuteAsync(
            Watch(untilUnchanged: 1));

        Assert.Empty(gain.Records);
        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(10, 1));

        var record = Assert.Single(gain.Records);
        Assert.Equal("watch", record.Command);
        Assert.True(record.Success);
        Assert.True(record.RawBytes > 0);
        Assert.True(record.RawLines > 0);
        Assert.Equal(10, record.EmittedBytes);
    }

    [Fact]
    public async Task Watch_poll_reports_use_clock_elapsed_milliseconds()
    {
        var clock = new FakeWatchClock
        {
            UtcNow = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        };
        var session = FakeSession.WithScalarPolls(1, 1);
        var outcome = await Module(session, clock).ExecuteAsync(
            Watch(untilUnchanged: 1, interval: TimeSpan.FromSeconds(10)));

        var report = Assert.IsType<SqlHarnessWatchReport>(outcome.Report);
        Assert.Equal(0, report.EmittedPolls[0].ElapsedMilliseconds);
        Assert.Equal(10_000, report.ElapsedMilliseconds);
    }

    private static SqlHarnessModule Module(
        FakeSession session,
        IWatchClock clock,
        FakeAzureCli? azure = null,
        FakeGainStore? gain = null,
        Exception? connectFailure = null,
        Func<IReadOnlyDictionary<string, TargetProfile>>? loadProfiles = null) =>
        new(
            new FakeSessionFactory(session, azure ?? new FakeAzureCli(Token), connectFailure),
            gain ?? new FakeGainStore(),
            loadProfiles ?? Profiles,
            clock);

    private static SqlHarnessWatchOperation Watch(
        string sql = "SELECT 1 AS Value",
        string? until = null,
        int? untilUnchanged = null,
        TimeSpan? interval = null,
        TimeSpan? maxDuration = null) =>
        new(
            Target(),
            sql,
            [],
            TimeoutSeconds: 30,
            MaxRows: 50,
            Interval: interval ?? TimeSpan.FromSeconds(30),
            MaxDuration: maxDuration ?? TimeSpan.FromMinutes(15),
            Until: until,
            UntilUnchanged: untilUnchanged);

    private static SqlTargetRequest Target() =>
        new("test", new Dictionary<string, string> { ["env"] = "a" });

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new("test-server", "testdb-{env}", new Dictionary<string, string> { ["env"] = "^(a|b)$" }, "integrated"),
        };

    private sealed class FakeAzureCli(string token, Exception? failure = null) : IAzureCli
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<bool> IsLoggedInAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<JsonElement> RunJsonAsync(IReadOnlyList<string> args, CancellationToken ct = default)
        {
            Calls.Add(args.ToArray());
            if (failure is not null)
                return Task.FromException<JsonElement>(failure);

            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { accessToken = token }));
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class FakeGainStore : IGainStore
    {
        public List<GainRecord> Records { get; } = [];

        public void Append(GainRecord record) => Records.Add(record);

        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class FakeSessionFactory(
        FakeSession session,
        FakeAzureCli azure,
        Exception? connectFailure) : ISqlSessionFactory
    {
        public async Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            if (connectFailure is not null)
                throw connectFailure;
            var tokenResponse = await azure.RunJsonAsync(
                ["account", "get-access-token", "--resource", "https://database.windows.net/"], ct);
            session.FactoryAccessToken = tokenResponse.GetProperty("accessToken").GetString();
            session.ConnectCount++;
            if (!string.Equals(target.Server, session.Identity.ActualServer, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(target.Database, session.Identity.ActualDatabase, StringComparison.Ordinal))
                throw new SqlTargetMismatchException("Connected SQL target identity does not match the resolved target.");
            return session;
        }
    }

    private sealed class FakeSession : ISqlSession
    {
        private readonly Queue<Func<ISqlReader>> _results;

        private FakeSession(IEnumerable<Func<ISqlReader>> results)
        {
            _results = new Queue<Func<ISqlReader>>(results);
        }

        public int ConnectCount { get; set; }
        public string? FactoryAccessToken { get; set; }
        public Exception? ExecuteFailure { get; set; }
        public List<SqlExecutionCommand> Commands { get; } = [];
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("test-server", "testdb-a", "test-server", "testdb-a", "profile");

        public static FakeSession WithScalarPolls(params object[] values)
        {
            var results = values.Select(value =>
            {
                ISqlReader reader = FakeScalarReader.Create(value);
                return (Func<ISqlReader>)(() => reader);
            });
            return new FakeSession(results);
        }

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (ExecuteFailure is not null)
                return Task.FromException<ISqlReader>(ExecuteFailure);
            if (_results.Count == 0)
                throw new InvalidOperationException("No more fake poll results queued.");
            return Task.FromResult(_results.Dequeue()());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeScalarReader(object value) : ISqlReader
    {
        private int _position = -1;

        public static FakeScalarReader Create(object value) => new(value);

        public int FieldCount => 1;
        public int RecordsAffected => -1;
        public string GetName(int ordinal) => "Value";
        public Type GetFieldType(int ordinal) => value.GetType();
        public bool GetAllowNull(int ordinal) => false;
        public object GetValue(int ordinal) => value;

        public Task<bool> ReadAsync(CancellationToken ct)
        {
            if (_position < 0)
            {
                _position = 0;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeWatchClock : IWatchClock
    {
        public DateTimeOffset UtcNow { get; set; } =
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public bool BlockDelay { get; set; }
        public Action? OnDelay { get; set; }
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            Delays.Add(delay);
            OnDelay?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (!BlockDelay)
                UtcNow += delay;
            return Task.CompletedTask;
        }
    }
}