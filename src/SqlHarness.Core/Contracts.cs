using System.Text.Json;
using System.Text.Json.Serialization;

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
    /// code and gain receipt, so the whole stream counts as emitted output.
    /// The default refuses; <see cref="SqlHarnessModule"/> implements it.
    /// </summary>
    Task<SqlHarnessOutcome> ExecuteWatchNdjsonAsync(
        SqlHarnessWatchOperation operation,
        TextWriter writer,
        CancellationToken ct = default) =>
        throw new NotSupportedException("This module does not support watch NDJSON streaming.");
}

public abstract record SqlHarnessOperation;

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
}

public sealed record SqlHarnessCompareOperation(
    SqlTargetRequest Target,
    string? SetupSql,
    string BaselineSql,
    string CandidateSql,
    IReadOnlyList<string> Parameters,
    int TimeoutSeconds,
    int Repeat,
    ResultComparisonMode CompareResults = ResultComparisonMode.Ordered) : SqlHarnessOperation
{
    /// <summary>Typed alternative to <see cref="Parameters"/> (012). When it is set, <see cref="Parameters"/> must be empty.</summary>
    public IReadOnlyList<SqlHarnessParameterInput>? TypedParameters { get; init; }
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
    ResultComparisonMode CompareResults = ResultComparisonMode.Ordered) : SqlHarnessOperation
{
    /// <summary>Typed alternative to <see cref="Parameters"/> (012). When it is set, <see cref="Parameters"/> must be empty.</summary>
    public IReadOnlyList<SqlHarnessParameterInput>? TypedParameters { get; init; }

    /// <summary>Typed alternative to <see cref="Matrix"/> (012). When it is set, <see cref="Matrix"/> must be empty.</summary>
    public SqlHarnessParameterMatrixInput? TypedMatrix { get; init; }
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
    SqlHarnessCompareReport Compare);

public sealed record SqlHarnessCompareMatrixReport(
    string ParameterName,
    string ParameterType,
    IReadOnlyList<CompareMatrixCellReport> Cells);

public sealed record SqlHarnessParameterSetInput(
    string Name, IReadOnlyList<string> Parameters);

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
}

public sealed record SqlHarnessGainOperation : SqlHarnessOperation;

public sealed record SqlHarnessPlanOperation(string ShowplanXml, OutputFootprint RawFootprint) : SqlHarnessOperation;

public sealed record SqlHarnessSchemaOperation(
    SqlTargetRequest Target,
    string? Filter,
    int TimeoutSeconds,
    int MaxObjects = 50,
    string? Object = null) : SqlHarnessOperation;

public sealed record SqlHarnessPingOperation(
    SqlTargetRequest Target, int TimeoutSeconds) : SqlHarnessOperation;

public sealed record SqlHarnessCountsOperation(
    SqlTargetRequest Target, IReadOnlyList<string> Tables, string? Like,
    int Top, bool Exact, int TimeoutSeconds) : SqlHarnessOperation;

public sealed record SqlHarnessSpaceOperation(
    SqlTargetRequest Target, int Top, string? Object,
    int TimeoutSeconds) : SqlHarnessOperation;

public sealed record SqlHarnessQueryStoreTopOperation(
    SqlTargetRequest Target, int Top, int WindowMinutes,
    int TimeoutSeconds) : SqlHarnessOperation;

public sealed record SqlHarnessIndexesOperation(
    SqlTargetRequest Target, int Top, string? Object,
    int TimeoutSeconds) : SqlHarnessOperation;

public static class IndexObjectSyntax
{
    private const string InvalidObject = "indexes --object must be a single object name or schema.name.";

    public static bool TryParse(string? objectSpec, out string? schema, out string? name, out string error)
    {
        schema = null;
        name = null;
        error = string.Empty;
        if (objectSpec is null)
            return true;

        if (string.IsNullOrWhiteSpace(objectSpec))
        {
            error = InvalidObject;
            return false;
        }

        var firstDot = objectSpec.IndexOf('.');
        if (firstDot < 0)
        {
            name = objectSpec;
            return true;
        }

        if (firstDot != objectSpec.LastIndexOf('.') || firstDot == 0 || firstDot == objectSpec.Length - 1)
        {
            error = InvalidObject;
            return false;
        }

        schema = objectSpec[..firstDot];
        name = objectSpec[(firstDot + 1)..];
        return true;
    }
}

