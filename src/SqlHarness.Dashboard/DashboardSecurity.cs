using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;

namespace SqlHarness.Dashboard;

/// <summary>
/// Loopback dashboard protection: exact Host allowlist (DNS rebinding), GET-only,
/// one-time ?t= token exchange into an HttpOnly SameSite=Strict cookie, and
/// cookie authentication on every other request. Tokens are compared in constant time.
/// </summary>
internal static class DashboardSecurity
{
    internal const string CookieName = "sqlharness_dashboard";
    private const string TokenQuery = "t";

    internal static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static async Task InvokeAsync(HttpContext context, Func<Task> next, string token, Func<int> port)
    {
        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
        headers.ContentSecurityPolicy =
            "default-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'";

        var host = context.Request.Host.Value;
        var allowedPort = port();
        if (allowedPort == 0
            || !(string.Equals(host, $"127.0.0.1:{allowedPort}", StringComparison.Ordinal)
                 || string.Equals(host, $"localhost:{allowedPort}", StringComparison.OrdinalIgnoreCase)))
        {
            await Reject(context, StatusCodes.Status400BadRequest, "Invalid host.");
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.Headers.Allow = "GET, HEAD";
            await Reject(context, StatusCodes.Status405MethodNotAllowed, "Method not allowed.");
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
            });
            var query = new QueryBuilder(context.Request.Query
                .Where(pair => pair.Key != TokenQuery)
                .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string>(pair.Key, value ?? string.Empty))));
            context.Response.Redirect(context.Request.PathBase + context.Request.Path + query.ToQueryString());
            return;
        }

        if (!context.Request.Cookies.TryGetValue(CookieName, out var cookie) || !Matches(cookie, token))
        {
            await Reject(context, StatusCodes.Status401Unauthorized, "Open the dashboard with: sqlharness dashboard");
            return;
        }

        await next();
    }

    private static bool Matches(string? supplied, string token) =>
        supplied is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(token));

    private static Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message);
    }
}