using System.Text.Json;
using SqlHarness.Core;

namespace SqlHarness.Tests;

public class ArtifactWriterTests
{
    [Fact]
    public void Writer_creates_safe_unique_metadata_and_exact_plan_files()
    {
        using var temp = new TempDirectory();
        const string plan = "<ShowPlanXML><Value>returned-secret-value</Value></ShowPlanXML>\r\n";
        var report = new SqlHarnessCompareReport(
            new SqlHarnessTargetIdentityReport("safe-server", "safe-db", "safe-server", "safe-db"),
            1, 2, true, Variant("baseline"), Variant("candidate"), null);
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 1, 2, 3,
                new Dictionary<string, long> { ["Clients"] = 3 }, "HASH", [plan], 2),
        };
        var writer = new CompareArtifactWriter(temp.Path, () => new DateTimeOffset(2026, 7, 13, 12, 34, 56, TimeSpan.Zero));

        var first = writer.Write(report, runs, "wind/../../unsafe");
        var second = writer.Write(report, runs, "wind/../../unsafe");

        Assert.NotEqual(first, second);
        Assert.StartsWith(Path.GetFullPath(temp.Path), Path.GetFullPath(first), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(first, "report.json")));
        Assert.True(File.Exists(Path.Combine(first, "runs.jsonl")));
        var planPath = Assert.Single(Directory.GetFiles(Path.Combine(first, "plans"), "*.sqlplan"));
        Assert.Equal(plan, File.ReadAllText(planPath));
        var metadata = File.ReadAllText(Path.Combine(first, "report.json")) + File.ReadAllText(Path.Combine(first, "runs.jsonl"));
        Assert.DoesNotContain("returned-secret-value", metadata, StringComparison.Ordinal);
        Assert.Contains("\"messageCount\":2", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "report.json"))));
    }

    [Fact]
    public void Writer_persists_measure_report_and_rejects_other_reports()
    {
        using var temp = new TempDirectory();
        var writer = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch);
        var report = new SqlHarnessMeasureReport(
            new SqlHarnessTargetIdentityReport("server", "db", "server", "db"),
            1, 1, true, Variant("measure"), null);
        var run = new CompareRunArtifact("measure", 1, 1, 2, 3,
            new Dictionary<string, long>(), "HASH", ["<plan />"], 0);

        var directory = writer.Write(report, [run], "wind");

        Assert.Single(File.ReadAllLines(Path.Combine(directory, "runs.jsonl")));
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "plans"), "*.sqlplan"));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Write(new object(), [], "wind"));
    }

    [Fact]
    public void Writer_creates_one_unique_artifact_for_each_plan_in_a_run()
    {
        using var temp = new TempDirectory();
        var report = new SqlHarnessCompareReport(
            new SqlHarnessTargetIdentityReport("server", "db", "server", "db"),
            1, 2, true, Variant("baseline"), Variant("candidate"), null);
        var run = new CompareRunArtifact("baseline", 1, 1, 2, 3,
            new Dictionary<string, long>(), "HASH", ["<plan id=\"1\" />", "<plan id=\"2\" />"], 0);

        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(report, [run], "wind");

        var files = Directory.GetFiles(Path.Combine(directory, "plans"), "*.sqlplan")
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(2, files.Length);
        Assert.Equal(["<plan id=\"1\" />", "<plan id=\"2\" />"], files.Select(File.ReadAllText));
        Assert.Equal(2, files.Select(Path.GetFileName).Distinct(StringComparer.Ordinal).Count());
    }

    private static CompareVariantReport Variant(string name) => new(
        name,
        new CompareDistribution(1, 2, 3),
        new CompareDistribution(2, 3, 4),
        new CompareDistribution(3, 4, 5),
        new Dictionary<string, long> { ["Clients"] = 4 },
        [new CompareOperatorReport(1, "Index Seek", "Clients", false, false, false)],
        []);

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sqlharness-artifacts-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
