using System.ComponentModel;
using System.Globalization;

using Spectre.Console.Cli;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

public sealed class WatchCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer, CliInput input)
    : SqlHarnessCommand<WatchCommand.Settings>(module, output, renderer)
{
    private const int DefaultTimeoutSeconds = 30;
    private const int DefaultMaxRows = 50;
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultMaxDuration = TimeSpan.FromMinutes(15);
    private const int DefaultUntilUnchanged = 3;

    public sealed class Settings : TargetSettings
    {
        [CommandOption("--file <PATH>")] public string? File { get; set; }
        [Description("Bind name[[:type]]=value. Types: nvarchar, nvarchar(max), varchar, varchar(max), char, nchar, int, bigint, smallint, tinyint, bit, decimal, decimal(p,s), numeric, numeric(p,s), float, real, money, smallmoney, date, time, datetime, datetime2, smalldatetime, datetimeoffset, uniqueidentifier, varbinary, varbinary(max), hierarchyid, geography, geometry. Null: name:null or name:type:null.")]
        [CommandOption("--param <VALUE>")]
        public string[] Parameters { get; set; } = [];
        [CommandOption("--timeout <SECONDS>")][DefaultValue(DefaultTimeoutSeconds)] public int Timeout { get; set; } = DefaultTimeoutSeconds;
        [CommandOption("--max-rows <COUNT>")][DefaultValue(DefaultMaxRows)] public int MaxRows { get; set; } = DefaultMaxRows;
        [CommandOption("--interval <DURATION>")] public string? Interval { get; set; }
        [CommandOption("--max-duration <DURATION>")] public string? MaxDuration { get; set; }
        [CommandOption("--until <PREDICATE>")] public string? Until { get; set; }
        [CommandOption("--until-unchanged <COUNT>")] public int? UntilUnchanged { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!settings.TryTarget(out var target, out var error)) return Invalid(error);

        var hasUntil = !string.IsNullOrWhiteSpace(settings.Until);
        var hasUntilUnchanged = settings.UntilUnchanged is not null;
        if (hasUntil && hasUntilUnchanged)
            return Invalid("Specify exactly one of --until or --until-unchanged.");

        string? until = null;
        int? untilUnchanged = null;
        if (hasUntil)
            until = settings.Until!.Trim();
        else if (hasUntilUnchanged)
        {
            if (settings.UntilUnchanged is < 1)
                return Invalid("--until-unchanged must be a positive integer.");
            untilUnchanged = settings.UntilUnchanged;
        }
        else
            untilUnchanged = DefaultUntilUnchanged;

        TimeSpan interval;
        if (string.IsNullOrWhiteSpace(settings.Interval))
            interval = DefaultInterval;
        else if (!TryParseDuration(settings.Interval, out interval, out var intervalError))
            return Invalid(intervalError);

        TimeSpan maxDuration;
        if (string.IsNullOrWhiteSpace(settings.MaxDuration))
            maxDuration = DefaultMaxDuration;
        else if (!TryParseDuration(settings.MaxDuration, out maxDuration, out var maxDurationError))
            return Invalid(maxDurationError);

        var hasFile = !string.IsNullOrWhiteSpace(settings.File);
        // Prefer explicit --file when present. Agent shells often redirect an empty stdin,
        // which must not block --file (xor of hasFile and StdinRedirected was too strict).
        if (!hasFile && !input.StdinRedirected)
            return Invalid("Provide exactly one SQL source: --file or redirected stdin.");

        try
        {
            var sql = hasFile ? await File.ReadAllTextAsync(settings.File!, ct) : await input.Stdin.ReadToEndAsync(ct);
            return await Dispatch(
                new SqlHarnessWatchOperation(
                    target,
                    sql,
                    settings.Parameters,
                    settings.Timeout,
                    settings.MaxRows,
                    interval,
                    maxDuration,
                    until,
                    untilUnchanged),
                ResolveOutputMode(settings.Json),
                ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Invalid("Unable to read SQL input file.");
        }
    }

    /// <summary>
    /// Positive integral duration with optional s/m/h suffix (bare number = seconds).
    /// Rejects values whose TimeSpan exceeds 24 hours.
    /// </summary>
    internal static bool TryParseDuration(string text, out TimeSpan duration, out string error)
    {
        duration = default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Duration must be a positive integral value with optional s, m, or h suffix.";
            return false;
        }

        var trimmed = text.Trim();
        char? unit = null;
        var numberPart = trimmed;
        if (trimmed.Length > 0 && char.IsAsciiLetter(trimmed[^1]))
        {
            unit = char.ToLowerInvariant(trimmed[^1]);
            if (unit is not ('s' or 'm' or 'h'))
            {
                error = "Duration must be a positive integral value with optional s, m, or h suffix.";
                return false;
            }

            numberPart = trimmed[..^1];
        }

        if (numberPart.Length == 0
            || !long.TryParse(numberPart, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            error = "Duration must be a positive integral value with optional s, m, or h suffix.";
            return false;
        }

        try
        {
            duration = unit switch
            {
                null or 's' => TimeSpan.FromSeconds(value),
                'm' => TimeSpan.FromMinutes(value),
                'h' => TimeSpan.FromHours(value),
                _ => default,
            };
        }
        catch (OverflowException)
        {
            error = "Duration must not exceed 24 hours.";
            return false;
        }

        if (duration > TimeSpan.FromHours(24))
        {
            error = "Duration must not exceed 24 hours.";
            return false;
        }

        return true;
    }
}
