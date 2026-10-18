using SqlHarness.Core;
using SqlHarness.Core.Dialect;

namespace SqlHarness.Tests.Journal;

public sealed class JournalBenchmarkBuilderTests
{
    private static CompareRunArtifact Run(
        string variant, int repetition, long cpu, long elapsed, long reads,
        string? set = null, int? cell = null, IReadOnlyList<string>? plans = null,
        IReadOnlyList<TableIoCounters>? io = null, BenchmarkRunMetrics? metrics = null) =>
        new(variant, repetition, cpu, elapsed, reads,
            new Dictionary<string, long> { ["Orders"] = reads }, "hash", plans ?? [], 1, set, metrics)
        {
            TableIo = io ?? [new TableIoCounters("Orders", 1, reads, 0, 0, 0, 0, 0, 0)],
            MatrixCell = cell,
        };

    [Fact]
    public void Runs_are_grouped_by_variant_set_and_cell_in_first_appearance_order()
    {
        var record = JournalBenchmarkBuilder.Build(
        [
            Run("baseline", 1, 10, 12, 5, cell: 0), Run("candidate", 1, 8, 9, 3, cell: 0),
            Run("candidate", 2, 9, 10, 4, cell: 0), Run("baseline", 2, 11, 13, 6, cell: 0),
            Run("baseline", 1, 20, 22, 7, cell: 1),
        ]);

        Assert.Equal(
            [("baseline", (int?)0, 2), ("candidate", 0, 2), ("baseline", 1, 1)],
            record.Variants.Select(v => (v.Variant, v.MatrixCell, v.Runs)));
        Assert.Equal(new CompareDistribution(10, 10, 11), record.Variants[0].CpuMilliseconds);
        Assert.Equal(new CompareDistribution(5, 5, 6), record.Variants[0].LogicalReads);
    }

    [Fact]
    public void Table_io_is_median_per_counter_with_cold_run_count()
    {
        var record = JournalBenchmarkBuilder.Build(
        [
            Run("measure", 1, 1, 1, 10, io: [new TableIoCounters("Orders", 1, 10, 4, 0, 8, 0, 0, 0)]),
            Run("measure", 2, 1, 1, 12, io: [new TableIoCounters("Orders", 1, 12, 0, 0, 0, 0, 0, 0)]),
            Run("measure", 3, 1, 1, 14, io: [new TableIoCounters("Orders", 3, 14, 0, 0, 0, 0, 0, 0), new TableIoCounters("Worktable", 0, 2, 0, 0, 0, 0, 0, 0)]),
        ]);

        var orders = record.Variants[0].TableIo.Single(t => t.Table == "Orders");
        Assert.Equal(12, orders.LogicalReads);
        Assert.Equal(1, orders.ScanCount);
        Assert.Equal(1, orders.ColdRuns);
        var worktable = record.Variants[0].TableIo.Single(t => t.Table == "Worktable");
        Assert.Equal(0, worktable.LogicalReads);
        Assert.Equal(["Orders", "Worktable"], record.Variants[0].TableIo.Select(t => t.Table));
    }

    [Fact]
    public void Table_variables_group_before_medians()
    {
        var record = JournalBenchmarkBuilder.Build(
        [
            Run("measure", 1, 1, 1, 10, io: [new TableIoCounters("#A1B2C3D4", 1, 10, 0, 0, 0, 0, 0, 0)]),
            Run("measure", 2, 1, 1, 10, io: [new TableIoCounters("[dbo].[#B2C3D4E5]", 1, 10, 0, 0, 0, 0, 0, 0)]),
            Run("measure", 3, 1, 1, 10, io: [new TableIoCounters("[#C3D4E5F6]", 1, 10, 0, 0, 0, 0, 0, 0)]),
        ]);

        var table = Assert.Single(Assert.Single(record.Variants).TableIo);
        Assert.Equal("table variables", table.Table);
        Assert.Equal(10, table.LogicalReads);
    }

