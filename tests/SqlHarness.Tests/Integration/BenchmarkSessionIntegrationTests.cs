using Microsoft.Data.SqlClient;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Integration;

public sealed class BenchmarkSessionIntegrationTests
{
    [SqlServerIntegrationFact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task Compare_setup_warmup_baseline_and_candidate_share_one_session_for_local_temp()
    {
        var connectionString = Environment.GetEnvironmentVariable(SqlServerIntegrationFactAttribute.Variable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.False(string.IsNullOrWhiteSpace(builder.DataSource));
        Assert.False(string.IsNullOrWhiteSpace(builder.InitialCatalog));

        using var temp = new TempDirectory();
        await using var factory = new IntegrationSessionFactory(connectionString);
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["integration"] = new(
                builder.DataSource,
                builder.InitialCatalog,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "integrated"),
        };

        var module = new SqlHarnessModule(
            factory,
            new NullGainStore(),
            new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UtcNow),
            () => profiles);

        var operation = new SqlHarnessCompareOperation(
            new SqlTargetRequest("integration", new Dictionary<string, string>()),
            """
            CREATE TABLE #Req (Id int NOT NULL PRIMARY KEY);
            INSERT #Req VALUES (1);
            """,
            "SELECT Id FROM #Req;",
            "SELECT Id FROM #Req;",
            [],
            30,
            1);

        var outcome = await module.ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.True(report.ResultsEquivalent);
        Assert.Equal(1, factory.ConnectCount);
    }

    [SqlServerIntegrationFact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task Compare_matrix_uses_a_distinct_session_and_one_setup_per_value()
    {
        var connectionString = Environment.GetEnvironmentVariable(SqlServerIntegrationFactAttribute.Variable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.False(string.IsNullOrWhiteSpace(builder.DataSource));
        Assert.False(string.IsNullOrWhiteSpace(builder.InitialCatalog));

        using var temp = new TempDirectory();
        await using var factory = new IntegrationSessionFactory(connectionString);
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["integration"] = new(
                builder.DataSource,
                builder.InitialCatalog,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "integrated"),
        };

        var module = new SqlHarnessModule(
            factory,
            new NullGainStore(),
            new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UtcNow),
            () => profiles);

        var operation = new SqlHarnessCompareMatrixOperation(
            new SqlTargetRequest("integration", new Dictionary<string, string>()),
            """
            CREATE TABLE #Req
            (
                Id int NOT NULL PRIMARY KEY,
                SessionId int NOT NULL
            );
            INSERT #Req(Id, SessionId) VALUES (@BatchSize, @@SPID);
            """,
            "SELECT Id, SessionId FROM #Req;",
            "SELECT Id, SessionId FROM #Req;",
            [],
            30,
            1,
            "BatchSize:int=1,20");

        var outcome = await module.ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessCompareMatrixReport>(outcome.Report);
        Assert.Equal(2, report.Cells.Count);
        Assert.All(report.Cells, cell => Assert.True(cell.Compare.ResultsEquivalent));
        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(2, factory.ServerProcessIds.Count);
    }

    [SqlServerIntegrationFact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task Measure_setup_temp_survives_warmup_and_repetitions_with_parameterized_population()
    {
        var connectionString = Environment.GetEnvironmentVariable(SqlServerIntegrationFactAttribute.Variable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.False(string.IsNullOrWhiteSpace(builder.DataSource));
        Assert.False(string.IsNullOrWhiteSpace(builder.InitialCatalog));

        using var temp = new TempDirectory();
        await using var factory = new IntegrationSessionFactory(connectionString);
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["integration"] = new(
                builder.DataSource,
                builder.InitialCatalog,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "integrated"),
        };

        var module = new SqlHarnessModule(
            factory,
            new NullGainStore(),
            new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UtcNow),
            () => profiles);

        var operation = new SqlHarnessMeasureOperation(
            new SqlTargetRequest("integration", new Dictionary<string, string>()),
            """
            CREATE TABLE #Prepared (Id int NOT NULL PRIMARY KEY);
            INSERT #Prepared(Id) VALUES (@Seed);
            """,
            "SELECT Id FROM #Prepared;",
            ["Seed:int=42"],
            30,
            3);

        var outcome = await module.ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessMeasureReport>(outcome.Report);
        Assert.True(report.ResultsStable);
        Assert.Equal(1, factory.ConnectCount);
    }

