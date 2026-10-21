using System.ComponentModel;
using System.Text;

using Spectre.Console;
using Spectre.Console.Cli;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

// Public so public CLI command/renderer APIs can accept text, legacy JSON, summaries, agent envelopes, and watch NDJSON streams.
public enum OutputMode
{
    Text,
    Json,
    JsonSummary,
    Agent,
    Ndjson,
}

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
    [CommandOption("--engine <NAME>")] public string? Engine { get; set; }
    [CommandOption("--ssl-mode <MODE>")] public string? SslMode { get; set; }
    [CommandOption("--root-certificate <PATH>")] public string? RootCertificate { get; set; }
    [CommandOption("--json")] public bool Json { get; set; }
    [CommandOption("--output <MODE>")] public string? Output { get; set; }
    [CommandOption("--max-output-bytes <BYTES>")][DefaultValue(65536)] public int MaxOutputBytes { get; set; } = 65536;
    [CommandOption("--max-cell-chars <CHARS>")][DefaultValue(AgentOutputLimits.DefaultCellCharacters)] public int MaxCellChars { get; set; } = AgentOutputLimits.DefaultCellCharacters;

    public bool TryTarget(out SqlTargetRequest target, out string error)
    {
        target = default!; error = string.Empty;
        var directValues = SqlTargetRequest.SuppliesDirectOption(
            Server, Database, Auth, SqlUser, PasswordEnvVar, TrustServerCertificate, Engine, SslMode, RootCertificate);
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
            var key = item[..separator];
            if (!vars.TryAdd(key, item[(separator + 1)..]))
            {
                var safeKey = key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');
                error = safeKey
                    ? $"Duplicate --var key '{key.ToLowerInvariant()}'."
                    : "Duplicate --var key.";
                return false;
            }
        }
        target = new(Profile, vars, Server, Database, Auth, UnsafeDirect, SqlUser, PasswordEnvVar, TrustServerCertificate, Engine, SslMode, RootCertificate);
        return true;
    }
}

internal static class ParameterInputCliReader
{
    internal static Task<IReadOnlyList<SqlHarnessParameterInput>?> ReadAsync(
        string? path,
        IReadOnlyList<string> declarations,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult<IReadOnlyList<SqlHarnessParameterInput>?>(null);
        if (declarations.Count > 0)
            throw new ParameterSetFileException("Use either --param or --params-json, not both.");
        return ReadFileAsync(path, cancellationToken);

        static async Task<IReadOnlyList<SqlHarnessParameterInput>?> ReadFileAsync(string file, CancellationToken ct) =>
            await ParameterInputFileReader.ReadAsync(file, ct);
    }
}

/// <summary>SQL input larger than <see cref="SqlInputReader.MaxSqlInputUtf8Bytes"/> UTF-8 bytes.</summary>
public sealed class SqlInputTooLargeException(string message) : Exception(message);

/// <summary>
/// Bounded SQL ingress for --file and redirected stdin (16 MiB UTF-8).
/// Rejection happens before parsing and before any connection is opened, so an
/// oversized batch never reaches the safety classifier or the server. Canonical
/// hashes and equivalence are unaffected: only inputs at or under the bound are
/// admitted, and admitted inputs are never truncated.
/// </summary>
public static class SqlInputReader
{
    /// <summary>Maximum admitted SQL input size in UTF-8 bytes (16 MiB).</summary>
    public const long MaxSqlInputUtf8Bytes = 16L * 1024 * 1024;

    public const string TooLargeMessage = "SQL input exceeds the 16 MiB UTF-8 limit.";

    public static async Task<string> ReadFileAsync(string path, CancellationToken ct)
    {
        // Fast path: reject by on-disk size before allocating the string.
        // The post-read UTF-8 count below still covers encoding growth and races.
        if (new FileInfo(path).Length > MaxSqlInputUtf8Bytes)
            throw new SqlInputTooLargeException(TooLargeMessage);
        var text = await File.ReadAllTextAsync(path, ct);
        if (Encoding.UTF8.GetByteCount(text) > MaxSqlInputUtf8Bytes)
            throw new SqlInputTooLargeException(TooLargeMessage);
        return text;
    }

