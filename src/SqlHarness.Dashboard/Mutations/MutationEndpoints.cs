using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using SqlHarness.Core;
using SqlHarness.Core.Journal;
using SqlHarness.Core.Mutations;
using SqlHarness.Core.Targets;

namespace SqlHarness.Dashboard.Mutations;

internal sealed class MutationEndpointServices(
    MutationBroker broker,
    ISqlHarnessModule module,
    Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles,
    Func<SqlHarnessConfigLoadResult> loadConfigResult,
    IActivityJournal journal,
    TimeProvider time,
    DashboardActivity activity,
    CancellationToken stopping,
    JsonSerializerOptions json)
{
    internal MutationBroker Broker { get; } = broker;
    internal ISqlHarnessModule Module { get; } = module;
    internal Func<IReadOnlyDictionary<string, TargetProfile>> LoadProfiles { get; } = loadProfiles;
    internal Func<SqlHarnessConfigLoadResult> LoadConfigResult { get; } = loadConfigResult;
    internal IActivityJournal Journal { get; } = journal;
    internal TimeProvider Time { get; } = time;
    internal DashboardActivity Activity { get; } = activity;
    internal CancellationToken Stopping { get; } = stopping;
    internal JsonSerializerOptions Json { get; } = json;
}

internal static class MutationEndpoints
{
    internal static void Map(IEndpointRouteBuilder routes, MutationEndpointServices services, JournalReader reader)
    {
        routes.MapMethods("/mutations", [HttpMethods.Post], (Delegate)((HttpContext context) => SubmitAsync(context, services)));
        routes.MapMethods("/mutations/events", [HttpMethods.Get], (Delegate)((HttpContext context) => EventsAsync(context, services)));
        routes.MapMethods("/mutations/{id}/approve", [HttpMethods.Post], (Delegate)((HttpContext context, string id) => DecideAsync(context, id, true, services)));
        routes.MapMethods("/mutations/{id}/deny", [HttpMethods.Post], (Delegate)((HttpContext context, string id) => DecideAsync(context, id, false, services)));
        routes.MapMethods("/mutations", [HttpMethods.Get, HttpMethods.Head], (Delegate)((long? cursor, int? limit) =>
            Results.Json(reader.MutationRequests(cursor, limit ?? JournalReader.MutationHistoryPageLimit), services.Json)));
    }

    private static async Task<IResult> SubmitAsync(HttpContext context, MutationEndpointServices services)
    {
        MutationRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<MutationRequest>(context.Request.Body, services.Json, context.RequestAborted);
            ValidateRequest(request);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request");
        }

        var configResult = services.LoadConfigResult();
        if (!IsSupportedConfiguration(configResult))
            return ConfigurationUnavailable();
        var config = configResult.Config;
        IReadOnlyDictionary<string, TargetProfile> profiles;
        ResolvedTarget target;
        TargetProfile profile;
        try
        {
            profiles = services.LoadProfiles();
            if (!profiles.TryGetValue(request!.Profile, out profile!))
                return Error(StatusCodes.Status400BadRequest, MutationStatus.DatabaseMismatch);
            target = TargetResolver.Resolve(new SqlTargetRequest(request.Profile, request.Vars), profiles);
        }
        catch (Exception)
        {
            return Error(StatusCodes.Status400BadRequest, MutationStatus.DatabaseMismatch);
        }

        if (!IdentityMatches(request!, target))
            return Error(StatusCodes.Status400BadRequest, MutationStatus.DatabaseMismatch);

