using System.ComponentModel;
using System.Reflection;

using Spectre.Console.Cli;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Cli.Commands;

[Description("Check local installation and profile-file availability without connecting.")]
public sealed class DoctorCommand(OutputContext output) : AsyncCommand<DoctorCommand.Settings>
{
    /// <summary>
    /// Permission bits that make a directory traversable by users other than its owner.
    /// </summary>
    private const UnixFileMode BroadBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Write one JSON diagnostics document.")]
        public bool Json { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!settings.Json)
        {
            output.Capture.WriteLine("doctor requires --json.");
            output.Capture.Flush();
            return Task.FromResult((int)SqlHarnessExitCode.Safety);
        }

        var assembly = typeof(DoctorCommand).Assembly;
        var config = SqlHarnessConfigLoader.Load();
        var homePath = SqlHarnessPaths.Home;
        var homePresent = Directory.Exists(homePath);
        var homeMode = homePresent ? SqlHarnessPaths.HomeMode : null;
        bool? homePrivate = homeMode is { } mode ? (mode & BroadBits) == 0 : null;
        var warnings = new List<string>();
        if (homePrivate is false)
        {
            warnings.Add($"SQLHarness home '{homePath}' is readable by other users; run: chmod 700 '{homePath}'");
        }

        var report = new
        {
            version = assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            buildId = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            runtime = Environment.Version.ToString(),
            profilesFilePresent = File.Exists(SqlHarnessPaths.TargetsFile),
            profileDirectoryPresent = Directory.Exists(Path.GetDirectoryName(SqlHarnessPaths.TargetsFile)),
            configFilePresent = config.Status != SqlHarnessConfigStatus.Missing,
            configValid = config.Status != SqlHarnessConfigStatus.Invalid,
            journalEnabled = config.Config.Journal.Enabled,
            journalStoreSensitive = config.Config.Journal.StoreSensitive,
            diagnosticsEnabled = config.Config.Diagnostics.Enabled,
            activityJournalPresent = File.Exists(SqlHarnessPaths.ActivityDatabase),
            homePath,
            homePresent,
            homePrivate,
            warnings = warnings.ToArray(),
            databaseCheckPerformed = false,
            credentialsRead = false,
            tokenRequested = false,
            installationUpdated = false,
        };
        output.Capture.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }));
        output.Capture.Flush();
        return Task.FromResult((int)SqlHarnessExitCode.Success);
    }
}