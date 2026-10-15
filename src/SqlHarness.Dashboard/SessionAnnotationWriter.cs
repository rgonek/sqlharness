using System.Globalization;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

internal sealed record AnnotationPatch(bool SetTitle, string? Title, bool SetNote, string? Note, bool? Starred, bool? Archived);

internal enum AnnotationWriteStatus { Updated, NotFound, SchemaMismatch, Busy }

/// <summary>
/// The dashboard's only journal write: one operator annotation per session, in one short
/// IMMEDIATE transaction. Refuses when the journal schema is not the one this binary knows;
/// the dashboard never migrates. An annotation with nothing left in it is deleted.
/// </summary>
internal sealed class SessionAnnotationWriter(string databasePath, TimeProvider time)
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    internal (AnnotationWriteStatus Status, SessionAnnotation? Annotation) Apply(long sessionId, AnnotationPatch patch)
    {
        if (!File.Exists(databasePath))
            return (AnnotationWriteStatus.NotFound, null);
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
            connection.Open();
            Execute(connection, "PRAGMA busy_timeout = 250; PRAGMA foreign_keys = ON;");
            Execute(connection, "BEGIN IMMEDIATE;");
            try
            {
                var result = ApplyLocked(connection, sessionId, patch);
                Execute(connection, result.Status == AnnotationWriteStatus.Updated ? "COMMIT;" : "ROLLBACK;");
                return result;
            }
            catch
            {
                Execute(connection, "ROLLBACK;");
                throw;
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is SqliteBusy or SqliteLocked)
        {
            return (AnnotationWriteStatus.Busy, null);
        }
    }

    private (AnnotationWriteStatus Status, SessionAnnotation? Annotation) ApplyLocked(SqliteConnection connection, long sessionId, AnnotationPatch patch)
    {
        if ((long)Scalar(connection, "PRAGMA user_version;")! != JournalSchema.CurrentVersion)
            return (AnnotationWriteStatus.SchemaMismatch, null);
        if (Scalar(connection, "SELECT 1 FROM sessions WHERE id = $id;", ("$id", sessionId)) is null)
            return (AnnotationWriteStatus.NotFound, null);

        var now = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        Execute(connection, """
            INSERT INTO session_annotations (session_id, updated_at) VALUES ($id, $now) ON CONFLICT(session_id) DO NOTHING;
            UPDATE session_annotations SET
                title = CASE WHEN $setTitle THEN $title ELSE title END,
                note = CASE WHEN $setNote THEN $note ELSE note END,
                starred = COALESCE($starred, starred),
                archived_at = CASE WHEN $archived IS NULL THEN archived_at WHEN $archived = 1 THEN COALESCE(archived_at, $now) ELSE NULL END,
                updated_at = $now
            WHERE session_id = $id;
            DELETE FROM session_annotations
            WHERE session_id = $id AND title IS NULL AND note IS NULL AND starred = 0 AND archived_at IS NULL;
            """,
            ("$id", sessionId), ("$now", now),
            ("$setTitle", patch.SetTitle ? 1 : 0), ("$title", patch.Title),
            ("$setNote", patch.SetNote ? 1 : 0), ("$note", patch.Note),
            ("$starred", patch.Starred is null ? null : patch.Starred.Value ? 1 : 0),
            ("$archived", patch.Archived is null ? null : patch.Archived.Value ? 1 : 0));

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT title, note, starred, archived_at IS NOT NULL FROM session_annotations WHERE session_id = $id;";
        read.Parameters.AddWithValue("$id", sessionId);
        using var reader = read.ExecuteReader();
        var annotation = reader.Read()
            ? new SessionAnnotation(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2) != 0, reader.GetInt64(3) != 0)
            : new SessionAnnotation(null, null, false, false);
        return (AnnotationWriteStatus.Updated, annotation);
    }

    private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
