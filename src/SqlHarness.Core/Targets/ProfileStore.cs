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
            using var document = JsonDocument.Parse(stream);
            RejectDuplicateProperties(document.RootElement);
            var profiles = document.RootElement.Deserialize<Dictionary<string, TargetProfile>>(Options)
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

                if (profile.Vars.Any(variable =>
                        string.IsNullOrWhiteSpace(variable.Key) || variable.Value is null))
                {
                    throw new JsonException("A profile variable rule is invalid.");
                }
            }

            return profiles;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new SqlHarnessSafetyException($"Could not parse target profiles file '{path}'.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new JsonException("A duplicate JSON property was found.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }
}