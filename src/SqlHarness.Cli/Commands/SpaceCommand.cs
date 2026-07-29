using System.ComponentModel;

using Spectre.Console.Cli;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

public sealed class SpaceCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer) : SqlHarnessCommand<SpaceCommand.Settings>(module, output, renderer)
{
    public sealed class Settings : TargetSettings
    {
        [CommandOption("--top <COUNT>")][DefaultValue(25)] public int Top { get; set; } = 25;
        [CommandOption("--object <NAME>")] public string? Object { get; set; }
        [CommandOption("--timeout <SECONDS>")][DefaultValue(30)] public int Timeout { get; set; } = 30;
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!settings.TryTarget(out var target, out var error)) return Task.FromResult(Invalid(error));
        if (settings.Timeout is < 1 or > 300 || settings.Top is < 1 or > 500)
            return Task.FromResult(Invalid("--timeout must be 1..300 and --top must be 1..500."));
        if (!TryValidateObject(settings.Object, out var objectError))
            return Task.FromResult(Invalid(objectError));
        return Dispatch(
            new SqlHarnessSpaceOperation(target, settings.Top, settings.Object, settings.Timeout),
            ResolveOutputMode(settings.Json),
            ct);
    }

    /// <summary>
    /// Accepts one exact name or schema.name; rejects empty parts and three-or-more-part names.
    /// Full object resolution (missing/ambiguous) happens later in Core.
    /// </summary>
    private static bool TryValidateObject(string? objectSpec, out string error)
    {
        error = string.Empty;
        if (objectSpec is null)
            return true;

        if (string.IsNullOrWhiteSpace(objectSpec))
        {
            error = "Space --object must be a single object name or schema.name.";
            return false;
        }

        var firstDot = objectSpec.IndexOf('.');
        if (firstDot < 0)
            return true;

        var lastDot = objectSpec.LastIndexOf('.');
        if (firstDot != lastDot)
        {
            error = "Space --object must be a single object name or schema.name.";
            return false;
        }

        var schema = objectSpec[..firstDot];
        var name = objectSpec[(firstDot + 1)..];
        if (schema.Length == 0 || name.Length == 0)
        {
            error = "Space --object must be a single object name or schema.name.";
            return false;
        }

        return true;
    }
}
