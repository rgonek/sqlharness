using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;

using Xunit;

namespace SqlHarness.Mcp.Tests;

[Collection("McpScopeHome")]
public sealed class ServedAnnotationReceiptTests : IAsyncLifetime
{
    private const string ProfileName = "annotation-receipt-test";
    private const string CallerKey = "mcp:annotation-test-caller";
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sqlharness-annotate-receipts-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
        EnsureSchema();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Served_receipt_fits_minimum_budget_and_persists_twenty_multibyte_labels(bool requestScope)
    {
        await using var fixture = await Fixture.CreateAsync(requestScope, 4096);
        Seed(fixture.Process.MutationSessionKey, 20, "snapshot");
        var items = Enumerable.Range(1, 20).Select(index => Item(Ref(index), new string('界', 120), true)).ToArray();

        var response = await fixture.AnnotateAsync(items, new Dictionary<string, object?> { ["maxResultBytes"] = 65536 });

        Assert.False(response.IsError == true, Text(response));
        var payload = Result(response);
        var outcomes = payload.GetProperty("result").EnumerateArray().ToArray();
        Assert.Equal(20, outcomes.Length);
        Assert.Equal(items.Select(item => (string)item["operationRef"]!), outcomes.Select(outcome => outcome.GetProperty("operationRef").GetString()));
        Assert.All(outcomes, outcome =>
        {
            Assert.Equal(4, outcome.EnumerateObject().Count());
            Assert.Equal("success", outcome.GetProperty("status").GetString());
            Assert.Equal("snapshot", outcome.GetProperty("tool").GetString());
            Assert.True(outcome.GetProperty("keep").GetBoolean());
            Assert.False(outcome.TryGetProperty("label", out _));
        });
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(Text(response)) <= 4096);
        Assert.Equal(Enumerable.Repeat(new string('界', 120), 20), ReadLabels());
    }

    [Fact]
    public async Task Served_receipt_costs_one_call_against_full_echo_rejection_and_split_workflow()
    {
        var label = new string('界', 120);
        var items = Enumerable.Range(1, 20).Select(index => Item(Ref(index), label, true)).ToArray();
        var receiptRequestBytes = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { items }));
        var fullRequestBytes = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { items, responseDetail = "full" }));

        var legacyCalls = 0;
        var legacyInputBytes = fullRequestBytes;
        var legacyOutputBytes = 0;
        string error;
        await using (var legacy = await Fixture.CreateAsync(requestScope: false, maxResultBytes: 4096))
        {
            Seed(legacy.Process.MutationSessionKey, 20, "snapshot");
            var rejected = await legacy.AnnotateAsync(items, new Dictionary<string, object?> { ["responseDetail"] = "full" });
            legacyCalls++;
            legacyOutputBytes += McpResultAdapter.MeasureBytes(rejected);
            Assert.True(rejected.IsError == true, "The full-echo request should exceed the 4096-byte operator ceiling.");
            error = Text(rejected);
            Assert.Contains("4096", error, StringComparison.Ordinal);
            Assert.Contains("receipt", error, StringComparison.OrdinalIgnoreCase);

            foreach (var chunk in items.Chunk(3))
            {
                legacyInputBytes += System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { items = chunk, responseDetail = "full" }));
                var split = await legacy.AnnotateAsync(chunk, new Dictionary<string, object?> { ["responseDetail"] = "full" });
                legacyCalls++;
                Assert.False(split.IsError == true, Text(split));
                legacyOutputBytes += McpResultAdapter.MeasureBytes(split);
            }
        }
        Assert.Equal(20, ReadLabelCount());

        ResetSchema();
        await using var receipt = await Fixture.CreateAsync(requestScope: false, maxResultBytes: 4096);
        Seed(receipt.Process.MutationSessionKey, 20, "snapshot");
        var receiptResponse = await receipt.AnnotateAsync(items, new Dictionary<string, object?>());
        Assert.False(receiptResponse.IsError == true, Text(receiptResponse));
        var receiptInputBytes = receiptRequestBytes;
        var receiptOutputBytes = McpResultAdapter.MeasureBytes(receiptResponse);
        Assert.Equal(20, ReadLabelCount());

        Console.WriteLine($"annotation workflow fixture (UTF-8 bytes, not model tokens): old full echo + rejection + split calls={legacyCalls}, input={legacyInputBytes}, output={legacyOutputBytes}, error={error}; receipt calls=1, input={receiptInputBytes}, output={receiptOutputBytes}");
        Assert.True(legacyCalls > 1);
        Assert.Single(new[] { receiptResponse });
        Assert.True(legacyInputBytes > receiptInputBytes);
        Assert.True(legacyOutputBytes > receiptOutputBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Served_full_detail_returns_resulting_labels_at_adequate_budget(bool requestScope)
    {
        await using var fixture = await Fixture.CreateAsync(requestScope, 65536);
        Seed(fixture.Process.MutationSessionKey, 20, "snapshot");
        var label = new string('界', 120);
        var items = Enumerable.Range(1, 20).Select(index => Item(Ref(index), label, true)).ToArray();

        var response = await fixture.AnnotateAsync(items, new Dictionary<string, object?> { ["responseDetail"] = "full" });

        Assert.False(response.IsError == true, Text(response));
        var outcomes = Result(response).GetProperty("result").EnumerateArray().ToArray();
        Assert.Equal(20, outcomes.Length);
        Assert.All(outcomes, outcome =>
        {
            Assert.Equal(label, outcome.GetProperty("label").GetString());
            Assert.Equal("snapshot", outcome.GetProperty("tool").GetString());
        });
        Assert.Equal(20, ReadLabelCount());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Served_full_detail_at_minimum_budget_rejects_and_preserves_prior_values(bool requestScope)
    {
        await using var fixture = await Fixture.CreateAsync(requestScope, 4096);
        Seed(fixture.Process.MutationSessionKey, 20, "query", priorValues: true);
        var items = Enumerable.Range(1, 20).Select(index => Item(Ref(index), new string('界', 120), false)).ToArray();

        var response = await fixture.AnnotateAsync(items, new Dictionary<string, object?> { ["responseDetail"] = "full" });

        Assert.True(response.IsError == true, "Oversized full detail must fail before commit.");
        Assert.Equal(20, ReadPriorLabelCount());
        Assert.Equal(20, ReadPriorKeepCount());
        Assert.Contains("4096", Text(response), StringComparison.Ordinal);
        Assert.Contains("receipt", Text(response), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Served_receipt_preserves_repeats_and_uniform_unavailable_results(bool requestScope)
    {
        await using var fixture = await Fixture.CreateAsync(requestScope, 4096);
        Seed(fixture.Process.MutationSessionKey, 1, "snapshot");
        SeedForeign(2);
        SeedCallerOperation(3, "query");
        DeleteOperation(3); // retention-expired evidence is indistinguishable from an unknown reference
        var response = await fixture.AnnotateAsync(
        [
            Item(Ref(1), "first", true),
            Item(Ref(1), "second", false),
            Item(Ref(2), "foreign", true),
            Item(Ref(3), "expired", true),
            Item(Ref(99), "unknown", true),
        ], new Dictionary<string, object?>());

        Assert.False(response.IsError == true, Text(response));
        var outcomes = Result(response).GetProperty("result").EnumerateArray().ToArray();
        Assert.Equal(5, outcomes.Length);
        Assert.Equal(new[] { Ref(1), Ref(1), Ref(2), Ref(3), Ref(99) }, outcomes.Select(outcome => outcome.GetProperty("operationRef").GetString()));
        AssertReceiptSuccess(outcomes[0], Ref(1), "snapshot", true);
        AssertReceiptSuccess(outcomes[1], Ref(1), "snapshot", false);
        Assert.Equal(4, outcomes[0].EnumerateObject().Count());
        Assert.Equal(4, outcomes[1].EnumerateObject().Count());
        Assert.False(outcomes[0].TryGetProperty("label", out _));
        Assert.False(outcomes[1].TryGetProperty("label", out _));
        var unavailable = outcomes.Skip(2).ToArray();
        Assert.All(unavailable, outcome =>
        {
            Assert.Equal("operation_not_available", outcome.GetProperty("status").GetString());
            Assert.Equal(2, outcome.EnumerateObject().Count());
        });
        Assert.All(unavailable, outcome =>
        {
            Assert.Equal(new[] { "operationRef", "status" }, outcome.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal("operation_not_available", outcome.GetProperty("status").GetString());
        });
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(Text(response)) <= 4096);
        Assert.Equal("second", ReadLabel(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Served_unsupported_options_fail_before_any_annotation_write(bool requestScope)
    {
        await using var fixture = await Fixture.CreateAsync(requestScope, 65536);
        Seed(fixture.Process.MutationSessionKey, 1, "query", priorValues: true);
        var cases = new[]
        {
            new Dictionary<string, object?> { ["responseDetail"] = "compact-but-unsupported" },
            new Dictionary<string, object?> { ["maxResultBytes"] = 1024 },
        };

        foreach (var options in cases)
        {
            var response = await fixture.AnnotateAsync([Item(Ref(1), "changed", false)], options);
            Assert.True(response.IsError == true, "Unsupported detail and out-of-range budget must be rejected.");
            Assert.Equal(1, ReadPriorLabelCount());
            Assert.Equal(1, ReadPriorKeepCount());
        }
    }

    private static Dictionary<string, object?> Item(string operationRef, string? label, bool? keep) => new()
    {
        ["operationRef"] = operationRef,
        ["label"] = label,
        ["keep"] = keep,
    };

    private static void AssertReceiptSuccess(JsonElement outcome, string operationRef, string tool, bool keep)
    {
        Assert.Equal(operationRef, outcome.GetProperty("operationRef").GetString());
        Assert.Equal("success", outcome.GetProperty("status").GetString());
        Assert.Equal(tool, outcome.GetProperty("tool").GetString());
        Assert.Equal(keep, outcome.GetProperty("keep").GetBoolean());
    }

    private static string Ref(int id) => id.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);

    private static JsonElement Result(CallToolResult response)
    {
        using var document = JsonDocument.Parse(Text(response));
        return document.RootElement.Clone();
    }

    private static string Text(CallToolResult response) => string.Concat(response.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private void Seed(string sessionKey, int count, string tool, bool priorValues = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(id, session_key, agent_kind, transport) VALUES(1, $key, 'codex', 'mcp');
            """;
        command.Parameters.AddWithValue("$key", sessionKey);
        command.ExecuteNonQuery();
        for (var id = 1; id <= count; id++)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO operations(id, session_id, operation, operation_ref, dry_run) VALUES($id, 1, $tool, $ref, 0);";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$tool", tool);
            command.Parameters.AddWithValue("$ref", Ref(id));
            command.ExecuteNonQuery();
            if (priorValues)
            {
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO operation_annotations(operation_id, agent_label_initial, agent_label, agent_keep, updated_at) VALUES($id, 'initial', 'prior', 1, '2026-10-20T00:00:00Z');";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
        }
    }

    private void SeedForeign(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(id, session_key, agent_kind, transport) VALUES(2, 'mcp:foreign', 'codex', 'mcp');
            INSERT INTO operations(id, session_id, operation, operation_ref, dry_run) VALUES($id, 2, 'snapshot', $ref, 0);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$ref", Ref(id));
        command.ExecuteNonQuery();
    }

    private void DeleteOperation(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM operations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private void SeedCallerOperation(int id, string tool)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO operations(id, session_id, operation, operation_ref, dry_run) VALUES($id, 1, $tool, $ref, 0);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$tool", tool);
        command.Parameters.AddWithValue("$ref", Ref(id));
        command.ExecuteNonQuery();
    }

    private long ReadLabelCount() => Scalar("SELECT COUNT(*) FROM operation_annotations WHERE agent_label IS NOT NULL AND agent_label != 'prior';");
    private long ReadPriorLabelCount() => Scalar("SELECT COUNT(*) FROM operation_annotations WHERE agent_label = 'prior';");
    private long ReadPriorKeepCount() => Scalar("SELECT COUNT(*) FROM operation_annotations WHERE agent_keep = 1 AND agent_label = 'prior';");
    private string? ReadLabel(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent_label FROM operation_annotations WHERE operation_id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }
    private IReadOnlyList<string?> ReadLabels()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent_label FROM operation_annotations ORDER BY operation_id;";
        using var reader = command.ExecuteReader();
        var labels = new List<string?>();
        while (reader.Read()) labels.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return labels;
    }
    private long Scalar(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private SqliteConnection Open()
    {
        var path = SqlHarnessPaths.ActivityDatabase;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE sessions(id INTEGER PRIMARY KEY, session_key TEXT NOT NULL, agent_kind TEXT NOT NULL, transport TEXT NOT NULL);
            CREATE TABLE operations(id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL, operation TEXT NOT NULL, operation_ref TEXT, agent_session_id TEXT, dry_run INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE agent_conversations(id INTEGER PRIMARY KEY, agent_kind TEXT NOT NULL, conversation_id TEXT NOT NULL);
            CREATE TABLE operation_agent_links(operation_id INTEGER NOT NULL, conversation INTEGER NOT NULL);
            CREATE TABLE operation_annotations(operation_id INTEGER PRIMARY KEY, agent_label_initial TEXT, agent_label TEXT, agent_keep INTEGER, owner_starred INTEGER NOT NULL DEFAULT 0, owner_note TEXT, updated_at TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    private void ResetSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE operation_annotations; DROP TABLE operation_agent_links; DROP TABLE agent_conversations; DROP TABLE operations; DROP TABLE sessions;";
        command.ExecuteNonQuery();
        EnsureSchema();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();
        private Task _serverTask = Task.CompletedTask;

        private Fixture(McpProcessContext process) => Process = process;
        public McpProcessContext Process { get; }
        public McpClient Client { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(bool requestScope, int maxResultBytes)
        {
            var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                [ProfileName] = new("server.invalid", "db", new Dictionary<string, string>(), "integrated"),
            };
            var options = requestScope
                ? new McpServerOptions { RequestScope = true, AllowedProfiles = [ProfileName], MaxResultBytes = maxResultBytes }
                : new McpServerOptions { Profile = ProfileName, MaxResultBytes = maxResultBytes };
            var config = new SqlHarnessConfig { Journal = new JournalConfig { Enabled = true, OperationAnnotations = true } };
            var process = McpProcessContext.Create(options, () => profiles, config: config);
            var fixture = new Fixture(process);
            var serverOptions = new ModelContextProtocol.Server.McpServerOptions
            {
                ServerInfo = new Implementation { Name = McpHost.ServerName, Version = "receipt-test" },
                ProtocolVersion = McpHost.FallbackProtocolVersion,
            };
            McpToolCatalog.Wire(serverOptions, process);
            var server = McpServer.Create(new StreamServerTransport(fixture._clientToServer.Reader.AsStream(), fixture._serverToClient.Writer.AsStream(), "annotation-test", NullLoggerFactory.Instance),
                serverOptions, NullLoggerFactory.Instance, serviceProvider: null);
            fixture._serverTask = server.RunAsync(fixture._cts.Token);
            fixture.Client = await McpClient.CreateAsync(new StreamClientTransport(fixture._clientToServer.Writer.AsStream(), fixture._serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
                new McpClientOptions { ClientInfo = new Implementation { Name = "receipt-test-client", Version = "1" }, ProtocolVersion = McpHost.FallbackProtocolVersion },
                NullLoggerFactory.Instance, fixture._cts.Token);
            return fixture;
        }

        public async Task<CallToolResult> AnnotateAsync(IReadOnlyList<Dictionary<string, object?>> items, Dictionary<string, object?> extra)
        {
            var arguments = new Dictionary<string, object?> { ["items"] = items };
            foreach (var pair in extra) arguments[pair.Key] = pair.Value;
            if (Process.RequestScope)
                arguments["target"] = new { profile = ProfileName, vars = new Dictionary<string, string>() };
            try
            {
                return await Client.CallToolAsync("sqlharness_annotate", arguments, cancellationToken: CancellationToken.None);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Served annotation invocation failed: " + error, error);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { await _serverTask; } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }
}