    public static async Task<string> ReadStdinAsync(TextReader stdin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        // Incremental UTF-8 accounting: an over-limit stream is rejected without
        // ever holding more than the bound plus one buffer. A trailing high
        // surrogate is held for the next buffer, so the count stays exact.
        char? carry = null;
        var builder = new StringBuilder();
        var buffer = new char[8192];
        long bytes = 0;
        int read;
        while ((read = await stdin.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            var span = buffer.AsSpan(0, read);
            var start = 0;
            if (carry is char held)
            {
                carry = null;
                if (span.Length > 0 && char.IsLowSurrogate(span[0]))
                {
                    bytes += 4;
                    start = 1;
                }
                else
                {
                    bytes += 3;
                }
            }
            var end = span.Length;
            if (end > start && char.IsHighSurrogate(span[end - 1]))
            {
                carry = span[end - 1];
                end--;
            }
            bytes += Encoding.UTF8.GetByteCount(span[start..end]);
            if (bytes > MaxSqlInputUtf8Bytes)
                throw new SqlInputTooLargeException(TooLargeMessage);
            builder.Append(buffer, 0, read);
        }
        if (carry is not null)
            bytes += 3;
        if (bytes > MaxSqlInputUtf8Bytes)
            throw new SqlInputTooLargeException(TooLargeMessage);
        return builder.ToString();
    }
}

public abstract class SqlHarnessCommand<TSettings>(ISqlHarnessModule module, OutputContext output, Renderer renderer, SqlHarnessConfig? config = null) : AsyncCommand<TSettings> where TSettings : CommandSettings
{
    protected ISqlHarnessModule Module => module;
    protected OutputContext Output => output;
    protected Renderer Renderer => renderer;
    protected int DefaultExecutionTimeoutSeconds => (config ?? SqlHarnessConfig.Default).Execution.DefaultTimeoutSeconds;

    protected int Invalid(string error, SqlHarnessError? structuredError = null)
    {
        var code = SqlHarnessExitCode.Safety;
        var safe = SecretRedactor.Redact(error, []);
        if (output.Mode is OutputMode.Agent or OutputMode.Json or OutputMode.JsonSummary)
            renderer.RenderError(code, safe, output.Mode, output.Command, output.Capture, structuredError, output.AgentOptions);
        else
            output.Capture.WriteLine(safe);
        return (int)code;
    }
    protected async Task<int> Dispatch(SqlHarnessOperation operation, OutputMode mode, CancellationToken ct, AgentOutputOptions? agentOptions = null)
    {
        agentOptions ??= output.AgentOptions;
        if (ValidateAgentOutputOptions(mode, agentOptions) is int invalidOutputOptions)
            return invalidOutputOptions;
        var mark = output.Begin();
        var outcome = await module.ExecuteAsync(operation, ct);
        if (mode == OutputMode.Agent)
            renderer.RenderAgent(outcome, output.Command, output.Capture, agentOptions);
        else
            renderer.Render(outcome, mode, output.Capture, output.Command);
        output.Capture.Flush();
        return await output.CompleteAsync(outcome, mark, ct);
    }

    protected int? ValidateAgentOutputOptions(OutputMode mode, AgentOutputOptions options)
    {
        var message = mode != OutputMode.Agent
            ? null
            : options.MaximumBytes is < 4096 or > 1048576
                ? $"Agent output limits must be --max-output-bytes 4096..1048576 and --max-cell-chars 0..{AgentOutputLimits.MaximumCellCharacters}."
                : options.MaximumCellCharacters is < 0 or > AgentOutputLimits.MaximumCellCharacters
                    ? $"Cell limit must be 0..{AgentOutputLimits.MaximumCellCharacters} characters."
                    : null;
        if (message is null)
            return null;

        renderer.RenderError(SqlHarnessExitCode.Safety, message, mode, output.Command, output.Capture, agentOptions: options);
        output.Capture.Flush();
        return (int)SqlHarnessExitCode.Safety;
    }

