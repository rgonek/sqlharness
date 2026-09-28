using System.Reflection;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Mcp.Tests;

[CollectionDefinition("McpScopeHome", DisableParallelization = true)]
public sealed class McpScopeHomeCollection;

/// <summary>
/// T2 frozen-scope contract: the target resolves once at startup, a later
/// profile-file change cannot redirect calls, the scoped provider replaces
/// the per-call global store read, and no reload/switch-target surface
/// exists. Module execution tests redirect SQLHARNESS_HOME to a synthetic
/// directory; user profiles are never touched.
/// </summary>
[Collection("McpScopeHome")]
public sealed class McpScopeTests : IDisposable
{
    private const string ProfileName = "mcp-t2";
    private const string FrozenDatabase = "report-frozen";
    private const string ChangedDatabase = "report-changed";
    private const string FrozenTenant = "frozen";
    private const string CrossDatabaseSql = "SELECT * FROM [otherdb].dbo.T";

    private readonly string _home;
    private readonly string? _savedHome;
    private readonly string _targetsFile;

    public McpScopeTests()
    {
        _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
        _home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-t2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
        _targetsFile = Path.Combine(_home, "targets.json");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static McpServerOptions Options() => new()
    {
        Profile = ProfileName,
        Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tenant"] = FrozenTenant,
        },
    };

    private void WriteTargetsFile(string database, string tenantPattern) =>
        File.WriteAllText(_targetsFile, TargetsJson(database, tenantPattern));

    private static string TargetsJson(string database, string tenantPattern) =>
        "{\"" + ProfileName + "\": {\"server\": \"mcp-unreachable.invalid\", \"database\": \"" + database + "\", \"vars\": {\"tenant\": \"" + tenantPattern + "\"}, \"auth\": \"integrated\"}}";

    [Fact]
    public void Frozen_scope_survives_profile_file_change()
    {
        WriteTargetsFile(FrozenDatabase, "^frozen$");
        var scope = McpScope.Create(Options(), ProfileStore.Load(_targetsFile));
        Assert.Equal(FrozenDatabase, scope.ResolvedTarget.Database);

        // Change the profile file before the second call: the new version no
        // longer accepts the startup vars at all.
        WriteTargetsFile(ChangedDatabase, "^changed$");
        var reread = ProfileStore.Load(_targetsFile);
        Assert.Equal(ChangedDatabase, reread[ProfileName].Database);
        Assert.Equal("^changed$", reread[ProfileName].Vars["tenant"]);

        // The second call through the frozen scope still targets the old goal.
        var again = TargetResolver.Resolve(scope.TargetRequest, scope.Profiles);
        Assert.Equal(FrozenDatabase, again.Database);
        Assert.Same(scope.Profiles, scope.ProfileProvider());
    }

    [Fact]
    public async Task Scoped_module_uses_frozen_snapshot_instead_of_rereading_the_file()
    {
        WriteTargetsFile(FrozenDatabase, "^frozen$");
        var scope = McpScope.Create(Options(), ProfileStore.Load(_targetsFile));
        WriteTargetsFile(ChangedDatabase, "^changed$");

        // The cross-database SQL is rejected before any connection attempt.
        // Against the frozen snapshot the request resolves and safety fires;
        // against the changed file the same request would die in var
        // validation. The observed outcome tells which snapshot the module
        // used, fully offline.
        var module = scope.CreateModule();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var outcome = await module.ExecuteAsync(
            new SqlHarnessQueryOperation(scope.TargetRequest, CrossDatabaseSql, [], 5, 50, false, null),
            cts.Token);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Contains("safety rejection", outcome.SafeError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("validation rule", outcome.SafeError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scoped_provider_is_read_exactly_once_not_per_call()
    {
        WriteTargetsFile(FrozenDatabase, "^frozen$");
        int reads = 0;
        Func<IReadOnlyDictionary<string, TargetProfile>> loader = () =>
        {
            reads++;
            return ProfileStore.Load(_targetsFile);
        };

        var scope = McpScope.Create(Options(), loader);
        _ = scope.ProfileProvider();
        _ = scope.ProfileProvider();
        var module = scope.CreateModule();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gain = await module.ExecuteAsync(
            new SqlHarnessGainOperation(),
            cts.Token);

        Assert.Equal(SqlHarnessExitCode.Success, gain.ExitCode);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Scope_exposes_no_reload_or_switch_target_surface()
    {
        var banned = new[] { "reload", "switch", "refresh", "reset", "update" };
        var members = typeof(McpScope).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
        foreach (var member in members)
        {
            foreach (var word in banned)
                Assert.DoesNotContain(word, member.Name, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(typeof(McpScope).IsSealed);
    }

    [Fact]
    public void Input_roots_default_to_empty_and_reject_non_absolute_or_missing_dirs()
    {
        WriteTargetsFile(FrozenDatabase, "^frozen$");
        var frozen = ProfileStore.Load(_targetsFile);

        Assert.Empty(McpScope.Create(Options(), frozen).InputRoots);

        Assert.Throws<McpStartupException>(() => McpScope.Create(
            new McpServerOptions { Profile = ProfileName, InputRoots = ["relative/dir"] },
            frozen));
        Assert.Throws<McpStartupException>(() => McpScope.Create(
            new McpServerOptions
            {
                Profile = ProfileName,
                InputRoots = [Path.Combine(_home, "no-such-dir")],
            },
            frozen));

        var existing = Path.Combine(_home, "inputs");
        Directory.CreateDirectory(existing);
        var scoped = McpScope.Create(
            new McpServerOptions
            {
                Profile = ProfileName,
                Vars = Options().Vars,
                InputRoots = [existing],
            },
            frozen);
        Assert.Equal([Path.GetFullPath(existing)], scoped.InputRoots);
    }

    [Fact]
    public void Startup_failures_carry_no_profile_or_var_values()
    {
        WriteTargetsFile(FrozenDatabase, "^frozen$");
        var frozen = ProfileStore.Load(_targetsFile);

        var failure = Assert.Throws<McpStartupException>(() => McpScope.Create(
            new McpServerOptions
            {
                Profile = "no-such-profile",
                Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["tenant"] = "t2-marker-secret-77aa",
                },
            },
            frozen));

        // The public message is generic. The inner chain keeps the Core
        // diagnostic for debugging, but McpHost never prints it: startup
        // errors use fixed literals on stderr (see McpStartupTests).
        Assert.DoesNotContain("t2-marker-secret-77aa", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no-such-profile", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FrozenDatabase, failure.Message, StringComparison.Ordinal);
    }
}
