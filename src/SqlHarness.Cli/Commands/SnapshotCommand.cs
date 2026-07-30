using System.ComponentModel;
using System.Text.RegularExpressions;

using Spectre.Console.Cli;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

public sealed class SnapshotCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer, CliInput input)
    : SqlHarnessCommand<SnapshotCommand.Settings>(module, output, renderer)
{
    private const int DefaultTimeoutSeconds = 30;
    private const int DefaultMaxRows = 50;

    private static readonly Regex SnapshotNamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public sealed class Settings : TargetSettings
    {
        [CommandOption("--file <PATH>")] public string? File { get; set; }
        [Description("Bind name[[:type]]=value. Types: nvarchar, nvarchar(max), varchar, varchar(max), char, nchar, int, bigint, smallint, tinyint, bit, decimal, decimal(p,s), numeric, numeric(p,s), float, real, money, smallmoney, date, time, datetime, datetime2, smalldatetime, datetimeoffset, uniqueidentifier, varbinary, varbinary(max), hierarchyid, geography, geometry. Null: name:null or name:type:null.")]
        [CommandOption("--param <VALUE>")]
        public string[] Parameters { get; set; } = [];
        [CommandOption("--timeout <SECONDS>")][DefaultValue(DefaultTimeoutSeconds)] public int Timeout { get; set; } = DefaultTimeoutSeconds;
        [CommandOption("--max-rows <COUNT>")][DefaultValue(DefaultMaxRows)] public int MaxRows { get; set; } = DefaultMaxRows;
        [CommandOption("--name <LABEL>")] public string? Name { get; set; }
        [CommandOption("--diff")] public bool Diff { get; set; }
        [CommandOption("--force")] public bool Force { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!settings.TryTarget(out var target, out var error)) return Invalid(error);

        if (settings.Diff && settings.Force)
            return Invalid("--force cannot be combined with --diff.");

        if (string.IsNullOrWhiteSpace(settings.Name) || !SnapshotNamePattern.IsMatch(settings.Name))
            return Invalid("--name must match ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$.");

        var hasFile = !string.IsNullOrWhiteSpace(settings.File);
        // Prefer explicit --file when present. Agent shells often redirect an empty stdin,
        // which must not block --file (xor of hasFile and StdinRedirected was too strict).
        if (!hasFile && !input.StdinRedirected)
            return Invalid("Provide exactly one SQL source: --file or redirected stdin.");

        try
        {
            var sql = hasFile ? await File.ReadAllTextAsync(settings.File!, ct) : await input.Stdin.ReadToEndAsync(ct);
            return await Dispatch(
                new SqlHarnessSnapshotOperation(
                    target,
                    sql,
                    settings.Parameters,
                    settings.Timeout,
                    settings.MaxRows,
                    settings.Name,
                    settings.Diff,
                    settings.Force),
                ResolveOutputMode(settings.Json),
                ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Invalid("Unable to read SQL input file.");
        }
    }
}