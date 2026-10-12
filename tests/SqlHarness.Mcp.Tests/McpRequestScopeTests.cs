using System.Text.Json;

using ModelContextProtocol.Protocol;

using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp.Tests;

public sealed class McpRequestScopeTests
{
    private const string Tenant = "synthetic-tenant";
    private static readonly string[] Allowed = ["sample-a", "sample-b"];

    private static Dictionary<string, TargetProfile> Profiles()
    {
        return new(StringComparer.Ordinal)
        {
            ["sample-a"] = Profile("server-a", "database-a"),
            ["sample-b"] = Profile("server-b", "database-b"),
        };
    }

    private static TargetProfile Profile(string server, string database) => new(
        server,
        database,
        new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = "^[a-z-]+$" },
        "integrated");

    private static McpServerOptions Options(params string[] allowed) => new()
    {
        RequestScope = true,
        AllowedProfiles = allowed,
    };

    private static McpRequestScope Request(string profile, IReadOnlyDictionary<string, string>? vars = null) =>
        new(profile, vars ?? new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = Tenant });

    private static string Text(CallToolResult result) => Assert.Single(result.Content.OfType<TextContentBlock>()).Text;

    [Fact]
    public void Request_startup_is_offline_needs_no_profile_vars_and_loads_once()
    {
        var reads = 0;
        var context = McpProcessContext.Create(Options(Allowed), () =>
        {
            reads++;
            return Profiles();
        });

        Assert.Equal(1, reads);
        Assert.Null(context.FixedScope);
        Assert.Equal(Allowed, context.AllowedProfiles);
        var scope = context.ResolveScope(Request("sample-a"));
        Assert.Equal("database-a", scope.ResolvedTarget.Database);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Request_scopes_resolve_distinct_targets_and_owners()
    {
        var context = McpProcessContext.Create(Options(Allowed), Profiles);

        var a = context.ResolveScope(Request("sample-a"));
        var b = context.ResolveScope(Request("sample-b"));

        Assert.Equal("server-a", a.ResolvedTarget.Server);
        Assert.Equal("database-a", a.ResolvedTarget.Database);
        Assert.Equal("server-b", b.ResolvedTarget.Server);
        Assert.Equal("database-b", b.ResolvedTarget.Database);
        Assert.NotEqual(a.Owner, b.Owner);
        Assert.NotSame(a, b);
        Assert.NotSame(a.CreateModule(), b.CreateModule());
    }

    [Fact]
    public void Request_scope_requires_allowlisted_profile_and_exact_valid_vars()
    {
        var context = McpProcessContext.Create(Options("sample-a"), Profiles);

        Assert.Throws<McpStartupException>(() => context.ResolveScope(null));
        Assert.Throws<McpStartupException>(() => context.ResolveScope(Request("sample-b")));
        Assert.Throws<McpStartupException>(() => context.ResolveScope(Request("unknown-profile")));
        Assert.Throws<McpStartupException>(() => context.ResolveScope(Request("sample-a", new Dictionary<string, string>())));
        Assert.Throws<McpStartupException>(() => context.ResolveScope(Request("sample-a", new Dictionary<string, string>
        {
            ["tenant"] = Tenant,
            ["extra"] = "marker-secret",
        })));
        var invalid = Assert.Throws<McpStartupException>(() => context.ResolveScope(Request("sample-a", new Dictionary<string, string>
        {
            ["tenant"] = "marker.secret",
        })));
        Assert.DoesNotContain("marker.secret", invalid.Message, StringComparison.Ordinal);
        Assert.Contains("sample-a", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_handler_names_missing_vars_and_redacts_selected_profile_password()
    {
        const string secret = "request-scope-missing-secret-3c81";
        const string passwordVariable = "SQLHARNESS_MCP_REQUEST_SCOPE_MISSING_PASSWORD";
        Environment.SetEnvironmentVariable(passwordVariable, secret);
        try
        {
            var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                [secret] = Profile("server-a", "database-a") with
                {
                    Auth = "sql",
                    SqlUser = "test-user",
                    PasswordEnvVar = passwordVariable,
                },
            };
            var context = McpProcessContext.Create(Options(secret), () => profiles);
            var handlers = new McpRequestToolHandlers(context);

            var result = await handlers.QueryAsync(
                null!,
                new McpRequestScopeArgument(secret, new Dictionary<string, string>()),
                "SELECT 1");

            Assert.True(result.IsError == true);
            using var document = JsonDocument.Parse(Text(result));
            var message = document.RootElement.GetProperty("error").GetProperty("message").GetString();
            Assert.Contains("Profile '[REDACTED]': Missing required profile vars: tenant.", message, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, null);
        }
    }

    [Fact]
    public async Task Request_handler_names_auth_reason_and_redacts_selected_profile_password()
    {
        const string secret = "request-scope-auth-secret-6d34";
        const string passwordVariable = "SQLHARNESS_MCP_REQUEST_SCOPE_AUTH_PASSWORD";
        Environment.SetEnvironmentVariable(passwordVariable, secret);
        try
        {
            var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                [secret] = Profile("server-a", "database-a") with
                {
                    Auth = "invalid-auth",
                    PasswordEnvVar = passwordVariable,
                },
            };
            var context = McpProcessContext.Create(Options(secret), () => profiles);
            var handlers = new McpRequestToolHandlers(context);

            var result = await handlers.QueryAsync(null!, new McpRequestScopeArgument(secret,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = Tenant }), "SELECT 1");

            Assert.True(result.IsError == true);
            using var document = JsonDocument.Parse(Text(result));
            var message = document.RootElement.GetProperty("error").GetProperty("message").GetString();
            Assert.Contains("authentication settings are invalid", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("authentication strategy is unknown", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[REDACTED]", message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, null);
        }
    }

    [Fact]
    public async Task Request_handler_keeps_malformed_scope_errors_generic()
    {
        var context = McpProcessContext.Create(Options("sample-a"), Profiles);
        var handlers = new McpRequestToolHandlers(context);

        var result = await handlers.QueryAsync(null!, new McpRequestScopeArgument("sample-a", null!), "SELECT 1");

        Assert.True(result.IsError == true);
        using var document = JsonDocument.Parse(Text(result));
        Assert.Equal(
            "The MCP request scope is invalid.",
            document.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Request_scope_rejects_case_colliding_var_keys()
    {
        var context = McpProcessContext.Create(Options("sample-a"), Profiles);
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenant"] = Tenant,
            ["Tenant"] = Tenant,
        };

        Assert.Throws<McpStartupException>(() => context.ResolveScope(Request("sample-a", vars)));
    }

    [Fact]
    public void Request_startup_deep_copies_profiles_and_nested_vars()
    {
        var profiles = Profiles();
        var original = profiles["sample-a"];
        var originalVars = (Dictionary<string, string>)original.Vars;
        var context = McpProcessContext.Create(Options("sample-a"), () => profiles);
        originalVars["tenant"] = "^changed$";
        profiles["sample-a"] = Profile("changed-server", "changed-database");

        var scope = context.ResolveScope(Request("sample-a"));

        Assert.Equal("server-a", scope.ResolvedTarget.Server);
        Assert.Equal("database-a", scope.ResolvedTarget.Database);
    }

    [Fact]
    public void Request_startup_requires_nonempty_unique_sql_server_allowlist()
    {
        Assert.Throws<McpStartupException>(() => McpProcessContext.Create(Options(), Profiles));
        Assert.Throws<McpStartupException>(() => McpProcessContext.Create(Options("sample-a", "sample-a"), Profiles));
        Assert.Throws<McpStartupException>(() => McpProcessContext.Create(Options("sample-a", "unknown"), Profiles));

        var postgres = Profiles();
        postgres["sample-a"] = Profile("server-a", "database-a") with { Engine = "postgres" };
        Assert.Throws<McpStartupException>(() => McpProcessContext.Create(Options("sample-a"), () => postgres));
    }

    [Fact]
    public void Startup_rejects_incompatible_scope_mode_options()
    {
        Assert.Throws<McpStartupException>(() => McpProcessContext.Create(
            new McpServerOptions { RequestScope = true, Profile = "sample-a", AllowedProfiles = ["sample-a"] },
            Profiles));
        Assert.Throws<McpStartupException>(() => McpProcessContext.Create(
            new McpServerOptions
            {
                RequestScope = true,
                Vars = new Dictionary<string, string> { ["tenant"] = Tenant },
                AllowedProfiles = ["sample-a"],
            },
            Profiles));
        Assert.Throws<McpStartupException>(() => McpProcessContext.Create(
            new McpServerOptions { Profile = "sample-a", AllowedProfiles = ["sample-a"] },
            Profiles));
    }
}