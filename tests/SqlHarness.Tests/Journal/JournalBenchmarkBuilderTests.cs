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
    public void Statement_attribution_uses_the_lower_median_total_run_and_its_io()
    {
        static string Plan(long reads) => $"""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT Value FROM dbo.Orders" StatementCompId="1" QueryPlanHash="0x01"><QueryPlan><MemoryGrantInfo RequestedMemory="{reads}" GrantedMemory="{reads}" MaxUsedMemory="{reads}"/><QueryTimeStats CpuTime="{100 - reads}" ElapsedTime="{200 - reads}"/><RelOp NodeId="0" PhysicalOp="Table Scan"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualExecutions="2" ActualLogicalReads="{reads}" ActualLobLogicalReads="0" ActualScans="3"/></RunTimeInformation><TableScan><Object Table="[dbo].[Orders]"/></TableScan></RelOp></QueryPlan></StmtSimple>
            </Statements></Batch></BatchSequence></ShowPlanXML>
            """;

        var record = JournalBenchmarkBuilder.Build(
        [
            Run("measure", 1, 1, 10, 10, plans: [Plan(10)], io: [new TableIoCounters("Orders", 3, 10, 0, 0, 0, 0, 0, 0)]),
            Run("measure", 2, 1, 20, 20, plans: [Plan(20)], io: [new TableIoCounters("Orders", 3, 20, 0, 0, 0, 0, 0, 0)]),
            Run("measure", 3, 1, 30, 30, plans: [Plan(30)], io: [new TableIoCounters("Orders", 3, 30, 0, 0, 0, 0, 0, 0)]),
            Run("measure", 4, 1, 40, 40, plans: [Plan(40)], io: [new TableIoCounters("Orders", 3, 40, 0, 0, 0, 0, 0, 0)]),
        ]);

        var statement = Assert.Single(record.Statements);
        Assert.Equal(20, Assert.Single(statement.Objects!).LogicalReads);
        Assert.Equal(2, statement.Executions);
        Assert.Equal(StatementStatus.Confirmed, statement.Status);
        Assert.Equal(80, statement.CpuMilliseconds);
        Assert.Equal(180, statement.ElapsedMilliseconds);
        Assert.Equal(2, statement.PlanRepetition);
        Assert.Equal(20, statement.GrantRequestedKb);
        Assert.Equal(20, statement.GrantGrantedKb);
        Assert.Equal(20, statement.GrantMaxUsedKb);
        Assert.Null(statement.HasSpillWarning);
        Assert.Equal(0, statement.PlanDocumentOrdinal);
        Assert.Equal(0, statement.PlanStatementIndex);
    }

    [Fact]
    public void Spill_tristate_is_retained_for_selected_run()
    {
        var clean = StatementMetricsTests.JournalPlan("SELECT 1", 1, 1, "0x01", 0, 1);
        var unknown = StatementMetricsTests.JournalPlan("SELECT dbo.f()", 1, 1, "0x01", 0, 1);
        var warning = unknown.Replace("<RelOp", "<Warnings><SpillToTempDb /></Warnings><RelOp", StringComparison.Ordinal);
        static JournalStatementMetrics Build(string plan) => Assert.Single(JournalBenchmarkBuilder.Build(
        [Run("measure", 1, 1, 1, 0, plans: [plan], io: [new TableIoCounters("@P", 0, 0, 0, 0, 0, 0, 0, 0)])]).Statements);
        Assert.False(Build(clean).HasSpillWarning);
        Assert.Null(Build(unknown).HasSpillWarning);
        Assert.True(Build(warning).HasSpillWarning);
    }

    [Fact]
    public void Journal_keeps_hidden_work_separate_from_reconciled_statement_counter_attribution()
    {
        var udf = StatementMetricsTests.JournalPlan("SELECT dbo.fn_scalar()", 1, 1, "0x01", 0, 1,
            udfElapsedTime: 28, elapsedTime: 29,
            operators: [("Compute Scalar", "", [(0L, 0L, 0L)])]);
        var tvf = StatementMetricsTests.JournalPlan("SELECT * FROM dbo.fn_multi()", 2, 2, "0x02", 0, 1,
            operators: [("Table-valued function", "[dbo].[fn_multi]", [(0L, 0L, 0L)])]);
        var record = JournalBenchmarkBuilder.Build(
        [
            Run("measure", 1, 1, 10, 0, plans: [udf], io: [new TableIoCounters("", 0, 0, 0, 0, 0, 0, 0, 0)]),
            Run("measure", 2, 1, 10, 0, set: "tvf", plans: [tvf], io: [new TableIoCounters("[dbo].[fn_multi]", 0, 0, 0, 0, 0, 0, 0, 0)]),
        ]);

        var scalarStatement = Assert.Single(record.Statements, item => item.ParameterSet is null);
        var tvfStatement = Assert.Single(record.Statements, item => item.ParameterSet == "tvf");
        Assert.Equal(StatementStatus.Confirmed, scalarStatement.Status);
        Assert.Equal(StatementCounterAttribution.Complete,
            StatementAttributionClassifier.Classify(scalarStatement.Status));
        Assert.Equal("scalar-udf", Assert.Single(scalarStatement.HiddenWork).Kind);
        Assert.Equal(StatementStatus.Confirmed, tvfStatement.Status);
        Assert.Equal(StatementCounterAttribution.Complete,
            StatementAttributionClassifier.Classify(tvfStatement.Status));
        Assert.Equal("multi-statement-tvf", Assert.Single(tvfStatement.HiddenWork).Kind);
    }

    [Fact]
    public void Multi_set_reconciliation_marks_only_the_statement_with_mismatched_counters_partial()
    {
        static string Plan(long ordersReads) => $"""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT Value FROM dbo.Orders" StatementId="1" StatementCompId="1" QueryPlanHash="0x01"><QueryPlan><RelOp NodeId="0" PhysicalOp="Table Scan"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualExecutions="1" ActualLogicalReads="{ordersReads}" ActualLobLogicalReads="0" ActualScans="1" /></RunTimeInformation><TableScan><Object Table="[dbo].[Orders]" /></TableScan></RelOp></QueryPlan></StmtSimple>
            <StmtSimple StatementText="SELECT Value FROM dbo.Products" StatementId="2" StatementCompId="2" QueryPlanHash="0x02"><QueryPlan><RelOp NodeId="0" PhysicalOp="Table Scan"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualExecutions="1" ActualLogicalReads="3" ActualLobLogicalReads="0" ActualScans="1" /></RunTimeInformation><TableScan><Object Table="[dbo].[Products]" /></TableScan></RelOp></QueryPlan></StmtSimple>
            </Statements></Batch></BatchSequence></ShowPlanXML>
            """;

        var record = JournalBenchmarkBuilder.Build(
        [
            Run("baseline", 1, 1, 10, 8, set: "small", cell: 0, plans: [Plan(5)], io: [
                new TableIoCounters("Orders", 1, 5, 0, 0, 0, 0, 0, 0), new TableIoCounters("Products", 1, 3, 0, 0, 0, 0, 0, 0)]),
            Run("baseline", 1, 1, 10, 12, set: "large", cell: 1, plans: [Plan(5)], io: [
                new TableIoCounters("Orders", 1, 9, 0, 0, 0, 0, 0, 0), new TableIoCounters("Products", 1, 3, 0, 0, 0, 0, 0, 0)]),
        ]);

        var small = record.Statements.Where(item => item.ParameterSet == "small").OrderBy(item => item.StatementOrdinal).ToArray();
        var large = record.Statements.Where(item => item.ParameterSet == "large").OrderBy(item => item.StatementOrdinal).ToArray();
        Assert.Equal(["baseline", "baseline"], small.Select(item => item.Variant));
        Assert.All(small, item => Assert.Equal(0, item.MatrixCell));
        Assert.All(large, item => Assert.Equal(1, item.MatrixCell));
        Assert.Equal(
            ("baseline", "small", (int?)0, 0, (int?)1),
            (small[0].Variant, small[0].ParameterSet!, small[0].MatrixCell, small[0].StatementOrdinal, small[0].PlanRepetition));
        Assert.Equal(
            ("baseline", "large", (int?)1, 1, (int?)1),
            (large[1].Variant, large[1].ParameterSet!, large[1].MatrixCell, large[1].StatementOrdinal, large[1].PlanRepetition));
        Assert.All(small, item => Assert.Equal(StatementCounterAttribution.Complete,
            StatementAttributionClassifier.Classify(item.Status)));
        Assert.Equal(StatementCounterAttribution.Partial,
            StatementAttributionClassifier.Classify(large[0].Status));
        Assert.Equal(StatementCounterAttribution.Complete,
            StatementAttributionClassifier.Classify(large[1].Status));
    }

    [Fact]
    public void Postgres_plan_has_no_sql_server_statement_attribution_evidence()
    {
        const string explain = """{"Plan":{"Node Type":"Seq Scan","Relation Name":"orders","Actual Rows":3}}""";
        var unavailable = new BenchmarkRunMetrics(
            BenchmarkMetricReport.Unavailable, BenchmarkMetricReport.Measured, null, false, null, null,
            BenchmarkMetricReport.Measured, BenchmarkMetricText.PostgresLogicalReadsSource,
            BenchmarkMetricText.PostgresRelationBufferSource, false, BenchmarkMetricReport.ResultStatement,
            BenchmarkMetricText.StatementRows, []);

        var record = JournalBenchmarkBuilder.Build([Run("measure", 1, 0, 1, 0, plans: [explain], metrics: unavailable, io: [])]);

        Assert.Empty(record.Statements);
        Assert.Equal(StatementCounterAttribution.Unavailable, StatementAttributionClassifier.Classify(null));
    }

    [Fact]
    public void Omitted_statement_count_is_journaled_and_retained_status_fails_closed()
    {
        var oversized = new string('x', JournalStatementMetricsExtractor.MaximumBytes + 1);
        var plan = $"""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements>
            <StmtSimple StatementText="{oversized}" />
            <StmtSimple StatementText="SELECT Value FROM dbo.Orders" StatementCompId="1" QueryPlanHash="0x01"><QueryPlan><RelOp NodeId="0" PhysicalOp="Table Scan"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualExecutions="1" ActualLogicalReads="10" ActualLobLogicalReads="0" ActualScans="1"/></RunTimeInformation><TableScan><Object Table="Orders"/></TableScan></RelOp></QueryPlan></StmtSimple>
            </Statements></Batch></BatchSequence></ShowPlanXML>
            """;
        var record = JournalBenchmarkBuilder.Build(
            [Run("measure", 1, 1, 10, 10, plans: [plan], io: [new TableIoCounters("Orders", 1, 10, 0, 0, 0, 0, 0, 0)])]);

        var statement = Assert.Single(record.Statements);
        Assert.Equal(1, record.Variants.Single().OmittedStatements);
        Assert.Equal(StatementStatus.Partial, statement.Status);
    }

    [Fact]
    public void Run_without_plan_statements_has_no_statement_rows_to_confirm()
    {
        var record = JournalBenchmarkBuilder.Build(
            [Run("measure", 1, 1, 10, 10, plans: [], io: [new TableIoCounters("Orders", 1, 10, 0, 0, 0, 0, 0, 0)])]);

        Assert.Empty(record.Statements);
        Assert.Equal(0, record.Variants.Single().OmittedStatements);
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
        var run1 = StatisticsIoDetailParser.Parse("Table '#A1B2C3D4'. Scan count 1, logical reads 10, lob logical reads 0.");
        var run2 = StatisticsIoDetailParser.Parse("Table '#B2C3D4E5'. Scan count 1, logical reads 10, lob logical reads 0.");
        var run3 = StatisticsIoDetailParser.Parse("Table '#C3D4E5F6'. Scan count 1, logical reads 10, lob logical reads 0.");
        var record = JournalBenchmarkBuilder.Build(
        [
            Run("measure", 1, 1, 1, 10, io: run1),
            Run("measure", 2, 1, 1, 10, io: run2),
            Run("measure", 3, 1, 1, 10, io: run3),
        ]);

        var table = Assert.Single(Assert.Single(record.Variants).TableIo);
        Assert.Equal("table variables", table.Table);
        Assert.Equal(10, table.LogicalReads);
    }

    [Fact]
    public void Journal_keeps_real_aggregate_name_separate_from_typed_variable_rows()
    {
        var io = StatisticsIoDetailParser.Parse("""
            Table '#A1B2C3D4'. Scan count 1, logical reads 30, lob logical reads 0.
            Table 'table variables'. Scan count 1, logical reads 7, lob logical reads 0.
            """);
        var record = JournalBenchmarkBuilder.Build([Run("measure", 1, 1, 1, 37, io: io)]);

        Assert.Equal(2, record.Variants[0].TableIo.Count);
        Assert.Equal(7, record.Variants[0].TableIo.Single(row => row.Table == "table variables").LogicalReads);
        Assert.Equal(30, record.Variants[0].TableIo.Single(row => row.Table == "table variables (aggregate)").LogicalReads);
    }

    [Fact]
    public void Journal_variants_share_collision_safe_aggregate_labels()
    {
        var baselineIo = StatisticsIoDetailParser.Parse("Table '#A1B2C3D4'. Scan count 1, logical reads 30, lob logical reads 0.");
        var candidateIo = StatisticsIoDetailParser.Parse("""
            Table '#B2C3D4E5'. Scan count 1, logical reads 30, lob logical reads 0.
            Table 'table variables'. Scan count 1, logical reads 7, lob logical reads 0.
            """);
        var record = JournalBenchmarkBuilder.Build(
        [
            Run("baseline", 1, 1, 1, 30, io: baselineIo),
            Run("candidate", 1, 1, 1, 37, io: candidateIo),
        ]);

        Assert.Equal("table variables (aggregate)", Assert.Single(record.Variants.Single(row => row.Variant == "baseline").TableIo).Table);
        Assert.Contains(record.Variants.Single(row => row.Variant == "candidate").TableIo, row => row.Table == "table variables (aggregate)");
        Assert.Contains(record.Variants.Single(row => row.Variant == "candidate").TableIo, row => row.Table == "table variables");
    }

    [Fact]
    public void Journal_preserves_hash_named_legacy_rows_without_provenance()
    {
        var record = JournalBenchmarkBuilder.Build([Run("measure", 1, 1, 1, 10,
            io: [new TableIoCounters("#A1B2C3D4", 1, 10, 0, 0, 0, 0, 0, 0)])]);

        Assert.Equal("#A1B2C3D4", Assert.Single(record.Variants[0].TableIo).Table);
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