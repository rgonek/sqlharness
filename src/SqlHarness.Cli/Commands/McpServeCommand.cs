using System.ComponentModel;

using Spectre.Console.Cli;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Cli.Commands;

/// <summary>
/// Error sink for `mcp serve`. Production wires process stderr so stdout
/// carries only protocol frames; tests inject a capture writer.
/// </summary>
public sealed class McpHostConsole(TextWriter error)
{
    public TextWriter Error { get; } = error;
}

/// <summary>
/// `mcp serve`: start one profile-scoped MCP server process over stdio
/// (spec sections 2-3). Startup syntax is exactly
/// `mcp serve &lt;profile&gt; --var key=value [--input-root &lt;absolute-directory&gt;]`;
/// --unsafe-direct and any other startup option are rejected with exit 2.
/// </summary>
public sealed class McpServeCommand(McpHostConsole console) : AsyncCommand<McpServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[<profile>]")]
        [Description("Closed target profile frozen for this server process.")]
        public string? Profile { get; set; }

        [CommandOption("--request-scope")]
        [Description("Resolve a closed target profile from each target-dependent request.")]
        public bool RequestScope { get; set; }

        [CommandOption("--allow-profile <PROFILE>")]
        [Description("Profile permitted for request-scoped mode. Required and repeatable with --request-scope.")]
        public string[] AllowedProfiles { get; set; } = [];

        [CommandOption("--var <KEY=VALUE>")]
        [Description("Profile variable fixed at startup.")]
        public string[] Vars { get; set; } = [];

        [CommandOption("--input-root <DIRECTORY>")]
        [Description("Absolute directory allowing file inputs. Repeatable; empty by default (no file inputs).")]
        public string[] InputRoots { get; set; } = [];

        [CommandOption("--max-result-bytes <BYTES>")]
        [Description("Process response cap 4096..1048576 bytes. Default 16384; a call may only lower it.")]
        public int MaxResultBytes { get; set; } = 16384;

        [CommandOption("--max-operation-seconds <SECONDS>")]
        [Description("Process DB time budget 1..86400 seconds. Default 900; a call may only lower it.")]
        public int MaxOperationSeconds { get; set; } = 900;

        [CommandOption("--unsafe-direct")]
        [Description("Blocked for mcp serve; MCP v1 has no unsafe-direct.")]
        public bool UnsafeDirect { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (settings.UnsafeDirect)
            return Task.FromResult(Fail("The MCP server does not support --unsafe-direct."));
        if (settings.RequestScope)
        {
            if (!string.IsNullOrWhiteSpace(settings.Profile) || settings.Vars.Length != 0)
                return Task.FromResult(Fail("Request-scoped mode cannot include a startup profile or --var."));
            if (settings.AllowedProfiles.Length == 0 || settings.AllowedProfiles.Any(string.IsNullOrWhiteSpace))
                return Task.FromResult(Fail("Request-scoped mode requires at least one --allow-profile."));
            if (settings.AllowedProfiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.AllowedProfiles.Length)
                return Task.FromResult(Fail("Each --allow-profile value must be unique."));
        }
        else if (settings.AllowedProfiles.Length != 0)
        {
            return Task.FromResult(Fail("--allow-profile requires --request-scope."));
        }
        else if (string.IsNullOrWhiteSpace(settings.Profile))
        {
            return Task.FromResult(Fail("The MCP server requires a closed profile."));
        }

        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in settings.Vars)
        {
            var separator = item.IndexOf('=');
            if (separator < 1)
                return Task.FromResult(Fail("Each --var must use key=value format."));
            var key = item[..separator];
            if (string.IsNullOrWhiteSpace(key))
                return Task.FromResult(Fail("Each --var must use key=value format."));
            try
            {
                vars.Add(key, item[(separator + 1)..]);
            }
            catch (ArgumentException)
            {
                return Task.FromResult(Fail("Each --var key must be unique."));
            }
        }

        var options = new SqlHarness.Mcp.McpServerOptions
        {
            RequestScope = settings.RequestScope,
            AllowedProfiles = settings.AllowedProfiles,
            Profile = settings.Profile ?? string.Empty,
            Vars = vars,
            InputRoots = settings.InputRoots,
            MaxResultBytes = settings.MaxResultBytes,
            MaxOperationSeconds = settings.MaxOperationSeconds,
        };
        return SqlHarness.Mcp.McpHost.RunAsync(
            options,
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            console.Error,
            () => ProfileStore.Load(),
            ct);
    }

    private int Fail(string message)
    {
        console.Error.WriteLine($"sqlharness-mcp: {message}");
        return (int)SqlHarnessExitCode.Safety;
    }
}
