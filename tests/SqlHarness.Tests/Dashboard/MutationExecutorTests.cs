using Npgsql;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Journal;
using SqlHarness.Core.Mutations;
using SqlHarness.Core.Targets;
using SqlHarness.Dashboard.Mutations;

namespace SqlHarness.Tests.Dashboard;

public sealed class MutationExecutorTests
{
    [Fact]
    public async Task Mode_changed_to_never_rejects_without_module_execution()
    {
        var module = new FakeModule();
        var executor = Executor(module, Profile("never"));

        var result = await executor.ExecuteAsync(Request(), Context(), default);

        Assert.Equal(MutationStatus.Disabled, result.Status);
        Assert.Empty(module.Operations);
    }

    [Fact]
    public async Task Changed_target_identity_is_rejected_before_database_access()
    {
        var module = new FakeModule();
        var executor = Executor(module, Profile("operator") with { Server = "other-server" });

        var result = await executor.ExecuteAsync(Request(), Context(), default);

        Assert.Equal(MutationStatus.DatabaseMismatch, result.Status);
        Assert.Empty(module.Operations);
    }

    [Fact]
    public async Task Privilege_grant_to_reader_principal_is_rejected_before_mutation()
    {
        var module = new FakeModule(operation => operation is SqlHarnessQueryOperation
            ? PrincipalOutcome("reader-login", "reader-user")
            : throw new InvalidOperationException("Mutation execution should not be reached."));
        var executor = Executor(module, Profile("operator"));

        var result = await executor.ExecuteAsync(Request(tier: "privilege", grantees: ["READER-USER"]), Context(), default);

        Assert.Equal(MutationStatus.PrincipalProtected, result.Status);
        Assert.Single(module.Operations);
        var principalQuery = Assert.IsType<SqlHarnessQueryOperation>(module.Operations[0]);
        Assert.Equal("SELECT SUSER_SNAME() AS l, USER_NAME() AS u", principalQuery.Sql);
    }

    [Fact]
    public async Task Reader_principal_query_failure_is_unavailable_and_stops_execution()
    {
        var module = new FakeModule(_ => new SqlHarnessOutcome(SqlHarnessExitCode.Authentication, null, "connect failed"));
        var executor = Executor(module, Profile("operator"));

        var result = await executor.ExecuteAsync(Request(tier: "privilege", grantees: ["other-user"]), Context(), default);

        Assert.Equal(MutationStatus.Unavailable, result.Status);
        Assert.Equal("Could not resolve the reader principal", result.Message);
        Assert.Single(module.Operations);
    }

    [Fact]
    public async Task Missing_writer_environment_variable_fails_without_exposing_a_value()
    {
        const string variable = "SQLHARNESS_TEST_MISSING_PASSWORD";
        Environment.SetEnvironmentVariable(variable, null);
        var module = new FakeModule();
        var executor = Executor(module, Profile("operator", new MutationWriter("writer", variable)));

        var result = await executor.ExecuteAsync(Request(), Context(), default);

        Assert.Equal(MutationStatus.Failed, result.Status);
        Assert.Contains(variable, result.Message);
        Assert.Empty(module.Operations);
    }

