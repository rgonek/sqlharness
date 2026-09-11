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
}
