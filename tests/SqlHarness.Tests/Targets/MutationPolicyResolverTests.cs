using SqlHarness.Core;
using SqlHarness.Core.Mutations;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Targets;

public sealed class MutationPolicyResolverTests
{
    [Theory]
    [InlineData(null, "never", "never", "global")]
    [InlineData(null, "operator", "operator", "global")]
    [InlineData(null, "yolo", "yolo", "global")]
    [InlineData("never", "yolo", "never", "profile")]
    [InlineData("operator", "never", "operator", "profile")]
    [InlineData("yolo", "operator", "yolo", "profile")]
    public void Profile_choice_and_global_default_resolve_independently(string? configured, string global, string effective, string source)
    {
        var profile = new TargetProfile("s", "d", new Dictionary<string, string>(), "integrated",
            Mutations: new ProfileMutations(configured, new MutationWriter(SameAsReader: true)));
        var policy = MutationPolicyResolver.Resolve(profile, new MutationsConfig { DefaultMode = global });
        Assert.Equal(configured, policy.ConfiguredMode);
        Assert.Equal(effective, policy.EffectiveMode);
        Assert.Equal(source, policy.ModeSource);
        Assert.True(policy.WriterConfigured);
    }

    [Fact]
    public void Absent_block_tracks_global_changes_without_configuring_a_writer()
    {
        var profile = new TargetProfile("s", "d", new Dictionary<string, string>(), "integrated", Environment: "staging");
        Assert.Equal("never", MutationPolicyResolver.Resolve(profile, new MutationsConfig()).EffectiveMode);
        var changed = MutationPolicyResolver.Resolve(profile, new MutationsConfig { DefaultMode = "yolo" });
        Assert.Equal("yolo", changed.EffectiveMode);
        Assert.False(changed.WriterConfigured);
        Assert.True(changed.ProductionAcknowledgementRequired);
        Assert.Null(profile.Mutations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("invalid")]
    public void Invalid_mode_is_rejected(string mode)
    {
        var profile = new TargetProfile("s", "d", new Dictionary<string, string>(), "integrated", Mutations: new ProfileMutations(mode));
        Assert.Throws<SqlHarnessSafetyException>(() => MutationPolicyResolver.Resolve(profile, new MutationsConfig()));
    }
}
