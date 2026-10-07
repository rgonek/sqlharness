using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

public sealed record DashboardServerOptions(string DatabasePath, int PreferredPort, IProcessInfo Processes)
{
    public TimeSpan LivePollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Fixed token for tests; production generates a fresh random token per start.</summary>
    internal string? Token { get; init; }
}

public sealed class RunningDashboard : IAsyncDisposable
{
    private readonly WebApplication _app;

    internal RunningDashboard(WebApplication app, int port, string token)
    {
        _app = app;
        Port = port;
        Token = token;
    }

    public int Port { get; }

    public string Token { get; }

    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    public Uri OpenUri => new($"http://127.0.0.1:{Port}/?t={Token}");

    internal IReadOnlyList<(string Route, IReadOnlyList<string> Methods)> Endpoints =>
        _app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => (
                "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/'),
                (IReadOnlyList<string>)(endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).ToArray()))
            .ToArray();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public static partial class DashboardServer
{
    public const int PortAttempts = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<RunningDashboard> StartAsync(DashboardServerOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var token = options.Token ?? DashboardSecurity.NewToken();
        var candidates = options.PreferredPort == 0
            ? [0]
            : Enumerable.Range(options.PreferredPort, PortAttempts).Where(port => port <= 65535).ToArray();
        Exception? last = null;
        foreach (var candidate in candidates)
        {
            var boundPort = 0;
            var app = Build(options, token, candidate, () => boundPort);
            try
            {
                await app.StartAsync(ct);
            }
            catch (Exception exception) when (IsAddressInUse(exception))
            {
                last = exception;
                await app.DisposeAsync();
                continue;
            }

            boundPort = BoundPort(app);
            return new RunningDashboard(app, boundPort, token);
        }

        throw new IOException("No free loopback port for the dashboard.", last);
    }

    private static WebApplication Build(DashboardServerOptions options, string token, int port, Func<int> boundPort)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(DashboardServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        // Explicit Listen overrides ASPNETCORE_URLS/--urls; the dashboard is loopback-only by construction.
        builder.WebHost.UseUrls();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Loopback, port);
        });
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

        var app = builder.Build();
        var reader = new JournalReader(options.DatabasePath, options.Processes);
        app.Use((context, next) => DashboardSecurity.InvokeAsync(context, () => next(context), token, boundPort));

        var api = app.MapGroup("/api");
        api.MapGet("/sessions", (string? agent, string? transport, string? from, string? to, long? cursor, int? limit) =>
            TryWindow(from, to, out var window)
                ? Results.Json(reader.Sessions(new SessionQuery(agent, transport, window.From, window.To, cursor, limit ?? JournalReader.DefaultLimit)), Json)
                : BadRequest("from/to must be ISO-8601 timestamps."));
        api.MapGet("/sessions/{id:long}", (long id) =>
            reader.Session(id) is { } detail ? Results.Json(detail, Json) : Results.NotFound());
        api.MapGet("/operations", (long? session, string? status, string? operation, string? from, string? to, long? cursor, int? limit) =>
            TryWindow(from, to, out var window)
                ? Results.Json(reader.Operations(new OperationQuery(session, status, operation, window.From, window.To, cursor, limit ?? JournalReader.DefaultLimit)), Json)
                : BadRequest("from/to must be ISO-8601 timestamps."));
        api.MapGet("/operations/{id:long}", (long id) =>
            reader.Operation(id) is { } detail ? Results.Json(detail, Json) : Results.NotFound());
        api.MapGet("/plans/{hash}", (string hash, string? view) => Plan(reader, hash, view));
        api.MapGet("/stats", (string? from, string? to) =>
            TryWindow(from, to, out var window)
                ? Results.Json(reader.Stats(new StatsQuery(window.From, window.To)), Json)
                : BadRequest("from/to must be ISO-8601 timestamps."));
        api.MapGet("/live", (HttpContext context) => LiveFeed.StreamAsync(context, reader, options.LivePollInterval, context.RequestAborted));
        api.MapGet("/{**rest}", () => Results.NotFound());

        app.MapGet("/", () => Index());
        app.MapGet("/{**path}", () => Index());
        return app;
    }

    private static IResult Plan(JournalReader reader, string hash, string? view)
    {
        if (!PlanHash().IsMatch(hash))
            return BadRequest("Plan hash must be 64 hexadecimal characters.");
        if (reader.Plan(hash.ToUpperInvariant()) is not { } plan)
            return Results.NotFound();
        if (string.Equals(view, "distilled", StringComparison.Ordinal))
        {
            try
            {
                var distilled = plan.Format == "explain-json"
                    ? Core.Postgres.PostgresPlanDistiller.Distill(plan.Document)
                    : PlanDistiller.Distill(plan.Document);
                return Results.Json(distilled, Json);
            }
            catch (Exception exception) when (exception is SqlHarnessSafetyException or System.Xml.XmlException or JsonException or InvalidOperationException)
            {
                return Results.UnprocessableEntity();
            }
        }

        var (contentType, extension) = plan.Format == "explain-json"
            ? ("application/json", ".explain.json")
            : ("application/xml", ".sqlplan");
        return Results.File(System.Text.Encoding.UTF8.GetBytes(plan.Document), contentType, plan.Hash.ToLowerInvariant() + extension);
    }

    private static IResult Index()
    {
        using var stream = typeof(DashboardServer).Assembly.GetManifestResourceStream("SqlHarness.Dashboard.wwwroot.index.html")
            ?? throw new InvalidOperationException("Dashboard index is missing.");
        using var text = new StreamReader(stream);
        return Results.Content(text.ReadToEnd(), "text/html; charset=utf-8");
    }

    private static IResult BadRequest(string message) => Results.Json(new { error = message }, Json, statusCode: StatusCodes.Status400BadRequest);

    private static bool TryWindow(string? from, string? to, out (DateTimeOffset? From, DateTimeOffset? To) window)
    {
        window = (null, null);
        if (!TryTimestamp(from, out var start) || !TryTimestamp(to, out var end))
            return false;
        window = (start, end);
        return true;
    }

    private static bool TryTimestamp(string? value, out DateTimeOffset? timestamp)
    {
        timestamp = null;
        if (string.IsNullOrEmpty(value))
            return true;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return false;
        timestamp = parsed;
        return true;
    }

    private static int BoundPort(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Uri(address).Port;
    }

    private static bool IsAddressInUse(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AddressInUseException)
                return true;
            if (current is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse or System.Net.Sockets.SocketError.AccessDenied })
                return true;
        }

        return false;
    }

    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlanHash();
}