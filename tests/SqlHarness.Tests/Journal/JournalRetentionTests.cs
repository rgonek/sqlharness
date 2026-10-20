using Microsoft.Data.Sqlite;

using SqlHarness.Core;
using SqlHarness.Core.Journal;

using OperationPhase = SqlHarness.Core.Journal.OperationPhase;

namespace SqlHarness.Tests.Journal;

public sealed class JournalRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Batch size the boundary tests cross. Production uses
    /// <see cref="JournalRetention.BatchSize"/> (500); a small value proves the same
    /// boundary behaviour with 30 seeded rows instead of 1010. It is passed per call,
    /// never set globally, because xUnit runs classes in parallel in one process.
    /// </summary>
    private const int TestBatchSize = 10;

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

    /// <summary>One seeded row; the batch tests vary the clock and sometimes the payload.</summary>
    private sealed record Row(
        DateTimeOffset At,
        int HostPid = 5151,
        bool Complete = true,
        string Key = "cli:a",
        BenchmarkJournalRecord? Benchmark = null,
        string? Payload = null);

    /// <summary>
    /// Seeds many rows through a single journal handle. Seeding opened the database
    /// once per row, and every open re-ran the migration check, the pragmas and a
    /// fsync; the batch-boundary tests need more than
    /// more rows than the batch size, which made them 90 seconds
    /// each. What is under test is <see cref="JournalRetention.Run"/>, not the
    /// seeding. The fixed clock is advanced per row, so the rows still land with
    /// the timestamps the assertions depend on.
    /// </summary>
    private static void SeedMany(JournalTempDirectory temp, int count, Func<int, Row> shape)
    {
        var clock = new FixedTimeProvider(shape(0).At);
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = true }, TextWriter.Null, clock);

        for (var i = 0; i < count; i++)
        {
            var row = shape(i);
            clock.Now = row.At;
            var session = JournalTestData.Session(row.Key) with { HostPid = row.HostPid };
            var handle = journal.Begin(session, JournalTestData.Start(row.Payload ?? "SELECT 1"))!;
            if (row.Complete)
                journal.Complete(handle, JournalTestData.End());
            if (row.Benchmark is not null)
                journal.RecordBenchmark(handle, row.Benchmark);
        }
    }

    private static BenchmarkJournalRecord Benchmark(string hash) => new(
        [new JournalVariantMetrics("measure", null, null, 1, null, null, null, null, null, null, null, null, null, 0, false, false, 0, [], null,
            [new JournalTableIo("T", 1, null, null, null, null, null, null, null, 0)], [new JournalPlanLink(1, 0, hash)])
            { PlanOccurrences = [new JournalPlanOccurrence(1, 0, 0, hash)] }],
        [new JournalPlanDocument(hash, hash, "showplan-xml", "<ShowPlanXML/>")]);

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
        Assert.Equal(1, Count(temp, "plan_occurrences"));
        Assert.Equal(hashKept, JournalDb.Rows(temp.DatabasePath, "SELECT sha256 FROM plan_documents").Single()["sha256"]);
        Assert.Equal("cli:new", JournalDb.Rows(temp.DatabasePath, "SELECT session_key FROM sessions").Single()["session_key"]);
    }

    [Fact]
    public void Retention_removes_links_with_operations_and_unreferenced_conversations()
    {
        using var temp = new JournalTempDirectory();
        Seed(temp, Now.AddDays(-40), key: "cli:old");
        Seed(temp, Now.AddDays(-1), key: "cli:new");
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO agent_conversations (id, agent_kind, conversation_id, title, updated_at) VALUES
                    (1, 'claude', 'old-conv', 'old', 't'), (2, 'claude', 'new-conv', 'new', 't'), (3, 'claude', 'staged', null, 't');
                INSERT INTO operation_agent_links (operation_id, conversation, model)
                SELECT id, CASE WHEN started_at < '2026-09-20' THEN 1 ELSE 2 END, 'm' FROM operations;
                INSERT INTO agent_tool_calls (agent_kind, call_id, conversation, seen_at) VALUES ('claude', 'toolu_x', 3, 't');
                """;
            command.ExecuteNonQuery();
        }

        JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(), new FixedTimeProvider(Now));

        Assert.Equal(new[] { "new-conv", "staged" },
            JournalDb.Rows(temp.DatabasePath, "SELECT conversation_id FROM agent_conversations ORDER BY conversation_id")
                .Select(row => (string)row["conversation_id"]!));
        Assert.Equal(1, Count(temp, "operation_agent_links"));
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
        // 40 rows of 64 KB is 2.6 MB against a 1 MB cap. The production fixture wrote
        // 25 MB to cross a 10 MB cap because the production step is 100 rows, so the
        // whole table fitted in one step and nothing was left to inspect. A small
        // step keeps the property under test visible: oldest first, stop under the
        // limit, rows still present afterwards.
        SeedMany(temp, 40, i => new Row(Now.AddMinutes(-40 + i), Payload: big + i));

        // Rows are "running" from a dead pid (5151 is not alive here), so they count as abandoned and deletable.
        var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 3650, maxSizeMb: 1), new Processes(), new FixedTimeProvider(Now),
            onBatchCommitted: null, sizeStep: 5);

        Assert.True(result.DeletedOperations > 0);
        Assert.True(result.DeletedOperations % 5 == 0, "rows go out in steps of the configured size");
        var remaining = JournalDb.Rows(temp.DatabasePath, "SELECT MIN(id) AS lo FROM operations")[0]["lo"];
        Assert.True((long)remaining! > 1, "the oldest operations go first");
        Assert.True((long)remaining! > result.DeletedOperations, "retention stops once under the limit");
        var used = JournalDb.Rows(temp.DatabasePath, "SELECT (page_count - freelist_count) * page_size AS used FROM pragma_page_count, pragma_freelist_count, pragma_page_size")[0]["used"];
        Assert.True((long)used! <= 1L * 1024 * 1024);
    }

    [Fact]
    public void Retention_deletes_in_short_transactions()
    {
        using var temp = new JournalTempDirectory();
        SeedMany(temp, TestBatchSize * 2 + 10, i => new Row(Now.AddDays(-40).AddSeconds(i)));
        var operationBatches = new List<int>();

        var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(), new FixedTimeProvider(Now),
            onBatchCommitted: null, onOperationBatchCommitted: operationBatches.Add, batchSize: TestBatchSize);

        Assert.Equal(TestBatchSize * 2 + 10, result.DeletedOperations);
        Assert.Equal(result.DeletedOperations, operationBatches.Sum());
        Assert.True(operationBatches.Count >= 3, $"expected at least 3 operation batches, saw {operationBatches.Count}");
        Assert.All(operationBatches, size => Assert.InRange(size, 1, TestBatchSize));
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
        SeedMany(temp, TestBatchSize + 5, i => new Row(Now.AddDays(-50).AddSeconds(i), HostPid: 100, Complete: false, Key: "cli:live"));
        Seed(temp, Now.AddDays(-40), key: "cli:old");

        var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(100), new FixedTimeProvider(Now));

        Assert.Equal(1, result.DeletedOperations);
        Assert.Equal(TestBatchSize + 5, Count(temp, "operations"));
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
        SeedMany(temp, TestBatchSize + 10, i => new Row(Now.AddDays(-40).AddSeconds(i)));
        var batches = 0;

        var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(), new FixedTimeProvider(Now),
            onBatchCommitted: () =>
            {
                if (++batches == 1)
                    throw new SqliteException("database is locked", 5);
            },
            batchSize: TestBatchSize);

        Assert.True(result.Ran);
        Assert.Equal(TestBatchSize, result.DeletedOperations);
        Assert.Equal(10, Count(temp, "operations"));
    }

    [Fact]
    public void Orphan_plans_are_deleted_in_short_transactions()
    {
        using var temp = new JournalTempDirectory();
        var plans = TestBatchSize + 20;
        SeedMany(temp, plans, i => new Row(
            Now.AddDays(-40).AddSeconds(i),
            Benchmark: Benchmark(i.ToString("X64", System.Globalization.CultureInfo.InvariantCulture))));
        var writes = 0;

        var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(), new FixedTimeProvider(Now),
            onBatchCommitted: () => writes++, batchSize: TestBatchSize);

        Assert.Equal((plans, plans, 1), (result.DeletedOperations, result.DeletedPlans, result.DeletedSessions));
        Assert.Equal(0, Count(temp, "plan_documents"));
        // One write transaction per batch, per table: operations and plans need the
        // same number of batches, sessions need one. Deleting orphans unbatched would
        // commit three times, once per table, whatever the row count.
        var batchesPerTable = (plans + TestBatchSize - 1) / TestBatchSize;
        Assert.Equal(batchesPerTable + batchesPerTable + 1, writes);
        Assert.True(writes > 3, "an unbatched orphan delete would commit only three times");
    }

    [Fact]
    public void Retention_cascades_phase_aggregates_and_events()
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null,
            new FixedTimeProvider(Now.AddDays(-40)));
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start())!;
        journal.RecordPhases(handle,
            [new PhaseAggregate(OperationPhase.Connect, 1, 2, 2)],
            [new PhaseEvent(1, 1, OperationPhase.Connect, 2, "succeeded", null, null)], 0);
        journal.Complete(handle, JournalTestData.End());

        var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(),
            new FixedTimeProvider(Now));

        Assert.Equal(1, result.DeletedOperations);
        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT * FROM operation_phases"));
        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT * FROM operation_events"));
    }

    [Fact]
    public void Retention_deletes_the_annotation_with_its_session()
    {
        using var temp = new JournalTempDirectory();
        Seed(temp, Now.AddDays(-40), key: "cli:old");
        Seed(temp, Now.AddDays(-1), key: "cli:new");
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO session_annotations (session_id, title, starred, updated_at)
                SELECT id, 'kept or not', 1, 't' FROM sessions;
                """;
            command.ExecuteNonQuery();
        }

        var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(), new FixedTimeProvider(Now));

        Assert.Equal(1, result.DeletedSessions);
        var remaining = JournalDb.Rows(temp.DatabasePath,
            "SELECT s.session_key FROM session_annotations a JOIN sessions s ON s.id = a.session_id").Single();
        Assert.Equal("cli:new", remaining["session_key"]);
    }

    [Fact]
    public void Normal_retention_expires_kept_and_unkept_operation_annotations()
    {
        using var temp = new JournalTempDirectory();
        Seed(temp, Now.AddDays(-40), key: "cli:old-kept");
        Seed(temp, Now.AddDays(-40), key: "cli:old-ordinary");
        Seed(temp, Now.AddDays(-1), key: "cli:new-kept");
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO operation_annotations (operation_id, agent_label_initial, agent_label, agent_keep, updated_at)
                SELECT operations.id, NULL, session_key, CASE WHEN session_key LIKE '%kept' THEN 1 ELSE 0 END, 't'
                FROM operations JOIN sessions ON sessions.id = operations.session_id;
                """;
            command.ExecuteNonQuery();
        }

        JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(), new FixedTimeProvider(Now));

        var remaining = JournalDb.Rows(temp.DatabasePath, "SELECT agent_keep FROM operation_annotations");
        Assert.Single(remaining);
        Assert.Equal(1L, remaining[0]["agent_keep"]);
    }
}