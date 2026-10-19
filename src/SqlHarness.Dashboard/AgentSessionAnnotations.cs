using System.Text.Json;

using Microsoft.AspNetCore.Http;

namespace SqlHarness.Dashboard;

internal static class AgentSessionAnnotations
{
    internal static async Task<IResult> PatchAsync(HttpContext context, string kind, string id,
        AgentSessionAnnotationWriter writer, JsonSerializerOptions json)
    {
        if (kind is not ("claude" or "codex" or "opencode") || string.IsNullOrWhiteSpace(id) || id.Length > 512)
            return SessionAnnotations.Error("The agent session was not found.", StatusCodes.Status404NotFound, json);
        var body = await SessionAnnotations.ReadBodyAsync(context.Request, context.RequestAborted);
        if (body is null)
            return SessionAnnotations.Error("The annotation body is larger than 64 KiB.", StatusCodes.Status400BadRequest, json);
        var (patch, error) = SessionAnnotations.Parse(body);
        if (patch is null)
            return SessionAnnotations.Error(error!, StatusCodes.Status400BadRequest, json);
        var (status, annotation) = writer.Apply(new AgentSessionKey(kind, id), patch);
        return status switch
        {
            AnnotationWriteStatus.Updated => Results.Json(annotation, json),
            AnnotationWriteStatus.NotFound => SessionAnnotations.Error("The agent session no longer exists.", StatusCodes.Status404NotFound, json),
            AnnotationWriteStatus.SchemaMismatch => SessionAnnotations.Error(
                "The activity journal schema does not match this dashboard; restart the dashboard with the current sqlharness.",
                StatusCodes.Status409Conflict, json),
            _ => SessionAnnotations.Error("The journal is busy; try again.", StatusCodes.Status503ServiceUnavailable, json),
        };
    }
}