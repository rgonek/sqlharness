using System.Net;
using System.Text.Json;

namespace SqlHarness.Dashboard;

/// <summary>
/// The page the browser is launched with: an owner-only local file in the SQLHarness home
/// that redirects to the token URL. Launching the browser with the token URL itself would
/// expose the token in the process list (<c>ps</c>, <c>/proc/*/cmdline</c>) to other local
/// users; a file only its owner can read keeps it private (the approach Jupyter uses).
/// The page is a file:// document, so the server's Content-Security-Policy does not apply
/// to it; the server's own responses keep theirs.
/// </summary>
internal static class DashboardOpenPage
{
    internal const string FileName = "dashboard-open.html";

    internal static string PathIn(string home) => Path.Combine(home, FileName);

    /// <summary>Replaces the page with a redirect to <paramref name="target"/> and returns its file URI.</summary>
    internal static Uri Write(string home, Uri target)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        ArgumentNullException.ThrowIfNull(target);
        var path = Path.GetFullPath(PathIn(home));
        var html = WebUtility.HtmlEncode(target.ToString());
        var script = JsonSerializer.Serialize(target.ToString());
        DashboardLock.WriteOwnerOnly(path, $"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta name="referrer" content="no-referrer">
            <meta http-equiv="refresh" content="0;url={html}">
            <title>SQLHarness dashboard</title>
            </head>
            <body>
            <script>location.replace({script});</script>
            <p><a href="{html}">Open the SQLHarness dashboard</a></p>
            </body>
            </html>

            """);
        return new Uri(path);
    }

    internal static void Delete(string home)
    {
        try
        {
            File.Delete(PathIn(home));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}