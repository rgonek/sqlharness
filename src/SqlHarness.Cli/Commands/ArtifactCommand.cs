using System.ComponentModel;

using Spectre.Console.Cli;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

/// <summary>
/// Offline selective read of an existing benchmark artifact. Takes no target,
/// profile, or module: a refusal or a result never opens a connection.
/// </summary>
[Description("Read safe sections of a saved benchmark artifact offline; never connects.")]
public sealed class ArtifactCommand(OutputContext output, Renderer renderer) : AsyncCommand<ArtifactCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[id]")]
        [Description("Artifact directory name from the saved report's artifactDirectory.")]
        public string? Id { get; set; }

        [CommandOption("--section <SECTION>")]
        [Description("Safe section to read: summary, metrics, operators, statements, matrix-cells, or plan.")]
        public string? Section { get; set; }

        [CommandOption("--cursor <INDEX>")]
        [Description("Optional zero-based continuation cursor for paged artifact sections.")]
        public int? Cursor { get; set; }

        [CommandOption("--variant <VARIANT>")]
        [Description("Plan variant: baseline, candidate, or query.")]
        public string? Variant { get; set; }

        [CommandOption("--statement <INDEX>")]
        [Description("Zero-based statement ordinal for section=plan.")]
        public int Statement { get; set; }

        [CommandOption("--parameter-set <NAME>")]
        [Description("Measure parameter set for section=plan.")]
        public string? ParameterSet { get; set; }

        [CommandOption("--repetition <INDEX>")]
        [Description("Measured repetition to select for section=plan.")]
        public int? Repetition { get; set; }

        [CommandOption("--include-text")]
        [Description("Include bounded statement text and predicates for section=plan.")]
        public bool IncludeText { get; set; }

        [CommandOption("--json")]
        [Description("Write the safe section as JSON.")]
        public bool Json { get; set; }

        [CommandOption("--output <MODE>")]
        [Description("Write the safe section in the agent envelope.")]
        public string? Output { get; set; }

        [CommandOption("--max-output-bytes <BYTES>")][DefaultValue(65536)] public int MaxOutputBytes { get; set; } = 65536;
        [CommandOption("--max-cell-chars <CHARS>")][DefaultValue(512)] public int MaxCellChars { get; set; } = 512;
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (settings.Json && settings.Output is not null)
            return Invalid("Choose only one of --output and --json.", OutputMode.Json);
        OutputMode mode;
        if (settings.Output is not null)
        {
            if (!string.Equals(settings.Output, "agent", StringComparison.OrdinalIgnoreCase))
                return Invalid("--output must be agent.", OutputMode.Agent);
            mode = OutputMode.Agent;
        }
        else if (settings.Json)
        {
            mode = OutputMode.Json;
        }
        else
        {
            return Invalid("artifact requires --json or --output agent.", OutputMode.Json);
        }

        var options = new AgentOutputOptions(settings.MaxOutputBytes, settings.MaxCellChars);
        if (mode == OutputMode.Agent
            && (options.MaximumBytes is < 4096 or > 1048576 || options.MaximumCellCharacters is < 0 or > 4096))
        {
            renderer.RenderError(
                SqlHarnessExitCode.Safety,
                "Agent output limits must be --max-output-bytes 4096..1048576 and --max-cell-chars 0..4096.",
                mode, "artifact", output.Capture, agentOptions: options);
            output.Capture.Flush();
            return Task.FromResult((int)SqlHarnessExitCode.Safety);
        }

        if (string.IsNullOrWhiteSpace(settings.Id) || string.IsNullOrWhiteSpace(settings.Section))
            return Invalid("artifact requires an artifact id and --section summary|metrics|operators|statements|matrix-cells|plan.", mode);
        if (settings.Cursor is < 0 || settings.Cursor is not null && settings.Section is not (ArtifactReader.MatrixCellsSection or ArtifactReader.StatementsSection or ArtifactReader.OperatorsSection or ArtifactReader.PlanSection))
            return Invalid("--cursor is a nonnegative offset for a paged artifact section.", mode);

        object result;
        try
        {
            if (settings.Section != ArtifactReader.PlanSection && (settings.Variant is not null || settings.Statement != 0 || settings.ParameterSet is not null || settings.Repetition is not null || settings.IncludeText))
                return Invalid("Plan selectors are only valid with --section plan.", mode);
            var selector = settings.Section == ArtifactReader.PlanSection && (settings.Variant is not null || settings.Statement != 0 || settings.ParameterSet is not null || settings.Repetition is not null)
                ? new ArtifactPlanSelector(settings.Variant ?? string.Empty, settings.Statement, settings.ParameterSet, settings.Repetition) : null;
            result = ArtifactReader.ReadSection(SqlHarnessPaths.CompareDir, settings.Id, settings.Section, cursor: settings.Cursor,
                planSelector: selector, includeText: settings.IncludeText);
        }
        catch (ArtifactReadException exception)
        {
            return Invalid(exception.Message, mode, exception.ExitCode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid("Artifact storage is unavailable.", mode, SqlHarnessExitCode.LocalStorage);
        }

        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, result, null);
        if (mode == OutputMode.Agent)
            renderer.RenderAgent(outcome, "artifact", output.Capture, options);
        else
            renderer.Render(outcome, mode, output.Capture, "artifact");
        output.Capture.Flush();
        return Task.FromResult((int)SqlHarnessExitCode.Success);
    }

    private Task<int> Invalid(string message, OutputMode mode, SqlHarnessExitCode code = SqlHarnessExitCode.Safety)
    {
        renderer.RenderError(code, message, mode, "artifact", output.Capture);
        output.Capture.Flush();
        return Task.FromResult((int)code);
    }
}