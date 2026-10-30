using SqlHarness.Core.Targets;

namespace SqlHarness.Core.Mutations;

public sealed record EffectiveMutationPolicy(string? ConfiguredMode, string EffectiveMode, string ModeSource,
    bool WriterConfigured, bool ProductionAcknowledgementRequired);

public static class MutationPolicyResolver
{
    public static EffectiveMutationPolicy Resolve(TargetProfile profile, MutationsConfig global)
    {
        var configured = profile.Mutations?.Mode;
        var mode = configured ?? global.DefaultMode;
        if (!string.Equals(mode, "never", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "operator", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "yolo", StringComparison.OrdinalIgnoreCase))
            throw new SqlHarnessSafetyException("The mutation mode is invalid.");
        var production = string.Equals(profile.Environment, "prod", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(profile.Environment, "production", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(profile.Environment, "staging", StringComparison.OrdinalIgnoreCase);
        return new(configured, mode.ToLowerInvariant(), configured is null ? "global" : "profile",
            profile.Mutations?.Writer is not null,
            production && string.Equals(mode, "yolo", StringComparison.OrdinalIgnoreCase) &&
            !(profile.Mutations?.AcknowledgeProductionYolo ?? false));
    }
}