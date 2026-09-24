namespace SqlHarness.Core.Postgres;

internal static class PostgresPing
{
    internal const string Sql = """
SELECT CAST(1 AS int) AS ok,
       current_database() AS db,
       COALESCE(inet_server_addr()::text, 'localhost') AS server,
       current_user AS login;
""";

    internal const string IdentitySql = """
SELECT current_database() AS DatabaseName,
       COALESCE(inet_server_addr()::text, 'localhost') AS ServerName;
""";
}