        var dryRun = new SqlHarnessMutationOperation(
            new SqlTargetRequest(request!.Profile, request.Vars), request.Sql, request.Parameters,
            request.TimeoutSeconds, request.MaxRows, request.Database)
        {
            SessionKey = request.SessionKey,
            DryRun = true,
        };
        SqlHarnessOutcome outcome;
        try
        {
            outcome = await services.Module.ExecuteAsync(dryRun, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request");
        }

        if (outcome.Report is not SqlHarnessDryRunReport report || outcome.ExitCode != SqlHarnessExitCode.Success)
            return Error(StatusCodes.Status400BadRequest,
                outcome.Report is MutationBrokerResult { Status: MutationStatus.NotAMutation } ? MutationStatus.NotAMutation : "invalid_request");
        if (!string.Equals(report.Tier, request.Tier, StringComparison.OrdinalIgnoreCase))
            return Error(StatusCodes.Status400BadRequest, "tier_mismatch");

        var preparedOutcome = await services.Module.ExecuteAsync(dryRun with { DryRun = false }, context.RequestAborted).ConfigureAwait(false);
        if (preparedOutcome.ExitCode != SqlHarnessExitCode.Success || preparedOutcome.Report is not SqlHarnessMutationPrepared prepared ||
            !string.Equals(prepared.Request.Digest, request.Digest, StringComparison.Ordinal))
            return Error(StatusCodes.Status400BadRequest, "request_mismatch");
        request = prepared.Request;

        if (!Enum.TryParse<MutationMode>(profile.Mutations?.Mode ?? config.Mutations.DefaultMode, true, out var mode))
            return Error(StatusCodes.Status400BadRequest, "invalid_mutation_mode");

        var decision = MutationPolicy.Decide(mode, ParseTier(report.Tier));
        var id = Guid.NewGuid().ToString("N");
        var view = new PendingMutationView(id, request, profile.Environment ?? "unknown",
            profile.Mutations?.Writer?.SameAsReader ?? false, services.Time.GetUtcNow() + TimeSpan.FromSeconds(config.Mutations.ApprovalTimeoutSeconds));
        services.Journal.RecordMutation(new MutationJournalRecord(
            id, request.SessionKey, request.Profile, request.Engine, request.Server, request.Database,
            report.Tier, mode.ToString().ToLowerInvariant(), request.Digest,
            request.Sql, JsonSerializer.Serialize(request.Parameters, services.Json), services.Time.GetUtcNow(), profile.Environment));

        MutationBrokerResult brokerResult;
        try
        {
            brokerResult = await services.Broker.SubmitAsync(request, decision.Kind,
                TimeSpan.FromSeconds(config.Mutations.ApprovalTimeoutSeconds), view, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            services.Journal.UpdateMutation(id, "cancelled", null, null, services.Time.GetUtcNow());
            throw;
        }
        PersistTerminal(services, id, brokerResult.Status);
        return Results.Json(brokerResult, services.Json);
    }

    private static async Task<IResult> DecideAsync(HttpContext context, string id, bool approve, MutationEndpointServices services)
    {
        if (id.Length != 32 || id.Any(character => !Uri.IsHexDigit(character)) || id != id.ToLowerInvariant())
            return Error(StatusCodes.Status400BadRequest, "invalid_request_id");
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (body.RootElement.ValueKind != JsonValueKind.Object || body.RootElement.EnumerateObject().Any())
                return Error(StatusCodes.Status400BadRequest, "invalid_verification");
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_verification");
        }

        if (!IsSupportedConfiguration(services.LoadConfigResult()))
            return ConfigurationUnavailable();

        if (approve)
        {
            var result = services.Broker.TryApproveWithResult(id, "click");
            return result switch
            {
                MutationApprovalResult.Accepted => Results.Ok(),
                MutationApprovalResult.Busy => Results.Conflict(new { status = MutationStatus.Busy, error = "profile_execution_busy" }),
                _ => Results.Conflict(new { error = "request_not_pending" }),
            };
        }
        return services.Broker.TryDeny(id) ? Results.Ok() : Results.Conflict(new { error = "request_not_pending" });
    }

