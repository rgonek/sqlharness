using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Dashboard;

public sealed record ProfileVariable(string Name, string Rule);

public sealed record ProfileView(
    string Name, string Engine, string Server, string Database, string Auth, string? SqlUser, string? PasswordEnvVar,
    string? SslMode, bool TrustServerCertificate, string? RootCertificate, string Tls, IReadOnlyList<ProfileVariable> Vars);

public sealed record ProfilesResponse(string Status, IReadOnlyList<ProfileView> Profiles, string? Message);

/// <summary>
/// Read-only view of targets.json. It never reads environment variables: a profile's
/// password stays in its variable and only the variable's name is shown.
/// </summary>
internal static class DashboardProfiles
{
    internal const string InvalidMessage = "targets.json could not be read. Run `sqlharness doctor` for details.";

    internal static ProfilesResponse Read(string path)
    {
        if (!File.Exists(path))
            return new ProfilesResponse("missing", [], null);
        IReadOnlyDictionary<string, TargetProfile> profiles;
        try
        {
            profiles = ProfileStore.Load(path);
        }
        catch (Exception exception) when (exception is SqlHarnessSafetyException or IOException or UnauthorizedAccessException)
        {
            // The loader's message names the path; the page gets a fixed sentence instead.
            return new ProfilesResponse("invalid", [], InvalidMessage);
        }

        return new ProfilesResponse("valid", profiles
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => View(pair.Key, pair.Value))
            .ToArray(), null);
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

    private static ProfileView View(string name, TargetProfile profile) => new(
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
        profile.Vars.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ProfileVariable(pair.Key, pair.Value)).ToArray());
}