using Microsoft.AspNetCore.Http;

namespace SqlHarness.Dashboard;

/// <summary>Server-sent events feed. Placeholder until the live feed is implemented.</summary>
internal static class LiveFeed
{
    internal static Task StreamAsync(HttpContext context, JournalReader reader, TimeSpan interval, CancellationToken ct)
    {
        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
        return Task.CompletedTask;
    }
}