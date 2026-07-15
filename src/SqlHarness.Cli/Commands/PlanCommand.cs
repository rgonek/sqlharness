using System.ComponentModel;
using Spectre.Console.Cli;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

public sealed class PlanCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer, CliInput input)
    : SqlHarnessCommand<PlanCommand.Settings>(module, output, renderer)
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[file]")]
        [Description("Showplan XML file, or -/omitted for stdin.")]
        public string? File { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        try
        {
            var xml = string.IsNullOrWhiteSpace(settings.File) || settings.File == "-"
                ? await input.Stdin.ReadToEndAsync(ct)
                : await System.IO.File.ReadAllTextAsync(settings.File, ct);
            return await Dispatch(new SqlHarnessPlanOperation(xml), settings.Json, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid("Unable to read execution plan input.");
        }
    }
}