    /// <summary>
    /// Separate NDJSON output path (watch only): the module streams
    /// started/changed plus exactly one terminal record to the capture writer
    /// while polling, then the usual begin/complete footprint lets the activity journal count
    /// the whole stream. Lives on the base class because derived commands
    /// cannot capture the base primary-constructor parameters directly.
    /// </summary>
    protected async Task<int> DispatchNdjson(SqlHarnessWatchOperation operation, CancellationToken ct)
    {
        var mark = output.Begin();
        var outcome = await module.ExecuteWatchNdjsonAsync(operation, output.Capture, ct);
        output.Capture.Flush();
        return await output.CompleteAsync(outcome, mark, ct);
    }
    protected static OutputMode ResolveOutputMode(bool json, bool jsonSummary = false, string? output = null)
    {
        if (json && jsonSummary || output is not null && (json || jsonSummary)) throw new InvalidOperationException("Choose only one of --output, --json, or --json-summary.");
        if (output is not null)
            return string.Equals(output, "agent", StringComparison.OrdinalIgnoreCase)
                ? OutputMode.Agent
                : throw new InvalidOperationException("--output must be agent.");
        if (jsonSummary) return OutputMode.JsonSummary;
        if (json) return OutputMode.Json;
        return OutputMode.Text;
    }
    protected static async Task<string?> Read(string? path, CancellationToken ct) => string.IsNullOrWhiteSpace(path) ? null : await SqlInputReader.ReadFileAsync(path, ct);
    protected int InvalidInputTooLarge() => Invalid(
        SqlInputReader.TooLargeMessage,
        new SqlHarnessError(
            "input_too_large",
            "input",
            SqlInputReader.TooLargeMessage,
            "Provide SQL input up to 16 MiB UTF-8 via --file or redirected stdin."));
}

public sealed class QueryCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer, CliInput input, SqlHarnessConfig config) : SqlHarnessCommand<QueryCommand.Settings>(module, output, renderer, config)
{
    public sealed class Settings : TargetSettings
    {
        [CommandOption("--file <PATH>")] public string? File { get; set; }
        [Description("Bind name[[:type]]=value. Types: nvarchar, nvarchar(max), varchar, varchar(max), char, nchar, int, bigint, smallint, tinyint, bit, decimal, decimal(p,s), numeric, numeric(p,s), float, real, date, time, datetime, datetime2, datetimeoffset, uniqueidentifier, varbinary, varbinary(max), varbinary(n), binary(n). SQL Server only: money, smallmoney, smalldatetime, hierarchyid, geography, geometry. Null: name:null or name:type:null.")]
        [CommandOption("--param <VALUE>")]
        public string[] Parameters { get; set; } = [];
        [Description("Read fixed typed parameters from a bounded .sqljson file; exclusive with --param.")]
        [CommandOption("--params-json <PATH>")]
        public string? ParametersJson { get; set; }
        [Description("SQL execution timeout in seconds (1..300); defaults to execution.defaultTimeoutSeconds from config.json.")][CommandOption("--timeout <SECONDS>")] public int? Timeout { get; set; }
        [Description("Presentation-only row cap (0..500). The server still computes the full result; canonical hashes and equivalence cover all rows.")]
        [CommandOption("--max-rows <COUNT>")][DefaultValue(50)] public int MaxRows { get; set; } = 50;
        [Description("Check the exact arguments offline (bounds, safety classification, parameter binding, setup shape) and stop before connecting, authenticating or writing. Same report as MCP dryRun; exit 0 when allowed, 2 when rejected.")]
        [CommandOption("--dry-run")] public bool DryRun { get; set; }
    }
    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!settings.TryTarget(out var target, out var error)) return Invalid(error);
        var hasFile = !string.IsNullOrWhiteSpace(settings.File);
        // Prefer explicit --file when present. Agent shells often redirect an empty stdin,
        // which must not block --file (xor of hasFile and StdinRedirected was too strict).
        if (!hasFile && !input.StdinRedirected)
            return Invalid("Provide exactly one SQL source: --file or redirected stdin.");
        try
        {
            var sql = hasFile ? await SqlInputReader.ReadFileAsync(settings.File!, ct) : await SqlInputReader.ReadStdinAsync(input.Stdin, ct);
            var typedParameters = await ParameterInputCliReader.ReadAsync(settings.ParametersJson, settings.Parameters, ct);
            var timeout = settings.Timeout ?? DefaultExecutionTimeoutSeconds;
            if (timeout is < 1 or > 300) return Invalid("--timeout must be 1..300.");
            return await Dispatch(new SqlHarnessQueryOperation(target, sql, settings.Parameters, timeout, settings.MaxRows, false, null) { TypedParameters = typedParameters, DryRun = settings.DryRun }, ResolveOutputMode(settings.Json, output: settings.Output), ct, new AgentOutputOptions(settings.MaxOutputBytes, settings.MaxCellChars));
        }
        catch (OperationCanceledException) { throw; }
        catch (ParameterSetFileException exception) { return Invalid(exception.Message); }
        catch (SqlInputTooLargeException) { return InvalidInputTooLarge(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Invalid("Unable to read SQL input file.", new SqlHarnessError("input_file_unavailable", "input", "Unable to read SQL input file.")); }
    }
}

