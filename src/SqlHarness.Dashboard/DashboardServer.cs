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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

public sealed record DashboardServerOptions(string DatabasePath, int PreferredPort, IProcessInfo Processes)
{
    public TimeSpan LivePollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Records requests and open live streams; a new instance on the system clock when null.</summary>
    internal DashboardActivity? Activity { get; init; }

    /// <summary>The operator settings file the settings page reads and writes.</summary>
    public string ConfigPath { get; init; } = SqlHarnessPaths.ConfigFile;

    /// <summary>The closed profile file shown read-only on the profiles page.</summary>
    public string TargetsPath { get; init; } = SqlHarnessPaths.TargetsFile;
}

public sealed class RunningDashboard : IAsyncDisposable
{
    private readonly WebApplication _app;

    internal RunningDashboard(WebApplication app, int port, string token, DashboardActivity activity)
    {
        _app = app;
        Port = port;
        Token = token;
        Activity = activity;
    }

    public int Port { get; }

    public string Token { get; }

    internal DashboardActivity Activity { get; }

    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    public Uri OpenUri => new($"http://127.0.0.1:{Port}/?t={Token}");

    /// <summary>Signalled when the server is asked to stop, including by SIGTERM/SIGQUIT through the host lifetime.</summary>
    public CancellationToken Stopping => _app.Lifetime.ApplicationStopping;

    /// <summary>Asks the server to stop, the way a host shutdown signal does.</summary>
    internal void RequestStop() => _app.Lifetime.StopApplication();

    internal IReadOnlyList<string> Addresses =>
        _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();

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

    private static readonly string[] ReadMethods = [HttpMethods.Get, HttpMethods.Head];

    public static async Task<RunningDashboard> StartAsync(DashboardServerOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PreferredPort, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.PreferredPort, IPEndPoint.MaxPort, nameof(options));
        var token = DashboardSecurity.NewToken();
        // The preferred port and the next ones, then an ephemeral loopback port as the last resort;
        // the caller publishes whatever port was actually bound.
        int[] candidates = options.PreferredPort == 0
            ? [0]
            : [.. Enumerable.Range(options.PreferredPort, PortAttempts).Where(port => port <= IPEndPoint.MaxPort), 0];
        var activity = options.Activity ?? new DashboardActivity(TimeProvider.System);
        Exception? last = null;
        foreach (var candidate in candidates)
        {
            var boundPort = 0;
            var app = Build(options, activity, token, candidate, () => boundPort);
            try
            {
                await app.StartAsync(ct);
                boundPort = BoundPort(app);
            }
            catch (Exception exception) when (IsAddressInUse(exception))
            {
                last = exception;
                await app.DisposeAsync();
                continue;
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }

            return new RunningDashboard(app, boundPort, token, activity);
        }

