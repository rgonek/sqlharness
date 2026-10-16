using Npgsql;

using SqlHarness.Core;
using SqlHarness.Core.Dialect;
using SqlHarness.Core.Postgres;

namespace SqlHarness.Tests.Postgres;

public sealed class PostgresReadOnlyRuntimeTests
{
    [Fact]
    public void Sqlstate_25006_maps_to_the_read_only_safety_message_even_when_nested()
    {
        var providerFailure = new PostgresException("cannot execute in a read-only transaction", "ERROR", "ERROR", "25006");
        var wrappedFailure = new InvalidOperationException("reader failed", providerFailure);

        Assert.Equal(SqlHarnessExitCode.Safety, OperationFailureMapper.Map(wrappedFailure, OperationPhase.Sql));
        var mapped = Assert.IsType<SqlHarnessSafetyException>(PostgresReadOnlyFailure.Map(wrappedFailure));
        Assert.Equal(PostgresReadOnlyFailure.RejectionMessage, mapped.Message);
        Assert.Equal(PostgresReadOnlyFailure.RejectionMessage, SecretRedactor.Redact(wrappedFailure, []));
    }

    [Fact]
    public void Sqlstate_25006_is_found_inside_aggregate_failures()
    {
        var providerFailure = new PostgresException("cannot execute in a read-only transaction", "ERROR", "ERROR", "25006");
        var wrappedFailure = new AggregateException(new IOException("copy failed", providerFailure));

        Assert.Equal(SqlHarnessExitCode.Safety, OperationFailureMapper.Map(wrappedFailure, OperationPhase.Sql));
    }

    [Fact]
    public void Mutation_transport_loss_is_unknown_but_server_rejection_is_failed()
    {
        Assert.True(OperationFailureMapper.IsMutationOutcomeUnknown(new NpgsqlException("connection lost")));
        Assert.True(OperationFailureMapper.IsMutationOutcomeUnknown(new TimeoutException("command timed out")));
        Assert.False(OperationFailureMapper.IsMutationOutcomeUnknown(
            new PostgresException("syntax error", "ERROR", "ERROR", "42601")));
    }

    [Fact]
    public void New_execution_commands_default_to_read_only_with_narrow_explicit_modes()
    {
        var safe = new SqlExecutionCommand("SELECT 1", [], 5);

        Assert.Equal(PostgresExecutionMode.ReadOnly, safe.PostgresMode);
        Assert.Equal(PostgresExecutionMode.SessionSetup,
            (safe with { PostgresMode = PostgresExecutionMode.SessionSetup }).PostgresMode);
        Assert.Equal(PostgresExecutionMode.ApprovedMutation,
            (safe with { PostgresMode = PostgresExecutionMode.ApprovedMutation }).PostgresMode);
    }

    [Fact]
    public async Task Postgres_setup_requires_a_successful_classification_before_read_write_mode()
    {
        using var raw = new CanonicalResultAccumulator();
        var exception = await Assert.ThrowsAsync<SqlHarnessSafetyException>(() =>
            SetupSqlExecution.ExecuteSetupAsync(
                SqlEngine.Postgres,
                "CREATE TEMP TABLE setup_probe (id integer)",
                [],
                5,
                new UnusedSession(),
                raw,
                CancellationToken.None));

        Assert.Equal("PostgreSQL setup execution requires successful setup classification.", exception.Message);
    }

    [Fact]
    public async Task Successfully_classified_Postgres_setup_emits_session_setup_mode_to_the_fake_session()
    {
        const string sql = "CREATE TEMP TABLE setup_probe (id integer)";
        var classifier = SqlDialects.For(SqlEngine.Postgres);
        var safety = classifier.Classify(
            sql,
            SqlUsage.CompareSetup,
            "appdb",
            allowMutation: false,
            confirmDatabase: null,
            new HashSet<string>(StringComparer.Ordinal));
        Assert.True(safety.Allowed);

        using var raw = new CanonicalResultAccumulator();
        var session = new CapturingSession();
        await SetupSqlExecution.ExecuteSetupAsync(
            SqlEngine.Postgres,
            sql,
            [],
            5,
            session,
            raw,
            CancellationToken.None,
            safety);

        Assert.Equal(PostgresExecutionMode.SessionSetup, Assert.Single(session.Commands).PostgresMode);
    }

    [Fact]
    public async Task Rejected_Postgres_setup_classification_never_emits_read_write_mode()
    {
        const string sql = "CREATE TABLE public.persisted_probe (id integer)";
        var safety = SqlDialects.For(SqlEngine.Postgres).Classify(
            sql,
            SqlUsage.CompareSetup,
            "appdb",
            allowMutation: false,
            confirmDatabase: null,
            new HashSet<string>(StringComparer.Ordinal));
        Assert.False(safety.Allowed);

        using var raw = new CanonicalResultAccumulator();
        var session = new CapturingSession();
        await Assert.ThrowsAsync<SqlHarnessSafetyException>(() => SetupSqlExecution.ExecuteSetupAsync(
            SqlEngine.Postgres,
            sql,
            [],
            5,
            session,
            raw,
            CancellationToken.None,
            safety));

        Assert.Empty(session.Commands);
    }

    private sealed class CapturingSession : ISqlSession
    {
        public List<SqlExecutionCommand> Commands { get; } = [];
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } = null!;

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult<ISqlReader>(new EmptyReader());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyReader : ISqlReader
    {
        public int FieldCount => 0;
        public int RecordsAffected => 0;
        public string GetName(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public Type GetFieldType(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public bool GetAllowNull(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public object GetValue(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnusedSession : ISqlSession
    {
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } = null!;
        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct) =>
            throw new InvalidOperationException("The setup preflight should reject before executing.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
