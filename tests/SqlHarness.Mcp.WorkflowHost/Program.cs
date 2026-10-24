using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Server;

using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp;
using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp.WorkflowHost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? scenarioId = null;
        string? home = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--scenario" && index + 1 < args.Length)
                scenarioId = args[++index];
            else if (args[index] == "--home" && index + 1 < args.Length)
                home = args[++index];
            else
            {
                Console.Error.WriteLine("sqlharness-workflow-host: expected --scenario <id> --home <isolated-directory>.");
                return (int)SqlHarnessExitCode.Safety;
            }
        }

        if (string.IsNullOrWhiteSpace(scenarioId) || string.IsNullOrWhiteSpace(home))
        {
            Console.Error.WriteLine("sqlharness-workflow-host: expected --scenario <id> --home <isolated-directory>.");
            return (int)SqlHarnessExitCode.Safety;
        }

        WorkflowScenario scenario;
        try
        {
            scenario = WorkflowScenarios.Require(scenarioId);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine("sqlharness-workflow-host: " + exception.Message);
            return (int)SqlHarnessExitCode.Safety;
        }

        home = Path.GetFullPath(home);
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", home);
        Environment.SetEnvironmentVariable("SQLHARNESS_077_FIXTURE_PW", "unused-fixture-password");
        var inputs = Path.Combine(home, "inputs");
        WorkflowScenarios.WriteInputs(inputs, scenario);

        var options = new McpServerOptions
        {
            RequestScope = scenario.RequestScope,
            Profile = scenario.RequestScope ? string.Empty : scenario.ProfileName,
            AllowedProfiles = scenario.RequestScope ? [scenario.ProfileName] : [],
            InputRoots = [inputs],
            MaxResultBytes = McpLimits.DefaultProcessResultCeilingBytes,
            MaxOperationSeconds = 300,
        };
        var process = McpProcessContext.Create(options, WorkflowScenarios.Profiles);
        var owner = scenario.RequestScope
            ? ArtifactOwner.From(
                new SqlTargetRequest(scenario.ProfileName, new Dictionary<string, string>()),
                TargetResolver.Resolve(
                    new SqlTargetRequest(scenario.ProfileName, new Dictionary<string, string>()),
                    WorkflowScenarios.Profiles()))
            : process.FixedScope!.Owner;
        var module = new WorkflowModule(scenario, owner, SqlHarnessPaths.CompareDir);
        ISqlHarnessModule Factory(McpScope scope) => process.Decorate(module);

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        var serverOptions = McpHost.CreateServerOptions();
        using var eofShutdown = new CancellationTokenSource();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, eofShutdown.Token);
        await using var eofInput = new EofShutdownInput(Console.OpenStandardInput(), eofShutdown);
        await using var rewrittenInput = new McpProtocolVersionRewriteInput(eofInput);
        McpToolCatalog.Wire(serverOptions, process, hostShutdown: lifetime.Token, moduleFactory: Factory);

        var exit = (int)SqlHarnessExitCode.Success;
        try
        {
            await using var server = McpServer.Create(
                new StreamServerTransport(rewrittenInput, Console.OpenStandardOutput(), McpHost.ServerName, NullLoggerFactory.Instance),
                serverOptions,
                NullLoggerFactory.Instance,
                serviceProvider: null);
            await server.RunAsync(lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            exit = (int)SqlHarnessExitCode.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine("sqlharness-workflow-host: " + exception.Message);
            exit = 1;
        }
        finally
        {
            File.WriteAllText(
                Path.Combine(home, "workflow-host-stats.json"),
                JsonSerializer.Serialize(
                    new { connectionAttempts = module.ConnectionAttempts, queryCalls = module.QueryCalls },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        return exit;
    }
}