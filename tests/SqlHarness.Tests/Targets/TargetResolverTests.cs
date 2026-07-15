using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Targets;

public sealed class TargetResolverTests
{
    private static readonly IReadOnlyDictionary<string, TargetProfile> Profiles =
        new Dictionary<string, TargetProfile>
        {
            ["prod-eu"] = new(
                "contoso.database.windows.net",
                "contoso-{tenant}-{env}",
                new Dictionary<string, string>
                {
                    ["tenant"] = "^[a-z0-9]{3,20}$",
                    ["env"] = "^(dev|test|uat)$",
                },
                "ad-default"),
        };

    [Fact]
    public void Resolves_profile_template_with_validated_vars()
    {
        var request = new SqlTargetRequest("prod-eu", new Dictionary<string, string>
        {
            ["tenant"] = "acme",
            ["env"] = "uat",
        });

        var target = TargetResolver.Resolve(request, Profiles);

        Assert.Equal("contoso.database.windows.net", target.Server);
        Assert.Equal("contoso-acme-uat", target.Database);
        Assert.Equal(AuthStrategy.AdDefault, target.Auth.Strategy);
        Assert.Equal("profile", target.Mode);
    }

    [Theory]
    [InlineData("missing", "Unknown profile")]
    public void Rejects_unknown_profile(string profile, string expected) =>
        AssertSafety(new SqlTargetRequest(profile, EmptyVars()), expected);

    [Fact]
    public void Rejects_missing_var() =>
        AssertSafety(new SqlTargetRequest("prod-eu", new Dictionary<string, string> { ["tenant"] = "acme" }), "Missing");

    [Fact]
    public void Rejects_extra_var() =>
        AssertSafety(new SqlTargetRequest("prod-eu", new Dictionary<string, string>
        {
            ["tenant"] = "acme", ["env"] = "uat", ["extra"] = "value",
        }), "Extra");

    [Fact]
    public void Rejects_regex_mismatch() =>
        AssertSafety(new SqlTargetRequest("prod-eu", new Dictionary<string, string>
        {
            ["tenant"] = "ACME", ["env"] = "uat",
        }), "tenant");

    [Fact]
    public void Rejects_unresolved_placeholder()
    {
        var profiles = new Dictionary<string, TargetProfile>
        {
            ["broken"] = new("server", "db-{undeclared}", EmptyVars(), "integrated"),
        };

        AssertSafety(new SqlTargetRequest("broken", EmptyVars()), "placeholder", profiles);
    }

    [Fact]
    public void Rejects_invalid_profile_regex_without_echoing_pattern()
    {
        const string secretPattern = "SECRET[";
        var profiles = new Dictionary<string, TargetProfile>
        {
            ["broken"] = new("server", "db-{tenant}", new Dictionary<string, string> { ["tenant"] = secretPattern }, "integrated"),
        };

        var error = Assert.Throws<SqlHarnessSafetyException>(() =>
            TargetResolver.Resolve(new SqlTargetRequest("broken", new Dictionary<string, string> { ["tenant"] = "x" }), profiles));
        Assert.DoesNotContain(secretPattern, error.ToString());
    }

    [Fact]
    public void Resolves_direct_target()
    {
        var request = new SqlTargetRequest(null, EmptyVars(), "server", "database", "integrated", true);

        var target = TargetResolver.Resolve(request, Profiles);

        Assert.Equal("server", target.Server);
        Assert.Equal("database", target.Database);
        Assert.Equal(AuthStrategy.Integrated, target.Auth.Strategy);
        Assert.Equal("direct", target.Mode);
    }

    [Fact]
    public void Rejects_direct_without_unsafe_direct() =>
        AssertSafety(new SqlTargetRequest(null, EmptyVars(), "server", "database", "integrated"), "unsafe-direct");

    [Fact]
    public void Rejects_direct_with_vars() =>
        AssertSafety(new SqlTargetRequest(null, new Dictionary<string, string> { ["tenant"] = "acme" }, "server", "database", "integrated", true), "vars");

    [Fact]
    public void Rejects_direct_without_auth() =>
        AssertSafety(new SqlTargetRequest(null, EmptyVars(), "server", "database", null, true), "auth");

    [Fact]
    public void Rejects_profile_combined_with_server() =>
        AssertSafety(new SqlTargetRequest("prod-eu", EmptyVars(), "server"), "profile");

    [Fact]
    public void Rejects_neither_profile_nor_direct_target() =>
        AssertSafety(new SqlTargetRequest(null, EmptyVars()), "target");

    private static IReadOnlyDictionary<string, string> EmptyVars() => new Dictionary<string, string>();

    private static void AssertSafety(
        SqlTargetRequest request,
        string message,
        IReadOnlyDictionary<string, TargetProfile>? profiles = null) =>
        Assert.Contains(message, Assert.Throws<SqlHarnessSafetyException>(() =>
            TargetResolver.Resolve(request, profiles ?? Profiles)).Message, StringComparison.OrdinalIgnoreCase);
}
