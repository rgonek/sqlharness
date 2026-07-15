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
    int Repeat) : SqlHarnessOperation;

public sealed record SqlHarnessMeasureOperation(
    SqlTargetRequest Target,
    string? SetupSql,
    string QuerySql,
    IReadOnlyList<string> Parameters,
    int TimeoutSeconds,
    int Repeat) : SqlHarnessOperation;

public sealed record SqlHarnessGainOperation : SqlHarnessOperation;

public sealed record SqlTargetRequest(
    string? Profile,
    IReadOnlyDictionary<string, string> Vars,
    string? Server = null,
    string? Database = null,
    string? Auth = null,
    bool UnsafeDirect = false);

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
}
