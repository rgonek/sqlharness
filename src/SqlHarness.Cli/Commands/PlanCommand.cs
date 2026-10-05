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
        [Description("SQL Server Showplan XML or Postgres EXPLAIN JSON file, or -/omitted for stdin.")]
        public string? File { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
        [CommandOption("--output <MODE>")]
        public string? Output { get; set; }
        [CommandOption("--max-output-bytes <BYTES>")][DefaultValue(16384)] public int MaxOutputBytes { get; set; } = 16384;
        [CommandOption("--max-cell-chars <CHARS>")][DefaultValue(512)] public int MaxCellChars { get; set; } = 512;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        try
        {
            await using var file = string.IsNullOrWhiteSpace(settings.File) || settings.File == "-"
                ? null
                : new FileStream(settings.File, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bounded = await BoundedPlanInputReader.ReadAsync(file ?? input.Stream, ct);
            if (settings.Json && settings.Output is not null) return Invalid("Choose only one of --output and --json.");
            return await Dispatch(
                new SqlHarnessPlanOperation(bounded.Text, bounded.Footprint),
                ResolveOutputMode(settings.Json, output: settings.Output),
                ct,
                new AgentOutputOptions(settings.MaxOutputBytes, settings.MaxCellChars));
        }
        catch (OperationCanceledException) { throw; }
        catch (PlanInputSafetyException exception) { return Invalid(exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid("Unable to read execution plan input.", new SqlHarnessError("input_file_unavailable", "input", "Unable to read execution plan input."));
        }
    }
}