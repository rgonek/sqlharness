using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SqlHarness.Mcp.Tests;

public sealed class McpProtocolNegotiationTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static async Task<(string? Client, string? Server)> NegotiateAsync(string requested)
    {
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var rewriteInput = new McpProtocolVersionRewriteInput(clientToServer.Reader.AsStream());
        await using var server = McpServer.Create(
            new StreamServerTransport(rewriteInput, serverToClient.Writer.AsStream(), "negotiation", NullLoggerFactory.Instance),
            McpHost.CreateServerOptions(), NullLoggerFactory.Instance, serviceProvider: null);
        var serverTask = server.RunAsync(cts.Token);
        await using var responseStream = serverToClient.Reader.AsStream();
        using var writer = new StreamWriter(clientToServer.Writer.AsStream(), new UTF8Encoding(false), leaveOpen: true);
        using var reader = new StreamReader(responseStream, Encoding.UTF8, leaveOpen: true);
        try
        {
            await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" + requested + "\",\"capabilities\":{},\"clientInfo\":{\"name\":\"negotiation-tests\",\"version\":\"1.0.0\"}}}");
            await writer.FlushAsync(cts.Token);
            var response = await reader.ReadLineAsync(cts.Token);
            using var document = JsonDocument.Parse(response!);
            var root = document.RootElement;
            var clientVersion = root.TryGetProperty("result", out var result) && result.TryGetProperty("protocolVersion", out var version)
                ? version.GetString()
                : throw new InvalidOperationException(response);
            return (clientVersion, server.NegotiatedProtocolVersion);
        }
        finally
        {
            await cts.CancelAsync();
            try { await serverTask; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    public async Task Offered_revisions_are_negotiated_as_requested(string requested)
    {
        var (client, _) = await NegotiateAsync(requested);

        Assert.Equal(requested, client);
    }

    [Theory]
    [InlineData("2024-11-05")]
    [InlineData("2025-03-26")]
    public async Task Older_handshake_revisions_are_answered_with_the_fallback(string requested)
    {
        var (client, server) = await NegotiateAsync(requested);

        Assert.Equal(McpHost.FallbackProtocolVersion, client);
        Assert.Equal(McpHost.FallbackProtocolVersion, server);
    }

    [Fact]
    public void Constants_describe_the_offered_revisions()
    {
        Assert.Equal(["2025-06-18", "2025-11-25", "2026-07-28"], McpHost.SupportedProtocolVersions);
        Assert.Equal(["2025-06-18", "2025-11-25"], McpHost.HandshakeProtocolVersions);
        Assert.Equal("2025-11-25", McpHost.FallbackProtocolVersion);
        Assert.Null(McpHost.CreateServerOptions().ProtocolVersion);
    }

    [Fact]
    public async Task Initialize_without_string_version_is_left_alone()
    {
        const string frame = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":20251125,\"capabilities\":{},\"clientInfo\":{\"name\":\"x\",\"version\":\"1\"}}}\n";

        Assert.Equal(frame, await RewriteAsync(frame));
    }

    [Fact]
    public async Task Initialize_without_protocol_version_is_left_alone()
    {
        const string frame = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"capabilities\":{},\"clientInfo\":{\"name\":\"x\",\"version\":\"1\"}}}\n";

        Assert.Equal(frame, await RewriteAsync(frame));
    }

    [Fact]
    public async Task Oversized_frame_is_rejected_before_any_bytes_are_forwarded()
    {
        var input = new string(' ', 16 * 1024 * 1024 + 1);

        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        await using var rewritten = new McpProtocolVersionRewriteInput(source);
        await using var destination = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() => rewritten.CopyToAsync(destination));
        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public async Task Rewrite_changes_only_unoffered_initialize_versions()
    {
        static string Initialize(string version) => "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" + version + "\",\"capabilities\":{},\"clientInfo\":{\"name\":\"x\",\"version\":\"1\"}}}\n";
        const string ping = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}\n";

        Assert.Contains("\"protocolVersion\":\"2025-11-25\"", await RewriteAsync(Initialize("2024-11-05")));
        Assert.Equal(Initialize("2025-06-18"), await RewriteAsync(Initialize("2025-06-18")));
        Assert.Equal(Initialize("2099-01-01"), await RewriteAsync(Initialize("2099-01-01")));
        Assert.Equal(ping, await RewriteAsync(ping));
    }

    private static async Task<string> RewriteAsync(string input)
    {
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        await using var rewritten = new McpProtocolVersionRewriteInput(source);
        await using var destination = new MemoryStream();
        await rewritten.CopyToAsync(destination);
        return Encoding.UTF8.GetString(destination.ToArray());
    }
}
