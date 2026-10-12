using System.ComponentModel;

using Spectre.Console.Cli;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Cli.Commands;

[Description("Describe the local SQLHarness command and engine contract; with a profile, add that engine's parameter types and limits (offline, never connects).")]
public sealed class CapabilitiesCommand(OutputContext output, Renderer renderer) : AsyncCommand<CapabilitiesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[profile]")]
        [Description("Optional target profile: scope the document to its engine. Read offline; secrets and variables are not needed.")]
        public string? Profile { get; set; }

        [CommandOption("--json")]
        [Description("Write one JSON capabilities document.")]
        public bool Json { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!settings.Json)
            return Task.FromResult(Reject("capabilities requires --json."));

        SqlHarnessCapabilities capabilities;
        if (string.IsNullOrWhiteSpace(settings.Profile))
        {
            capabilities = SqlHarnessCapabilitiesProvider.Get();
        }
        else
        {
            try
            {
                var profiles = ProfileStore.Load();
                if (!profiles.TryGetValue(settings.Profile, out var profile))
                    return Task.FromResult(RejectProfile("profile_unknown", UnknownProfileMessage(settings.Profile, profiles.Keys)));
                capabilities = SqlHarnessCapabilitiesProvider.Get(settings.Profile, profile.Engine);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Task.FromResult(RejectProfile("profile_unavailable", $"Profile '{settings.Profile}' could not be resolved: {exception.Message}"));
            }
        }

        output.Capture.WriteLine(System.Text.Json.JsonSerializer.Serialize(capabilities, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }));
        output.Capture.Flush();
        return Task.FromResult((int)SqlHarnessExitCode.Success);
    }

    private static string UnknownProfileMessage(string profile, IEnumerable<string> configured)
    {
        var names = configured.Order(StringComparer.Ordinal).ToArray();
        return names.Length == 0
            ? $"Unknown profile '{profile}'. No profiles are configured in {SqlHarnessPaths.TargetsFile}."
            : $"Unknown profile '{profile}'. Configured profiles: {string.Join(", ", names)}.";
    }

    private int RejectProfile(string code, string message)
    {
        renderer.RenderError(SqlHarnessExitCode.Safety, message, OutputMode.Json, "capabilities", output.Capture, new SqlHarnessError(code, "profile", message));
        output.Capture.Flush();
        return (int)SqlHarnessExitCode.Safety;
    }

    private int Reject(string message)
    {
        output.Capture.WriteLine(message);
        output.Capture.Flush();
        return (int)SqlHarnessExitCode.Safety;
    }
}