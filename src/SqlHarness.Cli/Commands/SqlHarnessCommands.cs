using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

public abstract class TargetSettings : CommandSettings
{
    [CommandArgument(0, "[profile]")] public string? Profile { get; set; }
    [CommandOption("--var <KEY=VALUE>")] public string[] Vars { get; set; } = [];
    [CommandOption("--unsafe-direct")] public bool UnsafeDirect { get; set; }
    [CommandOption("--server <SERVER>")] public string? Server { get; set; }
    [CommandOption("--database <DATABASE>")] public string? Database { get; set; }
    [CommandOption("--auth <STRATEGY>")] public string? Auth { get; set; }
    [CommandOption("--sql-user <USER>")] public string? SqlUser { get; set; }
    [CommandOption("--password-env-var <NAME>")] public string? PasswordEnvVar { get; set; }
    [CommandOption("--trust-server-certificate")] public bool TrustServerCertificate { get; set; }
    [CommandOption("--json")] public bool Json { get; set; }

    public bool TryTarget(out SqlTargetRequest target, out string error)
    {
        target = default!; error = string.Empty;
        var directValues = new object?[] { Server, Database, Auth, SqlUser, PasswordEnvVar }.Any(v => v is string s && !string.IsNullOrWhiteSpace(s)) || TrustServerCertificate;
        if (UnsafeDirect)
        {
            if (!string.IsNullOrWhiteSpace(Profile)) { error = "Choose either a profile or --unsafe-direct, not both."; return false; }
            if (Vars.Length > 0) { error = "--var is valid only with a profile."; return false; }
            if (string.IsNullOrWhiteSpace(Server) || string.IsNullOrWhiteSpace(Database) || string.IsNullOrWhiteSpace(Auth)) { error = "Direct mode requires --server, --database, and --auth."; return false; }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Profile)) { error = "Provide a profile or use --unsafe-direct."; return false; }
            if (directValues) { error = "Direct SQL options are valid only with --unsafe-direct."; return false; }
        }
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Vars)
        {
            var separator = item.IndexOf('=');
            if (separator < 1) { error = "Each --var must use key=value format."; return false; }
            vars[item[..separator]] = item[(separator + 1)..];
        }
        target = new(Profile, vars, Server, Database, Auth, UnsafeDirect, SqlUser, PasswordEnvVar, TrustServerCertificate);
        return true;
    }
}

public abstract class SqlHarnessCommand<TSettings>(ISqlHarnessModule module, OutputContext output, Renderer renderer) : AsyncCommand<TSettings> where TSettings : CommandSettings
{
    protected int Invalid(string error) { output.Capture.WriteLine(SecretRedactor.Redact(error, [])); return (int)SqlHarnessExitCode.Safety; }
    protected async Task<int> Dispatch(SqlHarnessOperation operation, bool json, CancellationToken ct)
    {
        var mark = output.Begin();
        var outcome = await module.ExecuteAsync(operation, ct);
        renderer.Render(outcome, json, output.Capture);
        output.Capture.Flush();
        return await output.CompleteAsync(outcome, mark, ct);
    }
    protected static async Task<string?> Read(string? path, CancellationToken ct) => string.IsNullOrWhiteSpace(path) ? null : await File.ReadAllTextAsync(path, ct);
}

public sealed class QueryCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer, CliInput input) : SqlHarnessCommand<QueryCommand.Settings>(module, output, renderer)
{
    public sealed class Settings : TargetSettings
    {
        [CommandOption("--file <PATH>")] public string? File { get; set; }
        [CommandOption("--param <VALUE>")] public string[] Parameters { get; set; } = [];
        [CommandOption("--timeout <SECONDS>")][DefaultValue(30)] public int Timeout { get; set; } = 30;
        [CommandOption("--max-rows <COUNT>")][DefaultValue(50)] public int MaxRows { get; set; } = 50;
        [CommandOption("--allow-mutation")] public bool AllowMutation { get; set; }
        [CommandOption("--confirm-database <DATABASE>")] public string? ConfirmDatabase { get; set; }
    }
    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!settings.TryTarget(out var target, out var error)) return Invalid(error);
        var hasFile = !string.IsNullOrWhiteSpace(settings.File);
        if (hasFile == input.StdinRedirected) return Invalid("Provide exactly one SQL source: --file or redirected stdin.");
        try
        {
            var sql = hasFile ? await File.ReadAllTextAsync(settings.File!, ct) : await input.Stdin.ReadToEndAsync(ct);
            return await Dispatch(new SqlHarnessQueryOperation(target, sql, settings.Parameters, settings.Timeout, settings.MaxRows, settings.AllowMutation, settings.ConfirmDatabase), settings.Json, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Invalid("Unable to read SQL input file."); }
    }
}

