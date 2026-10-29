namespace SqlHarness.Core.Targets;

public sealed record TargetProfile(
    string Server,
    string Database,
    IReadOnlyDictionary<string, string> Vars,
    string Auth,
    string? SqlUser = null,
    string? PasswordEnvVar = null,
    bool TrustServerCertificate = false,
    string? Engine = null,
    string? SslMode = null,
    string? RootCertificate = null,
    string? Environment = null,
    ProfileMutations? Mutations = null);

public sealed record MutationWriter(string? SqlUser = null, string? PasswordEnvVar = null, bool SameAsReader = false);
public sealed record ProfileMutations(string? Mode = null, MutationWriter? Writer = null, bool AcknowledgeProductionYolo = false);
