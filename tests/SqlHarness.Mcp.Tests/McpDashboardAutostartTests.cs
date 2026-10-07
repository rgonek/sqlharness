using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using SqlHarness.Cli;
using SqlHarness.Cli.Commands;
using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Dashboard;
using SqlHarness.Mcp;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// Dashboard autostart is a side effect of a validated MCP start only, and it never
/// writes to the MCP server's stdout. The spawner is a fake that records launches.
/// </summary>
[Collection("McpScopeHome")]
public sealed class McpDashboardAutostartTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-autostart-" + Guid.NewGuid().ToString("N"));

    public McpDashboardAutostartTests()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "config.json"), """{"dashboard":{"autoStart":true}}""");
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        SqliteConnection.ClearAllPools();
        Directory.Delete(_home, true);
    }

    private sealed class FakeLauncher : IDashboardLauncher
    {
        private readonly List<(DashboardLaunchCommand Command, string WorkingDirectory)> _launches = [];

        public IReadOnlyList<(DashboardLaunchCommand Command, string WorkingDirectory)> Launches
        {
            get
            {
                lock (_launches)
                    return [.. _launches];
            }
        }

        public void Launch(DashboardLaunchCommand command, string workingDirectory)
        {
            lock (_launches)
                _launches.Add((command, workingDirectory));
        }
    }

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
    {
        ["mcp-autostart"] = new TargetProfile("mcp-unreachable.invalid", "reportdb", new Dictionary<string, string>(), "integrated"),
    };

    [Fact]
    public async Task Cli_serve_with_an_unknown_profile_never_autostarts()
    {
        var launcher = new FakeLauncher();
        var output = new StringWriter();
        var error = new StringWriter();
        var app = SqlHarnessCli.Create(new SqlHarnessModule(), output, new StringReader(""), stdinRedirected: false, mcpError: error, dashboardLauncher: launcher);

        var exit = await app.RunAsync(["mcp", "serve", "no-such-profile"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Contains("invalid MCP startup configuration", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(launcher.Launches);
        Assert.Equal(string.Empty, output.ToString());
        Assert.False(File.Exists(Path.Combine(_home, "dashboard.json")));
    }

    [Fact]
    public async Task Invalid_startup_configuration_does_not_run_the_start_hook()
    {
        using var cts = new CancellationTokenSource(Budget);
        var launcher = new FakeLauncher();
        var log = new StringWriter();

        var exit = await McpHost.RunAsync(
            new McpServerOptions { Profile = "no-such-profile" },
            new Pipe().Reader.AsStream(),
            new Pipe().Writer.AsStream(),
            log,
            Profiles,
            cts.Token,
            McpServeCommand.OnStarted(launcher, log));

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(launcher.Launches);
    }

    [Fact]
    public async Task Valid_start_autostarts_once_and_stdout_carries_only_protocol_frames()
    {
        using var cts = new CancellationTokenSource(Budget);
        var launcher = new FakeLauncher();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var log = new StringWriter();

        var hostTask = McpHost.RunAsync(
            new McpServerOptions { Profile = "mcp-autostart" },
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            log,
            Profiles,
            cts.Token,
            McpServeCommand.OnStarted(launcher, log));

        var initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"autostart-test","version":"1"}}}""";
        await clientToServer.Writer.WriteAsync(Encoding.UTF8.GetBytes(initialize + "\n"), cts.Token);
        using var reader = new StreamReader(serverToClient.Reader.AsStream(), Encoding.UTF8);
        var firstLine = await reader.ReadLineAsync(cts.Token);

        await clientToServer.Writer.CompleteAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask.WaitAsync(Budget, cts.Token));
        await serverToClient.Writer.CompleteAsync();
        var rest = await reader.ReadToEndAsync(cts.Token);

        // Stdout holds exactly the initialize response: autostart wrote nothing to it.
        Assert.NotNull(firstLine);
        using (var frame = JsonDocument.Parse(firstLine))
        {
            Assert.Equal(1, frame.RootElement.GetProperty("id").GetInt32());
            Assert.True(frame.RootElement.TryGetProperty("result", out _));
        }
        Assert.Equal(string.Empty, rest);

        var launch = Assert.Single(launcher.Launches);
        Assert.Equal(_home, launch.WorkingDirectory);
        Assert.Equal(["dashboard", "--background"], launch.Command.Arguments.TakeLast(2));
        Assert.DoesNotContain("autostart failed", log.ToString(), StringComparison.Ordinal);
    }
}