using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlHarness.Core;

public interface ISqlHarnessModule
{
    Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default);
}

public abstract record SqlHarnessOperation;

public sealed record SqlHarnessQueryOperation(
    SqlTargetRequest Target,
    string Sql,
    IReadOnlyList<string> Parameters,
    int TimeoutSeconds,
    int MaxRows,
    bool AllowMutation,
    string? ConfirmDatabase) : SqlHarnessOperation;

public sealed record SqlHarnessCompareOperation(
    SqlTargetRequest Target,
    string? SetupSql,
    string BaselineSql,
    string CandidateSql,
    IReadOnlyList<string> Parameters,
    int TimeoutSeconds,
    int Repeat,
    ResultComparisonMode CompareResults = ResultComparisonMode.Ordered) : SqlHarnessOperation;

public sealed record SqlHarnessCompareMatrixOperation(
    SqlTargetRequest Target,
    string? SetupSql,
    string BaselineSql,
    string CandidateSql,
    IReadOnlyList<string> Parameters,
    int TimeoutSeconds,
    int Repeat,
    string Matrix,
    ResultComparisonMode CompareResults = ResultComparisonMode.Ordered) : SqlHarnessOperation;

public sealed record CompareMatrixCellReport(
    int Index,
    string ParameterValue,
    SqlHarnessCompareReport Compare);

public sealed record SqlHarnessCompareMatrixReport(
    string ParameterName,
    string ParameterType,
    IReadOnlyList<CompareMatrixCellReport> Cells);

public sealed record SqlHarnessMeasureOperation(
    SqlTargetRequest Target,
    string? SetupSql,
    string QuerySql,
    IReadOnlyList<string> Parameters,
    int TimeoutSeconds,
    int Repeat) : SqlHarnessOperation;

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
    string? Until, int? UntilUnchanged) : SqlHarnessOperation;

public sealed record SqlHarnessSnapshotOperation(
    SqlTargetRequest Target, string Sql, IReadOnlyList<string> Parameters,
    int TimeoutSeconds, int MaxRows, string Name, bool Diff, bool Force) : SqlHarnessOperation;

public enum WatchExitReason { ConditionMet, Unchanged, MaxDuration }
public enum SnapshotVerdict { Stored, Identical, Different }

public sealed record SqlHarnessWatchPoll(
    int Poll, long ElapsedMilliseconds, string ResultHash,
    IReadOnlyList<SqlHarnessResultSetReport> ResultSets);

public sealed record SqlHarnessWatchReport(
    SqlHarnessTargetIdentityReport Target, int PollCount, long ElapsedMilliseconds,
    WatchExitReason ExitReason, IReadOnlyList<SqlHarnessWatchPoll> EmittedPolls);

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

// net8.0 inbox System.Text.Json has no JsonStringEnumMemberName and no generic converter.
[JsonConverter(typeof(JsonStringEnumConverter<IndexOverlapClassification>))]
public enum IndexOverlapClassification
{
    [JsonStringEnumMemberName("covered")]
    Covered,

    [JsonStringEnumMemberName("include-gap")]
    IncludeGap,

    [JsonStringEnumMemberName("partial-key")]
    PartialKey,

    [JsonStringEnumMemberName("new-shape")]
    NewShape,
}

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class JsonStringEnumMemberNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

public sealed class JsonStringEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> Names = CreateNames();
    private static readonly Dictionary<string, TEnum> Values = Names.ToDictionary(
        pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String
            || reader.GetString() is not string text
            || !Values.TryGetValue(text, out var value))
            throw new JsonException($"Unknown {typeof(TEnum).Name} value.");

        return value;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (!Names.TryGetValue(value, out var name))
            throw new JsonException($"Unknown {typeof(TEnum).Name} value.");

        writer.WriteStringValue(name);
    }

    private static Dictionary<TEnum, string> CreateNames()
    {
        var names = new Dictionary<TEnum, string>();
        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attribute = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>();
            if (attribute is null)
                throw new InvalidOperationException(
                    $"{typeof(TEnum).Name}.{field.Name} requires {nameof(JsonStringEnumMemberNameAttribute)}.");

            names.Add((TEnum)field.GetValue(null)!, attribute.Name);
        }

        return names;
    }
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
    string? Engine = null);

public sealed record SqlHarnessOutcome(
    SqlHarnessExitCode ExitCode,
    object? Report,
    string? SafeError,
    SqlHarnessEmissionReceipt? EmissionReceipt = null);

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