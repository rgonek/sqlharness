using SqlHarness.Core.Postgres;

namespace SqlHarness.Core.Dialect;

internal static class SqlDialects
{
    private static readonly ISqlDialect SqlServer = new SqlServerDialect();
    private static readonly ISqlDialect Postgres = new PostgresDialect();

    internal static ISqlDialect For(SqlEngine engine) => engine switch
    {
        SqlEngine.SqlServer => SqlServer,
        SqlEngine.Postgres => Postgres,
        _ => throw new SqlHarnessSafetyException("Unknown engine."),
    };
}