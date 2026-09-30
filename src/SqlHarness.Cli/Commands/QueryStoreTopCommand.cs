using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

using Spectre.Console.Cli;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

public sealed class QueryStoreTopCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer) : SqlHarnessCommand<QueryStoreTopCommand.Settings>(module, output, renderer)
{
    public sealed class Settings : TargetSettings
    {
        [CommandOption("--top <COUNT>")][DefaultValue(20)] public int Top { get; set; } = 20;
        [CommandOption("--window <DURATION>")][DefaultValue("24h")] public string Window { get; set; } = "24h";
        [CommandOption("--timeout <SECONDS>")][DefaultValue(30)] public int Timeout { get; set; } = 30;
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!settings.TryTarget(out var target, out var error)) return Task.FromResult(Invalid(error));
        if (!OperationLimits.IsQueryTimeoutSeconds(settings.Timeout) || !OperationLimits.IsTop(settings.Top))
            return Task.FromResult(Invalid("--timeout must be 1..300 and --top must be 1..500."));
        int windowMinutes;
        try
        {
            windowMinutes = QueryStoreWindowParser.Parse(settings.Window);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(Invalid(exception.Message));
        }

        return Dispatch(
            new SqlHarnessQueryStoreTopOperation(target, settings.Top, windowMinutes, settings.Timeout),
            ResolveOutputMode(settings.Json, output: settings.Output),
            ct);
    }
}

public static class QueryStoreWindowParser
{
    private const string Error = "--window must be a positive integer with an m, h, or d suffix, totaling 1..44640 minutes.";

    private static readonly Regex WindowPattern = new(
        "^([1-9][0-9]*)([mhd])$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public static int Parse(string text)
    {
        // Format policy (lowercase-only units, no surrounding whitespace) stays
        // here; the pure magnitude-to-minutes conversion and 1..44640 range
        // live in Core.
        if (!TryRead(text, out var magnitude, out var unit)
            || !OperationLimits.TryConvertQueryStoreWindow(magnitude, unit, out var minutes))
            throw new ArgumentException(Error);

        return minutes;
    }

    private static bool TryRead(string text, out long magnitude, out char unit)
    {
        magnitude = 0;
        unit = '\0';
        if (text is null)
            return false;

        Match match;
        try
        {
            match = WindowPattern.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        if (!match.Success
            || !long.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out magnitude))
            return false;

        unit = match.Groups[2].ValueSpan[0];
        return unit is 'm' or 'h' or 'd';
    }
}
