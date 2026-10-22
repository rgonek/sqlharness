using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;

using Xunit.Abstractions;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// T3 catalog contract: exactly the 9 spec tools with strict input schemas,
/// no raw target/auth or caller-approval surface, and unknown-property rejection without
/// ever echoing values. Only synthetic HOME directories are used; the
/// unreachable profile target is never connected (unknown properties are
/// rejected at binding time, before any handler runs).
/// </summary>
[Collection("McpScopeHome")]
public sealed class McpToolSchemaTests : IDisposable
{
    private const string ProfileName = "mcp-t3-schema";
    private const string SecretSql = "SELECT * FROM Secrets WHERE x = 's3cr3t-value-xyz'";
    private const string SecretValue = "p4ss-value-abc";

    private static readonly string[] ExpectedTools =
    [
        "sqlharness_capabilities",
        "sqlharness_query",
        "sqlharness_mutation",
        "sqlharness_measure",
        "sqlharness_compare",
        "sqlharness_watch",
        "sqlharness_snapshot",
        "sqlharness_plan",
        "sqlharness_artifact",
    ];

    private readonly string _home;
    private readonly ITestOutputHelper _output;
    private readonly string? _savedHome;
    private readonly string _targetsFile;

    public McpToolSchemaTests(ITestOutputHelper output)
    {
        _output = output;
        _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
        _home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-t3-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
        _targetsFile = Path.Combine(_home, "targets.json");
        File.WriteAllText(
            _targetsFile,
            "{\""
            + ProfileName
            + "\": {\"server\": \"mcp-unreachable.invalid\", \"database\": \"reportdb\", "
            + "\"vars\": {\"tenant\": \"^frozen$\"}, \"auth\": \"integrated\"}}");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private McpScope Scope() => McpScope.Create(
        new McpServerOptions
        {
            Profile = ProfileName,
            Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tenant"] = "frozen" },
        },
        ProfileStore.Load(_targetsFile));

    private sealed class ServedCatalog : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();
        private Task _serverTask = Task.CompletedTask;

        public McpClient Client { get; private set; } = null!;

        public static async Task<ServedCatalog> CreateAsync(McpScope scope) => await CreateAsync(scope, scope.CreateModule());
        public static async Task<ServedCatalog> CreateAsync(McpScope scope, CompareConfig compareConfig) =>
            await CreateAsync(scope, scope.CreateModule(compareConfig), compareConfig);
        public static async Task<ServedCatalog> CreateAsync(McpScope scope, ISqlHarnessModule module)
            => await CreateAsync(scope, module, SqlHarnessConfig.Default.Compare);
        private static async Task<ServedCatalog> CreateAsync(McpScope scope, ISqlHarnessModule module, CompareConfig compareConfig)
        {
            var catalog = new ServedCatalog();
            var serverOptions = new ModelContextProtocol.Server.McpServerOptions
            {
                ServerInfo = new Implementation { Name = McpHost.ServerName, Version = "t3-test" },
                ProtocolVersion = McpHost.FallbackProtocolVersion,
            };
            McpToolCatalog.Wire(serverOptions, scope, module, compareConfig: compareConfig);
            var server = McpServer.Create(
                new StreamServerTransport(
                    catalog._clientToServer.Reader.AsStream(),
                    catalog._serverToClient.Writer.AsStream(),
                    "t3-test-server",
                    NullLoggerFactory.Instance),
                serverOptions,
                NullLoggerFactory.Instance,
                serviceProvider: null);
            catalog._serverTask = server.RunAsync(catalog._cts.Token);
            catalog.Client = await McpClient.CreateAsync(
                new StreamClientTransport(
                    catalog._clientToServer.Writer.AsStream(),
                    catalog._serverToClient.Reader.AsStream(),
                    NullLoggerFactory.Instance),
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "t3-test-client", Version = "1.0.0" },
                    ProtocolVersion = McpHost.FallbackProtocolVersion,
                },
                NullLoggerFactory.Instance,
                catalog._cts.Token);
            return catalog;
        }

        public static async Task<ServedCatalog> CreateAsync(McpProcessContext process)
        {
            var catalog = new ServedCatalog();
            var serverOptions = new ModelContextProtocol.Server.McpServerOptions
            {
                ServerInfo = new Implementation { Name = McpHost.ServerName, Version = "t3-test" },
                ProtocolVersion = McpHost.FallbackProtocolVersion,
            };
            McpToolCatalog.Wire(serverOptions, process);
            var server = McpServer.Create(
                new StreamServerTransport(catalog._clientToServer.Reader.AsStream(), catalog._serverToClient.Writer.AsStream(), "t3-test-server", NullLoggerFactory.Instance),
                serverOptions, NullLoggerFactory.Instance, serviceProvider: null);
            catalog._serverTask = server.RunAsync(catalog._cts.Token);
            catalog.Client = await McpClient.CreateAsync(
                new StreamClientTransport(catalog._clientToServer.Writer.AsStream(), catalog._serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
                new McpClientOptions { ClientInfo = new Implementation { Name = "t3-test-client", Version = "1.0.0" }, ProtocolVersion = McpHost.FallbackProtocolVersion },
                NullLoggerFactory.Instance, catalog._cts.Token);
            return catalog;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
        }
    }

    private sealed class HostedCatalog : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Pipe _clientToServer;
        private readonly Pipe _serverToClient;
        private readonly Task _serverTask;
        public McpClient Client { get; }

        private HostedCatalog(CancellationTokenSource cts, Pipe clientToServer, Pipe serverToClient, Task serverTask, McpClient client)
        {
            _cts = cts;
            _clientToServer = clientToServer;
            _serverToClient = serverToClient;
            _serverTask = serverTask;
            Client = client;
        }

        public static async Task<HostedCatalog> CreateAsync(McpScope scope)
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var options = new McpServerOptions { RequestScope = true, AllowedProfiles = [ProfileName] };
            var log = new StringWriter();
            var serverTask = McpHost.RunAsync(options, clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), log,
                () => scope.Profiles, cts.Token);
            var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
                new McpClientOptions { ClientInfo = new Implementation { Name = "compare-policy-test", Version = "1" }, ProtocolVersion = McpHost.FallbackProtocolVersion },
                NullLoggerFactory.Instance, cts.Token);
            return new HostedCatalog(cts, clientToServer, serverToClient, serverTask, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _cts.CancelAsync();
            try { await _serverTask; } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }

    private static JsonElement ServedSchema(McpClientTool tool) =>
        JsonDocument.Parse(JsonSerializer.Serialize(tool.ProtocolTool)).RootElement.GetProperty("inputSchema");

    private static HashSet<string> Required(JsonElement schema) =>
        schema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];

    private static JsonElement Properties(JsonElement schema) => schema.GetProperty("properties");

    private static string[] EnumOf(JsonElement schema, string property) =>
        Properties(schema).GetProperty(property).GetProperty("enum").EnumerateArray()
            .Select(item => item.GetString()!).ToArray();

    private static async Task<Dictionary<string, JsonElement>> ServedSchemasAsync(McpScope scope)
    {
        await using var served = await ServedCatalog.CreateAsync(scope);
        var tools = await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        return tools.ToDictionary(tool => tool.Name, ServedSchema, StringComparer.Ordinal);
    }

    private static readonly SqlHarnessTargetIdentityReport ConformanceTarget = new("req-srv", "req-db", "srv", "db", "profile");
    private sealed class CannedModule(SqlHarnessOutcome outcome) : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            Task.FromResult(outcome);
        public Task<SqlHarnessOutcome> ExecuteWatchNdjsonAsync(
            SqlHarnessWatchOperation operation, TextWriter writer, CancellationToken ct = default) =>
            throw new NotSupportedException("MCP must never use the NDJSON watch path.");
    }

    private static JsonElement ServedEnvelope(CallToolResult result, string tool)
    {
        Assert.Null(result.StructuredContent);
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        using var document = JsonDocument.Parse(text);
        var envelope = document.RootElement.Clone();
        Assert.False(envelope.TryGetProperty("command", out _), $"{tool} envelope must omit command");
        Assert.False(envelope.TryGetProperty("exitCode", out _), $"{tool} envelope must omit exitCode");
        return envelope;
    }

    private static void CollectPropertyNames(JsonElement node, HashSet<string> names)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;
        if (node.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                names.Add(property.Name);
                CollectPropertyNames(property.Value, names);
                if (property.Value.TryGetProperty("items", out var items))
                    CollectPropertyNames(items, names);
            }
        }
        else if (node.TryGetProperty("items", out var items))
        {
            CollectPropertyNames(items, names);
        }
    }

    [Fact]
    public void Catalog_registers_exactly_the_9_spec_tools()
    {
        var scope = Scope();
        var tools = McpToolCatalog.CreateTools(scope, scope.CreateModule());
        Assert.Equal(9, McpLimits.MaxToolsWithoutOperationAnnotations);
        Assert.Equal(McpLimits.MaxToolsWithoutOperationAnnotations, tools.Count);
        Assert.Equal(ExpectedTools, tools.Select(tool => tool.ProtocolTool.Name));
        Assert.Equal(ExpectedTools, McpToolCatalog.ToolNames);
        Assert.Equal(ExpectedTools.Length, McpToolCatalog.ToolNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Served_tools_list_matches_the_explicit_catalog()
    {
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var tools = await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        // The transport may order tools differently; the contract is the set.
        Assert.Equal(ExpectedTools.Order(StringComparer.Ordinal), tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(ExpectedTools.Length, tools.Count());
    }

    [Fact]
    public async Task Parameter_file_limits_are_published_in_descriptions_and_capabilities()
    {
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var tools = await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        foreach (var name in new[] { "sqlharness_measure", "sqlharness_compare" })
        {
            var schema = ServedSchema(tools.Single(tool => tool.Name == name));
            var description = Properties(schema).GetProperty("parameterSetFiles").GetProperty("description").GetString() ?? string.Empty;
            Assert.Contains("16 MiB/file", description, StringComparison.Ordinal);
            Assert.Contains("32 MiB total raw bytes per call", description, StringComparison.Ordinal);
        }

        var response = await served.Client.CallToolAsync("sqlharness_capabilities", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
        Assert.False(response.IsError == true);
        using var document = JsonDocument.Parse(Assert.Single(response.Content.OfType<TextContentBlock>()).Text);
        var capabilities = document.RootElement.GetProperty("result");
        var ingress = capabilities.GetProperty("parameterFileIngress");
        Assert.Equal(16 * 1024 * 1024, ingress.GetProperty("maxFileBytes").GetInt32());
        Assert.Equal(32 * 1024 * 1024, ingress.GetProperty("maxTotalBytes").GetInt32());
        Assert.Equal(32 * 1024 * 1024, capabilities.GetProperty("materializedParameterBytes").GetInt32());
    }

    [Fact]
    public async Task Operation_annotation_members_are_gated_by_the_startup_config()
    {
        Assert.False(SqlHarnessConfig.Default.Journal.OperationAnnotations);
        var scope = Scope();
        var offProcess = McpProcessContext.Create(new McpServerOptions { Profile = ProfileName, Vars = new Dictionary<string, string> { ["tenant"] = "frozen" } },
            () => scope.Profiles);
        await using var off = await ServedCatalog.CreateAsync(offProcess);
        var offTools = await off.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        Assert.DoesNotContain(offTools, tool => tool.Name == "sqlharness_annotate");
        foreach (var tool in offTools)
        {
            var properties = Properties(ServedSchema(tool));
            Assert.False(properties.TryGetProperty("label", out _), tool.Name);
            Assert.False(properties.TryGetProperty("keep", out _), tool.Name);
            Assert.False(properties.TryGetProperty("responseDetail", out _), tool.Name);
            Assert.DoesNotContain("annotation", tool.Description ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        var offCatalogBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(offTools.Select(tool => tool.ProtocolTool).ToArray()));
        Assert.InRange(offCatalogBytes, 1, 32768);
        _output.WriteLine($"annotations-off tools/list: {offCatalogBytes} UTF-8 bytes (Plan 095 contract)");
        var offCapabilities = await off.Client.CallToolAsync("sqlharness_capabilities", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
        Assert.False(ServedEnvelope(offCapabilities, "sqlharness_capabilities").GetProperty("result").TryGetProperty("operationAnnotations", out _));

        var enabledConfig = new SqlHarnessConfig
        {
            Journal = JsonSerializer.Deserialize<JournalConfig>("{\"Enabled\":true,\"OperationAnnotations\":true}")!,
        };
        var onProcess = McpProcessContext.Create(new McpServerOptions { Profile = ProfileName, Vars = new Dictionary<string, string> { ["tenant"] = "frozen" } },
            () => scope.Profiles, config: enabledConfig);
        await using var on = await ServedCatalog.CreateAsync(onProcess);
        var onTools = await on.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var annotationTool = onTools.Single(tool => tool.Name == "sqlharness_annotate");
        var annotationSchema = Properties(ServedSchema(annotationTool));
        Assert.Contains("items", annotationSchema.EnumerateObject().Select(property => property.Name));
        Assert.Contains("responseDetail", annotationSchema.EnumerateObject().Select(property => property.Name));
        Assert.Contains("maxResultBytes", annotationSchema.EnumerateObject().Select(property => property.Name));
        Assert.Equal("receipt", annotationSchema.GetProperty("responseDetail").GetProperty("default").GetString());
        Assert.Equal(new[] { "receipt", "full" }, annotationSchema.GetProperty("responseDetail").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(McpLimits.MinCallToolResultBudgetBytes, annotationSchema.GetProperty("maxResultBytes").GetProperty("minimum").GetInt32());
        Assert.Equal(McpLimits.MaxCallToolResultBudgetBytes, annotationSchema.GetProperty("maxResultBytes").GetProperty("maximum").GetInt32());
        Assert.Equal(1, annotationSchema.GetProperty("items").GetProperty("minItems").GetInt32());
        Assert.Equal(20, annotationSchema.GetProperty("items").GetProperty("maxItems").GetInt32());
        var itemProperties = Properties(annotationSchema.GetProperty("items").GetProperty("items"));
        Assert.Contains("operationRef", itemProperties.EnumerateObject().Select(property => property.Name));
        Assert.Contains("journaled", annotationTool.Description ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        foreach (var name in new[] { "sqlharness_query", "sqlharness_measure", "sqlharness_compare", "sqlharness_watch", "sqlharness_snapshot" })
        {
            var schema = ServedSchema(onTools.Single(tool => tool.Name == name));
            Assert.True(Properties(schema).TryGetProperty("label", out var label), name);
            Assert.True(Properties(schema).TryGetProperty("keep", out _), name);
            var description = label.GetProperty("description").GetString()!;
            Assert.DoesNotContain("\n", description, StringComparison.Ordinal);
            Assert.InRange(description.Length, 1, 160);
        }
        foreach (var name in new[] { "sqlharness_capabilities", "sqlharness_mutation", "sqlharness_plan", "sqlharness_artifact" })
        {
            var schema = ServedSchema(onTools.Single(tool => tool.Name == name));
            Assert.False(Properties(schema).TryGetProperty("label", out _), name);
            Assert.False(Properties(schema).TryGetProperty("keep", out _), name);
        }
        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(onTools.Select(tool => tool.ProtocolTool).ToArray()));
        Assert.Equal(McpLimits.MaxToolsWithOperationAnnotations, onTools.Count());
        Assert.True(bytes <= McpLimits.ToolsListBudgetBytes, $"annotation-enabled tools/list is {bytes} bytes.");
        _output.WriteLine($"annotations-on tools/list: {bytes} UTF-8 bytes of {McpLimits.ToolsListBudgetBytes}");
        var onCapabilities = await on.Client.CallToolAsync("sqlharness_capabilities", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
        Assert.True(ServedEnvelope(onCapabilities, "sqlharness_capabilities").GetProperty("result").GetProperty("operationAnnotations").GetBoolean());

        var requestProcess = McpProcessContext.Create(new McpServerOptions
        {
            RequestScope = true,
            AllowedProfiles = [ProfileName],
        }, () => scope.Profiles, config: enabledConfig);
        await using var requestOn = await ServedCatalog.CreateAsync(requestProcess);
        var requestTools = await requestOn.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        Assert.InRange(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(requestTools.Select(tool => tool.ProtocolTool).ToArray())), 1, 32768);
        foreach (var name in new[] { "sqlharness_query", "sqlharness_measure", "sqlharness_compare", "sqlharness_watch", "sqlharness_snapshot" })
            Assert.True(Properties(ServedSchema(requestTools.Single(tool => tool.Name == name))).TryGetProperty("label", out _), name);

        var rejected = await off.Client.CallToolAsync("sqlharness_query", new Dictionary<string, object?>
        {
            ["sql"] = "SELECT 1",
            ["label"] = "hidden when off",
        }, cancellationToken: CancellationToken.None);
        Assert.True(rejected.IsError == true);
    }

    [Fact]
    public void Operation_annotations_require_the_activity_journal_at_startup()
    {
        var scope = Scope();
        var config = new SqlHarnessConfig
        {
            Journal = JsonSerializer.Deserialize<JournalConfig>("{\"Enabled\":false,\"OperationAnnotations\":true}")!,
        };
        Assert.Throws<McpStartupException>(() => McpProcessContext.Create(
            new McpServerOptions { Profile = ProfileName, Vars = new Dictionary<string, string> { ["tenant"] = "frozen" } }, () => scope.Profiles, config: config));
    }

    [Fact]
    public async Task Cell_limit_is_forwarded_by_watch_snapshot_plan_and_artifact_tools()
    {
        var text = new string('x', 5000);
        var set = new SqlHarnessResultSetReport([new SqlHarnessColumnReport(0, "value", "text", true)], [[text]], 1, 0);
        var report = new SqlHarnessQueryReport(ConformanceTarget, "read-only", [set], [], 0, 1, "cell-limit", new OutputFootprint(5000, 1));
        var module = new CannedModule(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null));
        await using var served = await ServedCatalog.CreateAsync(Scope(), module);

        var watch = await served.Client.CallToolAsync("sqlharness_watch", new Dictionary<string, object?>
        {
            ["sql"] = "SELECT 1",
            ["untilUnchanged"] = 1,
            ["maxCellChars"] = 2048,
        }, cancellationToken: CancellationToken.None);
        var snapshot = await served.Client.CallToolAsync("sqlharness_snapshot", new Dictionary<string, object?>
        {
            ["action"] = "diff",
            ["name"] = "sample",
            ["sql"] = "SELECT 1",
            ["maxCellChars"] = 4096,
        }, cancellationToken: CancellationToken.None);
        var plan = await served.Client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?>
        {
            ["content"] = "<ShowPlanXML />",
            ["maxCellChars"] = 0,
        }, cancellationToken: CancellationToken.None);
        var artifact = await served.Client.CallToolAsync("sqlharness_artifact", new Dictionary<string, object?>
        {
            ["id"] = "artifact1",
            ["section"] = "summary",
            ["cursor"] = 1,
            ["maxCellChars"] = 100,
        }, cancellationToken: CancellationToken.None);
        var rejected = await served.Client.CallToolAsync("sqlharness_query", new Dictionary<string, object?>
        {
            ["sql"] = "SELECT 1",
            ["maxCellChars"] = 4097,
        }, cancellationToken: CancellationToken.None);

        var watchEnvelope = ServedEnvelope(watch, "sqlharness_watch");
        Assert.Equal(2048, watchEnvelope.GetProperty("truncation").GetProperty("maxCellChars").GetInt32());
        Assert.Equal(2048, watchEnvelope.GetProperty("result").GetProperty("resultSets")[0].GetProperty("rows")[0][0].GetString()!.Length);
        var snapshotEnvelope = ServedEnvelope(snapshot, "sqlharness_snapshot");
        Assert.Equal(4096, snapshotEnvelope.GetProperty("truncation").GetProperty("maxCellChars").GetInt32());
        Assert.Equal(4096, snapshotEnvelope.GetProperty("result").GetProperty("resultSets")[0].GetProperty("rows")[0][0].GetString()!.Length);
        using (var planDocument = JsonDocument.Parse(Assert.Single(plan.Content.OfType<TextContentBlock>()).Text))
        {
            if (planDocument.RootElement.TryGetProperty("truncation", out var planTruncation))
                Assert.Equal(0, planTruncation.GetProperty("maxCellChars").GetInt32());
        }
        var artifactEnvelope = ServedEnvelope(artifact, "sqlharness_artifact");
        if (artifactEnvelope.TryGetProperty("truncation", out var artifactTruncation))
            Assert.Equal(100, artifactTruncation.GetProperty("maxCellChars").GetInt32());
        Assert.True(rejected.IsError == true);
        Assert.Contains("Cell limit must be 0..4096 characters.", ServedEnvelope(rejected, "sqlharness_query").GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Request_scope_schema_is_required_only_on_the_target_tools()
    {
        var scope = Scope();
        var process = McpProcessContext.Create(new McpServerOptions
        {
            RequestScope = true,
            AllowedProfiles = [ProfileName],
        }, () => scope.Profiles);
        await using var requestServed = await ServedCatalog.CreateAsync(process);
        var requestTools = await requestServed.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        Assert.InRange(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(requestTools.Select(tool => tool.ProtocolTool).ToArray())), 1, 32768);
        var requestSchemas = requestTools.ToDictionary(tool => tool.Name, ServedSchema, StringComparer.Ordinal);
        Assert.Equal(["summary", "metrics", "operators", "statements", "matrix-cells", "plan"], EnumOf(requestSchemas["sqlharness_artifact"], "section"));
        Assert.Contains("cursor", Properties(requestSchemas["sqlharness_artifact"]).EnumerateObject().Select(property => property.Name));
        var requestArtifactProperties = Properties(requestSchemas["sqlharness_artifact"]);
        Assert.True(requestArtifactProperties.TryGetProperty("maxResultBytes", out var requestArtifactBudget), requestSchemas["sqlharness_artifact"].GetRawText());
        Assert.Equal(McpLimits.MinCallToolResultBudgetBytes, requestArtifactBudget.GetProperty("minimum").GetInt32());
        Assert.Equal(McpLimits.MaxCallToolResultBudgetBytes, requestArtifactBudget.GetProperty("maximum").GetInt32());
        Assert.True(requestArtifactProperties.TryGetProperty("pageSize", out var requestArtifactPageSize), requestSchemas["sqlharness_artifact"].GetRawText());
        Assert.Equal(1, requestArtifactPageSize.GetProperty("minimum").GetInt32());
        Assert.Equal(512, requestArtifactPageSize.GetProperty("maximum").GetInt32());
        var projectedTools = new[] { "sqlharness_query", "sqlharness_mutation", "sqlharness_measure", "sqlharness_compare", "sqlharness_watch", "sqlharness_snapshot", "sqlharness_plan", "sqlharness_artifact" };
        foreach (var tool in projectedTools)
            Assert.True(Properties(requestSchemas[tool]).TryGetProperty("maxCellChars", out _), $"{tool} must expose maxCellChars.");
        Assert.False(Properties(requestSchemas["sqlharness_capabilities"]).TryGetProperty("maxCellChars", out _));
        var requestCompare = Properties(requestSchemas["sqlharness_compare"]).GetProperty("compareNullability");
        Assert.Equal("boolean", requestCompare.GetProperty("type").GetString());
        Assert.False(requestCompare.GetProperty("default").GetBoolean());
        var targetTools = new[] { "sqlharness_query", "sqlharness_mutation", "sqlharness_measure", "sqlharness_compare", "sqlharness_watch", "sqlharness_snapshot", "sqlharness_artifact" };
        foreach (var tool in targetTools)
        {
            Assert.Contains("target", Required(requestSchemas[tool]));
            Assert.True(Properties(requestSchemas[tool]).TryGetProperty("target", out var scopeSchema), requestSchemas[tool].GetRawText());
            Assert.True(scopeSchema.TryGetProperty("type", out var scopeType), requestSchemas[tool].GetRawText());
            Assert.Equal("object", scopeType.GetString());
            Assert.Equal(new[] { "profile", "vars" }, scopeSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));
            Assert.True(scopeSchema.GetProperty("properties").TryGetProperty("profile", out _));
            Assert.True(scopeSchema.GetProperty("properties").TryGetProperty("vars", out _));
        }
        Assert.Contains("subject", Required(requestSchemas["sqlharness_compare"]));
        Assert.Contains("scope", Required(requestSchemas["sqlharness_compare"]));
        Assert.True(Properties(requestSchemas["sqlharness_measure"]).TryGetProperty("subject", out _));
        Assert.True(Properties(requestSchemas["sqlharness_measure"]).TryGetProperty("scope", out _));
        Assert.DoesNotContain("subject", Required(requestSchemas["sqlharness_measure"]));
        Assert.DoesNotContain("scope", Required(requestSchemas["sqlharness_measure"]));
        Assert.Equal("string", Properties(requestSchemas["sqlharness_compare"]).GetProperty("subject").GetProperty("type").GetString());
        Assert.Equal("string", Properties(requestSchemas["sqlharness_compare"]).GetProperty("scope").GetProperty("type").GetString());
        foreach (var tool in new[] { "sqlharness_capabilities", "sqlharness_plan" })
            Assert.DoesNotContain("scope", Properties(requestSchemas[tool]).EnumerateObject().Select(property => property.Name));
        var rawNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in requestSchemas.Values) CollectPropertyNames(schema, rawNames);
        Assert.DoesNotContain("server", rawNames);
        Assert.DoesNotContain("database", rawNames);
        Assert.DoesNotContain("auth", rawNames);
        Assert.DoesNotContain("engine", rawNames);
        Assert.DoesNotContain("allowMutation", rawNames);
        Assert.DoesNotContain("approved", rawNames);
        Assert.DoesNotContain("confirmDatabase", rawNames);

        await using var fixedServed = await ServedCatalog.CreateAsync(scope);
        var fixedTools = await fixedServed.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        Assert.InRange(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(fixedTools.Select(tool => tool.ProtocolTool).ToArray())), 1, 32768);
        var fixedSchemas = fixedTools.ToDictionary(tool => tool.Name, ServedSchema, StringComparer.Ordinal);
        foreach (var tool in targetTools)
            Assert.DoesNotContain("target", Properties(fixedSchemas[tool]).EnumerateObject().Select(property => property.Name));
        foreach (var tool in projectedTools)
            Assert.True(Properties(fixedSchemas[tool]).TryGetProperty("maxCellChars", out _), $"{tool} must expose maxCellChars.");
        var fixedArtifactProperties = Properties(fixedSchemas["sqlharness_artifact"]);
        Assert.True(fixedArtifactProperties.TryGetProperty("maxResultBytes", out var fixedArtifactBudget), fixedSchemas["sqlharness_artifact"].GetRawText());
        Assert.Equal(McpLimits.MinCallToolResultBudgetBytes, fixedArtifactBudget.GetProperty("minimum").GetInt32());
        Assert.Equal(McpLimits.MaxCallToolResultBudgetBytes, fixedArtifactBudget.GetProperty("maximum").GetInt32());
        Assert.True(fixedArtifactProperties.TryGetProperty("pageSize", out var fixedArtifactPageSize), fixedSchemas["sqlharness_artifact"].GetRawText());
        Assert.Equal(1, fixedArtifactPageSize.GetProperty("minimum").GetInt32());
        Assert.Equal(512, fixedArtifactPageSize.GetProperty("maximum").GetInt32());
        Assert.False(Properties(fixedSchemas["sqlharness_capabilities"]).TryGetProperty("maxCellChars", out _));
        Assert.Contains("subject", Required(fixedSchemas["sqlharness_compare"]));
        Assert.Contains("scope", Required(fixedSchemas["sqlharness_compare"]));
        Assert.True(Properties(fixedSchemas["sqlharness_measure"]).TryGetProperty("subject", out _));
        Assert.True(Properties(fixedSchemas["sqlharness_measure"]).TryGetProperty("scope", out _));
        Assert.DoesNotContain("subject", Required(fixedSchemas["sqlharness_measure"]));
        Assert.DoesNotContain("scope", Required(fixedSchemas["sqlharness_measure"]));
    }

    [Fact]
    public async Task Artifact_requests_raise_the_call_budget_and_reconstruct_statement_pages()
    {
        var process = McpProcessContext.Create(new McpServerOptions
        {
            Profile = ProfileName,
            Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tenant"] = "frozen" },
            MaxResultBytes = 262144,
        }, () => ProfileStore.Load(_targetsFile));
        var scope = process.FixedScope!;
        Directory.CreateDirectory(SqlHarnessPaths.CompareDir);
        var statements = Enumerable.Range(0, 512).Select(ordinal => new ArtifactStatementMetric(
            "baseline", "synthetic-parameter-set-name-with-stable-source-identity-regression", null, ordinal, new string((char)('a' + ordinal % 26), 64), 1, 2, 1,
            [new StatementOperatorSummary(ordinal, new string('o', 32), new string('t', 24), new string('i', 24), 1, 1, 1)])).ToArray();
        WriteStatementsArtifact("large-statements", scope.Owner, statements);
        WriteStatementsArtifact("foreign-statements", new ArtifactOwner("other-profile", new Dictionary<string, string>(), "sqlserver", "other", "other"), statements);
        var firstSourcePage = Assert.IsType<ArtifactStatementsSection>(ArtifactReader.ReadSection(
            SqlHarnessPaths.CompareDir, "large-statements", "statements", scope.Owner));
        Assert.Equal(128, firstSourcePage.Statements.Count);
        Assert.Equal(128, firstSourcePage.Continuation);
        await using var served = await ServedCatalog.CreateAsync(process);

        var defaultResult = await served.Client.CallToolAsync("sqlharness_artifact", new Dictionary<string, object?>
        {
            ["id"] = "large-statements",
            ["section"] = "statements",
        }, cancellationToken: CancellationToken.None);
        var raisedResult = await served.Client.CallToolAsync("sqlharness_artifact", new Dictionary<string, object?>
        {
            ["id"] = "large-statements",
            ["section"] = "statements",
            ["maxResultBytes"] = 131072,
            ["pageSize"] = 512,
        }, cancellationToken: CancellationToken.None);

        var defaultText = Assert.Single(defaultResult.Content.OfType<TextContentBlock>()).Text;
        Assert.Equal("success", ServedEnvelope(defaultResult, "sqlharness_artifact").GetProperty("status").GetString());
        var defaultBytes = Encoding.UTF8.GetByteCount(defaultText);
        Assert.InRange(defaultBytes, 1, 65536);
        var defaultProjection = ServedEnvelope(defaultResult, "sqlharness_artifact").GetProperty("result");
        Assert.Equal(128, defaultProjection.GetProperty("statements").GetArrayLength());
        Assert.Equal(128, defaultProjection.GetProperty("continuation").GetInt32());

        var raisedText = Assert.Single(raisedResult.Content.OfType<TextContentBlock>()).Text;
        Assert.Equal("success", ServedEnvelope(raisedResult, "sqlharness_artifact").GetProperty("status").GetString());
        var raisedBytes = Encoding.UTF8.GetByteCount(raisedText);
        Assert.InRange(raisedBytes, 65537, 131072);
        var raisedProjection = ServedEnvelope(raisedResult, "sqlharness_artifact").GetProperty("result");
        var allStatementOrdinals = raisedProjection.GetProperty("statements").EnumerateArray()
            .Select(statement => statement.GetProperty("statementOrdinal").GetInt32()).ToList();
        Assert.All(raisedProjection.GetProperty("statements").EnumerateArray(), statement =>
        {
            Assert.Equal(64, statement.GetProperty("statementHash").GetString()!.Length);
            Assert.Single(statement.GetProperty("topOperators").EnumerateArray());
        });
        Assert.InRange(allStatementOrdinals.Count, 129, 512);
        var continuation = raisedProjection.GetProperty("continuation");
        Assert.Equal(allStatementOrdinals.Count < 512 ? allStatementOrdinals.Count : (int?)null,
            continuation.ValueKind == JsonValueKind.Null ? null : continuation.GetInt32());
        var cursor = 0;
        var pageCount = 1;
        while (continuation.ValueKind != JsonValueKind.Null)
        {
            var next = continuation.GetInt32();
            Assert.True(next > cursor, $"Continuation cursor must advance beyond {cursor}, but was {next}.");
            Assert.InRange(++pageCount, 2, 512);
            var nextPage = await served.Client.CallToolAsync("sqlharness_artifact", new Dictionary<string, object?>
            {
                ["id"] = "large-statements",
                ["section"] = "statements",
                ["cursor"] = next,
                ["maxResultBytes"] = 131072,
                ["pageSize"] = 512,
            }, cancellationToken: CancellationToken.None);
            var nextText = Assert.Single(nextPage.Content.OfType<TextContentBlock>()).Text;
            Assert.InRange(Encoding.UTF8.GetByteCount(nextText), 1, 131072);
            var nextProjection = ServedEnvelope(nextPage, "sqlharness_artifact").GetProperty("result");
            var nextStatements = nextProjection.GetProperty("statements").EnumerateArray()
                .Select(statement => statement.GetProperty("statementOrdinal").GetInt32()).ToArray();
            Assert.All(nextProjection.GetProperty("statements").EnumerateArray(), statement =>
            {
                Assert.Equal(64, statement.GetProperty("statementHash").GetString()!.Length);
                Assert.Single(statement.GetProperty("topOperators").EnumerateArray());
            });
            Assert.NotEmpty(nextStatements);
            Assert.Equal(next, nextStatements[0]);
            allStatementOrdinals.AddRange(nextStatements);
            if (!nextProjection.TryGetProperty("continuation", out continuation))
                break;
            cursor = next;
        }
        Assert.Equal(Enumerable.Range(0, 512), allStatementOrdinals);

    }

    [Theory]
    [InlineData(1, "statements", true)]
    [InlineData(null, "statements", true)]
    [InlineData(512, "statements", true)]
    [InlineData(0, "statements", false)]
    [InlineData(513, "statements", false)]
    [InlineData(1, "summary", false)]
    public async Task Artifact_page_size_accepts_only_supported_windows(int? pageSize, string section, bool accepted)
    {
        var process = ArtifactProcess();
        WriteStatementsArtifact("page-size-statements", process.FixedScope!.Owner, SyntheticArtifactStatements());
        await using var served = await ServedCatalog.CreateAsync(process);
        var arguments = new Dictionary<string, object?>
        {
            ["id"] = "page-size-statements",
            ["section"] = section,
        };
        if (pageSize is int requested)
            arguments["pageSize"] = requested;
        var result = await served.Client.CallToolAsync("sqlharness_artifact", arguments, cancellationToken: CancellationToken.None);
        var envelope = ServedEnvelope(result, "sqlharness_artifact");
        if (accepted)
        {
            Assert.Equal("success", envelope.GetProperty("status").GetString());
            if (pageSize == 1)
                Assert.Single(envelope.GetProperty("result").GetProperty("statements").EnumerateArray());
        }
        else
        {
            var message = envelope.GetProperty("error").GetProperty("message").GetString();
            Assert.Contains("pageSize", message, StringComparison.Ordinal);
            Assert.Contains(section == "summary" ? "pageable" : "1..512", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Unknown_and_foreign_artifacts_stay_content_free_with_selectors_and_large_budget()
    {
        var process = ArtifactProcess();
        WriteStatementsArtifact("foreign-statements",
            new ArtifactOwner("other-profile", new Dictionary<string, string>(), "sqlserver", "other", "other"),
            SyntheticArtifactStatements());
        await using var served = await ServedCatalog.CreateAsync(process);
        foreach (var id in new[] { "missing-artifact", "foreign-statements" })
        {
            var refused = await served.Client.CallToolAsync("sqlharness_artifact", new Dictionary<string, object?>
            {
                ["id"] = id,
                ["section"] = "plan",
                ["variant"] = "secret-selector",
                ["statement"] = 999,
                ["repetition"] = 999,
                ["includeText"] = true,
                ["maxResultBytes"] = 131072,
                ["pageSize"] = 512,
            }, cancellationToken: CancellationToken.None);
            var refusalText = Assert.Single(refused.Content.OfType<TextContentBlock>()).Text;
            using var refusalDocument = JsonDocument.Parse(refusalText);
            var refusalEnvelope = refusalDocument.RootElement;
            Assert.True(!refusalEnvelope.TryGetProperty("result", out var refusalResult)
                || refusalResult.ValueKind == JsonValueKind.Null);
            Assert.DoesNotContain("\"statements\"", refusalText, StringComparison.Ordinal);
            Assert.DoesNotContain("statementOrdinal", refusalText, StringComparison.Ordinal);
            Assert.DoesNotContain("statementHash", refusalText, StringComparison.Ordinal);
            Assert.DoesNotContain("foreign-statements", refusalText, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-selector", refusalText, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('a', 64), refusalText, StringComparison.Ordinal);
            Assert.DoesNotContain("\"statementOrdinal\":0", refusalText, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('o', 32), refusalText, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('t', 24), refusalText, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('i', 24), refusalText, StringComparison.Ordinal);
        }
    }

    private McpProcessContext ArtifactProcess() => McpProcessContext.Create(new McpServerOptions
    {
        Profile = ProfileName,
        Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tenant"] = "frozen" },
        MaxResultBytes = 262144,
    }, () => ProfileStore.Load(_targetsFile));

    private static ArtifactStatementMetric[] SyntheticArtifactStatements() => Enumerable.Range(0, 512).Select(ordinal => new ArtifactStatementMetric(
        "baseline", null, null, ordinal, new string((char)('a' + ordinal % 26), 64), 1, 2, 1,
        [new StatementOperatorSummary(ordinal, new string('o', 32), new string('t', 24), new string('i', 24), 1, 1, 1)])).ToArray();

    private static void WriteStatementsArtifact(string id, ArtifactOwner owner, IReadOnlyList<ArtifactStatementMetric> statements)
    {
        var directory = Path.Combine(SqlHarnessPaths.CompareDir, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(
            new ArtifactManifest(ArtifactReader.CurrentManifestVersion, ArtifactReader.CompareKind, "report.json", ["statements"])
            { Owner = owner }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(Path.Combine(directory, "statements.json"), JsonSerializer.Serialize(
            new ArtifactStatementsSection(id, ArtifactReader.CompareKind, statements, 0),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task Compare_policy_is_frozen_per_catalog_and_rejects_hidden_or_mistyped_fields()
    {
        var scope = Scope();
        var onPolicy = new CompareConfig { RequireContext = true };
        var offPolicy = new CompareConfig { RequireContext = false };
        var onProcess = McpProcessContext.Create(new McpServerOptions { RequestScope = true, AllowedProfiles = [ProfileName] }, () => scope.Profiles, onPolicy);
        var offProcess = McpProcessContext.Create(new McpServerOptions { RequestScope = true, AllowedProfiles = [ProfileName] }, () => scope.Profiles, offPolicy);
        await using var onServed = await ServedCatalog.CreateAsync(onProcess);
        await using var offServed = await ServedCatalog.CreateAsync(offProcess);
        var onTools = await onServed.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var offTools = await offServed.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var onSchema = ServedSchema(Assert.Single(onTools, tool => tool.Name == "sqlharness_compare"));
        var offSchema = ServedSchema(Assert.Single(offTools, tool => tool.Name == "sqlharness_compare"));

        Assert.Contains("subject", Required(onSchema));
        Assert.Contains("scope", Required(onSchema));
        Assert.DoesNotContain("subject", Properties(offSchema).EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("scope", Properties(offSchema).EnumerateObject().Select(property => property.Name));
        Assert.Same(onPolicy, onProcess.CompareConfig);
        Assert.Same(offPolicy, offProcess.CompareConfig);
        var onBytes = Encoding.UTF8.GetByteCount(onSchema.GetRawText());
        var offBytes = Encoding.UTF8.GetByteCount(offSchema.GetRawText());
        _output.WriteLine($"compare inputSchema UTF-8 bytes: on={onBytes}, off={offBytes}, delta={onBytes - offBytes}");
        Assert.True(onBytes > offBytes);

        await using var fixedOffServed = await ServedCatalog.CreateAsync(scope, offPolicy);
        var fixedOffSchema = ServedSchema(Assert.Single(await fixedOffServed.Client.ListToolsAsync(cancellationToken: CancellationToken.None), tool => tool.Name == "sqlharness_compare"));
        Assert.DoesNotContain("subject", Properties(fixedOffSchema).EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("scope", Properties(fixedOffSchema).EnumerateObject().Select(property => property.Name));

        var hidden = await offServed.Client.CallToolAsync("sqlharness_compare", new Dictionary<string, object?>
        {
            ["baseline"] = new { sql = "SELECT 1" },
            ["candidate"] = new { sql = "SELECT 1" },
            ["subject"] = "query",
            ["scope"] = "whole",
        }, cancellationToken: CancellationToken.None);
        Assert.True(hidden.IsError == true);

        var hiddenFixed = await fixedOffServed.Client.CallToolAsync("sqlharness_compare", new Dictionary<string, object?>
        {
            ["baseline"] = new { sql = "SELECT 1" },
            ["candidate"] = new { sql = "SELECT 1" },
            ["subject"] = "query",
            ["scope"] = "whole",
        }, cancellationToken: CancellationToken.None);
        Assert.True(hiddenFixed.IsError == true);

        var target = new { profile = ProfileName, vars = new Dictionary<string, string> { ["tenant"] = "frozen" } };
        var validContext = await onServed.Client.CallToolAsync("sqlharness_compare", new Dictionary<string, object?>
        {
            ["target"] = target,
            ["baseline"] = new { sql = "SELECT 1" },
            ["candidate"] = new { sql = "SELECT 1" },
            ["subject"] = "query",
            ["scope"] = "whole",
            ["dryRun"] = true,
        }, cancellationToken: CancellationToken.None);
        Assert.False(validContext.IsError == true, string.Concat(validContext.Content.OfType<TextContentBlock>().Select(block => block.Text)));

        var oldScopeAlias = await offServed.Client.CallToolAsync("sqlharness_query", new Dictionary<string, object?>
        {
            ["scope"] = target,
            ["dryRun"] = true,
            ["sql"] = "SELECT 1",
        }, cancellationToken: CancellationToken.None);
        Assert.True(oldScopeAlias.IsError == true);

        var targetFreeOptions = new Dictionary<string, Dictionary<string, object?>>
        {
            ["sqlharness_query"] = new() { ["sql"] = "SELECT 1", ["dryRun"] = true },
            ["sqlharness_measure"] = new() { ["query"] = new { sql = "SELECT 1" }, ["dryRun"] = true },
            ["sqlharness_watch"] = new() { ["sql"] = "SELECT 1" },
            ["sqlharness_snapshot"] = new() { ["action"] = "capture", ["name"] = "sample", ["sql"] = "SELECT 1" },
            ["sqlharness_artifact"] = new() { ["id"] = "absent", ["section"] = "summary" },
        };
        foreach (var (tool, arguments) in targetFreeOptions)
        {
            arguments["target"] = target;
            arguments["scope"] = "whole";
            var textScope = await offServed.Client.CallToolAsync(tool, arguments, cancellationToken: CancellationToken.None);
            Assert.True(textScope.IsError == true, tool);
        }

        var legacyScopeArguments = new Dictionary<string, Dictionary<string, object?>>
        {
            ["sqlharness_capabilities"] = new(),
            ["sqlharness_query"] = new() { ["sql"] = "SELECT 1", ["dryRun"] = true, ["target"] = target },
            ["sqlharness_mutation"] = new() { ["sql"] = "INSERT INTO dbo.T (Id) VALUES (1)", ["dryRun"] = true, ["target"] = target },
            ["sqlharness_measure"] = new() { ["query"] = new { sql = "SELECT 1" }, ["dryRun"] = true, ["target"] = target },
            ["sqlharness_compare"] = new() { ["baseline"] = new { sql = "SELECT 1" }, ["candidate"] = new { sql = "SELECT 1" }, ["target"] = target },
            ["sqlharness_watch"] = new() { ["sql"] = "SELECT 1", ["target"] = target },
            ["sqlharness_snapshot"] = new() { ["action"] = "capture", ["name"] = "sample", ["sql"] = "SELECT 1", ["target"] = target },
            ["sqlharness_plan"] = new() { ["content"] = "<a/>" },
            ["sqlharness_artifact"] = new() { ["id"] = "absent", ["section"] = "summary", ["target"] = target },
        };
        Assert.Equal(McpToolCatalog.ToolNames.Order(StringComparer.Ordinal), legacyScopeArguments.Keys.Order(StringComparer.Ordinal));
        foreach (var (tool, arguments) in legacyScopeArguments)
        {
            arguments["scope"] = target;
            var oldAlias = await offServed.Client.CallToolAsync(tool, arguments, cancellationToken: CancellationToken.None);
            Assert.True(oldAlias.IsError == true, tool);
        }

        var missingContext = await onServed.Client.CallToolAsync("sqlharness_compare", new Dictionary<string, object?>
        {
            ["target"] = target,
            ["baseline"] = new { sql = "SELECT 1" },
            ["candidate"] = new { sql = "SELECT 1" },
            ["subject"] = "query",
            ["dryRun"] = true,
        }, cancellationToken: CancellationToken.None);
        Assert.True(missingContext.IsError == true);

        var wrongScopeType = await onServed.Client.CallToolAsync("sqlharness_compare", new Dictionary<string, object?>
        {
            ["baseline"] = new { sql = "SELECT 1" },
            ["candidate"] = new { sql = "SELECT 1" },
            ["subject"] = "query",
            ["scope"] = target,
        }, cancellationToken: CancellationToken.None);
        Assert.True(wrongScopeType.IsError == true);
    }

    [Fact]
    public async Task Host_freezes_config_until_restart_and_reloads_it_for_a_new_stdio_process()
    {
        var scope = Scope();
        var configPath = SqlHarnessPaths.ConfigFile;
        SqlHarnessConfigWriter.Write(configPath, new SqlHarnessConfig { Compare = new CompareConfig { RequireContext = true } });
        await using var firstHost = await HostedCatalog.CreateAsync(scope);
        var firstSchema = ServedSchema(Assert.Single(await firstHost.Client.ListToolsAsync(cancellationToken: CancellationToken.None), tool => tool.Name == "sqlharness_compare"));
        Assert.Contains("subject", Required(firstSchema));
        Assert.Contains("scope", Required(firstSchema));

        SqlHarnessConfigWriter.Write(configPath, new SqlHarnessConfig { Compare = new CompareConfig { RequireContext = false } });
        var sameHostSchema = ServedSchema(Assert.Single(await firstHost.Client.ListToolsAsync(cancellationToken: CancellationToken.None), tool => tool.Name == "sqlharness_compare"));
        Assert.Contains("subject", Required(sameHostSchema));
        Assert.Contains("scope", Required(sameHostSchema));

        await using var restartedHost = await HostedCatalog.CreateAsync(scope);
        var restartedSchema = ServedSchema(Assert.Single(await restartedHost.Client.ListToolsAsync(cancellationToken: CancellationToken.None), tool => tool.Name == "sqlharness_compare"));
        Assert.DoesNotContain("subject", Properties(restartedSchema).EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("scope", Properties(restartedSchema).EnumerateObject().Select(property => property.Name));

        var result = await restartedHost.Client.CallToolAsync("sqlharness_compare", new Dictionary<string, object?>
        {
            ["target"] = new { profile = ProfileName, vars = new Dictionary<string, string> { ["tenant"] = "frozen" } },
            ["baseline"] = new { sql = "SELECT 1" },
            ["candidate"] = new { sql = "SELECT 1" },
            ["dryRun"] = true,
        }, cancellationToken: CancellationToken.None);
        Assert.False(result.IsError == true, Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
    }

    [Fact]
    public async Task Capabilities_report_configured_execution_timeout_in_fixed_and_request_scope()
    {
        var scope = Scope();
        var config = new SqlHarnessConfig { Execution = new ExecutionConfig { DefaultTimeoutSeconds = 47 } };

        var fixedProcess = McpProcessContext.Create(new McpServerOptions
        {
            Profile = ProfileName,
            Vars = new Dictionary<string, string> { ["tenant"] = "frozen" },
        }, () => scope.Profiles, config: config);
        await using var fixedServed = await ServedCatalog.CreateAsync(fixedProcess);
        var fixedCapabilities = await fixedServed.Client.CallToolAsync("sqlharness_capabilities", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
        Assert.Equal(47, ServedEnvelope(fixedCapabilities, "sqlharness_capabilities").GetProperty("result").GetProperty("defaultExecutionTimeoutSeconds").GetInt32());

        var requestProcess = McpProcessContext.Create(new McpServerOptions
        {
            RequestScope = true,
            AllowedProfiles = [ProfileName],
        }, () => scope.Profiles, config: config);
        await using var requestServed = await ServedCatalog.CreateAsync(requestProcess);
        var requestCapabilities = await requestServed.Client.CallToolAsync("sqlharness_capabilities", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
        Assert.Equal(47, ServedEnvelope(requestCapabilities, "sqlharness_capabilities").GetProperty("result").GetProperty("defaultExecutionTimeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task Request_calls_resolve_each_supplied_scope_and_target_free_tools_need_none()
    {
        var scope = Scope();
        var profiles = scope.Profiles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        profiles["sample-b"] = new TargetProfile("mcp-b.invalid", "reportdb-b",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = "^b$" }, "integrated");
        var process = McpProcessContext.Create(new McpServerOptions
        {
            RequestScope = true,
            AllowedProfiles = [ProfileName, "sample-b"],
        }, () => profiles);
        await using var served = await ServedCatalog.CreateAsync(process);

        var capabilities = await served.Client.CallToolAsync("sqlharness_capabilities", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
        Assert.False(capabilities.IsError == true);
        var capabilityDocument = JsonDocument.Parse(Assert.Single(capabilities.Content.OfType<TextContentBlock>()).Text);
        var capability = capabilityDocument.RootElement.GetProperty("result");
        Assert.Equal("request", capability.GetProperty("scopeMode").GetString());
        Assert.Equal(30, capability.GetProperty("connectionTimeoutSeconds").GetInt32());
        Assert.Equal(new[] { ProfileName, "sample-b" }, capability.GetProperty("allowedProfiles").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain("frozen", capabilityDocument.RootElement.GetRawText(), StringComparison.Ordinal);

        var valid = new Dictionary<string, object?>
        {
            ["dryRun"] = true,
            ["sql"] = "SELECT 1",
            ["target"] = new { profile = ProfileName, vars = new Dictionary<string, string> { ["tenant"] = "frozen" } },
        };
        var first = await served.Client.CallToolAsync("sqlharness_query", valid, cancellationToken: CancellationToken.None);
        Assert.False(first.IsError == true);
        var second = await served.Client.CallToolAsync("sqlharness_query", new Dictionary<string, object?>
        {
            ["dryRun"] = true,
            ["sql"] = "SELECT 1",
            ["target"] = new { profile = "sample-b", vars = new Dictionary<string, string> { ["tenant"] = "b" } },
        }, cancellationToken: CancellationToken.None);
        Assert.False(second.IsError == true);
        var wrongB = await served.Client.CallToolAsync("sqlharness_query", new Dictionary<string, object?>
        {
            ["dryRun"] = true,
            ["sql"] = "SELECT 1",
            ["target"] = new { profile = "sample-b", vars = new Dictionary<string, string> { ["tenant"] = "frozen" } },
        }, cancellationToken: CancellationToken.None);
        Assert.Equal("error", ServedEnvelope(wrongB, "sqlharness_query").GetProperty("status").GetString());
        var malformedScope = new Dictionary<string, object?>
        {
            ["dryRun"] = true,
            ["sql"] = "SELECT 1",
            ["target"] = new { profile = ProfileName, vars = new Dictionary<string, string> { ["tenant"] = "frozen" }, extra = "sentinel" },
        };
        var rejected = await served.Client.CallToolAsync("sqlharness_query", malformedScope, cancellationToken: CancellationToken.None);
        Assert.True(rejected.IsError == true);
        Assert.DoesNotContain("sentinel", string.Concat(rejected.Content.OfType<TextContentBlock>().Select(block => block.Text)), StringComparison.Ordinal);
        var noScope = await served.Client.CallToolAsync("sqlharness_query", new Dictionary<string, object?> { ["dryRun"] = true, ["sql"] = "SELECT 1" }, cancellationToken: CancellationToken.None);
        Assert.True(noScope.IsError == true);

        await using var fixedServed = await ServedCatalog.CreateAsync(scope);
        var fixedWithScope = await fixedServed.Client.CallToolAsync("sqlharness_query", new Dictionary<string, object?>
        {
            ["dryRun"] = true,
            ["sql"] = "SELECT 1",
            ["scope"] = new { profile = ProfileName, vars = new Dictionary<string, string> { ["tenant"] = "frozen" } },
        }, cancellationToken: CancellationToken.None);
        Assert.True(fixedWithScope.IsError == true);

        var planXml = File.ReadAllText(McpStdioProcessHarness.FindRepositoryFile("tests", "SqlHarness.Tests", "Fixtures", "distiller-sample.sqlplan"));
        var plan = await served.Client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["content"] = planXml }, cancellationToken: CancellationToken.None);
        Assert.False(plan.IsError == true);
    }

    // Synthetic Showplan: the predicate carries a plain literal, quotes and brackets; statement text and predicates
    // are returned unchanged from the caller-supplied plan.
    private const string PlanLiteral = "N'ACTIVE'";
    private const string PlanPasswordValue = "hunter2-synthetic";
    private const string PlanTokenValue = "tok-synthetic-9f3";

    private static string ShowplanWithText(string statementText) =>
        "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements>"
        + "<StmtSimple StatementText=\"" + System.Security.SecurityElement.Escape(statementText) + "\"><QueryPlan>"
        + "<RelOp NodeId=\"0\" PhysicalOp=\"Clustered Index Scan\" LogicalOp=\"Clustered Index Scan\" EstimateRows=\"5\" EstimatedTotalSubtreeCost=\"1\">"
        + "<IndexScan><Object Table=\"[dbo].[Orders]\" Index=\"[PK_Orders]\" />"
        + "<Predicate><ScalarOperator ScalarString=\"[dbo].[Orders].[Status]=" + PlanLiteral + " AND [Name]=&quot;x&quot;\" /></Predicate>"
        + "</IndexScan></RelOp></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>";

    private static string DefaultStatementText =>
        $"SELECT * FROM dbo.Orders WHERE Status = {PlanLiteral} AND password='{PlanPasswordValue}' AND access_token={PlanTokenValue}";

    private const string PostgresPlan =
        "[{\"Plan\":{\"Node Type\":\"Hash Join\",\"Join Type\":\"Inner\",\"Total Cost\":100.0,\"Plan Rows\":10,\"Actual Rows\":10,\"Actual Loops\":1,"
        + "\"Hash Cond\":\"(a.id = b.id)\",\"Plans\":[{\"Node Type\":\"Seq Scan\",\"Relation Name\":\"foo\",\"Total Cost\":40.0,\"Plan Rows\":100,\"Actual Rows\":100,"
        + "\"Actual Loops\":1,\"Filter\":\"((status = 'ACTIVE'::text) AND (password = 'hunter2-synthetic'))\"}]},\"Planning Time\":10.4,\"Execution Time\":4.6}]";

    private static (JsonElement Statement, JsonElement Root, string Raw) PlanResult(CallToolResult result)
    {
        Assert.False(result.IsError == true, string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
        var envelope = ServedEnvelope(result, "sqlharness_plan");
        var statement = envelope.GetProperty("result").GetProperty("statements")[0];
        return (statement, statement.GetProperty("root"), Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private string WriteInput(string name, string content)
    {
        var root = Path.Combine(_home, "plan-inputs");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private McpScope ScopeWithInputRoot()
    {
        Directory.CreateDirectory(Path.Combine(_home, "plan-inputs"));
        return McpScope.Create(
            new McpServerOptions
            {
                Profile = ProfileName,
                Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tenant"] = "frozen" },
                InputRoots = [Path.Combine(_home, "plan-inputs")],
            },
            ProfileStore.Load(_targetsFile));
    }

    private async Task<ServedCatalog> ServePlanModeAsync(bool requestMode)
    {
        var scope = ScopeWithInputRoot();
        if (!requestMode)
            return await ServedCatalog.CreateAsync(scope);
        var process = McpProcessContext.Create(new McpServerOptions
        {
            RequestScope = true,
            AllowedProfiles = [ProfileName],
            InputRoots = [Path.Combine(_home, "plan-inputs")],
        }, () => scope.Profiles);
        return await ServedCatalog.CreateAsync(process);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_tool_defaults_to_bounded_text_unchanged_for_sqlserver(bool requestMode)
    {
        await using var served = await ServePlanModeAsync(requestMode);
        var xml = ShowplanWithText(DefaultStatementText);
        var inline = PlanResult(await served.Client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["content"] = xml }, cancellationToken: CancellationToken.None));

        var statementText = Text(inline.Statement, "sql");
        Assert.NotNull(statementText);
        Assert.Contains(PlanLiteral, statementText, StringComparison.Ordinal);
        Assert.Contains(PlanPasswordValue, statementText, StringComparison.Ordinal);
        Assert.Contains(PlanTokenValue, statementText, StringComparison.Ordinal);
        var predicate = Text(inline.Root, "predicate");
        Assert.NotNull(predicate);
        Assert.Contains("=" + PlanLiteral, predicate, StringComparison.Ordinal);
        // JSON escaping round-trips quotes; brackets stay literal.
        Assert.Contains("[Name]=\"x\"", predicate, StringComparison.Ordinal);
        Assert.DoesNotContain("[Name]=\"x\"", inline.Raw, StringComparison.Ordinal);
        Assert.Contains(PlanPasswordValue, inline.Raw, StringComparison.Ordinal);
        Assert.Contains(PlanTokenValue, inline.Raw, StringComparison.Ordinal);
        Assert.Contains("PK_Orders", Text(inline.Root, "indexName")!, StringComparison.Ordinal);
        // The input document is never echoed.
        Assert.DoesNotContain("ShowPlanXML", inline.Raw, StringComparison.Ordinal);

        // Same behavior from an allowed file; the source file stays unchanged.
        var path = WriteInput("sample.sqlplan", xml);
        var fileResult = PlanResult(await served.Client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["file"] = path }, cancellationToken: CancellationToken.None));
        Assert.Contains(PlanLiteral, Text(fileResult.Root, "predicate")!, StringComparison.Ordinal);
        Assert.Contains(PlanPasswordValue, fileResult.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowPlanXML", fileResult.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("sample.sqlplan", fileResult.Raw, StringComparison.Ordinal);
        Assert.Equal(xml, File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_tool_returns_bracketed_credential_column_literals_unchanged(bool requestMode)
    {
        await using var served = await ServePlanModeAsync(requestMode);
        var xml = ShowplanWithText("SELECT * FROM dbo.Users WHERE Password = N'hunter2' AND Status = N'ACTIVE'")
            .Replace("[Status]=N'ACTIVE'", "[Users].[Password]=N'hunter2' AND [dbo].[Users].[Status]=N'ACTIVE'", StringComparison.Ordinal);
        var result = PlanResult(await served.Client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["content"] = xml }, cancellationToken: CancellationToken.None));

        Assert.Contains("hunter2", result.Raw, StringComparison.Ordinal);
        Assert.Contains("[Password]=N'hunter2'", Text(result.Root, "predicate")!, StringComparison.Ordinal);
        Assert.Contains("[Status]=N'ACTIVE'", Text(result.Root, "predicate")!, StringComparison.Ordinal);
        Assert.Contains("Status = N'ACTIVE'", Text(result.Statement, "sql")!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_tool_include_text_false_omits_statement_text_and_predicates(bool requestMode)
    {
        await using var served = await ServePlanModeAsync(requestMode);
        var xml = ShowplanWithText(DefaultStatementText);
        var path = WriteInput("meta.sqlplan", xml);
        foreach (var args in new[]
        {
            new Dictionary<string, object?> { ["content"] = xml, ["includeText"] = false },
            new Dictionary<string, object?> { ["file"] = path, ["includeText"] = false },
        })
        {
            var result = PlanResult(await served.Client.CallToolAsync("sqlharness_plan", args, cancellationToken: CancellationToken.None));
            Assert.Null(Text(result.Statement, "sql"));
            Assert.Null(Text(result.Root, "predicate"));
            Assert.DoesNotContain(PlanLiteral, result.Raw, StringComparison.Ordinal);
            Assert.DoesNotContain("Status", result.Raw, StringComparison.Ordinal);
            Assert.Equal("Clustered Index Scan", Text(result.Root, "physicalOp"));
            Assert.Contains("PK_Orders", Text(result.Root, "indexName")!, StringComparison.Ordinal);
        }

        // Explicit true matches the default.
        var explicitTrue = PlanResult(await served.Client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["content"] = xml, ["includeText"] = true }, cancellationToken: CancellationToken.None));
        Assert.Contains(PlanLiteral, Text(explicitTrue.Root, "predicate")!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_tool_keeps_postgres_predicates_by_default_and_omits_them_when_false(bool requestMode)
    {
        await using var served = await ServePlanModeAsync(requestMode);
        var path = WriteInput("pg.json", PostgresPlan);

        foreach (var args in new[]
        {
            new Dictionary<string, object?> { ["content"] = PostgresPlan },
            new Dictionary<string, object?> { ["file"] = path },
        })
        {
            var result = PlanResult(await served.Client.CallToolAsync("sqlharness_plan", args, cancellationToken: CancellationToken.None));
            Assert.Equal("(a.id = b.id)", Text(result.Root, "predicate"));
            var filter = Text(result.Root.GetProperty("children")[0], "predicate");
            Assert.NotNull(filter);
            Assert.Contains("'ACTIVE'", filter, StringComparison.Ordinal);
            Assert.Contains(PlanPasswordValue, filter, StringComparison.Ordinal);
            Assert.Contains(PlanPasswordValue, result.Raw, StringComparison.Ordinal);
            Assert.DoesNotContain("Planning Time", result.Raw, StringComparison.Ordinal);
        }
        Assert.Equal(PostgresPlan, File.ReadAllText(path));

        var hidden = PlanResult(await served.Client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["content"] = PostgresPlan, ["includeText"] = false }, cancellationToken: CancellationToken.None));
        Assert.Null(Text(hidden.Root, "predicate"));
        Assert.Null(Text(hidden.Root.GetProperty("children")[0], "predicate"));
        Assert.DoesNotContain("ACTIVE", hidden.Raw, StringComparison.Ordinal);
        Assert.Equal("Hash Join", Text(hidden.Root, "physicalOp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_tool_clips_long_statement_text_and_marks_truncation(bool requestMode)
    {
        await using var served = await ServePlanModeAsync(requestMode);
        var longText = "SELECT " + new string('x', 5000) + $" FROM dbo.Orders WHERE Status = {PlanLiteral}";
        var result = await served.Client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["content"] = ShowplanWithText(longText) }, cancellationToken: CancellationToken.None);
        var parsed = PlanResult(result);

        var clipped = Text(parsed.Statement, "sql");
        Assert.NotNull(clipped);
        Assert.True(clipped.Length <= McpLimits.DefaultMaximumCellCharacters, $"statement text is {clipped.Length} characters.");
        Assert.True(clipped.Length < longText.Length);
        var envelope = ServedEnvelope(result, "sqlharness_plan");
        Assert.True(envelope.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
        Assert.True(McpResultAdapter.MeasureBytes(result) <= (int)McpLimits.DefaultCallToolResultBudgetBytes);
    }

    [Fact]
    public async Task Plan_tool_schema_declares_include_text_defaulting_to_true_in_both_modes()
    {
        var scope = ScopeWithInputRoot();
        var process = McpProcessContext.Create(new McpServerOptions
        {
            RequestScope = true,
            AllowedProfiles = [ProfileName],
        }, () => scope.Profiles);
        await using var fixedServed = await ServedCatalog.CreateAsync(scope);
        await using var requestServed = await ServedCatalog.CreateAsync(process);
        foreach (var served in new[] { fixedServed, requestServed })
        {
            var tools = await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
            var plan = tools.Single(tool => tool.Name == "sqlharness_plan");
            var include = Properties(ServedSchema(plan)).GetProperty("includeText");
            Assert.Equal("boolean", include.GetProperty("type").GetString());
            Assert.True(include.GetProperty("default").GetBoolean());
            Assert.DoesNotContain("includeText", Required(ServedSchema(plan)));
            Assert.Contains("includeText", plan.Description ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("bounded statement text and predicates", plan.Description ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("returns metadata only", plan.Description ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("Sanitized projection only", plan.Description ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Tools_list_explains_optimization_evidence_semantics_in_both_modes()
    {
        var scope = ScopeWithInputRoot();
        var process = McpProcessContext.Create(new McpServerOptions
        {
            RequestScope = true,
            AllowedProfiles = [ProfileName],
        }, () => scope.Profiles);
        await using var fixedServed = await ServedCatalog.CreateAsync(scope);
        await using var requestServed = await ServedCatalog.CreateAsync(process);

        foreach (var served in new[] { fixedServed, requestServed })
        {
            var tools = await ServedToolsAsync(served);
            var query = ToolDescription(tools, "sqlharness_query");
            var measure = ToolDescription(tools, "sqlharness_measure");
            var compare = ToolDescription(tools, "sqlharness_compare");
            var watch = ToolDescription(tools, "sqlharness_watch");
            var plan = ToolDescription(tools, "sqlharness_plan");
            var artifact = ToolDescription(tools, "sqlharness_artifact");

            Assert.Contains("not benchmark evidence", query, StringComparison.Ordinal);
            Assert.Contains("estimated", query, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("actualRows", query, StringComparison.Ordinal);
            Assert.Contains("executions", query, StringComparison.Ordinal);
            Assert.Contains("validation: passed", query, StringComparison.Ordinal);
            Assert.Contains("execution: not-run", query, StringComparison.Ordinal);
            Assert.StartsWith(
                "Measure one query across repeats. For an optimization measurement, provide both optional subject and textual scope; this is independent of compare.requireContext.",
                measure, StringComparison.Ordinal);
            Assert.Contains("Each parameter set runs on its own session", measure, StringComparison.Ordinal);
            Assert.Contains("Results report per-set stability, not cross-set equivalence.", measure, StringComparison.Ordinal);
            Assert.Contains("pass means no elapsed regression", compare, StringComparison.Ordinal);
            Assert.Contains("not improvement", compare, StringComparison.Ordinal);
            Assert.Contains("not domain equivalence", compare, StringComparison.Ordinal);
            Assert.Contains("execution: not-run", compare, StringComparison.Ordinal);
            Assert.Contains("baseline/candidate classifications", compare, StringComparison.Ordinal);
            Assert.Contains("no continuation", watch, StringComparison.Ordinal);
            Assert.Contains("projection omissions", watch, StringComparison.Ordinal);
            Assert.Contains("omittedChildCount", plan, StringComparison.Ordinal);
            Assert.Contains("omittedSubtreeCount", plan, StringComparison.Ordinal);
            Assert.Contains("returned continuation", artifact, StringComparison.Ordinal);
            Assert.Contains("omittedChildCount", artifact, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("sqlharness_measure", "{\"setup\":{\"sql\":\"SELECT 1\"}}")]
    [InlineData("sqlharness_compare", "{\"candidate\":{\"sql\":\"SELECT 1\"}}")]
    [InlineData("sqlharness_artifact", "{\"id\":\"x\"}")]
    public async Task Missing_required_arguments_are_rejected_without_echo(string tool, string argumentsJson)
    {
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var result = await served.Client.CallToolAsync(
            tool,
            (Dictionary<string, object?>)JsonSerializer.Deserialize(
                argumentsJson,
                typeof(Dictionary<string, object?>))!,
            cancellationToken: CancellationToken.None);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError == true, text);
        Assert.DoesNotContain(SecretSql, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tools_list_fits_the_32KiB_budget()
    {
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var tools = await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(tools.Select(tool => tool.ProtocolTool)));
        Assert.True(bytes <= McpLimits.ToolsListBudgetBytes, $"tools/list is {bytes} bytes.");
    }

    [Fact]
    public async Task Enum_required_and_default_shapes_are_correct()
    {
        var schemas = await ServedSchemasAsync(Scope());

        var query = schemas["sqlharness_query"];
        Assert.Empty(Required(query));
        Assert.True(Properties(query).GetProperty("timeout").GetProperty("default").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
        Assert.Equal(50, Properties(query).GetProperty("maxRows").GetProperty("default").GetInt32());

        var measure = schemas["sqlharness_measure"];
        Assert.Equal(["query"], Required(measure));
        Assert.True(Properties(measure).TryGetProperty("subject", out _));
        Assert.True(Properties(measure).TryGetProperty("scope", out _));
        foreach (var tool in new[] { measure, schemas["sqlharness_compare"] })
        {
            var properties = Properties(tool);
            Assert.True(properties.TryGetProperty("parameterSets", out _));
            Assert.True(properties.TryGetProperty("parameterSetFiles", out _));
            Assert.True(properties.TryGetProperty("matrix", out _));
            Assert.False(properties.TryGetProperty("paramSetFiles", out _));
        }
        Assert.Equal(5, Properties(measure).GetProperty("repeat").GetProperty("default").GetInt32());

        var compare = schemas["sqlharness_compare"];
        Assert.Equal(["baseline", "candidate", "scope", "subject"], Required(compare).Order().ToArray());
        Assert.Equal(["ordered", "multiset", "set", "off"], EnumOf(compare, "compareResults"));
        Assert.Equal("ordered", Properties(compare).GetProperty("compareResults").GetProperty("default").GetString());
        Assert.Equal("boolean", Properties(compare).GetProperty("compareNullability").GetProperty("type").GetString());
        Assert.False(Properties(compare).GetProperty("compareNullability").GetProperty("default").GetBoolean());
        var matrix = Properties(compare).GetProperty("matrix");
        Assert.Equal(["name", "type", "values"], matrix.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).Order().ToArray());

        var watch = schemas["sqlharness_watch"];
        Assert.Empty(Required(watch));
        Assert.True(Properties(watch).TryGetProperty("until", out _));
        Assert.True(Properties(watch).TryGetProperty("untilUnchanged", out _));

        var snapshot = schemas["sqlharness_snapshot"];
        Assert.Equal(["action", "name"], Required(snapshot).Order().ToArray());
        Assert.Equal(["capture", "diff"], EnumOf(snapshot, "action"));
        Assert.False(Properties(snapshot).TryGetProperty("force", out _));

        var plan = schemas["sqlharness_plan"];
        Assert.Empty(Required(plan));
        Assert.True(Properties(plan).TryGetProperty("content", out _));
        Assert.True(Properties(plan).TryGetProperty("file", out _));

        var artifact = schemas["sqlharness_artifact"];
        Assert.Equal(["id", "section"], Required(artifact).Order().ToArray());
        Assert.Equal(["summary", "metrics", "operators", "statements", "matrix-cells", "plan"], EnumOf(artifact, "section"));
        Assert.Contains("cursor", Properties(artifact).EnumerateObject().Select(property => property.Name));

        var capabilities = schemas["sqlharness_capabilities"];
        Assert.Empty(Required(capabilities));
        Assert.True(Properties(capabilities).TryGetProperty("includeDiagnostics", out _));
        Assert.Equal("integer", Properties(capabilities).GetProperty("inputRootCursor").GetProperty("type").GetString());
        Assert.Equal(0, Properties(capabilities).GetProperty("inputRootCursor").GetProperty("default").GetInt32());
        Assert.False(Properties(capabilities).TryGetProperty("includeDetails", out _));
    }

    [Fact]
    public async Task Served_capabilities_use_one_shared_description_and_reject_include_details_in_both_modes()
    {
        var scope = Scope();
        var process = McpProcessContext.Create(new McpServerOptions
        {
            RequestScope = true,
            AllowedProfiles = [ProfileName],
        }, () => scope.Profiles);
        await using var fixedServed = await ServedCatalog.CreateAsync(scope);
        await using var requestServed = await ServedCatalog.CreateAsync(process);
        foreach (var served in new[] { fixedServed, requestServed })
        {
            var tools = await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
            var tool = tools.Single(item => item.Name == "sqlharness_capabilities");
            Assert.Equal(McpToolCatalog.CapabilitiesDescription, tool.Description);
            var schema = ServedSchema(tool);
            Assert.Equal(McpToolCatalog.IncludeDiagnosticsDescription, Properties(schema).GetProperty("includeDiagnostics").GetProperty("description").GetString());
            Assert.Equal(McpToolCatalog.InputRootCursorDescription, Properties(schema).GetProperty("inputRootCursor").GetProperty("description").GetString());

            var rejected = await served.Client.CallToolAsync(
                "sqlharness_capabilities",
                new Dictionary<string, object?> { ["includeDetails"] = true },
                cancellationToken: CancellationToken.None);
            Assert.True(rejected.IsError == true);
            Assert.Equal("safety_rejected", ServedEnvelope(rejected, "sqlharness_capabilities").GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Served_capabilities_accept_the_input_root_cursor_and_still_reject_unknown_arguments_in_both_modes()
    {
        var scope = Scope();
        var process = McpProcessContext.Create(new McpServerOptions
        {
            RequestScope = true,
            AllowedProfiles = [ProfileName],
        }, () => scope.Profiles);
        await using var fixedServed = await ServedCatalog.CreateAsync(scope);
        await using var requestServed = await ServedCatalog.CreateAsync(process);
        foreach (var served in new[] { fixedServed, requestServed })
        {
            var tools = await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
            var schema = ServedSchema(tools.Single(tool => tool.Name == "sqlharness_capabilities"));
            Assert.True(Properties(schema).TryGetProperty("inputRootCursor", out _));

            var paged = await served.Client.CallToolAsync(
                "sqlharness_capabilities",
                new Dictionary<string, object?> { ["inputRootCursor"] = 0, ["includeDiagnostics"] = true },
                cancellationToken: CancellationToken.None);
            Assert.False(paged.IsError == true);
            var result = ServedEnvelope(paged, "sqlharness_capabilities").GetProperty("result");
            Assert.Equal(0, result.GetProperty("inputRoots").GetArrayLength());
            Assert.True(result.GetProperty("inputRootsComplete").GetBoolean());

            var unknown = await served.Client.CallToolAsync(
                "sqlharness_capabilities",
                new Dictionary<string, object?> { ["inputRootCursor"] = 0, ["bogus"] = SecretValue },
                cancellationToken: CancellationToken.None);
            Assert.True(unknown.IsError == true);
            var envelope = ServedEnvelope(unknown, "sqlharness_capabilities");
            Assert.Equal("safety_rejected", envelope.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains("inputRootCursor", envelope.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain(SecretValue, envelope.GetRawText(), StringComparison.Ordinal);
        }
    }

    // 012/T2: a matrix value is a string or JSON null (typed NULL).
    [Fact]
    public async Task Matrix_values_schema_advertises_string_or_null_items_without_a_comma_limit()
    {
        var schemas = await ServedSchemasAsync(Scope());
        var matrix = Properties(schemas["sqlharness_compare"]).GetProperty("matrix");
        var itemType = Properties(matrix).GetProperty("values").GetProperty("items").GetProperty("type");
        Assert.Equal(JsonValueKind.Array, itemType.ValueKind);
        Assert.Equal(["null", "string"], itemType.EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray());
    }

    // The fixed-name clash is Core's last matrix check, after every value is
    // bound: reaching it over the wire proves the JSON null, the empty string
    // and the comma value went through the Core binder, offline.
    [Fact]
    public async Task Served_compare_binds_comma_empty_and_json_null_matrix_values_in_core()
    {
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var result = await served.Client.CallToolAsync(
            "sqlharness_compare",
            (Dictionary<string, object?>)JsonSerializer.Deserialize(
                "{\"baseline\":{\"sql\":\"SELECT @Label\"},\"candidate\":{\"sql\":\"SELECT @Label\"},\"subject\":\"query\",\"scope\":\"whole\","
                + "\"parameters\":[{\"name\":\"label\",\"type\":\"int\",\"value\":\"7\"}],"
                + "\"matrix\":{\"name\":\"Label\",\"type\":\"nvarchar(20)\",\"values\":[\"a,b\",\"\",null,\"null\"]}}",
                typeof(Dictionary<string, object?>))!,
            cancellationToken: CancellationToken.None);
        var envelope = ServedEnvelope(result, "sqlharness_compare");
        Assert.True(result.IsError == true);
        Assert.Equal("error", envelope.GetProperty("status").GetString());
        Assert.Equal(
            "The --matrix option for SQL parameter '@Label' duplicates a fixed parameter.",
            envelope.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Inline_frame_cap_is_advertised_on_sql_and_plan_tools()
    {
        var schemas = await ServedSchemasAsync(Scope());
        Assert.Contains("1 MiB", Properties(schemas["sqlharness_query"]).GetProperty("sql").GetProperty("description").GetString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("1 MiB", Properties(schemas["sqlharness_plan"]).GetProperty("content").GetProperty("description").GetString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Injected_sdk_members_never_leak_into_schemas()
    {
        var schemas = await ServedSchemasAsync(Scope());
        foreach (var (name, schema) in schemas)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectPropertyNames(schema, names);
            Assert.DoesNotContain("ctx", names);
            Assert.DoesNotContain("ct", names);
        }
    }

    [Theory]
    [InlineData("sqlharness_query", "{\"sql\":\"SELECT 1\",\"bogus\":1}")]
    public async Task Unknown_top_level_properties_are_rejected_without_echo(string tool, string argumentsJson)
    {
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var result = await served.Client.CallToolAsync(
            tool,
            (Dictionary<string, object?>)JsonSerializer.Deserialize(
                argumentsJson,
                typeof(Dictionary<string, object?>))!,
            cancellationToken: CancellationToken.None);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError == true, text);
        Assert.DoesNotContain("bogus", text, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSql, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_nested_parameter_properties_are_rejected_without_echo()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["sql"] = SecretSql,
            ["parameters"] = new object[]
            {
                new Dictionary<string, object?> { ["name"] = "id", ["type"] = "int", ["value"] = SecretValue, ["target"] = "smuggled" },
            },
        };
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var result = await served.Client.CallToolAsync("sqlharness_query", arguments, cancellationToken: CancellationToken.None);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError == true, text);
        Assert.DoesNotContain(SecretValue, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSql, text, StringComparison.Ordinal);
        Assert.DoesNotContain("smuggled", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_schema_property_names_a_target_auth_mutation_or_force_field()
    {
        var banned = new[] { "target", "auth", "mutation", "allowmutation", "confirmdatabase", "unsafedirect", "server", "database", "password", "secret", "credential", "engine", "vars", "force" };
        var schemas = await ServedSchemasAsync(Scope());
        foreach (var (name, schema) in schemas)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectPropertyNames(schema, names);
            foreach (var property in names)
            {
                foreach (var word in banned)
                    Assert.False(string.Equals(word, property, StringComparison.OrdinalIgnoreCase), $"{name}.{property} exposes '{word}'");
            }
        }
    }

    [Fact]
    public void All_tools_publish_no_output_schema()
    {
        var scope = Scope();
        var tools = McpToolCatalog.CreateTools(scope, scope.CreateModule());
        Assert.Equal(McpToolCatalog.ToolNames.Count, tools.Count);
        foreach (var tool in tools)
            Assert.Null(tool.ProtocolTool.OutputSchema);
    }

    [Fact]
    public async Task Served_tools_publish_no_output_schema()
    {
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var tools = (await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None)).ToList();
        Assert.Equal(ExpectedTools.Length, tools.Count);
        foreach (var tool in tools)
            Assert.Null(tool.ProtocolTool.OutputSchema);
    }

    [Fact]
    public async Task Served_capabilities_envelope_conforms_to_the_agent_contract_without_output_schema()
    {
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var tools = await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        Assert.Null(tools.Single(tool => tool.Name == "sqlharness_capabilities").ProtocolTool.OutputSchema);
        var result = await served.Client.CallToolAsync(
            "sqlharness_capabilities",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);
        Assert.False(result.IsError == true);
        var envelope = ServedEnvelope(result, "sqlharness_capabilities");
        Assert.Empty(McpResultAdapter.ValidateEnvelope(envelope));
    }
    [Fact]
    public async Task Served_query_dry_run_envelopes_conform_to_the_agent_contract()
    {
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var success = await served.Client.CallToolAsync(
            "sqlharness_query",
            new Dictionary<string, object?> { ["dryRun"] = true, ["sql"] = "SELECT 1" },
            cancellationToken: CancellationToken.None);
        Assert.False(success.IsError == true);
        var successEnvelope = ServedEnvelope(success, "sqlharness_query");
        Assert.Empty(McpResultAdapter.ValidateEnvelope(successEnvelope));
        Assert.Equal("success", successEnvelope.GetProperty("status").GetString());
        Assert.Equal("read-only", successEnvelope.GetProperty("result").GetProperty("tier").GetString());
        var failure = await served.Client.CallToolAsync(
            "sqlharness_query",
            new Dictionary<string, object?> { ["dryRun"] = true, ["sql"] = "SELECT 1", ["bogus"] = 1 },
            cancellationToken: CancellationToken.None);
        Assert.True(failure.IsError == true);
        var failureEnvelope = ServedEnvelope(failure, "sqlharness_query");
        Assert.Empty(McpResultAdapter.ValidateEnvelope(failureEnvelope));
        Assert.Equal("error", failureEnvelope.GetProperty("status").GetString());
        Assert.Equal("safety_rejected", failureEnvelope.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Served_partial_watch_and_snapshot_envelopes_conform_to_the_agent_contract()
    {
        var partialModule = new CannedModule(new SqlHarnessOutcome(
            SqlHarnessExitCode.SqlExecution, new SqlHarnessCompareMatrixReport("batch", "int", []), "Cell 2 failed."));
        await using var partialServed = await ServedCatalog.CreateAsync(Scope(), partialModule);
        var partial = await partialServed.Client.CallToolAsync(
            "sqlharness_compare",
            new Dictionary<string, object?>
            {
                ["baseline"] = new Dictionary<string, object?> { ["sql"] = "SELECT 1" },
                ["candidate"] = new Dictionary<string, object?> { ["sql"] = "SELECT 1" },
                ["subject"] = "query",
                ["scope"] = "whole",
            },
            cancellationToken: CancellationToken.None);
        Assert.True(partial.IsError == true);
        var partialEnvelope = ServedEnvelope(partial, "sqlharness_compare");
        Assert.Empty(McpResultAdapter.ValidateEnvelope(partialEnvelope));
        Assert.Equal("partial", partialEnvelope.GetProperty("status").GetString());
        Assert.False(partialEnvelope.TryGetProperty("exitCode", out _));
        var watchModule = new CannedModule(new SqlHarnessOutcome(
            SqlHarnessExitCode.WatchMaxDuration,
            new SqlHarnessWatchReport(ConformanceTarget, 4, 900000, WatchExitReason.MaxDuration, []),
            null));
        await using var watchServed = await ServedCatalog.CreateAsync(Scope(), watchModule);
        var watch = await watchServed.Client.CallToolAsync(
            "sqlharness_watch",
            new Dictionary<string, object?> { ["sql"] = "SELECT 1" },
            cancellationToken: CancellationToken.None);
        Assert.False(watch.IsError == true);
        var watchEnvelope = ServedEnvelope(watch, "sqlharness_watch");
        Assert.Empty(McpResultAdapter.ValidateEnvelope(watchEnvelope));
        Assert.Equal("watch_max_duration", watchEnvelope.GetProperty("status").GetString());
        Assert.False(watchEnvelope.TryGetProperty("exitCode", out _));
        var snapshotModule = new CannedModule(new SqlHarnessOutcome(
            SqlHarnessExitCode.SnapshotDifferences,
            new SqlHarnessSnapshotReport(
                ConformanceTarget, "before-import", SnapshotVerdict.Different, 2,
                [new SqlHarnessSnapshotDifference(0, 1, 2, "changed")]),
            null));
        await using var snapshotServed = await ServedCatalog.CreateAsync(Scope(), snapshotModule);
        var snapshot = await snapshotServed.Client.CallToolAsync(
            "sqlharness_snapshot",
            new Dictionary<string, object?> { ["action"] = "diff", ["name"] = "before-import", ["sql"] = "SELECT 1" },
            cancellationToken: CancellationToken.None);
        Assert.False(snapshot.IsError == true);
        var snapshotEnvelope = ServedEnvelope(snapshot, "sqlharness_snapshot");
        Assert.Empty(McpResultAdapter.ValidateEnvelope(snapshotEnvelope));
        Assert.Equal("snapshot_differences", snapshotEnvelope.GetProperty("status").GetString());
        Assert.False(snapshotEnvelope.TryGetProperty("exitCode", out _));
    }

    [Fact]
    public void Query_description_states_the_static_visible_effects_boundary()
    {
        // 009/T2: tool descriptions must match the real control (static
        // check of effects visible in the text; DB account role prepared
        // outside SQLHarness) and stay consistent with the T1 safetyAnalysis
        // fields without duplicating their values. No effect guarantees.
        var scope = Scope();
        var tools = McpToolCatalog.CreateTools(scope, scope.CreateModule());
        var byName = tools.ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
        var query = byName["sqlharness_query"].ProtocolTool.Description ?? string.Empty;

        Assert.Contains("static", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("visible", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("role", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outside SQLHarness", query, StringComparison.Ordinal);

        foreach (var text in new[] { query })
        {
            Assert.DoesNotContain("guarantee", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("creates", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("verifies", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no mutation", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("without mutation", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("Read-only Query (allowMutation=false) and CompareSetup", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("EXEC sp_executesql N'SELECT 1';")]
    [InlineData("EXEC xp_cmdshell 'dir';")]
    [InlineData("SELECT * FROM OPENROWSET('SQLOLEDB', 's', 'SELECT 1') AS t(x int);")]
    public async Task Served_query_dry_run_rejects_denylisted_functions_without_dispatch(string sql)
    {
        // The dry run rejects the denylist offline: the real module with an
        // unreachable target proves no connection is attempted.
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var result = await served.Client.CallToolAsync(
            "sqlharness_query",
            new Dictionary<string, object?> { ["dryRun"] = true, ["sql"] = sql },
            cancellationToken: CancellationToken.None);
        Assert.True(result.IsError == true);
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        using var document = JsonDocument.Parse(text);
        var envelope = document.RootElement;
        Assert.Equal("error", envelope.GetProperty("status").GetString());
        Assert.False(envelope.TryGetProperty("exitCode", out _));
        Assert.DoesNotContain(sql, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Served_preflight_leaves_no_side_effects()
    {
        // 009/T3: served dry-run query/capabilities mutate no state — the
        // synthetic HOME holds exactly the files the fixture wrote.
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var before = Directory.GetFiles(_home, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var dryRun = await served.Client.CallToolAsync(
            "sqlharness_query",
            new Dictionary<string, object?> { ["dryRun"] = true, ["sql"] = "SELECT 1" },
            cancellationToken: CancellationToken.None);
        Assert.NotEqual(true, dryRun.IsError);
        await served.Client.CallToolAsync(
            "sqlharness_capabilities",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);
        Assert.Equal(before, Directory.GetFiles(_home, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Watch_description_makes_no_read_only_claim()
    {
        // 009/T3 (closes the T1/T2 deferred minor): watch must not
        // claim "read-only" — the preflight only checks effects visible in
        // the text, the same reason the query description avoids the phrase.
        var scope = Scope();
        var tools = McpToolCatalog.CreateTools(scope, scope.CreateModule());
        var byName = tools.ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
        foreach (var name in new[] { "sqlharness_watch" })
        {
            var description = byName[name].ProtocolTool.Description ?? string.Empty;
            Assert.DoesNotContain("read-only", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("guarantee", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("verifies", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no mutation", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("without mutation", description, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("static", byName["sqlharness_watch"].ProtocolTool.Description ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("visible", byName["sqlharness_watch"].ProtocolTool.Description ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private const string PgProfileName = "mcp-t3-schema-pg";

    // Distinctive registry tokens: SQL Server setup orders #temp creation before the first
    // parameter reference; PostgreSQL setup warns about user functions and READ ONLY commands.
    private static readonly string[] SqlServerTokens = ["#temp", "first parameter reference"];
    private static readonly string[] PostgresTokens = ["READ ONLY", "TEMP setup", "user function", "least-privilege"];

    private McpScope PostgresScope()
    {
        var profiles = new Dictionary<string, TargetProfile>(Scope().Profiles, StringComparer.Ordinal)
        {
            [PgProfileName] = new TargetProfile(
                "mcp-unreachable.invalid", "pgdb", new Dictionary<string, string>(), "sql",
                SqlUser: "mcp", PasswordEnvVar: "SQLHARNESS_MCP_TEST_PW", Engine: "postgres"),
        };
        return McpScope.Create(new McpServerOptions { Profile = PgProfileName }, profiles);
    }

    private McpProcessContext RequestProcess()
    {
        var scope = Scope();
        return McpProcessContext.Create(
            new McpServerOptions { RequestScope = true, AllowedProfiles = [ProfileName] }, () => scope.Profiles);
    }

    private static async Task<IReadOnlyList<Tool>> ServedToolsAsync(ServedCatalog served) =>
        (await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None)).Select(tool => tool.ProtocolTool).ToList();

    private static string ToolDescription(IReadOnlyList<Tool> tools, string name) =>
        Assert.Single(tools, tool => tool.Name == name).Description ?? string.Empty;

    private static string ArgumentDescription(IReadOnlyList<Tool> tools, string name, string argument) =>
        Properties(Assert.Single(tools, tool => tool.Name == name).InputSchema).GetProperty(argument).GetProperty("description").GetString() ?? string.Empty;

    private void AssertEngineGuidance(IReadOnlyList<Tool> tools, string[] expected, string[] foreign)
    {
        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(tools));
        _output.WriteLine($"tools/list bytes: {bytes} of {McpLimits.ToolsListBudgetBytes}.");
        Assert.True(bytes <= McpLimits.ToolsListBudgetBytes, $"tools/list is {bytes} bytes.");
        var setups = new[]
        {
            ArgumentDescription(tools, "sqlharness_measure", "setup"),
            ArgumentDescription(tools, "sqlharness_compare", "setup"),
        };
        foreach (var setup in setups)
        {
            Assert.Contains("once per measured session", setup, StringComparison.OrdinalIgnoreCase);
            // Engine-neutral fact (plan 064 decision 6): stated for every engine, once.
            Assert.Contains("do not carry", setup, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, Regex.Count(setup, "do not (carry|cross)", RegexOptions.IgnoreCase));
        }
        var query = ToolDescription(tools, "sqlharness_query");
        var guided = string.Join("\n", setups.Append(query));
        foreach (var token in expected)
            Assert.Contains(token, guided, StringComparison.Ordinal);
        var all = JsonSerializer.Serialize(tools);
        foreach (var token in foreign)
            Assert.DoesNotContain(token, all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fixed_sql_server_catalog_carries_only_sql_server_guidance()
    {
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var tools = await ServedToolsAsync(served);
        AssertEngineGuidance(tools, SqlServerTokens, PostgresTokens);
        Assert.Contains("Read-only Query (allowMutation=false) and CompareSetup", ToolDescription(tools, "sqlharness_measure"), StringComparison.Ordinal);
        Assert.Contains("Read-only Query (allowMutation=false) and CompareSetup", ToolDescription(tools, "sqlharness_compare"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fixed_postgres_catalog_carries_only_postgres_guidance()
    {
        var scope = PostgresScope();
        Assert.Equal(SqlEngine.Postgres, scope.ResolvedTarget.Engine);
        await using var served = await ServedCatalog.CreateAsync(scope);
        AssertEngineGuidance(await ServedToolsAsync(served), PostgresTokens, [.. SqlServerTokens, "SQL Server", "#temps"]);
    }

    [Fact]
    public async Task Request_scope_catalog_carries_sql_server_guidance()
    {
        await using var served = await ServedCatalog.CreateAsync(RequestProcess());
        var tools = await ServedToolsAsync(served);
        AssertEngineGuidance(tools, SqlServerTokens, PostgresTokens);
        Assert.Contains("Read-only Query (allowMutation=false) and CompareSetup", ToolDescription(tools, "sqlharness_measure"), StringComparison.Ordinal);
        Assert.Contains("Read-only Query (allowMutation=false) and CompareSetup", ToolDescription(tools, "sqlharness_compare"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_scalar_parameter_descriptions_name_read_only_paths_and_mutation_rejection()
    {
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var tools = await ServedToolsAsync(served);
        var parameters = ArgumentDescription(tools, "sqlharness_query", "parameters");
        var parameterSetFiles = ArgumentDescription(tools, "sqlharness_measure", "parameterSetFiles");
        var mutationParameters = ArgumentDescription(tools, "sqlharness_mutation", "parameters");

        Assert.Contains("read-only query, measure, compare, watch, and snapshot", parameters, StringComparison.Ordinal);
        Assert.Contains("Mutation rejects non-scalar inputs", parameters, StringComparison.Ordinal);
        Assert.Contains("read-only measure and compare", parameterSetFiles, StringComparison.Ordinal);
        Assert.Contains("Table-valued and list parameters are not supported", ToolDescription(tools, "sqlharness_mutation"), StringComparison.Ordinal);
        Assert.Contains("Mutation rejects non-scalar inputs", mutationParameters, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engine_guidance_changes_descriptions_only()
    {
        await using var sqlServer = await ServedCatalog.CreateAsync(Scope());
        await using var postgres = await ServedCatalog.CreateAsync(PostgresScope());
        var sqlServerTools = (await ServedToolsAsync(sqlServer)).ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var postgresTools = (await ServedToolsAsync(postgres)).ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        Assert.Equal(sqlServerTools.Keys.Order(StringComparer.Ordinal), postgresTools.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, tool) in sqlServerTools)
        {
            var other = postgresTools[name];
            Assert.Equal(Structure(tool.InputSchema), Structure(other.InputSchema));
            Assert.Equal(JsonSerializer.Serialize(tool.Annotations), JsonSerializer.Serialize(other.Annotations));
            Assert.Null(other.OutputSchema);
        }

        Assert.NotEqual(
            ArgumentDescription([.. sqlServerTools.Values], "sqlharness_measure", "setup"),
            ArgumentDescription([.. postgresTools.Values], "sqlharness_measure", "setup"));
    }

    [Fact]
    public void Independently_built_catalogs_share_no_description_objects()
    {
        var sqlServerScope = Scope();
        var sqlServer = McpToolCatalog.CreateTools(sqlServerScope, sqlServerScope.CreateModule());
        var before = JsonSerializer.Serialize(sqlServer.Select(tool => tool.ProtocolTool));
        var postgresScope = PostgresScope();
        var postgres = McpToolCatalog.CreateTools(postgresScope, postgresScope.CreateModule());
        var again = McpToolCatalog.CreateTools(sqlServerScope, sqlServerScope.CreateModule());

        Assert.Equal(before, JsonSerializer.Serialize(sqlServer.Select(tool => tool.ProtocolTool)));
        Assert.Equal(before, JsonSerializer.Serialize(again.Select(tool => tool.ProtocolTool)));
        foreach (var tool in sqlServer)
        {
            Assert.DoesNotContain(postgres, other => ReferenceEquals(other.ProtocolTool, tool.ProtocolTool));
            Assert.DoesNotContain(again, other => ReferenceEquals(other.ProtocolTool, tool.ProtocolTool));
        }
    }

    // Structural schema with every description removed: types, required, enums and defaults stay.
    private static string Structure(JsonElement schema)
    {
        static System.Text.Json.Nodes.JsonNode? Strip(System.Text.Json.Nodes.JsonNode? node)
        {
            switch (node)
            {
                case System.Text.Json.Nodes.JsonObject obj:
                    obj.Remove("description");
                    foreach (var (_, child) in obj.ToList())
                        Strip(child);
                    break;
                case System.Text.Json.Nodes.JsonArray array:
                    foreach (var child in array)
                        Strip(child);
                    break;
            }

            return node;
        }

        return Strip(System.Text.Json.Nodes.JsonNode.Parse(schema.GetRawText()))!.ToJsonString();
    }

    [Fact]
    public void Handler_and_dto_members_name_no_target_auth_mutation_or_force_field()
    {
        var banned = new[] { "target", "auth", "mutation", "allowmutation", "confirmdatabase", "unsafedirect", "server", "database", "password", "secret", "credential", "engine", "vars", "force" };
        var dtoTypes = new[] { typeof(McpParameterArgument), typeof(McpParameterSetArgument), typeof(McpSqlSourceArgument), typeof(McpMatrixArgument) };
        foreach (var type in dtoTypes)
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance))
            {
                if (member is PropertyInfo)
                {
                    foreach (var word in banned)
                        Assert.False(string.Equals(word, member.Name, StringComparison.OrdinalIgnoreCase), type.Name + "." + member.Name + " exposes '" + word + "'");
                }
            }
        }

        foreach (var method in typeof(McpToolHandlers).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.IsSpecialName)
                continue;
            foreach (var parameter in method.GetParameters())
            {
                if (parameter.ParameterType == typeof(CancellationToken))
                    continue;
                foreach (var word in banned)
                    Assert.False(string.Equals(word, parameter.Name, StringComparison.OrdinalIgnoreCase), method.Name + "." + parameter.Name + " exposes '" + word + "'");
            }
        }
    }
}
