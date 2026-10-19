using System.Globalization;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

internal sealed class AgentSessionAnnotationWriter(string databasePath, TimeProvider time)
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    internal (AnnotationWriteStatus Status, SessionAnnotation? Annotation) Apply(AgentSessionKey key, AnnotationPatch patch)
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
            Execute(connection, "PRAGMA busy_timeout = 250; PRAGMA foreign_keys = ON; BEGIN IMMEDIATE;");
            try
            {
                if ((long)Scalar(connection, "PRAGMA user_version;")! != JournalSchema.CurrentVersion)
                {
                    Execute(connection, "ROLLBACK;");
                    return (AnnotationWriteStatus.SchemaMismatch, null);
                }
                var exists = Scalar(connection, """
                    SELECT 1 WHERE EXISTS (SELECT 1 FROM agent_conversations WHERE agent_kind = $kind AND conversation_id = $id)
                       OR EXISTS (SELECT 1 FROM operations o JOIN sessions s ON s.id = o.session_id
                                  WHERE s.agent_kind = $kind AND o.agent_session_id = $id)
                       OR EXISTS (SELECT 1 FROM agent_session_annotations WHERE agent_kind = $kind AND conversation_id = $id);
                    """, ("$kind", key.AgentKind), ("$id", key.ConversationId));
                if (exists is null)
                {
                    Execute(connection, "ROLLBACK;");
                    return (AnnotationWriteStatus.NotFound, null);
                }
                var now = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                Execute(connection, """
                    INSERT INTO agent_session_annotations (agent_kind, conversation_id, updated_at)
                    VALUES ($kind, $id, $now) ON CONFLICT(agent_kind, conversation_id) DO NOTHING;
                    UPDATE agent_session_annotations SET
                        title = CASE WHEN $setTitle THEN $title ELSE title END,
                        note = CASE WHEN $setNote THEN $note ELSE note END,
                        starred = COALESCE($starred, starred),
                        archived = COALESCE($archived, archived),
                        updated_at = $now
                    WHERE agent_kind = $kind AND conversation_id = $id;
                    DELETE FROM agent_session_annotations
                    WHERE agent_kind = $kind AND conversation_id = $id
                      AND title IS NULL AND note IS NULL AND starred = 0 AND archived = 0;
                    """, ("$kind", key.AgentKind), ("$id", key.ConversationId), ("$now", now),
                    ("$setTitle", patch.SetTitle ? 1 : 0), ("$title", patch.Title),
                    ("$setNote", patch.SetNote ? 1 : 0), ("$note", patch.Note),
                    ("$starred", patch.Starred is null ? null : patch.Starred.Value ? 1 : 0),
                    ("$archived", patch.Archived is null ? null : patch.Archived.Value ? 1 : 0));
                using var read = connection.CreateCommand();
                read.CommandText = "SELECT title, note, starred, archived FROM agent_session_annotations WHERE agent_kind = $kind AND conversation_id = $id;";
                read.Parameters.AddWithValue("$kind", key.AgentKind);
                read.Parameters.AddWithValue("$id", key.ConversationId);
                using var reader = read.ExecuteReader();
                var annotation = reader.Read()
                    ? new SessionAnnotation(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.GetInt64(2) != 0, reader.GetInt64(3) != 0)
                    : new SessionAnnotation(null, null, false, false);
                Execute(connection, "COMMIT;");
                return (AnnotationWriteStatus.Updated, annotation);
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