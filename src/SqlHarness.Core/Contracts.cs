using System.Text.Json;
using System.Text.Json.Serialization;

using SqlHarness.Core.Mutations;
using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

public interface ISqlHarnessModule
{
    Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default);

    /// <summary>
    /// Optional NDJSON stream for <c>watch --output ndjson</c>: writes
    /// <c>started</c>/<c>changed</c> records plus exactly one terminal
    /// (<c>completed</c>/<c>failed</c>) record to <paramref name="writer"/>
    /// while polling, flushing after every record. The returned outcome
    /// carries no report (the stream is the output) but keeps the usual exit
    /// code and journal receipt, so the whole stream counts as emitted output.
    /// The default refuses; <see cref="SqlHarnessModule"/> implements it.
    /// </summary>
    Task<SqlHarnessOutcome> ExecuteWatchNdjsonAsync(
        SqlHarnessWatchOperation operation,
        TextWriter writer,
        CancellationToken ct = default) =>
        throw new NotSupportedException("This module does not support watch NDJSON streaming.");
}

public abstract record SqlHarnessOperation
{
    [JsonIgnore]
    public string? OperationRef { get; init; }

    [JsonIgnore]
    internal Action<ConditionsModel>? RecordConditions { get; init; }
}

public sealed record SqlHarnessArtifactReadOperation(
    string Root,
    string ArtifactId,
    string Section,
    ArtifactOwner? Owner = null,
    int? Cursor = null,
    ArtifactPlanSelector? PlanSelector = null,
    bool IncludeText = false) : SqlHarnessOperation;

public sealed record SqlHarnessArtifactReadReport(object Section, string? ProducerOperationRef);

internal static class SqlHarnessOperationReferences
{
    internal static SqlHarnessOperation Ensure(SqlHarnessOperation operation) =>
        string.IsNullOrWhiteSpace(operation.OperationRef)
            ? operation with { OperationRef = Guid.NewGuid().ToString("N") }
            : operation;
}

public sealed record SqlHarnessQueryOperation(
    SqlTargetRequest Target,
    string Sql,
    IReadOnlyList<string> Parameters,
    int TimeoutSeconds,
    int MaxRows,
    bool AllowMutation,
    string? ConfirmDatabase) : SqlHarnessOperation
{
    /// <summary>Typed alternative to <see cref="Parameters"/> (012). When it is set, <see cref="Parameters"/> must be empty.</summary>
    public IReadOnlyList<SqlHarnessParameterInput>? TypedParameters { get; init; }
    public bool DryRun { get; init; }

    [JsonIgnore]
    internal ResolvedTarget? ResolvedTarget { get; init; }

    [JsonIgnore]
    internal SqlUsage? UsageOverride { get; init; }
}

public sealed record SqlHarnessMutationOperation(
    SqlTargetRequest Target,
    string Sql,
    IReadOnlyList<SqlHarnessParameterInput> Parameters,
    int TimeoutSeconds,
    int MaxRows,
    string? ExpectDatabase) : SqlHarnessOperation
{
    public bool DryRun { get; init; }
    public string SessionKey { get; init; } = "";
    internal ApprovedMutation? Approval { get; init; }
}

public sealed record SqlHarnessCompareOperation(
    SqlTargetRequest Target,
    string? SetupSql,
    string BaselineSql,
    string CandidateSql,
    IReadOnlyList<string> Parameters,
    int TimeoutSeconds,
    int Repeat,
    ResultComparisonMode CompareResults = ResultComparisonMode.Ordered,
    bool CompareNullability = false) : SqlHarnessOperation
{
    public CompareContext? Context { get; init; }
    /// <summary>Typed alternative to <see cref="Parameters"/> (012). When it is set, <see cref="Parameters"/> must be empty.</summary>
    public IReadOnlyList<SqlHarnessParameterInput>? TypedParameters { get; init; }
    public bool DryRun { get; init; }
}

