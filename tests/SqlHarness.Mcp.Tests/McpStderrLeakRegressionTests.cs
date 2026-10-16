using System.Text;

using ModelContextProtocol.Protocol;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// 001/T1 process regression: request content sent through real stdio —
/// inline SQL, a parameter value, a file path, and binder exception content —
/// plus a malformed frame must never reach stderr, while stdout stays pure
/// protocol. Offline calls only (<c>sqlharness_query</c> with <c>dryRun</c>); no database is
/// opened. All markers are fictional and synthetic; no real secrets are used.
/// These tests FAIL on the old McpHost logger (all levels enabled, SDK
/// formatter rendered to stderr) and define the oracle T2/T3 must satisfy.
/// </summary>
[Collection("McpStdioProcess")]
public sealed class McpStderrLeakRegressionTests
{
    private static readonly string PlanXml = File.ReadAllText(McpStdioProcessHarness.FindRepositoryFile(
        "tests", "SqlHarness.Tests", "Fixtures", "distiller-sample.sqlplan"));

    private static readonly TimeSpan LeakBudget = TimeSpan.FromMinutes(6);

    private const string SqlMarker = "steno-sql-4171";
    private const string ParamMarker = "steno-param-8832";
    private const string PathMarker = "steno-path-5590";
    private const string FrameMarker = "steno-frame-6643";
    private const string ExceptionMarker = "steno-exc-2098";
    private const string ScopeVarMarker = "steno-scope-var-7745";
    private const string DuplicateScopeMarker = "steno-duplicate-scope-6458";

