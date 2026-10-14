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
                SqlHarnessCompareOperation => CompareReport(artifactDirectory!),
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

    }
}