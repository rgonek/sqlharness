using System.ComponentModel;

using Spectre.Console.Cli;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

public sealed class CountsCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer) : SqlHarnessCommand<CountsCommand.Settings>(module, output, renderer)
{
    public sealed class Settings : TargetSettings
    {
        [CommandOption("--table <NAME>")] public string[] Tables { get; set; } = [];
        [CommandOption("--like <PATTERN>")] public string? Like { get; set; }
        [CommandOption("--top <COUNT>")][DefaultValue(50)] public int Top { get; set; } = 50;
        [CommandOption("--exact")] public bool Exact { get; set; }
        [CommandOption("--timeout <SECONDS>")][DefaultValue(30)] public int Timeout { get; set; } = 30;
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!settings.TryTarget(out var target, out var error)) return Task.FromResult(Invalid(error));
        if (settings.Tables.Length > 0 && !string.IsNullOrWhiteSpace(settings.Like))
            return Task.FromResult(Invalid("--table cannot be combined with --like."));
        if (settings.Timeout is < 1 or > 300 || settings.Top is < 1 or > 500)
            return Task.FromResult(Invalid("--timeout must be 1..300 and --top must be 1..500."));
        return Dispatch(
            new SqlHarnessCountsOperation(target, settings.Tables, settings.Like, settings.Top, settings.Exact, settings.Timeout),
            ResolveOutputMode(settings.Json),
            ct);
    }
}
