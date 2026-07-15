using System.Text.RegularExpressions;
using SqlHarness.Core.Auth;

namespace SqlHarness.Core.Targets;

public sealed record ResolvedTarget(string Server, string Database, AuthSpec Auth, string Mode);

public static class TargetResolver
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Placeholder = new("\\{[^{}]+\\}", RegexOptions.CultureInvariant, RegexTimeout);

    public static ResolvedTarget Resolve(
        SqlTargetRequest request,
        IReadOnlyDictionary<string, TargetProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profiles);

        if (request.Vars is null)
            throw new SqlHarnessSafetyException("Target vars cannot be null.");

        var hasProfile = !string.IsNullOrWhiteSpace(request.Profile);
        var hasDirectFields = request.Server is not null ||
                              request.Database is not null ||
                              request.Auth is not null;

        var hasDirectAuthSettings = request.SqlUser is not null ||
                                    request.PasswordEnvVar is not null ||
                                    request.TrustServerCertificate;

        if (hasProfile && (request.UnsafeDirect || hasDirectFields || hasDirectAuthSettings))
            throw new SqlHarnessSafetyException("A profile cannot be combined with direct target options.");

        if (hasProfile)
            return ResolveProfile(request, profiles);

        if (!request.UnsafeDirect)
        {
            if (hasDirectFields)
                throw new SqlHarnessSafetyException("Direct target options require --unsafe-direct.");
            throw new SqlHarnessSafetyException("A target profile or an --unsafe-direct target is required.");
        }

        return ResolveDirect(request);
    }

    private static ResolvedTarget ResolveProfile(
        SqlTargetRequest request,
        IReadOnlyDictionary<string, TargetProfile> profiles)
    {
        if (!profiles.TryGetValue(request.Profile!, out var profile))
            throw new SqlHarnessSafetyException($"Unknown profile '{request.Profile}'.");

        var missing = profile.Vars.Keys.Where(key => !request.Vars.ContainsKey(key)).ToArray();
        if (missing.Length != 0)
            throw new SqlHarnessSafetyException("Missing required profile vars.");

        var extra = request.Vars.Keys.Where(key => !profile.Vars.ContainsKey(key)).ToArray();
        if (extra.Length != 0)
            throw new SqlHarnessSafetyException("Extra profile vars are not allowed.");

        var database = profile.Database;
        foreach (var (name, pattern) in profile.Vars)
        {
            if (pattern is null)
                throw new SqlHarnessSafetyException($"Profile var '{name}' has an invalid validation regex.");
            if (request.Vars[name] is null)
                throw new SqlHarnessSafetyException($"Profile var '{name}' cannot be null.");

            Regex validator;
            try
            {
                var anchored = pattern.StartsWith('^') && pattern.EndsWith('$')
                    ? pattern
                    : $"^(?:{pattern})$";
                validator = new Regex(anchored, RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
            }
            catch (ArgumentException)
            {
                throw new SqlHarnessSafetyException($"Profile var '{name}' has an invalid validation regex.");
            }

            try
            {
                var match = validator.Match(request.Vars[name]);
                if (!match.Success || match.Index != 0 || match.Length != request.Vars[name].Length)
                    throw new SqlHarnessSafetyException($"Profile var '{name}' does not match its validation rule.");
            }
            catch (RegexMatchTimeoutException)
            {
                throw new SqlHarnessSafetyException($"Validation timed out for profile var '{name}'.");
            }

            database = database.Replace($"{{{name}}}", request.Vars[name], StringComparison.Ordinal);
        }

        if (Placeholder.IsMatch(database))
            throw new SqlHarnessSafetyException("The database template contains an unresolved placeholder.");

        AuthSpec auth;
        try
        {
            auth = AuthSpec.Parse(
                profile.Auth,
                profile.SqlUser,
                profile.PasswordEnvVar,
                profile.TrustServerCertificate);
        }
        catch (SqlHarnessSafetyException)
        {
            throw new SqlHarnessSafetyException("The profile authentication settings are invalid.");
        }

        return new ResolvedTarget(profile.Server, database, auth, "profile");
    }

    private static ResolvedTarget ResolveDirect(SqlTargetRequest request)
    {
        if (request.Vars.Count != 0)
            throw new SqlHarnessSafetyException("Direct mode does not accept vars.");
        if (string.IsNullOrWhiteSpace(request.Server))
            throw new SqlHarnessSafetyException("Direct mode requires --server.");
        if (string.IsNullOrWhiteSpace(request.Database))
            throw new SqlHarnessSafetyException("Direct mode requires --database.");
        if (string.IsNullOrWhiteSpace(request.Auth))
            throw new SqlHarnessSafetyException("Direct mode requires --auth.");

        return new ResolvedTarget(
            request.Server,
            request.Database,
            AuthSpec.Parse(
                request.Auth,
                request.SqlUser,
                request.PasswordEnvVar,
                request.TrustServerCertificate),
            "direct");
    }
}
