using System.ComponentModel;

using Spectre.Console.Cli;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

public sealed class SchemaCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer) : SqlHarnessCommand<SchemaCommand.Settings>(module, output, renderer)
{
    public sealed class Settings : TargetSettings
    {
        [CommandOption("--filter <PATTERN>")] public string? Filter { get; set; }
        [CommandOption("--max-objects <COUNT>")][DefaultValue(50)] public int MaxObjects { get; set; } = 50;
        [CommandOption("--timeout <SECONDS>")][DefaultValue(30)] public int Timeout { get; set; } = 30;
    }
    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!settings.TryTarget(out var target, out var error)) return Task.FromResult(Invalid(error));
        if (settings.Timeout is < 1 or > 300 || settings.MaxObjects is < 1 or > 500) return Task.FromResult(Invalid("--timeout must be 1..300 and --max-objects must be 1..500."));
        return Dispatch(new SqlHarnessSchemaOperation(target, settings.Filter, settings.Timeout, settings.MaxObjects), settings.Json, ct);
    }
}