public sealed record SqlHarnessCompareMatrixOperation(
    SqlTargetRequest Target,
    string? SetupSql,
    string BaselineSql,
    string CandidateSql,
    IReadOnlyList<string> Parameters,
    int TimeoutSeconds,
    int Repeat,
    string Matrix,
    ResultComparisonMode CompareResults = ResultComparisonMode.Ordered,
    bool CompareNullability = false) : SqlHarnessOperation
{
    public CompareContext? Context { get; init; }
    /// <summary>Typed alternative to <see cref="Parameters"/> (012). When it is set, <see cref="Parameters"/> must be empty.</summary>
    public IReadOnlyList<SqlHarnessParameterInput>? TypedParameters { get; init; }

    /// <summary>Typed alternative to <see cref="Matrix"/> (012). When it is set, <see cref="Matrix"/> must be empty.</summary>
    public SqlHarnessParameterMatrixInput? TypedMatrix { get; init; }
    public IReadOnlyList<SqlHarnessParameterSetInput>? ParameterSets { get; init; }
    public bool DryRun { get; init; }
}

/// <summary>
/// One SQL parameter as structure instead of a <c>name:type=value</c> declaration (012).
/// <see cref="Name"/> has no <c>@</c>. A null <see cref="Type"/> binds as <c>nvarchar</c>.
/// A null <see cref="Value"/> is SQL NULL; an empty string is a text value. The value is
/// never split or unescaped, so <c>,</c> <c>=</c> and <c>:</c> are ordinary characters.
/// </summary>
public sealed record SqlHarnessParameterInput(string Name, string? Type, string? Value)
{
    /// <summary>Name and type only (012/final F8): the generated ToString() would print Value.</summary>
    public override string ToString() => $"SqlHarnessParameterInput {{ Name = {Name}, Type = {Type} }}";
}

/// <summary>
/// One matrix dimension as structure (012): one parameter name, one type, and the values in
/// caller order. A null element is a typed NULL of <see cref="Type"/>; it is distinct from
/// an empty string and from the text <c>null</c>.
/// </summary>
public sealed record SqlHarnessParameterMatrixInput(string Name, string Type, IReadOnlyList<string?> Values)
{
    /// <summary>Name, type and a count only (012/final F8): the generated ToString() would print Values.</summary>
    public override string ToString() =>
        $"SqlHarnessParameterMatrixInput {{ Name = {Name}, Type = {Type}, Values.Count = {Values?.Count} }}";
}

/// <summary><see cref="ParameterValue"/> is null only for a typed NULL cell, which legacy matrix text cannot express.</summary>
public sealed record CompareMatrixCellReport(
    int Index,
    string? ParameterValue,
    SqlHarnessCompareReport Compare)
{
    public string SetName { get; init; } = string.Empty;
    public IReadOnlyList<BenchmarkParameterReport> SetParameters { get; init; } = [];
}

public sealed record SqlHarnessCompareMatrixReport(
    string? ParameterName,
    string? ParameterType,
    IReadOnlyList<CompareMatrixCellReport> Cells)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MatrixExecutionCoverage? Coverage { get; init; }
}

/// <summary>Identified attempted matrix cell that stopped the run. Phase is omitted when not established.</summary>
[JsonConverter(typeof(MatrixFailedCaseJsonConverter))]
public sealed record MatrixFailedCase(
    int Index,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SetName = null,
    string? ParameterValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Phase = null)
{
    [JsonIgnore]
    public bool IncludeParameterValue { get; init; }
}

