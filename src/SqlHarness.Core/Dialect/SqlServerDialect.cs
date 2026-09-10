namespace SqlHarness.Core.Dialect;

internal sealed class SqlServerDialect : ISqlDialect
{
    public SqlEngine Engine => SqlEngine.SqlServer;
    public string IdentitySql => SqlExecution.IdentitySql;
    public string PingSql => PingQuery.Sql;
}
