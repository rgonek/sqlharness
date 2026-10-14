namespace SqlHarness.Core.Postgres;

internal static class PostgresPing
{
    internal const string IdentitySql = """
SELECT current_database() AS DatabaseName,
       COALESCE(inet_server_addr()::text, 'localhost') AS ServerName;
""";
}