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