using System.ComponentModel;

using Spectre.Console.Cli;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

public sealed class PingCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer) : SqlHarnessCommand<PingCommand.Settings>(module, output, renderer)
{
    public sealed class Settings : TargetSettings
    {
        [CommandOption("--timeout <SECONDS>")][DefaultValue(5)] public int Timeout { get; set; } = 5;
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!settings.TryTarget(out var target, out var error)) return Task.FromResult(Invalid(error));
        if (settings.Timeout is < 1 or > 300) return Task.FromResult(Invalid("--timeout must be 1..300."));
        return Dispatch(new SqlHarnessPingOperation(target, settings.Timeout), ResolveOutputMode(settings.Json), ct);
    }
}