using System.ComponentModel;

using Spectre.Console.Cli;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

public sealed class IndexesCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer) : SqlHarnessCommand<IndexesCommand.Settings>(module, output, renderer)
{
    public sealed class Settings : TargetSettings
    {
        [CommandOption("--top <COUNT>")][DefaultValue(20)] public int Top { get; set; } = 20;
        [CommandOption("--object <NAME>")] public string? Object { get; set; }
        [CommandOption("--timeout <SECONDS>")][DefaultValue(30)] public int Timeout { get; set; } = 30;
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!settings.TryTarget(out var target, out var error)) return Task.FromResult(Invalid(error));
        if (settings.Timeout is < 1 or > 300 || settings.Top is < 1 or > 500)
            return Task.FromResult(Invalid("--timeout must be 1..300 and --top must be 1..500."));
        if (!IndexObjectSyntax.TryParse(settings.Object, out _, out _, out var objectError))
            return Task.FromResult(Invalid(objectError));
        return Dispatch(
            new SqlHarnessIndexesOperation(target, settings.Top, settings.Object, settings.Timeout),
            ResolveOutputMode(settings.Json),
            ct);
    }
}