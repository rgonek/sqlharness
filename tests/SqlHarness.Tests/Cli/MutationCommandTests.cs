using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Core;
using SqlHarness.Core.Mutations;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Cli;

[Collection(SqlHarnessHomeCollection.Name)]
public sealed class MutationCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sqlharness-mutation-cli-" + Guid.NewGuid().ToString("N"));

    public MutationCommandTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Directory.Delete(_directory, true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Mutation_success_submits_the_prepared_request_and_prints_its_query_result()
    {
        var report = QueryReport();
        var broker = new RecordingBroker(new(MutationStatus.Success, null, report));
        var run = await Run(broker, "mutation", "ss", "--var", "tenant=alpha", "--file", Sql("DELETE FROM dbo.T;"),
            "--param", "code:int=42", "--timeout", "45", "--max-rows", "7", "--expect-database", "db", "--output", "agent");

        Assert.True(run.Exit == 0, run.Output);
        Assert.Equal(1, broker.SubmitCount);
        Assert.Contains("success", run.Output, StringComparison.Ordinal);
        Assert.Contains("db", run.Output, StringComparison.Ordinal);
        Assert.Equal("DELETE FROM dbo.T;", broker.Request!.Sql);
        Assert.Equal("alpha", broker.Request.Vars["tenant"]);
        Assert.Equal(new SqlHarnessParameterInput("code", "int", "42"), Assert.Single(broker.Request.Parameters));
        Assert.Equal(45, broker.Request.TimeoutSeconds);
        Assert.Equal(7, broker.Request.MaxRows);
        Assert.Equal("db", broker.Request.Database);
        Assert.False(string.IsNullOrWhiteSpace(broker.Request.SessionKey));
        Assert.Contains("Waiting for operator approval in the dashboard (up to 120 s)…", run.Error, StringComparison.Ordinal);
        using var envelope = JsonDocument.Parse(run.Output);
    }

    [Theory]
    [InlineData(MutationStatus.Disabled, 2)]
    [InlineData(MutationStatus.Unavailable, 2)]
    [InlineData(MutationStatus.Timeout, 2)]
    [InlineData(MutationStatus.Denied, 2)]
    [InlineData(MutationStatus.Busy, 2)]
    [InlineData(MutationStatus.NotAMutation, 2)]
    [InlineData(MutationStatus.PrincipalProtected, 2)]
    [InlineData(MutationStatus.Failed, 5)]
    [InlineData(MutationStatus.DatabaseMismatch, 4)]
    [InlineData(MutationStatus.OutcomeUnknown, 5)]
    public async Task Mutation_terminal_status_uses_its_fixed_exit_code(string status, int exitCode)
    {
        var broker = new RecordingBroker(new(status, "broker detail", null));

        var run = await Run(broker, "mutation", "ss", "--file", Sql("DELETE FROM dbo.T;"), "--output", "agent");

        Assert.True(run.Exit == exitCode, run.Output);
        using var json = JsonDocument.Parse(run.Output);
        Assert.Equal(status, json.RootElement.GetProperty("status").GetString());
        Assert.Equal("broker detail", json.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Mutation_dry_run_reports_tier_without_submitting_to_broker()
    {
        var broker = new RecordingBroker(new(MutationStatus.Success, null, QueryReport()));

        var run = await Run(broker, "mutation", "ss", "--file", Sql("DELETE FROM dbo.T;"), "--dry-run", "--json");

        Assert.True(run.Exit == 0, run.Output);
        Assert.Equal(0, broker.SubmitCount);
        using var json = JsonDocument.Parse(run.Output);
        Assert.Equal("dml", json.RootElement.GetProperty("tier").GetString());
    }

    [Theory]
    [InlineData("--max-output-bytes", "4095")]
    [InlineData("--max-output-bytes", "1048577")]
    [InlineData("--max-cell-chars", "-1")]
    [InlineData("--max-cell-chars", "4097")]
    public async Task Invalid_agent_output_limits_stop_before_module_preparation_or_broker_submit(string option, string value)
    {
        var broker = new RecordingBroker(new(MutationStatus.Success, null, QueryReport()));

        var run = await Run(broker, "mutation", "ss", "--file", Sql("DELETE FROM dbo.T;"), "--output", "agent", option, value);

        Assert.Equal((int)SqlHarnessExitCode.Safety, run.Exit);
        Assert.Equal(0, run.ModuleCalls);
        Assert.Equal(0, broker.SubmitCount);
        using var json = JsonDocument.Parse(run.Output);
        Assert.Equal("error", json.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("--allow-mutation")]
    [InlineData("--confirm-database")]
    public async Task Query_rejects_removed_mutation_options_as_parse_errors(string removedOption)
    {
        var broker = new RecordingBroker(new(MutationStatus.Success, null, QueryReport()));

        var args = removedOption == "--confirm-database"
            ? new[] { "query", "ss", "--file", Sql("DELETE FROM dbo.T;"), removedOption, "db", "--output", "agent" }
            : ["query", "ss", "--file", Sql("DELETE FROM dbo.T;"), removedOption, "--output", "agent"];
        var run = await Run(broker, args);

        Assert.Equal((int)SqlHarnessExitCode.Safety, run.Exit);
        Assert.Equal(0, broker.SubmitCount);
    }

    [Fact]
    public async Task Repeated_cli_invocations_keep_the_same_caller_session_key()
    {
        var broker = new RecordingBroker(new(MutationStatus.Busy, "already pending", null));
        var sql = Sql("DELETE FROM dbo.T;");

        _ = await Run(broker, "mutation", "ss", "--file", sql, "--output", "agent");
        _ = await Run(broker, "mutation", "ss", "--file", sql, "--output", "agent");

        Assert.Equal(2, broker.SubmitCount);
        Assert.Equal(broker.SessionKeys[0], broker.SessionKeys[1]);
    }

    [Fact]
    public async Task Query_persistent_dml_reports_mutation_command_hint()
    {
        var module = new SqlHarnessModule(new NoConnectionFactory(), Profiles);
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output).RunAsync(
            ["query", "ss", "--file", Sql("DELETE FROM dbo.T;"), "--dry-run", "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Contains("Persistent changes use `sqlharness mutation`.",
            json.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    private async Task<(int Exit, string Output, string Error, int ModuleCalls)> Run(RecordingBroker broker, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var module = new RecordingModule(operation => operation is SqlHarnessMutationOperation mutation
            ? mutation.DryRun
                ? new SqlHarnessOutcome(SqlHarnessExitCode.Success, new SqlHarnessDryRunReport("mutation", "mutation", "dml", [], 1), null)
                : new SqlHarnessOutcome(SqlHarnessExitCode.Success, new SqlHarnessMutationPrepared(Request(mutation)), null)
            : new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, "unexpected operation"));
        var exit = await SqlHarnessCli.Create(module, output, mutationBroker: broker, mcpError: error)
            .RunAsync(args);
        return (exit, output.ToString(), error.ToString(), module.Calls);
    }

    private string Sql(string sql)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".sql");
        File.WriteAllText(path, sql);
        return path;
    }

    private static MutationRequest Request(SqlHarnessMutationOperation operation) => new(
        operation.Target.Profile ?? "", operation.Target.Vars, "sqlserver", "server", "db", operation.Sql,
        operation.Parameters, operation.TimeoutSeconds, operation.MaxRows, "dml", [], operation.SessionKey);

    private static SqlHarnessQueryReport QueryReport() => new(
        new("server", "db", "server", "db", "ss"), "dml", [], [], 0, 1, "hash", new(10, 1));

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() => new Dictionary<string, TargetProfile>
    {
        ["ss"] = new("server", "db", new Dictionary<string, string>(), "integrated"),
    };

    private sealed class RecordingBroker(MutationBrokerResult result) : IMutationBrokerClient
    {
        public int SubmitCount { get; private set; }
        public MutationRequest? Request { get; private set; }
        public List<string> SessionKeys { get; } = [];

        public Task<MutationBrokerResult> SubmitAsync(MutationRequest request, IProgress<string>? progress, CancellationToken ct)
        {
            SubmitCount++;
            Request = request;
            SessionKeys.Add(request.SessionKey);
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingModule(Func<SqlHarnessOperation, SqlHarnessOutcome> execute) : ISqlHarnessModule
    {
        public int Calls { get; private set; }

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            Record(operation);

        private Task<SqlHarnessOutcome> Record(SqlHarnessOperation operation)
        {
            Calls++;
            return Task.FromResult(execute(operation));
        }
    }

    private sealed class NoConnectionFactory : ISqlSessionFactory
    {
        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct) =>
            throw new InvalidOperationException("This test must stay offline.");
    }
}
