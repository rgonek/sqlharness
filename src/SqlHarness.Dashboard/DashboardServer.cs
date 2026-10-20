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
using SqlHarness.Core.Journal;
using SqlHarness.Core.Mutations;
using SqlHarness.Core.Targets;
using SqlHarness.Dashboard.Mutations;
using SqlHarness.Dashboard.Transcripts;

namespace SqlHarness.Dashboard;

public sealed record DashboardServerOptions(string DatabasePath, int PreferredPort, IProcessInfo Processes)
{
    /// <summary>The persistent token; null generates a fresh one (tests).</summary>
    public string? Token { get; init; }

    public TimeSpan LivePollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Records requests and open live streams; a new instance on the system clock when null.</summary>
    internal DashboardActivity? Activity { get; init; }

    /// <summary>The operator settings file the settings page reads and writes.</summary>
    public string ConfigPath { get; init; } = SqlHarnessPaths.ConfigFile;

    /// <summary>The closed profile file shown read-only on the profiles page.</summary>
    public string TargetsPath { get; init; } = SqlHarnessPaths.TargetsFile;

    /// <summary>The refreshed model price snapshot; the embedded table is used when it is missing or unusable.</summary>
    public string PricesPath { get; init; } = Path.Combine(SqlHarnessPaths.Home, "data", "model-prices.json");

    /// <summary>Supplies the models.dev JSON on a refresh; null performs the HTTPS download. Tests inject this.</summary>
    public Func<CancellationToken, Task<Stream>>? PricesFetcher { get; init; }

    /// <summary>Clock for operator writes (annotation timestamps).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    internal AgentHomes AgentHomes { get; init; } = AgentHomes.Current();

    internal Func<string, bool>? AgentDirectoryProbe { get; init; }

    internal ISqlHarnessModule? MutationModule { get; init; }

    internal IActivityJournal? MutationJournal { get; init; }
}

public sealed class RunningDashboard : IAsyncDisposable
{
    private readonly WebApplication _app;

    internal RunningDashboard(WebApplication app, int port, string token, DashboardActivity activity, TranscriptMatcher transcripts)
    {
        _app = app;
        Port = port;
        Token = token;
        Activity = activity;
        Transcripts = transcripts;
    }

    public int Port { get; }

    public string Token { get; }

    internal DashboardActivity Activity { get; }