    [SqlServerIntegrationFact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task Compare_setup_with_extra_fixed_parameter_used_only_by_measured_sql()
    {
        var connectionString = Environment.GetEnvironmentVariable(SqlServerIntegrationFactAttribute.Variable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.False(string.IsNullOrWhiteSpace(builder.DataSource));
        Assert.False(string.IsNullOrWhiteSpace(builder.InitialCatalog));

        using var temp = new TempDirectory();
        await using var factory = new IntegrationSessionFactory(connectionString);
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["integration"] = new(
                builder.DataSource,
                builder.InitialCatalog,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "integrated"),
        };

        var module = new SqlHarnessModule(
            factory,
            new NullGainStore(),
            new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UtcNow),
            () => profiles);

        var operation = new SqlHarnessCompareOperation(
            new SqlTargetRequest("integration", new Dictionary<string, string>()),
            """
            CREATE TABLE #Req (Id int NOT NULL PRIMARY KEY);
            INSERT #Req VALUES (1);
            """,
            "SELECT @Unused AS Id UNION ALL SELECT Id FROM #Req;",
            "SELECT @Unused AS Id UNION ALL SELECT Id FROM #Req;",
            ["Unused:int=99"],
            30,
            2);

        var outcome = await module.ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.True(report.ResultsEquivalent);
        Assert.Equal(1, factory.ConnectCount);
    }

    [SqlServerIntegrationFact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task Measure_rejects_parameterized_select_into_temp_offline()
    {
        var connectionString = Environment.GetEnvironmentVariable(SqlServerIntegrationFactAttribute.Variable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.False(string.IsNullOrWhiteSpace(builder.DataSource));
        Assert.False(string.IsNullOrWhiteSpace(builder.InitialCatalog));

        using var temp = new TempDirectory();
        await using var factory = new IntegrationSessionFactory(connectionString);
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["integration"] = new(
                builder.DataSource,
                builder.InitialCatalog,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "integrated"),
        };

        var module = new SqlHarnessModule(
            factory,
            new NullGainStore(),
            new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UtcNow),
            () => profiles);

        var operation = new SqlHarnessMeasureOperation(
            new SqlTargetRequest("integration", new Dictionary<string, string>()),
            "SELECT Id INTO #Req FROM sys.objects WHERE object_id = @Id;",
            "SELECT Id FROM #Req;",
            ["Id:int=1"],
            30,
            1);

        var outcome = await module.ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Contains("session-local temp table", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, factory.ConnectCount);
    }

    [SqlServerIntegrationFact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task Compare_matrix_setup_failure_partway_keeps_completed_cell_artifacts()
    {
        var connectionString = Environment.GetEnvironmentVariable(SqlServerIntegrationFactAttribute.Variable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.False(string.IsNullOrWhiteSpace(builder.DataSource));
        Assert.False(string.IsNullOrWhiteSpace(builder.InitialCatalog));

        using var temp = new TempDirectory();
        await using var factory = new IntegrationSessionFactory(connectionString);
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["integration"] = new(
                builder.DataSource,
                builder.InitialCatalog,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "integrated"),
        };

        var module = new SqlHarnessModule(
            factory,
            new NullGainStore(),
            new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UtcNow),
            () => profiles);

        var operation = new SqlHarnessCompareMatrixOperation(
            new SqlTargetRequest("integration", new Dictionary<string, string>()),
            """
            CREATE TABLE #Req (Id int NOT NULL PRIMARY KEY);
            INSERT #Req(Id) VALUES (@BatchSize);
            SELECT 1 / (CASE WHEN @BatchSize = 20 THEN 0 ELSE 1 END) AS Divider;
            """,
            "SELECT Id FROM #Req;",
            "SELECT Id FROM #Req;",
            [],
            30,
            1,
            "BatchSize:int=1,20");

        var outcome = await module.ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessCompareMatrixReport>(outcome.Report);
        Assert.Single(report.Cells);
        Assert.True(report.Cells[0].Compare.ResultsEquivalent);
        Assert.False(string.IsNullOrWhiteSpace(report.Cells[0].Compare.ArtifactDirectory));
        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(2, factory.DisposedSessionCount);
    }

