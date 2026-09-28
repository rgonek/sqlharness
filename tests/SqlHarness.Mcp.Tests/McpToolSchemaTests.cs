using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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

        public static async Task<ServedCatalog> CreateAsync(McpScope scope)
        {
            var catalog = new ServedCatalog();
            var serverOptions = new ModelContextProtocol.Server.McpServerOptions
            {
                ServerInfo = new Implementation { Name = McpHost.ServerName, Version = "t3-test" },
                ProtocolVersion = McpHost.PinnedProtocolVersion,
            };
            McpToolCatalog.Wire(serverOptions, scope, scope.CreateModule());
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
        var tools = McpToolCatalog.CreateTools(Scope(), Scope().CreateModule());
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
        Assert.Equal(["summary", "metrics", "operators"], EnumOf(artifact, "section"));

        var gain = schemas["sqlharness_gain"];
        Assert.Empty(Properties(gain).EnumerateObject());

        var capabilities = schemas["sqlharness_capabilities"];
        Assert.Empty(Required(capabilities));
        Assert.True(Properties(capabilities).TryGetProperty("includeDiagnostics", out _));
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
