using System.Reflection;
using System.Text.Json;

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
    public async Task Frozen_scope_isolated_from_source_profile_mutation_and_keeps_target_vars_read_only()
    {
        var sourceVars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenant"] = "^frozen$",
        };
        var sourceProfile = new TargetProfile(
            "source-server.invalid",
            "source-{tenant}",
            sourceVars,
            "integrated");
        var sourceProfiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            [ProfileName] = sourceProfile,
        };

        var scope = McpScope.Create(Options(), sourceProfiles);
        sourceVars["tenant"] = "^changed$";
        sourceProfiles[ProfileName] = sourceProfile with
        {
            Server = "changed-server.invalid",
            Database = "changed-database",
        };
        sourceProfiles["late-profile"] = sourceProfile;

        Assert.NotSame(sourceProfiles, scope.ProfileProvider());
        Assert.Same(scope.Profiles, scope.ProfileProvider());
        Assert.Equal("source-server.invalid", scope.ResolvedTarget.Server);
        Assert.Equal("source-frozen", scope.ResolvedTarget.Database);
        Assert.Equal("^frozen$", scope.Profiles[ProfileName].Vars["tenant"]);
        Assert.DoesNotContain("late-profile", scope.Profiles.Keys);

        var exposedVars = Assert.IsAssignableFrom<IDictionary<string, string>>(scope.TargetRequest.Vars);
        Assert.Throws<NotSupportedException>(() => exposedVars["tenant"] = "changed");
        Assert.Equal(FrozenTenant, scope.TargetRequest.Vars["tenant"]);

        // The module resolves through the scope's frozen provider. Its
        // cross-database safety result proves the original var rule and
        // profile definition remained in use without opening a connection.
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

    // 003/T2 RED: artifact scope enforcement. Fixtures below carry an additive
    // v1 manifest "owner" ({profile, vars, engine, server, database} per
    // plans/003-scope-contract.md); the old mapper ignores it and serves
    // foreign artifacts, so refusal tests fail until T3 enforces the owner
    // in Core before projecting the report.

    private const string ScopeProfileA = "scope-a";
    private const string ScopeProfileB = "scope-b";
    private const string ScopeProfileSameDb = "scope-samedb";
    private const string ScopeProfileOtherVars = "scope-othervars";
    private const string ScopeProfileSameTarget = "scope-sametarget";
    private const string ScopeServerA = "scope-a.invalid";
    private const string ScopeServerB = "scope-b.invalid";
    private const string ScopeServerOther = "scope-other.invalid";
    private const string SharedDatabase = "sharedb";
    private const string ScopeMarker = "scope-marker-7f3a-synthetic";

    private void WriteScopeTargetsFile() => File.WriteAllText(
        _targetsFile,
        "{\""
        + ScopeProfileA + "\": {\"server\": \"" + ScopeServerA + "\", \"database\": \"" + SharedDatabase + "\", \"vars\": {\"tenant\": \"^frozen$\"}, \"auth\": \"integrated\"}, \""
        + ScopeProfileB + "\": {\"server\": \"" + ScopeServerB + "\", \"database\": \"otherdb\", \"vars\": {\"tenant\": \"^frozen$\"}, \"auth\": \"integrated\"}, \""
        + ScopeProfileSameDb + "\": {\"server\": \"" + ScopeServerOther + "\", \"database\": \"" + SharedDatabase + "\", \"vars\": {\"tenant\": \"^frozen$\"}, \"auth\": \"integrated\"}, \""
        + ScopeProfileOtherVars + "\": {\"server\": \"" + ScopeServerA + "\", \"database\": \"" + SharedDatabase + "\", \"vars\": {\"tenant\": \"^other$\"}, \"auth\": \"integrated\"}, \""
        + ScopeProfileSameTarget + "\": {\"server\": \"" + ScopeServerA + "\", \"database\": \"" + SharedDatabase + "\", \"vars\": {\"tenant\": \"^frozen$\"}, \"auth\": \"integrated\"}}");

    private McpScope ScopeFor(string profile, string tenant) => McpScope.Create(
        new McpServerOptions
        {
            Profile = profile,
            Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tenant"] = tenant },
        },
        ProfileStore.Load(_targetsFile));

    private static string ScopeOwnerJson(McpScope scope) =>
        "{\"profile\": " + JsonSerializer.Serialize(scope.TargetRequest.Profile)
        + ", \"vars\": {" + string.Join(",", scope.TargetRequest.Vars.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => JsonSerializer.Serialize(pair.Key) + ": " + JsonSerializer.Serialize(pair.Value))) + "}"
        + ", \"engine\": " + JsonSerializer.Serialize(scope.ResolvedTarget.Engine == SqlEngine.Postgres ? "postgres" : "sqlserver")
        + ", \"server\": " + JsonSerializer.Serialize(scope.ResolvedTarget.Server)
        + ", \"database\": " + JsonSerializer.Serialize(scope.ResolvedTarget.Database) + "}";

    private static SqlHarnessCompareReport ScopeFixtureReport() => new(
        new SqlHarnessTargetIdentityReport(ScopeServerA, SharedDatabase, ScopeServerA, SharedDatabase, "profile"),
        5, 10, true,
        new CompareVariantReport(
            "baseline",
            new CompareDistribution(1, 2, 3),
            new CompareDistribution(10, 20, 30),
            new CompareDistribution(3, 4, 5),
            new Dictionary<string, long>(),
            [new CompareOperatorReport(1, "Index Seek", "Clients", false, false, false)],
            [ScopeMarker]),
        new CompareVariantReport(
            "candidate",
            new CompareDistribution(1, 2, 3),
            new CompareDistribution(4, 5, 6),
            new CompareDistribution(3, 4, 5),
            new Dictionary<string, long>(),
            [new CompareOperatorReport(1, "Index Seek", "Clients", false, false, false)],
            []),
        null);

    private static string WriteScopedArtifact(string id, string? ownerJson)
    {
        var root = SqlHarnessPaths.CompareDir;
        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        var manifest = "{\"manifestVersion\": 1, \"artifactKind\": \"compare\", \"reportFile\": \"report.json\", \"sections\": [\"summary\", \"metrics\", \"operators\"]"
            + (ownerJson is null ? string.Empty : ", \"owner\": " + ownerJson) + "}";
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest);
        File.WriteAllText(
            Path.Combine(directory, "report.json"),
            JsonSerializer.Serialize(ScopeFixtureReport(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return id;
    }

    private static Exception AssertRefusedBeforeProjection(Func<object> read)
    {
        var refused = Assert.ThrowsAny<Exception>(read);
        Assert.DoesNotContain(ScopeMarker, refused.Message, StringComparison.Ordinal);
        return refused;
    }

    [Fact]
    public void Own_scope_reads_own_artifact()
    {
        WriteScopeTargetsFile();
        var scope = ScopeFor(ScopeProfileA, "frozen");
        var id = WriteScopedArtifact("own-scope-artifact", ScopeOwnerJson(scope));

        Assert.NotNull(McpOperationMapper.ReadArtifactSection(scope, id, "summary"));
        Assert.NotNull(McpOperationMapper.ReadArtifactSection(scope, id, "metrics"));
    }

    [Fact]
    public void Foreign_scope_is_refused_before_projection()
    {
        WriteScopeTargetsFile();
        var owner = ScopeFor(ScopeProfileA, "frozen");
        var foreign = ScopeFor(ScopeProfileB, "frozen");
        var id = WriteScopedArtifact("foreign-scope-artifact", ScopeOwnerJson(owner));

        AssertRefusedBeforeProjection(() => McpOperationMapper.ReadArtifactSection(foreign, id, "summary"));
    }

    [Fact]
    public void Same_database_name_on_another_server_is_refused()
    {
        WriteScopeTargetsFile();
        var owner = ScopeFor(ScopeProfileA, "frozen");
        var sameDb = ScopeFor(ScopeProfileSameDb, "frozen");
        Assert.Equal(SharedDatabase, sameDb.ResolvedTarget.Database);
        Assert.NotEqual(owner.ResolvedTarget.Server, sameDb.ResolvedTarget.Server);
        var id = WriteScopedArtifact("same-db-other-server", ScopeOwnerJson(owner));

        AssertRefusedBeforeProjection(() => McpOperationMapper.ReadArtifactSection(sameDb, id, "metrics"));
    }

    [Fact]
    public void Different_vars_are_refused()
    {
        WriteScopeTargetsFile();
        var owner = ScopeFor(ScopeProfileA, "frozen");
        var otherVars = ScopeFor(ScopeProfileOtherVars, "other");
        Assert.Equal(owner.ResolvedTarget.Server, otherVars.ResolvedTarget.Server);
        Assert.Equal(owner.ResolvedTarget.Database, otherVars.ResolvedTarget.Database);
        var id = WriteScopedArtifact("other-vars-artifact", ScopeOwnerJson(owner));

        AssertRefusedBeforeProjection(() => McpOperationMapper.ReadArtifactSection(otherVars, id, "summary"));
    }

    [Fact]
    public void Different_profile_name_only_is_refused()
    {
        WriteScopeTargetsFile();
        var owner = ScopeFor(ScopeProfileA, "frozen");
        var sameTarget = ScopeFor(ScopeProfileSameTarget, "frozen");
        Assert.Equal(owner.ResolvedTarget.Server, sameTarget.ResolvedTarget.Server);
        Assert.Equal(owner.ResolvedTarget.Database, sameTarget.ResolvedTarget.Database);
        var id = WriteScopedArtifact("same-target-other-profile", ScopeOwnerJson(owner));

        AssertRefusedBeforeProjection(() => McpOperationMapper.ReadArtifactSection(sameTarget, id, "summary"));
    }

    [Fact]
    public void Legacy_artifact_without_owner_is_refused_by_mapper_but_readable_via_cli()
    {
        WriteScopeTargetsFile();
        var scope = ScopeFor(ScopeProfileA, "frozen");
        var id = WriteScopedArtifact("legacy-no-owner", ownerJson: null);

        AssertRefusedBeforeProjection(() => McpOperationMapper.ReadArtifactSection(scope, id, "summary"));
        Assert.NotNull(ArtifactReader.ReadSection(SqlHarnessPaths.CompareDir, id, "summary"));
    }

    [Fact]
    public void Mapper_does_not_self_grant_access_by_name()
    {
        WriteScopeTargetsFile();
        var scope = ScopeFor(ScopeProfileA, "frozen");
        // The report itself names the scope database and the id is known, yet
        // without owner metadata the mapper must still refuse: names and ids
        // are not authority.
        var id = WriteScopedArtifact("named-legacy-artifact", ownerJson: null);

        AssertRefusedBeforeProjection(() => McpOperationMapper.ReadArtifactSection(scope, id, "operators"));
        Assert.NotNull(ArtifactReader.ReadSection(SqlHarnessPaths.CompareDir, id, "operators"));
    }
}