    [Fact]
    public void Headline_equals_per_table_definition()
    {
        var record = JournalBenchmarkBuilder.Build(
        [
            Run("measure", 1, 1, 1, 999, io: [
                new TableIoCounters("A", 1, 10, 0, 0, 0, 2, 0, 0),
                new TableIoCounters("B", 1, 20, 0, 0, 0, 1, 0, 0)]),
            Run("measure", 2, 1, 1, 999, io: [
                new TableIoCounters("A", 1, 20, 0, 0, 0, 1, 0, 0),
                new TableIoCounters("B", 1, 40, 0, 0, 0, 2, 0, 0)]),
            Run("measure", 3, 1, 1, 999, io: [
                new TableIoCounters("A", 1, 30, 0, 0, 0, 2, 0, 0),
                new TableIoCounters("B", 1, 60, 0, 0, 0, 3, 0, 0)]),
        ]);

        var variant = Assert.Single(record.Variants);
        Assert.Equal(63, variant.LogicalReads!.Median);
        Assert.Equal(2, variant.TableIo.Single(t => t.Table == "A").LobLogicalReads);
        Assert.Equal(21, variant.TableIo.Single(t => t.Table == "A").LogicalReads);
    }

    [Fact]
    public void Case_metrics_keep_the_canonical_display_label()
    {
        var conditions = new ConditionsModel(1, "sqlserver", "srv", "db",
            new ConditionsExecution("compare", "new-session-per-case", "bound-per-case", 0, 1, 30, null, null),
            null, [], [new ConditionsCase("class-a", 0, "@Class = N'X'", null, [], "fingerprint")], "fingerprint", []);

        var record = JournalBenchmarkBuilder.Build([Run("baseline", 1, 1, 1, 10, cell: 0)], conditions);

        Assert.Equal("class-a", Assert.Single(record.Variants).CaseId);
        Assert.Equal("@Class = N'X'", record.Variants[0].CaseLabel);
    }

    [Fact]
    public void Runs_without_detail_fall_back_to_logical_reads_by_table()
    {
        var record = JournalBenchmarkBuilder.Build([Run("measure", 1, 0, 5, 9, io: [])]);

        var table = Assert.Single(record.Variants[0].TableIo);
        Assert.Equal(("Orders", 9L), (table.Table, table.LogicalReads));
        Assert.Null(table.ScanCount);
        Assert.Null(table.PhysicalReads);
    }

    [Fact]
    public void Unavailable_metrics_are_null_and_table_io_is_skipped()
    {
        var unavailable = new BenchmarkRunMetrics(
            BenchmarkMetricReport.Unavailable, BenchmarkMetricReport.Unavailable, null, false, null, null,
            BenchmarkMetricReport.Unavailable, null, null, null, BenchmarkMetricReport.ResultStatement,
            BenchmarkMetricText.StatementRows, []);

        var record = JournalBenchmarkBuilder.Build([Run("measure", 1, 0, 0, 0, metrics: unavailable, io: [])]);

        var variant = record.Variants[0];
        Assert.Null(variant.CpuMilliseconds);
        Assert.Null(variant.ElapsedMilliseconds);
        Assert.Null(variant.LogicalReads);
        Assert.Empty(variant.TableIo);
    }

    [Fact]
    public async Task Table_io_from_more_than_one_thousand_messages_is_journaled()
    {
        var session = new TableIoSession();
        var run = await new SqlServerDialect().ExecuteBenchmarkRunAsync(
            session, "SELECT Value", [], 30, 1, "measure", null,
            captureComparison: false, comparisonMaximumRows: 0, CancellationToken.None);

        var journal = JournalBenchmarkBuilder.Build([run.Artifact]);

        var table = Assert.Single(Assert.Single(journal.Variants).TableIo);
        Assert.Equal("T", table.Table);
        Assert.Equal(2400, table.LogicalReads);
        Assert.Equal(1200, table.ScanCount);
    }

    [Fact]
    public async Task Incomplete_numbered_io_is_not_journaled_as_measured_when_time_is_valid()
    {
        var session = new IncompleteIoSession();
        var run = await new SqlServerDialect().ExecuteBenchmarkRunAsync(
            session, "SELECT Value", [], 30, 1, "measure", null,
            captureComparison: false, comparisonMaximumRows: 0, CancellationToken.None);

        Assert.Equal(5, run.Artifact.LogicalReads);
        Assert.False(run.Artifact.TableIoComplete);
        var metrics = Assert.IsType<BenchmarkRunMetrics>(run.Artifact.Metrics);
        Assert.Equal(BenchmarkMetricReport.Unavailable, metrics.LogicalReadsAvailability);
        Assert.Equal(BenchmarkMetricReport.Measured, metrics.CpuTimeAvailability);
        Assert.Equal(BenchmarkMetricReport.Measured, metrics.ElapsedTimeAvailability);

        var variant = Assert.Single(JournalBenchmarkBuilder.Build([run.Artifact]).Variants);
        Assert.Null(variant.LogicalReads);
        Assert.Empty(variant.TableIo);
        Assert.Equal(new CompareDistribution(12, 12, 12), variant.CpuMilliseconds);
        Assert.Equal(new CompareDistribution(20, 20, 20), variant.ElapsedMilliseconds);
    }

