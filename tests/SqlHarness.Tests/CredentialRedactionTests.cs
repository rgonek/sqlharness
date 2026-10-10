using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

// The SQL password is the only operator-held value the caller cannot obtain, so it is registered
// for redaction after every executing target resolution, at any length and without a prefix.
public sealed class CredentialRedactionTests
{
    private const string EnvVar = "SQLHARNESS_TEST_REDACTION_PASSWORD";
    private const string Password = "p@ss-W0rd!";

    public static TheoryData<string> Operations() =>
    [
        "query", "measure", "compare", "compare-matrix", "watch", "snapshot",
        "schema", "ping", "counts", "space", "qstop", "indexes",
    ];

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Sql_server_profile_password_is_redacted_for_every_operation(string operation)
    {
        var outcome = await RunAsync(operation, "sql", engine: null, Password);

        AssertRedacted(outcome, Password);
    }

    [Fact]
    public async Task One_character_password_is_still_redacted()
    {
        var outcome = await RunAsync("query", "sql", engine: null, "~");

        AssertRedacted(outcome, "~");
    }

    [Theory]
    [InlineData("query")]
    [InlineData("ping")]
    [InlineData("counts")]
    [InlineData("space")]
    public async Task Postgres_profile_password_is_redacted(string operation)
    {
        var outcome = await RunAsync(operation, "sql", engine: "postgres", Password);

        AssertRedacted(outcome, Password);
    }

    [Theory]
    [InlineData("integrated")]
    [InlineData("azure-cli")]
    [InlineData("ad-default")]
    public void Non_sql_auth_registers_nothing(string auth)
    {
        var target = Resolve(auth, engine: null);
        var secrets = new List<string>();

        WithPassword(Password, () => OperationSecrets.AddCredentials(secrets, target));

        Assert.Empty(secrets);
    }

    [Fact]
    public void Sql_auth_with_unset_password_registers_nothing()
    {
        var target = Resolve("sql", engine: null);
        var secrets = new List<string>();

        WithPassword(null, () => OperationSecrets.AddCredentials(secrets, target));

        Assert.Empty(secrets);
    }

    private static void AssertRedacted(SqlHarnessOutcome outcome, string password)
    {
        Assert.NotEqual(SqlHarnessExitCode.Success, outcome.ExitCode);
        var message = outcome.SafeError ?? string.Empty;
        Assert.DoesNotContain(password, message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", message, StringComparison.Ordinal);
    }

    private static async Task<SqlHarnessOutcome> RunAsync(
        string operation, string auth, string? engine, string password)
    {
        var session = new ThrowingSession(new InvalidOperationException($"boom {password} end"));
        var module = new SqlHarnessModule(session, new NoGain(), () => Profiles(auth, engine));
        var target = new SqlTargetRequest("p", new Dictionary<string, string>());
        const string sql = "SELECT 1 AS One";

        SqlHarnessOperation op = operation switch
        {
            "query" => new SqlHarnessQueryOperation(target, sql, [], 5, 10, false, null),
            "measure" => new SqlHarnessMeasureOperation(target, null, sql, [], 5, 2),
            "compare" => new SqlHarnessCompareOperation(target, null, sql, sql, [], 5, 2),
            "compare-matrix" => new SqlHarnessCompareMatrixOperation(
                target, null, "SELECT @N AS One", "SELECT @N AS One", [], 5, 2, "N:int=1,2"),
            "watch" => new SqlHarnessWatchOperation(
                target, sql, [], 5, 10, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), null, 3),
            "snapshot" => new SqlHarnessSnapshotOperation(target, sql, [], 5, 10, "redaction-test", false, false),
            "schema" => new SqlHarnessSchemaOperation(target, null, 5),
            "ping" => new SqlHarnessPingOperation(target, 5),
            "counts" => new SqlHarnessCountsOperation(target, [], null, 10, false, 5),
            "space" => new SqlHarnessSpaceOperation(target, 10, null, 5),
            "qstop" => new SqlHarnessQueryStoreTopOperation(target, 10, 60, 5),
            "indexes" => new SqlHarnessIndexesOperation(target, 10, null, 5),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        SqlHarnessOutcome? outcome = null;
        var previous = Environment.GetEnvironmentVariable(EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, password);
            outcome = await module.ExecuteAsync(op);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, previous);
        }

        return outcome;
    }

    private static ResolvedTarget Resolve(string auth, string? engine) =>
        TargetResolver.Resolve(
            new SqlTargetRequest("p", new Dictionary<string, string>()),
            Profiles(auth, engine));

    private static void WithPassword(string? value, Action action)
    {
        var previous = Environment.GetEnvironmentVariable(EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, previous);
        }
    }

    private static IReadOnlyDictionary<string, TargetProfile> Profiles(string auth, string? engine) =>
        new Dictionary<string, TargetProfile>
        {
            ["p"] = new(
                "server",
                "db",
                new Dictionary<string, string>(),
                auth,
                SqlUser: auth == "sql" ? "sqlharness" : null,
                PasswordEnvVar: auth == "sql" ? EnvVar : null,
                TrustServerCertificate: true,
                Engine: engine),
        };

    private sealed class NoGain : IGainSource
    {
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class ThrowingSession(Exception failure) : ISqlSessionFactory, ISqlSession
    {
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } = new("server", "db", "server", "db", "profile");

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct) =>
            Task.FromResult<ISqlSession>(this);

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct) =>
            Task.FromException<ISqlReader>(failure);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
