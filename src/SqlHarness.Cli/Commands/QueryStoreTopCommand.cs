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
        if (settings.Timeout is < 1 or > 300 || settings.Top is < 1 or > 500)
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
            ResolveOutputMode(settings.Json),
            ct);
    }
}

public static class QueryStoreWindowParser
{
    private const int MinimumMinutes = 1;
    private const int MaximumMinutes = 44640;
    private const string Error = "--window must be a positive integer with an m, h, or d suffix, totaling 1..44640 minutes.";

    private static readonly Regex WindowPattern = new(
        "^([1-9][0-9]*)([mhd])$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public static int Parse(string text)
    {
        if (!TryRead(text, out var magnitude, out var factor))
            throw new ArgumentException(Error);

        long minutes;
        try
        {
            minutes = checked(magnitude * factor);
        }
        catch (OverflowException)
        {
            throw new ArgumentException(Error);
        }

        if (minutes is < MinimumMinutes or > MaximumMinutes)
            throw new ArgumentException(Error);

        return (int)minutes;
    }

    private static bool TryRead(string text, out long magnitude, out long factor)
    {
        magnitude = 0;
        factor = 0;
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

        factor = match.Groups[2].ValueSpan[0] switch
        {
            'm' => 1L,
            'h' => 60L,
            'd' => 1440L,
            _ => 0L,
        };
        return factor != 0;
    }
}