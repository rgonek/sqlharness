using System.Text.Json;

using Microsoft.AspNetCore.Http;

namespace SqlHarness.Dashboard;

/// <summary>PATCH /api/sessions/{id}/annotation: a partial, strictly validated operator annotation.</summary>
internal static class SessionAnnotations
{
    internal const int MaximumTitleLength = 200;
    internal const int MaximumNoteLength = 10_000;
    internal const int MaximumBodyBytes = 64 * 1024;

    internal static async Task<IResult> PatchAsync(HttpContext context, long id, SessionAnnotationWriter writer, JsonSerializerOptions json)
    {
        var body = await ReadBodyAsync(context.Request, context.RequestAborted);
        if (body is null)
            return Error("The annotation body is larger than 64 KiB.", StatusCodes.Status400BadRequest, json);
        var (patch, error) = Parse(body);
        if (patch is null)
            return Error(error!, StatusCodes.Status400BadRequest, json);

        var (status, annotation) = writer.Apply(id, patch);
        return status switch
        {
            AnnotationWriteStatus.Updated => Results.Json(annotation, json),
            AnnotationWriteStatus.NotFound => Error("The session no longer exists.", StatusCodes.Status404NotFound, json),
            AnnotationWriteStatus.SchemaMismatch => Error(
                "The activity journal schema does not match this dashboard; restart the dashboard with the current sqlharness.",
                StatusCodes.Status409Conflict, json),
            _ => Error("The journal is busy; try again.", StatusCodes.Status503ServiceUnavailable, json),
        };
    }

    internal static (AnnotationPatch? Patch, string? Error) Parse(byte[] body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return (null, "The body must be a JSON object.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, "The body must be a JSON object.");
            bool setTitle = false, setNote = false;
            string? title = null, note = null;
            bool? starred = null, archived = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "title":
                        if (!TryText(property.Value, out title))
                            return (null, "title must be a string or null.");
                        title = title?.Trim();
                        if (string.IsNullOrEmpty(title))
                            title = null;
                        else if (title.Length > MaximumTitleLength)
                            return (null, "title must be at most 200 characters.");
                        else if (title.Contains('\n') || title.Contains('\r'))
                            return (null, "title must be one line.");
                        setTitle = true;
                        break;
                    case "note":
                        if (!TryText(property.Value, out note))
                            return (null, "note must be a string or null.");
                        if (string.IsNullOrWhiteSpace(note))
                            note = null;
                        else if (note.Length > MaximumNoteLength)
                            return (null, "note must be at most 10000 characters.");
                        setNote = true;
                        break;
                    case "starred":
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return (null, "starred must be a boolean.");
                        starred = property.Value.GetBoolean();
                        break;
                    case "archived":
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return (null, "archived must be a boolean.");
                        archived = property.Value.GetBoolean();
                        break;
                    default:
                        return (null, $"Unknown field: {property.Name}.");
                }
            }

            if (!setTitle && !setNote && starred is null && archived is null)
                return (null, "Nothing to change.");
            return (new AnnotationPatch(setTitle, title, setNote, note, starred, archived), null);
        }
    }

    private static bool TryText(JsonElement value, out string? text)
    {
        text = null;
        if (value.ValueKind is not JsonValueKind.String)
            return value.ValueKind is JsonValueKind.Null;
        try
        {
            text = value.GetString();
            return true;
        }
        catch (InvalidOperationException)
        {
            // An escaped lone surrogate is valid JSON but not a valid string.
            return false;
        }
    }

    private static IResult Error(string message, int status, JsonSerializerOptions json) =>
        Results.Json(new { error = message }, json, statusCode: status);

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaximumBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