internal sealed class MatrixFailedCaseJsonConverter : JsonConverter<MatrixFailedCase>
{
    public override MatrixFailedCase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Matrix failed case must be an object.");
        var index = root.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsedIndex)
            ? parsedIndex
            : throw new JsonException("Matrix failed case requires index.");
        string? setName = root.TryGetProperty("setName", out var setNameElement) && setNameElement.ValueKind == JsonValueKind.String
            ? setNameElement.GetString()
            : null;
        var hasParameterValue = root.TryGetProperty("parameterValue", out var parameterValueElement);
        string? parameterValue = hasParameterValue && parameterValueElement.ValueKind == JsonValueKind.String
            ? parameterValueElement.GetString()
            : null;
        if (hasParameterValue && parameterValueElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new JsonException("Matrix failed case parameterValue must be a string or null.");
        string? phase = root.TryGetProperty("phase", out var phaseElement) && phaseElement.ValueKind == JsonValueKind.String
            ? phaseElement.GetString()
            : null;
        return new MatrixFailedCase(index, setName, parameterValue, phase) { IncludeParameterValue = hasParameterValue };
    }

    public override void Write(Utf8JsonWriter writer, MatrixFailedCase value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("index", value.Index);
        if (value.SetName is not null)
            writer.WriteString("setName", value.SetName);
        if (value.IncludeParameterValue)
        {
            if (value.ParameterValue is null)
                writer.WriteNull("parameterValue");
            else
                writer.WriteString("parameterValue", value.ParameterValue);
        }
        if (value.Phase is not null)
            writer.WriteString("phase", value.Phase);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Execution coverage for a matrix run, distinct from inventory paging.
/// Null counts are unknown; an identified stop satisfies
/// requestedCases = completedCases + 1 + notExecutedCases.
/// </summary>
public sealed record MatrixExecutionCoverage(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RequestedCases = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CompletedCases = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NotExecutedCases = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MatrixFailedCase? FailedCase = null)
{
    public static MatrixExecutionCoverage AllCompleted(int requested) =>
        new(requested, requested, 0);

    public static MatrixExecutionCoverage IdentifiedFailure(int requested, int completed, MatrixFailedCase failedCase)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completed);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(completed, Math.Max(requested - 1, 0));
        return new(requested, completed, requested - completed - 1, failedCase);
    }
}

public sealed record SqlHarnessParameterSetInput(
    string Name, IReadOnlyList<string> Parameters)
{
    /// <summary>Typed alternative to <see cref="Parameters"/> for inline parameter sets.</summary>
    public IReadOnlyList<SqlHarnessParameterInput>? TypedParameters { get; init; }

    [JsonIgnore]
    internal string? SourcePath { get; init; }
}

public sealed record SqlHarnessMeasureOperation(
    SqlTargetRequest Target,
    string? SetupSql,
    string QuerySql,
    IReadOnlyList<string> Parameters,
    int TimeoutSeconds,
    int Repeat,
    IReadOnlyList<SqlHarnessParameterSetInput>? ParameterSets = null)
    : SqlHarnessOperation
{
    /// <summary>Typed alternative to <see cref="Parameters"/> (012). When it is set, <see cref="Parameters"/> must be empty.</summary>
    public IReadOnlyList<SqlHarnessParameterInput>? TypedParameters { get; init; }

    /// <summary>Typed one-parameter matrix, resolved by Core into indexed measure sets.</summary>
    public SqlHarnessParameterMatrixInput? TypedMatrix { get; init; }
    public bool DryRun { get; init; }
}

