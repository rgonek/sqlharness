using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Mutations;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Mutations;

public sealed class MutationOperationTests
{
    private static readonly IReadOnlyDictionary<string, TargetProfile> Profiles = new Dictionary<string, TargetProfile>
    {
        ["test"] = new("server", "database", new Dictionary<string, string>(), "sql", "user", "password"),
    };

    [Fact]
    public async Task Unapproved_operation_returns_request_without_opening_a_session()
    {
        var sessions = new CountingSessionFactory();
        var outcome = await Module(sessions).ExecuteAsync(Operation("UPDATE dbo.T SET Value = 1"));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var prepared = Assert.IsType<SqlHarnessMutationPrepared>(outcome.Report);
        Assert.Equal("UPDATE dbo.T SET Value = 1", prepared.Request.Sql);
        Assert.Equal(0, sessions.ConnectCount);
    }

    [Fact]
    public async Task Approved_operation_with_wrong_digest_is_rejected_before_opening_a_session()
    {
        var sessions = new CountingSessionFactory();
        var operation = Operation("UPDATE dbo.T SET Value = 1") with
        {
            Approval = new ApprovedMutation("wrong-digest", AuthSpec.Parse("sql", "writer", "password", false)),
        };

        var outcome = await Module(sessions).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal("Approval does not match the request.", outcome.SafeError);
        Assert.Equal(0, sessions.ConnectCount);
    }

    [Fact]
    public async Task Select_is_rejected_as_not_a_mutation()
    {
        var sessions = new CountingSessionFactory();
        var outcome = await Module(sessions).ExecuteAsync(Operation("SELECT 1"));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(MutationStatus.NotAMutation, Assert.IsType<MutationBrokerResult>(outcome.Report).Status);
        Assert.Equal(0, sessions.ConnectCount);
    }

    [Fact]
    public async Task Session_local_batch_is_rejected_as_not_a_mutation()
    {
        var sessions = new CountingSessionFactory();
        var outcome = await Module(sessions).ExecuteAsync(Operation("CREATE TABLE #local (Id int)"));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(MutationStatus.NotAMutation, Assert.IsType<MutationBrokerResult>(outcome.Report).Status);
        Assert.Equal(0, sessions.ConnectCount);
    }

    [Fact]
    public async Task Expected_database_mismatch_uses_target_mismatch_exit_code()
    {
        var sessions = new CountingSessionFactory();
        var outcome = await Module(sessions).ExecuteAsync(Operation("UPDATE dbo.T SET Value = 1") with
        {
            ExpectDatabase = "other",
        });

        Assert.Equal(SqlHarnessExitCode.TargetMismatch, outcome.ExitCode);
        Assert.Equal(MutationStatus.DatabaseMismatch, Assert.IsType<MutationBrokerResult>(outcome.Report).Status);
        Assert.Equal(0, sessions.ConnectCount);
    }

    [Fact]
    public async Task Dry_run_reports_mutation_tier_without_opening_a_session()
    {
        var sessions = new CountingSessionFactory();
        var outcome = await Module(sessions).ExecuteAsync(Operation("UPDATE dbo.T SET Value = 1") with { DryRun = true });

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessDryRunReport>(outcome.Report);
        Assert.Equal("mutation", report.Command);
        Assert.Equal("dml", report.Tier);
        Assert.Equal(0, sessions.ConnectCount);
    }

    private static SqlHarnessModule Module(CountingSessionFactory sessions) => new(sessions, () => Profiles);

    private static SqlHarnessMutationOperation Operation(string sql) => new(
        new SqlTargetRequest("test", new Dictionary<string, string>()), sql, [], 30, 50, null);

    private sealed class CountingSessionFactory : ISqlSessionFactory
    {
        public int ConnectCount { get; private set; }

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            ConnectCount++;
            throw new InvalidOperationException("Unexpected database connection.");
        }

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct, List<string> knownSecrets)
        {
            ConnectCount++;
            throw new InvalidOperationException("Unexpected database connection.");
        }
    }
}
