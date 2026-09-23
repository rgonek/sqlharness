using System.Text.Json;

using Microsoft.Data.SqlClient;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public sealed class QueryStoreTopTests
{
    private const string ProfileSecret = "tenant-qstop-secret-never-emit";
    private const string SecretSql = "SELECT Secret FROM dbo.T";

    [Fact]
    public async Task Qstop_executes_once_and_publishes_value_free_report()
    {
        var session = Fixture.Session(state: "READ_WRITE");
        var artifacts = new CapturingQueryStoreArtifactWriter("qstop-artifacts");
        var outcome = await Module(session, artifacts).ExecuteAsync(
            new SqlHarnessQueryStoreTopOperation(Target(), 20, 1440, 30));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessQueryStoreTopReport>(outcome.Report);
        Assert.Equal("qstop-artifacts", report.ArtifactDirectory);
        Assert.Single(session.Commands);
        Assert.Single(artifacts.Texts);
        Assert.DoesNotContain("SELECT Secret", JsonSerializer.Serialize(report),
            StringComparison.Ordinal);
        Assert.Equal(1, session.ConnectCalls);
        Assert.Same(session.Identity, report.Target);
        Assert.Equal(1440, report.WindowMinutes);
        Assert.Equal(20, report.Top);
        Assert.Equal(42L, Assert.Single(report.Queries).QueryId);
        Assert.Null(artifacts.Report!.ArtifactDirectory);
        Assert.Equal("db-acme", artifacts.Target);
        Assert.Equal(SecretSql, Assert.Single(artifacts.Texts).QuerySqlText);
        Assert.DoesNotContain("sys.query_store", JsonSerializer.Serialize(report), StringComparison.OrdinalIgnoreCase);

        var command = Assert.Single(session.Commands);
        Assert.Equal(QueryStoreTopQuery.Sql, command.Sql);
        Assert.Equal(30, command.TimeoutSeconds);
        Assert.Equal(QueryStoreTopQuery.Parameters(1440, 20), command.Parameters);
    }

    [Theory]
    [InlineData(0, 1440, 30)]
    [InlineData(501, 1440, 30)]
    [InlineData(-1, 1440, 30)]
    [InlineData(20, 0, 30)]
    [InlineData(20, 44641, 30)]
    [InlineData(20, -1, 30)]
    [InlineData(20, 1440, 0)]
    [InlineData(20, 1440, 301)]
    [InlineData(20, 1440, -1)]
    public async Task Qstop_rejects_invalid_bounds_without_connecting(int top, int windowMinutes, int timeoutSeconds)
    {
        var session = Fixture.Session();
        var artifacts = new CapturingQueryStoreArtifactWriter("qstop-artifacts");
        var outcome = await Module(session, artifacts).ExecuteAsync(
            new SqlHarnessQueryStoreTopOperation(Target(), top, windowMinutes, timeoutSeconds));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(0, session.ConnectCalls);
        Assert.Empty(session.Commands);
        Assert.Equal(0, artifacts.WriteCalls);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(500, 44640, 300)]
    public async Task Qstop_accepts_inclusive_bounds(int top, int windowMinutes, int timeoutSeconds)
    {
        var session = Fixture.Session();
        var artifacts = new CapturingQueryStoreArtifactWriter("qstop-artifacts");
        var outcome = await Module(session, artifacts).ExecuteAsync(
            new SqlHarnessQueryStoreTopOperation(Target(), top, windowMinutes, timeoutSeconds));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(1, session.ConnectCalls);
        var command = Assert.Single(session.Commands);
        Assert.Equal(timeoutSeconds, command.TimeoutSeconds);
        Assert.Equal(QueryStoreTopQuery.Parameters(windowMinutes, top), command.Parameters);
    }

    [Fact]
    public async Task Qstop_authentication_failure_returns_three_without_probe()
    {
        var session = Fixture.Session();
        session.ConnectFailure = new AzureCliException("not logged in");
        var artifacts = new CapturingQueryStoreArtifactWriter("qstop-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation());

        Assert.Equal(SqlHarnessExitCode.Authentication, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Empty(session.Commands);
        Assert.Equal(0, artifacts.WriteCalls);
    }

    [Fact]
    public async Task Qstop_target_mismatch_returns_four_without_probe()
    {
        var session = Fixture.Session();
        session.ConnectFailure = new SqlTargetMismatchException("mismatch");
        var artifacts = new CapturingQueryStoreArtifactWriter("qstop-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation());

        Assert.Equal(SqlHarnessExitCode.TargetMismatch, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Empty(session.Commands);
        Assert.Equal(0, artifacts.WriteCalls);
    }

    [Theory]
    [InlineData("OFF")]
    [InlineData("ERROR")]
    public async Task Qstop_unreadable_state_returns_five_without_an_artifact(string state)
    {
        var session = Fixture.Session(state: state);
        var artifacts = new CapturingQueryStoreArtifactWriter("qstop-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation());

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(0, artifacts.WriteCalls);
        Assert.Single(session.Commands);
        Assert.Contains($"Query Store is not readable: {state}.", outcome.SafeError, StringComparison.Ordinal);
        AssertNoQuerySql(outcome.SafeError);
    }

    [Fact]
    public async Task Qstop_permission_timeout_malformed_shape_and_sql_exception_return_five()
    {
        await AssertSqlPhaseFailure(Fixture.Failing(new UnauthorizedAccessException("VIEW DATABASE STATE permission denied")));
        await AssertSqlPhaseFailure(Fixture.Failing(new TimeoutException("query timeout")));
        await AssertSqlPhaseFailure(Fixture.Malformed());
        await AssertSqlPhaseFailure(Fixture.Failing(FakeSqlException()));
    }

    [Fact]
    public async Task Qstop_empty_readable_window_succeeds_and_writes_an_artifact()
    {
        var session = Fixture.Empty();
        var artifacts = new CapturingQueryStoreArtifactWriter("qstop-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation());

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessQueryStoreTopReport>(outcome.Report);
        Assert.Empty(report.Queries);
        Assert.Equal("qstop-artifacts", report.ArtifactDirectory);
        Assert.Empty(artifacts.Texts);
        Assert.Equal(1, artifacts.WriteCalls);
        Assert.Null(artifacts.Report!.ArtifactDirectory);
        Assert.Equal(1, session.ConnectCalls);
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task Qstop_writer_failure_returns_six_and_null_report()
    {
        var session = Fixture.Session();
        var artifacts = new ThrowingQueryStoreArtifactWriter();

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation());

        Assert.Equal(SqlHarnessExitCode.LocalStorage, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(1, artifacts.WriteCalls);
        Assert.DoesNotContain(SecretSql, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        AssertNoQuerySql(outcome.SafeError);
        Assert.Contains("[REDACTED]", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Qstop_caller_cancellation_returns_five()
    {
        using var cancellation = new CancellationTokenSource();
        var session = Fixture.Session();
        session.CancelOnExecute = cancellation;
        var artifacts = new CapturingQueryStoreArtifactWriter("qstop-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation(), cancellation.Token);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(0, artifacts.WriteCalls);
        Assert.Equal(1, session.ConnectCalls);
        Assert.Single(session.Commands);
        AssertNoQuerySql(outcome.SafeError);
    }

    [Fact]
    public async Task Qstop_redacts_secrets_and_omits_query_sql_from_safe_error()
    {
        var session = Fixture.Session();
        session.ExecuteFailure = new InvalidOperationException(
            $"failed {ProfileSecret} batch {QueryStoreTopQuery.Sql}");
        var artifacts = new CapturingQueryStoreArtifactWriter("qstop-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation(target: SecretTarget()));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.DoesNotContain(ProfileSecret, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        AssertNoQuerySql(outcome.SafeError);
        Assert.Equal(0, artifacts.WriteCalls);
    }

    [Fact]
    public async Task Qstop_postgres_does_not_execute_the_batch()
    {
        var session = Fixture.Session();
        var artifacts = new CapturingQueryStoreArtifactWriter("qstop-artifacts");
        var outcome = await Module(session, artifacts, profiles: PostgresProfiles).ExecuteAsync(
            new SqlHarnessQueryStoreTopOperation(
                new SqlTargetRequest("pg", new Dictionary<string, string>()),
                20,
                1440,
                30));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(1, session.ConnectCalls);
        Assert.Empty(session.Commands);
        Assert.Equal(0, artifacts.WriteCalls);
        Assert.Equal("Query Store is available only on SQL Server.", outcome.SafeError);
        AssertNoQuerySql(outcome.SafeError);
    }

    [Fact]
    public async Task Qstop_success_receipt_stores_qstop_and_nonzero_raw_footprint()
    {
        var session = Fixture.Session();
        var gain = new FakeGain();
        var artifacts = new CapturingQueryStoreArtifactWriter("qstop-artifacts");

        var outcome = await Module(session, artifacts, gain).ExecuteAsync(Operation());

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Empty(gain.Records);
        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(10, 1));
        var record = Assert.Single(gain.Records);
        Assert.Equal("qstop", record.Command);
        Assert.True(record.Success);
        Assert.True(record.RawBytes > 0);
        Assert.True(record.RawLines > 0);
    }

    [Fact]
    public async Task Qstop_gain_append_failure_completes_as_local_storage()
    {
        var session = Fixture.Session();
        var gain = new FakeGain(new IOException("disk full"));

        var outcome = await Module(session, new CapturingQueryStoreArtifactWriter("qstop-artifacts"), gain)
            .ExecuteAsync(Operation());

        var completion = await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(8, 1));

        Assert.Equal(SqlHarnessExitCode.LocalStorage, completion);
    }

    private static async Task AssertSqlPhaseFailure(FakeSession session)
    {
        var artifacts = new CapturingQueryStoreArtifactWriter("qstop-artifacts");
        var outcome = await Module(session, artifacts).ExecuteAsync(Operation());

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(0, artifacts.WriteCalls);
        Assert.Single(session.Commands);
        AssertNoQuerySql(outcome.SafeError);
    }

    private static void AssertNoQuerySql(string? safeError)
    {
        var error = safeError ?? string.Empty;
        Assert.DoesNotContain(QueryStoreTopQuery.Sql, error, StringComparison.Ordinal);
        Assert.DoesNotContain("sys.query_store", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretSql, error, StringComparison.Ordinal);
    }

    private static SqlHarnessQueryStoreTopOperation Operation(
        int top = 20,
        int windowMinutes = 1440,
        int timeoutSeconds = 30,
        SqlTargetRequest? target = null) =>
        new(target ?? Target(), top, windowMinutes, timeoutSeconds);

    private static SqlHarnessModule Module(
        FakeSession session,
        IQueryStoreArtifactWriter? artifacts = null,
        FakeGain? gain = null,
        Func<IReadOnlyDictionary<string, TargetProfile>>? profiles = null) =>
        new(session, gain ?? new FakeGain(), profiles ?? Profiles, queryStoreArtifacts: artifacts);

    private static SqlTargetRequest Target() =>
        new("test", new Dictionary<string, string> { ["tenant"] = "acme" });

    private static SqlTargetRequest SecretTarget() =>
        new("test", new Dictionary<string, string> { ["tenant"] = ProfileSecret });

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new(
                "server",
                "db-{tenant}",
                new Dictionary<string, string> { ["tenant"] = "^.+$" },
                "integrated"),
        };

    private static IReadOnlyDictionary<string, TargetProfile> PostgresProfiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["pg"] = new(
                "localhost",
                "appdb",
                new Dictionary<string, string>(),
                "sql",
                SqlUser: "sqlharness",
                PasswordEnvVar: "SQLHARNESS_PG_PASSWORD",
                TrustServerCertificate: true,
                Engine: "postgres"),
        };

    private static SqlException FakeSqlException() =>
        (SqlException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SqlException));

    private sealed class FakeGain(Exception? appendFailure = null) : IGainStore
    {
        public List<GainRecord> Records { get; } = [];

        public void Append(GainRecord record)
        {
            if (appendFailure is not null)
                throw appendFailure;
            Records.Add(record);
        }

        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class CapturingQueryStoreArtifactWriter(string directory) : IQueryStoreArtifactWriter
    {
        public List<SensitiveQueryStoreText> Texts { get; } = [];

        public SqlHarnessQueryStoreTopReport? Report { get; private set; }

        public string? Target { get; private set; }

        public int WriteCalls { get; private set; }

        public string Write(
            SqlHarnessQueryStoreTopReport report,
            IReadOnlyList<SensitiveQueryStoreText> texts,
            string target)
        {
            WriteCalls++;
            Report = report;
            Target = target;
            Texts.AddRange(texts);
            return directory;
        }
    }

    private sealed class ThrowingQueryStoreArtifactWriter : IQueryStoreArtifactWriter
    {
        public int WriteCalls { get; private set; }

        public string Write(
            SqlHarnessQueryStoreTopReport report,
            IReadOnlyList<SensitiveQueryStoreText> texts,
            string target)
        {
            WriteCalls++;
            var leaked = texts.Count == 0 ? string.Empty : texts[0].QuerySqlText;
            throw new IOException($"disk unavailable {leaked} {QueryStoreTopQuery.Sql}");
        }
    }

    private sealed class FakeSession(ISqlReader reader) : ISqlSessionFactory, ISqlSession
    {
        public Exception? ConnectFailure { get; set; }

        public Exception? ExecuteFailure { get; set; }

        public CancellationTokenSource? CancelOnExecute { get; set; }

        public int ConnectCalls { get; private set; }

        public List<SqlExecutionCommand> Commands { get; } = [];

        public IReadOnlyList<string> Messages => [];

        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("server", "db-acme", "server", "db-acme", "profile");

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            ConnectCalls++;
            return ConnectFailure is null
                ? Task.FromResult<ISqlSession>(this)
                : Task.FromException<ISqlSession>(ConnectFailure);
        }

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (CancelOnExecute is not null)
            {
                CancelOnExecute.Cancel();
                return Task.FromException<ISqlReader>(new OperationCanceledException(ct));
            }

            if (ExecuteFailure is not null)
                return Task.FromException<ISqlReader>(ExecuteFailure);
            return Task.FromResult(reader);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static class Fixture
    {
        private static readonly string[] StateColumns = ["actual_state_desc"];

        private static readonly string[] MetricColumns =
        [
            "query_id",
            "query_hash",
            "object_name",
            "execution_count",
            "plan_count",
            "total_duration_milliseconds",
            "average_duration_milliseconds",
            "maximum_duration_milliseconds",
            "total_cpu_milliseconds",
            "average_cpu_milliseconds",
            "maximum_cpu_milliseconds",
            "total_logical_reads",
            "average_logical_reads",
            "maximum_logical_reads",
            "last_execution_at",
        ];

        private static readonly string[] TextColumns = ["query_id", "query_hash", "query_sql_text"];

        public static FakeSession Session(string state = "READ_WRITE", string querySqlText = SecretSql) =>
            new(new FakeReader(StateSet(state), MetricSet(Metric()), TextSet(Text(sql: querySqlText))));

        public static FakeSession Empty(string state = "READ_WRITE") =>
            new(new FakeReader(StateSet(state), MetricSet(), TextSet()));

        public static FakeSession Malformed() =>
            new(new FakeReader(
                StateSet("READ_WRITE"),
                Set(["query_id", "query_hash"], [42, "A1B2"]),
                TextSet(Text())));

        public static FakeSession Failing(Exception failure)
        {
            var session = Session();
            session.ExecuteFailure = failure;
            return session;
        }

        private static object?[][] StateSet(params object?[] values)
        {
            var rows = new object?[values.Length][];
            for (var i = 0; i < values.Length; i++)
                rows[i] = [values[i]];
            return Set(StateColumns, rows);
        }

        private static object?[][] MetricSet(params object?[][] rows) => Set(MetricColumns, rows);

        private static object?[][] TextSet(params object?[][] rows) => Set(TextColumns, rows);

        private static object?[] Metric() =>
        [
            42,
            "A1B2",
            "dbo.T",
            4,
            1,
            10.5d,
            2.5d,
            8.0d,
            6.5d,
            1.5d,
            4.0d,
            100.5d,
            25.25d,
            80L,
            new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero),
        ];

        private static object?[] Text(object? queryId = null, object? queryHash = null, object? sql = null) =>
        [
            queryId ?? 42,
            queryHash ?? "A1B2",
            sql ?? SecretSql,
        ];

        private static object?[][] Set(string[] names, params object?[][] rows) => [[.. names], .. rows];
    }

    private sealed class FakeReader(params object?[][][] sets) : ISqlReader
    {
        private int _set;
        private int _row;

        private string[] Names => sets[_set][0].Cast<string>().ToArray();

        private object?[][] Rows => sets[_set].Skip(1).ToArray();

        public int FieldCount => Names.Length;

        public int RecordsAffected => -1;

        public string GetName(int ordinal) => Names[ordinal];

        public Type GetFieldType(int ordinal) => typeof(object);

        public bool GetAllowNull(int ordinal) => true;

        public object GetValue(int ordinal) => Rows[_row - 1][ordinal] ?? DBNull.Value;

        public Task<bool> ReadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_row < Rows.Length)
            {
                _row++;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (++_set < sets.Length)
            {
                _row = 0;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}