    [Fact]
    public async Task Process_stdio_keeps_request_content_off_stderr()
    {
        using var cts = new CancellationTokenSource(LeakBudget);
        var ct = cts.Token;
        string? home = null;
        McpStdioProcessHarness.StdioChild? child = null;
        try
        {
            // The subject here is what the server writes to stdout and stderr, not how
            // it is packaged. Running the built assembly as a child process exercises
            // the same logger path; a self-contained publish would add 60-80 seconds
            // to every one of these tests and prove nothing extra.
            home = McpStdioProcessHarness.CreateSyntheticHome(
                McpStdioProcessHarness.ProfileName,
                """{"mcp-stdio": {"server": "mcp-unreachable.invalid", "database": "mcp-stdio-db", "vars": {"tenant": "^steno-scope-var-7745$"}, "auth": "integrated"}}""");
            child = McpStdioProcessHarness.StartDotnetDllServer(
                McpStdioProcessHarness.BuiltCliDll(), home, "mcp serve --request-scope --allow-profile mcp-stdio");
            var stdin = child.Process.StandardInput.BaseStream;
            await using var client = await McpStdioProcessHarness.ConnectAsync(child, stdin, ct);

            static Dictionary<string, object?> RequestScope() => new()
            {
                ["profile"] = "mcp-stdio",
                ["vars"] = new Dictionary<string, string> { ["tenant"] = ScopeVarMarker },
            };

            // Handshake oracle: pinned protocol revision and server identity.
            Assert.Equal(McpStdioProcessHarness.DefaultProtocolVersion, client.NegotiatedProtocolVersion);
            Assert.Equal(McpHost.ServerName, client.ServerInfo.Name);

            // Valid dry-run query carrying the SQL and parameter markers. Offline
            // checks only; no database is opened.
            var valid = await client.CallToolAsync(
                "sqlharness_query",
                new Dictionary<string, object?>
                {
                    ["target"] = RequestScope(),
                    ["dryRun"] = true,
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
            Assert.False(validEnvelope.TryGetProperty("exitCode", out _));
            Assert.DoesNotContain(ScopeVarMarker, validEnvelope.ToString(), StringComparison.Ordinal);

            // Rejected file path carrying the path marker. This server has no
            // input roots, so the path is refused with a constant message that
            // never echoes it back on stdout.
            var badPath = await client.CallToolAsync(
                "sqlharness_query",
                new Dictionary<string, object?>
                {
                    ["target"] = RequestScope(),
                    ["dryRun"] = true,
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
                "sqlharness_query",
                new Dictionary<string, object?>
                {
                    ["target"] = RequestScope(),
                    ["dryRun"] = true,
                    ["sql"] = "SELECT 1",
                    ["bogusArgument"] = ParamMarker,
                },
                cancellationToken: ct);
            Assert.True(unknownArg.IsError == true);
            Assert.DoesNotContain(
                ParamMarker,
                McpStdioProcessHarness.Envelope(unknownArg).ToString(),
                StringComparison.Ordinal);

            // The disallowed compare mode carries a marker through MCP schema
            // validation. The protocol error must not echo it, and the process
            // itself must survive.
            var badUsage = await client.CallToolAsync(
                "sqlharness_compare",
                new Dictionary<string, object?>
                {
                    ["target"] = RequestScope(),
                    ["subject"] = "stderr-policy",
                    ["scope"] = "whole",
                    ["dryRun"] = true,
                    ["compareResults"] = "bogus-" + ExceptionMarker,
                    ["baseline"] = new Dictionary<string, object?> { ["sql"] = "SELECT 1" },
                    ["candidate"] = new Dictionary<string, object?> { ["sql"] = "SELECT 1" },
                },
                cancellationToken: ct);
            Assert.True(badUsage.IsError == true);
            var badUsageText = string.Concat(badUsage.Content.OfType<TextContentBlock>().Select(block => block.Text));
            Assert.DoesNotContain(ExceptionMarker, badUsageText, StringComparison.Ordinal);
            Assert.False(child.Process.HasExited, "The published server died on rejected arguments.");
            var afterRejection = await client.CallToolAsync(
                "sqlharness_query",
                new Dictionary<string, object?> { ["target"] = RequestScope(), ["dryRun"] = true, ["sql"] = "SELECT 2" },
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
                "sqlharness_query",
                new Dictionary<string, object?> { ["target"] = RequestScope(), ["dryRun"] = true, ["sql"] = "SELECT 3" },
                cancellationToken: ct);
            Assert.False(afterGarbage.IsError == true, McpStdioProcessHarness.Envelope(afterGarbage).ToString());

            // Duplicate raw target fields are stopped before SDK parsing; the
            // secret value in the second occurrence must stay out of frames and stderr.
            var duplicate = Encoding.UTF8.GetBytes(
                "{\"jsonrpc\":\"2.0\",\"id\":\"duplicate-target\",\"method\":\"tools/call\",\"params\":{\"name\":\"sqlharness_query\",\"arguments\":{\"target\":{\"profile\":\"mcp-stdio\",\"vars\":{\"tenant\":\"" + ScopeVarMarker + "\"}},\"target\":{\"profile\":\"mcp-stdio\",\"vars\":{\"tenant\":\"" + DuplicateScopeMarker + "\"}},\"dryRun\":true,\"sql\":\"SELECT 1\"}}}\n");
            await stdin.WriteAsync(duplicate, ct);
            await stdin.FlushAsync(ct);
            var afterDuplicate = await client.CallToolAsync("sqlharness_capabilities", new Dictionary<string, object?>(), cancellationToken: ct);
            Assert.False(afterDuplicate.IsError == true, McpStdioProcessHarness.Envelope(afterDuplicate).ToString());

            await client.DisposeAsync();
            child.Process.StandardInput.Close();
            Assert.True(child.Process.WaitForExit(30_000), "The published server did not exit after stdin EOF.");
            Assert.Equal(0, child.Process.ExitCode);

            // Stdout carries only protocol frames (success, failure, and
            // protocol-error frames are all valid JSON-RPC).
            await child.StdoutTee.DrainRemainingAsync(TimeSpan.FromSeconds(10), ct);
            McpStdioProcessHarness.AssertStdoutIsPureProtocol(child.StdoutTee.Recorded);
            var stdout = Encoding.UTF8.GetString(child.StdoutTee.Recorded);
            Assert.DoesNotContain(SqlMarker, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(ParamMarker, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(ScopeVarMarker, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(DuplicateScopeMarker, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(ExceptionMarker, stdout, StringComparison.Ordinal);

            // Stderr carries none of the request content on any path.
            var stderr = await child.Stderr.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.DoesNotContain(SqlMarker, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(ParamMarker, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(PathMarker, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(FrameMarker, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(ExceptionMarker, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(ScopeVarMarker, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(DuplicateScopeMarker, stderr, StringComparison.Ordinal);
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
            if (home is not null)
                McpStdioProcessHarness.DeleteHome(home);
        }
    }

    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task Process_stderr_has_no_trace_debug_or_information_lines(string revision)
    {
        using var cts = new CancellationTokenSource(LeakBudget);
        var ct = cts.Token;
        string? home = null;
        McpStdioProcessHarness.StdioChild? child = null;
        try
        {
            // Same reasoning as above: the subject is the logger's stream discipline,
            // not the published artifact.
            home = McpStdioProcessHarness.CreateSyntheticHome(
                McpStdioProcessHarness.ProfileName,
                """{"mcp-stdio": {"server": "mcp-unreachable.invalid", "database": "mcp-stdio-db", "vars": {}, "auth": "integrated"}}""");
            child = McpStdioProcessHarness.StartDotnetDllServer(
                McpStdioProcessHarness.BuiltCliDll(), home, "mcp serve mcp-stdio");
            var stdin = child.Process.StandardInput.BaseStream;
            var client = await McpStdioProcessHarness.ConnectAsync(child, stdin, ct, revision);
            Assert.Equal(revision, client.NegotiatedProtocolVersion);
            var plan = await client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["content"] = PlanXml }, cancellationToken: ct);
            Assert.NotEqual(true, plan.IsError);
            await client.DisposeAsync();
            child.Process.StandardInput.Close();
            Assert.True(child.Process.WaitForExit(30_000), "The published server did not exit after stdin EOF.");
            Assert.Equal(0, child.Process.ExitCode);
            await child.StdoutTee.DrainRemainingAsync(TimeSpan.FromSeconds(10), ct);
            McpStdioProcessHarness.AssertStdoutIsPureProtocol(child.StdoutTee.Recorded);

            var stderr = await child.Stderr.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.DoesNotContain(": Trace (event", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(": Debug (event", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(": Information (event", stderr, StringComparison.Ordinal);
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
            if (home is not null)
                McpStdioProcessHarness.DeleteHome(home);
        }
    }
}