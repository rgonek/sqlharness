using SqlHarness.Core.Dialect;

namespace SqlHarness.Core.Postgres;

internal sealed class PostgresDialect : ISqlDialect
{
    private readonly PostgresSafetyClassifier _classifier = new();

    public SqlEngine Engine => SqlEngine.Postgres;
    public string PingSql => PostgresPing.Sql;

    public SqlSafetyDecision Classify(
        string sql,
        SqlUsage usage,
        string? database,
        bool allowMutation,
        string? confirmDatabase,
        IReadOnlySet<string> sessionTempTables) =>
        _classifier.Classify(sql, usage, database, allowMutation, confirmDatabase, sessionTempTables);

    public IReadOnlyList<SqlHarnessParameter> ParseParameters(IReadOnlyList<string> inputs)
    {
        var parsed = SqlParameterParser.Parse(inputs);
        PostgresParameters.Validate(parsed);
        return parsed;
    }

    public void ValidateParameterReferences(IReadOnlyList<SqlHarnessParameter> parameters, params string?[] batches) =>
        PostgresParameterReferenceValidator.Validate(parameters, batches);

    public void ValidateMeasuredBatch(string sql) => PostgresBenchmark.ValidateMeasuredBatch(sql);

    public DistilledPlan DistillPlan(string document) => PostgresPlanDistiller.Distill(document);

    public string CountsCatalogSql => PostgresCounts.CatalogSql;

    public string BuildCountsExactSql(IReadOnlyList<ResolvedCountObject> objects) =>
        PostgresCounts.BuildExactSql(objects);

    public string SchemaSql => PostgresSchema.Sql;

    public string SpaceSql => PostgresSpace.Sql;

    public Task<CollectedCompareRun> ExecuteBenchmarkRunAsync(
        ISqlSession session,
        string sql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds,
        int repetition,
        string variant,
        CanonicalResultAccumulator raw,
        bool captureComparison,
        int comparisonMaximumRows,
        CancellationToken ct) =>
        PostgresBenchmark.ExecuteBenchmarkRunAsync(
            session, sql, parameters, timeoutSeconds, repetition, variant, raw, captureComparison, comparisonMaximumRows, ct);
}