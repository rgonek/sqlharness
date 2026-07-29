using Microsoft.Data.SqlClient;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public sealed class PingTests
{
    private const string ProfileSecret = "tenant-secret-never-emit";
    private const string PasswordSecret = "Password=hunter2-never-emit";

    [Fact]
    public async Task Ping_runs_one_fixed_probe_after_identity_verification()
    {
        var session = new FakeSession(Row(1, "db", "server", "login"));
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessPingOperation(Target(), 5));

        var report = Assert.IsType<SqlHarnessPingReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal("db", report.Database);
        Assert.Equal("login", report.Login);
        Assert.Equal("server", report.Server);
        Assert.Same(session.Identity, report.Target);
        Assert.True(report.DurationMilliseconds >= 0);
        Assert.Equal(PingQuery.Sql, Assert.Single(session.Commands).Sql);
        Assert.Empty(Assert.Single(session.Commands).Parameters);
        Assert.Equal(5, Assert.Single(session.Commands).TimeoutSeconds);
    }

    [Fact]
    public async Task Ping_authentication_failure_maps_to_three_without_probe()
    {
        var session = new FakeSession(Row(1, "db", "server", "login"))
        {
            ConnectFailure = new AzureCliException("not logged in"),
        };

        var outcome = await Module(session).ExecuteAsync(new SqlHarnessPingOperation(Target(), 5));

        Assert.Equal(SqlHarnessExitCode.Authentication, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Ping_target_mismatch_maps_to_four_without_probe()
    {
        var session = new FakeSession(Row(1, "db", "server", "login"))
        {
            ConnectFailure = new SqlTargetMismatchException("mismatch"),
        };

        var outcome = await Module(session).ExecuteAsync(new SqlHarnessPingOperation(Target(), 5));

        Assert.Equal(SqlHarnessExitCode.TargetMismatch, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Ping_sql_failure_maps_to_five()
    {
        var session = new FakeSession(Row(1, "db", "server", "login"))
        {
            ExecuteFailure = FakeSqlException(),
        };

        var outcome = await Module(session).ExecuteAsync(new SqlHarnessPingOperation(Target(), 5));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(PingQuery.Sql, Assert.Single(session.Commands).Sql);
    }

    [Fact]
    public async Task Ping_network_timeout_maps_to_five()
    {
        var session = new FakeSession(Row(1, "db", "server", "login"))
        {
            ExecuteFailure = new TimeoutException("network timeout"),
        };

        var outcome = await Module(session).ExecuteAsync(new SqlHarnessPingOperation(Target(), 5));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
    }

    [Theory]
    [MemberData(nameof(MalformedProbeCases))]
    public async Task Ping_malformed_probe_maps_to_five(FakeReader reader)
    {
        var session = new FakeSession(reader);

        var outcome = await Module(session).ExecuteAsync(new SqlHarnessPingOperation(Target(), 5));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(PingQuery.Sql, Assert.Single(session.Commands).Sql);
    }

    public static TheoryData<FakeReader> MalformedProbeCases() =>
    [
        // Wrong column count
        new FakeReader(Set(["Ok", "Db", "Server"], [1, "db", "server"])),
        // Empty result
        new FakeReader(Set(["Ok", "Db", "Server", "Login"])),
        // Ok != 1
        new FakeReader(Set(["Ok", "Db", "Server", "Login"], [0, "db", "server", "login"])),
        // Extra rows
        new FakeReader(Set(
            ["Ok", "Db", "Server", "Login"],
            [1, "db", "server", "login"],
            [1, "db2", "server2", "login2"])),
    ];

    [Fact]
    public async Task Ping_cancellation_during_probe_maps_to_sql_execution()
    {
        using var cts = new CancellationTokenSource();
        var session = new FakeSession(new CancelOnReadReader(cts));

        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessPingOperation(Target(), 5),
            cts.Token);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(PingQuery.Sql, Assert.Single(session.Commands).Sql);
    }

    [Fact]
    public async Task Ping_sql_and_errors_never_include_profile_variables_or_passwords()
    {
        Assert.DoesNotContain(ProfileSecret, PingQuery.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", PingQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@filter", PingQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@tables", PingQuery.Sql, StringComparison.OrdinalIgnoreCase);

        var session = new FakeSession(Row(1, "db", "server", "login"))
        {
            ExecuteFailure = new InvalidOperationException(
                $"Login failed {PasswordSecret}; var={ProfileSecret}"),
        };
        var target = new SqlTargetRequest(
            "test",
            new Dictionary<string, string> { ["tenant"] = ProfileSecret });

        var outcome = await Module(session).ExecuteAsync(new SqlHarnessPingOperation(target, 5));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.DoesNotContain(ProfileSecret, PingQuery.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(ProfileSecret, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(PasswordSecret, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(Assert.Single(session.Commands).Parameters);
    }

    [Fact]
    public async Task Ping_rejects_out_of_range_timeout_as_safety()
    {
        var session = new FakeSession(Row(1, "db", "server", "login"));

        var outcome = await Module(session).ExecuteAsync(new SqlHarnessPingOperation(Target(), 0));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Ping_records_gain_with_ping_command()
    {
        var session = new FakeSession(Row(1, "db", "server", "login"));
        var gain = new FakeGain();

        var outcome = await Module(session, gain).ExecuteAsync(new SqlHarnessPingOperation(Target(), 5));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(10, 1));
        Assert.Equal("ping", Assert.Single(gain.Records).Command);
    }

    private static SqlHarnessModule Module(FakeSession session, FakeGain? gain = null) =>
        new(session, gain ?? new FakeGain(), Profiles);

    private static FakeReader Row(int ok, string database, string server, string login) =>
        new(Set(["Ok", "Db", "Server", "Login"], [ok, database, server, login]));

    private static object?[][] Set(string[] names, params object?[][] rows) => [[.. names], .. rows];

    private static SqlTargetRequest Target() =>
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

    private static SqlException FakeSqlException() =>
        (SqlException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SqlException));

    private sealed class FakeGain : IGainStore
    {
        public List<GainRecord> Records { get; } = [];
        public void Append(GainRecord record) => Records.Add(record);
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class FakeSession(ISqlReader reader) : ISqlSessionFactory, ISqlSession
    {
        public Exception? ConnectFailure { get; init; }
        public Exception? ExecuteFailure { get; init; }
        public List<SqlExecutionCommand> Commands { get; } = [];
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("server", "db", "server", "db", "profile");

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct) =>
            ConnectFailure is null
                ? Task.FromResult<ISqlSession>(this)
                : Task.FromException<ISqlSession>(ConnectFailure);

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (ExecuteFailure is not null)
                return Task.FromException<ISqlReader>(ExecuteFailure);
            return Task.FromResult(reader);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public sealed class FakeReader(params object?[][][] sets) : ISqlReader
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
            if (_row < Rows.Length)
            {
                _row++;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            if (++_set < sets.Length)
            {
                _row = 0;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancelOnReadReader(CancellationTokenSource cts) : ISqlReader
    {
        public int FieldCount => 4;
        public int RecordsAffected => -1;
        public string GetName(int ordinal) => ordinal switch
        {
            0 => "Ok",
            1 => "Db",
            2 => "Server",
            _ => "Login",
        };
        public Type GetFieldType(int ordinal) => typeof(object);
        public bool GetAllowNull(int ordinal) => false;
        public object GetValue(int ordinal) => throw new InvalidOperationException("unreachable");

        public Task<bool> ReadAsync(CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }

        public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}