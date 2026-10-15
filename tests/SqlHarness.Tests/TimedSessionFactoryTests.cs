using Microsoft.Data.SqlClient;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Journal;
using SqlHarness.Core.Targets;
using SqlHarness.Tests.Journal;

using JournalOperationPhase = SqlHarness.Core.Journal.OperationPhase;

namespace SqlHarness.Tests;

public sealed class TimedSessionFactoryTests
{
    private static readonly ResolvedTarget Target = new(
        "server", "database", new AuthSpec(AuthStrategy.AzureCli), "profile");

    [Fact]
    public async Task Connect_success_records_connect_aggregate_and_diagnostic_detail()
    {
        var time = new ManualTime();
        var inner = new AdvancingFactory(time, 17, new FakeSession());
        var knownSecrets = new List<string> { "kept-local" };
        using (OperationPhaseRecorder.Begin(time, captureEvents: true))
        {
            await new TimedSessionFactory(inner).ConnectAsync(Target, CancellationToken.None, knownSecrets);

            var recorder = OperationPhaseRecorder.Current!;
            Assert.Equal(new PhaseAggregate(JournalOperationPhase.Connect, 1, 17, 17), Assert.Single(recorder.Aggregates()));
            var phaseEvent = Assert.Single(recorder.Events());
            Assert.Equal("succeeded", phaseEvent.Outcome);
            Assert.Contains("\"authStrategy\":\"azure-cli\"", phaseEvent.DetailJson!);
            Assert.Same(knownSecrets, inner.SeenKnownSecrets);
        }
    }

    [Fact]
    public async Task Integrated_auth_omits_auth_strategy_label()
    {
        var time = new ManualTime();
        var target = Target with { Auth = new AuthSpec(AuthStrategy.Integrated) };
        using (OperationPhaseRecorder.Begin(time, captureEvents: true))
        {
            await new TimedSessionFactory(new AdvancingFactory(time, 1, new FakeSession()))
                .ConnectAsync(target, CancellationToken.None);

            Assert.DoesNotContain("authStrategy", Assert.Single(OperationPhaseRecorder.Current!.Events()).DetailJson!);
        }
    }

    [Fact]
    public async Task Postgres_connect_uses_postgres_auth_strategy_label()
    {
        var time = new ManualTime();
        var target = Target with { Engine = SqlEngine.Postgres };
        using (OperationPhaseRecorder.Begin(time, captureEvents: true))
        {
            await new TimedSessionFactory(new AdvancingFactory(time, 1, new FakeSession()))
                .ConnectAsync(target, CancellationToken.None);

            Assert.Contains("\"authStrategy\":\"postgres\"", Assert.Single(OperationPhaseRecorder.Current!.Events()).DetailJson!);
        }
    }

    [Fact]
    public async Task Connect_sql_exception_records_failed_outcome_and_number()
    {
        var time = new ManualTime();
        var failure = (SqlException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SqlException));
        var inner = new AdvancingFactory(time, 9, failure);
        using (OperationPhaseRecorder.Begin(time, captureEvents: true))
        {
            await Assert.ThrowsAsync<SqlException>(() =>
                new TimedSessionFactory(inner).ConnectAsync(Target, CancellationToken.None));

            var recorder = OperationPhaseRecorder.Current!;
            Assert.Equal(new PhaseAggregate(JournalOperationPhase.Connect, 1, 9, 9), Assert.Single(recorder.Aggregates()));
            var phaseEvent = Assert.Single(recorder.Events());
            Assert.Equal("failed", phaseEvent.Outcome);
            Assert.Equal(failure.Number, phaseEvent.ErrorNumber);
            Assert.Contains("\"authStrategy\":\"azure-cli\"", phaseEvent.DetailJson!);
            Assert.Contains("\"attempt\":1", phaseEvent.DetailJson!);
            Assert.Contains("\"clientConnectionId\"", phaseEvent.DetailJson!);
        }
    }

    [Fact]
    public async Task No_recorder_keeps_factory_as_passthrough()
    {
        var session = new FakeSession();
        var inner = new AdvancingFactory(new ManualTime(), 1, session);

        var actual = await new TimedSessionFactory(inner).ConnectAsync(Target, CancellationToken.None);

        Assert.Same(session, actual);
        Assert.Null(OperationPhaseRecorder.Current);
    }

    [Fact]
    public async Task SqlClient_session_exposes_client_connection_id()
    {
        await using var connection = new SqlConnection();
        SqlInfoMessageEventHandler handler = (_, _) => { };
        await using var session = new SqlClientSession(connection, new SessionMessageBuffer(), handler);

        Assert.Equal(connection.ClientConnectionId.ToString(), session.ClientConnectionId);
    }

    private sealed class AdvancingFactory(ManualTime time, int milliseconds, object result) : ISqlSessionFactory
    {
        internal List<string>? SeenKnownSecrets { get; private set; }

        public async Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            await Task.Yield();
            time.Advance(milliseconds);
            if (result is Exception exception)
                throw exception;
            return (ISqlSession)result;
        }

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct, List<string> knownSecrets)
        {
            SeenKnownSecrets = knownSecrets;
            return ConnectAsync(target, ct);
        }
    }

    private sealed class FakeSession : ISqlSession
    {
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } = null!;
        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct) =>
            throw new NotImplementedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}