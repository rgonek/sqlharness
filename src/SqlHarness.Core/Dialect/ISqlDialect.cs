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

    IReadOnlyList<SqlHarnessParameter> ParseParameters(IReadOnlyList<string> inputs);

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