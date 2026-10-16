using System.Data;
using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Core;
using SqlHarness.Core.Dialect;
using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Cli;

[Collection(SqlHarnessHomeCollection.Name)]
public sealed class CapabilitiesCommandTests
{
    [Fact]
    public async Task Capabilities_reports_build_engines_limits_and_command_support_without_dispatch()
    {
        var module = new RecordingModule();
        var output = new StringWriter();
        var exitCode = await SqlHarnessCli.Create(module, output).RunAsync(["capabilities", "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Empty(module.Operations);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal(5, root.GetProperty("contractVersion").GetInt32());
        var mutations = root.GetProperty("mutations");
        Assert.Equal("never", mutations.GetProperty("defaultMode").GetString());
        Assert.Equal("click", mutations.GetProperty("verification").GetString());
        Assert.Equal(120, mutations.GetProperty("approvalTimeoutSeconds").GetInt32());
        var context = root.GetProperty("compareContext");
        Assert.True(context.GetProperty("requireContext").GetBoolean());
        Assert.Equal("whole", context.GetProperty("wholeScope").GetString());
        Assert.Equal("(sessionId, subject, scope)", context.GetProperty("versionIdentity").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("buildId").GetString()));
        Assert.Contains(root.GetProperty("commands").EnumerateArray(), command => command.GetProperty("name").GetString() == "query");
        Assert.DoesNotContain(root.GetProperty("commands").EnumerateArray(), command => command.GetProperty("name").GetString() is "validate" or "ping" or "schema" or "counts" or "space" or "qstop" or "indexes");
        var engines = root.GetProperty("engines").EnumerateArray().ToArray();
        Assert.Equal(["postgres", "sqlserver"], engines.Select(engine => engine.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));
        Assert.All(engines, engine => Assert.Equal(["name", "summary"], engine.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
        Assert.False(root.TryGetProperty("profile", out _));
        Assert.False(root.TryGetProperty("parameterTypes", out _));
        Assert.Contains("agentOutputBytes", root.GetProperty("limits").EnumerateObject().Select(property => property.Name));
        var limits = root.GetProperty("limits");
        var journalStorage = limits.GetProperty("activityJournalStorage");
        Assert.Equal("credential-redacted error messages are stored by default", journalStorage.GetProperty("errorMessages").GetString());
        Assert.Equal("journal.storeSensitive", journalStorage.GetProperty("sqlTextAndPlans").GetString());
        Assert.Equal(1000000, limits.GetProperty("comparisonRowCapPerRun").GetProperty("max").GetInt32());
        Assert.Equal(2000000, limits.GetProperty("comparisonUniqueFingerprintBudgetPerCell").GetProperty("max").GetInt32());
        foreach (var engineOnly in new[] { "sessionTempStatements" })
            Assert.False(limits.TryGetProperty(engineOnly, out _), engineOnly);
    }

    [Fact]
    public void Contract_version_is_pinned()
    {
        Assert.Equal(5, SqlHarnessCapabilitiesProvider.ContractVersion);
        Assert.Equal(5, SqlHarnessCapabilitiesProvider.Get().ContractVersion);
    }

    [Fact]
    public async Task Capabilities_reports_active_compare_context_policy_without_off_mode_instructions()
    {
        var module = new RecordingModule();
        var output = new StringWriter();
        var exitCode = await SqlHarnessCli.Create(module, output, compareConfig: new CompareConfig { RequireContext = false })
            .RunAsync(["capabilities", "--json"]);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var context = document.RootElement.GetProperty("compareContext");
        Assert.False(context.GetProperty("requireContext").GetBoolean());
        Assert.Single(context.EnumerateObject());
    }

    [Theory]
    [InlineData("sqlserver-profile", "sqlserver", "postgres", true)]
    [InlineData("pg-profile", "postgres", "sqlserver", false)]
    public async Task Capabilities_with_a_profile_carries_only_that_engines_details(string profile, string engine, string other, bool sqlServer)
    {
        using var home = new TempProfiles();
        var module = new RecordingModule();
        var output = new StringWriter();
        var exitCode = await SqlHarnessCli.Create(module, output).RunAsync(["capabilities", profile, "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Empty(module.Operations);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal(profile, root.GetProperty("profile").GetProperty("name").GetString());
        Assert.Equal(engine, root.GetProperty("profile").GetProperty("engine").GetString());
        Assert.Equal("never", root.GetProperty("profile").GetProperty("mutationMode").GetString());
        Assert.False(root.GetProperty("profile").GetProperty("writerConfigured").GetBoolean());
        Assert.Equal([engine], root.GetProperty("engines").EnumerateArray().Select(e => e.GetProperty("name").GetString()!));
        var types = root.GetProperty("parameterTypes").EnumerateArray().Select(t => t.GetString()!).ToArray();
        Assert.Contains("nvarchar(n)", types);
        Assert.Equal(sqlServer, types.Contains("hierarchyid"));
        var limits = root.GetProperty("limits");
        var temp = limits.GetProperty("sessionTempStatements");
        Assert.Equal(JsonValueKind.Array, temp.ValueKind);
        Assert.Equal(sqlServer, temp.GetRawText().Contains("#temp", StringComparison.Ordinal));
        Assert.DoesNotContain("\"name\": \"" + other + "\"", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Mutation_capability_reports_only_effective_policy_and_writer_presence()
    {
        var config = new SqlHarnessConfig { Mutations = new MutationsConfig { DefaultMode = "operator", Verification = "click", ApprovalTimeoutSeconds = 180 } };
        var profile = new TargetProfile("host", "db", new Dictionary<string, string>(), "integrated");
        var enabledProfile = profile with { Mutations = new ProfileMutations("yolo", new MutationWriter(SameAsReader: true)) };

        using var global = JsonDocument.Parse(Serialize(SqlHarnessCapabilitiesProvider.Get(mutationsConfig: config.Mutations, dashboardReachable: true)));
        var globalPolicy = global.RootElement.GetProperty("mutations");
        Assert.Equal("operator", globalPolicy.GetProperty("defaultMode").GetString());
        Assert.Equal(180, globalPolicy.GetProperty("approvalTimeoutSeconds").GetInt32());
        Assert.True(globalPolicy.GetProperty("dashboardReachable").GetBoolean());

        using var scoped = JsonDocument.Parse(Serialize(SqlHarnessCapabilitiesProvider.Get(SqlEngine.SqlServer, "p", targetProfile: profile, mutationsConfig: config.Mutations)));
        Assert.Equal("operator", scoped.RootElement.GetProperty("profile").GetProperty("mutationMode").GetString());
        Assert.False(scoped.RootElement.GetProperty("profile").GetProperty("writerConfigured").GetBoolean());

        using var enabled = JsonDocument.Parse(Serialize(SqlHarnessCapabilitiesProvider.Get(SqlEngine.SqlServer, "p", targetProfile: enabledProfile, mutationsConfig: config.Mutations)));
        Assert.Equal("yolo", enabled.RootElement.GetProperty("profile").GetProperty("mutationMode").GetString());
        Assert.True(enabled.RootElement.GetProperty("profile").GetProperty("writerConfigured").GetBoolean());
        Assert.DoesNotContain("sameAsReader", enabled.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capabilities_dashboard_reachability_tracks_the_live_home_lock_without_exposing_its_token()
    {
        using var home = new TempProfiles();
        using var held = DashboardLock.TryAcquire(home.HomePath)!;
        held.Publish(new DashboardEndpoint(Environment.ProcessId, null, 47831, "capability-test-token"));
        var output = new StringWriter();

        var exitCode = await SqlHarnessCli.Create(new RecordingModule(), output).RunAsync(["capabilities", "--json"]);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.True(document.RootElement.GetProperty("mutations").GetProperty("dashboardReachable").GetBoolean());
        Assert.DoesNotContain("capability-test-token", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capabilities_with_an_unknown_profile_exits_2_naming_it()
    {
        using var home = new TempProfiles();
        var output = new StringWriter();
        var exitCode = await SqlHarnessCli.Create(new RecordingModule(), output).RunAsync(["capabilities", "nope-profile", "--json"]);

        AssertProfileError(exitCode, output, "profile_unknown", "nope-profile");
        Assert.Contains("Configured profiles: pg-profile, sqlserver-profile.", ErrorMessage(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capabilities_with_a_missing_profiles_file_is_an_unknown_profile_json_error()
    {
        using var home = new TempProfiles();
        File.Delete(Path.Combine(Environment.GetEnvironmentVariable("SQLHARNESS_HOME")!, "targets.json"));
        var output = new StringWriter();
        var exitCode = await SqlHarnessCli.Create(new RecordingModule(), output).RunAsync(["capabilities", "any-profile", "--json"]);

        AssertProfileError(exitCode, output, "profile_unknown", "any-profile");
        var message = ErrorMessage(output);
        Assert.Contains("No profiles are configured in", message, StringComparison.Ordinal);
        Assert.Contains("targets.json", message, StringComparison.Ordinal);
    }

    private static string ErrorMessage(StringWriter output)
    {
        using var document = JsonDocument.Parse(output.ToString());
        return document.RootElement.GetProperty("error").GetProperty("message").GetString()!;
    }

    private static void AssertProfileError(int exitCode, StringWriter output, string code, string profile)
    {
        Assert.Equal((int)SqlHarnessExitCode.Safety, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("result").ValueKind);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Contains(profile, error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capabilities_with_an_unreadable_profiles_file_exits_2_naming_the_profile_and_reason()
    {
        using var home = new TempProfiles("{ not json");
        var output = new StringWriter();
        var exitCode = await SqlHarnessCli.Create(new RecordingModule(), output).RunAsync(["capabilities", "any-profile", "--json"]);

        AssertProfileError(exitCode, output, "profile_unavailable", "any-profile");
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Contains("could not be resolved", document.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capabilities_without_json_explains_required_output_mode()
    {
        var output = new StringWriter();
        var exitCode = await SqlHarnessCli.Create(new RecordingModule(), output).RunAsync(["capabilities"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exitCode);
        Assert.Contains("capabilities requires --json", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capabilities_json_discloses_the_versioned_static_analysis_boundary()
    {
        // 009/T1 red witness: capabilities carries the same static-analysis
        // boundary clients see on validate (kind, contract version, hidden
        // effects not verified, unknown catalog/permission state).
        var output = new StringWriter();
        var exitCode = await SqlHarnessCli.Create(new RecordingModule(), output).RunAsync(["capabilities", "--json"]);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var safety = document.RootElement.GetProperty("safetyAnalysis");
        Assert.Equal("static-visible-effects", safety.GetProperty("analysisKind").GetString());
        Assert.Equal(1, safety.GetProperty("analysisContractVersion").GetInt32());
        Assert.False(safety.GetProperty("hiddenEffectsVerified").GetBoolean());
        Assert.Equal("unknown", safety.GetProperty("objectAndPermissionStatus").GetString());
    }

    [Fact]
    public async Task Capabilities_safety_analysis_matches_the_shared_validate_boundary()
    {
        // 009/T3: CLI capabilities and CLI validate disclose one contract —
        // every value comes from the shared SqlSafetyAnalysis source.
        var output = new StringWriter();
        var exitCode = await SqlHarnessCli.Create(new RecordingModule(), output).RunAsync(["capabilities", "--json"]);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var safety = document.RootElement.GetProperty("safetyAnalysis");
        Assert.Equal(SqlSafetyAnalysis.AnalysisKind, safety.GetProperty("analysisKind").GetString());
        Assert.Equal(SqlSafetyAnalysis.ContractVersion, safety.GetProperty("analysisContractVersion").GetInt32());
        Assert.Equal(SqlSafetyAnalysis.HiddenEffectsVerified, safety.GetProperty("hiddenEffectsVerified").GetBoolean());
        Assert.Equal(SqlSafetyAnalysis.ObjectAndPermissionStatus, safety.GetProperty("objectAndPermissionStatus").GetString());
    }

    [Fact]
    public void Capabilities_lists_mcp_not_dashboard_and_sized_parameter_types()
    {
        var caps = SqlHarnessCapabilitiesProvider.Get();
        Assert.Contains(caps.Commands, c => c.Name == "mcp" && c.Description.Contains("MCP client", StringComparison.Ordinal));
        Assert.DoesNotContain(caps.Commands, c => c.Name == "dashboard");
        foreach (var engine in new[] { SqlEngine.SqlServer, SqlEngine.Postgres })
            Assert.All(new[] { "nvarchar(n)", "varchar(n)", "char(n)", "nchar(n)", "varbinary(n)", "binary(n)" }, t => Assert.Contains(t, SqlHarnessCapabilitiesProvider.Get(engine, "p").ParameterTypes!));
    }

    // Distinctive tokens only: the wording belongs to the registry entry, the facts to these tests.
    private static readonly string[] PostgresOnlyTokens = ["READ ONLY", "TEMP setup", "least-privilege", "sslMode", "verify-full", "user functions"];
    private static readonly string[] SqlServerOnlyTokens = ["#temp"];

    private static string Serialize(SqlHarnessCapabilities capabilities) => JsonSerializer.Serialize(capabilities, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void Engine_neutral_discovery_carries_no_engine_specific_guidance()
    {
        var caps = SqlHarnessCapabilitiesProvider.Get();
        var commands = string.Join('\n', caps.Commands.Select(c => c.Description));
        var targetIdentity = caps.Limits["targetIdentity"].ToString()!;

        foreach (var token in PostgresOnlyTokens.Concat(SqlServerOnlyTokens))
        {
            Assert.DoesNotContain(token, commands, StringComparison.Ordinal);
            Assert.DoesNotContain(token, targetIdentity, StringComparison.Ordinal);
        }
        Assert.Null(caps.Profile);
        Assert.Null(caps.EngineGuidance);
        Assert.DoesNotContain("\"engineGuidance\":", Serialize(caps), StringComparison.Ordinal);
    }

    [Fact]
    public void Postgres_scope_carries_postgres_guidance_and_no_sqlserver_setup_rule()
    {
        var caps = SqlHarnessCapabilitiesProvider.Get(SqlEngine.Postgres, "pg-profile");
        var guidance = Assert.IsType<SqlHarnessEngineGuidance>(caps.EngineGuidance);
        var all = Serialize(caps);

        Assert.Contains("TEMP", guidance.Setup, StringComparison.Ordinal);
        Assert.Contains("least-privilege", guidance.Setup, StringComparison.Ordinal);
        Assert.Contains("least-privilege", guidance.Query, StringComparison.Ordinal);
        Assert.Contains("READ ONLY", guidance.Query, StringComparison.Ordinal);
        Assert.Contains("sslMode", caps.Limits["targetIdentity"].ToString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("#temp", all, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlserver", all, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServer_scope_carries_sqlserver_guidance_and_no_postgres_facts()
    {
        var caps = SqlHarnessCapabilitiesProvider.Get(SqlEngine.SqlServer, "sql-profile");
        var guidance = Assert.IsType<SqlHarnessEngineGuidance>(caps.EngineGuidance);
        var all = Serialize(caps);

        Assert.Contains("#temp", guidance.Setup, StringComparison.Ordinal);
        foreach (var token in PostgresOnlyTokens)
            Assert.DoesNotContain(token, all, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres", all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scoped_documents_keep_commands_and_parameter_types_and_limits_of_the_neutral_document()
    {
        var neutral = SqlHarnessCapabilitiesProvider.Get();
        foreach (var engine in Enum.GetValues<SqlEngine>())
        {
            var scoped = SqlHarnessCapabilitiesProvider.Get(engine, "any-profile");
            Assert.Equal(neutral.Commands, scoped.Commands);
            // The selected engine's server-identity fact replaces the generic one in place; no second home.
            var targetIdentity = scoped.Limits["targetIdentity"].ToString()!;
            Assert.Equal(engine == SqlEngine.Postgres, targetIdentity.Contains("sslMode", StringComparison.Ordinal));
            if (engine == SqlEngine.SqlServer)
                Assert.Equal(neutral.Limits["targetIdentity"].ToString(), targetIdentity);
            Assert.DoesNotContain("targetServer", Serialize(scoped), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(neutral.Limits["queryMaxRows"], scoped.Limits["queryMaxRows"]);
            Assert.Contains("nvarchar(n)", scoped.ParameterTypes!);
        }
    }

    [Fact]
    public void Every_registered_engine_has_one_guidance_entry()
    {
        foreach (var engine in Enum.GetValues<SqlEngine>())
        {
            var guidance = SqlHarnessCapabilitiesProvider.GuidanceFor(engine);
            Assert.NotNull(guidance.Query);
            Assert.False(string.IsNullOrWhiteSpace(guidance.Setup), engine.ToString());
        }
        Assert.Throws<InvalidOperationException>(() => SqlHarnessCapabilitiesProvider.GuidanceFor((SqlEngine)999));
        Assert.Throws<InvalidOperationException>(() => SqlHarnessCapabilitiesProvider.Get((SqlEngine)999, "p"));
    }

    [Fact]
    public void Profile_without_engine_defaults_to_sqlserver_and_unknown_engine_is_rejected()
    {
        Assert.Equal("sqlserver", SqlHarnessCapabilitiesProvider.Get("p", null).Profile!.Engine);
        Assert.Equal("sqlserver", SqlHarnessCapabilitiesProvider.Get("p", "").Profile!.Engine);
        Assert.Equal("postgres", SqlHarnessCapabilitiesProvider.Get("p", "Postgres").Profile!.Engine);
        Assert.Throws<SqlHarnessSafetyException>(() => SqlHarnessCapabilitiesProvider.Get("p", "oracle"));
    }

    [Fact]
    public void Query_measure_and_compare_capabilities_describe_offline_dry_run_surface()
    {
        var commands = SqlHarnessCapabilitiesProvider.Get().Commands.ToDictionary(c => c.Name, c => c.Description);

        foreach (var command in new[] { "query", "measure", "compare" })
        {
            Assert.Contains("--dry-run", commands[command], StringComparison.Ordinal);
            Assert.Contains("dryRun", commands[command], StringComparison.Ordinal);
            Assert.Contains("offline", commands[command], StringComparison.Ordinal);
            Assert.DoesNotContain("no CLI switch", commands[command], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Mutation_is_advertised_with_agent_facing_status_contract()
    {
        var capabilities = SqlHarnessCapabilitiesProvider.Get();
        var mutation = capabilities.Commands.Single(command => command.Name == "mutation");

        Assert.Contains("dashboard mutation policy", mutation.Description, StringComparison.Ordinal);
        Assert.Contains("non-success status", mutation.Description, StringComparison.Ordinal);
        Assert.Equal(["text", "json", "agent"], capabilities.OutputModesByCommand["mutation"]);
    }

    [Theory]
    [InlineData("nvarchar(10)", "nvarchar(n)", SqlDbType.NVarChar)]
    [InlineData("varchar(10)", "varchar(n)", SqlDbType.VarChar)]
    [InlineData("char(10)", "char(n)", SqlDbType.Char)]
    [InlineData("nchar(10)", "nchar(n)", SqlDbType.NChar)]
    [InlineData("varbinary(10)", "varbinary(n)", SqlDbType.VarBinary)]
    [InlineData("binary(10)", "binary(n)", SqlDbType.Binary)]
    public void Sized_types_advertised_for_sqlserver_bind_through_the_sqlserver_dialect(string declared, string advertised, SqlDbType expected)
    {
        var value = declared.Contains("binary", StringComparison.Ordinal) ? "AQID" : "a";
        var bound = SqlDialects.For(SqlEngine.SqlServer).BindParameters([new SqlHarnessParameterInput("x", declared, value)]);

        Assert.Equal(expected, Assert.Single(bound).Type);
        Assert.Contains(advertised, SqlHarnessCapabilitiesProvider.Get(SqlEngine.SqlServer, "p").ParameterTypes!);
    }

    [Fact]
    public void Capabilities_states_output_modes_per_command()
    {
        var modes = SqlHarnessCapabilitiesProvider.Get().OutputModesByCommand;
        Assert.Equal(["text", "json", "json-summary", "agent"], modes["measure"]);
        Assert.Equal(["text", "json", "json-summary", "agent"], modes["compare"]);
        Assert.Contains("ndjson", modes["watch"]);
        Assert.DoesNotContain("json-summary", modes["query"]);
        Assert.DoesNotContain("ndjson", modes["query"]);
        Assert.DoesNotContain("mcp", modes.Keys);
    }

    [Fact]
    public void Watch_capability_description_makes_no_read_only_claim()
    {
        // 009/final-fix (closes the final-review minor): the capabilities
        // watch entry must not claim "read-only query" — the preflight only
        // checks effects visible in the text, the same reason the CLI help
        // and the MCP sqlharness_watch description avoid the phrase.
        var watch = SqlHarnessCapabilitiesProvider.Get().Commands.Single(command => command.Name == "watch");

        Assert.Contains("static", watch.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("visible", watch.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("read-only query", watch.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no mutation", watch.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("without mutation", watch.Description, StringComparison.OrdinalIgnoreCase);
    }

    private const string CapabilitySetEntry =
        "Plain SET @v = expr to a scalar local declared earlier in the same batch: allowed in query, measured SQL and --setup. The RHS gets the external, stateful and cross-database checks. Compound, cursor, member and session/transaction option SET stay denied";

    private const string CapabilityTableVariableEntry =
        "DECLARE @t TABLE (...) then INSERT/UPDATE/DELETE/MERGE/SELECT against it, declared earlier in the same batch. A table variable from --setup is not visible to measured SQL (separate batch): carry data in #temp. OUTPUT INTO @t only when the primary target is also proven local (persistent primary target: MutationNotAllowed). Aliased DML targets and user-defined table types stay denied";

    private const string CapabilityPostgresTruncateEntry =
        "TRUNCATE [ONLY] of proven current-session temps only, in --setup only; named unqualified or as pg_temp.name; not ON COMMIT DROP. Persistent, mixed, CASCADE, RESTART IDENTITY and other schema-qualified targets stay denied";

    // Plan 029 Step 5: the PostgreSQL temp-proof entry is checked by the facts it must carry, not
    // pinned verbatim; README "PostgreSQL temp target proof" owns the wording.
    private static bool IsPostgresProofEntry(string entry) =>
        new[] { "name-based", "search_path", "pg_temp.name", "63 UTF-8 bytes", "README.md" }
            .All(fact => entry.Contains(fact, StringComparison.Ordinal));

    private static readonly IReadOnlySet<string> NoCarriedTemps = new HashSet<string>(StringComparer.Ordinal);

    // 63 ASCII letters: the longest name that still has a known stored name.
    private static readonly string A63 = new('a', 63);

    private static IReadOnlyList<string> SessionTempStatements(SqlEngine engine) =>
        (IReadOnlyList<string>)SqlHarnessCapabilitiesProvider.Get(engine, "p").Limits["sessionTempStatements"];

    private static string ExpandNames(string sql) => sql.Replace("{A63}", A63, StringComparison.Ordinal);

    [Fact]
    public void SessionTempStatements_lists_are_pinned_exactly()
    {
        // 011/T6 fix round 1: agents read this wording as a contract, so the
        // whole list is pinned; the verdict tests below tie each 011 entry to
        // the classifier.
        Assert.False(SqlHarnessCapabilitiesProvider.Get().Limits.ContainsKey("sessionTempStatements"));
        Assert.Equal(
            [
                "DECLARE scalar variables with analyzed initializers",
                CapabilitySetEntry,
                CapabilityTableVariableEntry,
                "TRUNCATE TABLE #temp (unambiguous local temp only)",
                "ALTER TABLE #temp ADD/DROP COLUMN and local CHECK/DEFAULT/NULL/UNIQUE constraints",
            ],
            SessionTempStatements(SqlEngine.SqlServer));
        Assert.Equal(
            [
                "EXPLAIN over a safe SELECT (plan-only, read-only)",
                "EXPLAIN ANALYZE with full inner-statement effect analysis",
                "SELECT INTO TEMP TABLE with unambiguous single-part name, in --setup only",
                CapabilityPostgresTruncateEntry,
            ],
            SessionTempStatements(SqlEngine.Postgres).Where(entry => !IsPostgresProofEntry(entry)));
        Assert.Single(SessionTempStatements(SqlEngine.Postgres), IsPostgresProofEntry);
    }

    [Fact]
    public void Capabilities_describe_sqlserver_control_flow_without_changing_postgres_summary()
    {
        var sqlServerSummary = SqlHarnessCapabilitiesProvider.Get(SqlEngine.SqlServer, "p").Engines.Single().Summary;
        var postgresSummary = SqlHarnessCapabilitiesProvider.Get(SqlEngine.Postgres, "p").Engines.Single().Summary;

        Assert.Contains("IF/ELSE/BEGIN…END (nested) allowed", sqlServerSummary, StringComparison.Ordinal);
        Assert.Contains("scan every branch regardless of predicate", sqlServerSummary, StringComparison.Ordinal);
        Assert.Contains("WHILE/BREAK/CONTINUE/GOTO/labels/RETURN/TRY…CATCH/WAITFOR/THROW/RAISERROR denied", sqlServerSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("Allowed control flow", postgresSummary, StringComparison.Ordinal);
    }

    // 011/final (M8): the plan 011 entries hold no character that the JSON
    // writer escapes, so the text an agent reads is the text in the source.
    [Fact]
    public async Task SessionTempStatements_serialize_without_json_escapes()
    {
        using var home = new TempProfiles();
        foreach (var (profile, entries) in new[]
        {
            ("sqlserver-profile", new[] { CapabilitySetEntry, CapabilityTableVariableEntry }),
            ("pg-profile", new[] { CapabilityPostgresTruncateEntry, SessionTempStatements(SqlEngine.Postgres).Single(IsPostgresProofEntry) }),
        })
        {
            var output = new StringWriter();
            var exitCode = await SqlHarnessCli.Create(new RecordingModule(), output).RunAsync(["capabilities", profile, "--json"]);
            Assert.Equal(0, exitCode);

            using var document = JsonDocument.Parse(output.ToString());
            var raw = document.RootElement.GetProperty("limits").GetProperty("sessionTempStatements").GetRawText();
            Assert.DoesNotContain("\\u", raw, StringComparison.Ordinal);

            foreach (var entry in entries)
            {
                Assert.Contains("\"" + entry + "\"", raw, StringComparison.Ordinal);
                Assert.DoesNotContain(entry, character => character is '<' or '>' or '+' or '\'' or '&' or '`' or '"' or '\\' || character > 0x7E);
            }
        }
    }

    [Theory]
    // SqlUsage.Query is the usage of `query` and of the measured measure / compare SQL.
    [InlineData("DECLARE @n int; SET @n = 5; SELECT @n")]
    [InlineData("DECLARE @m int; SET @m = (SELECT MAX(Id) FROM dbo.Clients); SELECT @m")]
    public void SessionTempStatements_sqlserver_SET_entry_allows_what_it_claims(string sql)
    {
        Assert.Contains(CapabilitySetEntry, SessionTempStatements(SqlEngine.SqlServer));

        var decision = new SqlSafetyClassifier().Classify(sql, SqlUsage.Query, "db", allowMutation: false);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.False(decision.HasMutation);
    }

    [Theory]
    // "declared earlier in the same batch"
    [InlineData("SET @undeclared = 1; SELECT @undeclared", "UnsupportedStatement")]
    [InlineData("SET @v = 1; DECLARE @v int; SELECT @v", "UnsupportedStatement")]
    [InlineData("DECLARE @v int;\nGO\nSET @v = 1; SELECT @v", "UnsupportedStatement")]
    // "The RHS gets the external, stateful and cross-database checks"
    [InlineData("DECLARE @n int; SET @n = (SELECT COUNT(*) FROM OPENROWSET('MSOLEDBSQL', 'Server=other;Trusted_Connection=yes;', 'SELECT 1') AS r); SELECT @n", "UnsupportedStatement")]
    [InlineData("DECLARE @n int; SET @n = NEXT VALUE FOR dbo.Seq; SELECT @n", "UnsupportedStatement")]
    [InlineData("DECLARE @m int; SET @m = (SELECT MAX(Id) FROM otherdb.dbo.Clients); SELECT @m", "CrossDatabaseReference")]
    // "Compound, cursor, member and session/transaction option SET stay denied"
    [InlineData("DECLARE @v int; SET @v += 1; SELECT @v", "UnsupportedStatement")]
    [InlineData("DECLARE @cur int; SET @cur = CURSOR FOR SELECT Id FROM dbo.Clients; SELECT @cur", "UnsupportedStatement")]
    [InlineData("DECLARE @v dbo.SomeType; SET @v.Member = 1", "UnsupportedStatement")]
    [InlineData("SET NOCOUNT ON", "UnsupportedStatement")]
    [InlineData("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", "UnsupportedStatement")]
    public void SessionTempStatements_sqlserver_SET_entry_denies_what_it_names(string sql, string expected)
    {
        Assert.Contains(CapabilitySetEntry, SessionTempStatements(SqlEngine.SqlServer));

        var decision = new SqlSafetyClassifier().Classify(sql, SqlUsage.Query, "db", allowMutation: false);
        Assert.False(decision.Allowed);
        Assert.Equal(expected, decision.Reason.ToString());
    }

    [Theory]
    [InlineData("DECLARE @n int; SET @n = 5; SELECT @n")]
    [InlineData("DECLARE @n int; SET @n = 5; CREATE TABLE #t (Id int)")]
    public void SessionTempStatements_sqlserver_SET_entry_is_allowed_in_setup_as_it_says(string sql)
    {
        Assert.Contains(CapabilitySetEntry, SessionTempStatements(SqlEngine.SqlServer));

        var decision = new SqlSafetyClassifier().Classify(sql, SqlUsage.CompareSetup, "db", allowMutation: false);
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void SessionTempStatements_sqlserver_table_variable_entry_also_holds_in_setup()
    {
        Assert.Contains(CapabilityTableVariableEntry, SessionTempStatements(SqlEngine.SqlServer));

        var decision = new SqlSafetyClassifier().Classify("DECLARE @t TABLE (Id int); INSERT @t (Id) VALUES (1)", SqlUsage.CompareSetup, "db", allowMutation: false);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.False(decision.HasMutation);
    }

    [Fact]
    public void SessionTempStatements_sqlserver_table_variable_from_setup_is_not_visible_to_measured_SQL()
    {
        // 011/final (M7): setup and measured SQL are separate batches, and a
        // table variable lives in its declaring batch. #temp carries the data.
        Assert.Contains(CapabilityTableVariableEntry, SessionTempStatements(SqlEngine.SqlServer));
        var classifier = new SqlSafetyClassifier();

        const string setup = "DECLARE @t TABLE (Id int); INSERT @t (Id) VALUES (1); SELECT Id INTO #ids FROM @t";
        var setupDecision = classifier.Classify(setup, SqlUsage.CompareSetup, "db", allowMutation: false);
        Assert.True(setupDecision.Allowed, setupDecision.RejectionDescription);
        Assert.True(setupDecision.HasSessionLocalWork);

        var measuredFromVariable = classifier.Classify("SELECT Id FROM @t", SqlUsage.Query, "db", allowMutation: false);
        Assert.False(measuredFromVariable.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, measuredFromVariable.Reason);

        var measuredFromTemp = classifier.Classify("SELECT Id FROM #ids", SqlUsage.Query, "db", allowMutation: false);
        Assert.True(measuredFromTemp.Allowed, measuredFromTemp.RejectionDescription);
        Assert.False(measuredFromTemp.HasMutation);
    }

    [Theory]
    [InlineData("DECLARE @t TABLE (Id int); INSERT @t (Id) VALUES (1)")]
    [InlineData("DECLARE @t TABLE (Id int); UPDATE @t SET Id = 2")]
    [InlineData("DECLARE @t TABLE (Id int); DELETE @t")]
    [InlineData("DECLARE @t TABLE (Id int); MERGE INTO @t USING (SELECT 1 AS SrcId) AS src ON Id = src.SrcId WHEN NOT MATCHED THEN INSERT (Id) VALUES (src.SrcId);")]
    // OUTPUT INTO @t with a primary target that is itself proven local.
    [InlineData("DECLARE @t TABLE (Id int); DECLARE @log TABLE (Id int); DELETE @t OUTPUT deleted.Id INTO @log (Id)")]
    [InlineData("DECLARE @log TABLE (Id int); CREATE TABLE #t (Id int); DELETE #t OUTPUT deleted.Id INTO @log (Id)")]
    public void SessionTempStatements_sqlserver_table_variable_entry_allows_what_it_claims(string sql)
    {
        Assert.Contains(CapabilityTableVariableEntry, SessionTempStatements(SqlEngine.SqlServer));

        var decision = new SqlSafetyClassifier().Classify(sql, SqlUsage.Query, "db", allowMutation: false);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
    }

    [Fact]
    public void SessionTempStatements_sqlserver_table_variable_entry_allows_SELECT_from_the_variable()
    {
        Assert.Contains(CapabilityTableVariableEntry, SessionTempStatements(SqlEngine.SqlServer));

        var decision = new SqlSafetyClassifier().Classify("DECLARE @t TABLE (Id int); SELECT Id FROM @t", SqlUsage.Query, "db", allowMutation: false);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.False(decision.HasMutation);
    }

    [Theory]
    // "declared earlier in the same batch"
    [InlineData("INSERT @t (Id) VALUES (1)", "UnsupportedStatement")]
    [InlineData("INSERT @t (Id) VALUES (1); DECLARE @t TABLE (Id int)", "UnsupportedStatement")]
    [InlineData("DECLARE @t TABLE (Id int);\nGO\nINSERT @t (Id) VALUES (1)", "UnsupportedStatement")]
    // "persistent primary target: MutationNotAllowed"
    [InlineData("DECLARE @t TABLE (Id int); UPDATE dbo.Clients SET Active = 0 OUTPUT inserted.Id INTO @t (Id)", "MutationNotAllowed")]
    [InlineData("DECLARE @t TABLE (Id int); DELETE dbo.Clients OUTPUT deleted.Id INTO @t (Id)", "MutationNotAllowed")]
    // "aliased DML targets and user-defined table types stay denied"
    [InlineData("DECLARE @t TABLE (Id int, Name nvarchar(20)); UPDATE x SET Name = N'b' FROM @t AS x", "UnsupportedStatement")]
    [InlineData("DECLARE @t TABLE (Id int); DELETE x FROM @t AS x", "UnsupportedStatement")]
    [InlineData("DECLARE @v dbo.SomeType; INSERT @v (Id) VALUES (1)", "UnsupportedStatement")]
    [InlineData("DECLARE @v dbo.SomeType; SELECT Id FROM @v", "UnsupportedStatement")]
    public void SessionTempStatements_sqlserver_table_variable_entry_denies_what_it_names(string sql, string expected)
    {
        Assert.Contains(CapabilityTableVariableEntry, SessionTempStatements(SqlEngine.SqlServer));

        var decision = new SqlSafetyClassifier().Classify(sql, SqlUsage.Query, "db", allowMutation: false);
        Assert.False(decision.Allowed);
        Assert.Equal(expected, decision.Reason.ToString());
    }

    [Theory]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE t")]
    [InlineData("CREATE TEMPORARY TABLE t (id int); TRUNCATE ONLY t")]
    [InlineData("CREATE TEMP TABLE t (id int); CREATE TEMP TABLE u (id int); TRUNCATE TABLE t, u")]
    // 011/final (I2): the pg_temp-qualified spelling of a proven temp.
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.t")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE ONLY pg_temp.t")]
    public void SessionTempStatements_postgres_TRUNCATE_entry_allows_what_it_claims(string sql)
    {
        Assert.Contains(CapabilityPostgresTruncateEntry, SessionTempStatements(SqlEngine.Postgres));

        var decision = new PostgresSafetyClassifier().Classify(sql, SqlUsage.Query, "appdb", allowMutation: false, confirmDatabase: null, NoCarriedTemps);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
    }

    [Theory]
    // persistent, mixed, schema-qualified, ON COMMIT DROP: not a proven temp.
    [InlineData("TRUNCATE items", "NonTemporaryWrite")]
    [InlineData("TRUNCATE ONLY items", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE t, items", "NonTemporaryWrite")]
    [InlineData("TRUNCATE pg_temp.t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp_3.t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE public.t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP; TRUNCATE t", "NonTemporaryWrite")]
    // CASCADE and RESTART IDENTITY: denied even over proven temps.
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE t CASCADE", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE t RESTART IDENTITY", "UnsupportedStatement")]
    public void SessionTempStatements_postgres_TRUNCATE_entry_denies_what_it_names(string sql, string expected)
    {
        Assert.Contains(CapabilityPostgresTruncateEntry, SessionTempStatements(SqlEngine.Postgres));

        // Mutation approval unlocks none of these.
        var classifier = new PostgresSafetyClassifier();
        foreach (var approve in new[] { false, true })
        {
            var decision = classifier.Classify(sql, SqlUsage.Query, "appdb", allowMutation: approve, confirmDatabase: approve ? "appdb" : null, NoCarriedTemps);
            Assert.False(decision.Allowed);
            Assert.Equal(expected, decision.Reason.ToString());
        }
    }

    [Theory]
    // Quoted, all-ASCII-unquoted (folded), and exactly 63 bytes are proven.
    [InlineData("CREATE TEMP TABLE \"é\" (id int); INSERT INTO \"é\" VALUES (1)")]
    [InlineData("CREATE TEMP TABLE Items (id int); DELETE FROM items")]
    [InlineData("CREATE TEMP TABLE {A63} (id int); INSERT INTO {A63} VALUES (1); TRUNCATE {A63}")]
    // A DROP whose stored name is known revokes only that name.
    [InlineData("CREATE TEMP TABLE items (id int); CREATE TEMP TABLE other (id int); DROP TABLE other; UPDATE items SET id = 1")]
    // 011/final (I2): the pg_temp-qualified spelling the entry recommends.
    [InlineData("INSERT INTO pg_temp.items VALUES (1)")]
    [InlineData("CREATE TEMP TABLE items (id int); DELETE FROM pg_temp.items; TRUNCATE pg_temp.items")]
    public void SessionTempStatements_postgres_proof_entry_allows_what_it_claims(string sql)
    {
        Assert.Contains(SessionTempStatements(SqlEngine.Postgres), IsPostgresProofEntry);

        var decision = new PostgresSafetyClassifier().Classify(ExpandNames(sql), SqlUsage.Query, "appdb", allowMutation: false, confirmDatabase: null, NoCarriedTemps);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
    }

    [Theory]
    // Unquoted non-ASCII where declared or where used: never proven.
    [InlineData("CREATE TEMP TABLE \"é\" (id int); DROP TABLE É", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE É (id int); CREATE INDEX ix ON \"é\" (id)", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE É (id int); TRUNCATE É", "NonTemporaryWrite")]
    // Longer than 63 UTF-8 bytes: never proven, quoted or not.
    [InlineData("CREATE TEMP TABLE {A63}x (id int); DROP TABLE {A63}x", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE {A63}x (id int); CREATE INDEX ix ON {A63}x (id)", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE {A63}x (id int); TRUNCATE {A63}x", "NonTemporaryWrite")]
    // DROP TABLE of a name unknown offline revokes every proof, unrelated names too.
    [InlineData("CREATE TEMP TABLE items (id int); DROP TABLE pg_temp.{A63}y; TRUNCATE items", "NonTemporaryWrite")]
    public void SessionTempStatements_postgres_proof_entry_denies_what_it_names(string sql, string expected)
    {
        Assert.Contains(SessionTempStatements(SqlEngine.Postgres), IsPostgresProofEntry);

        var decision = new PostgresSafetyClassifier().Classify(ExpandNames(sql), SqlUsage.Query, "appdb", allowMutation: false, confirmDatabase: null, NoCarriedTemps);
        Assert.False(decision.Allowed);
        Assert.Equal(expected, decision.Reason.ToString());
    }

    [Theory]
    // 047 step 3: the entries above still deny DDL over an unproven name. The
    // DML shapes they used to deny now run read-only, where the server refuses a
    // persistent target itself.
    [InlineData("CREATE TEMP TABLE É (id int); INSERT INTO É VALUES (1)")]
    [InlineData("CREATE TEMP TABLE {A63}x (id int); INSERT INTO {A63}x VALUES (1)")]
    [InlineData("CREATE TEMP TABLE \"{A63}x\" (id int); DELETE FROM \"{A63}x\"")]
    [InlineData("CREATE TEMP TABLE items (id int); DROP TABLE IF EXISTS pg_temp.É; INSERT INTO items VALUES (1)")]
    public void SessionTempStatements_postgres_proof_entry_defers_temp_dml_to_the_read_only_server(string sql)
    {
        Assert.Contains(SessionTempStatements(SqlEngine.Postgres), IsPostgresProofEntry);

        var classifier = new PostgresSafetyClassifier();
        var decision = classifier.Classify(ExpandNames(sql), SqlUsage.Query, "appdb", allowMutation: false, confirmDatabase: null, NoCarriedTemps);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);

        // The paths that execute read-write keep the name proof.
        var setup = classifier.Classify(ExpandNames(sql), SqlUsage.CompareSetup, "appdb", allowMutation: false, confirmDatabase: null, NoCarriedTemps);
        Assert.False(setup.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, setup.Reason);

        var approved = classifier.Classify(ExpandNames(sql), SqlUsage.Query, "appdb", allowMutation: true, confirmDatabase: "appdb", NoCarriedTemps);
        Assert.True(approved.Allowed, approved.RejectionDescription);
        Assert.True(approved.HasMutation);
    }

    [Fact]
    public void SessionTempStatements_postgres_proof_is_name_based_and_reads_no_search_path()
    {
        // The search_path clause is a stated limitation for the paths that keep
        // the proof: setup DDL and the approved mutation path are read-write, so a
        // recorded name is all the proof there is. A read-only DML target is
        // decided by the server instead.
        Assert.Contains(SessionTempStatements(SqlEngine.Postgres), IsPostgresProofEntry);

        var classifier = new PostgresSafetyClassifier();
        var setup = classifier.Classify("CREATE TEMP TABLE t (id int)", SqlUsage.CompareSetup, "appdb", allowMutation: false, confirmDatabase: null, NoCarriedTemps);
        Assert.True(setup.Allowed, setup.RejectionDescription);
        Assert.Contains("t", setup.SessionTempTables);

        var proven = classifier.Classify("INSERT INTO t VALUES (1)", SqlUsage.Query, "appdb", allowMutation: false, confirmDatabase: null, setup.SessionTempTables);
        Assert.True(proven.Allowed, proven.RejectionDescription);
        Assert.False(proven.HasMutation);

        var unproven = classifier.Classify("INSERT INTO t VALUES (1)", SqlUsage.Query, "appdb", allowMutation: false, confirmDatabase: null, NoCarriedTemps);
        Assert.True(unproven.Allowed, unproven.RejectionDescription);
        Assert.False(unproven.HasMutation);

        var unprovenTruncate = classifier.Classify("TRUNCATE t", SqlUsage.Query, "appdb", allowMutation: false, confirmDatabase: null, NoCarriedTemps);
        Assert.False(unprovenTruncate.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, unprovenTruncate.Reason);
    }

    private sealed class TempProfiles : IDisposable
    {
        private readonly string? _original = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
        private readonly string _path = Path.Combine(Path.GetTempPath(), "sqlharness-caps-" + Guid.NewGuid().ToString("N"));
        public string HomePath => _path;

        public TempProfiles(string? targetsJson = null)
        {
            Directory.CreateDirectory(_path);
            File.WriteAllText(Path.Combine(_path, "targets.json"), targetsJson ??
                "{\"sqlserver-profile\":{\"server\":\"offline.invalid\",\"database\":\"unused\",\"vars\":{},\"auth\":\"sql\",\"sqlUser\":\"u\",\"passwordEnvVar\":\"UNUSED_SQLHARNESS_PASSWORD\"}," +
                "\"pg-profile\":{\"server\":\"offline.invalid\",\"database\":\"unused\",\"vars\":{},\"auth\":\"sql\",\"sqlUser\":\"u\",\"passwordEnvVar\":\"UNUSED_SQLHARNESS_PASSWORD\",\"engine\":\"postgres\",\"sslMode\":\"verify-full\"}}");
            Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _original);
            try { Directory.Delete(_path, true); } catch (IOException) { }
        }
    }

    private sealed class RecordingModule : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        }
    }
}