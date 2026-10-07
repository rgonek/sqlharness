using Microsoft.Data.Sqlite;

using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

internal sealed class JournalTempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "sqlharness-journal-" + Guid.NewGuid().ToString("N"));

    public JournalTempDirectory() => Directory.CreateDirectory(Path);

    public string DatabasePath => System.IO.Path.Combine(Path, "data", "activity.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(Path, true);
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class JournalTestData
{
    internal static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    internal static SessionIdentity Session(string key = "cli:test") => new(
        key, "claude", "process-tree", JournalTransport.Cli, null, null, null,
        4242, T0.AddHours(-1), 5151, T0, "/work");

    internal static OperationStart Start(string sql = "SELECT 1") => new(
        "query", "local", new Dictionary<string, string> { ["tenant"] = "acme" }, false,
        "sha256:abc", null, sql, null);

    internal static OperationEnd End(string status = "succeeded", int exitCode = 0) => new(
        status, exitCode, null, 12, "sqlserver", "srv", "db", 1, 3);
}

internal static class JournalDb
{
    internal static List<Dictionary<string, object?>> Rows(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Raw bytes of the database and its WAL/SHM side files, for leak scans.</summary>
    internal static byte[] AllBytes(string path)
    {
        using var buffer = new MemoryStream();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            if (!File.Exists(file))
                continue;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.CopyTo(buffer);
        }

        return buffer.ToArray();
    }

    internal static bool Contains(byte[] haystack, string marker) =>
        haystack.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(marker)) >= 0;
}