    internal TranscriptMatcher Transcripts { get; }

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
    private const int MaximumPlanManifestResponseBytes = 1024 * 1024;
    private const int MaximumPlanGraphResponseBytes = 1024 * 1024;
    private const int MaximumPlanSourceMapResponseBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] ReadMethods = [HttpMethods.Get, HttpMethods.Head];

    public static async Task<RunningDashboard> StartAsync(DashboardServerOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PreferredPort, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.PreferredPort, IPEndPoint.MaxPort, nameof(options));
        var token = options.Token ?? DashboardSecurity.NewToken();
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
            var app = Build(options, activity, token, candidate, () => boundPort, out var transcripts);
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

            return new RunningDashboard(app, boundPort, token, activity, transcripts);
        }

        throw new IOException("No free loopback port for the dashboard.", last);
    }

    private static WebApplication Build(DashboardServerOptions options, DashboardActivity activity, string token, int port, Func<int> boundPort, out TranscriptMatcher transcripts)
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
        var loadConfigResult = () => SqlHarnessConfigLoader.Load(options.ConfigPath);
        var loadConfig = () => loadConfigResult().Config;
        var loadProfiles = () => ProfileStore.Load(options.TargetsPath);
        var initialConfig = loadConfig();
        var module = options.MutationModule ?? new SqlHarnessModule(loadProfiles, initialConfig.Compare, initialConfig.Connection);
        var journal = options.MutationJournal ?? NullActivityJournal.Instance;
        var executor = new MutationExecutor(module, loadProfiles, loadConfig, journal, options.Time);
        var mutationBroker = new MutationBroker(options.Time,
            (request, execution, ct) => executor.ExecuteAsync(request, execution, ct));
        var mutationServices = new MutationEndpointServices(mutationBroker, module, loadProfiles, loadConfigResult,
            journal, options.Time, activity, app.Lifetime.ApplicationStopping, Json);
        mutationBroker.Changed += change => MutationEndpoints.PersistEvent(mutationServices, change);
        var matcher = new TranscriptMatcher(options.DatabasePath, options.AgentHomes, options.Time)
        {
            DirectoryExists = options.AgentDirectoryProbe ?? Directory.Exists,
        };
        transcripts = matcher;
        app.Use((context, next) => DashboardSecurity.InvokeAsync(context, () => next(context), token, boundPort));
        // Only authenticated requests reach here, so probes from other local processes do not keep the dashboard alive.
        app.Use((context, next) =>
        {
            activity.Touch();
            return next(context);
        });
        app.UseRouting();

        var api = app.MapGroup("/api");
        MapRead(api, "/agents", () => Results.Json(reader.AgentKinds(), Json));
        MapRead(api, "/sessions", (string? agent, string? transport, string? from, string? to, long? cursor, int? limit,
            string? archived, bool? starred, string? q, bool? hasCompare) =>
            q is { Length: > JournalReader.MaximumSearchLength }
                ? BadRequest("q must be at most 200 characters.")
                : !TryArchived(archived, out var archivedFilter)
                    ? BadRequest("archived must be true, false or any.")
                : TryWindow(from, to, out var window)
                    ? Results.Json(ToSessionList(reader.Sessions(new SessionQuery(agent, transport, window.From, window.To, cursor,
                        limit ?? JournalReader.DefaultLimit, archivedFilter, starred, string.IsNullOrWhiteSpace(q) ? null : q.Trim(), hasCompare)),
                        TranscriptsEnabled(options.ConfigPath)), Json)
                    : BadRequest("from/to must be ISO-8601 timestamps."));
        MapRead(api, "/agent-sessions", (string? archived, bool? starred, long? cursor, int? limit, string? agent, string? q, bool? hasCompare) =>
            !TranscriptsEnabled(options.ConfigPath)
                ? Results.NotFound()
                : !TryArchived(archived, out var archivedFilter)
                    ? BadRequest("archived must be true, false or any.")
                : q is { Length: > JournalReader.MaximumSearchLength }
                    ? BadRequest($"q must be at most {JournalReader.MaximumSearchLength} characters.")
                    : Results.Json(reader.AgentSessions(archivedFilter, starred, cursor, limit, agent, q, hasCompare), Json));
        MapRead(api, "/agent-sessions/unmatched", (string? agent, string? transport, string? from, string? to, long? cursor, int? limit,
            string? archived, bool? starred, string? q, bool? hasCompare) =>
            !TranscriptsEnabled(options.ConfigPath)
                ? Results.NotFound()
                : q is { Length: > JournalReader.MaximumSearchLength }
                    ? BadRequest($"q must be at most {JournalReader.MaximumSearchLength} characters.")
                : !TryArchived(archived, out var archivedFilter)
                    ? BadRequest("archived must be true, false or any.")
                : TryWindow(from, to, out var window)
                    ? Results.Json(ToSessionList(reader.UnmatchedAgentSessions(new SessionQuery(agent, transport, window.From, window.To,
                        cursor, limit ?? JournalReader.DefaultLimit, archivedFilter, starred, string.IsNullOrWhiteSpace(q) ? null : q.Trim(), hasCompare)),
                        matching: true), Json)
                    : BadRequest("from/to must be ISO-8601 timestamps."));
        MapRead(api, "/agent-sessions/{kind}/{id}", (string kind, string id) =>
            !TranscriptsEnabled(options.ConfigPath)
                ? Results.NotFound()
                : kind is not ("claude" or "codex" or "opencode") || id.Length is 0 or > 512
                    ? BadRequest("kind or id is invalid.")
                    : reader.AgentSession(new AgentSessionKey(kind, id)) is { } detail ? Results.Json(detail, Json) : Results.NotFound());
        MapRead(api, "/agent-sessions/{kind}/{id}/optimization", (string kind, string id) =>
            !TranscriptsEnabled(options.ConfigPath)
                ? Results.NotFound()
                : kind is not ("claude" or "codex" or "opencode") || id.Length is 0 or > 512
                    ? BadRequest("kind or id is invalid.")
                    : reader.AgentSession(new AgentSessionKey(kind, id)) is null
                        ? Results.NotFound()
                        : Results.Json(reader.AgentOptimization(new AgentSessionKey(kind, id), loadConfig().Journal.StoreSensitive), Json));
        MapRead(api, "/agent-sessions/{kind}/{id}/optimization/compare", (string kind, string id, string? subject, string? database,
            string? group, string? scope, string? a, string? b, string? @case, string? aggregate, string? view) =>
            !TranscriptsEnabled(options.ConfigPath)
                ? Results.NotFound()
                : kind is not ("claude" or "codex" or "opencode") || id.Length is 0 or > 512
                    ? BadRequest("kind or id is invalid.")
                    : AgentSessionOptimizationCompare(reader, new AgentSessionKey(kind, id), subject, database, group, scope,
                        a, b, @case, aggregate, view, loadConfig().Journal.StoreSensitive, Json));
        MapRead(api, "/agent-sessions/{kind}/{id}/sql", (string kind, string id, string? hash) =>
            !TranscriptsEnabled(options.ConfigPath)
                ? Results.NotFound()
                : kind is not ("claude" or "codex" or "opencode") || id.Length is 0 or > 512
                    ? BadRequest("kind or id is invalid.")
                : hash is null || !SqlHashPattern().IsMatch(hash)
                    ? BadRequest("hash must be sha256:<64 lowercase hex>.")
                    : reader.AgentSessionSqlText(new AgentSessionKey(kind, id), hash) is { } text ? Results.Json(text, Json) : Results.NotFound());
        MapRead(api, "/sessions/{id:long}", (long id) =>
            reader.Session(id) is { } detail ? Results.Json(detail, Json) : Results.NotFound());
        MapRead(api, "/sessions/{id:long}/optimization", (long id) =>
            reader.Session(id) is null ? Results.NotFound() : Results.Json(reader.Optimization(id, loadConfig().Journal.StoreSensitive), Json));
        MapRead(api, "/sessions/{id:long}/optimization/compare", (long id, string? subject, string? database,
            string? group, string? scope, string? a, string? b, string? @case, string? aggregate, string? view) =>
            SessionOptimizationCompare(reader, id, subject, database, group, scope, a, b, @case, aggregate, view, loadConfig().Journal.StoreSensitive, Json));
        MapRead(api, "/sessions/{id:long}/compares", (long id, long? cursor, int? limit) =>
            Results.Json(reader.CompareAttempts(id, cursor, limit ?? JournalReader.CompareHistoryPageLimit), Json));
        MapRead(api, "/sessions/{id:long}/sql", (long id, string? hash) =>
            hash is null || !SqlHashPattern().IsMatch(hash)
                ? BadRequest("hash must be sha256:<64 lowercase hex>.")
                : reader.SessionSqlText(id, hash) is { } text ? Results.Json(text, Json) : Results.NotFound());
        MapRead(api, "/operations", (long? session, string? status, string? operation, string? from, string? to, long? cursor, int? limit,
            string? profile, string? dimensions, bool? unprofiled) =>
            !TryWindow(from, to, out var window)
                ? BadRequest("from/to must be ISO-8601 timestamps.")
                : !TryDimensions(dimensions, out var dimensionFilters)
                    ? BadRequest("dimensions must be a JSON object with string or null values.")
                    : Results.Json(reader.Operations(new OperationQuery(session, status, operation, window.From, window.To, cursor,
                        limit ?? JournalReader.DefaultLimit, profile, dimensionFilters, unprofiled ?? false)), Json));
        MapRead(api, "/named-operations", (string? q, long? cursor, int? limit) =>
            q is { Length: > JournalReader.MaximumSearchLength }
                ? BadRequest($"q must be at most {JournalReader.MaximumSearchLength} characters.")
                : cursor is <= 0
                    ? BadRequest("cursor must be positive.")
                    : Results.Json(reader.NamedOperations(string.IsNullOrWhiteSpace(q) ? null : q, cursor,
                        limit ?? JournalReader.DefaultLimit), Json));
        MapRead(api, "/operations/{id:long}", (long id) =>
            reader.Operation(id, DashboardProfiles.DimensionNames(options.TargetsPath), loadConfig().Journal.StoreSensitive) is { } detail
                ? Results.Json(detail, Json)
                : Results.NotFound());
        MapRead(api, "/operations/{id:long}/optimization", (long id, string? @case, string? aggregate, string? view) =>
            OperationOptimizationCompare(reader, id, @case, aggregate, view, loadConfig().Journal.StoreSensitive, Json));
        MapRead(api, "/operations/{id:long}/repro", (long id, string? side, string? @case, string? form) =>
        {
            if (side is not ("baseline" or "candidate" or "query"))
                return BadRequest("side must be baseline, candidate or query.");
            if (form is not ("as-executed" or "local-variables"))
                return BadRequest("form must be as-executed or local-variables.");
            if (string.IsNullOrWhiteSpace(@case))
                return BadRequest("case is required.");
            var script = reader.Reproduction(id, side, @case,
                form == "as-executed" ? ReproScriptForm.AsExecuted : ReproScriptForm.LocalVariables);
            return script is null ? Results.NotFound() : Results.Json(new
            {
                script.Text,
                completeness = script.Completeness.ToString(),
                script.Label,
            }, Json);
        });
        MapRead(api, "/operations/{id:long}/plans", (long id) =>
        {
            var manifest = reader.Plans(id, loadConfig().Journal.StoreSensitive);
            return manifest is null ? Results.NotFound() : BoundedPlanManifest(manifest);
        });
        MapRead(api, "/operations/{id:long}/source-map", (long id, string? variant, string? parameterSet,
            int? repetition, string? caseId) => PlanSourceMap(reader, id, variant, parameterSet, repetition, caseId,
                loadConfig().Journal.StoreSensitive));
        MutationEndpoints.Map(api, mutationServices, reader);
        MapRead(api, "/plan-documents/{sha}", (string sha) => RawPlanDocument(reader, sha, loadConfig().Journal.StoreSensitive));
        MapRead(api, "/plan-documents/{sha}/graph", (string sha) => PlanGraph(reader, sha, loadConfig().Journal.StoreSensitive));
        MapRead(api, "/plan-documents/{sha}/nodes/{nodeId}/properties", (string sha, string nodeId) =>
            PlanProperties(reader, sha, nodeId, loadConfig().Journal.StoreSensitive));
        MapRead(api, "/stats", (string? from, string? to, string? profile, string? dimensions,
            string? rowDimension, string? columnDimension, bool? unprofiled) =>
            !TryWindow(from, to, out var window)
                ? BadRequest("from/to must be ISO-8601 timestamps.")
                : !TryDimensions(dimensions, out var dimensionFilters)
                    ? BadRequest("dimensions must be a JSON object with string or null values.")
                    : Results.Json(reader.Stats(new StatsQuery(window.From, window.To, profile, dimensionFilters,
                            rowDimension, columnDimension, unprofiled ?? false),
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
        MapRead(api, "/transcripts", () => Results.Json(matcher.Status(TranscriptsEnabled(options.ConfigPath)) with
        {
            Coverage = reader.AgentSessionCoverage(),
        }, Json));
        // Guarded writes (settings, prices, session annotations, transcript clear, shutdown); DashboardSecurity admits only these, with its extra checks.
        api.MapMethods("/settings", [HttpMethods.Put], (Delegate)((HttpContext context) => DashboardSettings.PutAsync(context, options.ConfigPath, Json)));
        MapRead(api, "/prices", () => Results.Json(DashboardPrices.Describe(options.PricesPath), Json));
        api.MapMethods("/prices/refresh", [HttpMethods.Post], (Delegate)((HttpContext context) =>
            DashboardPrices.RefreshAsync(options.PricesPath, options.PricesFetcher ?? DashboardPrices.FetchModelsDevAsync, Json, context.RequestAborted)));
        var annotationWriter = new SessionAnnotationWriter(options.DatabasePath, options.Time);
        api.MapMethods("/sessions/{id:long}/annotation", [HttpMethods.Patch], (Delegate)((HttpContext context, long id) =>
            SessionAnnotations.PatchAsync(context, id, annotationWriter, Json)));
        var operationAnnotationWriter = new OperationAnnotationWriter(options.DatabasePath, options.Time);
        api.MapMethods("/operations/{id:long}/annotation", [HttpMethods.Patch], (Delegate)((HttpContext context, long id) =>
            OperationAnnotations.PatchAsync(context, id, operationAnnotationWriter, Json)));
        var agentSessionAnnotationWriter = new AgentSessionAnnotationWriter(options.DatabasePath, options.Time);
        api.MapMethods("/agent-sessions/{kind}/{id}/annotation", [HttpMethods.Patch], (Delegate)(async (HttpContext context, string kind, string id) =>
        {
            if (!TranscriptsEnabled(options.ConfigPath))
                return Results.NotFound();
            return await AgentSessionAnnotations.PatchAsync(context, kind, id, agentSessionAnnotationWriter, Json);
        }));
        api.MapMethods("/shutdown", ReadMethods, (Delegate)((HttpContext context) =>
        {
            context.Response.Headers.Allow = HttpMethods.Post;
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }));
        api.MapMethods("/shutdown", [HttpMethods.Post], (Delegate)((HttpContext context) =>
        {
            if (!MatchesExpectedShutdownIdentity(context.Request, options.Processes))
                return Results.Conflict();

            context.Response.OnCompleted(() =>
            {
                app.Lifetime.StopApplication();
                return Task.CompletedTask;
            });
            return Results.StatusCode(StatusCodes.Status202Accepted);
        }));
        api.MapMethods("/transcripts/clear", [HttpMethods.Post], (Delegate)(() =>
            matcher.Clear()
                ? Results.Json(matcher.Status(TranscriptsEnabled(options.ConfigPath)) with
                {
                    Coverage = reader.AgentSessionCoverage(),
                }, Json)
                : Results.Json(new { error = "The journal is busy; try again." }, Json, statusCode: StatusCodes.Status503ServiceUnavailable)));
        MapRead(api, "/{**rest}", () => Results.NotFound());

        var assets = DashboardAssets.Default;
        MapRead(app, "/", () => assets.Index());
        MapRead(app, "/{**path}", (string? path) => assets.Resolve(path));
        return app;
    }

    private static IResult SessionOptimizationCompare(JournalReader reader, long sessionId, string? subject,
        string? database, string? fingerprint, string? scope, string? a, string? b, string? selectedCase,
        string? aggregate, string? view, bool storeSensitive, JsonSerializerOptions json)
    {
        var normalizedAggregate = NormalizeAggregate(aggregate);
        if (normalizedAggregate is null)
            return BadRequest("aggregate must be sum or mean.");
        if (reader.Session(sessionId) is null)
            return Results.NotFound();

        if (!TryNormalizeView(view, out var normalizedView))
            return BadRequest("view must be changed, whole or statements.");
        var evidence = reader.OptimizationEvidence(sessionId, storeSensitive);
        var section = OptimizationSummaryBuilder.Build(evidence);
        var subjectChoices = section.Subjects.Where(item => (subject is null || item.Subject == subject)
            && (database is null || item.Database == database)).ToArray();
        if (subjectChoices.Length != 1)
            return Results.NotFound();
        var selectedSubject = subjectChoices[0];
        var groupChoices = selectedSubject.Groups.Where(item => fingerprint is null || item.Fingerprint == fingerprint).ToArray();
        if (groupChoices.Length != 1)
            return Results.NotFound();
        var selectedGroup = groupChoices[0];
        var scopeName = NormalizeOptimizationScope(scope);
        var scopeChoices = selectedGroup.Scopes.Where(item => scopeName is null || NormalizeOptimizationScope(item.Scope) == scopeName)
            .OrderBy(item => item.Scope == "whole query" ? 0 : 1).ThenBy(item => item.Scope, StringComparer.Ordinal).ToArray();
        if (scopeChoices.Length == 0)
            return Results.NotFound();
        var selectedScope = scopeChoices[0];
        var selectedA = a ?? selectedScope.DefaultA;
        var selectedB = b ?? selectedScope.DefaultB;
        if (selectedA is null || selectedB is null
            || !selectedScope.Versions.Any(version => version.Version == selectedA)
            || !selectedScope.Versions.Any(version => version.Version == selectedB))
            return Results.NotFound();

        var scopedGroup = selectedGroup with
        {
            Scopes = [selectedScope],
            DefaultA = selectedScope.DefaultA ?? selectedA,
            DefaultB = selectedScope.DefaultB,
        };
        var scopedEvidence = evidence.Where(item => item.Subject == selectedSubject.Subject
                && (item.Database ?? string.Empty) == selectedSubject.Database
                && item.Fingerprint == selectedGroup.Fingerprint
                && NormalizeOptimizationScope(item.Scope) == NormalizeOptimizationScope(selectedScope.Scope))
            .ToArray();
        var comparison = OptimizationComparer.Compare(scopedGroup, scopedEvidence, selectedA, selectedB,
            selectedCase ?? "all", normalizedAggregate, normalizedView);
        if (selectedCase is not null && selectedCase != "all"
            && !comparison.Cases.Any(item => item.Id == selectedCase))
            return Results.NotFound();
        return Results.Json(comparison, json);
    }

    private static IResult AgentSessionOptimizationCompare(JournalReader reader, AgentSessionKey key, string? subject,
        string? database, string? fingerprint, string? scope, string? a, string? b, string? selectedCase,
        string? aggregate, string? view, bool storeSensitive, JsonSerializerOptions json)
    {
        var normalizedAggregate = NormalizeAggregate(aggregate);
        if (normalizedAggregate is null)
            return BadRequest("aggregate must be sum or mean.");
        if (reader.AgentSession(key) is null)
            return Results.NotFound();
        if (!TryNormalizeView(view, out var normalizedView))
            return BadRequest("view must be changed, whole or statements.");

        var evidence = reader.OptimizationEvidenceForAgentSession(key, storeSensitive);
        var section = reader.AgentOptimization(key, storeSensitive);
        var subjectChoices = section.Subjects.Where(item => (subject is null || item.Subject == subject)
            && (database is null || item.Database == database)).ToArray();
        if (subjectChoices.Length != 1)
            return Results.NotFound();
        var selectedSubject = subjectChoices[0];
        var groupChoices = selectedSubject.Groups.Where(item => fingerprint is null || item.Fingerprint == fingerprint).ToArray();
        if (groupChoices.Length != 1)
            return Results.NotFound();
        var selectedGroup = groupChoices[0];
        var scopeName = NormalizeOptimizationScope(scope);
        var scopeChoices = selectedGroup.Scopes.Where(item => scopeName is null || NormalizeOptimizationScope(item.Scope) == scopeName)
            .OrderBy(item => item.Scope == "whole query" ? 0 : 1).ThenBy(item => item.Scope, StringComparer.Ordinal).ToArray();
        if (scopeChoices.Length == 0)
            return Results.NotFound();
        var selectedScope = scopeChoices[0];
        var selectedA = a ?? selectedScope.DefaultA;
        var selectedB = b ?? selectedScope.DefaultB;
        if (selectedA is null || selectedB is null
            || !selectedScope.Versions.Any(version => version.Version == selectedA)
            || !selectedScope.Versions.Any(version => version.Version == selectedB))
            return Results.NotFound();
        var scopedGroup = selectedGroup with
        {
            Scopes = [selectedScope],
            DefaultA = selectedScope.DefaultA ?? selectedA,
            DefaultB = selectedScope.DefaultB,
        };
        var scopedEvidence = evidence.Where(item => item.Subject == selectedSubject.Subject
                && (item.Database ?? string.Empty) == selectedSubject.Database
                && item.Fingerprint == selectedGroup.Fingerprint
                && NormalizeOptimizationScope(item.Scope) == NormalizeOptimizationScope(selectedScope.Scope))
            .ToArray();
        var comparison = OptimizationComparer.Compare(scopedGroup, scopedEvidence, selectedA, selectedB,
            selectedCase ?? "all", normalizedAggregate, normalizedView) with
        {
            VersionLabelsProvisional = section.VersionLabelsProvisional,
            VersionLabelNote = section.VersionLabelNote,
        };
        if (selectedCase is not null && selectedCase != "all"
            && !comparison.Cases.Any(item => item.Id == selectedCase))
            return Results.NotFound();
        return Results.Json(comparison, json);
    }

    private static IResult OperationOptimizationCompare(JournalReader reader, long operationId, string? selectedCase,
        string? aggregate, string? view, bool storeSensitive, JsonSerializerOptions json)
    {
        var normalizedAggregate = NormalizeAggregate(aggregate);
        if (normalizedAggregate is null)
            return BadRequest("aggregate must be sum or mean.");
        if (!TryNormalizeView(view, out var normalizedView))
            return BadRequest("view must be changed, whole or statements.");
        var operation = reader.Operation(operationId, storeSensitive: storeSensitive);
        if (operation is null || operation.Operation.Operation != "compare")
            return Results.NotFound();
        var allEvidence = reader.OptimizationEvidence(operation.Operation.SessionId, storeSensitive);
        var evidence = allEvidence.SingleOrDefault(item => item.OperationId == operationId);
        if (evidence is null || evidence.BaselineHash is null || evidence.CandidateHash is null)
            return Results.NotFound();

        var section = OptimizationSummaryBuilder.Build(allEvidence);
        var subject = evidence.Subject ?? "(unassigned)";
        var database = evidence.Database ?? string.Empty;
        var scopeLabel = evidence.Scope == "whole" ? "whole query" : evidence.Scope ?? "whole query";
        var group = section.Subjects.FirstOrDefault(item => item.Subject == evidence.Subject && item.Database == database)?
            .Groups.FirstOrDefault(item => item.Fingerprint == evidence.Fingerprint);
        var scopeVersions = group?.Scopes.FirstOrDefault(item => item.Scope == scopeLabel);
        var a = scopeVersions?.Versions.FirstOrDefault(item => item.Hash == evidence.BaselineHash)?.Version ?? "v0";
        var b = evidence.CandidateHash == evidence.BaselineHash
            ? a
            : scopeVersions?.Versions.FirstOrDefault(item => item.Hash == evidence.CandidateHash)?.Version ?? (a == "v0" ? "v1" : "v0");
        IReadOnlyList<VersionRow> versions = scopeVersions?.Versions ?? (a == b
            ? [new VersionRow(a, evidence.BaselineHash, null, null, null, null, "incomplete", [operationId], operationId)]
            : [
                new VersionRow(a, evidence.BaselineHash, null, null, null, null, "incomplete", [operationId], operationId),
                new VersionRow(b, evidence.CandidateHash, null, null, null, null, "incomplete", [operationId], operationId),
            ]);
        var ownEvidence = evidence with
        {
            Subject = subject,
            Database = database,
            Scope = evidence.Scope ?? (scopeLabel == "whole query" ? "whole" : scopeLabel),
        };
        var selectedScope = new ScopeVersions(scopeLabel, versions) { DefaultA = a, DefaultB = b };
        var compareGroup = new ConditionsGroup(evidence.Fingerprint ?? string.Empty, "", [selectedScope], a, b)
        {
            Subject = subject,
            Database = database,
        };
        var comparison = OptimizationComparer.Compare(compareGroup, [ownEvidence], a, b,
            selectedCase ?? "all", normalizedAggregate, normalizedView);
        if (selectedCase is not null && selectedCase != "all"
            && !comparison.Cases.Any(item => item.Id == selectedCase))
            return Results.NotFound();
        return Results.Json(comparison, json);
    }

    private static string? NormalizeAggregate(string? aggregate) => aggregate is null
        || aggregate.Equals("sum", StringComparison.OrdinalIgnoreCase) ? "sum"
        : aggregate.Equals("mean", StringComparison.OrdinalIgnoreCase) ? "mean" : null;

    private static bool TryNormalizeView(string? view, out string normalized)
    {
        normalized = view?.ToLowerInvariant() ?? "changed";
        if (normalized is "changed" or "whole" or "statements")
            return true;
        normalized = "changed";
        return false;
    }

    private static string? NormalizeOptimizationScope(string? scope) => scope switch
    {
        "whole" or "whole query" => "whole",
        null => null,
        _ => scope,
    };

    private static void MapRead(IEndpointRouteBuilder routes, string pattern, Delegate handler) =>
        routes.MapMethods(pattern, ReadMethods, handler);

    private static SessionListPage ToSessionList(Page<SessionSummary> page, bool matching) =>
        new(page.Items, page.NextCursor, matching);

    private static bool MatchesExpectedShutdownIdentity(HttpRequest request, IProcessInfo processes)
    {
        var hasPid = request.Headers.ContainsKey(DashboardSecurity.ShutdownProcessIdHeader);
        var hasStartedAt = request.Headers.ContainsKey(DashboardSecurity.ShutdownStartedAtTicksHeader);
        if (!hasPid && !hasStartedAt)
            return true;

        var pidValues = request.Headers[DashboardSecurity.ShutdownProcessIdHeader];
        var startedAtValues = request.Headers[DashboardSecurity.ShutdownStartedAtTicksHeader];
        if (pidValues.Count != 1 || startedAtValues.Count != 1
            || !int.TryParse(pidValues[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var expectedPid)
            || !long.TryParse(startedAtValues[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var expectedStartedAtTicks)
            || expectedPid != Environment.ProcessId)
            return false;

        var actual = processes.Get(expectedPid);
        return actual?.StartedAt is { } startedAt && startedAt.UtcTicks == expectedStartedAtTicks;
    }

    private static bool TranscriptsEnabled(string configPath) =>
        SqlHarnessConfigLoader.Load(configPath).Config.Dashboard.AgentTranscripts.Enabled;

    private static IResult RawPlanDocument(JournalReader reader, string sha, bool storeSensitive)
    {
        if (!PlanHash().IsMatch(sha))
            return BadRequest("Plan document SHA-256 must be 64 hexadecimal characters.");
        if (!storeSensitive)
            return PlansNotStored();

        StoredPlan? plan;
        try
        {
            plan = reader.Plan(sha.ToUpperInvariant());
        }
        catch (InvalidDataException)
        {
            return Results.UnprocessableEntity();
        }

        if (plan is null)
            return Results.NotFound();

        var (contentType, extension) = plan.Format == "explain-json"
            ? ("application/json", ".explain.json")
            : ("application/xml", ".sqlplan");
        return Results.File(System.Text.Encoding.UTF8.GetBytes(plan.Document), contentType,
            plan.Hash.ToLowerInvariant() + extension);
    }

    private static IResult PlanGraph(JournalReader reader, string sha, bool storeSensitive)
    {
        if (!PlanHash().IsMatch(sha))
            return BadRequest("Plan document SHA-256 must be 64 hexadecimal characters.");
        if (!storeSensitive)
            return PlansNotStored();

        StoredPlan? plan;
        try
        {
            plan = reader.Plan(sha.ToUpperInvariant());
        }
        catch (InvalidDataException)
        {
            return Results.UnprocessableEntity();
        }

        if (plan is null)
            return Results.NotFound();

        var result = BuildPlanGraph(plan);
        var payload = JsonSerializer.SerializeToUtf8Bytes(result, Json);
        if (result.Graph is not null && payload.Length > MaximumPlanGraphResponseBytes)
        {
            result = new PlanGraphBuildResult(result.Engine, null,
                new PlanGraphBuildError("graph_response_size_limit_exceeded", "The complete plan graph response exceeds the response size limit."));
            payload = JsonSerializer.SerializeToUtf8Bytes(result, Json);
        }

        return Results.Json(result, Json,
            statusCode: result.Error is null ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity);
    }

    private static IResult PlanProperties(JournalReader reader, string sha, string nodeId, bool storeSensitive)
    {
        if (!PlanHash().IsMatch(sha))
            return BadRequest("Plan document SHA-256 must be 64 hexadecimal characters.");
        if (!storeSensitive)
            return PlansNotStored();

        StoredPlan? plan;
        try
        {
            plan = reader.Plan(sha.ToUpperInvariant());
        }
        catch (InvalidDataException)
        {
            return Results.UnprocessableEntity();
        }

        if (plan is null)
            return Results.NotFound();

        var result = plan.Format == "showplan-xml"
            ? PlanGraphBuilder.BuildProperties(plan.Document, nodeId)
            : new PlanGraphPropertyResult(PlanEngine(plan.Format), nodeId, null,
                new PlanGraphBuildError("unsupported_plan_format", "Operator properties are available only for SQL Server Showplan XML."));
        return Results.Json(result, Json,
            statusCode: result.Error is null ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity);
    }

    private static IResult PlanSourceMap(JournalReader reader, long operationId, string? variant,
        string? parameterSet, int? repetition, string? caseId, bool storeSensitive)
    {
        if (string.IsNullOrWhiteSpace(variant) || variant.Length > 100)
            return BadRequest("variant is required and must be at most 100 characters.");
        if (parameterSet?.Length > JournalReader.MaximumSearchLength || caseId?.Length > JournalReader.MaximumSearchLength)
            return BadRequest($"parameterSet and caseId must be at most {JournalReader.MaximumSearchLength} characters.");
        if (repetition is null or < 1)
            return BadRequest("repetition must be a positive integer.");

        var selection = reader.PlanSourceMap(operationId, variant, parameterSet, repetition.Value, caseId, storeSensitive);
        if (selection is null)
            return Results.NotFound();
        if (selection.ErrorCode is not null)
        {
            var status = selection.ErrorCode == "case_required"
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status422UnprocessableEntity;
            return Results.Json(new { error = new { code = selection.ErrorCode, message = selection.Error } }, Json, statusCode: status);
        }
        if (selection.State == "plans not stored")
            return BoundedPlanSourceMap(new PlanSourceMapResponse(operationId, selection.State, selection.Engine,
                variant, parameterSet, caseId, null, repetition, null, null, [], [], []), StatusCodes.Status200OK);

        if (selection.ExecutedText is null)
            return Results.Json(new { error = new { code = "executed_sql_unavailable", message = "The exact executed SQL text is unavailable in the sensitive journal." } },
                Json, statusCode: StatusCodes.Status422UnprocessableEntity);

        var links = new List<PlanSourceMapLink>();
        var omitted = selection.Occurrences
            .Where(occurrence => occurrence.Omitted || occurrence.DocumentSha256 is null || !occurrence.Stored)
            .Select(occurrence => new PlanSourceMapOmission(occurrence.DocumentOrdinal, occurrence.StatementIndex,
                occurrence.DocumentSha256, occurrence.MatchKind, occurrence.Omitted, occurrence.Stored))
            .ToArray();
        var errors = new List<PlanSourceMapError>();
        var engine = selection.Engine;
        int? algorithmVersion = SourceMapBuilder.CurrentAlgorithmVersion;

        foreach (var group in selection.Occurrences.Where(occurrence => occurrence.DocumentSha256 is not null && occurrence.Stored)
                     .GroupBy(occurrence => occurrence.DocumentOrdinal).OrderBy(group => group.Key))
        {
            var documentSha = group.First().DocumentSha256!;
            StoredPlan? plan;
            try
            {
                plan = reader.Plan(documentSha.ToUpperInvariant());
            }
            catch (InvalidDataException exception)
            {
                errors.Add(new PlanSourceMapError(group.Key, documentSha, "stored_plan_invalid", exception.Message));
                continue;
            }

            if (plan is null)
            {
                errors.Add(new PlanSourceMapError(group.Key, documentSha, "stored_plan_missing", "The occurrence references a missing plan document."));
                continue;
            }

            var graphResult = BuildPlanGraph(plan);
            var graph = graphResult.Graph;
            if (graph is null)
            {
                var error = graphResult.Error ?? new PlanGraphBuildError("plan_graph_unavailable", "A complete plan graph is unavailable.");
                errors.Add(new PlanSourceMapError(group.Key, documentSha, error.Code, error.Message));
                continue;
            }

            var sourceMap = SourceMapBuilder.Build(selection.ExecutedText, plan.Document, graph,
                group.Select(occurrence => new SourceMapStatementSpan(occurrence.StatementIndex, occurrence.SpanStart,
                    occurrence.SpanLength, occurrence.MatchKind)).ToArray());
            engine = sourceMap.Engine;
            algorithmVersion = sourceMap.AlgorithmVersion;
            links.AddRange(sourceMap.Links.Select(link => new PlanSourceMapLink(documentSha, group.Key, link)));
        }

        var response = new PlanSourceMapResponse(operationId, selection.State, engine, selection.Variant,
            selection.ParameterSet, selection.CaseId, selection.CaseLabel, selection.Repetition, selection.ExecutedText,
            algorithmVersion, links, omitted, errors);
        return BoundedPlanSourceMap(response, errors.Count == 0
            ? StatusCodes.Status200OK
            : StatusCodes.Status422UnprocessableEntity);
    }

    private static PlanGraphBuildResult BuildPlanGraph(StoredPlan plan) => plan.Format == "showplan-xml"
        ? PlanGraphBuilder.Build(plan.Document)
        : new PlanGraphBuildResult(PlanEngine(plan.Format), null,
            new PlanGraphBuildError("unsupported_plan_format", "Plan graphs are available only for SQL Server Showplan XML."));

    private static string PlanEngine(string format) => format == "explain-json" ? "postgres" : "unknown";

    private static IResult PlansNotStored() => Results.Json(new
    {
        state = "plans not stored",
        error = new { code = "plans_not_stored", message = "Enable journal.storeSensitive to store SQL, plan documents, and source-map occurrences." },
    }, Json, statusCode: StatusCodes.Status409Conflict);

    internal static IResult BoundedPlanManifest(OperationPlanManifest manifest,
        int maximumBytes = MaximumPlanManifestResponseBytes) =>
        BoundedJson(manifest, maximumBytes,
            "plan_manifest_size_limit_exceeded", "The plan manifest exceeds the response size limit.");

    internal static IResult BoundedPlanSourceMap(PlanSourceMapResponse response, int statusCode,
        int maximumBytes = MaximumPlanSourceMapResponseBytes) =>
        BoundedJson(response, maximumBytes,
            "source_map_size_limit_exceeded", "The complete source-map response exceeds the response size limit.", statusCode);

    private static IResult BoundedJson<T>(T value, int maximumBytes, string errorCode, string errorMessage, int statusCode = StatusCodes.Status200OK)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        return payload.Length > maximumBytes
            ? Results.Json(new { error = new { code = errorCode, message = errorMessage } }, Json, statusCode: StatusCodes.Status413PayloadTooLarge)
            : Results.Json(value, Json, statusCode: statusCode);
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

    private static bool TryArchived(string? value, out bool? archived)
    {
        archived = value?.ToLowerInvariant() switch { null or "false" => false, "true" => true, _ => null };
        return value is null || value.ToLowerInvariant() is "true" or "false" or "any";
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

    [GeneratedRegex(@"\Asha256:[0-9a-f]{64}\z")]
    private static partial Regex SqlHashPattern();
}
