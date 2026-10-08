using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// T3 catalog contract: exactly the 11 spec tools with strict input schemas,
/// no target/auth/mutation surface, and unknown-property rejection without
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
        "sqlharness_inspect",
        "sqlharness_validate",
        "sqlharness_query",
        "sqlharness_measure",
        "sqlharness_compare",
        "sqlharness_watch",
        "sqlharness_snapshot",
        "sqlharness_plan",
        "sqlharness_artifact",
        "sqlharness_gain",
    ];

    private readonly string _home;
    private readonly string? _savedHome;
    private readonly string _targetsFile;

    public McpToolSchemaTests()
    {
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
        public static async Task<ServedCatalog> CreateAsync(McpScope scope, ISqlHarnessModule module)
        {
            var catalog = new ServedCatalog();
            var serverOptions = new ModelContextProtocol.Server.McpServerOptions
            {
                ServerInfo = new Implementation { Name = McpHost.ServerName, Version = "t3-test" },
                ProtocolVersion = McpHost.PinnedProtocolVersion,
            };
            McpToolCatalog.Wire(serverOptions, scope, module);
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
                    ProtocolVersion = McpHost.PinnedProtocolVersion,
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
                ProtocolVersion = McpHost.PinnedProtocolVersion,
            };
            McpToolCatalog.Wire(serverOptions, process);
            var server = McpServer.Create(
                new StreamServerTransport(catalog._clientToServer.Reader.AsStream(), catalog._serverToClient.Writer.AsStream(), "t3-test-server", NullLoggerFactory.Instance),
                serverOptions, NullLoggerFactory.Instance, serviceProvider: null);
            catalog._serverTask = server.RunAsync(catalog._cts.Token);
            catalog.Client = await McpClient.CreateAsync(
                new StreamClientTransport(catalog._clientToServer.Writer.AsStream(), catalog._serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
                new McpClientOptions { ClientInfo = new Implementation { Name = "t3-test-client", Version = "1.0.0" }, ProtocolVersion = McpHost.PinnedProtocolVersion },
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
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        using var document = JsonDocument.Parse(text);
        var envelope = document.RootElement.Clone();
        if (result.StructuredContent.HasValue)
        {
            using var structured = JsonDocument.Parse(result.StructuredContent.Value.GetRawText());
            var structuredRoot = structured.RootElement;
            Assert.Equal(envelope.GetProperty("command").GetString(), structuredRoot.GetProperty("command").GetString());
            Assert.Equal(envelope.GetProperty("status").GetString(), structuredRoot.GetProperty("status").GetString());
            Assert.Equal(envelope.GetProperty("exitCode").GetInt32(), structuredRoot.GetProperty("exitCode").GetInt32());
        }
        Assert.Equal(tool, envelope.GetProperty("command").GetString());
        return envelope;
    }

    private static void AssertEnvelopeMatchesServedSchema(JsonElement servedSchema, JsonElement envelope, string tool)
    {
        var required = servedSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).ToArray();
        foreach (var name in required)
            Assert.True(envelope.TryGetProperty(name, out _), $"{tool} envelope lacks served-required '{name}'.");
        Assert.Equal(
            servedSchema.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32(),
            envelope.GetProperty("schemaVersion").GetInt32());
        var allowed = servedSchema.GetProperty("properties").GetProperty("status").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(envelope.GetProperty("status").GetString()!, allowed);
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
    public void Catalog_registers_exactly_the_11_spec_tools()
    {
        var scope = Scope();
        var tools = McpToolCatalog.CreateTools(scope, scope.CreateModule());
        Assert.Equal(McpLimits.MaxTools, tools.Count);
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
    public async Task Request_scope_schema_is_required_only_on_the_eight_target_tools()
    {
        var scope = Scope();
        var process = McpProcessContext.Create(new McpServerOptions
        {
            RequestScope = true,
            AllowedProfiles = [ProfileName],
        }, () => scope.Profiles);
        await using var requestServed = await ServedCatalog.CreateAsync(process);
        var requestSchemas = (await requestServed.Client.ListToolsAsync(cancellationToken: CancellationToken.None))
            .ToDictionary(tool => tool.Name, ServedSchema, StringComparer.Ordinal);
        var targetTools = new[] { "sqlharness_inspect", "sqlharness_validate", "sqlharness_query", "sqlharness_measure", "sqlharness_compare", "sqlharness_watch", "sqlharness_snapshot", "sqlharness_artifact" };
        foreach (var tool in targetTools)
        {
            Assert.Contains("scope", Required(requestSchemas[tool]));
            Assert.True(Properties(requestSchemas[tool]).TryGetProperty("scope", out var scopeSchema), requestSchemas[tool].GetRawText());
            Assert.True(scopeSchema.TryGetProperty("type", out var scopeType), requestSchemas[tool].GetRawText());
            Assert.Equal("object", scopeType.GetString());
            Assert.Equal(new[] { "profile", "vars" }, scopeSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));
            Assert.True(scopeSchema.GetProperty("properties").TryGetProperty("profile", out _));
            Assert.True(scopeSchema.GetProperty("properties").TryGetProperty("vars", out _));
        }
        foreach (var tool in new[] { "sqlharness_capabilities", "sqlharness_plan", "sqlharness_gain" })
            Assert.DoesNotContain("scope", Properties(requestSchemas[tool]).EnumerateObject().Select(property => property.Name));
        var rawNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in requestSchemas.Values) CollectPropertyNames(schema, rawNames);
        Assert.DoesNotContain("server", rawNames);
        Assert.DoesNotContain("database", rawNames);
        Assert.DoesNotContain("auth", rawNames);
        Assert.DoesNotContain("engine", rawNames);
        Assert.DoesNotContain("allowMutation", rawNames);

        await using var fixedServed = await ServedCatalog.CreateAsync(scope);
        var fixedSchemas = (await fixedServed.Client.ListToolsAsync(cancellationToken: CancellationToken.None))
            .ToDictionary(tool => tool.Name, ServedSchema, StringComparer.Ordinal);
        foreach (var tool in targetTools)
            Assert.DoesNotContain("scope", Properties(fixedSchemas[tool]).EnumerateObject().Select(property => property.Name));
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
        Assert.Equal(new[] { ProfileName, "sample-b" }, capability.GetProperty("allowedProfiles").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain("frozen", capabilityDocument.RootElement.GetRawText(), StringComparison.Ordinal);

        var valid = new Dictionary<string, object?>
        {
            ["usage"] = "query",
            ["sql"] = "SELECT 1",
            ["scope"] = new { profile = ProfileName, vars = new Dictionary<string, string> { ["tenant"] = "frozen" } },
        };
        var first = await served.Client.CallToolAsync("sqlharness_validate", valid, cancellationToken: CancellationToken.None);
        Assert.False(first.IsError == true);
        var second = await served.Client.CallToolAsync("sqlharness_validate", new Dictionary<string, object?>
        {
            ["usage"] = "query",
            ["sql"] = "SELECT 1",
            ["scope"] = new { profile = "sample-b", vars = new Dictionary<string, string> { ["tenant"] = "b" } },
        }, cancellationToken: CancellationToken.None);
        Assert.False(second.IsError == true);
        var wrongB = await served.Client.CallToolAsync("sqlharness_validate", new Dictionary<string, object?>
        {
            ["usage"] = "query",
            ["sql"] = "SELECT 1",
            ["scope"] = new { profile = "sample-b", vars = new Dictionary<string, string> { ["tenant"] = "frozen" } },
        }, cancellationToken: CancellationToken.None);
        Assert.Equal(2, ServedEnvelope(wrongB, "sqlharness_validate").GetProperty("exitCode").GetInt32());
        var malformedScope = new Dictionary<string, object?>
        {
            ["usage"] = "query",
            ["sql"] = "SELECT 1",
            ["scope"] = new { profile = ProfileName, vars = new Dictionary<string, string> { ["tenant"] = "frozen" }, extra = "sentinel" },
        };
        var rejected = await served.Client.CallToolAsync("sqlharness_validate", malformedScope, cancellationToken: CancellationToken.None);
        Assert.True(rejected.IsError == true);
        Assert.DoesNotContain("sentinel", string.Concat(rejected.Content.OfType<TextContentBlock>().Select(block => block.Text)), StringComparison.Ordinal);
        var noScope = await served.Client.CallToolAsync("sqlharness_validate", new Dictionary<string, object?> { ["usage"] = "query", ["sql"] = "SELECT 1" }, cancellationToken: CancellationToken.None);
        Assert.True(noScope.IsError == true);

        await using var fixedServed = await ServedCatalog.CreateAsync(scope);
        var fixedWithScope = await fixedServed.Client.CallToolAsync("sqlharness_validate", new Dictionary<string, object?>
        {
            ["usage"] = "query",
            ["sql"] = "SELECT 1",
            ["scope"] = new { profile = ProfileName, vars = new Dictionary<string, string> { ["tenant"] = "frozen" } },
        }, cancellationToken: CancellationToken.None);
        Assert.True(fixedWithScope.IsError == true);

        var planXml = File.ReadAllText(McpStdioProcessHarness.FindRepositoryFile("tests", "SqlHarness.Tests", "Fixtures", "distiller-sample.sqlplan"));
        var plan = await served.Client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["content"] = planXml }, cancellationToken: CancellationToken.None);
        Assert.False(plan.IsError == true);
    }

    [Theory]
    [InlineData("sqlharness_validate", "{\"sql\":\"SELECT 1\"}")]
    [InlineData("sqlharness_measure", "{\"setup\":{\"sql\":\"SELECT 1\"}}")]
    [InlineData("sqlharness_compare", "{\"candidate\":{\"sql\":\"SELECT 1\"}}")]
    [InlineData("sqlharness_inspect", "{}")]
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

        var inspect = schemas["sqlharness_inspect"];
        Assert.Equal(["kind"], Required(inspect));
        Assert.Equal(["ping", "schema", "counts", "space", "qstop", "indexes"], EnumOf(inspect, "kind"));

        var validate = schemas["sqlharness_validate"];
        Assert.Equal(["usage"], Required(validate));
        Assert.Equal(["query", "setup", "benchmark"], EnumOf(validate, "usage"));

        var query = schemas["sqlharness_query"];
        Assert.Empty(Required(query));
        Assert.Equal(30, Properties(query).GetProperty("timeout").GetProperty("default").GetInt32());
        Assert.Equal(50, Properties(query).GetProperty("maxRows").GetProperty("default").GetInt32());

        var measure = schemas["sqlharness_measure"];
        Assert.Equal(["query"], Required(measure));
        Assert.True(Properties(measure).TryGetProperty("paramSetFiles", out _));
        Assert.Equal(5, Properties(measure).GetProperty("repeat").GetProperty("default").GetInt32());

        var compare = schemas["sqlharness_compare"];
        Assert.Equal(["baseline", "candidate"], Required(compare).Order().ToArray());
        Assert.Equal(["ordered", "multiset", "set", "off"], EnumOf(compare, "compareResults"));
        Assert.Equal("ordered", Properties(compare).GetProperty("compareResults").GetProperty("default").GetString());
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
        Assert.Equal(["summary", "metrics", "operators", "statements"], EnumOf(artifact, "section"));

        var gain = schemas["sqlharness_gain"];
        Assert.Empty(Properties(gain).EnumerateObject());

        var capabilities = schemas["sqlharness_capabilities"];
        Assert.Empty(Required(capabilities));
        Assert.True(Properties(capabilities).TryGetProperty("includeDiagnostics", out _));
    }

    // 012/T2: a matrix value is a string or JSON null (typed NULL); the
    // served schema and description must say so and no longer forbid commas.
    [Fact]
    public async Task Matrix_values_schema_advertises_string_or_null_items_without_a_comma_limit()
    {
        var schemas = await ServedSchemasAsync(Scope());
        var matrix = Properties(schemas["sqlharness_compare"]).GetProperty("matrix");
        var itemType = Properties(matrix).GetProperty("values").GetProperty("items").GetProperty("type");
        Assert.Equal(JsonValueKind.Array, itemType.ValueKind);
        Assert.Equal(["null", "string"], itemType.EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray());
        var description = matrix.GetProperty("description").GetString()!;
        Assert.DoesNotContain("no commas", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("null", description, StringComparison.Ordinal);
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
                "{\"baseline\":{\"sql\":\"SELECT @Label\"},\"candidate\":{\"sql\":\"SELECT @Label\"},"
                + "\"parameters\":[{\"name\":\"label\",\"type\":\"int\",\"value\":\"7\"}],"
                + "\"matrix\":{\"name\":\"Label\",\"type\":\"nvarchar(20)\",\"values\":[\"a,b\",\"\",null,\"null\"]}}",
                typeof(Dictionary<string, object?>))!,
            cancellationToken: CancellationToken.None);
        var envelope = ServedEnvelope(result, "sqlharness_compare");
        Assert.True(result.IsError == true);
        Assert.Equal((int)SqlHarnessExitCode.Safety, envelope.GetProperty("exitCode").GetInt32());
        Assert.Equal(
            "The --matrix option for SQL parameter '@Label' duplicates a fixed parameter.",
            envelope.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Inline_frame_cap_is_advertised_on_sql_and_plan_tools()
    {
        var schemas = await ServedSchemasAsync(Scope());
        Assert.Contains("1 MiB", Properties(schemas["sqlharness_validate"]).GetProperty("sql").GetProperty("description").GetString() ?? string.Empty, StringComparison.Ordinal);
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
    [InlineData("sqlharness_validate", "{\"sql\":\"SELECT 1\",\"usage\":\"query\",\"bogus\":1}")]
    [InlineData("sqlharness_inspect", "{\"kind\":\"ping\",\"bogus\":1}")]
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
            ["usage"] = "query",
            ["parameters"] = new object[]
            {
                new Dictionary<string, object?> { ["name"] = "id", ["type"] = "int", ["value"] = SecretValue, ["target"] = "smuggled" },
            },
        };
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var result = await served.Client.CallToolAsync("sqlharness_validate", arguments, cancellationToken: CancellationToken.None);
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
    public void All_tools_publish_the_envelope_output_schema()
    {
        var expected = McpResultAdapter.OutputSchema.RootElement.GetRawText();
        var scope = Scope();
        var tools = McpToolCatalog.CreateTools(scope, scope.CreateModule());
        Assert.Equal(McpToolCatalog.ToolNames.Count, tools.Count);
        foreach (var tool in tools)
        {
            Assert.True(
                tool.ProtocolTool.OutputSchema.HasValue,
                $"{tool.ProtocolTool.Name} publishes no outputSchema.");
            Assert.Equal(
                expected,
                tool.ProtocolTool.OutputSchema!.Value.GetRawText());
        }
    }

    [Fact]
    public async Task Served_tools_publish_the_envelope_output_schema()
    {
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var tools = (await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None)).ToList();
        Assert.Equal(ExpectedTools.Length, tools.Count);
        foreach (var tool in tools)
        {
            var schema = tool.ProtocolTool.OutputSchema;
            Assert.True(schema.HasValue, $"{tool.Name} publishes no outputSchema.");
            Assert.Equal(JsonValueKind.Object, schema!.Value.ValueKind);
            var properties = schema.Value.GetProperty("properties");
            foreach (var name in new[] { "schemaVersion", "command", "status", "exitCode", "result", "error", "truncation" })
                Assert.True(properties.TryGetProperty(name, out _), $"{tool.Name} outputSchema lacks '{name}'.");
        }
    }

    [Fact]
    public async Task Served_capabilities_envelope_conforms_to_the_advertised_output_schema()
    {
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var tools = await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var capabilities = tools.Single(tool => tool.Name == "sqlharness_capabilities");
        Assert.True(
            capabilities.ProtocolTool.OutputSchema.HasValue,
            "sqlharness_capabilities publishes no outputSchema.");
        var result = await served.Client.CallToolAsync(
            "sqlharness_capabilities",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);
        Assert.False(result.IsError == true);
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        using var document = JsonDocument.Parse(text);
        var envelope = document.RootElement;
        Assert.Empty(McpResultAdapter.ValidateEnvelope(envelope));
        Assert.Equal("sqlharness_capabilities", envelope.GetProperty("command").GetString());
    }

    [Fact]
    public async Task Served_output_schemas_cover_the_envelope_contract_for_all_tools()
    {
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var tools = (await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None)).ToList();
        Assert.Equal(ExpectedTools.Order(StringComparer.Ordinal), tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(ExpectedTools.Length, tools.Count);
        foreach (var tool in tools)
        {
            Assert.True(tool.ProtocolTool.OutputSchema.HasValue, $"{tool.Name} publishes no outputSchema.");
            var servedSchema = tool.ProtocolTool.OutputSchema!.Value;
            Assert.Equal(JsonValueKind.Object, servedSchema.ValueKind);
            Assert.Equal("object", servedSchema.GetProperty("type").GetString());
            Assert.Equal(
                ["command", "error", "exitCode", "result", "schemaVersion", "status", "truncation"],
                servedSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray());
            var properties = servedSchema.GetProperty("properties");
            Assert.Equal(1, properties.GetProperty("schemaVersion").GetProperty("const").GetInt32());
            Assert.Equal(
                ["error", "partial", "snapshot_differences", "success", "watch_max_duration"],
                properties.GetProperty("status").GetProperty("enum").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(JsonValueKind.True, properties.GetProperty("result").ValueKind);
            Assert.Equal(
                ["code", "message", "phase"],
                properties.GetProperty("error").GetProperty("required").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(
                ["detailLimit", "maxCellChars", "omittedItems"],
                properties.GetProperty("truncation").GetProperty("required").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    public async Task Served_tools_share_the_free_form_envelope_schema_without_per_tool_narrowing()
    {
        // 008/T4 decision (DONE without narrowing): per-tool narrowing of
        // `result` was evaluated against the 32768 B tools/list budget and
        // rejected. Measured on the wire: catalog 24239 B with 11
        // byte-identical 973 B envelope schemas (8529 B headroom); a
        // lower-bound narrowing sketch for the simplest tool (gain, naming
        // only 3 of its 7 sub-objects) already costs +156 B per tool
        // (+1716 B across all 11), while an honest schema for the
        // polymorphic tools (inspect serves 6 report variants, compare 3
        // shapes, artifact serves disk-shaped JSON, every tool emits null
        // on failure plus degraded placeholders under budget pressure and
        // raw passthrough of projection-unknown reports) converges back
        // to free-form with extra bytes. This test locks the plan minimum:
        // every served tool advertises the shared envelope with free-form
        // `result`, so no per-tool divergence can slip in silently.
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var tools = (await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None)).ToList();
        Assert.Equal(ExpectedTools.Length, tools.Count);
        var expected = JsonSerializer.Serialize(
            McpResultAdapter.OutputSchema.RootElement, McpJsonUtilities.DefaultOptions);
        foreach (var tool in tools)
        {
            Assert.True(tool.ProtocolTool.OutputSchema.HasValue, $"{tool.Name} publishes no outputSchema.");
            Assert.Equal(
                expected,
                JsonSerializer.Serialize(tool.ProtocolTool.OutputSchema!.Value, McpJsonUtilities.DefaultOptions));
        }
    }

    [Fact]
    public async Task Served_validate_envelopes_conform_to_the_advertised_schema()
    {
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var tools = await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var servedSchema = tools.Single(tool => tool.Name == "sqlharness_validate").ProtocolTool.OutputSchema!.Value;
        var success = await served.Client.CallToolAsync(
            "sqlharness_validate",
            new Dictionary<string, object?> { ["usage"] = "query", ["sql"] = "SELECT 1" },
            cancellationToken: CancellationToken.None);
        Assert.False(success.IsError == true);
        var successEnvelope = ServedEnvelope(success, "sqlharness_validate");
        Assert.Empty(McpResultAdapter.ValidateEnvelope(successEnvelope));
        Assert.Equal("success", successEnvelope.GetProperty("status").GetString());
        Assert.Equal(0, successEnvelope.GetProperty("exitCode").GetInt32());
        AssertEnvelopeMatchesServedSchema(servedSchema, successEnvelope, "sqlharness_validate");
        var failure = await served.Client.CallToolAsync(
            "sqlharness_validate",
            new Dictionary<string, object?> { ["usage"] = "query", ["sql"] = "SELECT 1", ["bogus"] = 1 },
            cancellationToken: CancellationToken.None);
        Assert.True(failure.IsError == true);
        var failureEnvelope = ServedEnvelope(failure, "sqlharness_validate");
        Assert.Empty(McpResultAdapter.ValidateEnvelope(failureEnvelope));
        Assert.Equal("error", failureEnvelope.GetProperty("status").GetString());
        Assert.Equal(2, failureEnvelope.GetProperty("exitCode").GetInt32());
        Assert.Equal("safety_rejected", failureEnvelope.GetProperty("error").GetProperty("code").GetString());
        AssertEnvelopeMatchesServedSchema(servedSchema, failureEnvelope, "sqlharness_validate");
    }

    [Fact]
    public async Task Served_partial_watch_and_snapshot_envelopes_conform_to_the_advertised_schema()
    {
        var partialModule = new CannedModule(new SqlHarnessOutcome(
            SqlHarnessExitCode.SqlExecution, new SqlHarnessCompareMatrixReport("batch", "int", []), "Cell 2 failed."));
        await using var partialServed = await ServedCatalog.CreateAsync(Scope(), partialModule);
        var partialTools = await partialServed.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var partialSchema = partialTools.Single(tool => tool.Name == "sqlharness_compare").ProtocolTool.OutputSchema!.Value;
        var partial = await partialServed.Client.CallToolAsync(
            "sqlharness_compare",
            new Dictionary<string, object?>
            {
                ["baseline"] = new Dictionary<string, object?> { ["sql"] = "SELECT 1" },
                ["candidate"] = new Dictionary<string, object?> { ["sql"] = "SELECT 1" },
            },
            cancellationToken: CancellationToken.None);
        Assert.True(partial.IsError == true);
        var partialEnvelope = ServedEnvelope(partial, "sqlharness_compare");
        Assert.Empty(McpResultAdapter.ValidateEnvelope(partialEnvelope));
        Assert.Equal("partial", partialEnvelope.GetProperty("status").GetString());
        Assert.Equal(5, partialEnvelope.GetProperty("exitCode").GetInt32());
        AssertEnvelopeMatchesServedSchema(partialSchema, partialEnvelope, "sqlharness_compare");
        var watchModule = new CannedModule(new SqlHarnessOutcome(
            SqlHarnessExitCode.WatchMaxDuration,
            new SqlHarnessWatchReport(ConformanceTarget, 4, 900000, WatchExitReason.MaxDuration, []),
            null));
        await using var watchServed = await ServedCatalog.CreateAsync(Scope(), watchModule);
        var watchTools = await watchServed.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var watchSchema = watchTools.Single(tool => tool.Name == "sqlharness_watch").ProtocolTool.OutputSchema!.Value;
        var watch = await watchServed.Client.CallToolAsync(
            "sqlharness_watch",
            new Dictionary<string, object?> { ["sql"] = "SELECT 1" },
            cancellationToken: CancellationToken.None);
        Assert.False(watch.IsError == true);
        var watchEnvelope = ServedEnvelope(watch, "sqlharness_watch");
        Assert.Empty(McpResultAdapter.ValidateEnvelope(watchEnvelope));
        Assert.Equal("watch_max_duration", watchEnvelope.GetProperty("status").GetString());
        Assert.Equal(7, watchEnvelope.GetProperty("exitCode").GetInt32());
        AssertEnvelopeMatchesServedSchema(watchSchema, watchEnvelope, "sqlharness_watch");
        var snapshotModule = new CannedModule(new SqlHarnessOutcome(
            SqlHarnessExitCode.SnapshotDifferences,
            new SqlHarnessSnapshotReport(
                ConformanceTarget, "before-import", SnapshotVerdict.Different, 2,
                [new SqlHarnessSnapshotDifference(0, 1, 2, "changed")]),
            null));
        await using var snapshotServed = await ServedCatalog.CreateAsync(Scope(), snapshotModule);
        var snapshotTools = await snapshotServed.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var snapshotSchema = snapshotTools.Single(tool => tool.Name == "sqlharness_snapshot").ProtocolTool.OutputSchema!.Value;
        var snapshot = await snapshotServed.Client.CallToolAsync(
            "sqlharness_snapshot",
            new Dictionary<string, object?> { ["action"] = "diff", ["name"] = "before-import", ["sql"] = "SELECT 1" },
            cancellationToken: CancellationToken.None);
        Assert.False(snapshot.IsError == true);
        var snapshotEnvelope = ServedEnvelope(snapshot, "sqlharness_snapshot");
        Assert.Empty(McpResultAdapter.ValidateEnvelope(snapshotEnvelope));
        Assert.Equal("snapshot_differences", snapshotEnvelope.GetProperty("status").GetString());
        Assert.Equal(8, snapshotEnvelope.GetProperty("exitCode").GetInt32());
        AssertEnvelopeMatchesServedSchema(snapshotSchema, snapshotEnvelope, "sqlharness_snapshot");
    }

    [Fact]
    public async Task Served_validate_and_capabilities_disclose_the_static_analysis_boundary()
    {
        // 009/T1 red witness: MCP clients get the same versioned boundary
        // through the budgeted envelope results (validate report fields and
        // the capabilities safetyAnalysis block).
        await using var served = await ServedCatalog.CreateAsync(Scope());
        var validate = await served.Client.CallToolAsync(
            "sqlharness_validate",
            new Dictionary<string, object?> { ["usage"] = "query", ["sql"] = "SELECT 1" },
            cancellationToken: CancellationToken.None);
        Assert.False(validate.IsError == true);
        var validateResult = ServedEnvelope(validate, "sqlharness_validate").GetProperty("result");
        Assert.Equal("static-visible-effects", validateResult.GetProperty("analysisKind").GetString());
        Assert.Equal(1, validateResult.GetProperty("analysisContractVersion").GetInt32());
        Assert.False(validateResult.GetProperty("hiddenEffectsVerified").GetBoolean());

        var capabilities = await served.Client.CallToolAsync(
            "sqlharness_capabilities",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);
        Assert.False(capabilities.IsError == true);
        var safety = ServedEnvelope(capabilities, "sqlharness_capabilities").GetProperty("result").GetProperty("safetyAnalysis");
        Assert.Equal("static-visible-effects", safety.GetProperty("analysisKind").GetString());
        Assert.Equal(1, safety.GetProperty("analysisContractVersion").GetInt32());
        Assert.False(safety.GetProperty("hiddenEffectsVerified").GetBoolean());
        Assert.Equal("unknown", safety.GetProperty("objectAndPermissionStatus").GetString());
    }

    [Fact]
    public void Query_and_validate_descriptions_state_the_static_visible_effects_boundary()
    {
        // 009/T2: tool descriptions must match the real control (static
        // check of effects visible in the text; DB account role prepared
        // outside SQLHarness) and stay consistent with the T1 safetyAnalysis
        // fields without duplicating their values. No effect guarantees.
        var scope = Scope();
        var tools = McpToolCatalog.CreateTools(scope, scope.CreateModule());
        var byName = tools.ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
        var validate = byName["sqlharness_validate"].ProtocolTool.Description ?? string.Empty;
        var query = byName["sqlharness_query"].ProtocolTool.Description ?? string.Empty;

        Assert.Contains("static", validate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("visible", validate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("safetyAnalysis", validate, StringComparison.Ordinal);
        Assert.Contains("unknown", validate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("static", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("visible", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("role", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outside SQLHarness", query, StringComparison.Ordinal);

        foreach (var text in new[] { validate, query })
        {
            Assert.DoesNotContain("guarantee", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("creates", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("verifies", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no mutation", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("without mutation", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("read-only query", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Served_validate_reports_unknown_object_and_permission_status()
    {
        // 009/T3 closes the T1/T2 deferred minor: MCP validate pins
        // objectAndPermissionStatus like CLI validate already does.
        await using var served = await ServedCatalog.CreateAsync(Scope(), new ThrowingModule());
        var result = await served.Client.CallToolAsync(
            "sqlharness_validate",
            new Dictionary<string, object?> { ["usage"] = "query", ["sql"] = "SELECT 1" },
            cancellationToken: CancellationToken.None);
        Assert.False(result.IsError == true);
        var validateResult = ServedEnvelope(result, "sqlharness_validate").GetProperty("result");
        Assert.Equal(SqlSafetyAnalysis.ObjectAndPermissionStatus, validateResult.GetProperty("objectAndPermissionStatus").GetString());
        Assert.False(validateResult.GetProperty("executed").GetBoolean());
    }

    [Fact]
    public async Task Served_validate_and_capabilities_boundaries_match_the_shared_contract()
    {
        // 009/T3: MCP surfaces disclose the same versioned boundary as
        // CLI — one SqlSafetyAnalysis contract on every surface, without
        // ever dispatching (the throwing module proves no connection).
        await using var served = await ServedCatalog.CreateAsync(Scope(), new ThrowingModule());
        var validate = await served.Client.CallToolAsync(
            "sqlharness_validate",
            new Dictionary<string, object?> { ["usage"] = "query", ["sql"] = "SELECT 1" },
            cancellationToken: CancellationToken.None);
        Assert.False(validate.IsError == true);
        var validateResult = ServedEnvelope(validate, "sqlharness_validate").GetProperty("result");
        Assert.Equal(SqlSafetyAnalysis.AnalysisKind, validateResult.GetProperty("analysisKind").GetString());
        Assert.Equal(SqlSafetyAnalysis.ContractVersion, validateResult.GetProperty("analysisContractVersion").GetInt32());
        Assert.Equal(SqlSafetyAnalysis.HiddenEffectsVerified, validateResult.GetProperty("hiddenEffectsVerified").GetBoolean());
        Assert.Equal(SqlSafetyAnalysis.ObjectAndPermissionStatus, validateResult.GetProperty("objectAndPermissionStatus").GetString());

        var capabilities = await served.Client.CallToolAsync(
            "sqlharness_capabilities",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);
        Assert.False(capabilities.IsError == true);
        var safety = ServedEnvelope(capabilities, "sqlharness_capabilities").GetProperty("result").GetProperty("safetyAnalysis");
        Assert.Equal(SqlSafetyAnalysis.AnalysisKind, safety.GetProperty("analysisKind").GetString());
        Assert.Equal(SqlSafetyAnalysis.ContractVersion, safety.GetProperty("analysisContractVersion").GetInt32());
        Assert.Equal(SqlSafetyAnalysis.HiddenEffectsVerified, safety.GetProperty("hiddenEffectsVerified").GetBoolean());
        Assert.Equal(SqlSafetyAnalysis.ObjectAndPermissionStatus, safety.GetProperty("objectAndPermissionStatus").GetString());
    }

    [Theory]
    [InlineData("EXEC sp_executesql N'SELECT 1';")]
    [InlineData("EXEC xp_cmdshell 'dir';")]
    [InlineData("SELECT * FROM OPENROWSET('SQLOLEDB', 's', 'SELECT 1') AS t(x int);")]
    public async Task Served_validate_rejects_denylisted_functions_without_dispatch(string sql)
    {
        // 009/T3: the MCP preflight rejects the denylist offline — the
        // throwing module would fail the test on any execution attempt.
        await using var served = await ServedCatalog.CreateAsync(Scope(), new ThrowingModule());
        var result = await served.Client.CallToolAsync(
            "sqlharness_validate",
            new Dictionary<string, object?> { ["usage"] = "query", ["sql"] = sql },
            cancellationToken: CancellationToken.None);
        Assert.False(result.IsError == true);
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        using var document = JsonDocument.Parse(text);
        var envelope = document.RootElement;
        Assert.Equal("success", envelope.GetProperty("status").GetString());
        Assert.False(envelope.GetProperty("result").GetProperty("allowed").GetBoolean());
        Assert.False(envelope.GetProperty("result").GetProperty("executed").GetBoolean());
        Assert.DoesNotContain(sql, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Served_preflight_leaves_no_side_effects()
    {
        // 009/T3: served validate/capabilities mutate no state — the
        // synthetic HOME holds exactly the files the fixture wrote.
        await using var served = await ServedCatalog.CreateAsync(Scope(), new ThrowingModule());
        var before = Directory.GetFiles(_home, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        await served.Client.CallToolAsync(
            "sqlharness_validate",
            new Dictionary<string, object?> { ["usage"] = "query", ["sql"] = "SELECT 1" },
            cancellationToken: CancellationToken.None);
        await served.Client.CallToolAsync(
            "sqlharness_capabilities",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);
        Assert.Equal(before, Directory.GetFiles(_home, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Inspect_and_watch_descriptions_make_no_read_only_claim()
    {
        // 009/T3 (closes the T1/T2 deferred minor): inspect/watch must not
        // claim "read-only" — the preflight only checks effects visible in
        // the text, the same reason the query description avoids the phrase.
        var scope = Scope();
        var tools = McpToolCatalog.CreateTools(scope, scope.CreateModule());
        var byName = tools.ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
        foreach (var name in new[] { "sqlharness_inspect", "sqlharness_watch" })
        {
            var description = byName[name].ProtocolTool.Description ?? string.Empty;
            Assert.DoesNotContain("read-only", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("guarantee", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("verifies", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no mutation", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("without mutation", description, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("No SQL input", byName["sqlharness_inspect"].ProtocolTool.Description ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("static", byName["sqlharness_watch"].ProtocolTool.Description ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("visible", byName["sqlharness_watch"].ProtocolTool.Description ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingModule : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            throw new InvalidOperationException("Preflight must never dispatch a database operation.");
        public Task<SqlHarnessOutcome> ExecuteWatchNdjsonAsync(
            SqlHarnessWatchOperation operation, TextWriter writer, CancellationToken ct = default) =>
            throw new InvalidOperationException("Preflight must never dispatch a database operation.");
    }

    [Fact]
    public void Handler_and_dto_members_name_no_target_auth_mutation_or_force_field()
    {
        var banned = new[] { "target", "auth", "mutation", "allowmutation", "confirmdatabase", "unsafedirect", "server", "database", "password", "secret", "credential", "engine", "vars", "force" };
        var dtoTypes = new[] { typeof(McpParameterArgument), typeof(McpSqlSourceArgument), typeof(McpMatrixArgument) };
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