        throw new IOException("No free loopback port for the dashboard.", last);
    }

    private static WebApplication Build(DashboardServerOptions options, DashboardActivity activity, string token, int port, Func<int> boundPort)
    {
        // The empty builder reads no command line, environment variables, or appsettings files,
        // so nothing outside this method can add a Kestrel endpoint, change the environment,
        // or enable the developer exception page. The dashboard is loopback-only by construction.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(DashboardServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Loopback, port);
        });
        builder.Services.AddRoutingCore();
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

        var app = builder.Build();
        var reader = new JournalReader(options.DatabasePath, options.Processes);
        app.Use((context, next) => DashboardSecurity.InvokeAsync(context, () => next(context), token, boundPort));
        // Only authenticated requests reach here, so probes from other local processes do not keep the dashboard alive.
        app.Use((context, next) =>
        {
            activity.Touch();
            return next(context);
        });
        app.UseRouting();

        var api = app.MapGroup("/api");
        MapRead(api, "/sessions", (string? agent, string? transport, string? from, string? to, long? cursor, int? limit) =>
            TryWindow(from, to, out var window)
                ? Results.Json(reader.Sessions(new SessionQuery(agent, transport, window.From, window.To, cursor, limit ?? JournalReader.DefaultLimit)), Json)
                : BadRequest("from/to must be ISO-8601 timestamps."));
        MapRead(api, "/sessions/{id:long}", (long id) =>
            reader.Session(id) is { } detail ? Results.Json(detail, Json) : Results.NotFound());
        MapRead(api, "/operations", (long? session, string? status, string? operation, string? from, string? to, long? cursor, int? limit,
            string? profile, string? dimensions) =>
            !TryWindow(from, to, out var window)
                ? BadRequest("from/to must be ISO-8601 timestamps.")
                : !TryDimensions(dimensions, out var dimensionFilters)
                    ? BadRequest("dimensions must be a JSON object with string or null values.")
                    : Results.Json(reader.Operations(new OperationQuery(session, status, operation, window.From, window.To, cursor,
                        limit ?? JournalReader.DefaultLimit, profile, dimensionFilters)), Json));
        MapRead(api, "/operations/{id:long}", (long id) =>
            reader.Operation(id, DashboardProfiles.DimensionNames(options.TargetsPath)) is { } detail
                ? Results.Json(detail, Json)
                : Results.NotFound());
        MapRead(api, "/plans/{hash}", (string hash, string? view) => Plan(reader, hash, view));
        MapRead(api, "/stats", (string? from, string? to, string? profile, string? dimensions) =>
            !TryWindow(from, to, out var window)
                ? BadRequest("from/to must be ISO-8601 timestamps.")
                : !TryDimensions(dimensions, out var dimensionFilters)
                    ? BadRequest("dimensions must be a JSON object with string or null values.")
                    : Results.Json(reader.Stats(new StatsQuery(window.From, window.To, profile, dimensionFilters),
                        DashboardProfiles.DimensionNames(options.TargetsPath)), Json));
        MapRead(api, "/live", async (HttpContext context) =>
        {
            // Stopping ends open streams, so a connected browser never holds up shutdown.
            using var stream = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
            activity.StreamOpened();
            try
            {
                await LiveFeed.StreamAsync(context, reader, options.LivePollInterval, stream.Token);
            }
            finally
            {
                activity.StreamClosed();
            }
        });
        MapRead(api, "/settings", () => Results.Json(DashboardSettings.Read(options.ConfigPath), Json));
        MapRead(api, "/profiles", () => Results.Json(DashboardProfiles.Read(options.TargetsPath), Json));
        // The only write route; DashboardSecurity admits PUT here alone, with its extra checks.
        api.MapMethods("/settings", [HttpMethods.Put], (Delegate)((HttpContext context) => DashboardSettings.PutAsync(context, options.ConfigPath, Json)));
        MapRead(api, "/{**rest}", () => Results.NotFound());

        var assets = DashboardAssets.Default;
        MapRead(app, "/", () => assets.Index());
        MapRead(app, "/{**path}", (string? path) => assets.Resolve(path));
        return app;
    }

    private static void MapRead(IEndpointRouteBuilder routes, string pattern, Delegate handler) =>
        routes.MapMethods(pattern, ReadMethods, handler);

    private static IResult Plan(JournalReader reader, string hash, string? view)
    {
        if (!PlanHash().IsMatch(hash))
            return BadRequest("Plan hash must be 64 hexadecimal characters.");
        StoredPlan? plan;
        try
        {
            plan = reader.Plan(hash.ToUpperInvariant());
        }
        catch (InvalidDataException)
        {
            // A corrupt gzip body is the same outcome as a plan that cannot be distilled.
            return Results.UnprocessableEntity();
        }

        if (plan is null)
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
            catch (Exception exception) when (exception is SqlHarnessSafetyException or System.Xml.XmlException or JsonException or InvalidOperationException or InvalidDataException)
            {
                return Results.UnprocessableEntity();
            }
        }

        var (contentType, extension) = plan.Format == "explain-json"
            ? ("application/json", ".explain.json")
            : ("application/xml", ".sqlplan");
        return Results.File(System.Text.Encoding.UTF8.GetBytes(plan.Document), contentType, plan.Hash.ToLowerInvariant() + extension);
    }

    private static IResult BadRequest(string message) => Results.Json(new { error = message }, Json, statusCode: StatusCodes.Status400BadRequest);

    private static bool TryDimensions(string? json, out IReadOnlyDictionary<string, string?> dimensions)
    {
        dimensions = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
            return true;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            var parsed = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (parsed.ContainsKey(property.Name))
                    return false;
                if (property.Value.ValueKind == JsonValueKind.String)
                    parsed.Add(property.Name, property.Value.GetString());
                else if (property.Value.ValueKind == JsonValueKind.Null)
                    parsed.Add(property.Name, null);
                else
                    return false;
            }
            dimensions = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

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
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        if (addresses.Count != 1)
            throw new InvalidOperationException($"The dashboard must listen on exactly one address, found {addresses.Count}.");
        var address = new Uri(addresses.Single());
        if (!IPAddress.TryParse(address.Host, out var ip) || !ip.Equals(IPAddress.Loopback))
            throw new InvalidOperationException("The dashboard must listen only on 127.0.0.1.");
        return address.Port;
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