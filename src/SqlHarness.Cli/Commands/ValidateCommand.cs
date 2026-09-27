using System.ComponentModel;
using Spectre.Console.Cli;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Cli.Commands;

[Description("Classify SQL offline using a closed profile; this command never connects.")]
public sealed class ValidateCommand(OutputContext output, Renderer renderer) : AsyncCommand<ValidateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[profile]")]
        [Description("Closed target profile used to select the SQL dialect.")]
        public string? Profile { get; set; }

        [CommandOption("--var <KEY=VALUE>")]
        [Description("Profile variable. Values are used for local resolution and omitted from output.")]
        public string[] Vars { get; set; } = [];

        [CommandOption("--file <PATH>")]
        [Description("SQL file to classify without execution.")]
        public string? File { get; set; }

        [CommandOption("--param <VALUE>")]
        [Description("Parameter declaration name[[:type]]=value; values are never returned.")]
        public string[] Parameters { get; set; } = [];

        [CommandOption("--json")]
        [Description("Write one JSON validation report.")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Profile) || string.IsNullOrWhiteSpace(settings.File) || !settings.Json)
            return Invalid("validate requires a closed profile, --file, and --json.");

        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in settings.Vars)
        {
            var equals = declaration.IndexOf('=');
            if (equals < 1 || !vars.TryAdd(declaration[..equals], declaration[(equals + 1)..]))
                return Invalid("Each --var must use a unique key=value declaration.");
        }

        string sql;
        try
        {
            sql = await SqlInputReader.ReadFileAsync(settings.File, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (SqlInputTooLargeException) { return Invalid(SqlInputReader.TooLargeMessage, new SqlHarnessError("input_too_large", "input", SqlInputReader.TooLargeMessage, "Provide a SQL file up to 16 MiB UTF-8 via --file.")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid("Unable to read SQL input file.", new SqlHarnessError("input_file_unavailable", "input", "Unable to read SQL input file."));
        }

        try
        {
            var report = SqlValidation.Validate(new SqlTargetRequest(settings.Profile, vars), sql, settings.Parameters, ProfileStore.Load());
            var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);
            renderer.Render(outcome, OutputMode.Json, output.Capture, "validate");
            output.Capture.Flush();
            return (int)SqlHarnessExitCode.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid("Local profile data is unavailable.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Invalid("SQL or profile validation could not be completed.");
        }
    }

    private int Invalid(string message, SqlHarnessError? error = null)
    {
        renderer.RenderError(SqlHarnessExitCode.Safety, message, OutputMode.Json, "validate", output.Capture, error);
        output.Capture.Flush();
        return (int)SqlHarnessExitCode.Safety;
    }
}
