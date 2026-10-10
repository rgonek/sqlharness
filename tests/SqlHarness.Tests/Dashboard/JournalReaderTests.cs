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
    public void Dead_hosts_are_looked_up_once_while_live_hosts_are_rechecked()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:live", hostPid: 100), complete: false);
        seed.Operation(JournalSeed.Session("cli:dead", hostPid: 200), complete: false);
        var processes = new FakeProcesses().Alive(100, JournalSeed.HostStarted);
        var reader = Reader(home, processes);

        for (var tick = 0; tick < 3; tick++)
        {
            using var connection = reader.OpenReadOnly()!;
            var statuses = reader.RunningOperations(connection).ToDictionary(o => o.SessionId, o => o.Status);
            Assert.Equal(["abandoned", "running"], statuses.Values.Order());
        }

        Assert.Equal("abandoned", reader.Operations(new OperationQuery(Status: "abandoned")).Items.Single().Status);
        Assert.Equal(1, processes.Lookups(200));
        Assert.Equal(4, processes.Lookups(100));
    }

    [Fact]
    public void Dead_process_cache_starts_over_when_full()
    {
        var dead = new DeadProcesses();
        for (var pid = 0; pid < DeadProcesses.Capacity; pid++)
            dead.Add(pid, null);
        Assert.Equal(DeadProcesses.Capacity, dead.Count);

        dead.Add(-1, "x");

        Assert.Equal(1, dead.Count);
        Assert.True(dead.Contains(-1, "x"));
        Assert.False(dead.Contains(0, null));
    }

    [ProcessReaderFact]
    public void Real_exited_host_is_abandoned_while_the_current_process_stays_running()
    {
        using var home = new TempHome();
        // A child that waits on stdin, so its snapshot is taken while it is certainly alive.
        var start = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "cat")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        ProcessSnapshot child;
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            child = ProcessInfo.Current.Get(process.Id)!;
            Assert.NotNull(child);
            process.StandardInput.Close();
            Assert.True(process.WaitForExit(10_000));
        }

        var self = ProcessInfo.Current.Get(Environment.ProcessId)!;
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(Host("cli:exited", child), complete: false);
        seed.Operation(Host("cli:self", self), complete: false);
        var reader = new JournalReader(home.DatabasePath, ProcessInfo.Current);

        var statuses = reader.Operations(new OperationQuery()).Items.ToDictionary(o => o.SessionId, o => o.Status);
        var sessions = reader.Sessions(new SessionQuery()).Items.ToDictionary(s => s.SessionKey, s => s.Id);

        Assert.Equal("abandoned", statuses[sessions["cli:exited"]]);
        Assert.Equal("running", statuses[sessions["cli:self"]]);

        static SessionIdentity Host(string key, ProcessSnapshot host) =>
            JournalSeed.Session(key, hostPid: host.Pid) with { HostStartedAt = host.StartedAt };
    }

    [Fact]
    public void Live_filtered_pages_scan_past_dead_running_rows()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:live", hostPid: 100), complete: false);
        for (var i = 0; i < 3; i++)
            seed.Operation(JournalSeed.Session("cli:dead", hostPid: 200), complete: false);
        var reader = Reader(home, new FakeProcesses().Alive(100, JournalSeed.HostStarted));

        var running = reader.Operations(new OperationQuery(Status: "running", Limit: 2));
        var abandoned = reader.Operations(new OperationQuery(Status: "abandoned", Limit: 2));
        var rest = reader.Operations(new OperationQuery(Status: "abandoned", Limit: 2, Cursor: abandoned.NextCursor));

        Assert.Equal("running", Assert.Single(running.Items).Status);
        Assert.Null(running.NextCursor);
        Assert.Equal(2, abandoned.Items.Count);
        Assert.NotNull(abandoned.NextCursor);
        Assert.Equal("abandoned", Assert.Single(rest.Items).Status);
        Assert.Null(rest.NextCursor);
    }

    [Fact]
    public void Session_detail_lists_operations_with_running_and_abandoned_counts()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:x", hostPid: 100), complete: false);
        seed.Operation(JournalSeed.Session("cli:x", hostPid: 200), complete: false);
        seed.Operation(JournalSeed.Session("cli:x", hostPid: 100));
        var reader = Reader(home, new FakeProcesses().Alive(100, JournalSeed.HostStarted));
        var summary = Assert.Single(reader.Sessions(new SessionQuery()).Items);

        var detail = reader.Session(summary.Id)!;

        Assert.Equal((1, 1), (summary.Running, summary.Abandoned));
        Assert.Equal((1, 1), (detail.Session.Running, detail.Session.Abandoned));
        Assert.Equal(3, detail.Session.Operations);
        Assert.Equal(["succeeded", "abandoned", "running"], detail.Operations.Select(o => o.Status));
        Assert.Null(reader.Session(summary.Id + 1));
    }

    [Fact]
    public void Malformed_stored_json_reads_as_null_fields()
    {
        using var home = new TempHome();
        var handle = new JournalSeed(home.DatabasePath).Operation(
            JournalSeed.Session("cli:j"), operation: "measure", benchmark: JournalSeed.Benchmark());
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE operations SET vars_json = '{', summary_json = 'nope', progress_json = '[';
                UPDATE operation_metrics SET waits_json = '[{"waitType":';
                """;
            command.ExecuteNonQuery();
        }

        var reader = Reader(home);
        var detail = reader.Operation(handle.OperationId)!;
        var stats = reader.Stats(new StatsQuery());

        Assert.Null(detail.Vars);
        Assert.Null(detail.Summary);
        Assert.Null(detail.Operation.Progress);
        Assert.Null(Assert.Single(detail.Variants).Waits);
        Assert.Empty(stats.TopWaits);
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
    public void Stats_token_totals_count_only_operations_with_both_token_counts()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var s = JournalSeed.Session("cli:a");
        seed.Operation(s);
        seed.Operation(s, tokens: SeedTokens.RawOnly);
        seed.Operation(s, tokens: SeedTokens.EmittedOnly);
        seed.Operation(s, tokens: SeedTokens.EmittedOnly);

        var stats = Reader(home).Stats(new StatsQuery());

        Assert.Equal((100L, 10L), (stats.Tokens.Raw, stats.Tokens.Emitted));
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
    public void Dimension_stats_are_complete_filtered_and_keep_missing_values_separate_from_unknown_text()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        for (var i = 0; i < 25; i++)
            seed.Operation(JournalSeed.Session($"cli:profile-{i}"), profile: $"profile-{i:D2}");
        seed.Operation(JournalSeed.Session("cli:literal"), profile: "app", variables: new Dictionary<string, string>
        {
            ["region"] = "Unknown",
            ["component"] = "worker",
        }, durationMs: 30);
        seed.Operation(JournalSeed.Session("cli:missing"), profile: "app", status: "failed", exitCode: 5,
            variables: new Dictionary<string, string> { ["component"] = "worker" }, durationMs: 20);
        seed.Operation(JournalSeed.Session("cli:running", hostPid: 100), profile: "app", complete: false,
            variables: new Dictionary<string, string> { ["component"] = "worker" });
        seed.Operation(JournalSeed.Session("cli:eu-api-1"), profile: "app",
            variables: new Dictionary<string, string> { ["region"] = "eu", ["component"] = "api" }, durationMs: 5);
        seed.Operation(JournalSeed.Session("cli:eu-api-2"), profile: "app",
            variables: new Dictionary<string, string> { ["region"] = "eu", ["component"] = "api" }, durationMs: 7);
        seed.Operation(JournalSeed.Session("cli:us-portal"), profile: "app", status: "rejected", exitCode: 2,
            variables: new Dictionary<string, string> { ["region"] = "us", ["component"] = "portal" }, durationMs: 11);
        seed.Operation(JournalSeed.Session("cli:deleted-profile"), profile: "deleted",
            variables: new Dictionary<string, string> { ["historical"] = "kept" });
        var definitions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["app"] = ["region", "component"],
            ["empty-profile"] = ["scope"],
        };

        var reader = Reader(home, new FakeProcesses());
        var all = reader.Stats(new StatsQuery(Profile: "app", RowDimension: "region", ColumnDimension: "component"), definitions);
        var filtered = reader.Stats(new StatsQuery(Profile: "app",
            Dimensions: new Dictionary<string, string?> { ["region"] = null },
            RowDimension: "region", ColumnDimension: "component"), definitions);
        var empty = reader.Stats(new StatsQuery(Profile: "empty-profile",
            To: new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero)), definitions);
        var deletedProfile = reader.Stats(new StatsQuery(Profile: "deleted"), definitions);

        Assert.Equal(27, all.ProfileOperations.Count);
        Assert.Equal(32, all.ProfileOperations.Sum(item => item.Operations));
        Assert.Equal("app", all.ProfileOperations[0].Profile);
        Assert.Equal("deleted", all.ProfileOperations[1].Profile);
        Assert.True(all.ProfileDimensions.ProfileDefinitionAvailable);
        Assert.Equal(6, all.ProfileDimensions.Operations);
        var region = Assert.Single(all.ProfileDimensions.Dimensions, item => item.Name == "region");
        Assert.Equal(4, region.Values.Count);
        var literal = Assert.Single(region.Values, item => !item.IsUnknown && item.Value == "Unknown");
        var missing = Assert.Single(region.Values, item => item.IsUnknown);
        Assert.Equal(("Unknown", 1), (literal.Value, literal.Operations));
        Assert.NotNull(literal.Percentage);
        Assert.Equal(100d / 6, literal.Percentage.Value, 3);
        Assert.Equal(("Unknown", 2, 20L, 1, 1, 1),
            (missing.Value, missing.Operations, missing.TotalDurationMs, missing.DurationAvailableOperations,
                missing.DurationUnavailableOperations, missing.Failed));
        var matrix = Assert.IsType<DimensionMatrixStats>(all.ProfileDimensions.Matrix);
        Assert.Equal(("region", "component", 6), (matrix.RowDimension, matrix.ColumnDimension, matrix.Totals.Operations));
        Assert.Equal(6, matrix.Cells.Sum(cell => cell.Metrics.Operations));
        Assert.Equal((1, 1), (matrix.Totals.Failed, matrix.Totals.Rejected));
        Assert.Equal(2, Assert.Single(matrix.Cells, cell => !cell.Row.IsUnknown && cell.Row.Value == "eu"
            && !cell.Column.IsUnknown && cell.Column.Value == "api").Metrics.Operations);
        var literalUnknown = Assert.Single(matrix.Cells, cell => !cell.Row.IsUnknown && cell.Row.Value == "Unknown"
            && cell.Column.Value == "worker");
        var missingRegion = Assert.Single(matrix.Cells, cell => cell.Row.IsUnknown && cell.Column.Value == "worker");
        Assert.Equal((1, 30L, 0), (literalUnknown.Metrics.Operations, literalUnknown.Metrics.TotalDurationMs,
            literalUnknown.Metrics.DurationUnavailableOperations));
        Assert.Equal((2, 20L, 1, 1), (missingRegion.Metrics.Operations, missingRegion.Metrics.TotalDurationMs,
            missingRegion.Metrics.DurationUnavailableOperations, missingRegion.Metrics.Failed));
        Assert.Equal(2, filtered.ProfileDimensions.Operations);
        Assert.Equal(2, filtered.ProfileDimensions.Matrix!.Totals.Operations);
        Assert.Equal(2, Assert.Single(filtered.ProfileDimensions.Matrix.Cells).Metrics.Operations);
        Assert.Equal(all.ProfileOperations, filtered.ProfileOperations);
        Assert.Equal(2, Assert.Single(filtered.ProfileDimensions.Dimensions, item => item.Name == "region").Values.Single().Operations);
        Assert.Equal(0, empty.ProfileDimensions.Operations);
        Assert.True(empty.ProfileDimensions.ProfileDefinitionAvailable);
        Assert.Empty(Assert.Single(empty.ProfileDimensions.Dimensions).Values);
        Assert.False(deletedProfile.ProfileDimensions.ProfileDefinitionAvailable);
        Assert.Equal("kept", Assert.Single(Assert.Single(deletedProfile.ProfileDimensions.Dimensions).Values).Value);
    }

    [Fact]
    public void Dimension_target_limit_reports_full_filtered_target_count_without_changing_totals()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        for (var i = 0; i < 23; i++)
            seed.Operation(JournalSeed.Session($"cli:selected-{i}"), profile: "app",
                variables: new Dictionary<string, string> { ["tenant"] = "selected", ["component"] = "api" },
                server: $"server-{i}", database: $"db-{i}");
        for (var i = 0; i < 3; i++)
            seed.Operation(JournalSeed.Session($"cli:other-{i}"), profile: "app",
                variables: new Dictionary<string, string> { ["tenant"] = "other", ["component"] = "api" },
                server: $"other-server-{i}", database: $"other-db-{i}");

        var stats = Reader(home).Stats(new StatsQuery(Profile: "app",
            Dimensions: new Dictionary<string, string?> { ["tenant"] = "selected" },
            RowDimension: "tenant", ColumnDimension: "component"),
            new Dictionary<string, IReadOnlyList<string>> { ["app"] = ["tenant", "component"] });

        Assert.Equal(23, stats.ProfileDimensions.Operations);
        Assert.Equal(23, stats.ProfileDimensions.TargetCount);
        Assert.Equal(20, stats.ProfileDimensions.Targets.Count);
        Assert.Equal(23, stats.ProfileDimensions.Matrix!.Totals.Operations);
        Assert.Equal(20, stats.ProfileDimensions.Targets.Sum(target => target.Count));
        Assert.Equal(20, stats.ProfileDimensions.Targets.Select(target => (target.Engine, target.Server, target.Database)).Distinct().Count());
    }

    [Fact]
    public void Operation_pages_filter_by_profile_and_multiple_dimensions_without_losing_cursor_matches()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:filters");
        for (var i = 0; i < 5; i++)
        {
            seed.Operation(session, profile: i % 2 == 0 ? "app" : "other", variables: new Dictionary<string, string>
            {
                ["region"] = i % 2 == 0 ? "eu:west" : "us",
                ["component"] = i < 3 ? "api" : "portal",
                ["tenant"] = "acme",
            });
        }

        var reader = Reader(home);
        var first = reader.Operations(new OperationQuery(Profile: "app", Dimensions: new Dictionary<string, string?>
        {
            ["region"] = "eu:west",
            ["component"] = "api",
            ["tenant"] = "acme",
        }, Limit: 1));
        var rest = reader.Operations(new OperationQuery(Profile: "app", Dimensions: new Dictionary<string, string?>
        {
            ["region"] = "eu:west",
            ["component"] = "api",
            ["tenant"] = "acme",
        }, Cursor: first.NextCursor, Limit: 1));

        Assert.NotNull(first.NextCursor);
        Assert.Null(rest.NextCursor);
        Assert.Equal(2, first.Items.Count + rest.Items.Count);
        Assert.All(first.Items.Concat(rest.Items), operation => Assert.Equal("app", operation.Profile));
    }

    [Fact]
    public void Unprofiled_scope_and_minute_bounds_match_statistics_and_drilldown()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:unprofiled");
        seed.Operation(session, profile: null, variables: new Dictionary<string, string> { ["region"] = "Unknown", ["component"] = "api" },
            server: "unprofiled-server", database: "unprofiled-db", engine: "postgres");
        seed.Operation(session, profile: "app", variables: new Dictionary<string, string> { ["region"] = "west", ["component"] = "api" });
        var last = seed.Operation(session, profile: null,
            variables: new Dictionary<string, string> { ["region"] = "Unknown", ["component"] = "portal" },
            server: "unprofiled-server", database: "unprofiled-db", engine: "postgres");

        var from = new DateTimeOffset(2026, 10, 7, 9, 0, 2, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 10, 7, 9, 0, 5, TimeSpan.Zero);
        var dimensions = new Dictionary<string, string?> { ["region"] = "Unknown" };
        var stats = Reader(home).Stats(new StatsQuery(from, to, Dimensions: dimensions, RowDimension: "region", ColumnDimension: "component",
            UnprofiledOnly: true));
        var page = Reader(home).Operations(new OperationQuery(From: from, To: to, Dimensions: dimensions, UnprofiledOnly: true));

        Assert.Equal(1, stats.ProfileDimensions.Operations);
        Assert.False(stats.ProfileDimensions.ProfileDefinitionAvailable);
        Assert.Equal(("postgres", "unprofiled-server", "unprofiled-db", 1),
            (Assert.Single(stats.ProfileDimensions.Targets).Engine, stats.ProfileDimensions.Targets[0].Server,
                stats.ProfileDimensions.Targets[0].Database, stats.ProfileDimensions.Targets[0].Count));
        Assert.Equal(last.OperationId, Assert.Single(page.Items).Id);
        Assert.Equal(1, stats.ProfileDimensions.Matrix!.Totals.Operations);
    }

    [Fact]
    public void Token_coverage_counts_raw_only_emitted_only_missing_and_paired_rows()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:coverage");
        seed.Operation(session, tokens: SeedTokens.RawOnly);
        seed.Operation(session, tokens: SeedTokens.EmittedOnly);
        seed.Operation(session, tokens: SeedTokens.None);
        seed.Operation(session, tokens: SeedTokens.Both);

        var tokens = Reader(home).Stats(new StatsQuery()).Tokens;

        Assert.Equal((4, 1, 1, 1, 1), (tokens.TotalOperations, tokens.PairedOperations, tokens.RawOnlyOperations,
            tokens.EmittedOnlyOperations, tokens.MissingBothOperations));
        Assert.Equal((100L, 10L), (tokens.Raw, tokens.Emitted));
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

    [Fact]
    public void Operation_carries_the_stored_error_message()
    {
        using var home = new TempHome();
        var journal = ActivityJournal.Open(home.DatabasePath, new JournalConfig { StoreSensitive = true }, TextWriter.Null, TimeProvider.System);
        var handle = journal.Begin(Journal.JournalTestData.Session(), Journal.JournalTestData.Start());
        journal.Complete(handle, Journal.JournalTestData.End("failed", 5) with { ErrorKind = "sql_execution_failed", ErrorMessage = "Invalid column name 'x'." });
        var reader = new JournalReader(home.DatabasePath, new FakeProcesses());

        var operation = reader.Operation(handle!.OperationId)!.Operation;

        Assert.Equal("sql_execution_failed", operation.ErrorKind);
        Assert.Equal("Invalid column name 'x'.", operation.ErrorMessage);
    }
}