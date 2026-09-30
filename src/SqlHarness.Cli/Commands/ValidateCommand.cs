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

        [CommandOption("--usage <USAGE>")]
        [Description("Validation intent: query (default), setup, or benchmark. Benchmark adds the measured-batch shape check; setup classifies the batch as session-local preparation.")]
        public string Usage { get; set; } = "query";

        [CommandOption("--setup <PATH>")]
        [Description("Setup SQL file providing session-temp context for query or benchmark validation; read like --file. Ignored for --usage setup.")]
        public string? Setup { get; set; }

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

        if (!TryParseUsage(settings.Usage, out var usage))
            return Invalid("Unknown --usage. Supported usages: query, setup, benchmark.");

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

        string? setupSql = null;
        if (!string.IsNullOrWhiteSpace(settings.Setup))
        {
            try
            {
                setupSql = await SqlInputReader.ReadFileAsync(settings.Setup, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (SqlInputTooLargeException) { return Invalid(SqlInputReader.TooLargeMessage, new SqlHarnessError("input_too_large", "input", SqlInputReader.TooLargeMessage, "Provide a SQL file up to 16 MiB UTF-8 via --setup.")); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Invalid("Unable to read SQL setup file.", new SqlHarnessError("input_file_unavailable", "input", "Unable to read SQL setup file."));
            }
        }

        try
        {
            var report = SqlValidation.Validate(new SqlTargetRequest(settings.Profile, vars), sql, settings.Parameters, ProfileStore.Load(), new ValidationOptions(usage, setupSql));
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

    private static bool TryParseUsage(string? usage, out ValidationUsage parsed)
    {
        if (string.Equals(usage, "query", StringComparison.OrdinalIgnoreCase)) { parsed = ValidationUsage.Query; return true; }
        if (string.Equals(usage, "setup", StringComparison.OrdinalIgnoreCase)) { parsed = ValidationUsage.Setup; return true; }
        if (string.Equals(usage, "benchmark", StringComparison.OrdinalIgnoreCase)) { parsed = ValidationUsage.Benchmark; return true; }
        parsed = ValidationUsage.Query;
        return false;
    }

    private int Invalid(string message, SqlHarnessError? error = null)
    {
        renderer.RenderError(SqlHarnessExitCode.Safety, message, OutputMode.Json, "validate", output.Capture, error);
        output.Capture.Flush();
        return (int)SqlHarnessExitCode.Safety;
    }
}