    [Fact]
    public async Task Connection_loss_after_dispatch_is_outcome_unknown_and_not_retried()
    {
        const string variable = "SQLHARNESS_TEST_UNKNOWN_PASSWORD";
        Environment.SetEnvironmentVariable(variable, "writer-secret");
        var module = new FakeModule(operation => operation is SqlHarnessMutationOperation
            ? throw new NpgsqlException("connection lost after command send")
            : throw new InvalidOperationException("Reader principal should not be resolved for DML."));
        var executor = Executor(module, Profile("operator", new MutationWriter("writer", variable)));

        var result = await executor.ExecuteAsync(Request(), Context("request-unknown"), default);

        Assert.Equal(MutationStatus.OutcomeUnknown, result.Status);
        Assert.Single(module.Operations);
        Assert.DoesNotContain("writer-secret", result.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(module.Operations.OfType<SqlHarnessMutationOperation>(), operation => operation.Approval is not null);
        Assert.Equal("writer", Assert.Single(module.Operations.OfType<SqlHarnessMutationOperation>()).Approval!.Writer.SqlUser);
        Environment.SetEnvironmentVariable(variable, null);
    }

    [Fact]
    public async Task Successful_execution_updates_the_exact_journal_row_to_succeeded()
    {
        const string variable = "SQLHARNESS_TEST_SUCCESS_PASSWORD";
        Environment.SetEnvironmentVariable(variable, "writer-secret");
        var journal = new RecordingJournal();
        var module = new FakeModule(operation => operation is SqlHarnessMutationOperation
            ? new SqlHarnessOutcome(SqlHarnessExitCode.Success, QueryReport([["writer", "writer-secret"]]), null)
            : throw new InvalidOperationException("Reader principal should not be resolved for DML."));
        var executor = Executor(module, Profile("operator", new MutationWriter("writer", variable)), journal);

        var result = await executor.ExecuteAsync(Request(), Context("request-success"), default);

        Assert.Equal(MutationStatus.Success, result.Status);
        Assert.Equal(("request-success", 0), Assert.Single(journal.Results));
        Assert.Equal(new[] { "executing", "succeeded" }, journal.Updates
            .Where(update => update.RequestId == "request-success")
            .Select(update => update.State));
        Assert.Contains(journal.Updates, update => update.RequestId == "request-success" && update.State == "succeeded");
        Assert.DoesNotContain("writer-secret", result.Message ?? string.Empty, StringComparison.Ordinal);
        var executed = Assert.Single(module.Operations.OfType<SqlHarnessMutationOperation>());
        Assert.Equal(Request().Digest, executed.Approval!.Digest);
        Assert.Equal("UPDATE dbo.T SET Value = 1", executed.Sql);
        Assert.Equal("session", executed.SessionKey);
        Assert.Equal("writer", executed.Approval.Writer.SqlUser);
        var resultReport = Assert.IsType<SqlHarnessQueryReport>(result.Result);
        Assert.Equal("[REDACTED]", resultReport.ResultSets[0].Rows[0][0]);
        Assert.Equal("[REDACTED]", resultReport.ResultSets[0].Rows[0][1]);
        Environment.SetEnvironmentVariable(variable, null);
    }

    [Fact]
    public async Task Matching_prepared_digest_executes_frozen_sql_through_core_with_writer_target()
    {
        const string variable = "SQLHARNESS_TEST_APPROVED_PATH_PASSWORD";
        Environment.SetEnvironmentVariable(variable, "writer-secret");
        var profile = Profile("operator", new MutationWriter("writer", variable));
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal) { ["local"] = profile };
        var sessions = new RecordingMutationSessionFactory();
        var module = new SqlHarnessModule(sessions, () => profiles);
        var request = Request();
        var prepared = await module.ExecuteAsync(new SqlHarnessMutationOperation(
            new SqlTargetRequest(request.Profile, request.Vars), request.Sql, request.Parameters,
            request.TimeoutSeconds, request.MaxRows, request.Database)
        { SessionKey = request.SessionKey });
        var frozen = Assert.IsType<SqlHarnessMutationPrepared>(prepared.Report).Request;
        var executor = new MutationExecutor(module, () => profiles, () => new SqlHarnessConfig(),
            new RecordingJournal(), TimeProvider.System);

        try
        {
            var result = await executor.ExecuteAsync(frozen, Context("request-core-approved"), default);

            Assert.Equal(MutationStatus.Success, result.Status);
            Assert.Equal("writer", sessions.Target!.Auth.SqlUser);
            Assert.Contains(sessions.Commands, command => command.Sql == frozen.Sql);
            Assert.Equal(frozen.Digest, Request().Digest);
            Assert.Equal(frozen.SessionKey, request.SessionKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task Automatic_non_dml_execution_is_disabled_but_operator_approved_privilege_is_allowed()
    {
        const string variable = "SQLHARNESS_TEST_ORIGIN_PASSWORD";
        Environment.SetEnvironmentVariable(variable, "writer-secret");
        var module = new FakeModule(operation => operation switch
        {
            SqlHarnessQueryOperation => PrincipalOutcome("reader-login", "reader-user"),
            SqlHarnessMutationOperation => new SqlHarnessOutcome(SqlHarnessExitCode.Success, QueryReport(), null),
            _ => throw new InvalidOperationException(),
        });
        var executor = Executor(module, Profile("yolo", new MutationWriter("writer", variable)));

        var automatic = await executor.ExecuteAsync(Request(tier: "schema"), Context(origin: MutationExecutionOrigin.Automatic), default);
        var operatorApproved = await executor.ExecuteAsync(Request(tier: "privilege", grantees: ["other-user"]), Context("request-operator", MutationExecutionOrigin.Operator), default);

        Assert.Equal(MutationStatus.Disabled, automatic.Status);
        Assert.Equal(MutationStatus.Success, operatorApproved.Status);
        Assert.Single(module.Operations.OfType<SqlHarnessMutationOperation>());
        Environment.SetEnvironmentVariable(variable, null);
    }

    [Fact]
    public async Task Automatic_dml_is_rejected_if_mode_changed_from_yolo_to_operator()
    {
        const string variable = "SQLHARNESS_TEST_AUTO_PASSWORD";
        Environment.SetEnvironmentVariable(variable, "writer-secret");
        var module = new FakeModule();
        var executor = Executor(module, Profile("operator", new MutationWriter("writer", variable)));

        var result = await executor.ExecuteAsync(
            Request(), Context(origin: MutationExecutionOrigin.Automatic), default);

        Assert.Equal(MutationStatus.Disabled, result.Status);
        Assert.Empty(module.Operations);
        Environment.SetEnvironmentVariable(variable, null);
    }

    [Fact]
    public async Task Same_database_server_change_between_executor_and_core_resolution_never_connects()
    {
        var original = new TargetProfile(
            "server", "db", new Dictionary<string, string>(), "integrated",
            Mutations: new ProfileMutations("operator", new MutationWriter(SameAsReader: true)));
        var changed = original with { Server = "changed-server" };
        var reads = 0;
        IReadOnlyDictionary<string, TargetProfile> LoadProfiles() =>
            new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                ["local"] = Interlocked.Increment(ref reads) == 1 ? original : changed,
            };
        var sessions = new TrackingSessionFactory();
        var module = new SqlHarnessModule(sessions, LoadProfiles);
        var executor = new MutationExecutor(module, LoadProfiles,
            () => new SqlHarnessConfig(), new RecordingJournal(), TimeProvider.System);

        var result = await executor.ExecuteAsync(Request(), Context("server-drift"), default);

        Assert.Equal(MutationStatus.Failed, result.Status);
        Assert.Equal(2, reads);
        Assert.Empty(sessions.ConnectedTargets);
    }

    private static MutationExecutor Executor(FakeModule module, TargetProfile profile, IActivityJournal? journal = null) =>
        new(module, () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal) { ["local"] = profile },
            () => new SqlHarnessConfig(), journal ?? new RecordingJournal(), TimeProvider.System);

    private static TargetProfile Profile(string mode, MutationWriter? writer = null) => new(
        "server", "db", new Dictionary<string, string>(), "sql", "reader", "SQLHARNESS_TEST_READER_PASSWORD",
        Mutations: new ProfileMutations(mode, writer ?? new MutationWriter("writer", "SQLHARNESS_TEST_MUTATION_PASSWORD")));

    private static MutationRequest Request(string tier = "dml", IReadOnlyList<string>? grantees = null) => new(
        "local", new Dictionary<string, string>(), "sqlserver", "server", "db", "UPDATE dbo.T SET Value = 1",
        Array.Empty<SqlHarnessParameterInput>(), 30, 10, tier, grantees ?? Array.Empty<string>(), "session");

    private static MutationExecutionContext Context(string requestId = "request-test", MutationExecutionOrigin origin = MutationExecutionOrigin.Operator) =>
        new(requestId, origin);

    private static SqlHarnessOutcome PrincipalOutcome(string login, string user) =>
        new(SqlHarnessExitCode.Success, QueryReport([[login, user]]), null);

    private static SqlHarnessQueryReport QueryReport(IReadOnlyList<IReadOnlyList<object?>>? rows = null) => new(
        new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile", "sqlserver"),
        "query",
        [new SqlHarnessResultSetReport(
            [new SqlHarnessColumnReport(0, "l", "nvarchar", false), new SqlHarnessColumnReport(1, "u", "nvarchar", false)],
            rows ?? Array.Empty<IReadOnlyList<object?>>(), 1, 0)],
        Array.Empty<string>(), 0, 1, "hash", new OutputFootprint(0, 0));

    private sealed class FakeModule(Func<SqlHarnessOperation, SqlHarnessOutcome>? execute = null) : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            return Task.FromResult(execute?.Invoke(operation) ?? new SqlHarnessOutcome(SqlHarnessExitCode.Success, QueryReport(), null));
        }
    }

    private sealed class RecordingJournal : IActivityJournal
    {
        public List<(string RequestId, string State, string? Status)> Updates { get; } = [];
        public List<(string RequestId, int Records)> Results { get; } = [];
        public void RecordMutationResult(string requestId, int recordsAffected) => Results.Add((requestId, recordsAffected));

        public JournalHandle? Begin(SessionIdentity session, OperationStart start) => null;
        public bool Complete(JournalHandle? handle, OperationEnd end) => false;
        public void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted) { }
        public void UpdateMutation(string requestId, string state, string? status, string? verification, DateTimeOffset at) =>
            Updates.Add((requestId, state, status));
    }

    private sealed class TrackingSessionFactory : ISqlSessionFactory
    {
        public List<string> ConnectedTargets { get; } = [];

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            ConnectedTargets.Add(target.Server);
            throw new InvalidOperationException("The test must reject target drift before connecting.");
        }
    }

    private sealed class RecordingMutationSessionFactory : ISqlSessionFactory
    {
        public ResolvedTarget? Target { get; private set; }
        public List<SqlExecutionCommand> Commands { get; } = [];

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            Target = target;
            return Task.FromResult<ISqlSession>(new RecordingMutationSession(target, Commands));
        }

        private sealed class RecordingMutationSession(ResolvedTarget target, List<SqlExecutionCommand> commands) : ISqlSession
        {
            public IReadOnlyList<SessionMessage> Messages => [];
            public SqlHarnessTargetIdentityReport Identity { get; set; } = new(target.Server, target.Database,
                target.Server, target.Database, "local", SqlEngineNames.Format(target.Engine));

            public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
            {
                commands.Add(command);
                return Task.FromResult<ISqlReader>(new EmptyMutationReader());
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class EmptyMutationReader : ISqlReader
        {
            public int FieldCount => 0;
            public int RecordsAffected => 1;
            public string GetName(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
            public Type GetFieldType(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
            public bool GetAllowNull(int ordinal) => false;
            public object GetValue(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
            public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(false);
            public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
