namespace SqlHarness.Core;

public enum SqlEngine
{
    SqlServer = 0,
    Postgres = 1,
}

internal static class SqlEngineNames
{
    internal const string SqlServer = "sqlserver";
    internal const string Postgres = "postgres";

    internal static SqlEngine Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, SqlServer, StringComparison.OrdinalIgnoreCase))
            return SqlEngine.SqlServer;
        if (string.Equals(value, Postgres, StringComparison.OrdinalIgnoreCase))
            return SqlEngine.Postgres;
        throw new SqlHarnessSafetyException("Unknown engine.");
    }

    internal static string Format(SqlEngine engine) => engine switch
    {
        SqlEngine.SqlServer => SqlServer,
        SqlEngine.Postgres => Postgres,
        _ => throw new SqlHarnessSafetyException("Unknown engine."),
    };
}
