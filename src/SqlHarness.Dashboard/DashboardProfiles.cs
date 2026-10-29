using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Http;

using SqlHarness.Core;
using SqlHarness.Core.Mutations;
using SqlHarness.Core.Targets;

namespace SqlHarness.Dashboard;

public sealed record ProfileVariable(string Name, string Rule);

public sealed record ProfileView(
    string Name, string Engine, string Server, string Database, string Auth, string? SqlUser, string? PasswordEnvVar,
    string? SslMode, bool TrustServerCertificate, string? RootCertificate, string Tls, IReadOnlyList<ProfileVariable> Vars,
    string? Environment, string MutationMode, string? MutationWriterKind, bool AcknowledgeProductionYolo,
    string? ConfiguredMode, string EffectiveMode, string ModeSource, bool WriterConfigured, bool ProductionAcknowledgementRequired,
    TargetProfile? EditableProfile = null);

public sealed record ProfilesResponse(string Status, IReadOnlyList<ProfileView> Profiles, string? Message, string? Revision = null);

/// <summary>
/// Read-only view of targets.json. It never reads environment variables: a profile's
/// password stays in its variable and only the variable's name is shown.
/// </summary>
internal static class DashboardProfiles
{
    internal const string InvalidMessage = "targets.json could not be read. Run `sqlharness doctor` for details.";

    internal static ProfilesResponse Read(string path, MutationsConfig? config = null)
    {
        if (!File.Exists(path))
            return new ProfilesResponse("missing", [], null);
        IReadOnlyDictionary<string, TargetProfile> profiles;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
            profiles = ProfileStore.Parse(bytes);
        }
        catch (Exception exception) when (exception is SqlHarnessSafetyException or IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // The loader's message names the path; the page gets a fixed sentence instead.
            return new ProfilesResponse("invalid", [], InvalidMessage);
        }

        return FromSnapshot(profiles, ProfileWriter.Revision(bytes), config);
    }

    private static ProfilesResponse FromSnapshot(IReadOnlyDictionary<string, TargetProfile> profiles, string revision, MutationsConfig? config)
    {
        return new ProfilesResponse("valid", profiles
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => View(pair.Key, pair.Value, config ?? new MutationsConfig()))
            .ToArray(), null, revision);
    }


    private sealed record PutRequest(string Name, string ExpectedRevision, TargetProfile Profile);
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static async Task<IResult> PutAsync(HttpContext context, string path, JsonSerializerOptions json, MutationsConfig? config = null)
    {
        const int limit = 1024 * 1024;
        if (context.Request.ContentLength > limit) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + count > limit) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            buffer.Write(chunk, 0, count);
        }
        PutRequest? request;
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            ProfileStore.RejectDuplicateProperties(document.RootElement);
            request = document.RootElement.Deserialize<PutRequest>(StrictJson);
            if (request is null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ExpectedRevision) || request.Profile is null)
                throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return Results.Json(new { errors = new[] { new { field = "$", message = "Invalid profile request." } } }, json, statusCode: 400);
        }
        context.RequestAborted.ThrowIfCancellationRequested();
        var result = ProfileWriter.Update(path, request.Name, request.ExpectedRevision, request.Profile, context.RequestAborted);
        return result.Status switch
        {
            ProfileWriteStatus.Success => Results.Json(FromSnapshot(result.Profiles!, result.Revision!, config), json),
            ProfileWriteStatus.Conflict => Results.Json(new { error = "Profiles changed; reload before saving." }, json, statusCode: 409),
            ProfileWriteStatus.NotFound => Results.Json(new { error = "Profile not found." }, json, statusCode: 404),
            ProfileWriteStatus.Invalid => Results.Json(new { errors = new[] { result.Error ?? new ProfileFieldError("$", "Invalid profile or profiles document.") } }, json, statusCode: 400),
            _ => Results.Json(new { error = "Could not save profiles." }, json, statusCode: 500),
        };
    }

    /// <summary>Reads only profile names and variable definitions for Statistics aggregation.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> DimensionNames(string path)
    {
        try
        {
            return ProfileStore.Load(path).ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.Vars.Keys.Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is SqlHarnessSafetyException or IOException or UnauthorizedAccessException)
        {
            // Profile metadata is optional for journal history and must never hide its totals.
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        }
    }

    private static string EngineOf(TargetProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Engine) ? "sqlserver" : profile.Engine.Trim().ToLowerInvariant();

    // Mirrors PostgresTransportPolicy: without sslMode, trustServerCertificate false means Require and true means Disable.
    private static string Tls(TargetProfile profile) =>
        EngineOf(profile) == "postgres"
            ? profile.SslMode ?? (profile.TrustServerCertificate ? "disable (legacy)" : "require (legacy)")
            : profile.TrustServerCertificate ? "trust server certificate" : "verify";

    private static ProfileView View(string name, TargetProfile profile, MutationsConfig config)
    {
        var policy = MutationPolicyResolver.Resolve(profile, config);
        return new(
            name,
            EngineOf(profile),
            profile.Server,
            profile.Database,
            profile.Auth,
            profile.SqlUser,
            profile.PasswordEnvVar,
            profile.SslMode,
            profile.TrustServerCertificate,
            profile.RootCertificate,
            Tls(profile),
            profile.Vars.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ProfileVariable(pair.Key, pair.Value)).ToArray(),
            profile.Environment,
            policy.EffectiveMode,
            profile.Mutations?.Writer is { } writer ? writer.SameAsReader ? "sameAsReader" : "separate" : null,
            profile.Mutations?.AcknowledgeProductionYolo ?? false,
            policy.ConfiguredMode, policy.EffectiveMode, policy.ModeSource, policy.WriterConfigured, policy.ProductionAcknowledgementRequired, profile);
    }
}