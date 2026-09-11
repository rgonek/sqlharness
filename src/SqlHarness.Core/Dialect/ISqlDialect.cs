namespace SqlHarness.Core.Dialect;

internal interface ISqlDialect
{
    SqlEngine Engine { get; }
    string IdentitySql { get; }
    string PingSql { get; }

    SqlSafetyDecision Classify(
        string sql,
        SqlUsage usage,
        string? database,
        bool allowMutation,
        string? confirmDatabase,
        IReadOnlySet<string> sessionTempTables);

    IReadOnlySet<string> CollectSessionTempTables(string sql);

    IReadOnlyList<SqlHarnessParameter> ParseParameters(IReadOnlyList<string> inputs);

    void ValidateMeasuredBatch(string sql);

    DistilledPlan DistillPlan(string document);

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
}
