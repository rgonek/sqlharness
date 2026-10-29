using System.Text.Json;
using System.Text.Json.Serialization;

using SqlHarness.Core.Mutations;
using SqlHarness.Core.Postgres;

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
            return Parse(File.ReadAllBytes(path));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            var safeMutationMessage = exception.Message is
                "A profile mutation mode is invalid." or
                "A profile that allows mutations needs mutations.writer." or
                "mutations.writer needs sqlUser and passwordEnvVar, or sameAsReader." or
                "yolo on a prod or staging profile needs mutations.acknowledgeProductionYolo: true."
                ? $" {exception.Message}"
                : string.Empty;
            throw new SqlHarnessSafetyException($"Could not parse target profiles file '{path}'.{safeMutationMessage}");
        }
    }

    public static IReadOnlyDictionary<string, TargetProfile> Parse(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
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

            ValidateMutations(profile);

            if (!string.IsNullOrWhiteSpace(profile.Engine))
            {
                try
                {
                    SqlEngineNames.Parse(profile.Engine);
                }
                catch (SqlHarnessSafetyException)
                {
                    throw new JsonException("A profile engine is invalid.");
                }
            }

            PostgresTransportPolicy.Resolve(
                SqlEngineNames.Parse(profile.Engine),
                profile.TrustServerCertificate,
                profile.SslMode,
                profile.RootCertificate);
        }

        return profiles;
    }

    internal static void ValidateMutations(TargetProfile profile)
    {
        if (profile.Mutations is not { } mutations)
            return;

        if (mutations.Mode is not null && !TryParseMutationMode(mutations.Mode, out _))
            throw new JsonException("A profile mutation mode is invalid.");

        if (mutations.Mode is not null && TryParseMutationMode(mutations.Mode, out var mode) && mode != MutationMode.Never && mutations.Writer is null)
            throw new JsonException("A profile that allows mutations needs mutations.writer.");

        if (mutations.Writer is { } writer)
        {
            var hasSqlUser = !string.IsNullOrWhiteSpace(writer.SqlUser);
            var hasPasswordEnvVar = !string.IsNullOrWhiteSpace(writer.PasswordEnvVar);
            var hasCredentials = hasSqlUser && hasPasswordEnvVar;
            if (writer.SameAsReader ? hasSqlUser || hasPasswordEnvVar : !hasCredentials)
                throw new JsonException("mutations.writer needs sqlUser and passwordEnvVar, or sameAsReader.");
        }

        if (string.Equals(mutations.Mode, "yolo", StringComparison.OrdinalIgnoreCase) &&
            profile.Environment is { } environment &&
            (environment.Equals("prod", StringComparison.OrdinalIgnoreCase) ||
             environment.Equals("production", StringComparison.OrdinalIgnoreCase) ||
             environment.Equals("staging", StringComparison.OrdinalIgnoreCase)) &&
            !mutations.AcknowledgeProductionYolo)
        {
            throw new JsonException("yolo on a prod or staging profile needs mutations.acknowledgeProductionYolo: true.");
        }
    }

    private static bool TryParseMutationMode(string? value, out MutationMode mode)
    {
        if (string.Equals(value, "never", StringComparison.OrdinalIgnoreCase))
        {
            mode = MutationMode.Never;
            return true;
        }
        if (string.Equals(value, "operator", StringComparison.OrdinalIgnoreCase))
        {
            mode = MutationMode.Operator;
            return true;
        }
        if (string.Equals(value, "yolo", StringComparison.OrdinalIgnoreCase))
        {
            mode = MutationMode.Yolo;
            return true;
        }

        mode = default;
        return false;
    }

    public static void RejectDuplicateProperties(JsonElement element)
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