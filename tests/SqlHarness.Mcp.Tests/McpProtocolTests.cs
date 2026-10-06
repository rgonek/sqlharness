using System.IO.Pipelines;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp.Tests;

public sealed class McpProtocolTests
{
    private const string PinnedProtocolVersion = "2025-11-25";

    [Fact]
    public async Task Initialize_handshake_negotiates_pinned_protocol_version()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var serverOptions = new ModelContextProtocol.Server.McpServerOptions
        {
            ServerInfo = new Implementation { Name = "sqlharness-mcp", Version = "1.0.0" },
            ProtocolVersion = PinnedProtocolVersion,
        };
        await using var server = McpServer.Create(
            new StreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream(),
                "sqlharness-mcp",
                NullLoggerFactory.Instance),
            serverOptions,
            NullLoggerFactory.Instance,
            serviceProvider: null);
        Task serverTask = server.RunAsync(cts.Token);

        var clientOptions = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "sqlharness-mcp-tests", Version = "1.0.0" },
            ProtocolVersion = PinnedProtocolVersion,
        };
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream(),
                NullLoggerFactory.Instance),
            clientOptions,
            NullLoggerFactory.Instance,
            cts.Token);

        Assert.Equal(PinnedProtocolVersion, client.NegotiatedProtocolVersion);
        Assert.Equal(PinnedProtocolVersion, server.NegotiatedProtocolVersion);
        Assert.Equal("sqlharness-mcp", client.ServerInfo.Name);
        Assert.NotNull(server.ClientInfo);
        Assert.Equal("sqlharness-mcp-tests", server.ClientInfo.Name);

        await cts.CancelAsync();
        await serverTask;
    }

    [Fact]
    public async Task Duplicate_wire_fields_are_replaced_before_sdk_parsing_and_next_frame_survives()
    {
        const string sentinel = "duplicate-scope-secret-marker";
        var input = Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"id\":41,\"method\":\"tools/call\",\"params\":{\"name\":\"sqlharness_validate\",\"arguments\":{\"scope\":{\"profile\":\"sample-a\",\"profile\":\"" + sentinel + "\",\"vars\":{\"tenant\":\"x\"}},\"usage\":\"query\",\"sql\":\"SELECT 1\"}}}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":43,\"method\":\"tools/call\",\"params\":{\"name\":\"sqlharness_validate\",\"arguments\":{\"scope\":{\"profile\":\"sample-a\",\"vars\":{\"tenant\":\"x\",\"Tenant\":\"" + sentinel + "\"}},\"usage\":\"query\",\"sql\":\"SELECT 1\"}}}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"ping\",\"params\":{}}\n");
        await using var guarded = new McpDuplicateJsonFieldGuardInput(new MemoryStream(input));
        using var output = new MemoryStream();
        await guarded.CopyToAsync(output);
        var frames = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, frames.Length);
        Assert.Contains("__invalid_request__", frames[0], StringComparison.Ordinal);
        Assert.Contains("\"id\":41", frames[0], StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, frames[0], StringComparison.Ordinal);
        Assert.Contains("\"id\":43", frames[1], StringComparison.Ordinal);
        Assert.Contains("__invalid_request__", frames[1], StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, frames[1], StringComparison.Ordinal);
        Assert.Equal("{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"ping\",\"params\":{}}", frames[2]);
    }

    [Fact]
    public async Task Raw_duplicate_scope_call_is_rejected_and_session_serves_next_valid_call()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var profile = new TargetProfile("server.invalid", "sampledb",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = "^[a-z]+$" }, "integrated");
        var process = McpProcessContext.Create(new McpServerOptions
        {
            RequestScope = true,
            AllowedProfiles = ["sample-a"],
        }, () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal) { ["sample-a"] = profile });
        var serverOptions = new ModelContextProtocol.Server.McpServerOptions
        {
            ServerInfo = new Implementation { Name = "sqlharness-mcp", Version = "1.0.0" },
            ProtocolVersion = PinnedProtocolVersion,
        };
        McpToolCatalog.Wire(serverOptions, process);
        await using var guard = new McpDuplicateJsonFieldGuardInput(clientToServer.Reader.AsStream());
        await using var server = McpServer.Create(
            new StreamServerTransport(guard, serverToClient.Writer.AsStream(), "sqlharness-mcp", NullLoggerFactory.Instance),
            serverOptions, NullLoggerFactory.Instance, serviceProvider: null);
        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "sqlharness-mcp-tests", Version = "1.0.0" },
                ProtocolVersion = PinnedProtocolVersion,
            }, NullLoggerFactory.Instance, cts.Token);

        const string sentinel = "outer-scope-duplicate-secret";
        var raw = "{\"jsonrpc\":\"2.0\",\"id\":\"raw-duplicate\",\"method\":\"tools/call\",\"params\":{\"name\":\"sqlharness_validate\",\"arguments\":{\"scope\":{\"profile\":\"sample-a\",\"vars\":{\"tenant\":\"abc\"}},\"scope\":{\"profile\":\"sample-a\",\"vars\":{\"tenant\":\"" + sentinel + "\"}},\"usage\":\"query\",\"sql\":\"SELECT 1\"}}}\n";
        var rawBytes = Encoding.UTF8.GetBytes(raw);
        await clientToServer.Writer.WriteAsync(rawBytes, cts.Token);
        await clientToServer.Writer.FlushAsync(cts.Token);

        var result = await client.CallToolAsync("sqlharness_capabilities", new Dictionary<string, object?>(), cancellationToken: cts.Token);
        Assert.False(result.IsError == true);
        Assert.DoesNotContain(sentinel, string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text)), StringComparison.Ordinal);
        await cts.CancelAsync();
        await serverTask;
    }
}