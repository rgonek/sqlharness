using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;

namespace SqlHarness.Dashboard;

/// <summary>
/// Loopback dashboard protection: exact Host allowlist (DNS rebinding), GET-only except guarded writes and the bearer-authenticated mutation submit route,
/// persistent ?t= token exchanged into a long-lived HttpOnly SameSite=Strict cookie through a same-origin hand-off page, and
/// cookie authentication on every other request. Tokens are compared in constant time.
/// </summary>
internal static class DashboardSecurity
{
    internal const string CookieName = "sqlharness_dashboard";
    internal const string WriteHeader = "X-SqlHarness-Dashboard";
    internal const string ShutdownProcessIdHeader = "X-SqlHarness-Dashboard-Pid";
    internal const string ShutdownStartedAtTicksHeader = "X-SqlHarness-Dashboard-StartedAtTicks";
    internal const string ProfilesPath = "/api/profiles";
    internal const string SettingsPath = "/api/settings";
    internal const string PricesRefreshPath = "/api/prices/refresh";
    internal const string ShutdownPath = "/api/shutdown";
    internal const string TranscriptsClearPath = "/api/transcripts/clear";
    internal const string MutationsPath = "/api/mutations";
    private static readonly System.Text.RegularExpressions.Regex AnnotationPath =
        new(@"^/api/sessions/[0-9]{1,19}/annotation\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex OperationAnnotationPath =
        new(@"^/api/operations/[0-9]{1,19}/annotation\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex AgentSessionAnnotationPath =
        new(@"^/api/agent-sessions/(?:claude|codex|opencode)/[^/]{1,512}/annotation\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    internal static readonly TimeSpan CookieMaxAge = TimeSpan.FromDays(400);
    private const string TokenQuery = "t";

    internal static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Outermost step: any unhandled failure becomes an empty 500 that still carries
    /// the security headers, so no exception detail or stack trace reaches the client.
    /// </summary>
    internal static async Task InvokeAsync(HttpContext context, Func<Task> next, string token, Func<int> port)
    {
        ApplyHeaders(context.Response.Headers);
        try
        {
            await GuardAsync(context, next, token, port);
        }
        catch (Exception) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            context.Response.Clear();
            ApplyHeaders(context.Response.Headers);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }

    private static void ApplyHeaders(IHeaderDictionary headers)
    {
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
        headers.ContentSecurityPolicy =
            "default-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'";
    }

    private static async Task GuardAsync(HttpContext context, Func<Task> next, string token, Func<int> port)
    {
        var host = context.Request.Host.Value;
        var allowedPort = port();
        var local = context.Connection.LocalIpAddress;
        if (local is { IsIPv4MappedToIPv6: true })
            local = local.MapToIPv4();
        if (allowedPort == 0
            || local is null
            || !local.Equals(IPAddress.Loopback)
            || context.Connection.LocalPort != allowedPort
            || !(string.Equals(host, $"127.0.0.1:{allowedPort}", StringComparison.Ordinal)
                 || string.Equals(host, $"localhost:{allowedPort}", StringComparison.OrdinalIgnoreCase)))
        {
            await Reject(context, StatusCodes.Status400BadRequest, "Invalid host.");
            return;
        }

        var isRead = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        var isSettingsWrite = HttpMethods.IsPut(context.Request.Method)
            && string.Equals(context.Request.Path.Value, SettingsPath, StringComparison.Ordinal);
        var isPricesRefresh = HttpMethods.IsPost(context.Request.Method)
            && string.Equals(context.Request.Path.Value, PricesRefreshPath, StringComparison.Ordinal);
        var isShutdown = HttpMethods.IsPost(context.Request.Method)
            && string.Equals(context.Request.Path.Value, ShutdownPath, StringComparison.Ordinal);
        var isTranscriptsClear = HttpMethods.IsPost(context.Request.Method)
            && string.Equals(context.Request.Path.Value, TranscriptsClearPath, StringComparison.Ordinal);
        var isAnnotationWrite = HttpMethods.IsPatch(context.Request.Method)
            && (AnnotationPath.IsMatch(context.Request.Path.Value ?? string.Empty)
                || OperationAnnotationPath.IsMatch(context.Request.Path.Value ?? string.Empty)
                || AgentSessionAnnotationPath.IsMatch(context.Request.Path.Value ?? string.Empty));
        var isMutationSubmit = HttpMethods.IsPost(context.Request.Method)
            && string.Equals(context.Request.Path.Value, MutationsPath, StringComparison.Ordinal);
        var isMutationDecision = HttpMethods.IsPost(context.Request.Method)
            && System.Text.RegularExpressions.Regex.IsMatch(context.Request.Path.Value ?? string.Empty,
                @"^/api/mutations/[a-f0-9]{32}/(?:approve|deny)\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var isProfilesWrite = HttpMethods.IsPut(context.Request.Method)
            && string.Equals(context.Request.Path.Value, ProfilesPath, StringComparison.Ordinal);
        var isWrite = isProfilesWrite || isSettingsWrite || isPricesRefresh || isAnnotationWrite || isTranscriptsClear || isShutdown || isMutationDecision;
        if (!isRead && !isWrite && !isMutationSubmit)
        {
            context.Response.Headers.Allow = "GET, HEAD";
            await Reject(context, StatusCodes.Status405MethodNotAllowed, "Method not allowed.");
            return;
        }

        // The guarded writes: a cross-site form cannot set the custom header, a cross-site fetch with it
        // needs a CORS preflight the dashboard never answers, and a foreign Origin is refused outright.
        if (isWrite && !IsTrustedWrite(context.Request, allowedPort))
        {
            await Reject(context, StatusCodes.Status403Forbidden, "Forbidden.");
            return;
        }

        if (isMutationSubmit)
        {
            if (!context.Request.HasJsonContentType() ||
                !MatchesBearer(context.Request.Headers.Authorization.ToString(), token))
            {
                await Reject(context, StatusCodes.Status401Unauthorized, "Bearer authentication required.");
                return;
            }
            await next();
            return;
        }

        if (context.Request.Query.TryGetValue(TokenQuery, out var supplied))
        {
            if (!Matches(supplied.ToString(), token))
            {
                await Reject(context, StatusCodes.Status401Unauthorized, "Open the dashboard with: sqlharness dashboard");
                return;
            }

            context.Response.Cookies.Append(CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = false,
                Path = "/",
                IsEssential = true,
                MaxAge = CookieMaxAge,
            });
            var query = new QueryBuilder(context.Request.Query
                .Where(pair => !string.Equals(pair.Key, TokenQuery, StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string>(pair.Key, value ?? string.Empty))));
            // Collapse leading slashes and backslashes so "//host/x" cannot become a protocol-relative redirect.
            var path = "/" + (context.Request.PathBase + context.Request.Path).Value?.TrimStart('/', '\\');
            var target = new PathString(path).ToUriComponent() + query.ToQueryString();
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            var encoded = WebUtility.HtmlEncode(target);
            // A same-origin navigation from this page sends the Strict cookie; a redirect would
            // continue the cross-site navigation that started on the file:// launch page.
            await context.Response.WriteAsync($"""
                <!doctype html><html><head><meta charset="utf-8"><meta name="referrer" content="no-referrer">
                <meta http-equiv="refresh" content="0;url={encoded}"><title>SQLHarness dashboard</title></head>
                <body><p><a href="{encoded}">Open the SQLHarness dashboard</a></p></body></html>
                """);
            return;
        }

        if (!context.Request.Cookies.TryGetValue(CookieName, out var cookie) || !Matches(cookie, token))
        {
            await Reject(context, StatusCodes.Status401Unauthorized, "Open the dashboard with: sqlharness dashboard");
            return;
        }

        await next();
    }

    private static bool IsTrustedWrite(HttpRequest request, int port)
    {
        if (request.Query.ContainsKey(TokenQuery))
            return false;
        if (!string.Equals(request.Headers[WriteHeader].ToString(), "1", StringComparison.Ordinal))
            return false;
        if (!request.HasJsonContentType())
            return false;
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0
            || string.Equals(origin, $"http://127.0.0.1:{port}", StringComparison.Ordinal)
            || string.Equals(origin, $"http://localhost:{port}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(string? supplied, string token) =>
        supplied is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(token));

    private static bool MatchesBearer(string supplied, string token) =>
        supplied.StartsWith("Bearer ", StringComparison.Ordinal) && Matches(supplied[7..], token);

    private static Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message);
    }
}