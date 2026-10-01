using System.Globalization;
using System.Text;
using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public class WatchNdjsonTests
{
    private const string Token = "fake-access-token-never-emit";

    [Fact]
    public void NdjsonWriter_flushes_after_every_event_with_strict_sequence()
    {
        var transport = new CountingWriter();
        var stream = new WatchNdjsonWriter(transport);

        stream.WriteStarted(new { target = "t" }, 0);
        Assert.Equal(["started"], transport.Events());
        Assert.Equal([1], transport.Sequences());

        stream.WriteChanged(new { poll = 1 }, 10);
        Assert.Equal(["started", "changed"], transport.Events());
        Assert.Equal([1, 2], transport.Sequences());

        stream.WriteCompleted(new { exitReason = "condition-met" }, 20);
        Assert.Equal(["started", "changed", "completed"], transport.Events());
        Assert.Equal([1, 2, 3], transport.Sequences());

        // One flush per record: consumers observe each event immediately.
        Assert.Equal(3, transport.FlushCount);
        Assert.True(stream.IsClosed);
    }

    [Fact]
    public void NdjsonWriter_schema_version_is_one_on_every_record()
    {
        var transport = new CountingWriter();
        var stream = new WatchNdjsonWriter(transport);

        stream.WriteStarted(null, 0);
        stream.WriteFailed(new { exitCode = 5 }, 1);

        Assert.Equal([1, 1], transport.SchemaVersions());
    }

    [Fact]
    public void NdjsonWriter_serialization_failure_writes_nothing_and_consumes_no_sequence()
    {
        var transport = new CountingWriter();
        var stream = new WatchNdjsonWriter(transport);

        // The transport itself cannot fail here (plain StringWriter), so any
        // throw is a serialization failure.
        Assert.ThrowsAny<Exception>(() => stream.WriteChanged(new { payload = new ThrowingPayload() }, 0));

        Assert.Empty(transport.Lines());
        Assert.Equal(0, transport.FlushCount);
        Assert.Equal(1, stream.NextSequence);
        Assert.False(stream.IsClosed);

        // The stream stays usable and the next record keeps sequence 1.
        stream.WriteChanged(new { poll = 1 }, 0);
        Assert.Equal([1], transport.Sequences());
        Assert.Equal(1, transport.FlushCount);
    }

    [Fact]
    public void NdjsonWriter_rejects_second_terminal_record()
    {
        var stream = new WatchNdjsonWriter(new CountingWriter());

        stream.WriteStarted(null, 0);
        stream.WriteCompleted(new { exitReason = "unchanged" }, 0);

        Assert.Throws<InvalidOperationException>(() => stream.WriteFailed(new { exitCode = 5 }, 0));
        Assert.Throws<InvalidOperationException>(() => stream.WriteCompleted(new { exitReason = "unchanged" }, 0));
        Assert.Throws<InvalidOperationException>(() => stream.WriteChanged(new { poll = 9 }, 0));
    }

    [Fact]
    public async Task Ndjson_emits_started_changed_completed_with_single_terminal()
    {
        var transport = new StringWriter();
        var outcome = await Module(FakeSession.WithScalarPolls(1, 1, 2), new FakeWatchClock())
            .ExecuteWatchNdjsonAsync(Watch(until: "Value >= 2"), transport);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Null(outcome.Report);

        var records = Parse(transport);
        Assert.Equal(["started", "changed", "changed", "completed"], records.Select(r => r.Event));
        Assert.Equal([1, 2, 3, 4], records.Select(r => r.Sequence));
        Assert.All(records, r => Assert.Equal(1, r.SchemaVersion));

        // No changed record for the unchanged second poll.
        Assert.Equal([1, 3], records.Where(r => r.Event == "changed").Select(r => r.Data.GetProperty("poll").GetInt32()));

        var completed = Assert.Single(records, r => r.Event == "completed");
        Assert.Equal("condition-met", completed.Data.GetProperty("exitReason").GetString());
        Assert.Equal(3, completed.Data.GetProperty("pollCount").GetInt32());
        Assert.Equal(2, completed.Data.GetProperty("totalChangedPolls").GetInt32());
        Assert.Equal(0, completed.Data.GetProperty("omittedPolls").GetInt32());

        var started = Assert.Single(records, r => r.Event == "started");
        Assert.Equal("testdb-a", started.Data.GetProperty("target").GetProperty("actualDatabase").GetString());
    }

    [Fact]
    public async Task Ndjson_deadline_emits_completed_with_max_duration()
    {
        var clock = new FakeWatchClock();
        var transport = new StringWriter();
        var outcome = await Module(FakeSession.WithScalarPolls(1, 2, 3, 4, 5, 6, 7, 8), clock)
            .ExecuteWatchNdjsonAsync(
                Watch(
                    untilUnchanged: 100,
                    interval: TimeSpan.FromSeconds(30),
                    maxDuration: TimeSpan.FromSeconds(60)),
                transport);

        Assert.Equal(SqlHarnessExitCode.WatchMaxDuration, outcome.ExitCode);

        var records = Parse(transport);
        Assert.Equal(["started", "changed", "changed", "completed"], records.Select(r => r.Event));
        var completed = Assert.Single(records, r => r.Event == "completed");
        Assert.Equal("max-duration", completed.Data.GetProperty("exitReason").GetString());
        Assert.Equal(2, completed.Data.GetProperty("pollCount").GetInt32());
    }

    [Fact]
    public async Task Ndjson_cancellation_midstream_emits_failed_once()
    {
        var clock = new FakeWatchClock { BlockDelay = true };
        var transport = new StringWriter();
        using var cts = new CancellationTokenSource();
        clock.OnDelay = () => cts.Cancel();

        var outcome = await Module(FakeSession.WithScalarPolls(1, 2, 3), clock)
            .ExecuteWatchNdjsonAsync(Watch(untilUnchanged: 100, interval: TimeSpan.FromSeconds(30)), transport, cts.Token);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);

        var records = Parse(transport);
        Assert.Equal(["started", "changed", "failed"], records.Select(r => r.Event));
        Assert.Equal([1, 2, 3], records.Select(r => r.Sequence));
        var failed = Assert.Single(records, r => r.Event == "failed");
        Assert.Equal((int)SqlHarnessExitCode.SqlExecution, failed.Data.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Ndjson_transport_break_midstream_is_failure_never_success()
    {
        // Two lines fit, then the transport dies: started + first changed are
        // on the wire, the second changed and the failed fallback both fail.
        var transport = new FlakyWriter(budget: 2);
        var outcome = await Module(FakeSession.WithScalarPolls(1, 2, 3), new FakeWatchClock())
            .ExecuteWatchNdjsonAsync(Watch(until: "Value >= 3"), transport);

        Assert.NotEqual(SqlHarnessExitCode.Success, outcome.ExitCode);

        var records = Parse(transport);
        Assert.Equal(["started", "changed"], records.Select(r => r.Event));
        Assert.DoesNotContain(records, r => r.Event is "completed" or "failed");
    }

    [Fact]
    public async Task Ndjson_empty_transport_break_is_failure_never_success()
    {
        var transport = new FlakyWriter(budget: 0);
        var outcome = await Module(FakeSession.WithScalarPolls(1), new FakeWatchClock())
            .ExecuteWatchNdjsonAsync(Watch(untilUnchanged: 1), transport);

        Assert.NotEqual(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.NotNull(outcome.SafeError);
        Assert.Empty(Parse(transport));
    }

    [Fact]
    public async Task Ndjson_slow_consumer_receives_ordered_stream()
    {
        var transport = new SlowWriter();
        var outcome = await Module(FakeSession.WithScalarPolls(1, 2, 3), new FakeWatchClock())
            .ExecuteWatchNdjsonAsync(Watch(until: "Value >= 3"), transport);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);

        var records = Parse(transport);
        Assert.Equal(["started", "changed", "changed", "changed", "completed"], records.Select(r => r.Event));
        Assert.Equal([1, 2, 3, 4, 5], records.Select(r => r.Sequence));
    }

    [Fact]
    public async Task Ndjson_streams_every_change_without_history_retention()
    {
        var values = Enumerable.Range(1, 300).Cast<object>().ToArray();
        var transport = new StringWriter();
        var outcome = await Module(FakeSession.WithScalarPolls(values), new FakeWatchClock())
            .ExecuteWatchNdjsonAsync(
                Watch(until: "Value >= 300", interval: TimeSpan.FromMilliseconds(1)) with { HistoryLimit = 100 },
                transport);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        // The outcome retains nothing: the stream is the output.
        Assert.Null(outcome.Report);

        var records = Parse(transport);
        Assert.Equal(302, records.Count);
        Assert.Equal("started", records[0].Event);
        Assert.Equal("completed", records[^1].Event);
        Assert.Equal(300, records.Count(r => r.Event == "changed"));
        var completed = Assert.Single(records, r => r.Event == "completed");
        Assert.Equal(300, completed.Data.GetProperty("totalChangedPolls").GetInt32());
        Assert.Equal(300, completed.Data.GetProperty("pollCount").GetInt32());
    }

    [Fact]
    public async Task Ndjson_sql_failure_emits_failed_with_redacted_message()
    {
        var session = FakeSession.WithScalarPolls(1);
        session.ExecuteFailure = new TimeoutException($"timeout {Token}");
        var transport = new StringWriter();
        var outcome = await Module(session, new FakeWatchClock())
            .ExecuteWatchNdjsonAsync(Watch(untilUnchanged: 1), transport);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.DoesNotContain(Token, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);

        var records = Parse(transport);
        Assert.Equal(["started", "failed"], records.Select(r => r.Event));
        var failed = Assert.Single(records, r => r.Event == "failed");
        Assert.Equal((int)SqlHarnessExitCode.SqlExecution, failed.Data.GetProperty("exitCode").GetInt32());
        Assert.Equal("sql_execution_failed", failed.Data.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(Token, failed.Data.GetProperty("error").GetProperty("message").GetString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ndjson_connect_deadline_emits_failed_only_stream_with_deadline_exit()
    {
        var clock = new FakeWatchClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BlockingConnectFactory(entered);
        var transport = new StringWriter();
        var module = new SqlHarnessModule(factory, new FakeGainStore(), Profiles, clock);

        var task = module.ExecuteWatchNdjsonAsync(Watch(untilUnchanged: 100), transport);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Budgets.Single().Cancel();
        var outcome = await task;

        Assert.Equal(SqlHarnessExitCode.WatchMaxDuration, outcome.ExitCode);

        var records = Parse(transport);
        var failed = Assert.Single(records);
        Assert.Equal("failed", failed.Event);
        Assert.Equal(1, failed.Sequence);
        Assert.Equal((int)SqlHarnessExitCode.WatchMaxDuration, failed.Data.GetProperty("exitCode").GetInt32());
        Assert.Equal("watch_max_duration", failed.Data.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Ndjson_validation_failure_emits_failed_without_started()
    {
        var transport = new StringWriter();
        var outcome = await Module(FakeSession.WithScalarPolls(1), new FakeWatchClock())
            .ExecuteWatchNdjsonAsync(Watch(until: "not-a-predicate"), transport);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);

        var records = Parse(transport);
        var failed = Assert.Single(records);
        Assert.Equal("failed", failed.Event);
        Assert.Equal(1, failed.Sequence);
        Assert.Equal((int)SqlHarnessExitCode.Safety, failed.Data.GetProperty("exitCode").GetInt32());
    }

    // 012/T1 review gap: ExecuteWatchNdjsonAsync (the NDJSON streaming twin of
    // ExecuteWatchAsync) reads TypedParameters too, but had no test of its own;
    // CompareMatrixTests only exercises the non-streaming watch dispatch.
    [Fact]
    public async Task Ndjson_typed_parameter_is_bound_whole_into_the_poll()
    {
        var session = FakeSession.WithScalarPolls(1, 1);
        var transport = new StringWriter();
        var operation = Watch(sql: "SELECT @Flag AS Value", untilUnchanged: 1) with
        {
            TypedParameters = [new SqlHarnessParameterInput("Flag", null, "a,b=c:d")],
        };

        var outcome = await Module(session, new FakeWatchClock()).ExecuteWatchNdjsonAsync(operation, transport);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.NotEmpty(session.Commands);
        Assert.All(session.Commands, command =>
            Assert.Equal("a,b=c:d", command.Parameters.Single(p => p.Name == "@Flag").Value));
    }

    [Fact]
    public async Task Ndjson_typed_parameter_rejection_emits_failed_without_echoing_the_value()
    {
        var transport = new StringWriter();
        var operation = Watch(untilUnchanged: 1) with
        {
            TypedParameters = [new SqlHarnessParameterInput("Flag", "int", "nope-secret")],
        };

        var outcome = await Module(FakeSession.WithScalarPolls(1), new FakeWatchClock())
            .ExecuteWatchNdjsonAsync(operation, transport);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal("Invalid value for SQL parameter 'Flag' of type 'int'.", outcome.SafeError);
        Assert.DoesNotContain("nope-secret", transport.ToString(), StringComparison.Ordinal);

        var records = Parse(transport);
        var failed = Assert.Single(records);
        Assert.Equal("failed", failed.Event);
    }

    [Fact]
    public async Task Ndjson_gain_receipt_covers_completed_stream_as_success()
    {
        var gain = new FakeGainStore();
        var transport = new StringWriter();
        var outcome = await Module(FakeSession.WithScalarPolls(1, 1), new FakeWatchClock(), gain)
            .ExecuteWatchNdjsonAsync(Watch(untilUnchanged: 1), transport);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Empty(gain.Records);
        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(10, 4));

        var record = Assert.Single(gain.Records);
        Assert.Equal("watch", record.Command);
        Assert.True(record.Success);
        Assert.Equal(10, record.EmittedBytes);
        Assert.Equal(4, record.EmittedLines);
    }

    [Fact]
    public void Capabilities_announces_watch_ndjson_events()
    {
        var capabilities = SqlHarnessCapabilitiesProvider.Get();

        Assert.True(capabilities.Limits.TryGetValue("watchNdjson", out var entry));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(entry));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            ["started", "changed", "completed", "failed"],
            root.GetProperty("events").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains(capabilities.Commands, command => command.Name == "watch");
    }

    [Fact]
    public async Task Watch_ndjson_cli_streams_lines_and_counts_gain()
    {
        var sql = TempFile("SELECT 1 AS Value");
        try
        {
            var module = new StreamingModule();
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(module, output).RunAsync(
                ["watch", "dev", "--file", sql, "--output", "ndjson", "--until-unchanged", "1"]);

            Assert.Equal(0, exit);
            var operation = Assert.IsType<SqlHarnessWatchOperation>(Assert.Single(module.Operations));
            Assert.Equal(1, operation.UntilUnchanged);

            var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Equal("started", JsonDocument.Parse(lines[0]).RootElement.GetProperty("event").GetString());
            Assert.Equal("completed", JsonDocument.Parse(lines[1]).RootElement.GetProperty("event").GetString());

            // Gain counts the whole stream as emitted output.
            Assert.NotNull(module.CapturedFootprint);
            Assert.Equal(Encoding.UTF8.GetByteCount(output.ToString()), module.CapturedFootprint.Bytes);
            Assert.Equal(2, module.CapturedFootprint.Lines);
        }
        finally
        {
            File.Delete(sql);
        }
    }

    [Fact]
    public async Task Watch_ndjson_cli_rejects_json_combination()
    {
        var sql = TempFile("SELECT 1 AS Value");
        try
        {
            var module = new StreamingModule();
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(module, output).RunAsync(
                ["watch", "dev", "--file", sql, "--output", "ndjson", "--json"]);

            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Contains("Choose only one of --output, --json, or --json-summary.", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(module.Operations);
        }
        finally
        {
            File.Delete(sql);
        }
    }

    [Fact]
    public async Task Query_ndjson_output_is_rejected()
    {
        var sql = TempFile("SELECT 1 AS Value");
        try
        {
            var module = new StreamingModule();
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(module, output).RunAsync(
                ["query", "dev", "--file", sql, "--output", "ndjson"]);

            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Empty(module.Operations);
        }
        finally
        {
            File.Delete(sql);
        }
    }

    [Fact]
    public async Task Watch_json_output_is_unchanged_by_ndjson()
    {
        var sql = TempFile("SELECT 1 AS Value");
        try
        {
            var report = new SqlHarnessWatchReport(
                new("s", "d", "s", "d", "profile"), 1, 0, WatchExitReason.ConditionMet, []);
            var module = new FixedModule(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null));
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(module, output).RunAsync(
                ["watch", "dev", "--file", sql, "--json"]);

            Assert.Equal(0, exit);
            using var document = JsonDocument.Parse(output.ToString());
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("pollCount").GetInt32());
            Assert.True(root.TryGetProperty("emittedPolls", out _));
            Assert.False(root.TryGetProperty("sequence", out _));
            Assert.False(root.TryGetProperty("schemaVersion", out _));
        }
        finally
        {
            File.Delete(sql);
        }
    }

    [Fact]
    public async Task Parity_condition_met_matches_report_path()
    {
        var reportOutcome = await Module(FakeSession.WithScalarPolls(1, 1, 2), new FakeWatchClock())
            .ExecuteAsync(Watch(until: "Value >= 2"));

        var report = Assert.IsType<SqlHarnessWatchReport>(reportOutcome.Report);
        Assert.Equal(SqlHarnessExitCode.Success, reportOutcome.ExitCode);
        Assert.Equal(WatchExitReason.ConditionMet, report.ExitReason);

        var transport = new StringWriter();
        var ndjsonOutcome = await Module(FakeSession.WithScalarPolls(1, 1, 2), new FakeWatchClock())
            .ExecuteWatchNdjsonAsync(Watch(until: "Value >= 2"), transport);

        Assert.Equal(reportOutcome.ExitCode, ndjsonOutcome.ExitCode);
        Assert.Null(ndjsonOutcome.Report);

        var records = Parse(transport);
        Assert.Equal(["started", "changed", "changed", "completed"], records.Select(r => r.Event));
        var completed = Assert.Single(records, r => r.Event == "completed");
        Assert.Equal("condition-met", completed.Data.GetProperty("exitReason").GetString());
        Assert.Equal(report.PollCount, completed.Data.GetProperty("pollCount").GetInt32());
        Assert.Equal(report.TotalChangedPolls, completed.Data.GetProperty("totalChangedPolls").GetInt32());
        Assert.Equal(0, completed.Data.GetProperty("omittedPolls").GetInt32());
    }

    [Fact]
    public async Task Parity_unchanged_matches_report_path()
    {
        var reportOutcome = await Module(FakeSession.WithScalarPolls(5, 5), new FakeWatchClock())
            .ExecuteAsync(Watch(untilUnchanged: 1));

        var report = Assert.IsType<SqlHarnessWatchReport>(reportOutcome.Report);
        Assert.Equal(SqlHarnessExitCode.Success, reportOutcome.ExitCode);
        Assert.Equal(WatchExitReason.Unchanged, report.ExitReason);

        var transport = new StringWriter();
        var ndjsonOutcome = await Module(FakeSession.WithScalarPolls(5, 5), new FakeWatchClock())
            .ExecuteWatchNdjsonAsync(Watch(untilUnchanged: 1), transport);

        Assert.Equal(reportOutcome.ExitCode, ndjsonOutcome.ExitCode);
        Assert.Null(ndjsonOutcome.Report);

        var records = Parse(transport);
        Assert.Equal(["started", "changed", "completed"], records.Select(r => r.Event));
        // No changed record for the unchanged repeat poll.
        Assert.Equal([1], records.Where(r => r.Event == "changed").Select(r => r.Data.GetProperty("poll").GetInt32()));
        var completed = Assert.Single(records, r => r.Event == "completed");
        Assert.Equal("unchanged", completed.Data.GetProperty("exitReason").GetString());
        Assert.Equal(report.PollCount, completed.Data.GetProperty("pollCount").GetInt32());
        Assert.Equal(report.TotalChangedPolls, completed.Data.GetProperty("totalChangedPolls").GetInt32());
        Assert.Equal(0, completed.Data.GetProperty("omittedPolls").GetInt32());
    }

    [Fact]
    public async Task Parity_connect_timeout_matches_report_path()
    {
        var reportClock = new FakeWatchClock();
        var reportEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportFactory = new BlockingConnectFactory(reportEntered);
        var reportModule = new SqlHarnessModule(reportFactory, new FakeGainStore(), Profiles, reportClock);
        var reportTask = reportModule.ExecuteAsync(Watch(untilUnchanged: 100));
        await reportEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        reportClock.Budgets.Single().Cancel();
        var reportOutcome = await reportTask;

        Assert.Equal(SqlHarnessExitCode.WatchMaxDuration, reportOutcome.ExitCode);
        Assert.Null(reportOutcome.Report);

        var ndjsonClock = new FakeWatchClock();
        var ndjsonEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ndjsonFactory = new BlockingConnectFactory(ndjsonEntered);
        var ndjsonModule = new SqlHarnessModule(ndjsonFactory, new FakeGainStore(), Profiles, ndjsonClock);
        var transport = new StringWriter();
        var ndjsonTask = ndjsonModule.ExecuteWatchNdjsonAsync(Watch(untilUnchanged: 100), transport);
        await ndjsonEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ndjsonClock.Budgets.Single().Cancel();
        var ndjsonOutcome = await ndjsonTask;

        Assert.Equal(reportOutcome.ExitCode, ndjsonOutcome.ExitCode);
        Assert.Null(ndjsonOutcome.Report);

        var records = Parse(transport);
        var failed = Assert.Single(records);
        Assert.Equal("failed", failed.Event);
        Assert.Equal((int)SqlHarnessExitCode.WatchMaxDuration, failed.Data.GetProperty("exitCode").GetInt32());
        Assert.Equal("watch_max_duration", failed.Data.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Parity_blocking_poll_timeout_matches_report_path()
    {
        var reportSession = BlockingSecondPollSession(out var reportEntered);
        var reportClock = new FakeWatchClock();
        var reportTask = Module(reportSession, reportClock).ExecuteAsync(
            Watch(untilUnchanged: 100, interval: TimeSpan.FromSeconds(30)));
        await reportEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        reportClock.Budgets.Single().Cancel();
        var reportOutcome = await reportTask;

        var report = Assert.IsType<SqlHarnessWatchReport>(reportOutcome.Report);
        Assert.Equal(SqlHarnessExitCode.WatchMaxDuration, reportOutcome.ExitCode);
        Assert.Equal(WatchExitReason.MaxDuration, report.ExitReason);

        var ndjsonSession = BlockingSecondPollSession(out var ndjsonEntered);
        var ndjsonClock = new FakeWatchClock();
        var transport = new StringWriter();
        var ndjsonTask = Module(ndjsonSession, ndjsonClock).ExecuteWatchNdjsonAsync(
            Watch(untilUnchanged: 100, interval: TimeSpan.FromSeconds(30)), transport);
        await ndjsonEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ndjsonClock.Budgets.Single().Cancel();
        var ndjsonOutcome = await ndjsonTask;

        Assert.Equal(reportOutcome.ExitCode, ndjsonOutcome.ExitCode);
        Assert.Null(ndjsonOutcome.Report);

        var records = Parse(transport);
        Assert.Equal(["started", "changed", "completed"], records.Select(r => r.Event));
        var completed = Assert.Single(records, r => r.Event == "completed");
        Assert.Equal("max-duration", completed.Data.GetProperty("exitReason").GetString());
        Assert.Equal(report.PollCount, completed.Data.GetProperty("pollCount").GetInt32());
        Assert.Equal(report.TotalChangedPolls, completed.Data.GetProperty("totalChangedPolls").GetInt32());
    }

    [Fact]
    public async Task Parity_delay_timeout_matches_report_path()
    {
        // Always-changing values so only the deadline ends the loop:
        // poll at T0, delay to T30, poll at T30, delay to T60, then deadline.
        var reportClock = new FakeWatchClock();
        var reportOutcome = await Module(FakeSession.WithScalarPolls(1, 2, 3, 4, 5, 6, 7, 8), reportClock)
            .ExecuteAsync(Watch(
                untilUnchanged: 100,
                interval: TimeSpan.FromSeconds(30),
                maxDuration: TimeSpan.FromSeconds(60)));

        var report = Assert.IsType<SqlHarnessWatchReport>(reportOutcome.Report);
        Assert.Equal(SqlHarnessExitCode.WatchMaxDuration, reportOutcome.ExitCode);
        Assert.Equal(WatchExitReason.MaxDuration, report.ExitReason);
        Assert.Equal(2, report.PollCount);

        var ndjsonClock = new FakeWatchClock();
        var transport = new StringWriter();
        var ndjsonOutcome = await Module(FakeSession.WithScalarPolls(1, 2, 3, 4, 5, 6, 7, 8), ndjsonClock)
            .ExecuteWatchNdjsonAsync(
                Watch(
                    untilUnchanged: 100,
                    interval: TimeSpan.FromSeconds(30),
                    maxDuration: TimeSpan.FromSeconds(60)),
                transport);

        Assert.Equal(reportOutcome.ExitCode, ndjsonOutcome.ExitCode);
        Assert.Null(ndjsonOutcome.Report);
        Assert.Equal(reportClock.Delays, ndjsonClock.Delays);

        var records = Parse(transport);
        Assert.Equal(["started", "changed", "changed", "completed"], records.Select(r => r.Event));
        var completed = Assert.Single(records, r => r.Event == "completed");
        Assert.Equal("max-duration", completed.Data.GetProperty("exitReason").GetString());
        Assert.Equal(report.PollCount, completed.Data.GetProperty("pollCount").GetInt32());
    }

    [Fact]
    public async Task Parity_late_result_matches_report_path()
    {
        // The read overruns the deadline: the matching late result is dropped
        // and both paths keep the last complete in-budget state (none yet).
        var reportSession = FakeSession.WithScalarPolls(2);
        var reportClock = new FakeWatchClock();
        reportSession.BeforeResult = () => reportClock.Advance(TimeSpan.FromSeconds(60).Add(TimeSpan.FromMilliseconds(1)));
        var reportOutcome = await Module(reportSession, reportClock)
            .ExecuteAsync(Watch(until: "Value >= 2", maxDuration: TimeSpan.FromSeconds(60)));

        var report = Assert.IsType<SqlHarnessWatchReport>(reportOutcome.Report);
        Assert.Equal(SqlHarnessExitCode.WatchMaxDuration, reportOutcome.ExitCode);
        Assert.Equal(WatchExitReason.MaxDuration, report.ExitReason);
        Assert.Empty(report.EmittedPolls);

        var ndjsonSession = FakeSession.WithScalarPolls(2);
        var ndjsonClock = new FakeWatchClock();
        ndjsonSession.BeforeResult = () => ndjsonClock.Advance(TimeSpan.FromSeconds(60).Add(TimeSpan.FromMilliseconds(1)));
        var transport = new StringWriter();
        var ndjsonOutcome = await Module(ndjsonSession, ndjsonClock)
            .ExecuteWatchNdjsonAsync(Watch(until: "Value >= 2", maxDuration: TimeSpan.FromSeconds(60)), transport);

        Assert.Equal(reportOutcome.ExitCode, ndjsonOutcome.ExitCode);
        Assert.Null(ndjsonOutcome.Report);

        var records = Parse(transport);
        Assert.Equal(["started", "completed"], records.Select(r => r.Event));
        Assert.DoesNotContain(records, r => r.Event == "changed");
        var completed = Assert.Single(records, r => r.Event == "completed");
        Assert.Equal("max-duration", completed.Data.GetProperty("exitReason").GetString());
        Assert.Equal(report.PollCount, completed.Data.GetProperty("pollCount").GetInt32());
        Assert.Equal(0, completed.Data.GetProperty("totalChangedPolls").GetInt32());
    }

    [Fact]
    public async Task Parity_condition_met_exactly_at_deadline_matches_report_path()
    {
        // The read completes exactly at the deadline: still in budget on both paths.
        var reportSession = FakeSession.WithScalarPolls(2);
        var reportClock = new FakeWatchClock();
        reportSession.BeforeResult = () => reportClock.Advance(TimeSpan.FromSeconds(60));
        var reportOutcome = await Module(reportSession, reportClock)
            .ExecuteAsync(Watch(until: "Value >= 2", maxDuration: TimeSpan.FromSeconds(60)));

        var report = Assert.IsType<SqlHarnessWatchReport>(reportOutcome.Report);
        Assert.Equal(SqlHarnessExitCode.Success, reportOutcome.ExitCode);
        Assert.Equal(WatchExitReason.ConditionMet, report.ExitReason);

        var ndjsonSession = FakeSession.WithScalarPolls(2);
        var ndjsonClock = new FakeWatchClock();
        ndjsonSession.BeforeResult = () => ndjsonClock.Advance(TimeSpan.FromSeconds(60));
        var transport = new StringWriter();
        var ndjsonOutcome = await Module(ndjsonSession, ndjsonClock)
            .ExecuteWatchNdjsonAsync(Watch(until: "Value >= 2", maxDuration: TimeSpan.FromSeconds(60)), transport);

        Assert.Equal(reportOutcome.ExitCode, ndjsonOutcome.ExitCode);
        Assert.Null(ndjsonOutcome.Report);

        var records = Parse(transport);
        Assert.Equal(["started", "changed", "completed"], records.Select(r => r.Event));
        var completed = Assert.Single(records, r => r.Event == "completed");
        Assert.Equal("condition-met", completed.Data.GetProperty("exitReason").GetString());
        Assert.Equal(report.PollCount, completed.Data.GetProperty("pollCount").GetInt32());
    }

    [Fact]
    public async Task Parity_caller_cancellation_matches_report_path()
    {
        var reportClock = new FakeWatchClock { BlockDelay = true };
        using var reportCts = new CancellationTokenSource();
        reportClock.OnDelay = () => reportCts.Cancel();
        var reportOutcome = await Module(FakeSession.WithScalarPolls(1, 2, 3), reportClock).ExecuteAsync(
            Watch(untilUnchanged: 100, interval: TimeSpan.FromSeconds(30)),
            reportCts.Token);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, reportOutcome.ExitCode);

        var ndjsonClock = new FakeWatchClock { BlockDelay = true };
        using var ndjsonCts = new CancellationTokenSource();
        ndjsonClock.OnDelay = () => ndjsonCts.Cancel();
        var transport = new StringWriter();
        var ndjsonOutcome = await Module(FakeSession.WithScalarPolls(1, 2, 3), ndjsonClock).ExecuteWatchNdjsonAsync(
            Watch(untilUnchanged: 100, interval: TimeSpan.FromSeconds(30)),
            transport,
            ndjsonCts.Token);

        Assert.Equal(reportOutcome.ExitCode, ndjsonOutcome.ExitCode);
        Assert.Null(ndjsonOutcome.Report);

        var records = Parse(transport);
        Assert.Equal(["started", "changed", "failed"], records.Select(r => r.Event));
        var failed = Assert.Single(records, r => r.Event == "failed");
        Assert.Equal((int)SqlHarnessExitCode.SqlExecution, failed.Data.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Parity_sql_failure_matches_report_path()
    {
        var reportSession = FakeSession.WithScalarPolls(1);
        reportSession.ExecuteFailure = new TimeoutException($"timeout {Token}");
        var reportOutcome = await Module(reportSession, new FakeWatchClock())
            .ExecuteAsync(Watch(untilUnchanged: 1));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, reportOutcome.ExitCode);
        Assert.DoesNotContain(Token, reportOutcome.SafeError ?? string.Empty, StringComparison.Ordinal);

        var ndjsonSession = FakeSession.WithScalarPolls(1);
        ndjsonSession.ExecuteFailure = new TimeoutException($"timeout {Token}");
        var transport = new StringWriter();
        var ndjsonOutcome = await Module(ndjsonSession, new FakeWatchClock())
            .ExecuteWatchNdjsonAsync(Watch(untilUnchanged: 1), transport);

        Assert.Equal(reportOutcome.ExitCode, ndjsonOutcome.ExitCode);
        Assert.DoesNotContain(Token, ndjsonOutcome.SafeError ?? string.Empty, StringComparison.Ordinal);

        var records = Parse(transport);
        Assert.Equal(["started", "failed"], records.Select(r => r.Event));
        var failed = Assert.Single(records, r => r.Event == "failed");
        Assert.Equal((int)SqlHarnessExitCode.SqlExecution, failed.Data.GetProperty("exitCode").GetInt32());
        Assert.DoesNotContain(Token, failed.Data.GetProperty("error").GetProperty("message").GetString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parity_ndjson_retains_no_history_while_report_bounds_it()
    {
        var values = Enumerable.Range(1, 300).Cast<object>().ToArray();
        var reportOutcome = await Module(FakeSession.WithScalarPolls(values), new FakeWatchClock())
            .ExecuteAsync(Watch(until: "Value >= 300", interval: TimeSpan.FromMilliseconds(1)) with { HistoryLimit = 100 });

        var report = Assert.IsType<SqlHarnessWatchReport>(reportOutcome.Report);
        Assert.Equal(SqlHarnessExitCode.Success, reportOutcome.ExitCode);
        Assert.Equal(300, report.PollCount);
        Assert.Equal(300, report.TotalChangedPolls);
        Assert.Equal(200, report.OmittedPolls);
        Assert.Equal(100, report.EmittedPolls.Count);

        var transport = new StringWriter();
        var ndjsonOutcome = await Module(FakeSession.WithScalarPolls(values), new FakeWatchClock())
            .ExecuteWatchNdjsonAsync(
                Watch(until: "Value >= 300", interval: TimeSpan.FromMilliseconds(1)) with { HistoryLimit = 100 },
                transport);

        Assert.Equal(reportOutcome.ExitCode, ndjsonOutcome.ExitCode);
        // The NDJSON outcome retains nothing: the stream is the output.
        Assert.Null(ndjsonOutcome.Report);

        var records = Parse(transport);
        Assert.Equal(300, records.Count(r => r.Event == "changed"));
        var completed = Assert.Single(records, r => r.Event == "completed");
        Assert.Equal(report.PollCount, completed.Data.GetProperty("pollCount").GetInt32());
        Assert.Equal(report.TotalChangedPolls, completed.Data.GetProperty("totalChangedPolls").GetInt32());
        Assert.Equal(0, completed.Data.GetProperty("omittedPolls").GetInt32());
    }

    private static FakeSession BlockingSecondPollSession(out TaskCompletionSource entered)
    {
        var session = FakeSession.WithScalarPolls(1);
        var enteredSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        entered = enteredSource;
        var calls = 0;
        session.ExecuteHandler = async (command, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                return FakeScalarReader.Create(1);
            enteredSource.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable.");
        };
        return session;
    }

    private sealed record NdjsonRecord(string Event, long Sequence, int SchemaVersion, JsonElement Data);

    private static List<NdjsonRecord> Parse(StringWriter transport)
    {
        var records = new List<NdjsonRecord>();
        foreach (var line in transport.ToString()!.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            records.Add(new NdjsonRecord(
                root.GetProperty("event").GetString()!,
                root.GetProperty("sequence").GetInt64(),
                root.GetProperty("schemaVersion").GetInt32(),
                root.GetProperty("data").Clone()));
        }
        return records;
    }

    private static SqlHarnessModule Module(FakeSession session, FakeWatchClock clock, FakeGainStore? gain = null) =>
        new(
            new FakeSessionFactory(session, new FakeAzureCli(Token), null),
            gain ?? new FakeGainStore(),
            Profiles,
            clock);

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

    private static string TempFile(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class ThrowingPayload
    {
        public string Kaboom => throw new InvalidOperationException("serialization boom");
    }

    private sealed class CountingWriter : StringWriter
    {
        public int FlushCount { get; private set; }

        public override void Flush()
        {
            FlushCount++;
            base.Flush();
        }

        public List<string> Lines() =>
            ToString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

        private static string Property(string line, string name)
        {
            using var document = JsonDocument.Parse(line);
            var value = document.RootElement.GetProperty(name);
            return value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString()!;
        }

        public List<string> Events() => Lines().Select(line => Property(line, "event")).ToList();

        public List<long> Sequences() => Lines().Select(line => long.Parse(Property(line, "sequence"), CultureInfo.InvariantCulture)).ToList();

        public List<int> SchemaVersions() => Lines().Select(line => int.Parse(Property(line, "schemaVersion"), CultureInfo.InvariantCulture)).ToList();
    }

    private sealed class FlakyWriter(int budget) : StringWriter
    {
        private int _remaining = budget;

        public override void WriteLine(string? value)
        {
            if (_remaining <= 0)
                throw new IOException("transport gone");
            _remaining--;
            base.WriteLine(value);
        }
    }

    private sealed class SlowWriter : StringWriter
    {
        public override void Write(string? value)
        {
            Thread.Sleep(5);
            base.Write(value);
        }
    }

    private sealed class FakeGainStore : IGainStore
    {
        public List<GainRecord> Records { get; } = [];

        public void Append(GainRecord record) => Records.Add(record);

        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

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

    private sealed class StreamingModule : ISqlHarnessModule
    {
        public List<SqlHarnessWatchOperation> Operations { get; } = [];
        public OutputFootprint? CapturedFootprint { get; private set; }

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            throw new NotSupportedException("Streaming tests use the NDJSON entry point.");

        public Task<SqlHarnessOutcome> ExecuteWatchNdjsonAsync(
            SqlHarnessWatchOperation operation,
            TextWriter writer,
            CancellationToken ct = default)
        {
            Operations.Add(operation);
            writer.WriteLine("{\"schemaVersion\":1,\"event\":\"started\",\"sequence\":1,\"elapsedMilliseconds\":0,\"data\":null}");
            writer.WriteLine("{\"schemaVersion\":1,\"event\":\"completed\",\"sequence\":2,\"elapsedMilliseconds\":1,\"data\":null}");
            var receipt = new SqlHarnessEmissionReceipt((emitted, _) =>
            {
                CapturedFootprint = emitted;
                return Task.FromResult(SqlHarnessExitCode.Success);
            });
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null, receipt));
        }
    }

    private sealed class FixedModule(SqlHarnessOutcome outcome) : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            Task.FromResult(outcome);
    }
}