public sealed class MeasureCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer, SqlHarnessConfig config) : SqlHarnessCommand<MeasureCommand.Settings>(module, output, renderer, config)
{
    public sealed class Settings : TargetSettings
    {
        [CommandOption("--query <PATH>")] public string? Query { get; set; }
        [Description("Setup once on the measured session; setup locals do not carry into measured SQL. Native temp syntax and parameterized-setup rules: capabilities <profile> --json.")]
        [CommandOption("--setup <PATH>")] public string? Setup { get; set; }
        [Description("Optional optimization subject; provide together with --scope.")]
        [CommandOption("--subject <LABEL>")] public string? Subject { get; set; }
        [Description("Optional optimization scope; provide together with --subject.")]
        [CommandOption("--scope <LABEL>")] public string? Scope { get; set; }
        [Description("Bind name[[:type]]=value. Types: nvarchar, nvarchar(max), varchar, varchar(max), char, nchar, int, bigint, smallint, tinyint, bit, decimal, decimal(p,s), numeric, numeric(p,s), float, real, date, time, datetime, datetime2, datetimeoffset, uniqueidentifier, varbinary, varbinary(max), varbinary(n), binary(n). SQL Server only: money, smallmoney, smalldatetime, hierarchyid, geography, geometry. Null: name:null or name:type:null.")]
        [CommandOption("--param <VALUE>")]
        public string[] Parameters { get; set; } = [];
        [Description("Read fixed typed parameters from a bounded .sqljson file; exclusive with --param.")]
        [CommandOption("--params-json <PATH>")]
        public string? ParametersJson { get; set; }
        [Description("2..32 named JSON sets with matching declared types; only name and parameters (64 KiB; BOM/comments/trailing commas accepted). Entries may be scalar strings or typed objects; table/list execution is not available yet. Fixed --param names must not overlap. Each set runs on its own session with setup bound to that set; sets run in order and the first failure stops the run, keeping completed sets. Report per-set stability, not cross-set equivalence.")]
        [CommandOption("--param-set <PATH>")]
        public string[] ParameterSets { get; set; } = [];
        [CommandOption("--repeat <COUNT>")][DefaultValue(5)] public int Repeat { get; set; } = 5;
        [Description("SQL execution timeout in seconds (1..300); defaults to execution.defaultTimeoutSeconds from config.json.")][CommandOption("--timeout <SECONDS>")] public int? Timeout { get; set; }
        [CommandOption("--json-summary")] public bool JsonSummary { get; set; }
        [Description("Check the exact arguments offline (bounds, safety classification, parameter binding, setup shape) and stop before connecting, authenticating or writing. Same report as MCP dryRun; exit 0 when allowed, 2 when rejected.")]
        [CommandOption("--dry-run")] public bool DryRun { get; set; }
    }
    protected override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.TryTarget(out var target, out var error)) return Invalid(error);
        if (s.Json && s.JsonSummary || s.Output is not null && (s.Json || s.JsonSummary)) return Invalid("Choose only one of --output, --json, or --json-summary.");
        if (string.IsNullOrWhiteSpace(s.Query)) return Invalid("--query SQL file is required.");
        var timeout = s.Timeout ?? DefaultExecutionTimeoutSeconds;
        if (timeout is < 1 or > 300 || s.Repeat is < 1 or > 100) return Invalid("--timeout must be 1..300 and --repeat must be 1..100.");
        var setPaths = s.ParameterSets ?? [];
        if (setPaths.Length == 1)
            return Invalid("Exactly one --param-set is invalid. Use --param for one set.");
        CompareContext? measureContext;
        try
        {
            measureContext = CompareContext.Parse(s.Subject, s.Scope, required: false);
        }
        catch (ArgumentException exception)
        {
            return Invalid(exception.Message);
        }
        try
        {
            var typedParameters = await ParameterInputCliReader.ReadAsync(s.ParametersJson, s.Parameters, ct);
            var parameterSets = setPaths.Length == 0 ? null : await ReadParameterSetsAsync(setPaths, ct);
            return await Dispatch(
                new SqlHarnessMeasureOperation(target, await Read(s.Setup, ct), (await Read(s.Query, ct))!, s.Parameters, timeout, s.Repeat, parameterSets) { TypedParameters = typedParameters, DryRun = s.DryRun, Context = measureContext },
                ResolveOutputMode(s.Json, s.JsonSummary, s.Output),
                ct,
                new AgentOutputOptions(s.MaxOutputBytes, s.MaxCellChars));
        }
        catch (OperationCanceledException) { throw; }
        catch (ParameterSetFileException exception) { return Invalid(exception.Message); }
        catch (SqlInputTooLargeException) { return InvalidInputTooLarge(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Invalid("Unable to read SQL input file.", new SqlHarnessError("input_file_unavailable", "input", "Unable to read SQL input file.")); }
    }

    // Read every set before returning so a later failure is not skipped.
    private static async Task<IReadOnlyList<SqlHarnessParameterSetInput>> ReadParameterSetsAsync(
        IReadOnlyList<string> paths, CancellationToken ct)
    {
        var sets = new List<SqlHarnessParameterSetInput>(paths.Count);
        ParameterSetFileException? failure = null;
        foreach (var path in paths)
        {
            try
            {
                sets.Add(await ParameterSetFileReader.ReadAsync(path, ct));
            }
            catch (ParameterSetFileException exception)
            {
                failure ??= exception;
            }
        }

        if (failure is not null)
            throw failure;
        return sets;
    }
}

