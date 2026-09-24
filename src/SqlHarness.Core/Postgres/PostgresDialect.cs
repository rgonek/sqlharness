using SqlHarness.Core.Dialect;

namespace SqlHarness.Core.Postgres;

internal sealed class PostgresDialect : ISqlDialect
{
    private static readonly IReadOnlySet<string> NoSessionTemps =
        new HashSet<string>(StringComparer.Ordinal);

    private readonly PostgresSafetyClassifier _classifier = new();

    public SqlEngine Engine => SqlEngine.Postgres;
    public string IdentitySql => PostgresPing.IdentitySql;
    public string PingSql => PostgresPing.Sql;

    public SqlSafetyDecision Classify(
        string sql,
        SqlUsage usage,
        string? database,
        bool allowMutation,
        string? confirmDatabase,
        IReadOnlySet<string> sessionTempTables) =>
        _classifier.Classify(sql, usage, database, allowMutation, confirmDatabase, sessionTempTables);

    public IReadOnlySet<string> CollectSessionTempTables(string sql)
    {
        var decision = _classifier.Classify(
            sql,
            SqlUsage.CompareSetup,
            database: null,
            allowMutation: false,
            confirmDatabase: null,
            NoSessionTemps);
        return decision.Allowed ? decision.SessionTempTables : NoSessionTemps;
    }

    public IReadOnlyList<SqlHarnessParameter> ParseParameters(IReadOnlyList<string> inputs)
    {
        var parsed = SqlParameterParser.Parse(inputs);
        PostgresParameters.Validate(parsed);
        return parsed;
    }

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