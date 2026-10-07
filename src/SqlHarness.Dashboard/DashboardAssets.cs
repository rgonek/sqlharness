using System.Reflection;

using Microsoft.AspNetCore.Http;

namespace SqlHarness.Dashboard;

/// <summary>
/// Embedded single-page app: the Vite build under resource prefix
/// <c>SqlHarness.Dashboard.ui/</c>, or the placeholder page when the assembly
/// was built with SkipDashboardUi. Client routes get index.html; a path that
/// names a file (its last segment has an extension) and does not exist is 404,
/// so a stale script URL can never be answered with HTML.
/// </summary>
internal sealed class DashboardAssets
{
    internal const string UiPrefix = "SqlHarness.Dashboard.ui/";
    internal const string PlaceholderName = "SqlHarness.Dashboard.wwwroot.index.html";

    private static readonly Lazy<DashboardAssets> DefaultInstance = new(() => FromAssembly(typeof(DashboardAssets).Assembly));

    private readonly IReadOnlyDictionary<string, byte[]> _files;
    private readonly byte[] _index;

    internal DashboardAssets(IReadOnlyDictionary<string, byte[]> uiFiles, byte[] placeholder)
    {
        _files = uiFiles;
        HasUi = uiFiles.TryGetValue("index.html", out var index);
        _index = index ?? placeholder;
    }

    internal static DashboardAssets Default => DefaultInstance.Value;

    internal bool HasUi { get; }

    internal IResult Index() => Results.Bytes(_index, "text/html; charset=utf-8");

    internal IResult Resolve(string? path)
    {
        var key = Normalize(path ?? string.Empty);
        if (key.Length > 0 && key != "index.html" && _files.TryGetValue(key, out var bytes))
            return Results.Bytes(bytes, ContentType(key));
        var lastSegment = key[(key.LastIndexOf('/') + 1)..];
        return lastSegment.Contains('.') ? Results.NotFound() : Index();
    }

    internal static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    internal static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".html" => "text/html; charset=utf-8",
        ".json" or ".map" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private static DashboardAssets FromAssembly(Assembly assembly)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.StartsWith(UiPrefix, StringComparison.Ordinal))
                files[Normalize(name[UiPrefix.Length..])] = Read(assembly, name);
        }

        return new DashboardAssets(files, Read(assembly, PlaceholderName));
    }

    private static byte[] Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Dashboard resource '{name}' is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}