public sealed class CompareCommand(ISqlHarnessModule module, OutputContext output, Renderer renderer, CompareConfig compareConfig, SqlHarnessConfig config) : SqlHarnessCommand<CompareCommand.Settings>(module, output, renderer, config)
{
    public sealed class Settings : TargetSettings
    {
        [CommandOption("--baseline <PATH>")] public string? Baseline { get; set; }
        [CommandOption("--candidate <PATH>")] public string? Candidate { get; set; }
        [CommandOption("--subject <LABEL>")] public string? Subject { get; set; }
        [CommandOption("--scope <LABEL>")] public string? Scope { get; set; }
        [Description("Setup once per case's session; setup locals do not carry into measured SQL. Native temp syntax and parameterized-setup rules: capabilities <profile> --json.")]
        [CommandOption("--setup <PATH>")] public string? Setup { get; set; }
        [Description("Bind name[[:type]]=value. Types: nvarchar, nvarchar(max), varchar, varchar(max), char, nchar, int, bigint, smallint, tinyint, bit, decimal, decimal(p,s), numeric, numeric(p,s), float, real, date, time, datetime, datetime2, datetimeoffset, uniqueidentifier, varbinary, varbinary(max), varbinary(n), binary(n). SQL Server only: money, smallmoney, smalldatetime, hierarchyid, geography, geometry. Null: name:null or name:type:null.")]
        [CommandOption("--param <VALUE>")]
        public string[] Parameters { get; set; } = [];
        [Description("Read fixed typed parameters from a bounded .sqljson file; exclusive with --param.")]
        [CommandOption("--params-json <PATH>")]
        public string? ParametersJson { get; set; }
        [Description("One typed parameter: 2..32 distinct, comma-separated nonempty values in supplied order; no overlap with fixed --param. A fresh session/setup per value. First failure stops the run; completed cell artifacts remain.")]
        [CommandOption("--matrix <NAME:TYPE=VALUES>")]
        public string[] Matrix { get; set; } = [];
        [CommandOption("--repeat <COUNT>")][DefaultValue(5)] public int Repeat { get; set; } = 5;
        [Description("SQL execution timeout in seconds (1..300); defaults to execution.defaultTimeoutSeconds from config.json.")][CommandOption("--timeout <SECONDS>")] public int? Timeout { get; set; }
        [Description("Compare observed rows: ordered, multiset, set, or off. Read regression verdict/reasons separately; it does not change the exit code.")]
        [CommandOption("--compare-results <MODE>")]
        [DefaultValue("ordered")]
        public string CompareResults { get; set; } = "ordered";
        [Description("Also require matching column nullability (allowNull) for result equivalence. Default compares observed data only and reports nullability separately.")]
        [CommandOption("--compare-nullability")]
        public bool CompareNullability { get; set; }
        [CommandOption("--json-summary")] public bool JsonSummary { get; set; }
        [Description("Check the exact arguments offline (bounds, safety classification, parameter binding, setup shape) and stop before connecting, authenticating or writing. Same report as MCP dryRun; exit 0 when allowed, 2 when rejected.")]
        [CommandOption("--dry-run")] public bool DryRun { get; set; }
    }
    protected override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.TryTarget(out var target, out var error)) return Invalid(error);
        if (s.Json && s.JsonSummary || s.Output is not null && (s.Json || s.JsonSummary)) return Invalid("Choose only one of --output, --json, or --json-summary.");
        var matrix = s.Matrix ?? [];
        if (matrix.Length > 1) return Invalid("Version 1 accepts exactly one --matrix option.");
        if (string.IsNullOrWhiteSpace(s.Baseline) || string.IsNullOrWhiteSpace(s.Candidate)) return Invalid("Both --baseline and --candidate SQL files are required.");
        var timeout = s.Timeout ?? DefaultExecutionTimeoutSeconds;
        if (timeout is < 1 or > 300 || s.Repeat is < 1 or > 100) return Invalid("--timeout must be 1..300 and --repeat must be 1..100.");
        if (!TryParseCompareResults(s.CompareResults, out var compareResults))
            return Invalid("--compare-results must be ordered, multiset, set, or off.");
        if (s.CompareNullability && compareResults == ResultComparisonMode.Off)
            return Invalid("--compare-nullability requires --compare-results ordered, multiset, or set.");
        CompareContext? compareContext;
        try
        {
            compareContext = CompareContext.Parse(s.Subject, s.Scope, compareConfig.RequireContext);
        }
        catch (ArgumentException exception)
        {
            return Invalid(exception.Message);
        }
        try
        {
            var typedParameters = await ParameterInputCliReader.ReadAsync(s.ParametersJson, s.Parameters, ct);
            var setup = await Read(s.Setup, ct);
            var baseline = (await Read(s.Baseline, ct))!;
            var candidate = (await Read(s.Candidate, ct))!;
            SqlHarnessOperation operation = matrix.Length == 1
                ? new SqlHarnessCompareMatrixOperation(target, setup, baseline, candidate, s.Parameters, timeout, s.Repeat, matrix[0], compareResults, s.CompareNullability) { TypedParameters = typedParameters, DryRun = s.DryRun, Context = compareContext }
                : new SqlHarnessCompareOperation(target, setup, baseline, candidate, s.Parameters, timeout, s.Repeat, compareResults, s.CompareNullability) { TypedParameters = typedParameters, DryRun = s.DryRun, Context = compareContext };
            return await Dispatch(operation, ResolveOutputMode(s.Json, s.JsonSummary, s.Output), ct, new AgentOutputOptions(s.MaxOutputBytes, s.MaxCellChars));
        }
        catch (OperationCanceledException) { throw; }
        catch (ParameterSetFileException exception) { return Invalid(exception.Message); }
        catch (SqlInputTooLargeException) { return InvalidInputTooLarge(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Invalid("Unable to read SQL input file.", new SqlHarnessError("input_file_unavailable", "input", "Unable to read SQL input file.")); }
    }

    private static bool TryParseCompareResults(string value, out ResultComparisonMode mode)
    {
        if (string.Equals(value, "ordered", StringComparison.OrdinalIgnoreCase))
        {
            mode = ResultComparisonMode.Ordered;
            return true;
        }
        if (string.Equals(value, "multiset", StringComparison.OrdinalIgnoreCase))
        {
            mode = ResultComparisonMode.Multiset;
            return true;
        }
        if (string.Equals(value, "set", StringComparison.OrdinalIgnoreCase))
        {
            mode = ResultComparisonMode.Set;
            return true;
        }
        if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            mode = ResultComparisonMode.Off;
            return true;
        }

        mode = default;
        return false;
    }
}

public sealed record CliInput(TextReader Stdin, bool StdinRedirected);