/// <summary>
/// Result of a dry run: the call passed every offline check a real call performs
/// (bounds, safety classification, parameter binding and references, setup shape,
/// matrix or parameter-set binding) and stopped before connecting.
/// </summary>
public sealed record SqlHarnessDryRunReport(
    string Command,
    string Classification,
    string Tier,
    IReadOnlyList<BenchmarkParameterReport> Parameters,
    int ParameterSets)
{
    public string Validation { get; init; } = "passed";
    public string Execution { get; init; } = "not-run";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BenchmarkClassificationReport? BenchmarkClassification { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompareClassificationReport? CompareClassification { get; init; }
}

public sealed record SqlHarnessPlanOperation(string ShowplanXml, OutputFootprint RawFootprint) : SqlHarnessOperation;

public sealed record SqlHarnessWatchOperation(
    SqlTargetRequest Target, string Sql, IReadOnlyList<string> Parameters,
    int TimeoutSeconds, int MaxRows, TimeSpan Interval, TimeSpan MaxDuration,
    string? Until, int? UntilUnchanged, int HistoryLimit = 100) : SqlHarnessOperation
{
    /// <summary>Typed alternative to <see cref="Parameters"/> (012). When it is set, <see cref="Parameters"/> must be empty.</summary>
    public IReadOnlyList<SqlHarnessParameterInput>? TypedParameters { get; init; }

    /// <summary>
    /// Per-poll progress for the activity journal. Internal and never serialized; the
    /// runner invokes it best-effort after each completed poll, so a throwing callback
    /// cannot change the watch outcome.
    /// </summary>
    [JsonIgnore]
    internal Action<WatchProgress>? Progress { get; init; }
}

/// <summary>
/// Optional scope owner for snapshot capture/diff (003): the MCP mapper sets
/// it from the frozen scope, the CLI leaves it null and keeps working by name.
/// The runner stamps captures with it and refuses foreign or ownerless
/// baselines before touching data; null means no scope enforcement.
/// </summary>
public sealed record SqlHarnessSnapshotOperation(
    SqlTargetRequest Target, string Sql, IReadOnlyList<string> Parameters,
    int TimeoutSeconds, int MaxRows, string Name, bool Diff, bool Force,
    ArtifactOwner? Owner = null) : SqlHarnessOperation
{
    /// <summary>Typed alternative to <see cref="Parameters"/> (012). When it is set, <see cref="Parameters"/> must be empty.</summary>
    public IReadOnlyList<SqlHarnessParameterInput>? TypedParameters { get; init; }
}

public enum WatchExitReason { ConditionMet, Unchanged, MaxDuration }
public enum SnapshotVerdict { Stored, Identical, Different }

public sealed record SqlHarnessWatchPoll(
    int Poll, long ElapsedMilliseconds, string ResultHash,
    IReadOnlyList<SqlHarnessResultSetReport> ResultSets);

public sealed record SqlHarnessWatchReport(
    SqlHarnessTargetIdentityReport Target, int PollCount, long ElapsedMilliseconds,
    WatchExitReason ExitReason, IReadOnlyList<SqlHarnessWatchPoll> EmittedPolls,
    int TotalChangedPolls = 0, int OmittedPolls = 0);

public sealed record SqlHarnessSnapshotDifference(
    int ResultSet, long? Row, int? Column, string Kind);

public sealed record SqlHarnessSnapshotReport(
    SqlHarnessTargetIdentityReport Target, string Name, SnapshotVerdict Verdict,
    int DifferenceCount, IReadOnlyList<SqlHarnessSnapshotDifference> Differences);

public sealed record SqlTargetRequest(
    string? Profile,
    IReadOnlyDictionary<string, string> Vars,
    string? Server = null,
    string? Database = null,
    string? Auth = null,
    bool UnsafeDirect = false,
    string? SqlUser = null,
    string? PasswordEnvVar = null,
    bool TrustServerCertificate = false,
    string? Engine = null,
    string? SslMode = null,
    string? RootCertificate = null)
{
    public bool HasSuppliedDirectOption =>
        SuppliesDirectOption(
            Server, Database, Auth, SqlUser, PasswordEnvVar, TrustServerCertificate, Engine, SslMode, RootCertificate);

    // Null means omitted. Empty and unknown values still count as supplied.
    public static bool SuppliesDirectOption(
        string? server,
        string? database,
        string? auth,
        string? sqlUser,
        string? passwordEnvVar,
        bool trustServerCertificate,
        string? engine,
        string? sslMode = null,
        string? rootCertificate = null) =>
        server is not null ||
        database is not null ||
        auth is not null ||
        sqlUser is not null ||
        passwordEnvVar is not null ||
        trustServerCertificate ||
        engine is not null ||
        sslMode is not null ||
        rootCertificate is not null;
}

/// <summary>
/// Frozen scope identity stamped onto every benchmark artifact manifest at
/// publish time (003): profile name, canonical var values, engine label, and
/// resolved server/database. No auth material or secrets ever land here: vars
/// hold the validated values, never passwords. Comparison is exact — profile,
/// engine, server, and database ordinal; var keys case-insensitive with exact
/// values — so any drift fails closed. A missing manifest owner never matches.
/// </summary>
public sealed record ArtifactOwner(
    string? Profile,
    IReadOnlyDictionary<string, string> Vars,
    string Engine,
    string Server,
    string Database)
{
    /// <summary>
    /// Builds the owner from the operation target request and the resolved
    /// target. Vars are stored case-insensitively with deterministic
    /// (ordinal-sorted) insertion order, so manifests serialize stably.
    /// </summary>
    public static ArtifactOwner From(SqlTargetRequest request, ResolvedTarget target)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(target);
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.Vars is not null)
        {
            foreach (var pair in request.Vars.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                vars[pair.Key] = pair.Value;
        }

        return new ArtifactOwner(
            request.Profile, vars, SqlEngineNames.Format(target.Engine), target.Server, target.Database);
    }

    /// <summary>
    /// Exact scope match: every field must agree, including the full var set.
    /// Never throws: malformed candidate metadata simply does not match.
    /// </summary>
    public bool Matches(ArtifactOwner? actual)
    {
        if (actual is null || actual.Vars is null || Vars is null)
            return false;
        if (!string.Equals(Profile, actual.Profile, StringComparison.Ordinal))
            return false;
        if (!string.Equals(Engine, actual.Engine, StringComparison.Ordinal))
            return false;
        if (!string.Equals(Server, actual.Server, StringComparison.Ordinal))
            return false;
        if (!string.Equals(Database, actual.Database, StringComparison.Ordinal))
            return false;
        if (Vars.Count != actual.Vars.Count)
            return false;
        foreach (var (key, value) in Vars)
        {
            var matched = false;
            foreach (var (actualKey, actualValue) in actual.Vars)
            {
                if (string.Equals(key, actualKey, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(value, actualValue, StringComparison.Ordinal))
                        return false;
                    matched = true;
                    break;
                }
            }

            if (!matched)
                return false;
        }

        return true;
    }
}

