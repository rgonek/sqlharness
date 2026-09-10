using SqlHarness.Core.Dialect;

namespace SqlHarness.Core.Postgres;

internal sealed class PostgresDialect : ISqlDialect
{
    public SqlEngine Engine => SqlEngine.Postgres;
    public string IdentitySql => PostgresPing.IdentitySql;
    public string PingSql => PostgresPing.Sql;
}