    [SqlServerIntegrationFact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task Setup_cancellation_on_split_path_disposes_session_and_drops_temp()
    {
        var connectionString = Environment.GetEnvironmentVariable(SqlServerIntegrationFactAttribute.Variable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.False(string.IsNullOrWhiteSpace(builder.DataSource));
        Assert.False(string.IsNullOrWhiteSpace(builder.InitialCatalog));

        using var temp = new TempDirectory();
        await using var factory = new IntegrationSessionFactory(connectionString);
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["integration"] = new(
                builder.DataSource,
                builder.InitialCatalog,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "integrated"),
        };

        var module = new SqlHarnessModule(
            factory,
            new NullGainStore(),
            new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UtcNow),
            () => profiles);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000));
        var operation = new SqlHarnessMeasureOperation(
            new SqlTargetRequest("integration", new Dictionary<string, string>()),
            """
            CREATE TABLE #Cancel (Id int);
            SELECT COUNT(*)
            FROM sys.objects a
            CROSS JOIN sys.columns b
            CROSS JOIN sys.types c
            CROSS JOIN sys.objects d
            CROSS JOIN sys.columns e;
            INSERT #Cancel(Id) VALUES (@x);
            """,
            "SELECT Id FROM #Cancel;",
            ["x:int=1"],
            30,
            1);

        var outcome = await module.ExecuteAsync(operation, cts.Token);

        Assert.True(
            outcome.ExitCode == SqlHarnessExitCode.SqlExecution,
            $"Expected SqlExecution but got {outcome.ExitCode}. Error: {outcome.SafeError}");
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(1, factory.DisposedSessionCount);

        var verifyBuilder = new SqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
        };
        await using var verifyConnection = new SqlConnection(verifyBuilder.ConnectionString);
        await verifyConnection.OpenAsync();
        await using var verifyCommand = new SqlCommand("SELECT OBJECT_ID('tempdb..#Cancel')", verifyConnection);
        var objectId = await verifyCommand.ExecuteScalarAsync();
        Assert.Equal(DBNull.Value, objectId);
    }

    private sealed class IntegrationSessionFactory : ISqlSessionFactory, IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly List<int> _serverProcessIds = [];
        private readonly List<HeldSession> _heldSessions = [];

        public IntegrationSessionFactory(string connectionString)
        {
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                // Pooling would hand the next cell the same physical session.
                Pooling = false,
            };
            _connectionString = builder.ConnectionString;
        }

        public int ConnectCount { get; private set; }

        public IReadOnlyList<int> ServerProcessIds => _serverProcessIds;

        public int DisposedSessionCount { get; private set; }

        public async Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            ConnectCount++;
            var connection = new SqlConnection(_connectionString);
            var opened = false;
            try
            {
                var messages = new SessionMessageBuffer();
                SqlInfoMessageEventHandler handler = (_, args) => messages.Add(args.Message);
                connection.InfoMessage += handler;
                await connection.OpenAsync(ct);
                opened = true;
                _serverProcessIds.Add(connection.ServerProcessId);
                var session = new SqlClientSession(connection, messages, handler)
                {
                    Identity = new SqlHarnessTargetIdentityReport(
                        connection.DataSource,
                        connection.Database,
                        connection.DataSource,
                        connection.Database,
                        target.Mode),
                };
                var held = new HeldSession(session, () => DisposedSessionCount++);
                _heldSessions.Add(held);
                return held;
            }
            finally
            {
                if (!opened)
                    await connection.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var session in _heldSessions)
                await session.DisposeAsync();
            _heldSessions.Clear();
        }

        private sealed class HeldSession(SqlClientSession inner, Action onDisposed) : ISqlSession
        {
            private readonly List<TrackingReader> _readers = [];
            private int _disposed;

            public IReadOnlyList<string> Messages => inner.Messages;

            public SqlHarnessTargetIdentityReport Identity
            {
                get => inner.Identity;
                set => inner.Identity = value;
            }

            public async Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
            {
                var reader = await inner.ExecuteReaderAsync(command, ct);
                var tracking = new TrackingReader(reader);
                _readers.Add(tracking);
                return tracking;
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    foreach (var reader in _readers)
                    {
                        Assert.True(reader.IsDisposed, "A SQL reader was not disposed before its session.");
                    }

                    onDisposed();
                    return inner.DisposeAsync();
                }

                return ValueTask.CompletedTask;
            }
        }

        private sealed class TrackingReader(ISqlReader inner) : ISqlReader
        {
            public bool IsDisposed { get; private set; }

            public int FieldCount => inner.FieldCount;

            public int RecordsAffected => inner.RecordsAffected;

            public string GetName(int ordinal) => inner.GetName(ordinal);

            public Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);

            public bool GetAllowNull(int ordinal) => inner.GetAllowNull(ordinal);

            public object GetValue(int ordinal) => inner.GetValue(ordinal);

            public Task<bool> ReadAsync(CancellationToken ct) => inner.ReadAsync(ct);

            public Task<bool> NextResultAsync(CancellationToken ct) => inner.NextResultAsync(ct);

            public async ValueTask DisposeAsync()
            {
                IsDisposed = true;
                await inner.DisposeAsync();
            }
        }
    }

    private sealed class NullGainStore : IGainSource
    {
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sqlharness-integration-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of disposable artifact root.
            }
        }
    }
}