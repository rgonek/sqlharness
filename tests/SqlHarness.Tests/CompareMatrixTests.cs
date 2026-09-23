using System.Globalization;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public class CompareMatrixTests
{
    private const string SetupSql = "SELECT Id INTO #ids FROM dbo.Clients WHERE Tenant = @Tenant";
    private const string BaselineSql = "SELECT Value FROM dbo.Clients WHERE BatchSize = @BatchSize AND Tenant = @Tenant";
    private const string CandidateSql = "SELECT Value FROM dbo.Clients WHERE BatchSize = @BatchSize AND Tenant = @Tenant -- candidate";
    private const string Plan = "<ShowPlanXML><BatchSequence><RelOp NodeId=\"1\" PhysicalOp=\"Index Seek\"><IndexScan><Object Table=\"[Clients]\" /></IndexScan></RelOp></BatchSequence></ShowPlanXML>";

    [Fact]
    public async Task Matrix_runs_each_value_on_a_distinct_session_in_order()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();

        var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix("BatchSize:int=1,20,100"));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(3, factory.ConnectCount);
        Assert.Equal(3, factory.Sessions.Count);
        Assert.NotSame(factory.Sessions[0], factory.Sessions[1]);
        Assert.NotSame(factory.Sessions[1], factory.Sessions[2]);
        Assert.All(factory.Sessions, session => Assert.Equal(1, session.SetupCount));
        Assert.Equal([1], factory.Sessions[0].BatchSizes);
        Assert.Equal([20], factory.Sessions[1].BatchSizes);
        Assert.Equal([100], factory.Sessions[2].BatchSizes);
        Assert.All(factory.Sessions, session => Assert.Equal([7], session.TenantValues));
        Assert.All(factory.Sessions, session =>
        {
            var userCommands = session.Commands.Where(command => !IsStatistics(command.Sql)).ToArray();
            Assert.NotEmpty(userCommands);
            Assert.All(userCommands, command =>
                Assert.Equal(["@Tenant", "@BatchSize"], command.Parameters.Select(parameter => parameter.Name)));
        });

        var report = Assert.IsType<SqlHarnessCompareMatrixReport>(outcome.Report);
        Assert.Equal("@BatchSize", report.ParameterName);
        Assert.Equal("int", report.ParameterType);
        Assert.Equal([0, 1, 2], report.Cells.Select(cell => cell.Index));
        Assert.Equal(["1", "20", "100"], report.Cells.Select(cell => cell.ParameterValue));
        Assert.Equal(3, artifacts.Directories.Count);
        Assert.All(report.Cells, cell =>
        {
            Assert.True(cell.Compare.ResultsEquivalent);
            Assert.Contains(cell.Compare.ArtifactDirectory, artifacts.Directories);
            Assert.True(Directory.Exists(cell.Compare.ArtifactDirectory));
            Assert.Equal(["@Tenant", "@BatchSize"], cell.Compare.Parameters.Select(parameter => parameter.Name));
        });
    }

    [Fact]
    public async Task Matrix_rejects_invalid_sql_before_any_connection()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();

        var outcome = await Module(factory, artifacts).ExecuteAsync(
            Matrix("BatchSize:int=1,20,100", candidate: "DELETE dbo.Clients"));

        Assert.Equal(2, (int)outcome.ExitCode);
        Assert.Equal(0, factory.ConnectCount);
        Assert.Null(outcome.Report);
        Assert.Empty(artifacts.Directories);
    }

    [Fact]
    public async Task Matrix_rejects_invalid_final_value_before_any_connection()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();

        var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix("BatchSize:int=1,20,nope"));

        Assert.Equal(2, (int)outcome.ExitCode);
        Assert.Equal(0, factory.ConnectCount);
        Assert.Null(outcome.Report);
        Assert.DoesNotContain("nope", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Matrix_rejects_fixed_parameter_conflict_before_any_connection()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();

        var outcome = await Module(factory, artifacts).ExecuteAsync(
            Matrix("BatchSize:int=1,20", parameters: ["BatchSize:int=5"]));

        Assert.Equal(2, (int)outcome.ExitCode);
        Assert.Equal(0, factory.ConnectCount);
        Assert.Null(outcome.Report);
        Assert.Contains("duplicates", outcome.SafeError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@BatchSize", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("20", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("=5", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Matrix_rejects_unreferenced_parameter_before_any_connection()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();

        var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix(
            "BatchSize:int=1,20",
            setup: null,
            baseline: "SELECT Value FROM dbo.Clients",
            candidate: "SELECT Value FROM dbo.Clients",
            parameters: []));

        Assert.Equal(2, (int)outcome.ExitCode);
        Assert.Equal(0, factory.ConnectCount);
        Assert.Null(outcome.Report);
        Assert.Contains("@BatchSize", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("not referenced", outcome.SafeError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Amount:money=1.00,2.00", "1.00")]
    [InlineData("Path:hierarchyid=/1/2/,/1/3/", "/1/2/")]
    [InlineData("Loc:geography=POINT(0 0),POINT(1 1)", "POINT(0 0)")]
    [InlineData("Shape:geometry=POINT(0 0),POINT(1 1)", "POINT(0 0)")]
    public async Task Postgres_rejects_unsupported_matrix_types_before_connect(string matrix, string secret)
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();
        var name = matrix[..matrix.IndexOf(':')];
        var sql = $"SELECT @{name} AS {name}";

        var outcome = await Module(factory, artifacts, PostgresProfiles).ExecuteAsync(Matrix(
            matrix,
            setup: null,
            baseline: sql,
            candidate: sql,
            parameters: [],
            profile: "local-pg"));

        Assert.Equal(2, (int)outcome.ExitCode);
        Assert.Equal(0, factory.ConnectCount);
        Assert.Null(outcome.Report);
        Assert.Contains("not supported on Postgres", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Postgres_rejects_unsupported_fixed_parameter_before_connect()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();
        const string sql = "SELECT @BatchSize AS BatchSize, @Amount AS Amount";

        var outcome = await Module(factory, artifacts, PostgresProfiles).ExecuteAsync(Matrix(
            "BatchSize:int=1,20",
            setup: null,
            baseline: sql,
            candidate: sql,
            parameters: ["Amount:money=9.99"],
            profile: "local-pg"));

        Assert.Equal(2, (int)outcome.ExitCode);
        Assert.Equal(0, factory.ConnectCount);
        Assert.Null(outcome.Report);
        Assert.Contains("not supported on Postgres", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("9.99", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Matrix_stops_on_sql_failure_and_keeps_the_earlier_artifact_directory()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory(failSqlAt: 1);

        var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix("BatchSize:int=1,20,100"));

        Assert.Equal(5, (int)outcome.ExitCode);
        Assert.Equal(2, factory.ConnectCount);
        Assert.Null(outcome.Report);
        var error = outcome.SafeError ?? string.Empty;
        Assert.Contains("cell 1", error, StringComparison.Ordinal);
        Assert.Contains("@BatchSize", error, StringComparison.Ordinal);
        Assert.Contains("measured-run-failed", error, StringComparison.Ordinal);
        Assert.DoesNotContain("20", error, StringComparison.Ordinal);
        Assert.Equal([1], factory.Sessions[0].BatchSizes);
        Assert.Equal([20], factory.Sessions[1].BatchSizes);
        var kept = Assert.Single(artifacts.Directories);
        Assert.True(Directory.Exists(kept));
    }

    [Fact]
    public async Task Matrix_applies_the_row_cap_and_does_not_open_a_later_cell()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory(resultRowCount: 3);

        var outcome = await Module(factory, artifacts, comparisonMaximumRows: 2)
            .ExecuteAsync(Matrix("BatchSize:int=1,20"));

        Assert.Equal(2, (int)outcome.ExitCode);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Null(outcome.Report);
        Assert.Contains("cell 0", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("@BatchSize", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("20", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Matrix_preserves_authentication_failure_for_the_first_cell()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory(failConnect: true);

        var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix("BatchSize:int=1,20"));

        Assert.Equal(3, (int)outcome.ExitCode);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Empty(factory.Sessions);
        Assert.Null(outcome.Report);
        Assert.Contains("cell 0", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("@BatchSize", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("20", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    private static SqlHarnessModule Module(
        MatrixSessionFactory sessions,
        ICompareArtifactWriter artifacts,
        Func<IReadOnlyDictionary<string, TargetProfile>>? profiles = null,
        int? comparisonMaximumRows = null) =>
        new(sessions, new FakeGainStore(), artifacts, profiles ?? SqlProfiles)
        {
            ComparisonMaximumRows = comparisonMaximumRows ?? CanonicalComparisonAccumulator.MaximumComparedRows,
        };

    private static SqlHarnessCompareMatrixOperation Matrix(
        string matrix,
        string? setup = SetupSql,
        string? baseline = BaselineSql,
        string? candidate = CandidateSql,
        IReadOnlyList<string>? parameters = null,
        int repeat = 1,
        string profile = "test") =>
        new(
            new SqlTargetRequest(profile, profile == "test"
                ? new Dictionary<string, string> { ["env"] = "a" }
                : new Dictionary<string, string>()),
            setup,
            baseline!,
            candidate!,
            parameters ?? ["Tenant:int=7"],
            30,
            repeat,
            matrix);

    private static IReadOnlyDictionary<string, TargetProfile> SqlProfiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new("test-server", "testdb-{env}", new Dictionary<string, string> { ["env"] = "^(a|b)$" }, "integrated"),
        };

    private static IReadOnlyDictionary<string, TargetProfile> PostgresProfiles() =>
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

    private static bool IsStatistics(string sql) =>
        sql.Contains("STATISTICS", StringComparison.Ordinal);

    private sealed class FakeGainStore : IGainStore
    {
        public void Append(GainRecord record) { }
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class DirectoryArtifactWriter : ICompareArtifactWriter, IDisposable
    {
        public List<string> Directories { get; } = [];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sqlharness-matrix-" + Guid.NewGuid().ToString("N"));

        public DirectoryArtifactWriter() => Directory.CreateDirectory(Root);

        public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target)
        {
            var directory = Path.Combine(Root, "cell-" + Directories.Count.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            Directories.Add(directory);
            return directory;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class MatrixSessionFactory : ISqlSessionFactory
    {
        private readonly int? _failSqlAt;
        private readonly bool _failConnect;
        private readonly int _resultRowCount;

        public MatrixSessionFactory(int? failSqlAt = null, bool failConnect = false, int resultRowCount = 1)
        {
            _failSqlAt = failSqlAt;
            _failConnect = failConnect;
            _resultRowCount = resultRowCount;
        }

        public int ConnectCount { get; private set; }
        public List<MatrixSession> Sessions { get; } = [];

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            var index = ConnectCount;
            ConnectCount++;
            if (_failConnect)
                return Task.FromException<ISqlSession>(new InvalidOperationException("login failed"));

            var session = new MatrixSession(failSql: _failSqlAt == index, _resultRowCount);
            Sessions.Add(session);
            return Task.FromResult<ISqlSession>(session);
        }
    }

    private sealed class MatrixSession : ISqlSession
    {
        private readonly bool _failSql;
        private readonly int _resultRowCount;
        private readonly List<string> _messages = [];

        public MatrixSession(bool failSql, int resultRowCount)
        {
            _failSql = failSql;
            _resultRowCount = resultRowCount;
        }

        public List<SqlExecutionCommand> Commands { get; } = [];
        public int SetupCount { get; private set; }
        public IReadOnlyList<string> Messages => _messages;
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("test-server", "testdb-a", "test-server", "testdb-a", "profile");

        public IReadOnlyList<int> BatchSizes => Values("@BatchSize");
        public IReadOnlyList<int> TenantValues => Values("@Tenant");

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (IsStatistics(command.Sql))
                return Task.FromResult<ISqlReader>(MatrixReader.Empty());

            if (_failSql)
            {
                var batch = Convert.ToString(
                    command.Parameters.Single(parameter => parameter.Name == "@BatchSize").Value,
                    CultureInfo.InvariantCulture);
                return Task.FromException<ISqlReader>(new TimeoutException($"measured-run-failed:{batch}"));
            }

            if (command.Sql.Contains("INTO #ids", StringComparison.Ordinal))
            {
                SetupCount++;
                return Task.FromResult<ISqlReader>(MatrixReader.Empty());
            }

            _messages.Add(
                "Table 'Clients'. Scan count 1, logical reads 5, physical reads 0, lob logical reads 0.\nSQL Server Execution Times: CPU time = 10 ms, elapsed time = 12 ms.");
            var rows = Enumerable.Range(0, _resultRowCount)
                .Select(index => new object?[] { 42 + index })
                .ToArray();
            return Task.FromResult<ISqlReader>(MatrixReader.WithPlan(["Value"], rows, Plan));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private IReadOnlyList<int> Values(string name) =>
            Commands
                .Where(command => !IsStatistics(command.Sql))
                .Select(command => (int)command.Parameters.Single(parameter => parameter.Name == name).Value)
                .Distinct()
                .ToArray();
    }

    private sealed class MatrixReader : ISqlReader
    {
        private readonly IReadOnlyList<Result> _results;
        private int _result;
        private int _row = -1;
        private Result Current => _results[_result];

        private MatrixReader(IReadOnlyList<Result> results) => _results = results;

        public int FieldCount => _results.Count == 0 ? 0 : Current.Names.Length;
        public int RecordsAffected => -1;
        public static MatrixReader Empty() => new([]);
        public static MatrixReader WithPlan(string[] names, object?[][] rows, string plan) => new(
            [new(names, rows), new(["Microsoft SQL Server 2005 XML Showplan"], [[plan]])]);

        public string GetName(int ordinal) => Current.Names[ordinal];
        public Type GetFieldType(int ordinal) => Current.Rows[0][ordinal]?.GetType() ?? typeof(object);
        public bool GetAllowNull(int ordinal) => false;
        public object GetValue(int ordinal) => Current.Rows[_row][ordinal] ?? DBNull.Value;
        public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(_results.Count > 0 && ++_row < Current.Rows.Length);
        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            if (_results.Count == 0 || ++_result >= _results.Count)
                return Task.FromResult(false);
            _row = -1;
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed record Result(string[] Names, object?[][] Rows);
    }
}
