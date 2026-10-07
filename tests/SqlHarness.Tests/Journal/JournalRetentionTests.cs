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
        var operationBatches = new List<int>();

        var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(), new FixedTimeProvider(Now),
            onBatchCommitted: null, onOperationBatchCommitted: operationBatches.Add);

        Assert.Equal(JournalRetention.BatchSize * 2 + 10, result.DeletedOperations);
        Assert.Equal(result.DeletedOperations, operationBatches.Sum());
        Assert.True(operationBatches.Count >= 3, $"expected at least 3 operation batches, saw {operationBatches.Count}");
        Assert.All(operationBatches, size => Assert.InRange(size, 1, JournalRetention.BatchSize));
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

    [Fact]
    public void Live_running_rows_at_the_head_do_not_block_older_deletable_rows()
    {
        using var temp = new JournalTempDirectory();
        for (var i = 0; i < JournalRetention.BatchSize + 5; i++)
            Seed(temp, Now.AddDays(-50).AddSeconds(i), hostPid: 100, complete: false, key: "cli:live");
        Seed(temp, Now.AddDays(-40), key: "cli:old");

        var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(100), new FixedTimeProvider(Now));

        Assert.Equal(1, result.DeletedOperations);
        Assert.Equal(JournalRetention.BatchSize + 5, Count(temp, "operations"));
        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT 1 FROM operations WHERE status <> 'running'"));
    }

    private sealed class FailingProcesses : IProcessInfo
    {
        public int CurrentPid => Environment.ProcessId;
        public ProcessSnapshot? Get(int pid) => throw new InvalidOperationException("process table unreadable");
    }

    [Fact]
    public void A_failed_process_lookup_keeps_the_running_operation()
    {
        using var temp = new JournalTempDirectory();
        Seed(temp, Now.AddDays(-40), hostPid: 100, complete: false, key: "cli:unknown");
        Seed(temp, Now.AddDays(-40), key: "cli:done");

        var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new FailingProcesses(), new FixedTimeProvider(Now));

        Assert.Equal(1, result.DeletedOperations);
        Assert.Equal("running", JournalDb.Rows(temp.DatabasePath, "SELECT status FROM operations").Single()["status"]);
    }

    [Fact]
    public void A_run_failing_partway_reports_what_it_already_deleted()
    {
        using var temp = new JournalTempDirectory();
        for (var i = 0; i < JournalRetention.BatchSize + 10; i++)
            Seed(temp, Now.AddDays(-40).AddSeconds(i));
        var batches = 0;

        var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(), new FixedTimeProvider(Now),
            onBatchCommitted: () =>
            {
                if (++batches == 1)
                    throw new SqliteException("database is locked", 5);
            });

        Assert.True(result.Ran);
        Assert.Equal(JournalRetention.BatchSize, result.DeletedOperations);
        Assert.Equal(10, Count(temp, "operations"));
    }

    [Fact]
    public void Orphan_plans_are_deleted_in_short_transactions()
    {
        using var temp = new JournalTempDirectory();
        var plans = JournalRetention.BatchSize + 20;
        for (var i = 0; i < plans; i++)
            Seed(temp, Now.AddDays(-40).AddSeconds(i), benchmark: Benchmark(i.ToString("X64", System.Globalization.CultureInfo.InvariantCulture)));
        var writes = 0;

        var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(), new FixedTimeProvider(Now),
            onBatchCommitted: () => writes++);

        Assert.Equal((plans, plans, 1), (result.DeletedOperations, result.DeletedPlans, result.DeletedSessions));
        Assert.Equal(0, Count(temp, "plans"));
        // Two operation batches, two plan batches, one session batch; an unbatched orphan delete would give at most four.
        Assert.Equal(5, writes);
    }
}