public sealed class MeasureCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer) : SqlHarnessCommand<MeasureCommand.Settings>(module, output, renderer)
{
    public sealed class Settings : TargetSettings
    {
        [CommandOption("--query <PATH>")] public string? Query { get; set; }
        [CommandOption("--setup <PATH>")] public string? Setup { get; set; }
        [CommandOption("--param <VALUE>")] public string[] Parameters { get; set; } = [];
        [CommandOption("--repeat <COUNT>")][DefaultValue(5)] public int Repeat { get; set; } = 5;
        [CommandOption("--timeout <SECONDS>")][DefaultValue(30)] public int Timeout { get; set; } = 30;
    }
    protected override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.TryTarget(out var target, out var error)) return Invalid(error);
        if (string.IsNullOrWhiteSpace(s.Query)) return Invalid("--query SQL file is required.");
        if (s.Timeout is < 1 or > 300 || s.Repeat is < 1 or > 100) return Invalid("--timeout must be 1..300 and --repeat must be 1..100.");
        try { return await Dispatch(new SqlHarnessMeasureOperation(target, await Read(s.Setup, ct), (await Read(s.Query, ct))!, s.Parameters, s.Timeout, s.Repeat), s.Json, ct); }
        catch (OperationCanceledException) { throw; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Invalid("Unable to read SQL input file."); }
    }
}

public sealed class CompareCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer) : SqlHarnessCommand<CompareCommand.Settings>(module, output, renderer)
{
    public sealed class Settings : TargetSettings
    {
        [CommandOption("--baseline <PATH>")] public string? Baseline { get; set; }
        [CommandOption("--candidate <PATH>")] public string? Candidate { get; set; }
        [CommandOption("--setup <PATH>")] public string? Setup { get; set; }
        [CommandOption("--param <VALUE>")] public string[] Parameters { get; set; } = [];
        [CommandOption("--repeat <COUNT>")][DefaultValue(5)] public int Repeat { get; set; } = 5;
        [CommandOption("--timeout <SECONDS>")][DefaultValue(30)] public int Timeout { get; set; } = 30;
    }
    protected override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.TryTarget(out var target, out var error)) return Invalid(error);
        if (string.IsNullOrWhiteSpace(s.Baseline) || string.IsNullOrWhiteSpace(s.Candidate)) return Invalid("Both --baseline and --candidate SQL files are required.");
        if (s.Timeout is < 1 or > 300 || s.Repeat is < 1 or > 100) return Invalid("--timeout must be 1..300 and --repeat must be 1..100.");
        try { return await Dispatch(new SqlHarnessCompareOperation(target, await Read(s.Setup, ct), (await Read(s.Baseline, ct))!, (await Read(s.Candidate, ct))!, s.Parameters, s.Timeout, s.Repeat), s.Json, ct); }
        catch (OperationCanceledException) { throw; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Invalid("Unable to read SQL input file."); }
    }
}

public sealed class GainCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer) : SqlHarnessCommand<GainCommand.Settings>(module, output, renderer)
{
    public sealed class Settings : CommandSettings { [CommandOption("--json")] public bool Json { get; set; } }
    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct) => Dispatch(new SqlHarnessGainOperation(), settings.Json, ct);
}

public sealed record CliInput(TextReader Stdin, bool StdinRedirected);