public sealed record SqlHarnessOutcome(
    SqlHarnessExitCode ExitCode,
    object? Report,
    string? SafeError,
    SqlHarnessEmissionReceipt? EmissionReceipt = null,
    SqlHarnessError? Error = null)
{
    /// <summary>True only for an approved mutation whose transport failed after entering command execution.</summary>
    [JsonIgnore]
    internal bool MutationOutcomeUnknown { get; init; }

    /// <summary>Structured session safety events retained for the activity journal.</summary>
    [JsonIgnore]
    internal IReadOnlyList<string> SessionWarnings { get; init; } = [];

    [JsonIgnore]
    public string? OperationRef { get; init; }

    [JsonIgnore]
    public string? ParentOperationRef { get; init; }

    /// <summary>Structured error mapped at outcome construction, before any renderer runs.</summary>
    public SqlHarnessError? MachineError { get; } = MapMachineError(ExitCode, SafeError, Error);

    /// <summary>
    /// Measured benchmark runs of a successful measure/compare, for the activity journal only.
    /// Internal and never serialized: agent output and artifacts are unchanged by its presence.
    /// </summary>
    [JsonIgnore]
    internal IReadOnlyList<CompareRunArtifact>? BenchmarkRuns { get; init; }

    /// <summary>
    /// Matrix execution coverage from the runner, including first-cell stops with a null report.
    /// </summary>
    [JsonIgnore]
    public MatrixExecutionCoverage? Coverage { get; init; }

    private static SqlHarnessError? MapMachineError(
        SqlHarnessExitCode exitCode,
        string? safeError,
        SqlHarnessError? error) =>
        error ?? (string.IsNullOrWhiteSpace(safeError)
            ? exitCode switch
            {
                SqlHarnessExitCode.WatchMaxDuration => new SqlHarnessError(
                    "watch_max_duration",
                    "watch",
                    "Watch reached its maximum duration before a stop condition was met."),
                SqlHarnessExitCode.SnapshotDifferences => new SqlHarnessError(
                    "snapshot_differences",
                    "comparison",
                    "Snapshot comparison found differences."),
                _ => null,
            }
            : SqlHarnessError.From(exitCode, safeError));
}

public sealed class SqlHarnessEmissionReceipt
{
    private readonly object _sync = new();
    private readonly Func<OutputFootprint, CancellationToken, Task<SqlHarnessExitCode>> _complete;
    private Task<SqlHarnessExitCode>? _completion;

    internal SqlHarnessEmissionReceipt(
        Func<OutputFootprint, CancellationToken, Task<SqlHarnessExitCode>> complete) =>
        _complete = complete;

    /// <summary>Pre-projection footprint known to the module; the journal records it alongside the emitted footprint.</summary>
    internal OutputFootprint? RawFootprint { get; init; }

    public Task<SqlHarnessExitCode> CompleteAsync(
        OutputFootprint emitted,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(emitted);
        ArgumentOutOfRangeException.ThrowIfNegative(emitted.Bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(emitted.Lines);
        lock (_sync)
            return _completion ??= _complete(emitted, ct);
    }
}

public enum SqlHarnessExitCode
{
    Success = 0,
    Safety = 2,
    Authentication = 3,
    TargetMismatch = 4,
    SqlExecution = 5,
    LocalStorage = 6,
    WatchMaxDuration = 7,
    SnapshotDifferences = 8,
}
