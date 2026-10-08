using Microsoft.AspNetCore.Http;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

public sealed record SettingsResponse(string Status, string Path, SqlHarnessConfig Settings);

/// <summary>
/// Reads and replaces operator settings in config.json. The file is validated with the
/// loader's strict rules, dashboard.port is always kept from the current file, and an
/// invalid file is replaced only on explicit request. Raw file text never leaves the server.
/// </summary>
internal static class DashboardSettings
{
    internal static SettingsResponse Read(string path)
    {
        var loaded = SqlHarnessConfigLoader.Load(path);
        return new SettingsResponse(loaded.Status.ToString().ToLowerInvariant(), path, loaded.Config);
    }

    internal static async Task<IResult> PutAsync(HttpContext context, string path, System.Text.Json.JsonSerializerOptions json)
    {
        var body = await ReadBodyAsync(context.Request, context.RequestAborted);
        if (body is null)
            return Results.Json(new { errors = new[] { new SqlHarnessConfigFieldError("$", "The settings document is larger than 64 KiB.") } }, json, statusCode: StatusCodes.Status400BadRequest);
        var parsed = SqlHarnessConfigLoader.Parse(body);
        if (parsed.Config is not { } requested)
            return Results.Json(new { errors = parsed.Errors }, json, statusCode: StatusCodes.Status400BadRequest);

        var current = SqlHarnessConfigLoader.Load(path);
        var overwrite = string.Equals(context.Request.Query["overwriteInvalid"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        if (current.Status == SqlHarnessConfigStatus.Invalid && !overwrite)
            return Results.Json(new { error = "config.json is invalid; confirm replacing it." }, json, statusCode: StatusCodes.Status409Conflict);

        // The running dashboard owns its port; changing it from the page would cut the page off.
        var config = requested with { Dashboard = requested.Dashboard with { Port = current.Config.Dashboard.Port } };
        try
        {
            SqlHarnessConfigWriter.Write(path, config);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Results.Json(new { error = "Could not write config.json." }, json, statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Json(Read(path), json);
    }

    /// <summary>The request body, or null when it exceeds the config size limit.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > SqlHarnessConfigLoader.MaximumBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
