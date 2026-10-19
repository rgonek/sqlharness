using Microsoft.Data.Sqlite;

namespace SqlHarness.Dashboard;

/// <summary>Reads SQLite's per-connection commit signal without changing the connection baseline.</summary>
internal static class JournalDataVersion
{
    internal static long Read(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version;";
        return (long)command.ExecuteScalar()!;
    }
}