using System.Text.Json;

using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
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
        // T0 poll1, delay to T30 poll2, delay to T60, then deadline: no further poll.
        Assert.Equal(2, report.PollCount);
        Assert.Equal([1, 2], report.EmittedPolls.Select(p => p.Poll));
        Assert.Equal(2, clock.Delays.Count);
        Assert.Equal(2, session.Commands.Count);
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
    public async Task Watch_max_duration_gain_receipt_counts_as_success()
    {
        var gain = new FakeGainStore();
        var clock = new FakeWatchClock();
        var session = FakeSession.WithScalarPolls(1, 2, 3, 4, 5, 6, 7, 8);
        var outcome = await Module(session, clock, gain: gain).ExecuteAsync(
            Watch(
                untilUnchanged: 100,
                interval: TimeSpan.FromSeconds(30),
                maxDuration: TimeSpan.FromSeconds(60)));

        Assert.Equal(SqlHarnessExitCode.WatchMaxDuration, outcome.ExitCode);
        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(5, 1));

        var record = Assert.Single(gain.Records);
        Assert.Equal("watch", record.Command);
        Assert.True(record.Success);
    }

    [Fact]
    public async Task Watch_rejects_max_rows_zero_with_until_before_authentication()
    {
        var azure = new FakeAzureCli(Token);
        var session = FakeSession.WithScalarPolls(1);
        var outcome = await Module(session, new FakeWatchClock(), azure: azure).ExecuteAsync(
            Watch(until: "Value >= 1", maxRows: 0));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Contains("max-rows", outcome.SafeError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.ConnectCount);
    }

    [Fact]
    public async Task Watch_clamps_delay_to_remaining_max_duration()
    {
        var clock = new FakeWatchClock();
        // Interval 30s, budget 40s: poll at 0, delay 30 to 30, poll at 30, delay clamp 10 to 40, exit max-duration without another poll.
        var session = FakeSession.WithScalarPolls(1, 2, 3, 4, 5);
        var outcome = await Module(session, clock).ExecuteAsync(
            Watch(
                untilUnchanged: 100,
                interval: TimeSpan.FromSeconds(30),
                maxDuration: TimeSpan.FromSeconds(40)));

        var report = Assert.IsType<SqlHarnessWatchReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.WatchMaxDuration, outcome.ExitCode);
        Assert.Equal(WatchExitReason.MaxDuration, report.ExitReason);
        Assert.Equal(2, report.PollCount);
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)], clock.Delays);
        Assert.Equal(2, session.Commands.Count);
    }

    [Fact]
    public async Task Watch_condition_met_exactly_at_deadline()
    {
        var clock = new FakeWatchClock();
        var session = FakeSession.WithScalarPolls(2);
        // The read completes exactly at the deadline: still in budget.
        session.BeforeResult = () => clock.Advance(TimeSpan.FromSeconds(60));
        var outcome = await Module(session, clock).ExecuteAsync(
            Watch(until: "Value >= 2", maxDuration: TimeSpan.FromSeconds(60)));

        var report = Assert.IsType<SqlHarnessWatchReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(WatchExitReason.ConditionMet, report.ExitReason);
        Assert.Equal(1, report.PollCount);
        Assert.Equal([1], report.EmittedPolls.Select(p => p.Poll));
    }

    [Fact]
    public async Task Watch_result_completed_after_deadline_exits_max_duration()
    {
        var clock = new FakeWatchClock();
        var session = FakeSession.WithScalarPolls(2);
        // The read overruns the deadline: the matching late result is dropped and
        // the report keeps the last complete in-budget state (here: none yet).
        session.BeforeResult = () => clock.Advance(TimeSpan.FromSeconds(60).Add(TimeSpan.FromMilliseconds(1)));
        var outcome = await Module(session, clock).ExecuteAsync(
            Watch(until: "Value >= 2", maxDuration: TimeSpan.FromSeconds(60)));

        var report = Assert.IsType<SqlHarnessWatchReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.WatchMaxDuration, outcome.ExitCode);
        Assert.Equal(WatchExitReason.MaxDuration, report.ExitReason);
        Assert.Equal(1, report.PollCount);
        Assert.Empty(report.EmittedPolls);
    }

    [Fact]
    public async Task Watch_command_timeout_clamped_to_remaining_budget()
    {
        var clock = new FakeWatchClock();
        var session = FakeSession.WithScalarPolls(1, 2, 3);
        var outcome = await Module(session, clock).ExecuteAsync(
            Watch(
                untilUnchanged: 100,
                interval: TimeSpan.FromSeconds(30),
                maxDuration: TimeSpan.FromSeconds(40)) with
            { TimeoutSeconds = 300 });

        var report = Assert.IsType<SqlHarnessWatchReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.WatchMaxDuration, outcome.ExitCode);
        Assert.Equal(WatchExitReason.MaxDuration, report.ExitReason);
        Assert.Equal(2, session.Commands.Count);
        Assert.Equal(40, session.Commands[0].TimeoutSeconds);
        Assert.Equal(10, session.Commands[1].TimeoutSeconds);
        var scope = Assert.Single(clock.Budgets);
        Assert.Equal(scope.Token, session.CapturedTokens[0]);
        Assert.Equal(scope.Token, session.CapturedTokens[1]);
    }

    [Fact]
    public async Task Watch_subsecond_remainder_uses_unit_timeout_and_linked_token()
    {
        var clock = new FakeWatchClock { Elapsed = TimeSpan.FromSeconds(39.5) };
        var session = FakeSession.WithScalarPolls(1, 2);
        var outcome = await Module(session, clock).ExecuteAsync(
            Watch(
                untilUnchanged: 100,
                interval: TimeSpan.FromSeconds(30),
                maxDuration: TimeSpan.FromSeconds(40)) with
            { TimeoutSeconds = 300 });

        var report = Assert.IsType<SqlHarnessWatchReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.WatchMaxDuration, outcome.ExitCode);
        Assert.Equal(WatchExitReason.MaxDuration, report.ExitReason);
        Assert.Single(session.Commands);
        // 0.5 s of budget left: whole-second CommandTimeout floors to 1 s and the
        // linked token (not the timeout) enforces the exact deadline.
        Assert.Equal(1, session.Commands[0].TimeoutSeconds);
        Assert.Equal([TimeSpan.FromSeconds(0.5)], clock.Delays);
        var scope = Assert.Single(clock.Budgets);
        Assert.Equal(scope.Token, session.CapturedTokens[0]);
    }

    [Fact]
    public async Task Watch_blocking_connect_receives_budget_cancellation()
    {
        var clock = new FakeWatchClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BlockingConnectFactory(entered);
        using var userCts = new CancellationTokenSource();
        var module = new SqlHarnessModule(factory, new FakeGainStore(), Profiles, clock);

        var task = module.ExecuteAsync(Watch(untilUnchanged: 100), userCts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Budgets.Single().Cancel();
        var outcome = await task;

        Assert.Equal(SqlHarnessExitCode.WatchMaxDuration, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.True(factory.CapturedToken.IsCancellationRequested);
        Assert.False(userCts.IsCancellationRequested);
    }

    [Fact]
    public async Task Watch_blocking_poll_receives_budget_cancellation()
    {
        var clock = new FakeWatchClock();
        var session = FakeSession.WithScalarPolls(1);
        var enteredSecondPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var capturedPollToken = CancellationToken.None;
        session.ExecuteHandler = async (command, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                return FakeScalarReader.Create(1);
            capturedPollToken = token;
            enteredSecondPoll.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable.");
        };
        using var userCts = new CancellationTokenSource();
        var task = Module(session, clock).ExecuteAsync(
            Watch(untilUnchanged: 100, interval: TimeSpan.FromSeconds(30)), userCts.Token);
        await enteredSecondPoll.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Budgets.Single().Cancel();
        var outcome = await task;

        var report = Assert.IsType<SqlHarnessWatchReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.WatchMaxDuration, outcome.ExitCode);
        Assert.Equal(WatchExitReason.MaxDuration, report.ExitReason);
        Assert.Equal(2, report.PollCount);
        Assert.Equal([1], report.EmittedPolls.Select(p => p.Poll));
        Assert.Equal(2, session.Commands.Count);
        Assert.True(capturedPollToken.IsCancellationRequested);
        Assert.False(userCts.IsCancellationRequested);
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

    [Fact]
    public async Task Watch_invalid_parameter_value_is_redacted_before_connect()
    {
        var session = FakeSession.WithScalarPolls(1);
        var azure = new FakeAzureCli(Token);

        var outcome = await Module(session, new FakeWatchClock(), azure: azure).ExecuteAsync(
            Watch(sql: "SELECT @n AS Value", untilUnchanged: 1) with
            {
                Parameters = ["n:int=private-audit-value"],
            });

        Assert.Equal(0, session.ConnectCount);
        Assert.Empty(azure.Calls);
        Assert.Empty(session.Commands);
        AssertParameterError(
            outcome,
            "Invalid value for SQL parameter 'n' of type 'int'.",
            "private-audit-value",
            "n:int=private-audit-value");
    }

    private static void AssertParameterError(SqlHarnessOutcome outcome, string expected, params string[] forbidden)
    {
        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(expected, outcome.SafeError);
        Assert.DoesNotContain(" | ", outcome.SafeError, StringComparison.Ordinal);
        foreach (var secret in forbidden)
            Assert.DoesNotContain(secret, outcome.SafeError, StringComparison.Ordinal);

        var stdout = new StringWriter();
        new Renderer().Render(outcome, OutputMode.Text, new OutputCaptureWriter(stdout));
        var rendered = stdout.ToString();
        Assert.Contains(expected, rendered, StringComparison.Ordinal);
        foreach (var secret in forbidden)
            Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), "sqlharness-audit-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var report = Path.Combine(directory, "error.txt");
            File.WriteAllText(report, rendered);
            var saved = File.ReadAllText(report);
            Assert.Contains(expected, saved, StringComparison.Ordinal);
            foreach (var secret in forbidden)
                Assert.DoesNotContain(secret, saved, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

    [Fact]
    public async Task Watch_bounds_history_to_history_limit_with_counters_and_last_result()
    {
        var clock = new FakeWatchClock();
        var values = Enumerable.Range(1, 10000).Cast<object>().ToArray();
        var session = FakeSession.WithScalarPolls(values);
        var outcome = await Module(session, clock).ExecuteAsync(
            Watch(until: "Value >= 10000", interval: TimeSpan.FromMilliseconds(1)) with { HistoryLimit = 100 });

        var report = Assert.IsType<SqlHarnessWatchReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(WatchExitReason.ConditionMet, report.ExitReason);
        Assert.Equal(10000, report.PollCount);
        Assert.Equal(10000, report.TotalChangedPolls);
        Assert.Equal(9900, report.OmittedPolls);
        Assert.Equal(100, report.EmittedPolls.Count);
        Assert.Equal(9901, report.EmittedPolls[0].Poll);
        Assert.Equal(10000, report.EmittedPolls[^1].Poll);
        Assert.Equal(10000, Assert.Single(report.EmittedPolls[^1].ResultSets[0].Rows[0]));
    }

    [Fact]
    public async Task Watch_unchanged_criterion_ignores_history_retention()
    {
        var clock = new FakeWatchClock();
        var session = FakeSession.WithScalarPolls(1, 2, 2, 2);
        var outcome = await Module(session, clock).ExecuteAsync(
            Watch(untilUnchanged: 2, interval: TimeSpan.FromMilliseconds(1)) with { HistoryLimit = 1 });

        var report = Assert.IsType<SqlHarnessWatchReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(WatchExitReason.Unchanged, report.ExitReason);
        Assert.Equal(4, report.PollCount);
        Assert.Equal(2, report.TotalChangedPolls);
        Assert.Equal(1, report.OmittedPolls);
        Assert.Equal([2], report.EmittedPolls.Select(p => p.Poll));
    }

    [Fact]
    public async Task Watch_rejects_history_limit_outside_range_before_authentication()
    {
        foreach (var historyLimit in new[] { 0, 10001 })
        {
            var azure = new FakeAzureCli(Token);
            var session = FakeSession.WithScalarPolls(1);
            var outcome = await Module(session, new FakeWatchClock(), azure: azure).ExecuteAsync(
                Watch(untilUnchanged: 1) with { HistoryLimit = historyLimit });

            Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
            Assert.Contains("--history-limit", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
            Assert.Empty(azure.Calls);
            Assert.Equal(0, session.ConnectCount);
        }
    }

    private static SqlHarnessWatchOperation Watch(
        string sql = "SELECT 1 AS Value",
        string? until = null,
        int? untilUnchanged = null,
        TimeSpan? interval = null,
        TimeSpan? maxDuration = null,
        int maxRows = 50) =>
        new(
            Target(),
            sql,
            [],
            TimeoutSeconds: 30,
            MaxRows: maxRows,
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
        public List<CancellationToken> CapturedTokens { get; } = [];
        public Action? BeforeResult { get; set; }
        public Func<SqlExecutionCommand, CancellationToken, Task<ISqlReader>>? ExecuteHandler { get; set; }
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
            CapturedTokens.Add(ct);
            BeforeResult?.Invoke();
            if (ExecuteHandler is not null)
                return ExecuteHandler(command, ct);
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

        public TimeSpan Elapsed { get; set; } = TimeSpan.Zero;

        public bool BlockDelay { get; set; }
        public Action? OnDelay { get; set; }
        public List<TimeSpan> Delays { get; } = [];
        public List<FakeWatchBudget> Budgets { get; } = [];

        public void Advance(TimeSpan delta)
        {
            UtcNow += delta;
            Elapsed += delta;
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            Delays.Add(delay);
            OnDelay?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (!BlockDelay)
                Advance(delay);
            return Task.CompletedTask;
        }

        public IWatchBudget StartBudget(TimeSpan budget, CancellationToken ct)
        {
            var scope = new FakeWatchBudget(this, budget, ct);
            Budgets.Add(scope);
            return scope;
        }
    }

    private sealed class FakeWatchBudget : IWatchBudget
    {
        private readonly FakeWatchClock _clock;
        private readonly TimeSpan _budget;
        private readonly CancellationTokenSource _cts;
        private readonly CancellationToken _token;

        public FakeWatchBudget(FakeWatchClock clock, TimeSpan budget, CancellationToken ct)
        {
            _clock = clock;
            _budget = budget;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _token = _cts.Token;
        }

        public TimeSpan Remaining
        {
            get
            {
                var remaining = _budget - _clock.Elapsed;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        public bool IsExpired => _clock.Elapsed > _budget;

        public CancellationToken Token => _token;

        public void Cancel() => _cts.Cancel();

        public void Dispose() => _cts.Dispose();
    }

    private sealed class BlockingConnectFactory(TaskCompletionSource entered) : ISqlSessionFactory
    {
        public CancellationToken CapturedToken;

        public async Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            CapturedToken = ct;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}