public sealed record SqlHarnessWatchOperation(
    SqlTargetRequest Target, string Sql, IReadOnlyList<string> Parameters,
    int TimeoutSeconds, int MaxRows, TimeSpan Interval, TimeSpan MaxDuration,
    string? Until, int? UntilUnchanged, int HistoryLimit = 100) : SqlHarnessOperation
{
    /// <summary>Typed alternative to <see cref="Parameters"/> (012). When it is set, <see cref="Parameters"/> must be empty.</summary>
    public IReadOnlyList<SqlHarnessParameterInput>? TypedParameters { get; init; }
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

public sealed record DatabaseFileSpaceReport(
    string LogicalName, string Type, string? PhysicalName,
    decimal SizeMb, decimal UsedMb, decimal FreeMb);

public sealed record DatabaseAllocationReport(
    decimal ReservedMb, decimal UsedMb, decimal DataMb);

public sealed record TableSpaceReport(
    string Schema, string Name, long Rows,
    decimal ReservedMb, decimal UsedMb, decimal DataMb);

public sealed record IndexSpaceReport(
    string Schema, string Table, string Index, string Type,
    decimal ReservedMb, decimal UsedMb, decimal DataMb, string? Compression);

public sealed record SqlHarnessSpaceReport(
    SqlHarnessTargetIdentityReport Target,
    IReadOnlyList<DatabaseFileSpaceReport> Files,
    DatabaseAllocationReport Allocation,
    IReadOnlyList<TableSpaceReport> Tables,
    IReadOnlyList<IndexSpaceReport> Indexes);

public sealed record QueryStoreTopItemReport(
    long QueryId,
    string QueryHash,
    string? ObjectName,
    long ExecutionCount,
    int PlanCount,
    decimal TotalDurationMilliseconds,
    decimal AverageDurationMilliseconds,
    decimal MaximumDurationMilliseconds,
    decimal TotalCpuMilliseconds,
    decimal AverageCpuMilliseconds,
    decimal MaximumCpuMilliseconds,
    decimal TotalLogicalReads,
    decimal AverageLogicalReads,
    decimal MaximumLogicalReads,
    DateTimeOffset LastExecutionAt);

public sealed record SqlHarnessQueryStoreTopReport(
    SqlHarnessTargetIdentityReport Target,
    int WindowMinutes,
    int Top,
    IReadOnlyList<QueryStoreTopItemReport> Queries,
    string? ArtifactDirectory);

[JsonConverter(typeof(IndexOverlapClassificationConverter))]
public enum IndexOverlapClassification
{
    Covered, IncludeGap, PartialKey, NewShape
}

public sealed class IndexOverlapClassificationConverter : JsonConverter<IndexOverlapClassification>
{
    public override IndexOverlapClassification Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Unknown index overlap classification.");

        return reader.GetString() switch
        {
            "covered" => IndexOverlapClassification.Covered,
            "include-gap" => IndexOverlapClassification.IncludeGap,
            "partial-key" => IndexOverlapClassification.PartialKey,
            "new-shape" => IndexOverlapClassification.NewShape,
            _ => throw new JsonException("Unknown index overlap classification."),
        };
    }

    public override void Write(Utf8JsonWriter writer, IndexOverlapClassification value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            IndexOverlapClassification.Covered => "covered",
            IndexOverlapClassification.IncludeGap => "include-gap",
            IndexOverlapClassification.PartialKey => "partial-key",
            IndexOverlapClassification.NewShape => "new-shape",
            _ => throw new JsonException("Unknown index overlap classification."),
        });
}

public sealed record IndexCandidateReport(
    long CandidateId, string Schema, string Table,
    IReadOnlyList<string> EqualityColumns,
    IReadOnlyList<string> InequalityColumns,
    IReadOnlyList<string> IncludeColumns,
    long UserSeeks, long UserScans,
    decimal AverageTotalUserCost, decimal AverageUserImpactPercent,
    decimal CumulativeImpactScore,
    DateTimeOffset? LastUserSeek, DateTimeOffset? LastUserScan,
    IndexOverlapClassification Classification,
    string? BestExistingIndex,
    int MatchedKeyColumnCount, int CandidateKeyColumnCount,
    IReadOnlyList<string> MissingIncludeColumns,
    bool? ExistingIndexDisabled, bool? ExistingIndexHasFilter,
    string? ExistingIndexFilterHash);

public sealed record SqlHarnessIndexesReport(
    SqlHarnessTargetIdentityReport Target,
    DateTimeOffset ObservationSince, DateTimeOffset ObservedAt,
    int Top, string? ObjectFilter, IReadOnlyList<string> Warnings,
    IReadOnlyList<IndexCandidateReport> Candidates,
    string? ArtifactDirectory);

public sealed record SqlHarnessPingReport(
    SqlHarnessTargetIdentityReport Target, string Server, string Database,
    string Login, long DurationMilliseconds);

public sealed record SqlHarnessCountReport(
    string Schema, string Name, long Rows, string Method);

public sealed record SqlHarnessCountsReport(
    SqlHarnessTargetIdentityReport Target,
    IReadOnlyList<SqlHarnessCountReport> Tables, int Omitted);

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
    /// <summary>Structured error mapped at outcome construction, before any renderer runs.</summary>
    public SqlHarnessError? MachineError { get; } = MapMachineError(ExitCode, SafeError, Error);

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