using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp;

namespace SqlHarness.Mcp.Tests;

[Collection("McpScopeHome")]
public sealed class McpClientIdentityTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-identity-" + Guid.NewGuid().ToString("N"));

    public McpClientIdentityTests()
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

    [Fact]
    public void First_non_null_client_info_wins()
    {
        var identity = new McpClientIdentity();
        identity.Record(null);
        identity.Record(new Implementation { Name = "claude-code", Version = "2.1.296" });
        identity.Record(new Implementation { Name = "other", Version = "9" });

        Assert.Equal(("claude-code", "2.1.296"), (identity.Name, identity.Version));
    }

    [Fact]
    public void Identity_is_taken_from_the_first_request_that_carries_it()
    {
        var identity = new McpClientIdentity();
        identity.Record(null);
        Assert.Null(identity.Name);
        identity.Record(new Implementation { Name = "late", Version = "1" });
        Assert.Equal("late", identity.Name);
    }

    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task Journal_session_has_client_info_on_every_revision(string revision)
    {
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var hostTask = McpHost.RunAsync(
            new McpServerOptions { Profile = "mcp-t5" },
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), new StringWriter(),
            () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                ["mcp-t5"] = new TargetProfile("mcp-unreachable.invalid", "reportdb", new Dictionary<string, string>(), "integrated"),
            },
            cts.Token);

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions { ClientInfo = new Implementation { Name = "claude-code", Version = "9.9.9" }, ProtocolVersion = revision },
            NullLoggerFactory.Instance, cts.Token))
        {
            Assert.Equal(revision, client.NegotiatedProtocolVersion);
            var result = await client.CallToolAsync("sqlharness_gain", new Dictionary<string, object?>(), cancellationToken: cts.Token);
            Assert.NotEqual(true, result.IsError);
        }

        await clientToServer.Writer.CompleteAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask.WaitAsync(Budget, cts.Token));

        using var connection = new SqliteConnection($"Data Source={Path.Combine(_home, "data", "activity.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent_kind, source, client_name, client_version FROM sessions";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(("claude", "mcp-clientinfo", "claude-code", "9.9.9"),
            (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
    }

    [Fact]
    public async Task Journal_session_enriches_identity_when_a_later_request_supplies_it()
    {
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var filteredInput = new Pipe();
        var serverToClient = new Pipe();
        var filterTask = StripClientInfoUntilFirstToolCallAsync(clientToServer.Reader.AsStream(), filteredInput.Writer.AsStream());
        var hostTask = McpHost.RunAsync(
            new McpServerOptions { Profile = "mcp-t5" },
            filteredInput.Reader.AsStream(), serverToClient.Writer.AsStream(), new StringWriter(),
            () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                ["mcp-t5"] = new TargetProfile("mcp-unreachable.invalid", "reportdb", new Dictionary<string, string>(), "integrated"),
            },
            cts.Token);

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions { ClientInfo = new Implementation { Name = "claude-code", Version = "9.9.9" }, ProtocolVersion = "2026-07-28" },
            NullLoggerFactory.Instance, cts.Token))
        {
            var first = await client.CallToolAsync("sqlharness_gain", new Dictionary<string, object?>(), cancellationToken: cts.Token);
            Assert.NotEqual(true, first.IsError);
            var second = await client.CallToolAsync("sqlharness_gain", new Dictionary<string, object?>(), cancellationToken: cts.Token);
            Assert.NotEqual(true, second.IsError);
        }

        await clientToServer.Writer.CompleteAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask.WaitAsync(Budget, cts.Token));
        await filterTask.WaitAsync(Budget, cts.Token);

        using var connection = new SqliteConnection($"Data Source={Path.Combine(_home, "data", "activity.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT client_name, client_version, COUNT(*) OVER() FROM sessions";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(("claude-code", "9.9.9", 1),
            (reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
    }

    private static async Task StripClientInfoUntilFirstToolCallAsync(Stream input, Stream output)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, leaveOpen: true);
        await using var writer = output;
        var firstCallSeen = false;
        while (await reader.ReadLineAsync() is { } line)
        {
            var message = JsonNode.Parse(line)?.AsObject();
            if (!firstCallSeen && message?["params"] is JsonObject parameters)
            {
                if (parameters["_meta"] is JsonObject meta)
                    meta.Remove("io.modelcontextprotocol/clientInfo");
                firstCallSeen = message["method"]?.GetValue<string>() == "tools/call";
            }

            var bytes = Encoding.UTF8.GetBytes((message?.ToJsonString() ?? line) + "\n");
            await writer.WriteAsync(bytes, CancellationToken.None);
            await writer.FlushAsync(CancellationToken.None);
        }
    }
}
