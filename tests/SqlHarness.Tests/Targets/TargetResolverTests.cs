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
    public void Invalid_profile_auth_is_rejected_without_echoing_config_value()
    {
        const string secretAuth = "SECRET_AUTH_STRATEGY";
        var profiles = new Dictionary<string, TargetProfile>
        {
            ["broken"] = new("server", "database", EmptyVars(), secretAuth),
        };

        var error = Assert.Throws<SqlHarnessSafetyException>(() =>
            TargetResolver.Resolve(new SqlTargetRequest("broken", EmptyVars()), profiles));

        Assert.DoesNotContain(secretAuth, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Regex_timeout_is_sanitized_without_value_or_inner_exception()
    {
        const string secretSuffix = "SECRET_TIMEOUT_VALUE";
        var profiles = new Dictionary<string, TargetProfile>
        {
            ["slow"] = new(
                "server",
                "db-{tenant}",
                new Dictionary<string, string> { ["tenant"] = "^(a+)+$" },
                "integrated"),
        };
        var value = new string('a', 100_000) + secretSuffix;

        var error = Assert.Throws<SqlHarnessSafetyException>(() =>
            TargetResolver.Resolve(
                new SqlTargetRequest("slow", new Dictionary<string, string> { ["tenant"] = value }),
                profiles));

        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretSuffix, error.ToString());
        Assert.Null(error.InnerException);
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

    [Theory]
    [InlineData("azure-cli", AuthStrategy.AzureCli)]
    [InlineData("ad-default", AuthStrategy.AdDefault)]
    [InlineData("integrated", AuthStrategy.Integrated)]
    public void Resolves_each_non_sql_direct_auth_strategy(string auth, AuthStrategy expected)
    {
        var request = new SqlTargetRequest(null, EmptyVars(), "server", "database", auth, true);

        Assert.Equal(expected, TargetResolver.Resolve(request, Profiles).Auth.Strategy);
    }

    [Fact]
    public void Resolves_direct_sql_auth_with_password_environment_variable()
    {
        const string passwordVariable = "SQLHARNESS_TEST_PASSWORD";
        Environment.SetEnvironmentVariable(passwordVariable, "test-only-password");
        try
        {
            var request = new SqlTargetRequest(
                null, EmptyVars(), "server", "database", "sql", true,
                SqlUser: "agent", PasswordEnvVar: passwordVariable);

            var target = TargetResolver.Resolve(request, Profiles);

            Assert.Equal(AuthStrategy.Sql, target.Auth.Strategy);
            Assert.Equal("agent", target.Auth.SqlUser);
            Assert.Equal(passwordVariable, target.Auth.PasswordEnvVar);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, null);
        }
    }

    [Theory]
    [InlineData(null, "PASSWORD", "user")]
    [InlineData("agent", null, "password")]
    public void Rejects_direct_sql_auth_with_missing_fields(string? user, string? passwordVariable, string expected)
    {
        var request = new SqlTargetRequest(
            null, EmptyVars(), "server", "database", "sql", true,
            SqlUser: user, PasswordEnvVar: passwordVariable);

        AssertSafety(request, expected);
    }

    [Fact]
    public void Preserves_direct_trust_server_certificate_flag()
    {
        var request = new SqlTargetRequest(
            null, EmptyVars(), "server", "database", "integrated", true,
            TrustServerCertificate: true);

        Assert.True(TargetResolver.Resolve(request, Profiles).Auth.TrustServerCertificate);
    }

    [Theory]
    [InlineData("agent", null, false)]
    [InlineData(null, "PASSWORD", false)]
    [InlineData(null, null, true)]
    [InlineData("", null, false)]
    [InlineData(null, "", false)]
    public void Rejects_direct_auth_fields_in_profile_mode(string? user, string? passwordVariable, bool trust)
    {
        var request = new SqlTargetRequest(
            "prod-eu",
            new Dictionary<string, string> { ["tenant"] = "acme", ["env"] = "uat" },
            SqlUser: user,
            PasswordEnvVar: passwordVariable,
            TrustServerCertificate: trust);

        var error = Assert.Throws<SqlHarnessSafetyException>(() => TargetResolver.Resolve(request, Profiles));
        Assert.Equal("A profile cannot be combined with direct target options.", error.Message);
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

    [Theory]
    [InlineData("server", "")]
    [InlineData("server", "   ")]
    [InlineData("database", "")]
    [InlineData("database", "   ")]
    [InlineData("auth", "")]
    [InlineData("auth", "   ")]
    public void Rejects_profile_combined_with_supplied_empty_or_whitespace_direct_field(
        string field,
        string value)
    {
        var request = field switch
        {
            "server" => new SqlTargetRequest("prod-eu", EmptyVars(), Server: value),
            "database" => new SqlTargetRequest("prod-eu", EmptyVars(), Database: value),
            "auth" => new SqlTargetRequest("prod-eu", EmptyVars(), Auth: value),
            _ => throw new InvalidOperationException(),
        };

        var error = Assert.Throws<SqlHarnessSafetyException>(() => TargetResolver.Resolve(request, Profiles));
        Assert.Equal("A profile cannot be combined with direct target options.", error.Message);
    }

    [Theory]
    [InlineData("server", "")]
    [InlineData("server", "   ")]
    [InlineData("database", "")]
    [InlineData("database", "   ")]
    [InlineData("auth", "")]
    [InlineData("auth", "   ")]
    public void Classifies_supplied_direct_field_without_unsafe_as_direct_combination(
        string field,
        string value)
    {
        var request = field switch
        {
            "server" => new SqlTargetRequest(null, EmptyVars(), Server: value),
            "database" => new SqlTargetRequest(null, EmptyVars(), Database: value),
            "auth" => new SqlTargetRequest(null, EmptyVars(), Auth: value),
            _ => throw new InvalidOperationException(),
        };

        var error = Assert.Throws<SqlHarnessSafetyException>(() => TargetResolver.Resolve(request, Profiles));
        Assert.Equal("Direct target options require --unsafe-direct.", error.Message);
    }

    [Fact]
    public void Rejects_neither_profile_nor_direct_target() =>
        AssertSafety(new SqlTargetRequest(null, EmptyVars()), "target");

    [Fact]
    public void Rejects_terminal_newline_regex_bypass()
    {
        var request = new SqlTargetRequest("prod-eu", new Dictionary<string, string>
        {
            ["tenant"] = "acme\n", ["env"] = "uat",
        });

        AssertSafety(request, "tenant");
    }

    [Fact]
    public void Rejects_null_vars_dictionary_without_null_reference_exception()
    {
        var error = Assert.Throws<SqlHarnessSafetyException>(() =>
            TargetResolver.Resolve(new SqlTargetRequest("prod-eu", null!), Profiles));

        Assert.Contains("vars", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_null_var_value_without_null_reference_exception()
    {
        var request = new SqlTargetRequest("prod-eu", new Dictionary<string, string>
        {
            ["tenant"] = null!, ["env"] = "uat",
        });

        AssertSafety(request, "tenant");
    }

    private static IReadOnlyDictionary<string, string> EmptyVars() => new Dictionary<string, string>();

    private static void AssertSafety(
        SqlTargetRequest request,
        string message,
        IReadOnlyDictionary<string, TargetProfile>? profiles = null) =>
        Assert.Contains(message, Assert.Throws<SqlHarnessSafetyException>(() =>
            TargetResolver.Resolve(request, profiles ?? Profiles)).Message, StringComparison.OrdinalIgnoreCase);
}
