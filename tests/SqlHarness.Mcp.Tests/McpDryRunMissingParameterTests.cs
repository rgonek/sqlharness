using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// MCP dryRun shares the Core dry-run path with the CLI --dry-run switch, so a referenced but
/// unsupplied parameter is rejected offline here too (plan 060 task 2 fix).
/// <para>
/// The host runs in this process and journals every tool call, so SQLHARNESS_HOME points at a
/// synthetic directory: these dry runs must never reach the operator's real
/// <c>~/.sqlharness/data/activity.db</c> and the dashboard that reads it.
/// </para>
/// </summary>
[Collection("McpScopeHome")]
public sealed class McpDryRunMissingParameterTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-dryrun-" + Guid.NewGuid().ToString("N"));

    public McpDryRunMissingParameterTests()
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
    public async Task Served_query_dry_run_rejects_a_missing_parameter_and_accepts_a_supplied_one()
    {
        var (missing, supplied) = await RunAsync();
        Assert.True(missing.IsError);
        Assert.Contains("missing_parameters", missing.Text, StringComparison.Ordinal);
        Assert.Contains("id", missing.Text, StringComparison.Ordinal);
        Assert.False(supplied.IsError);
        using var document = JsonDocument.Parse(supplied.Text);
        Assert.Equal("query", document.RootElement.GetProperty("result").GetProperty("command").GetString());
    }

    /// <summary>
    /// Regression: the served dry runs journal into the synthetic home, never the operator's real
    /// activity database, so running the tests cannot fill the dashboard with test rows.
    /// </summary>
    [Fact]
    public async Task Served_query_dry_run_journals_only_into_the_synthetic_home()
    {
        var real = RealActivityDatabase();
        var before = Stamp(real);
        await RunAsync();

        Assert.Equal(before, Stamp(real));
        var journal = Path.Combine(_home, "data", "activity.db");
        Assert.True(File.Exists(journal), $"expected the synthetic journal at {journal}");
        using var connection = new SqliteConnection($"Data Source={journal}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM operations";
        Assert.Equal(2L, (long)command.ExecuteScalar()!);
    }

    private sealed record ToolCall(bool IsError, string Text);

    private static async Task<(ToolCall Missing, ToolCall Supplied)> RunAsync()
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
        ToolCall missing;
        ToolCall supplied;
        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions { ClientInfo = new Implementation { Name = "dry-run", Version = "1" } },
            NullLoggerFactory.Instance, cts.Token))
        {
            var scope = new Dictionary<string, object?> { ["profile"] = "sample-a", ["vars"] = new Dictionary<string, string> { ["tenant"] = "a" } };

            var missingResult = await client.CallToolAsync("sqlharness_query", new Dictionary<string, object?>
            {
                ["target"] = scope,
                ["dryRun"] = true,
                ["sql"] = "SELECT @id",
            }, cancellationToken: cts.Token);
            var suppliedResult = await client.CallToolAsync("sqlharness_query", new Dictionary<string, object?>
            {
                ["target"] = scope,
                ["dryRun"] = true,
                ["sql"] = "SELECT @id",
                ["parameters"] = new[] { new Dictionary<string, object?> { ["name"] = "@id", ["type"] = "int", ["value"] = "1" } },
            }, cancellationToken: cts.Token);
            missing = new ToolCall(missingResult.IsError == true, Assert.Single(missingResult.Content.OfType<TextContentBlock>()).Text);
            supplied = new ToolCall(suppliedResult.IsError == true, Assert.Single(suppliedResult.Content.OfType<TextContentBlock>()).Text);
        }

        await clientToServer.Writer.CompleteAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask.WaitAsync(Budget, cts.Token));
        return (missing, supplied);
    }

    /// <summary>The operator's activity database, which SQLHARNESS_HOME must keep out of reach here.</summary>
    private static string RealActivityDatabase() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sqlharness", "data", "activity.db");

    /// <summary>Existence plus last-write stamp, so an unchanged database is a byte-level fact.</summary>
    private static (bool Exists, DateTime Written) Stamp(string path) =>
        File.Exists(path) ? (true, File.GetLastWriteTimeUtc(path)) : (false, DateTime.MinValue);
}