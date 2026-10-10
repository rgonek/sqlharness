using System.IO.Pipelines;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp;

namespace SqlHarness.Mcp.Tests;

[Collection("McpScopeHome")]
public sealed class McpJournalTests : IDisposable
{
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
                ClientInfo = new Implementation { Name = "claude-code", Version = "9.9.9" },
                ProtocolVersion = McpHost.FallbackProtocolVersion,
            },
            NullLoggerFactory.Instance,
            cts.Token))
        {
            var result = await client.CallToolAsync("sqlharness_gain", new Dictionary<string, object?>(), cancellationToken: cts.Token);
            Assert.NotEqual(true, result.IsError);
        }

        await clientToServer.Writer.CompleteAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask.WaitAsync(Budget, cts.Token));

        var database = Path.Combine(_home, "data", "activity.db");
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.agent_kind, s.transport, s.source, s.client_name, s.client_version, s.mcp_mode, s.session_key, o.operation, o.status
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
        Assert.Equal("gain", reader.GetString(7));
        Assert.Equal("succeeded", reader.GetString(8));
        Assert.False(reader.Read());
        Assert.DoesNotContain("activity journal", log.ToString());
    }
}