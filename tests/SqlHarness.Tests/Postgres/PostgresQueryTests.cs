using System.Data;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Postgres;

public sealed class PostgresQueryTests
{
    [Fact]
    public async Task Postgres_temp_batch_succeeds_with_user_sql_unchanged()
    {
        const string sql = """
            CREATE TEMP TABLE t (id int);
            INSERT INTO t VALUES (1);
            SELECT id FROM t;
            """;
        var session = FakeSession.WithIdentity("localhost", "appdb", FakeReader.Rows(["id"], [1]));
        var module = new SqlHarnessModule(session, new FakeGain(), Profiles);

        var outcome = await module.ExecuteAsync(new SqlHarnessQueryOperation(
            new SqlTargetRequest("local-pg", new Dictionary<string, string>()),
            sql,
            [],
            30,
            50,
            false,
            null));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(sql, Assert.Single(session.Commands).Sql);
        Assert.Equal("session-local", Assert.IsType<SqlHarnessQueryReport>(outcome.Report).StatementClassification);
    }

    // 011/final (M1): PostgreSQL TRUNCATE of a proven session temp through the
    // module entry point that `query` uses, in both accepted spellings.
    [Theory]
    [InlineData("CREATE TEMP TABLE t (id int); INSERT INTO t VALUES (1); TRUNCATE t; SELECT id FROM t")]
    [InlineData("CREATE TEMP TABLE t (id int); INSERT INTO t VALUES (1); TRUNCATE ONLY pg_temp.t; SELECT id FROM pg_temp.t")]
    public async Task Postgres_truncate_of_a_proven_temp_runs_unchanged_without_mutation_approval(string sql)
    {
        var session = FakeSession.WithIdentity("localhost", "appdb", FakeReader.Rows(["id"]));
        var module = new SqlHarnessModule(session, new FakeGain(), Profiles);

        var outcome = await module.ExecuteAsync(Query(sql));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(sql, Assert.Single(session.Commands).Sql);
        Assert.Equal("session-local", Assert.IsType<SqlHarnessQueryReport>(outcome.Report).StatementClassification);
    }

    [Theory]
    [InlineData("TRUNCATE items", "NonTemporaryWrite.")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE t, items", "NonTemporaryWrite.")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE public.t", "NonTemporaryWrite.")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp_3.t", "NonTemporaryWrite.")]
    [InlineData("TRUNCATE pg_temp.t", "NonTemporaryWrite.")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE t CASCADE", "UnsupportedStatement.")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.t RESTART IDENTITY", "UnsupportedStatement.")]
    public async Task Postgres_truncate_of_an_unproven_target_is_rejected_before_connect(string sql, string reason)
    {
        var session = FakeSession.WithIdentity("localhost", "appdb", FakeReader.Rows(["id"]));
        var module = new SqlHarnessModule(session, new FakeGain(), Profiles);

        // Mutation approval does not unlock a persistent TRUNCATE either.
        foreach (var approve in new[] { false, true })
        {
            var outcome = await module.ExecuteAsync(Query(sql) with
            {
                AllowMutation = approve,
                ConfirmDatabase = approve ? "appdb" : null,
            });

            Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
            Assert.Equal($"SQL safety rejection: {reason}", outcome.SafeError);
            Assert.Empty(session.Commands);
            Assert.Null(outcome.Report);
        }
    }

    [Fact]
    public async Task Postgres_unproven_temp_write_is_rejected_with_the_hint_and_no_sql_echo()
    {
        // 011/final (M9) end to end: exit code 2 and the reason are unchanged.
        const string sql = "CREATE TEMP TABLE scratch_rows (id int) ON COMMIT DROP; INSERT INTO scratch_rows VALUES (1)";
        var session = FakeSession.WithIdentity("localhost", "appdb", FakeReader.Rows(["id"]));
        var module = new SqlHarnessModule(session, new FakeGain(), Profiles);

        var outcome = await module.ExecuteAsync(Query(sql));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.StartsWith("SQL safety rejection: MutationNotAllowed. A TEMP table declared in this session flow is not a proven session temp", outcome.SafeError, StringComparison.Ordinal);
        Assert.DoesNotContain("scratch_rows", outcome.SafeError, StringComparison.Ordinal);
        Assert.Empty(session.Commands);
    }

    private static SqlHarnessQueryOperation Query(string sql) =>
        new(new SqlTargetRequest("local-pg", new Dictionary<string, string>()), sql, [], 30, 50, false, null);

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

    private sealed class FakeGain : IGainSource
    {
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class FakeSession : ISqlSessionFactory, ISqlSession
    {
        private readonly Queue<Func<ISqlReader>> _results;

        private FakeSession(IEnumerable<Func<ISqlReader>> results) => _results = new(results);

        public List<SqlExecutionCommand> Commands { get; } = [];
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } = null!;

        public static FakeSession WithIdentity(string server, string database, FakeReader reader) =>
            new([() => reader])
            {
                Identity = new(server, database, server, database, "profile", Engine: "postgres"),
            };

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            if (!string.Equals(target.Database, Identity.ActualDatabase, StringComparison.Ordinal))
                throw new SqlTargetMismatchException("Connected SQL target identity does not match the resolved target.");
            return Task.FromResult<ISqlSession>(this);
        }

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(_results.Dequeue()());
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