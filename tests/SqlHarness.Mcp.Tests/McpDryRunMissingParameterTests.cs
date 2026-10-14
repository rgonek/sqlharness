using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// MCP dryRun shares the Core dry-run path with the CLI --dry-run switch, so a referenced but
/// unsupplied parameter is rejected offline here too (plan 060 task 2 fix).
/// </summary>
[Collection("McpScopeHome")]
public sealed class McpDryRunMissingParameterTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Served_query_dry_run_rejects_a_missing_parameter_and_accepts_a_supplied_one()
    {
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var hostTask = McpHost.RunAsync(
            new McpServerOptions { RequestScope = true, AllowedProfiles = ["sample-a"] },
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), new StringWriter(),
            () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                ["sample-a"] = new("server-a.invalid", "database-a", new Dictionary<string, string> { ["tenant"] = "^a$" }, "integrated"),
            },
            cts.Token);
        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions { ClientInfo = new Implementation { Name = "dry-run", Version = "1" } },
            NullLoggerFactory.Instance, cts.Token))
        {
            var scope = new Dictionary<string, object?> { ["profile"] = "sample-a", ["vars"] = new Dictionary<string, string> { ["tenant"] = "a" } };

            var missing = await client.CallToolAsync("sqlharness_query", new Dictionary<string, object?>
            {
                ["scope"] = scope,
                ["dryRun"] = true,
                ["sql"] = "SELECT @id",
            }, cancellationToken: cts.Token);
            var supplied = await client.CallToolAsync("sqlharness_query", new Dictionary<string, object?>
            {
                ["scope"] = scope,
                ["dryRun"] = true,
                ["sql"] = "SELECT @id",
                ["parameters"] = new[] { new Dictionary<string, object?> { ["name"] = "@id", ["type"] = "int", ["value"] = "1" } },
            }, cancellationToken: cts.Token);

            Assert.True(missing.IsError == true);
            var text = Assert.Single(missing.Content.OfType<TextContentBlock>()).Text;
            Assert.Contains("missing_parameters", text, StringComparison.Ordinal);
            Assert.Contains("id", text, StringComparison.Ordinal);
            Assert.NotEqual(true, supplied.IsError);
            using var document = JsonDocument.Parse(Assert.Single(supplied.Content.OfType<TextContentBlock>()).Text);
            Assert.Equal("query", document.RootElement.GetProperty("result").GetProperty("command").GetString());
        }

        await clientToServer.Writer.CompleteAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask.WaitAsync(Budget, cts.Token));
    }
}