    private static async Task EventsAsync(HttpContext context, MutationEndpointServices services)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        using var stream = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, services.Stopping);
        using var subscription = services.Broker.Subscribe();
        services.Activity.StreamOpened();
        try
        {
            await WriteEvent(context, "snapshot", subscription.Sequence, subscription.Snapshot, services.Json, stream.Token).ConfigureAwait(false);
            await foreach (var envelope in subscription.Events.ReadAllAsync(stream.Token).ConfigureAwait(false))
            {
                switch (envelope.Event)
                {
                    case MutationAdded added:
                        await WriteEvent(context, "added", envelope.Sequence, added.View, services.Json, stream.Token).ConfigureAwait(false);
                        break;
                    case MutationRemoved removed:
                        await WriteEvent(context, "removed", envelope.Sequence,
                            new { requestId = removed.RequestId, state = StateName(removed.State) }, services.Json, stream.Token).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (stream.IsCancellationRequested)
        {
        }
        finally
        {
            services.Activity.StreamClosed();
        }
    }

    internal static void PersistEvent(MutationEndpointServices services, BrokerEvent change)
    {
        if (change is MutationRemoved removed)
        {
            var status = removed.State switch
            {
                MutationState.Approved => null,
                MutationState.Denied => MutationStatus.Denied,
                MutationState.Expired => MutationStatus.Timeout,
                MutationState.Cancelled => null,
                _ => null,
            };
            var verification = removed.State is MutationState.Approved or MutationState.Denied ? "click" : null;
            services.Journal.UpdateMutation(removed.RequestId, StateName(removed.State), status, verification, services.Time.GetUtcNow());
        }
    }

    private static void PersistTerminal(MutationEndpointServices services, string id, string status)
    {
        var state = status switch
        {
            MutationStatus.Success => "succeeded",
            MutationStatus.OutcomeUnknown => "outcome_unknown",
            MutationStatus.Timeout => "expired",
            MutationStatus.Denied => "denied",
            MutationStatus.Busy => "failed",
            _ => "failed",
        };
        services.Journal.UpdateMutation(id, state, status, null, services.Time.GetUtcNow());
    }

    private static async Task WriteEvent(HttpContext context, string name, long sequence, object data, JsonSerializerOptions json, CancellationToken ct)
    {
        await context.Response.WriteAsync($"id: {sequence}\nevent: {name}\ndata: {JsonSerializer.Serialize(data, json)}\n\n", ct).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void ValidateRequest(MutationRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Profile) || request.Vars is null ||
            string.IsNullOrWhiteSpace(request.Engine) || string.IsNullOrWhiteSpace(request.Server) ||
            string.IsNullOrWhiteSpace(request.Database) || string.IsNullOrWhiteSpace(request.Sql) ||
            request.Parameters is null || request.Parameters.Any(parameter => parameter is null) ||
            request.Grantees is null || request.Grantees.Any(string.IsNullOrWhiteSpace) ||
            string.IsNullOrWhiteSpace(request.SessionKey) || request.TimeoutSeconds is < 1 or > 600 || request.MaxRows < 1)
            throw new ArgumentException("The mutation request is incomplete.");
    }

    private static bool IdentityMatches(MutationRequest request, ResolvedTarget target) =>
        string.Equals(request.Engine, SqlEngineNames.Format(target.Engine), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(request.Server, target.Server, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(request.Database, target.Database, StringComparison.OrdinalIgnoreCase);

    private static SqlRiskTier ParseTier(string tier) => tier.ToLowerInvariant() switch
    {
        "dml" => SqlRiskTier.Dml,
        "schema" => SqlRiskTier.Schema,
        "privilege" => SqlRiskTier.Privilege,
        "opaque" => SqlRiskTier.Opaque,
        _ => SqlRiskTier.ReadOnly,
    };

    private static string StateName(MutationState state) => state switch
    {
        MutationState.Pending => "pending",
        MutationState.Approved => "approved",
        MutationState.Denied => "denied",
        MutationState.Expired => "expired",
        MutationState.Cancelled => "cancelled",
        MutationState.Executing => "executing",
        MutationState.Succeeded => "succeeded",
        MutationState.OutcomeUnknown => "outcome_unknown",
        _ => "failed",
    };

    private static IResult Error(int httpStatus, string error)
    {
        var wireStatus = error is MutationStatus.Unavailable or MutationStatus.DatabaseMismatch or
            MutationStatus.NotAMutation or MutationStatus.PrincipalProtected ? error : null;
        return Results.Json(new { status = wireStatus, error }, statusCode: httpStatus);
    }

    private static IResult ConfigurationUnavailable() => Results.Json(new
    {
        status = MutationStatus.Unavailable,
        error = "invalid_mutation_configuration",
        message = "Mutation configuration is invalid; click is the only supported verification method.",
    }, statusCode: StatusCodes.Status400BadRequest);

    private static bool IsSupportedConfiguration(SqlHarnessConfigLoadResult result) =>
        result.Status != SqlHarnessConfigStatus.Invalid &&
        string.Equals(result.Config.Mutations.Verification, "click", StringComparison.OrdinalIgnoreCase);
}