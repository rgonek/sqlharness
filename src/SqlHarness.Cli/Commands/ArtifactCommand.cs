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
public sealed class ArtifactCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer)
    : SqlHarnessCommand<ArtifactCommand.Settings>(module, output, renderer)
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[id]")]
        [Description("Artifact directory name from the saved report's artifactDirectory.")]
        public string? Id { get; set; }

        [CommandOption("--section <SECTION>")]
        [Description("summary, metrics, operators, statements, matrix-cells, or plan. operators is capped; planShapeStable concerns shape, not timing or row counts.")]
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
        [Description("Measured repetition for section=plan; default is the identified last measured run. A missing plan is reported, never replaced by an earlier plan.")]
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
        [CommandOption("--max-cell-chars <CHARS>")][DefaultValue(AgentOutputLimits.DefaultCellCharacters)] public int MaxCellChars { get; set; } = AgentOutputLimits.DefaultCellCharacters;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (settings.Json && settings.Output is not null)
            return Invalid("Choose only one of --output and --json.");
        OutputMode mode;
        if (settings.Output is not null)
        {
            if (!string.Equals(settings.Output, "agent", StringComparison.OrdinalIgnoreCase))
                return Invalid("--output must be agent.");
            mode = OutputMode.Agent;
        }
        else if (settings.Json)
        {
            mode = OutputMode.Json;
        }
        else
        {
            return Invalid("artifact requires --json or --output agent.");
        }

        var options = new AgentOutputOptions(settings.MaxOutputBytes, settings.MaxCellChars);
        if (mode == OutputMode.Agent && options.MaximumBytes is < 4096 or > 1048576)
        {
            return Invalid($"Agent output limits must be --max-output-bytes 4096..1048576 and --max-cell-chars 0..{AgentOutputLimits.MaximumCellCharacters}.");
        }
        if (mode == OutputMode.Agent && options.MaximumCellCharacters is < 0 or > AgentOutputLimits.MaximumCellCharacters)
            return Invalid($"Cell limit must be 0..{AgentOutputLimits.MaximumCellCharacters} characters.");

        if (string.IsNullOrWhiteSpace(settings.Id) || string.IsNullOrWhiteSpace(settings.Section))
            return Invalid("artifact requires an artifact id and --section summary|metrics|operators|statements|matrix-cells|plan.");
        if (settings.Cursor is < 0 || settings.Cursor is not null && settings.Section is not (ArtifactReader.MatrixCellsSection or ArtifactReader.StatementsSection or ArtifactReader.OperatorsSection or ArtifactReader.PlanSection))
            return Invalid("--cursor is a nonnegative offset for a paged artifact section.");

        if (settings.Section != ArtifactReader.PlanSection && (settings.Variant is not null || settings.Statement != 0 || settings.ParameterSet is not null || settings.Repetition is not null || settings.IncludeText))
            return Invalid("Plan selectors are only valid with --section plan.");
        var selector = settings.Section == ArtifactReader.PlanSection && (settings.Variant is not null || settings.Statement != 0 || settings.ParameterSet is not null || settings.Repetition is not null)
            ? new ArtifactPlanSelector(settings.Variant ?? string.Empty, settings.Statement, settings.ParameterSet, settings.Repetition) : null;
        var operation = new SqlHarnessArtifactReadOperation(
            SqlHarnessPaths.CompareDir, settings.Id, settings.Section, Cursor: settings.Cursor,
            PlanSelector: selector, IncludeText: settings.IncludeText);
        return await Dispatch(operation, mode, ct, options);
    }
}