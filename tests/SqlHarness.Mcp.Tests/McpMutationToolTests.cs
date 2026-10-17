using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Core;
using SqlHarness.Core.Mutations;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp.Tests;

[Collection("McpScopeHome")]
public sealed class McpMutationToolTests
{
    private const string Profile = "mutation-test";
    private const string Sql = "INSERT INTO dbo.Items (Name) VALUES (@name);";

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() => new Dictionary<string, TargetProfile>
    {
        [Profile] = new("localhost", "testdb", new Dictionary<string, string>(), "integrated"),
    };

    [Fact]
    public void Catalog_exposes_nine_tools_with_mutation_within_tools_list_budget()
    {
        var scope = FixedScope();
        var tools = McpToolCatalog.CreateTools(scope, new SqlHarnessModule())
            .Select(tool => tool.ProtocolTool)
            .ToArray();
        var json = JsonSerializer.Serialize(tools);

        Assert.Equal(9, tools.Length);
        Assert.Contains(tools, tool => tool.Name == "sqlharness_mutation");
        Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, McpLimits.ToolsListBudgetBytes);
    }

    [Fact]
    public async Task Mutation_returns_success_with_bounded_query_result_sets()
    {
        var broker = new FakeBroker(_ => Task.FromResult(new MutationBrokerResult(MutationStatus.Success, null, QueryResult())));
        await using var host = await HostedServer.CreateAsync(FixedScope(), broker);
        var result = await host.CallMutationAsync(Args());
        var envelope = Envelope(result);

        Assert.False(result.IsError == true);
        Assert.Equal("success", envelope.RootElement.GetProperty("status").GetString());
        Assert.False(envelope.RootElement.TryGetProperty("command", out _));
        Assert.False(envelope.RootElement.TryGetProperty("exitCode", out _));
        var sets = envelope.RootElement.GetProperty("result").GetProperty("result").GetProperty("resultSets");
        Assert.Equal(JsonValueKind.Array, sets.ValueKind);
        Assert.Equal("saved", sets[0].GetProperty("rows")[0][0].GetString());
        Assert.Equal("mcp:stable-test", Assert.Single(broker.Requests).SessionKey);
    }

    [Fact]
    public async Task Approval_timeout_is_error_and_keeps_the_mutation_status()
    {
        var broker = new FakeBroker(_ => Task.FromResult(new MutationBrokerResult(MutationStatus.Timeout, "Approval expired.", null)));
        await using var host = await HostedServer.CreateAsync(FixedScope(), broker);
        var result = await host.CallMutationAsync(Args());
        var envelope = Envelope(result);

        Assert.True(result.IsError == true);
        Assert.Equal("approval_timeout", envelope.RootElement.GetProperty("status").GetString());
        Assert.Equal("approval_timeout", envelope.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("confirmDatabase")]
    public async Task Caller_approval_arguments_are_rejected_before_module_or_broker(string forbidden)
    {
        var broker = new FakeBroker(_ => throw new Xunit.Sdk.XunitException("The broker must not be called."));
        await using var host = await HostedServer.CreateAsync(FixedScope(), broker);
        var args = Args();
        args[forbidden] = true;

        var result = await host.CallMutationAsync(args);

        Assert.True(result.IsError == true);
        Assert.Empty(broker.Requests);
    }

    [Theory]
    [InlineData("maxResultBytes", 1000)]
    [InlineData("maxOperationSeconds", 0)]
    [InlineData("timeout", 0)]
    [InlineData("maxRows", 501)]
    public async Task Invalid_limits_are_rejected_before_broker_submission(string name, int value)
    {
        var broker = new FakeBroker(_ => throw new Xunit.Sdk.XunitException("The broker must not be called."));
        await using var host = await HostedServer.CreateAsync(FixedScope(), broker);
        var args = Args();
        args[name] = value;

        var result = await host.CallMutationAsync(args);

        Assert.True(result.IsError == true);
        Assert.Empty(broker.Requests);
    }

    [Fact]
    public async Task Expected_database_mismatch_is_reported_without_broker_submission()
    {
        var broker = new FakeBroker(_ => throw new Xunit.Sdk.XunitException("The broker must not be called."));
        await using var host = await HostedServer.CreateAsync(FixedScope(), broker);
        var args = Args();
        args["expectDatabase"] = "another-database";

        var result = await host.CallMutationAsync(args);

        Assert.True(result.IsError == true);
        Assert.Equal("database_mismatch", Envelope(result).RootElement.GetProperty("status").GetString());
        Assert.Empty(broker.Requests);
    }

    [Fact]
    public async Task Dry_run_returns_validation_result_without_broker_submission()
    {
        var broker = new FakeBroker(_ => throw new Xunit.Sdk.XunitException("The broker must not be called."));
        await using var host = await HostedServer.CreateAsync(FixedScope(), broker);
        var args = Args();
        args["dryRun"] = true;

        var result = await host.CallMutationAsync(args);

        Assert.False(result.IsError == true);
        Assert.Equal("success", Envelope(result).RootElement.GetProperty("status").GetString());
        Assert.Empty(broker.Requests);
    }

    [Fact]
    public async Task Progress_reports_awaiting_approval_while_broker_is_waiting()
    {
        var waiting = new TaskCompletionSource<MutationBrokerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new FakeBroker(_ => waiting.Task);
        await using var host = await HostedServer.CreateAsync(FixedScope(), broker);
        var report = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = host.Client.RegisterNotificationHandler(
            NotificationMethods.ProgressNotification,
            (notification, _) =>
            {
                var message = notification.Params?["message"]?.GetValue<string>();
                if (message == "sqlharness_mutation awaiting_approval")
                    report.TrySetResult(message);
                return ValueTask.CompletedTask;
            });
        var progress = new Progress<ProgressNotificationValue>(_ => { });
        var call = host.MutationTool.CallAsync(Args(), progress, null, host.Token);

        try
        {
            var message = await report.Task.WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Equal("sqlharness_mutation awaiting_approval", message);
        }
        finally
        {
            waiting.TrySetResult(new MutationBrokerResult(MutationStatus.Success, null, QueryResult()));
            await call;
        }
    }

    [Fact]
    public async Task Request_scope_requires_and_validates_target_before_broker_submission()
    {
        var process = McpProcessContext.Create(
            new McpServerOptions { RequestScope = true, AllowedProfiles = [Profile] },
            Profiles);
        var broker = new FakeBroker(_ => throw new Xunit.Sdk.XunitException("The broker must not be called."));
        await using var host = await HostedServer.CreateAsync(process, broker);
        var args = Args();
        args["target"] = new { profile = "outside-allowlist", vars = new { } };

        var result = await host.CallMutationAsync(args);

        Assert.True(result.IsError == true);
        Assert.Empty(broker.Requests);
    }

    [Fact]
    public async Task Request_scope_mutation_uses_the_validated_target()
    {
        var process = McpProcessContext.Create(
            new McpServerOptions { RequestScope = true, AllowedProfiles = [Profile] },
            Profiles);
        var broker = new FakeBroker(_ => Task.FromResult(new MutationBrokerResult(MutationStatus.Success, null, QueryResult())));
        await using var host = await HostedServer.CreateAsync(process, broker);
        var args = Args();
        args["target"] = new { profile = Profile, vars = new { } };

        var result = await host.CallMutationAsync(args);

        Assert.False(result.IsError == true);
        Assert.Equal("success", Envelope(result).RootElement.GetProperty("status").GetString());
        Assert.Equal(Profile, Assert.Single(broker.Requests).Profile);
    }

    private static Dictionary<string, object?> Args() => new()
    {
        ["sql"] = Sql,
        ["parameters"] = new[] { new { name = "name", type = "nvarchar", value = "saved" } },
    };

    private static McpScope FixedScope() => McpScope.Create(
        new McpServerOptions { Profile = Profile }, Profiles());

    private static SqlHarnessQueryReport QueryResult() => new(
        new SqlHarnessTargetIdentityReport("req-srv", "req-db", "localhost", "testdb", Profile),
        "dml",
        [new SqlHarnessResultSetReport(
            [new SqlHarnessColumnReport(0, "Name", "nvarchar", false)],
            [["saved"]],
            1,
            0)],
        [],
        1,
        4,
        "hash",
        new OutputFootprint(5, 1));

    private static JsonDocument Envelope(CallToolResult result) =>
        JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);

    private sealed class FakeBroker(Func<MutationRequest, Task<MutationBrokerResult>> execute) : IMutationBrokerClient
    {
        public List<MutationRequest> Requests { get; } = [];

        public Task<MutationBrokerResult> SubmitAsync(MutationRequest request, IProgress<string>? progress, CancellationToken ct)
        {
            Requests.Add(request);
            return execute(request);
        }
    }

    private sealed class HostedServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly McpServer _server;
        private readonly Task _serverTask;

        public McpClient Client { get; }
        public McpClientTool MutationTool { get; }
        public CancellationToken Token => _cts.Token;

        private HostedServer(McpClient client, McpClientTool mutationTool, McpServer server, Task serverTask, CancellationTokenSource cts)
        {
            Client = client;
            MutationTool = mutationTool;
            _server = server;
            _serverTask = serverTask;
            _cts = cts;
        }

        public static Task<HostedServer> CreateAsync(McpScope scope, IMutationBrokerClient broker) =>
            CreateAsync(options => McpToolCatalog.Wire(options, scope, scope.CreateModule(),
                mutationBroker: broker, mutationSessionKey: "mcp:stable-test"));

        public static Task<HostedServer> CreateAsync(McpProcessContext process, IMutationBrokerClient broker) =>
            CreateAsync(options => McpToolCatalog.Wire(options, process,
                mutationBroker: broker, mutationSessionKey: "mcp:stable-test"));

        private static async Task<HostedServer> CreateAsync(Action<ModelContextProtocol.Server.McpServerOptions> wire)
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var options = new ModelContextProtocol.Server.McpServerOptions
            {
                ServerInfo = new Implementation { Name = "mutation-test-server", Version = "1" },
                ProtocolVersion = McpHost.FallbackProtocolVersion,
            };
            wire(options);
            var server = McpServer.Create(
                new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "mutation-test", NullLoggerFactory.Instance),
                options,
                NullLoggerFactory.Instance,
                serviceProvider: null);
            var serverTask = server.RunAsync(cts.Token);
            var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "mutation-tests", Version = "1" },
                    ProtocolVersion = McpHost.FallbackProtocolVersion,
                },
                NullLoggerFactory.Instance,
                cts.Token);
            var mutation = (await client.ListToolsAsync(cancellationToken: cts.Token)).Single(tool => tool.Name == "sqlharness_mutation");
            return new HostedServer(client, mutation, server, serverTask, cts);
        }

        public Task<CallToolResult> CallMutationAsync(Dictionary<string, object?> args) =>
            MutationTool.CallAsync(args, null, null, Token).AsTask();

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _cts.CancelAsync();
            try { await _serverTask; }
            catch (OperationCanceledException) { }
            await _server.DisposeAsync();
            _cts.Dispose();
        }
    }
}