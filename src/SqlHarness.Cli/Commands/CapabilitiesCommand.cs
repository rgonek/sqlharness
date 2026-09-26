using System.ComponentModel;

using Spectre.Console.Cli;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

[Description("Describe the local SQLHarness command and engine contract.")]
public sealed class CapabilitiesCommand(OutputContext output) : AsyncCommand<CapabilitiesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Write one JSON capabilities document.")]
        public bool Json { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!settings.Json)
        {
            output.Capture.WriteLine("capabilities requires --json.");
            output.Capture.Flush();
            return Task.FromResult((int)SqlHarnessExitCode.Safety);
        }
        output.Capture.WriteLine(System.Text.Json.JsonSerializer.Serialize(SqlHarnessCapabilitiesProvider.Get(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }));
        output.Capture.Flush();
        return Task.FromResult((int)SqlHarnessExitCode.Success);
    }
}
