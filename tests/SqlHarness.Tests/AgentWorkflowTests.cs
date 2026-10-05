using System.Text;
using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

using Xunit.Abstractions;

namespace SqlHarness.Tests;

[Collection(SqlHarnessHomeCollection.Name)]
public sealed class AgentWorkflowTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AgentWorkflow");
    private readonly ITestOutputHelper _output;

    public AgentWorkflowTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Capability_discovery_then_schema_uses_two_bounded_calls_without_database_access()
    {
        var module = new WorkflowModule();
        var capabilityOutput = NewOutput();
        var exitCodes = new List<int>
        {
            await SqlHarnessCli.Create(module, capabilityOutput).RunAsync(["capabilities", "--json"]),
        };
        var schemaOutput = NewOutput();
        exitCodes.Add(await SqlHarnessCli.Create(module, schemaOutput).RunAsync(["schema", "local", "--object", "dbo.Orders", "--json"]));

        Assert.All(exitCodes, code => Assert.Equal(0, code));
        Assert.Contains("schema", capabilityOutput.ToString(), StringComparison.Ordinal);
        Assert.IsType<SqlHarnessSchemaOperation>(Assert.Single(module.Operations));
        AssertBytesWithinBudget("discoveryThenSchema", capabilityOutput.ToString() + schemaOutput.ToString(), exitCodes);
    }

    [Fact]
    public async Task Offline_validation_rejects_then_accepts_corrected_input_in_two_calls()
    {
        var home = Path.Combine(Path.GetTempPath(), $"sqlharness-workflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        var originalHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", home);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(home, "targets.json"),
                "{\"local\":{\"server\":\"offline.invalid\",\"database\":\"unused\",\"vars\":{},\"auth\":\"sql\",\"sqlUser\":\"unused\",\"passwordEnvVar\":\"UNUSED_SQLHARNESS_PASSWORD\"}}");
            var module = new WorkflowModule();
            var deniedOutput = NewOutput();
            var exitCodes = new List<int>
            {
                await SqlHarnessCli.Create(module, deniedOutput).RunAsync([
                    "validate", "local", "--file", Fixture("validate-denied.sql"), "--json"]),
            };
            var correctedOutput = NewOutput();
            exitCodes.Add(await SqlHarnessCli.Create(module, correctedOutput).RunAsync([
                "validate", "local", "--file", Fixture("validate-corrected.sql"), "--json"]));

            Assert.All(exitCodes, code => Assert.Equal(0, code));
            using var denied = JsonDocument.Parse(deniedOutput.ToString());
            using var corrected = JsonDocument.Parse(correctedOutput.ToString());
            Assert.False(denied.RootElement.GetProperty("allowed").GetBoolean());
            Assert.True(corrected.RootElement.GetProperty("allowed").GetBoolean());
            Assert.False(denied.RootElement.GetProperty("executed").GetBoolean());
            Assert.False(corrected.RootElement.GetProperty("executed").GetBoolean());
            Assert.Empty(module.Operations);
            AssertBytesWithinBudget("validationCorrection", deniedOutput.ToString() + correctedOutput.ToString(), exitCodes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQLHARNESS_HOME", originalHome);
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task Compare_summary_points_to_existing_artifacts_and_leaves_detail_lookup_for_plan_06_t2()
    {
        var artifactDirectory = Path.Combine(Path.GetTempPath(), $"sqlharness-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactDirectory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "baseline.sqlplan"), "<ShowPlanXML />");
            await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "candidate.sqlplan"), "<ShowPlanXML />");
            var module = new WorkflowModule(artifactDirectory);
            var output = NewOutput();
            var exitCodes = new List<int>
            {
                await SqlHarnessCli.Create(module, output).RunAsync([
                    "compare", "local", "--baseline", Fixture("baseline.sql"), "--candidate", Fixture("candidate.sql"), "--output", "agent"]),
            };

            Assert.All(exitCodes, code => Assert.Equal(0, code));
            using var document = JsonDocument.Parse(output.ToString());
            var artifactPath = document.RootElement.GetProperty("result").GetProperty("artifactDirectory").GetString();
            Assert.Equal(artifactDirectory, artifactPath);
            Assert.True(File.Exists(Path.Combine(artifactPath!, "baseline.sqlplan")));
            Assert.True(File.Exists(Path.Combine(artifactPath!, "candidate.sqlplan")));
            Assert.IsType<SqlHarnessCompareOperation>(Assert.Single(module.Operations));
            var escapedDirectory = JsonSerializer.Serialize(artifactDirectory).Trim('"');
            var normalized = output.ToString().Replace(escapedDirectory, "<artifactDirectory>", StringComparison.Ordinal);
            AssertBytesWithinBudget("compareSummary", normalized, exitCodes);
        }
        finally
        {
            Directory.Delete(artifactDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Gain_text_names_estimates_and_the_heuristic()
    {
        var output = NewOutput();
        var exit = await SqlHarnessCli.Create(new WorkflowModule(), output).RunAsync(["gain"]);

        Assert.Equal(0, exit);
        Assert.Contains("Token estimates: utf8-bytes-div-4 (ceil UTF-8 bytes / 4)", output.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("Scope\tExecutions\tFailures\tSaved tokens\tSavings %", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Net estimated tokens by scope\nScope\tNet estimated tokens", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Output_capture_uses_the_sink_newline_for_written_lines()
    {
        var sink = NewOutput();
        var capture = new OutputCaptureWriter(sink);

        capture.WriteLine("stable line");

        Assert.Equal("\n", capture.NewLine);
        Assert.Equal("stable line\n", sink.ToString());
    }

    [Fact]
    public void Workflow_byte_measurement_normalizes_platform_line_endings()
    {
        Assert.Equal(CountNormalizedUtf8Bytes("one\ntwo\n"), CountNormalizedUtf8Bytes("one\r\ntwo\r\n"));
    }

    private static string Fixture(string name) => Path.Combine(FixtureDirectory, name);

    private static StringWriter NewOutput() => new() { NewLine = "\n" };

    private static int CountNormalizedUtf8Bytes(string content) =>
        Encoding.UTF8.GetByteCount(content.Replace("\r\n", "\n", StringComparison.Ordinal));

    private void AssertBytesWithinBudget(string scenario, string content, IReadOnlyCollection<int> exitCodes)
    {
        using var budgets = JsonDocument.Parse(File.ReadAllText(Fixture("byte-budgets.json")));
        var budget = budgets.RootElement.GetProperty("budgets").GetProperty(scenario).GetInt32();
        var expectedCalls = budgets.RootElement.GetProperty("calls").GetProperty(scenario).GetInt32();
        var observedBytes = budgets.RootElement.GetProperty("observedUtf8Bytes").GetProperty(scenario).GetInt32();
        var commandCalls = exitCodes.Count;
        var utf8Bytes = CountNormalizedUtf8Bytes(content);
        _output.WriteLine($"{scenario}: calls={commandCalls}, utf8Bytes={utf8Bytes}, budget={budget}");
        Assert.Equal(expectedCalls, commandCalls);
        Assert.Equal(observedBytes, utf8Bytes);
        Assert.InRange(utf8Bytes, 1, budget);
    }

    private sealed class WorkflowModule(string? artifactDirectory = null) : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            object report = operation switch
            {
                SqlHarnessSchemaOperation => new SqlHarnessSchemaReport(
                    new("offline.invalid", "unused", "offline.invalid", "unused", "profile"),
                    [new SchemaObjectReport("dbo", "Orders", "table", [], [], [])], 0),
                SqlHarnessCompareOperation => CompareReport(artifactDirectory!),
                SqlHarnessGainOperation => new SqlHarnessGainReport(Summary(100, 200), Summary(0, 0), Summary(0, 0)),
                _ => throw new InvalidOperationException($"Unexpected workflow operation: {operation.GetType().Name}"),
            };
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null));
        }

        private static SqlHarnessCompareReport CompareReport(string artifactDirectory)
        {
            var baseline = Variant("baseline");
            var candidate = Variant("candidate");
            return new SqlHarnessCompareReport(
                new("offline.invalid", "unused", "offline.invalid", "unused", "profile"),
                1, 1, true, baseline, candidate, artifactDirectory);
        }

        private static CompareVariantReport Variant(string name) => new(
            name, new(1, 1, 1), new(1, 1, 1), new(0, 0, 0),
            new Dictionary<string, long>(), [], []);

        private static SqlHarnessGainSummary Summary(long rawTokens, long emittedTokens) => new(
            0, 0, 0, rawTokens * 4, 0, emittedTokens * 4, 0,
            rawTokens, emittedTokens, Math.Max(rawTokens - emittedTokens, 0));
    }
}