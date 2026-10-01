using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Core;
using SqlHarness.Core.Postgres;

namespace SqlHarness.Tests.Cli;

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
        Assert.True(root.GetProperty("contractVersion").GetInt32() > 0);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("buildId").GetString()));
        Assert.Contains(root.GetProperty("commands").EnumerateArray(), command => command.GetProperty("name").GetString() == "validate");
        var sqlServer = root.GetProperty("engines").EnumerateArray().Single(engine => engine.GetProperty("name").GetString() == "sqlserver");
        var postgres = root.GetProperty("engines").EnumerateArray().Single(engine => engine.GetProperty("name").GetString() == "postgres");
        Assert.True(sqlServer.GetProperty("supportsQstop").GetBoolean());
        Assert.False(postgres.GetProperty("supportsQstop").GetBoolean());
        Assert.Contains("agentOutputBytes", root.GetProperty("limits").EnumerateObject().Select(property => property.Name));
        var limits = root.GetProperty("limits");
        Assert.Equal(1000000, limits.GetProperty("comparisonRowCapPerRun").GetProperty("max").GetInt32());
        Assert.Equal(2000000, limits.GetProperty("comparisonUniqueFingerprintBudgetPerCell").GetProperty("max").GetInt32());
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

    [Fact]
    public void SessionTempStatements_sqlserver_entries_match_the_classifiers_real_verdicts()
    {
        // 011/T6: capabilities must describe only what the classifier actually
        // allows (SET to a same-batch scalar local, DML against a proven table
        // variable) and must not overclaim (undeclared targets stay denied).
        var sessionTempStatements = (Dictionary<string, string[]>)SqlHarnessCapabilitiesProvider.Get().Limits["sessionTempStatements"];
        var sqlServer = sessionTempStatements["sqlserver"];

        Assert.Contains(sqlServer, entry => entry.Contains("SET", StringComparison.Ordinal) && entry.Contains("scalar local", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sqlServer, entry => entry.Contains("DECLARE @t TABLE", StringComparison.Ordinal));

        var classifier = new SqlSafetyClassifier();

        var setAllowed = classifier.Classify("DECLARE @n int; SET @n = 5; SELECT @n", SqlUsage.Query, "db", allowMutation: false);
        Assert.True(setAllowed.Allowed, setAllowed.RejectionDescription);

        var setDenied = classifier.Classify("SET @undeclared = 1; SELECT @undeclared", SqlUsage.Query, "db", allowMutation: false);
        Assert.False(setDenied.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, setDenied.Reason);

        var tableVariableAllowed = classifier.Classify("DECLARE @t TABLE (Id int); INSERT @t (Id) VALUES (1)", SqlUsage.Query, "db", allowMutation: false);
        Assert.True(tableVariableAllowed.Allowed, tableVariableAllowed.RejectionDescription);
        Assert.True(tableVariableAllowed.HasSessionLocalWork);

        var tableVariableDenied = classifier.Classify("INSERT @t (Id) VALUES (1)", SqlUsage.Query, "db", allowMutation: false);
        Assert.False(tableVariableDenied.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, tableVariableDenied.Reason);
    }

    [Fact]
    public void SessionTempStatements_postgres_TRUNCATE_entry_matches_the_classifiers_real_verdicts()
    {
        // 011/T6: capabilities must describe PG TRUNCATE exactly as the
        // classifier implements it: allowed only over proven session temps,
        // denied for any persistent target even with mutation approval.
        var sessionTempStatements = (Dictionary<string, string[]>)SqlHarnessCapabilitiesProvider.Get().Limits["sessionTempStatements"];
        var postgres = sessionTempStatements["postgres"];

        Assert.Contains(postgres, entry => entry.Contains("TRUNCATE", StringComparison.Ordinal) && entry.Contains("session temps", StringComparison.OrdinalIgnoreCase));

        var classifier = new PostgresSafetyClassifier();
        var empty = (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);

        var truncateAllowed = classifier.Classify("CREATE TEMP TABLE t (id int); TRUNCATE t", SqlUsage.Query, "appdb", allowMutation: false, confirmDatabase: null, empty);
        Assert.True(truncateAllowed.Allowed, truncateAllowed.RejectionDescription);
        Assert.True(truncateAllowed.HasSessionLocalWork);

        var truncatePersistentDenied = classifier.Classify("TRUNCATE items", SqlUsage.Query, "appdb", allowMutation: true, confirmDatabase: "appdb", empty);
        Assert.False(truncatePersistentDenied.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, truncatePersistentDenied.Reason);
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
