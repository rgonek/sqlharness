using Npgsql;

using SqlHarness.Core;
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

    private sealed class UnusedSession : ISqlSession
    {
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } = null!;
        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct) =>
            throw new InvalidOperationException("The setup preflight should reject before executing.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
