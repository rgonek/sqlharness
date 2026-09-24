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
        Assert.Equal(15, stats.ElapsedTimeMs); // 10.4 + 4.6 → 15
        Assert.Equal(3, stats.LogicalReads);
        Assert.Equal(3, stats.Tables["foo"]);
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
    public async Task Measure_uses_explain_wrap_without_statistics_or_sidecar()
    {
        var session = FakeSession.Create();
        var writer = new CapturingArtifactWriter();

        var outcome = await Module(session, writer).ExecuteAsync(Measure("SELECT 1", repeat: 1));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(2, session.Commands.Count);
        Assert.All(session.Commands, command =>
            Assert.Equal("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)\nSELECT 1", command.Sql));
        Assert.All(session.Commands, command =>
            Assert.DoesNotContain("SET STATISTICS IO ON", command.Sql, StringComparison.Ordinal));
        var report = Assert.IsType<SqlHarnessMeasureReport>(outcome.Report);
        Assert.Equal(new CompareDistribution(0, 0, 0), report.Query.CpuTimeMilliseconds);
        Assert.Equal(new CompareDistribution(15, 15, 15), report.Query.ElapsedTimeMilliseconds);
        Assert.Equal(new CompareDistribution(3, 3, 3), report.Query.LogicalReads);
        Assert.Equal(3, report.Query.TotalLogicalReadsByTable["foo"]);
        Assert.Contains(report.Query.Operators, op => op is { NodeId: 1, PhysicalOp: "Seq Scan", Object: "foo" });
        Assert.All(writer.Runs, run => Assert.Equal(ExplainFixture, Assert.Single(run.PlanXmls)));
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

    private sealed class FakeSession : ISqlSessionFactory, ISqlSession
    {
        private readonly int _sidecarValue;

        private FakeSession(int sidecarValue) => _sidecarValue = sidecarValue;

        public List<SqlExecutionCommand> Commands { get; } = [];
        public int FactoryOpenCount { get; private set; }
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("localhost", "appdb", "localhost", "appdb", "profile", Engine: "postgres");

        public static FakeSession Create(int sidecarValue = 1) => new(sidecarValue);

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            FactoryOpenCount++;
            return Task.FromResult<ISqlSession>(this);
        }

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (command.Sql.StartsWith("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)", StringComparison.Ordinal))
                return Task.FromResult<ISqlReader>(FakeReader.Rows(["QUERY PLAN"], [ExplainFixture]));
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