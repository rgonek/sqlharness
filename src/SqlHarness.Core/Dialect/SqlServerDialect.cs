namespace SqlHarness.Core.Dialect;

internal sealed class SqlServerDialect : ISqlDialect
{
    private static readonly IReadOnlySet<string> NoSessionTemps =
        new HashSet<string>(StringComparer.Ordinal);

    private readonly SqlSafetyClassifier _classifier = new();

    public SqlEngine Engine => SqlEngine.SqlServer;
    public string IdentitySql => SqlExecution.IdentitySql;
    public string PingSql => PingQuery.Sql;

    public SqlSafetyDecision Classify(
        string sql,
        SqlUsage usage,
        string? database,
        bool allowMutation,
        string? confirmDatabase,
        IReadOnlySet<string> sessionTempTables) =>
        // Local #temp is syntactic on SQL Server; sessionTempTables is ignored.
        _classifier.Classify(sql, usage, database, allowMutation, confirmDatabase);

    public IReadOnlySet<string> CollectSessionTempTables(string sql) => NoSessionTemps;
}
