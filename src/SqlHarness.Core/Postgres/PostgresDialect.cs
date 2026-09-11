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
}
