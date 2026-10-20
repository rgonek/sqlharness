using System.Globalization;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

internal sealed record OperationAnnotationPatch(bool? Starred, bool SetNote, string? Note);
internal enum OperationAnnotationWriteStatus { Updated, NotFound, SchemaMismatch, Busy }

internal static class OperationAnnotations
{
    private const int MaximumNoteLength = 10_000;
    private const int MaximumBodyBytes = 64 * 1024;

    internal static async Task<IResult> PatchAsync(HttpContext context, long id, OperationAnnotationWriter writer, JsonSerializerOptions json)
    {
        var body = await SessionAnnotations.ReadBodyAsync(context.Request, context.RequestAborted);
        if (body is null) return SessionAnnotations.Error("The annotation body is larger than 64 KiB.", 400, json);
        JsonDocument document;
        try { document = JsonDocument.Parse(body); }
        catch (JsonException) { return SessionAnnotations.Error("The body must be a JSON object.", 400, json); }
        OperationAnnotationPatch? patch;
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return SessionAnnotations.Error("The body must be a JSON object.", 400, json);
            bool setNote = false;
            bool? starred = null;
            string? note = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name == "starred" && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    starred = property.Value.GetBoolean();
                else if (property.Name == "note" && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null)
                {
                    note = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    if (note is not null && note.Length > MaximumNoteLength)
                        return SessionAnnotations.Error("note must be at most 10000 characters.", 400, json);
                    if (string.IsNullOrWhiteSpace(note)) note = null;
                    setNote = true;
                }
                else return SessionAnnotations.Error($"Invalid annotation field: {property.Name}.", 400, json);
            }
            if (starred is null && !setNote)
                return SessionAnnotations.Error("Nothing to change.", 400, json);
            patch = new OperationAnnotationPatch(starred, setNote, note);
        }
        var (status, annotation) = writer.Apply(id, patch);
        return status switch
        {
            OperationAnnotationWriteStatus.Updated => Results.Json(annotation, json),
            OperationAnnotationWriteStatus.NotFound => SessionAnnotations.Error("The operation no longer exists.", 404, json),
            OperationAnnotationWriteStatus.SchemaMismatch => SessionAnnotations.Error("The activity journal schema does not match this dashboard; restart the dashboard with the current sqlharness.", 409, json),
            _ => SessionAnnotations.Error("The journal is busy; try again.", 503, json),
        };
    }
}

internal sealed class OperationAnnotationWriter(string databasePath, TimeProvider time)
{
    internal (OperationAnnotationWriteStatus Status, OperationAnnotation? Annotation) Apply(long operationId, OperationAnnotationPatch patch)
    {
        if (!File.Exists(databasePath)) return (OperationAnnotationWriteStatus.NotFound, null);
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString());
            connection.Open();
            using (var pragma = connection.CreateCommand()) { pragma.CommandText = "PRAGMA busy_timeout=250; PRAGMA foreign_keys=ON; BEGIN IMMEDIATE;"; pragma.ExecuteNonQuery(); }
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version;";
                if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != JournalSchema.CurrentVersion)
                {
                    using var rollback = connection.CreateCommand(); rollback.CommandText = "ROLLBACK;"; rollback.ExecuteNonQuery();
                    return (OperationAnnotationWriteStatus.SchemaMismatch, null);
                }
                command.CommandText = "SELECT 1 FROM operations WHERE id=$id;"; command.Parameters.AddWithValue("$id", operationId);
                if (command.ExecuteScalar() is null)
                {
                    using var rollback = connection.CreateCommand(); rollback.CommandText = "ROLLBACK;"; rollback.ExecuteNonQuery();
                    return (OperationAnnotationWriteStatus.NotFound, null);
                }
                var now = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO operation_annotations(operation_id, owner_starred, owner_note, updated_at) VALUES($id, COALESCE($starred,0), $note, $now) ON CONFLICT(operation_id) DO UPDATE SET owner_starred=COALESCE($starred,owner_starred), owner_note=CASE WHEN $setNote THEN $note ELSE owner_note END, updated_at=$now;";
                command.Parameters.AddWithValue("$id", operationId); command.Parameters.AddWithValue("$starred", patch.Starred is null ? DBNull.Value : patch.Starred.Value ? 1 : 0);
                command.Parameters.AddWithValue("$setNote", patch.SetNote ? 1 : 0); command.Parameters.AddWithValue("$note", patch.Note is null ? DBNull.Value : patch.Note);
                command.Parameters.AddWithValue("$now", now); command.ExecuteNonQuery();
                command.Parameters.Clear(); command.CommandText = "SELECT agent_label, COALESCE(agent_keep,0), owner_starred, owner_note FROM operation_annotations WHERE operation_id=$id;";
                command.Parameters.AddWithValue("$id", operationId);
                using var reader = command.ExecuteReader(); reader.Read();
                var annotation = new OperationAnnotation(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1) != 0, reader.GetInt64(2) != 0, reader.IsDBNull(3) ? null : reader.GetString(3));
                reader.Close();
                using var commit = connection.CreateCommand(); commit.CommandText = "COMMIT;"; commit.ExecuteNonQuery();
                return (OperationAnnotationWriteStatus.Updated, annotation);
            }
            catch { using var rollback = connection.CreateCommand(); rollback.CommandText = "ROLLBACK;"; rollback.ExecuteNonQuery(); throw; }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) { return (OperationAnnotationWriteStatus.Busy, null); }
    }
}
