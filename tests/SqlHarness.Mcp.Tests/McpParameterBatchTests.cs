using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp;
using SqlHarness.Mcp.Tools;

using Xunit;

namespace SqlHarness.Mcp.Tests;

[Collection("McpScopeHome")]
public sealed class McpParameterBatchTests
{
    private const string ProfileName = "parameter-batches";
    private static readonly IReadOnlyDictionary<string, TargetProfile> Profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
    {
        [ProfileName] = new("unreachable.invalid", "batchdb", new Dictionary<string, string> { ["tenant"] = "^frozen$" }, "integrated"),
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Served_fixed_and_request_tools_accept_named_and_matrix_singletons_offline(bool requestScope)
    {
        var attempts = 0;
        await using var served = await ServedCatalog.CreateAsync(requestScope, () => Interlocked.Increment(ref attempts));
        var schemas = await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var measure = ServedSchema(schemas.Single(tool => tool.Name == "sqlharness_measure"));
        var compare = ServedSchema(schemas.Single(tool => tool.Name == "sqlharness_compare"));
        AssertCollectionBounds(measure, "parameterSets");
        AssertCollectionBounds(measure, "parameterSetFiles");
        AssertCollectionBounds(Properties(measure).GetProperty("matrix"), "values");
        AssertCollectionBounds(compare, "parameterSets");
        AssertCollectionBounds(compare, "parameterSetFiles");
        AssertCollectionBounds(Properties(compare).GetProperty("matrix"), "values");

        object? target = requestScope ? new { profile = ProfileName, vars = new Dictionary<string, string> { ["tenant"] = "frozen" } } : null;
        var setCall = new Dictionary<string, object?>
        {
            ["query"] = new { sql = "SELECT @id" },
            ["parameterSets"] = new[] { new { name = "only", parameters = new[] { new { name = "id", type = "int", value = "7" } } } },
            ["repeat"] = 1,
            ["dryRun"] = true,
        };
        var matrixCall = new Dictionary<string, object?>
        {
            ["baseline"] = new { sql = "SELECT @id" },
            ["candidate"] = new { sql = "SELECT @id" },
            ["subject"] = "singleton",
            ["scope"] = "whole",
            ["matrix"] = new { name = "id", type = "int", values = new string?[] { "7" } },
            ["repeat"] = 1,
            ["dryRun"] = true,
        };
        if (requestScope)
        {
            setCall["target"] = target;
            matrixCall["target"] = target;
        }

        var setResult = await served.Client.CallToolAsync("sqlharness_measure", setCall, cancellationToken: CancellationToken.None);
        var matrixResult = await served.Client.CallToolAsync("sqlharness_compare", matrixCall, cancellationToken: CancellationToken.None);

        Assert.False(setResult.IsError == true, Text(setResult));
        Assert.False(matrixResult.IsError == true, Text(matrixResult));
        Assert.Equal("success", Envelope(setResult).GetProperty("status").GetString());
        Assert.Equal("success", Envelope(matrixResult).GetProperty("status").GetString());
        Assert.Equal(0, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task Explicit_empty_collection_is_rejected_and_33_cases_get_split_guidance()
    {
        var scope = FixedScope();
        var query = new McpSqlSourceArgument { Sql = "SELECT @id" };
        var empty = await Assert.ThrowsAsync<McpMappingException>(() => McpOperationMapper.MapMeasureAsync(
            scope, query, null, null, null, 1, null, CancellationToken.None,
            parameterSets: Array.Empty<McpParameterSetArgument>()));
        Assert.Contains("between 1 and 32", empty.Message, StringComparison.Ordinal);

        var tooMany = await Assert.ThrowsAsync<McpMappingException>(() => McpOperationMapper.MapCompareAsync(
            scope, query, query, null, null, 1, null, "ordered",
            new McpMatrixArgument { Name = "id", Type = "int", Values = Enumerable.Range(0, 33).Select(value => (string?)value.ToString()).ToArray() },
            CancellationToken.None, dryRun: true, subject: "query", optimizationScope: "whole"));
        Assert.Contains("split into calls of at most 32 cases", tooMany.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("singleton remainder", tooMany.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertCollectionBounds(JsonElement parent, string name)
    {
        var schema = Properties(parent).GetProperty(name);
        Assert.Equal(1, schema.GetProperty("minItems").GetInt32());
        Assert.Equal(32, schema.GetProperty("maxItems").GetInt32());
    }

    private static JsonElement ServedSchema(McpClientTool tool) => JsonDocument.Parse(JsonSerializer.Serialize(tool.ProtocolTool)).RootElement.GetProperty("inputSchema");

    private static JsonElement Properties(JsonElement schema) => schema.GetProperty("properties");

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static JsonElement Envelope(CallToolResult result) => JsonDocument.Parse(Text(result)).RootElement;

    private static McpScope FixedScope() => McpScope.Create(
        new McpServerOptions { Profile = ProfileName, Vars = new Dictionary<string, string> { ["tenant"] = "frozen" } },
        Profiles);

    private sealed class ServedCatalog : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();
        private Task _serverTask = Task.CompletedTask;

        public McpClient Client { get; private set; } = null!;

        public static async Task<ServedCatalog> CreateAsync(bool requestScope, Action onConnect)
        {
            var catalog = new ServedCatalog();
            var options = new ModelContextProtocol.Server.McpServerOptions
            {
                ServerInfo = new Implementation { Name = "sqlharness-tests", Version = "1.0" },
                ProtocolVersion = McpHost.FallbackProtocolVersion,
            };
            if (requestScope)
            {
                var process = McpProcessContext.Create(
                    new SqlHarness.Mcp.McpServerOptions { RequestScope = true, AllowedProfiles = [ProfileName] },
                    () => Profiles);
                McpToolCatalog.Wire(options, process, moduleFactory: scope => new SqlHarnessModule(new ForbiddenSessionFactory(onConnect), scope.ProfileProvider));
            }
            else
            {
                var scope = FixedScope();
                var module = new SqlHarnessModule(new ForbiddenSessionFactory(onConnect), scope.ProfileProvider);
                McpToolCatalog.Wire(options, scope, module);
            }
            var server = McpServer.Create(
                new StreamServerTransport(catalog._clientToServer.Reader.AsStream(), catalog._serverToClient.Writer.AsStream(), "batch-test-server", NullLoggerFactory.Instance),
                options,
                NullLoggerFactory.Instance,
                serviceProvider: null);
            catalog._serverTask = server.RunAsync(catalog._cts.Token);
            catalog.Client = await McpClient.CreateAsync(
                new StreamClientTransport(catalog._clientToServer.Writer.AsStream(), catalog._serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "batch-test-client", Version = "1.0" },
                    ProtocolVersion = McpHost.FallbackProtocolVersion,
                },
                NullLoggerFactory.Instance,
                catalog._cts.Token);
            return catalog;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await Client.DisposeAsync(); } catch (OperationCanceledException) { }
            try { await _serverTask; } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }

    private sealed class ForbiddenSessionFactory(Action onConnect) : ISqlSessionFactory
    {
        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            onConnect();
            throw new InvalidOperationException("Dry-run must not connect.");
        }
    }
}
