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

    private sealed class ThrowingJournal : IActivityJournal
    {
        public int ProgressCalls;
        public JournalHandle? Begin(SessionIdentity session, OperationStart start) => new(1);
        public bool Complete(JournalHandle? handle, OperationEnd end) => true;
        public void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted) { }
        public bool RecordWatchProgress(JournalHandle? handle, WatchProgress progress)
        {
            ProgressCalls++;
            throw new InvalidOperationException("journal");
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
    public async Task Progress_recording_stops_after_the_first_thrown_write()
    {
        var journal = new ThrowingJournal();

        await new JournalingModule(new ProgressModule(polls: 5), () => journal, () => JournalTestData.Session()).ExecuteAsync(Watch());

        Assert.Equal(1, journal.ProgressCalls);
    }

    [Fact]
    public void Progress_is_not_written_after_the_operation_completed()
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null, TimeProvider.System);
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        const string Expected = """{"polls":2,"changedPolls":1,"elapsedMs":20}""";

        Assert.True(journal.RecordWatchProgress(handle, new WatchProgress(2, 1, 20)));
        Assert.Equal(Expected, JournalDb.Rows(temp.DatabasePath, "SELECT progress_json FROM operations").Single()["progress_json"]);

        Assert.True(journal.Complete(handle, JournalTestData.End()));
        journal.RecordWatchProgress(handle, new WatchProgress(9, 7, 90));

        Assert.Equal(Expected, JournalDb.Rows(temp.DatabasePath, "SELECT progress_json FROM operations").Single()["progress_json"]);
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