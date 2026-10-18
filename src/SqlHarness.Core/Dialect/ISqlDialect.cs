namespace SqlHarness.Core.Dialect;

internal interface ISqlDialect
{
    SqlEngine Engine { get; }

    SqlSafetyDecision Classify(
        string sql,
        SqlUsage usage,
        string? database,
        bool allowMutation,
        string? confirmDatabase,
        IReadOnlySet<string> sessionTempTables);

    SqlSafetyDecision Classify(
        string sql,
        SqlUsage usage,
        string? database,
        bool allowMutation,
        string? confirmDatabase,
        IReadOnlySet<string> sessionTempTables,
        IReadOnlySet<string> tableValuedParameterNames) =>
        Classify(sql, usage, database, allowMutation, confirmDatabase, sessionTempTables);

    /// <summary>Legacy text adapter kept for compatibility and characterization tests only (012/final F3): no production call site uses it. Production resolves and binds through <see cref="BindParameters"/>.</summary>
    IReadOnlyList<SqlHarnessParameter> ParseParameters(IReadOnlyList<string> inputs);

    /// <summary>Binds the typed model. <see cref="ParseParameters"/> is the declaration-text adapter over it.</summary>
    IReadOnlyList<SqlHarnessParameter> BindParameters(IEnumerable<SqlHarnessParameterInput> inputs);

    void ValidateParameterReferences(IReadOnlyList<SqlHarnessParameter> parameters, params string?[] batches);

    void ValidateMeasuredBatch(string sql);

    Task<SessionState?> CaptureSessionStateAsync(ISqlSession session, CancellationToken ct) =>
        Task.FromResult<SessionState?>(null);

    Task RestoreSessionStateAsync(ISqlSession session, SessionState? state, CancellationToken ct) =>
        Task.CompletedTask;

    Task<CollectedCompareRun> ExecuteBenchmarkRunAsync(
        ISqlSession session,
        string sql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds,
        int repetition,
        string variant,
        CanonicalResultAccumulator? raw,
        bool captureComparison,
        int comparisonMaximumRows,
        CancellationToken ct);
}
