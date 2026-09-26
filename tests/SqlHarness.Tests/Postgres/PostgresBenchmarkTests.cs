using System.Text.Json;

using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;
using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Postgres;

public sealed class PostgresBenchmarkTests
{
    private const string ExplainFixture = """
        [{"Plan":{"Node Type":"Seq Scan","Relation Name":"foo","Shared Hit Blocks":2,"Shared Read Blocks":1,"Local Hit Blocks":0,"Local Read Blocks":0},"Planning Time":10.4,"Execution Time":4.6}]
        """;

    [Fact]
    public void Wrap_prefixes_explain_and_strips_one_trailing_semicolon()
    {
        Assert.Equal(
            "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)\nSELECT 1",
            PostgresBenchmark.Wrap("SELECT 1;"));
    }

    [Fact]
    public void Parse_reads_timing_buffers_and_tables()
    {
        var stats = PostgresBenchmark.ParseStats(ExplainFixture);
        Assert.Equal(0, stats.CpuTimeMs);
        Assert.Equal(BenchmarkMetricReport.Unavailable, stats.Metrics.CpuTimeAvailability);
        Assert.Contains(BenchmarkMetricText.PostgresCpuUnavailable, stats.Metrics.Warnings);
        Assert.Equal(15, stats.ElapsedTimeMs); // 10.4 + 4.6 → 15
        Assert.Equal(15m, stats.Metrics.ElapsedTimeMillisecondsExact);
        Assert.Equal(10.4m, stats.Metrics.PlanningTimeMilliseconds);
        Assert.Equal(4.6m, stats.Metrics.ExecutionTimeMilliseconds);
        Assert.True(stats.Metrics.ElapsedWholeMillisecondsAreExact);
        Assert.Equal(3, stats.LogicalReads);
        Assert.Equal(BenchmarkMetricReport.Measured, stats.Metrics.LogicalReadsAvailability);
        Assert.Equal(3, stats.Tables["foo"]);
        Assert.False(stats.Metrics.RelationBuffersAreAdditive);
        Assert.Contains("not additive", stats.Metrics.RelationBufferSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_uses_root_buffers_when_a_child_reports_the_same_blocks()
    {
        const string wrapped = """
            [{"Plan":{"Node Type":"Aggregate","Shared Hit Blocks":10,"Shared Read Blocks":0,"Local Hit Blocks":0,"Local Read Blocks":0,"Plans":[{"Node Type":"Aggregate","Shared Hit Blocks":10,"Shared Read Blocks":0,"Local Hit Blocks":0,"Local Read Blocks":0,"Plans":[{"Node Type":"Seq Scan","Schema":"public","Relation Name":"foo","Shared Hit Blocks":10,"Shared Read Blocks":0,"Local Hit Blocks":0,"Local Read Blocks":0}]}]},"Planning Time":1,"Execution Time":1}]
            """;
        const string scan = """
            [{"Plan":{"Node Type":"Seq Scan","Schema":"public","Relation Name":"foo","Shared Hit Blocks":10,"Shared Read Blocks":0,"Local Hit Blocks":0,"Local Read Blocks":0},"Planning Time":1,"Execution Time":1}]
            """;

        var wrappedStats = PostgresBenchmark.ParseStats(wrapped);
        var scanStats = PostgresBenchmark.ParseStats(scan);

        Assert.Equal(10, wrappedStats.LogicalReads);
        Assert.Equal(10, scanStats.LogicalReads);
        Assert.Equal(10, wrappedStats.Tables["public.foo"]);
        Assert.Equal(10, scanStats.Tables["public.foo"]);
    }

    [Fact]
    public void Parse_counts_local_buffers_once_and_keeps_schema_qualified_relations()
    {
        const string json = """
            [{"Plan":{"Node Type":"Nested Loop","Shared Hit Blocks":1,"Shared Read Blocks":2,"Local Hit Blocks":3,"Local Read Blocks":4,"Plans":[{"Node Type":"Seq Scan","Schema":"pg_temp","Relation Name":"stage","Shared Hit Blocks":0,"Shared Read Blocks":0,"Local Hit Blocks":3,"Local Read Blocks":4},{"Node Type":"Index Scan","Schema":"public","Relation Name":"foo","Shared Hit Blocks":1,"Shared Read Blocks":2,"Local Hit Blocks":0,"Local Read Blocks":0}]},"Planning Time":2,"Execution Time":2}]
            """;

        var stats = PostgresBenchmark.ParseStats(json);

        Assert.Equal(10, stats.LogicalReads);
        Assert.Equal(7, stats.Tables["pg_temp.stage"]);
        Assert.Equal(3, stats.Tables["public.foo"]);
        Assert.Equal(2, stats.Tables.Count);
        Assert.Contains("not additive", stats.Metrics.RelationBufferSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_does_not_add_parallel_workers_a_second_time()
    {
        const string json = """
            [{"Plan":{"Node Type":"Gather","Shared Hit Blocks":10,"Shared Read Blocks":0,"Local Hit Blocks":1,"Local Read Blocks":0,"Workers":[{"Worker Number":0,"Shared Hit Blocks":50},{"Worker Number":1,"Shared Hit Blocks":50}],"Plans":[{"Node Type":"Seq Scan","Schema":"public","Relation Name":"foo","Shared Hit Blocks":8,"Shared Read Blocks":0,"Local Hit Blocks":1,"Local Read Blocks":0,"Workers":[{"Worker Number":0,"Shared Hit Blocks":4,"Local Hit Blocks":1},{"Worker Number":1,"Shared Hit Blocks":4}],"Plans":[{"Node Type":"Seq Scan","Schema":"public","Relation Name":"foo","Shared Hit Blocks":2,"Shared Read Blocks":0,"Local Hit Blocks":0,"Local Read Blocks":0}]}]},"Planning Time":1.0,"Execution Time":1.0}]
            """;

        var stats = PostgresBenchmark.ParseStats(json);

        Assert.Equal(11, stats.LogicalReads);
        Assert.Equal(9, stats.Tables["public.foo"]);
    }

    [Fact]
    public void Parse_missing_root_buffers_are_unavailable_not_a_measured_zero()
    {
        const string json = """
            [{"Plan":{"Node Type":"Aggregate","Plans":[{"Node Type":"Seq Scan","Relation Name":"foo","Shared Hit Blocks":5}]},"Planning Time":1,"Execution Time":1}]
            """;

        var stats = PostgresBenchmark.ParseStats(json);

        Assert.Equal(0, stats.LogicalReads);
        Assert.Equal(BenchmarkMetricReport.Unavailable, stats.Metrics.LogicalReadsAvailability);
        Assert.Contains(BenchmarkMetricText.PostgresBuffersMissing, stats.Metrics.Warnings);
        Assert.Equal(5, stats.Tables["foo"]);
    }

    [Fact]
    public void Parse_keeps_sub_millisecond_elapsed_out_of_an_exact_zero()
    {
        const string json = """
            [{"Plan":{"Node Type":"Result","Shared Hit Blocks":1},"Planning Time":0.1,"Execution Time":0.2}]
            """;

        var stats = PostgresBenchmark.ParseStats(json);

        Assert.Equal(0, stats.ElapsedTimeMs);
        Assert.Equal(0.3m, stats.Metrics.ElapsedTimeMillisecondsExact);
        Assert.Equal(0.1m, stats.Metrics.PlanningTimeMilliseconds);
        Assert.Equal(0.2m, stats.Metrics.ExecutionTimeMilliseconds);
        Assert.False(stats.Metrics.ElapsedWholeMillisecondsAreExact);
        Assert.Equal(BenchmarkMetricReport.Measured, stats.Metrics.ElapsedTimeAvailability);
        Assert.Contains(BenchmarkMetricText.PostgresSubMillisecond, stats.Metrics.Warnings);
    }

    [Fact]
    public async Task Missing_timing_is_unavailable_in_text_and_both_json_projections()
    {
        const string plan = """[{"Plan":{"Node Type":"Result","Shared Hit Blocks":2}}]""";
        var outcome = await Module(FakeSession.Create(explain: plan)).ExecuteAsync(Measure("SELECT 1", repeat: 1));

        var text = Render(outcome, OutputMode.Text);
        Assert.Contains("measure\tunavailable", text, StringComparison.Ordinal);
        Assert.DoesNotContain("measure\t0/0/0", text, StringComparison.Ordinal);
        foreach (var json in new[] { Render(outcome, OutputMode.Json), Render(outcome, OutputMode.JsonSummary) })
        {
            using var document = JsonDocument.Parse(json);
            var metric = document.RootElement.GetProperty("query").GetProperty("metricReport");
            Assert.Equal(BenchmarkMetricReport.Unavailable, metric.GetProperty("elapsedTimeAvailability").GetString());
            Assert.False(metric.GetProperty("elapsedWholeMillisecondsAreExact").GetBoolean());
            Assert.False(metric.TryGetProperty("elapsedTimeMillisecondsExact", out _));
            Assert.Contains(
                BenchmarkMetricText.PostgresTimingMissing,
                metric.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(
                BenchmarkMetricReport.Measured,
                metric.GetProperty("logicalReadsAvailability").GetString());
        }
    }

    [Fact]
    public async Task Sub_millisecond_measure_is_not_an_exact_zero_in_text_or_either_json_projection()
    {
        const string plan = """
            [{"Plan":{"Node Type":"Aggregate","Shared Hit Blocks":10,"Plans":[{"Node Type":"Seq Scan","Schema":"public","Relation Name":"foo","Shared Hit Blocks":10}]},"Planning Time":0.1,"Execution Time":0.2}]
            """;
        var session = FakeSession.Create(explain: plan);
        var outcome = await Module(session).ExecuteAsync(Measure("SELECT 1", repeat: 1));
        var report = Assert.IsType<SqlHarnessMeasureReport>(outcome.Report);
        var metric = report.Query.MetricReport;
        Assert.NotNull(metric);
        Assert.Equal(0, report.Query.ElapsedTimeMilliseconds.Median);
        Assert.Equal(0.3m, metric!.ElapsedTimeMillisecondsExact!.Median);
        Assert.False(metric.ElapsedWholeMillisecondsAreExact);
        Assert.Equal(BenchmarkMetricReport.Unavailable, metric.CpuTimeAvailability);
        Assert.Equal(10, report.Query.LogicalReads.Median);
        Assert.Equal(10, report.Query.TotalLogicalReadsByTable["public.foo"]);
        Assert.Contains(report.Query.Operators, op => op.Object == "public.foo");
        Assert.Equal(BenchmarkMetricReport.ResultUnmeasuredSidecar, metric.ResultRowSource);
        Assert.Equal(BenchmarkMetricText.SidecarRows, metric.ResultRowSourceDescription);
        Assert.Equal(0.1m, metric.PlanningTimeMilliseconds!.Median);
        Assert.Equal(0.2m, metric.ExecutionTimeMilliseconds!.Median);

        var text = Render(outcome, OutputMode.Text);
        var json = Render(outcome, OutputMode.Json);
        var summary = Render(outcome, OutputMode.JsonSummary);
        Assert.Contains("0.3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("measure\t0/0/0", text, StringComparison.Ordinal);
        AssertSameAvailability(json, summary);
        using var full = JsonDocument.Parse(json);
        using var projected = JsonDocument.Parse(summary);
        var fullMetric = full.RootElement.GetProperty("query").GetProperty("metricReport");
        var summaryMetric = projected.RootElement.GetProperty("query").GetProperty("metricReport");
        Assert.Equal(fullMetric.GetRawText(), summaryMetric.GetRawText());
        Assert.Equal(0.3m, fullMetric.GetProperty("elapsedTimeMillisecondsExact").GetProperty("median").GetDecimal());
        Assert.False(fullMetric.GetProperty("elapsedWholeMillisecondsAreExact").GetBoolean());
        Assert.Equal("unavailable", fullMetric.GetProperty("cpuTimeAvailability").GetString());
        Assert.Equal("unmeasured-sidecar", fullMetric.GetProperty("resultRowSource").GetString());
        Assert.Contains("not additive", fullMetric.GetProperty("relationBufferSource").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_second_statement()
    {
        var error = Assert.Throws<SqlHarnessSafetyException>(
            () => PostgresBenchmark.ValidateMeasuredBatch("SELECT 1; SELECT 2"));
        Assert.DoesNotContain("SELECT 1", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_accepts_values() =>
        PostgresBenchmark.ValidateMeasuredBatch("VALUES (1)");

    [Fact]
    public void Validate_accepts_with_select() =>
        PostgresBenchmark.ValidateMeasuredBatch("WITH x AS (SELECT 1 AS n) SELECT n FROM x");

    [Fact]
    public void Validate_rejects_writable_cte()
    {
        const string sql = "WITH t AS (INSERT INTO foo SELECT 1 RETURNING *) SELECT * FROM t";
        var error = Assert.Throws<SqlHarnessSafetyException>(
            () => PostgresBenchmark.ValidateMeasuredBatch(sql));
        Assert.DoesNotContain("INSERT", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("foo", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sql, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Measure_hashes_canonical_rows_without_statistics_or_comparison_cap()
    {
        var session = FakeSession.Create();
        var writer = new CapturingArtifactWriter();

        var outcome = await Module(session, writer).ExecuteAsync(Measure("SELECT 1", repeat: 1));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        const string explain = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)\nSELECT 1";
        Assert.Equal([explain, explain, "SELECT 1"], session.Commands.Select(command => command.Sql));
        Assert.All(session.Commands, command =>
            Assert.DoesNotContain("SET STATISTICS IO ON", command.Sql, StringComparison.Ordinal));
        using var expected = new CanonicalResultAccumulator();
        expected.BeginResultSet([new CanonicalColumn(0, "n", "System.Int32", false)]);
        expected.AddRow([1]);
        expected.EndResultSet();
        Assert.Equal(expected.Complete().Hash, Assert.Single(writer.Runs).ResultHash);
        var report = Assert.IsType<SqlHarnessMeasureReport>(outcome.Report);
        Assert.Equal(new CompareDistribution(0, 0, 0), report.Query.CpuTimeMilliseconds);
        Assert.Equal(new CompareDistribution(15, 15, 15), report.Query.ElapsedTimeMilliseconds);
        Assert.Equal(new CompareDistribution(3, 3, 3), report.Query.LogicalReads);
        Assert.Equal(3, report.Query.TotalLogicalReadsByTable["foo"]);
        Assert.Contains(report.Query.Operators, op => op is { NodeId: 1, PhysicalOp: "Seq Scan", Object: "foo" });
        Assert.All(writer.Runs, run => Assert.Equal(ExplainFixture, Assert.Single(run.PlanXmls)));
    }

    [Fact]
    public async Task Parameter_sets_hash_rows_per_set_without_the_comparison_fingerprint()
    {
        var session = new VaryingRowSession();
        var writer = new CapturingArtifactWriter();
        var module = new SqlHarnessModule(session, new FakeGain(), writer, Profiles)
        {
            ComparisonMaximumRows = 1,
        };
        var operation = new SqlHarnessMeasureOperation(
            Target(),
            null,
            "SELECT @id AS n",
            [],
            30,
            2,
            [
                new("stable", ["id:int=1"]),
                new("drifting", ["id:int=2"]),
            ]);

        var outcome = await module.ExecuteAsync(operation);

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        Assert.Equal(4, session.Commands.Count(command => command.Sql == "SELECT @id AS n"));
        Assert.Equal(6, session.Commands.Count(command => command.Sql.StartsWith("EXPLAIN ", StringComparison.Ordinal)));
        Assert.DoesNotContain(session.Commands, command => command.Sql.Contains("SET STATISTICS", StringComparison.Ordinal));
        using var expected = new CanonicalResultAccumulator();
        expected.BeginResultSet([new CanonicalColumn(0, "n", "System.Int32", false)]);
        expected.AddRow([7]);
        expected.AddRow([8]);
        expected.EndResultSet();
        var stableHash = expected.Complete().Hash;
        using var empty = new CanonicalResultAccumulator();
        Assert.NotEqual(empty.Complete().Hash, stableHash);

        var stableRuns = writer.Runs.Where(run => run.ParameterSet == "stable").ToArray();
        var driftingRuns = writer.Runs.Where(run => run.ParameterSet == "drifting").ToArray();
        Assert.Equal(2, stableRuns.Length);
        Assert.Equal(2, driftingRuns.Length);
        Assert.All(stableRuns, run => Assert.Equal(stableHash, run.ResultHash));
        Assert.NotEqual(driftingRuns[0].ResultHash, driftingRuns[1].ResultHash);

        var report = Assert.IsType<SqlHarnessMeasureSetReport>(outcome.Report);
        var stable = Assert.Single(report.Sets, set => set.Name == "stable");
        var drifting = Assert.Single(report.Sets, set => set.Name == "drifting");
        Assert.True(stable.ResultsStable);
        Assert.Equal(stableHash, stable.ResultHash);
        Assert.False(drifting.ResultsStable);
        Assert.Null(drifting.ResultHash);
        Assert.Equal(BenchmarkMetricReport.Unavailable, report.CrossSetSummary.CpuTimeAvailability);
        Assert.Null(report.CrossSetSummary.MinimumMedianCpuSet);
        Assert.Null(report.CrossSetSummary.MaximumMedianCpuSet);
        var text = Render(outcome, OutputMode.Text);
        Assert.Contains("Cross-set cpu: unavailable", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Cross-set cpu: stable", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Cross-set cpu: drifting", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_ordered_runs_explain_then_unmeasured_sidecar()
    {
        var session = FakeSession.Create(sidecarValue: 99);
        var writer = new CapturingArtifactWriter();

        var outcome = await Module(session, writer).ExecuteAsync(Compare("SELECT 1", "SELECT 1", repeat: 1));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        const string explain = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)\nSELECT 1";
        Assert.Equal(
            [explain, explain, explain, "SELECT 1", explain, "SELECT 1"],
            session.Commands.Select(command => command.Sql));
        Assert.All(session.Commands, command =>
            Assert.DoesNotContain("SET STATISTICS IO ON", command.Sql, StringComparison.Ordinal));
        using var expected = new CanonicalResultAccumulator();
        expected.BeginResultSet([new CanonicalColumn(0, "n", "System.Int32", false)]);
        expected.AddRow([99]);
        expected.EndResultSet();
        var sidecarHash = expected.Complete().Hash;
        Assert.All(writer.Runs, run => Assert.Equal(sidecarHash, run.ResultHash));
        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.True(report.ResultsEquivalent);
        Assert.Equal(ResultComparisonMode.Ordered, report.Equivalence.Mode);
        Assert.Equal(new CompareDistribution(0, 0, 0), report.Baseline.CpuTimeMilliseconds);
        Assert.Equal(new CompareDistribution(15, 15, 15), report.Baseline.ElapsedTimeMilliseconds);
        Assert.Equal(BenchmarkMetricReport.Unavailable, report.Baseline.MetricReport!.CpuTimeAvailability);
        Assert.Equal(BenchmarkMetricReport.ResultUnmeasuredSidecar, report.Baseline.MetricReport.ResultRowSource);
        Assert.Equal(BenchmarkMetricText.SidecarRows, report.Baseline.MetricReport.ResultRowSourceDescription);
    }

    [Fact]
    public async Task Compare_off_runs_explain_only()
    {
        var session = FakeSession.Create();

        var outcome = await Module(session).ExecuteAsync(
            Compare("SELECT 1", "SELECT 1", repeat: 1) with { CompareResults = ResultComparisonMode.Off });

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.All(session.Commands, command =>
            Assert.Equal("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)\nSELECT 1", command.Sql));
        Assert.DoesNotContain(session.Commands, command => command.Sql == "SELECT 1");
        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.Equal(BenchmarkMetricReport.ResultNotCaptured, report.Baseline.MetricReport!.ResultRowSource);
        Assert.Equal(BenchmarkMetricText.RowsNotCaptured, report.Baseline.MetricReport.ResultRowSourceDescription);
    }

    [Fact]
    public async Task Measure_rejects_multi_statement_before_connect()
    {
        var session = FakeSession.Create();

        var outcome = await Module(session).ExecuteAsync(Measure("SELECT 1; SELECT 2", repeat: 1));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(0, session.FactoryOpenCount);
        Assert.DoesNotContain("SELECT 1", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT 2", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    private static SqlHarnessModule Module(FakeSession session, ICompareArtifactWriter? writer = null) =>
        new(session, new FakeGain(), writer ?? new CapturingArtifactWriter(), Profiles);

    private static string Render(SqlHarnessOutcome outcome, OutputMode mode)
    {
        var writer = new StringWriter();
        new Renderer().Render(outcome, mode, new OutputCaptureWriter(writer));
        return writer.ToString();
    }

    private static void AssertSameAvailability(string fullJson, string summaryJson)
    {
        using var full = JsonDocument.Parse(fullJson);
        using var summary = JsonDocument.Parse(summaryJson);
        Assert.Equal(
            full.RootElement.GetProperty("query").GetProperty("metricReport").GetProperty("cpuTimeAvailability").GetString(),
            summary.RootElement.GetProperty("query").GetProperty("metricReport").GetProperty("cpuTimeAvailability").GetString());
        Assert.Equal(
            full.RootElement.GetProperty("query").GetProperty("metricReport").GetProperty("elapsedWholeMillisecondsAreExact").GetBoolean(),
            summary.RootElement.GetProperty("query").GetProperty("metricReport").GetProperty("elapsedWholeMillisecondsAreExact").GetBoolean());
    }

    private static SqlHarnessMeasureOperation Measure(string sql, int repeat) =>
        new(Target(), null, sql, [], 30, repeat);

    private static SqlHarnessCompareOperation Compare(string baseline, string candidate, int repeat) =>
        new(Target(), null, baseline, candidate, [], 30, repeat);

    private static SqlTargetRequest Target() =>
        new("local-pg", new Dictionary<string, string>());

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["local-pg"] = new(
                "localhost,5432",
                "appdb",
                new Dictionary<string, string>(),
                "sql",
                SqlUser: "sqlharness",
                PasswordEnvVar: "SQLHARNESS_PG_PASSWORD",
                TrustServerCertificate: true,
                Engine: "postgres"),
        };

    private sealed class FakeGain : IGainStore
    {
        public void Append(GainRecord record) { }
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class CapturingArtifactWriter : ICompareArtifactWriter
    {
        public IReadOnlyList<CompareRunArtifact> Runs { get; private set; } = [];

        public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target)
        {
            Runs = runs.ToArray();
            return "pg-artifacts";
        }
    }

    private sealed class VaryingRowSession : ISqlSessionFactory, ISqlSession
    {
        private int _driftingRows;

        public List<SqlExecutionCommand> Commands { get; } = [];
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("localhost", "appdb", "localhost", "appdb", "profile", Engine: "postgres");

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct) =>
            Task.FromResult<ISqlSession>(this);

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (command.Sql.StartsWith("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)", StringComparison.Ordinal))
                return Task.FromResult<ISqlReader>(FakeReader.Rows(["QUERY PLAN"], [ExplainFixture]));

            var id = Assert.IsType<int>(Assert.Single(command.Parameters, parameter => parameter.Name == "@id").Value);
            if (id == 1)
                return Task.FromResult<ISqlReader>(FakeReader.Rows(["n"], [7], [8]));

            _driftingRows++;
            return Task.FromResult<ISqlReader>(FakeReader.Rows(["n"], [_driftingRows == 1 ? 1 : 2]));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSession : ISqlSessionFactory, ISqlSession
    {
        private readonly int _sidecarValue;
        private readonly string _explain;

        private FakeSession(int sidecarValue, string explain)
        {
            _sidecarValue = sidecarValue;
            _explain = explain;
        }

        public List<SqlExecutionCommand> Commands { get; } = [];
        public int FactoryOpenCount { get; private set; }
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("localhost", "appdb", "localhost", "appdb", "profile", Engine: "postgres");

        public static FakeSession Create(int sidecarValue = 1, string? explain = null) => new(sidecarValue, explain ?? ExplainFixture);

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            FactoryOpenCount++;
            return Task.FromResult<ISqlSession>(this);
        }

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (command.Sql.StartsWith("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)", StringComparison.Ordinal))
                return Task.FromResult<ISqlReader>(FakeReader.Rows(["QUERY PLAN"], [_explain]));
            return Task.FromResult<ISqlReader>(FakeReader.Rows(["n"], [_sidecarValue]));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeReader(string[] names, object?[][] rows) : ISqlReader
    {
        private int _position = -1;
        public int FieldCount => names.Length;
        public int RecordsAffected => -1;
        public static FakeReader Rows(string[] names, params object?[][] rows) => new(names, rows);
        public string GetName(int ordinal) => names[ordinal];
        public Type GetFieldType(int ordinal) => rows.FirstOrDefault()?[ordinal]?.GetType() ?? typeof(object);
        public bool GetAllowNull(int ordinal) => rows.Any(row => row[ordinal] is null or DBNull);
        public object GetValue(int ordinal) => rows[_position][ordinal] ?? DBNull.Value;
        public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(++_position < rows.Length);
        public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}