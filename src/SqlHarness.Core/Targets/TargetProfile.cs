namespace SqlHarness.Core.Targets;

public sealed record TargetProfile(
    string Server,
    string Database,
    IReadOnlyDictionary<string, string> Vars,
    string Auth,
    string? SqlUser = null,
    string? PasswordEnvVar = null,
    bool TrustServerCertificate = false);