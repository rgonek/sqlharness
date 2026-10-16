using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp;
using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp.Tests;

[Collection("McpScopeHome")]
public sealed class McpJournalTests : IDisposable
{
    private static readonly string PlanXml = File.ReadAllText(McpStdioProcessHarness.FindRepositoryFile(
        "tests", "SqlHarness.Tests", "Fixtures", "distiller-sample.sqlplan"));

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-journal-" + Guid.NewGuid().ToString("N"));

    public McpJournalTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        SqliteConnection.ClearAllPools();
        Directory.Delete(_home, true);
    }

    public static TheoryData<string> Modes => new() { "fixed", "request" };

    [Fact]
    public async Task Artifact_tool_journals_own_and_parent_references_for_success_and_failure()
    {
        var profile = new TargetProfile("artifact-host.invalid", "artifact-db", new Dictionary<string, string>(), "integrated");
        var scope = McpScope.Create(new McpServerOptions { Profile = "artifact-profile" },
            new Dictionary<string, TargetProfile> { ["artifact-profile"] = profile });
        var successId = WriteArtifact("journal-artifact-success", scope.Owner, "artifact-producer-success");
        var failureId = WriteArtifact("journal-artifact-failure", scope.Owner with { Profile = "other-profile" }, "artifact-producer-secret");
        var journal = ActivityJournal.Open(new JournalConfig(), TextWriter.Null);
        var module = new JournalingModule(new SqlHarnessModule(), () => journal,
            () => new SessionIdentity("mcp:artifact-test", "codex", "mcp-clientinfo", JournalTransport.Mcp,
                "codex-test", "1", "fixed", Environment.ProcessId, DateTimeOffset.UtcNow,
                Environment.ProcessId, DateTimeOffset.UtcNow, Directory.GetCurrentDirectory()));
        var handlers = new McpToolHandlers(scope, module);

        var success = await handlers.ArtifactAsync(null!, successId, "matrix-cells");
        var successEnvelope = JsonDocument.Parse(McpText(success));
        Assert.False(success.IsError == true);
        Assert.False(successEnvelope.RootElement.TryGetProperty("operationRef", out _));
        Assert.False(successEnvelope.RootElement.TryGetProperty("parentOperationRef", out _));
        Assert.DoesNotContain("artifact-producer-success", McpText(success), StringComparison.Ordinal);

        var failure = await handlers.ArtifactAsync(null!, failureId, "matrix-cells");
        var failureEnvelope = JsonDocument.Parse(McpText(failure));
        Assert.True(failure.IsError == true);
        Assert.False(failureEnvelope.RootElement.TryGetProperty("operationRef", out _));
        Assert.False(failureEnvelope.RootElement.TryGetProperty("parentOperationRef", out _));

        using var connection = new SqliteConnection($"Data Source={Path.Combine(_home, "data", "activity.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation_ref, parent_operation_ref, status FROM operations ORDER BY id";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.False(reader.IsDBNull(0));
        Assert.Equal("artifact-producer-success", reader.GetString(1));
        Assert.Equal("succeeded", reader.GetString(2));
        Assert.True(reader.Read());
        Assert.False(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal("rejected", reader.GetString(2));
        Assert.False(reader.Read());
    }

    private static string McpText(ModelContextProtocol.Protocol.CallToolResult result) =>
        string.Join("\n", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(block => block.Text));

    private static string WriteArtifact(string id, ArtifactOwner owner, string producerOperationRef)
    {
        var directory = Path.Combine(SqlHarnessPaths.CompareDir, id);
        Directory.CreateDirectory(directory);
        var manifest = new ArtifactManifest(ArtifactReader.CurrentManifestVersion, ArtifactReader.CompareKind,
            ArtifactReader.ReportFileName, [ArtifactReader.MatrixCellsSection])
        {
            Owner = owner,
            ProducerOperationRef = producerOperationRef,
        };
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        File.WriteAllText(Path.Combine(directory, ArtifactReader.ReportFileName), "{}");
        File.WriteAllText(Path.Combine(directory, "matrix-cells.json"), "{\"cases\":[]}");
        return id;
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Tool_call_meta_is_recorded_on_the_operation(string mode)
    {
        var options = mode == "fixed"
            ? new McpServerOptions { Profile = "mcp-t5" }
            : new McpServerOptions { RequestScope = true, AllowedProfiles = ["sample-a"] };
        Func<IReadOnlyDictionary<string, TargetProfile>> profiles = () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-t5"] = new TargetProfile("mcp-unreachable.invalid", "reportdb", new Dictionary<string, string>(), "integrated"),
            ["sample-a"] = new("server-a.invalid", "database-a", new Dictionary<string, string> { ["tenant"] = "^a$" }, "integrated"),
        };
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var hostTask = McpHost.RunAsync(options, clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), new StringWriter(), profiles, cts.Token);

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions { ClientInfo = new Implementation { Name = "codex-mcp-client", Version = "0.160.0" }, ProtocolVersion = McpHost.FallbackProtocolVersion },
            NullLoggerFactory.Instance,
            cts.Token))
        {
            var meta = System.Text.Json.Nodes.JsonNode.Parse("""
                {"callId":"exec-9","x-codex-turn-metadata":{"model":"gpt-6-sol","reasoning_effort":"high","turn_id":"turn-9"}}
                """)!.AsObject();
            await client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["content"] = PlanXml },
                options: new ModelContextProtocol.RequestOptions { Meta = meta }, cancellationToken: cts.Token);
        }

        await clientToServer.Writer.CompleteAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask.WaitAsync(Budget, cts.Token));

        using var connection = new SqliteConnection($"Data Source={Path.Combine(_home, "data", "activity.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT client_call_id, agent_model, agent_reasoning_effort, agent_turn_id FROM operations";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(("exec-9", "gpt-6-sol", "high", "turn-9"), (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Tool_call_records_session_from_client_info(string mode)
    {
        var options = mode == "fixed"
            ? new McpServerOptions { Profile = "mcp-t5" }
            : new McpServerOptions { RequestScope = true, AllowedProfiles = ["sample-a"] };
        Func<IReadOnlyDictionary<string, TargetProfile>> profiles = () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-t5"] = new TargetProfile("mcp-unreachable.invalid", "reportdb", new Dictionary<string, string>(), "integrated"),
            ["sample-a"] = new("server-a.invalid", "database-a", new Dictionary<string, string> { ["tenant"] = "^a$" }, "integrated"),
        };
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var log = new StringWriter();
        var hostTask = McpHost.RunAsync(options, clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), log, profiles, cts.Token);

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "claude-code", Version = "9.9.9", Title = "Claude Code" },
                ProtocolVersion = McpHost.FallbackProtocolVersion,
            },
            NullLoggerFactory.Instance,
            cts.Token))
        {
            var result = await client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["content"] = PlanXml }, cancellationToken: cts.Token);
            Assert.NotEqual(true, result.IsError);
        }

        await clientToServer.Writer.CompleteAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask.WaitAsync(Budget, cts.Token));

        var database = Path.Combine(_home, "data", "activity.db");
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.agent_kind, s.transport, s.source, s.client_name, s.client_version, s.mcp_mode, s.session_key, o.operation, o.status, s.client_title
            FROM operations o JOIN sessions s ON s.id = o.session_id
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("claude", reader.GetString(0));
        Assert.Equal("mcp", reader.GetString(1));
        Assert.Equal("mcp-clientinfo", reader.GetString(2));
        Assert.Equal("claude-code", reader.GetString(3));
        Assert.Equal("9.9.9", reader.GetString(4));
        Assert.Equal(mode, reader.GetString(5));
        Assert.StartsWith("mcp:", reader.GetString(6));
        Assert.Equal("plan", reader.GetString(7));
        Assert.Equal("succeeded", reader.GetString(8));
        Assert.Equal("Claude Code", reader.GetString(9));
        Assert.False(reader.Read());
        Assert.DoesNotContain("activity journal", log.ToString());
    }
}