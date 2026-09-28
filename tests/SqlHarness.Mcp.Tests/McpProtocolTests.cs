using System.IO.Pipelines;

using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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
}
