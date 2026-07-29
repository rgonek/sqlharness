using System.Globalization;

namespace SqlHarness.Core;

internal static class PingQuery
{
    internal const string Sql = """
SELECT CAST(1 AS int) AS Ok,
       CONVERT(nvarchar(128), DB_NAME()) AS Db,
       CONVERT(nvarchar(128), @@SERVERNAME) AS Server,
       CONVERT(nvarchar(128), SUSER_SNAME()) AS Login;
""";

    internal static async Task<(string Database, string Server, string Login)> ReadAsync(
        ISqlReader reader,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.FieldCount != 4)
            throw new InvalidOperationException("Ping probe returned unexpected column count.");
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Ping probe returned no row.");

        var ok = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
        if (ok != 1)
            throw new InvalidOperationException("Ping probe returned unexpected status.");

        var database = Text(reader.GetValue(1));
        var server = Text(reader.GetValue(2));
        var login = Text(reader.GetValue(3));

        if (await reader.ReadAsync(ct))
            throw new InvalidOperationException("Ping probe returned extra rows.");

        while (await reader.NextResultAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
            }
        }

        return (database, server, login);
    }

    private static string Text(object? value) =>
        value is null or DBNull
            ? string.Empty
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}