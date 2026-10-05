using System.Text;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// 001/T1 process regression: request content sent through real stdio —
/// inline SQL, a parameter value, a file path, and binder exception content —
/// plus a malformed frame must never reach stderr, while stdout stays pure
/// protocol. Offline tools only (<c>sqlharness_validate</c>); no database is
/// opened. All markers are fictional and synthetic; no real secrets are used.
/// These tests FAIL on the old McpHost logger (all levels enabled, SDK
/// formatter rendered to stderr) and define the oracle T2/T3 must satisfy.
/// </summary>
[Collection("McpStdioProcess")]
public sealed class McpStderrLeakRegressionTests
{
    private static readonly TimeSpan LeakBudget = TimeSpan.FromMinutes(6);

    private const string SqlMarker = "steno-sql-4171";
    private const string ParamMarker = "steno-param-8832";
    private const string PathMarker = "steno-path-5590";
    private const string FrameMarker = "steno-frame-6643";
    private const string ExceptionMarker = "steno-exc-2098";

    [Fact]
    public async Task Process_stdio_keeps_request_content_off_stderr()
    {
        using var cts = new CancellationTokenSource(LeakBudget);
        var ct = cts.Token;
        var exe = await McpStdioProcessHarness.PublishAsync(McpStdioProcessHarness.CurrentRid, ct);
        var home = McpStdioProcessHarness.CreateSyntheticHome(
            McpStdioProcessHarness.ProfileName,
            """{"mcp-stdio": {"server": "mcp-unreachable.invalid", "database": "mcp-stdio-db", "vars": {"tenant": "^stdio$"}, "auth": "integrated"}}""");
        McpStdioProcessHarness.StdioChild? child = null;
        try
        {
            child = McpStdioProcessHarness.StartServer(exe, home, "mcp serve mcp-stdio --var tenant=stdio");
            var stdin = child.Process.StandardInput.BaseStream;
            await using var client = await McpStdioProcessHarness.ConnectAsync(child, stdin, ct);

            // Handshake oracle: pinned protocol revision and server identity.
            Assert.Equal(McpStdioProcessHarness.PinnedProtocolVersion, client.NegotiatedProtocolVersion);
            Assert.Equal(McpHost.ServerName, client.ServerInfo.Name);

            // Valid validate carrying the SQL and parameter markers. Offline
            // classifier only; no database is opened.
            var valid = await client.CallToolAsync(
                "sqlharness_validate",
                new Dictionary<string, object?>
                {
                    ["usage"] = "query",
                    ["sql"] = $"SELECT @stenoparam, '{SqlMarker}'",
                    ["parameters"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["name"] = "stenoparam",
                            ["type"] = "nvarchar",
                            ["value"] = ParamMarker,
                        },
                    },
                },
                cancellationToken: ct);
            Assert.False(valid.IsError == true, McpStdioProcessHarness.Envelope(valid).ToString());
            var validEnvelope = McpStdioProcessHarness.Envelope(valid);
            Assert.Equal("success", validEnvelope.GetProperty("status").GetString());
            Assert.Equal(0, validEnvelope.GetProperty("exitCode").GetInt32());

            // Rejected file path carrying the path marker. This server has no
            // input roots, so the path is refused with a constant message that
            // never echoes it back on stdout.
            var badPath = await client.CallToolAsync(
                "sqlharness_validate",
                new Dictionary<string, object?>
                {
                    ["usage"] = "query",
                    ["file"] = $"{PathMarker}/missing.sql",
                },
                cancellationToken: ct);
            Assert.True(badPath.IsError == true);
            Assert.Equal("error", McpStdioProcessHarness.Envelope(badPath).GetProperty("status").GetString());
            Assert.DoesNotContain(
                PathMarker,
                McpStdioProcessHarness.Envelope(badPath).ToString(),
                StringComparison.Ordinal);

            // Rejected unknown argument carrying marker content. The handler
            // contract rejects it with constant text (supported names only);
            // the value must survive nowhere but the request itself.
            var unknownArg = await client.CallToolAsync(
                "sqlharness_validate",
                new Dictionary<string, object?>
                {
                    ["usage"] = "query",
                    ["sql"] = "SELECT 1",
                    ["bogusArgument"] = ParamMarker,
                },
                cancellationToken: ct);
            Assert.True(unknownArg.IsError == true);
            Assert.DoesNotContain(
                ParamMarker,
                McpStdioProcessHarness.Envelope(unknownArg).ToString(),
                StringComparison.Ordinal);

            // Invalid usage value carrying the exception-content marker. The
            // value travels through binder/handler exception content, but the
            // stdout rejection stays constant and value-free; the process
            // itself must survive.
            var badUsage = await client.CallToolAsync(
                "sqlharness_validate",
                new Dictionary<string, object?>
                {
                    ["usage"] = "bogus-" + ExceptionMarker,
                    ["sql"] = "SELECT 1",
                },
                cancellationToken: ct);
            Assert.True(badUsage.IsError == true);
            Assert.Equal("error", McpStdioProcessHarness.Envelope(badUsage).GetProperty("status").GetString());
            Assert.DoesNotContain(
                ExceptionMarker,
                McpStdioProcessHarness.Envelope(badUsage).ToString(),
                StringComparison.Ordinal);
            Assert.False(child.Process.HasExited, "The published server died on rejected arguments.");
            var afterRejection = await client.CallToolAsync(
                "sqlharness_validate",
                new Dictionary<string, object?> { ["usage"] = "query", ["sql"] = "SELECT 2" },
                cancellationToken: ct);
            Assert.False(afterRejection.IsError == true, McpStdioProcessHarness.Envelope(afterRejection).ToString());

            // Malformed frame carrying a marker: raw non-JSON bytes straight
            // to stdin. The process must survive and serve the next valid call.
            var garbage = Encoding.UTF8.GetBytes($"THIS IS NOT JSON-RPC {FrameMarker} {{{{\r\n");
            await stdin.WriteAsync(garbage, ct);
            await stdin.FlushAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            Assert.False(child.Process.HasExited, "The published server died on malformed input.");
            var afterGarbage = await client.CallToolAsync(
                "sqlharness_validate",
                new Dictionary<string, object?> { ["usage"] = "query", ["sql"] = "SELECT 3" },
                cancellationToken: ct);
            Assert.False(afterGarbage.IsError == true, McpStdioProcessHarness.Envelope(afterGarbage).ToString());

            await client.DisposeAsync();
            child.Process.StandardInput.Close();
            Assert.True(child.Process.WaitForExit(30_000), "The published server did not exit after stdin EOF.");
            Assert.Equal(0, child.Process.ExitCode);

            // Stdout carries only protocol frames (success, failure, and
            // protocol-error frames are all valid JSON-RPC).
            await child.StdoutTee.DrainRemainingAsync(TimeSpan.FromSeconds(10), ct);
            McpStdioProcessHarness.AssertStdoutIsPureProtocol(child.StdoutTee.Recorded);

            // Stderr carries none of the request content on any path.
            var stderr = await child.Stderr.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.DoesNotContain(SqlMarker, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(ParamMarker, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(PathMarker, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(FrameMarker, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(ExceptionMarker, stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (child is not null && !child.Process.HasExited)
            {
                try
                {
                    child.Process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                child.Process.WaitForExit(10_000);
            }

            child?.Process.Dispose();
            McpStdioProcessHarness.DeleteHome(home);
        }
    }
}