    private sealed class IncompleteIoSession : ISqlSession
    {
        private readonly SessionMessageBuffer _messages = new();

        public IReadOnlyList<SessionMessage> Messages => _messages.Snapshot();
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("server", "database", "server", "database", "profile");

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            if (!command.Sql.StartsWith("SET", StringComparison.Ordinal))
            {
                _messages.Add(3615, "Table 'Partial'. Scan count 1, logical reads 5, physical reads 0, lob logical reads 0.");
                _messages.Add(3615, "localized or malformed statistics output");
                _messages.Add(3612, "SQL Server Execution Times: CPU time = 12 ms, elapsed time = 20 ms.");
            }
            return Task.FromResult<ISqlReader>(new TestReader(["Value"], [[1]]));
        }

        public ConsumedSessionMessages ConsumeMessages(int startIndex) => _messages.Consume(startIndex);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TableIoSession : ISqlSession
    {
        private readonly SessionMessageBuffer _messages = new();

        public IReadOnlyList<SessionMessage> Messages => _messages.Snapshot();
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("server", "database", "server", "database", "profile");

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            if (command.Sql.StartsWith("SET", StringComparison.Ordinal))
                return Task.FromResult<ISqlReader>(new TestReader([], []));

            for (var index = 0; index < 1200; index++)
                _messages.Add(3615,
                    "Table 'T'. Scan count 1, logical reads 2, physical reads 0, lob logical reads 0.");
            return Task.FromResult<ISqlReader>(new TestReader(["Value"], [[1]]));
        }

