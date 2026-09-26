using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class CapabilitiesCommandTests
{
    [Fact]
    public async Task Capabilities_reports_build_engines_limits_and_command_support_without_dispatch()
    {
        var module = new RecordingModule();
        var output = new StringWriter();
        var exitCode = await SqlHarnessCli.Create(module, output).RunAsync(["capabilities", "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Empty(module.Operations);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.True(root.GetProperty("contractVersion").GetInt32() > 0);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("buildId").GetString()));
        Assert.Contains(root.GetProperty("commands").EnumerateArray(), command => command.GetProperty("name").GetString() == "validate");
        var sqlServer = root.GetProperty("engines").EnumerateArray().Single(engine => engine.GetProperty("name").GetString() == "sqlserver");
        var postgres = root.GetProperty("engines").EnumerateArray().Single(engine => engine.GetProperty("name").GetString() == "postgres");
        Assert.True(sqlServer.GetProperty("supportsQstop").GetBoolean());
        Assert.False(postgres.GetProperty("supportsQstop").GetBoolean());
        Assert.Contains("agentOutputBytes", root.GetProperty("limits").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task Capabilities_without_json_explains_required_output_mode()
    {
        var output = new StringWriter();
        var exitCode = await SqlHarnessCli.Create(new RecordingModule(), output).RunAsync(["capabilities"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exitCode);
        Assert.Contains("capabilities requires --json", output.ToString(), StringComparison.Ordinal);
    }

    private sealed class RecordingModule : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        }
    }
}
