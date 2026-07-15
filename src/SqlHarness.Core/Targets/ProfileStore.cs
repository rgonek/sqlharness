using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlHarness.Core.Targets;

public static class ProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static IReadOnlyDictionary<string, TargetProfile> Load(string? path = null)
    {
        path ??= SqlHarnessPaths.TargetsFile;
        if (!File.Exists(path))
            return new Dictionary<string, TargetProfile>(StringComparer.Ordinal);

        try
        {
            using var stream = File.OpenRead(path);
            var profiles = JsonSerializer.Deserialize<Dictionary<string, TargetProfile>>(stream, Options)
                ?? throw new JsonException("The profile document was null.");

            foreach (var (name, profile) in profiles)
            {
                if (string.IsNullOrWhiteSpace(name) || profile is null ||
                    string.IsNullOrWhiteSpace(profile.Server) ||
                    string.IsNullOrWhiteSpace(profile.Database) ||
                    string.IsNullOrWhiteSpace(profile.Auth) || profile.Vars is null)
                {
                    throw new JsonException("A profile is incomplete.");
                }
            }

            return profiles;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new SqlHarnessSafetyException($"Could not parse target profiles file '{path}'.");
        }
    }
}