        public ConsumedSessionMessages ConsumeMessages(int startIndex) => _messages.Consume(startIndex);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestReader(string[] names, object?[][] rows) : ISqlReader
    {
        private int _position = -1;

        public int FieldCount => names.Length;
        public int RecordsAffected => -1;
        public string GetName(int ordinal) => names[ordinal];
        public Type GetFieldType(int ordinal) => rows[0][ordinal]?.GetType() ?? typeof(object);
        public bool GetAllowNull(int ordinal) => true;
        public object GetValue(int ordinal) => rows[_position][ordinal] ?? DBNull.Value;
        public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(++_position < rows.Length);
        public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void Plan_metrics_are_aggregated_and_documents_deduplicated()
    {
        var plan = PlanMetricsExtractorTests.ActualPlan;
        var record = JournalBenchmarkBuilder.Build(
        [
            Run("measure", 1, 1, 1, 1, plans: [plan]),
            Run("measure", 2, 1, 1, 1, plans: [plan]),
        ]);

        var variant = record.Variants[0];
        Assert.Equal(4096, variant.GrantGrantedKb);
        Assert.Equal(256, variant.GrantMaxUsedKb);
        Assert.Equal(4, variant.Dop);
        Assert.Equal(1, variant.SpillCount);
        Assert.True(variant.HasImplicitConversion);
        Assert.Equal(new JournalWait("PAGEIOLATCH_SH", 40, 7), variant.Waits[0]);
        Assert.Equal([(1, 0), (2, 0)], variant.PlanLinks.Select(link => (link.Repetition, link.Ordinal)));
        Assert.Single(variant.PlanLinks.Select(link => link.Hash).Distinct());
        var document = Assert.Single(record.PlanDocuments);
        Assert.Equal("showplan-xml", document.Format);
        Assert.Equal(variant.PlanLinks[0].Hash, document.Sha256);
    }

    [Fact]
    public void Equal_shape_statements_do_not_collide()
    {
        const string first = "<ShowPlanXML><StmtSimple StatementText=\"SELECT 1\"><QueryPlan QueryPlanHash=\"0xabc\"/></StmtSimple></ShowPlanXML>";
        const string second = "<ShowPlanXML><StmtSimple StatementText=\"SELECT 2\"><QueryPlan QueryPlanHash=\"0xabc\"/></StmtSimple></ShowPlanXML>";

        var record = JournalBenchmarkBuilder.Build(
        [
            Run("measure", 1, 1, 1, 1, plans: [first]),
            Run("measure", 2, 1, 1, 1, plans: [second]),
        ]);

        Assert.Equal(2, record.PlanDocuments.Count);
        Assert.Equal(2, record.Variants[0].PlanLinks.Select(link => link.Hash).Distinct().Count());
        Assert.Single(record.PlanDocuments.Select(document => document.ShapeHash).Distinct());
        Assert.Equal(
            [Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(first))),
             Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(second)))],
            record.Variants[0].PlanLinks.Select(link => link.Hash).ToArray());
        Assert.Equal([first, second], record.PlanDocuments.Select(document => document.Document).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Builder_keeps_repetition_specific_actual_plan_documents()
    {
        const string first = "<ShowPlanXML><StmtSimple StatementText=\"SELECT 1\"><QueryPlan QueryPlanHash=\"0xabc\"><RelOp NodeId=\"0\" PhysicalOp=\"Index Scan\"><RunTimeInformation><RunTimeCountersPerThread Thread=\"0\" ActualRows=\"3\"/></RunTimeInformation></RelOp></QueryPlan></StmtSimple></ShowPlanXML>";
        const string second = "<ShowPlanXML><StmtSimple StatementText=\"SELECT 1\"><QueryPlan QueryPlanHash=\"0xabc\"><RelOp NodeId=\"0\" PhysicalOp=\"Index Scan\"><RunTimeInformation><RunTimeCountersPerThread Thread=\"0\" ActualRows=\"7\"/></RunTimeInformation></RelOp></QueryPlan></StmtSimple></ShowPlanXML>";

        var record = JournalBenchmarkBuilder.Build(
        [
            Run("measure", 1, 1, 1, 1, plans: [first]),
            Run("measure", 2, 1, 1, 1, plans: [second]),
        ]);

        Assert.Equal(2, record.PlanDocuments.Count);
        Assert.Equal(2, record.Variants[0].PlanLinks.Select(link => link.Hash).Distinct().Count());
        Assert.Single(record.PlanDocuments.Select(document => document.ShapeHash).Distinct());
        Assert.Contains(first, record.PlanDocuments.Select(document => document.Document));
        Assert.Contains(second, record.PlanDocuments.Select(document => document.Document));
    }

    [Fact]
    public void Waits_are_capped_at_ten()
    {
        var waits = string.Concat(Enumerable.Range(1, 12).Select(i => $"<Wait WaitType=\"W{i:D2}\" WaitTimeMs=\"{i}\" WaitCount=\"1\" />"));
        var plan = $"""<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements><StmtSimple><QueryPlan><WaitStats>{waits}</WaitStats></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>""";

        var record = JournalBenchmarkBuilder.Build([Run("measure", 1, 1, 1, 1, plans: [plan])]);

        Assert.Equal(JournalBenchmarkBuilder.MaximumWaits, record.Variants[0].Waits.Count);
        Assert.Equal("W12", record.Variants[0].Waits[0].WaitType);
    }

    [Fact]
    public void Extreme_counters_do_not_overflow_cold_run_detection()
    {
        var max = long.MaxValue;
        var record = JournalBenchmarkBuilder.Build(
        [
            Run("measure", 1, max, max, max, io: [new TableIoCounters("Orders", max, max, max, max, max, max, max, max)]),
            Run("measure", 2, max, max, max, io: [new TableIoCounters("Orders", max, max, max, max, max, max, max, max)]),
        ]);

        var orders = Assert.Single(record.Variants[0].TableIo);
        Assert.Equal(2, orders.ColdRuns);
        Assert.Equal(max, orders.LogicalReads);
        Assert.Equal(new CompareDistribution(max, max, max), record.Variants[0].CpuMilliseconds);
    }

    [Fact]
    public void Empty_input_yields_empty_record() =>
        Assert.Empty(JournalBenchmarkBuilder.Build([]).Variants);
}
