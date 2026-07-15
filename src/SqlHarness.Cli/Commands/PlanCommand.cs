using System.ComponentModel;
using Spectre.Console.Cli;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

public sealed class PlanCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer, PlanInput input)
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
            await using var file = string.IsNullOrWhiteSpace(settings.File) || settings.File == "-"
                ? null
                : new FileStream(settings.File, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bounded = await BoundedPlanInputReader.ReadAsync(file ?? input.Stream, ct);
            return await Dispatch(new SqlHarnessPlanOperation(bounded.Text, bounded.Footprint), settings.Json, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (PlanInputSafetyException exception) { return Invalid(exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid("Unable to read execution plan input.");
        }
    }
}
