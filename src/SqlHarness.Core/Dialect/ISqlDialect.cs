namespace SqlHarness.Core.Dialect;

internal interface ISqlDialect
{
    SqlEngine Engine { get; }
    string PingSql { get; }

    SqlSafetyDecision Classify(
        string sql,
        SqlUsage usage,
        string? database,
        bool allowMutation,
        string? confirmDatabase,
        IReadOnlySet<string> sessionTempTables);

    /// <summary>Legacy text adapter kept for compatibility and characterization tests only (012/final F3): no production call site uses it. Production resolves and binds through <see cref="BindParameters"/>.</summary>
    IReadOnlyList<SqlHarnessParameter> ParseParameters(IReadOnlyList<string> inputs);

    /// <summary>Binds the typed model. <see cref="ParseParameters"/> is the declaration-text adapter over it.</summary>
    IReadOnlyList<SqlHarnessParameter> BindParameters(IEnumerable<SqlHarnessParameterInput> inputs);

    void ValidateParameterReferences(IReadOnlyList<SqlHarnessParameter> parameters, params string?[] batches);

    void ValidateMeasuredBatch(string sql);

    Task<CollectedCompareRun> ExecuteBenchmarkRunAsync(
        ISqlSession session,
        string sql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds,
        int repetition,
        string variant,
        CanonicalResultAccumulator raw,
        bool captureComparison,
        int comparisonMaximumRows,
        CancellationToken ct);

    string CountsCatalogSql { get; }

    string BuildCountsExactSql(IReadOnlyList<ResolvedCountObject> objects);

    string SchemaSql { get; }

    string SpaceSql { get; }
}