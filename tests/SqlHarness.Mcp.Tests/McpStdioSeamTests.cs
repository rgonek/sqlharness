using System.IO.Pipelines;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using SqlHarness.Core;
using SqlHarness.Mcp;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// The in-process half of the stdout boundary proof. It lives in McpScopeHome rather
/// than beside the child-process smoke because it redirects the process-wide
/// SQLHARNESS_HOME, which is a global every parallel collection would share.
/// </summary>
[Collection("McpScopeHome")]
public sealed class McpStdioSeamTests : IDisposable
{
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-stdio-" + Guid.NewGuid().ToString("N"));

    public McpStdioSeamTests()
    {
        Directory.CreateDirectory(_home);
        // The host journals module calls; keep any journal rows out of the real home.
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_home, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 001/T3 stdout boundary over the host seam: a full handshake plus an
    /// offline validate call emits only JSON-RPC frames on stdout (every
    /// recorded byte parses as a frame), and stdin EOF then exits 0.
    /// No database is opened; the profile is synthetic and unreachable.
    /// </summary>
    [Fact]
    public async Task Inprocess_session_keeps_stdout_pure_protocol_and_exits_zero_on_eof()
    {
        var budget = McpStdioProcessTests.SeamBudget;
        using var cts = new CancellationTokenSource(budget);
        var ct = cts.Token;
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var recording = new RecordingStream(serverToClient.Reader.AsStream());
        var log = new StringWriter();

        Task<int> hostTask = McpHost.RunAsync(
            new McpServerOptions { Profile = "mcp-t3" },
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            log,
            McpStdioProcessTests.SeamProfiles,
            ct);

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                recording,
                NullLoggerFactory.Instance),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "sqlharness-mcp-tests", Version = "1.0.0" },
                ProtocolVersion = McpStdioProcessHarness.DefaultProtocolVersion,
            },
            NullLoggerFactory.Instance,
            ct))
        {
            Assert.Equal(McpStdioProcessHarness.DefaultProtocolVersion, client.NegotiatedProtocolVersion);
            var validate = await client.CallToolAsync(
                "sqlharness_query",
                new Dictionary<string, object?> { ["dryRun"] = true, ["sql"] = "SELECT 1" },
                cancellationToken: ct);
            McpStdioProcessTests.AssertValidateSuccess(validate, "sqlharness_query");
        }

        await clientToServer.Writer.CompleteAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask.WaitAsync(budget, ct));

        // Drain shutdown-phase bytes the client never read before judging
        // purity; every emitted byte must still be a protocol frame.
        await recording.DrainRemainingAsync(TimeSpan.FromSeconds(10), ct);
        McpStdioProcessHarness.AssertStdoutIsPureProtocol(recording.Recorded);
    }
}