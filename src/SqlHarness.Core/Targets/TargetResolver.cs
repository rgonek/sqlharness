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

        var hasProfile = !string.IsNullOrWhiteSpace(request.Profile);
        var hasDirectFields = !string.IsNullOrWhiteSpace(request.Server) ||
                              !string.IsNullOrWhiteSpace(request.Database) ||
                              !string.IsNullOrWhiteSpace(request.Auth);

        if (hasProfile && (request.UnsafeDirect || hasDirectFields))
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
                if (!validator.IsMatch(request.Vars[name]))
                    throw new SqlHarnessSafetyException($"Profile var '{name}' does not match its validation rule.");
            }
            catch (RegexMatchTimeoutException exception)
            {
                throw new SqlHarnessSafetyException($"Validation timed out for profile var '{name}'.", exception);
            }

            database = database.Replace($"{{{name}}}", request.Vars[name], StringComparison.Ordinal);
        }

        if (Placeholder.IsMatch(database))
            throw new SqlHarnessSafetyException("The database template contains an unresolved placeholder.");

        return new ResolvedTarget(
            profile.Server,
            database,
            AuthSpec.Parse(profile.Auth, profile.SqlUser, profile.PasswordEnvVar, profile.TrustServerCertificate),
            "profile");
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
            AuthSpec.Parse(request.Auth, null, null, false),
            "direct");
    }
}
