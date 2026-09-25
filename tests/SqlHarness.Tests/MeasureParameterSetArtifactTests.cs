using System.Text;
using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

[Collection(SqlHarnessHomeCollection.Name)]
public sealed class MeasureParameterSetArtifactTests
{
    private const string Secret = "acme-secret-884422";
    private const string OtherSecret = "other-secret-991991";
    private const string SourcePath = @"D:\tickets\measure-sets\orders.sql";
    private const string OtherPath = @"D:\tickets\measure-sets\other.sql";
    private const string Query = "SELECT @id, @label, @source /* ticket-query-7f3a */";
    private const string NarrowValue = "717171";
    private const string WideValue = "828282";

    [Fact]
    public void Measure_set_artifacts_keep_set_identity_without_values_or_paths()
    {
        using var temp = new TempDirectory();
        var narrowLabel = $"label:nvarchar={Secret}";
        var narrowSource = $"source:nvarchar={SourcePath}";
        var wideLabel = $"label:nvarchar={OtherSecret}";
        var wideSource = $"source:nvarchar={OtherPath}";
        var narrowInputs = new[] { $"id:int={NarrowValue}", narrowLabel, narrowSource };
        var wideInputs = new[] { $"id:int={WideValue}", wideLabel, wideSource };
        var sets = MeasureParameterSetValidator.Prepare(
            [],
            [new("narrow", narrowInputs), new("small.set", wideInputs)],
            null,
            Query);
        Assert.Equal(Secret, Assert.Single(sets[0].Parameters, parameter => parameter.Name == "@label").Value);
        Assert.Equal(SourcePath, Assert.Single(sets[0].Parameters, parameter => parameter.Name == "@source").Value);
        Assert.DoesNotContain(NarrowValue, sets[0].ValueHash, StringComparison.Ordinal);
        Assert.DoesNotContain(WideValue, sets[1].ValueHash, StringComparison.Ordinal);

        var narrowPlans = new[] { FixturePlan(), FixturePlan() + "\n" };
        var explain = """[{"Plan":{"Node Type":"Seq Scan","Relation Name":"foo"}}]""";
        var runs = new[]
        {
            Run("narrow", 1, narrowPlans, "RESULT-NARROW"),
            Run("small.set", 1, [explain], "RESULT-WIDE"),
        };
        var report = MeasureParameterSetReportProjector.Project(
            new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile"),
            1,
            new MeasureParameterSetExecution(0, ["narrow", "small.set"], runs),
            sets);

        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(report, runs.Select(run => run.Artifact).ToArray(), "testdb");

        var plansDirectory = Path.Combine(directory, "plans");
        var names = Directory.GetFiles(plansDirectory).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            [
                "narrow-measure-001-000-000.plan.json",
                "narrow-measure-001-000-000.sqlplan",
                "narrow-measure-001-000-001.plan.json",
                "narrow-measure-001-000-001.sqlplan",
                "small.set-measure-001-001-000.explain.json",
                "small.set-measure-001-001-000.plan.json",
            ],
            names);
        Assert.Equal(names.Distinct(StringComparer.Ordinal).Count(), names.Length);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Assert.Equal(utf8.GetBytes(narrowPlans[0]), File.ReadAllBytes(Path.Combine(plansDirectory, "narrow-measure-001-000-000.sqlplan")));
        Assert.Equal(utf8.GetBytes(narrowPlans[1]), File.ReadAllBytes(Path.Combine(plansDirectory, "narrow-measure-001-000-001.sqlplan")));
        Assert.Equal(utf8.GetBytes(explain), File.ReadAllBytes(Path.Combine(plansDirectory, "small.set-measure-001-001-000.explain.json")));
        Assert.All(Directory.GetFiles(plansDirectory), path => AssertDirectChild(plansDirectory, path));

        var reportText = File.ReadAllText(Path.Combine(directory, "report.json"));
        var runsText = File.ReadAllText(Path.Combine(directory, "runs.jsonl"));
        using var reportJson = JsonDocument.Parse(reportText);
        Assert.Equal(directory, reportJson.RootElement.GetProperty("artifactDirectory").GetString());
        Assert.Equal(sets[0].ValueHash, reportJson.RootElement.GetProperty("sets")[0].GetProperty("valueHash").GetString());
        Assert.Equal(sets[1].ValueHash, reportJson.RootElement.GetProperty("sets")[1].GetProperty("valueHash").GetString());
        Assert.Equal(
            ["name", "type"],
            reportJson.RootElement.GetProperty("sets")[0].GetProperty("parameters")[0].EnumerateObject().Select(property => property.Name).ToArray());
        var lines = File.ReadAllLines(Path.Combine(directory, "runs.jsonl"));
        Assert.Equal(2, lines.Length);
        using var narrowRun = JsonDocument.Parse(lines[0]);
        using var wideRun = JsonDocument.Parse(lines[1]);
        Assert.Equal(
            [
                "variant", "parameterSet", "repetition", "cpuTimeMilliseconds", "elapsedTimeMilliseconds",
                "logicalReads", "logicalReadsByTable", "resultHash", "messageCount", "planFiles", "planJsonFiles",
            ],
            narrowRun.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("narrow", narrowRun.RootElement.GetProperty("parameterSet").GetString());
        Assert.Equal("small.set", wideRun.RootElement.GetProperty("parameterSet").GetString());
        Assert.Equal(
            ["narrow-measure-001-000-000.sqlplan", "narrow-measure-001-000-001.sqlplan"],
            narrowRun.RootElement.GetProperty("planFiles").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(
            "small.set-measure-001-001-000.explain.json",
            Assert.Single(wideRun.RootElement.GetProperty("planFiles").EnumerateArray()).GetString());

        var surface = string.Join('\n', new[] { reportText, runsText }.Concat(names).Concat(Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
        Assert.Contains("narrow", surface, StringComparison.Ordinal);
        Assert.Contains("small.set", surface, StringComparison.Ordinal);
        Assert.Contains(sets[0].ValueHash, surface, StringComparison.Ordinal);
        Assert.Contains(sets[1].ValueHash, surface, StringComparison.Ordinal);
        Assert.Contains("@id", surface, StringComparison.Ordinal);
        Assert.Contains("@label", surface, StringComparison.Ordinal);
        Assert.Contains("@source", surface, StringComparison.Ordinal);
        Assert.Contains("nvarchar", surface, StringComparison.Ordinal);
        foreach (var forbidden in new[]
        {
            Secret,
            OtherSecret,
            NarrowValue,
            WideValue,
            narrowLabel,
            narrowSource,
            wideLabel,
            wideSource,
            narrowInputs[0],
            wideInputs[0],
            SourcePath,
            OtherPath,
            SourcePath.Replace("\\", "\\\\", StringComparison.Ordinal),
            OtherPath.Replace("\\", "\\\\", StringComparison.Ordinal),
            "orders.sql",
            Query,
            "ticket-query-7f3a",
        })
        {
            Assert.DoesNotContain(forbidden, surface, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Unsafe_set_labels_stay_inside_one_plans_directory_and_keep_unique_names()
    {
        using var temp = new TempDirectory();
        var labels = new[]
        {
            @"..\..\outside",
            @"C:\tickets\orders.sql",
            "plans/../../escape",
            "..",
            "",
            "small.set",
            "a..b",
            "a.",
        };
        var plan = FixturePlan();
        var second = plan + "\n";
        var runs = labels.Select((label, index) => new CompareRunArtifact(
            "measure",
            1,
            1,
            1,
            1,
            new Dictionary<string, long>(),
            "HASH",
            index == 5 ? [plan, second] : [plan],
            0,
            label)).ToArray();

        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(Report(), runs, "testdb");

        var plansDirectory = Path.Combine(directory, "plans");
        var files = Directory.GetFiles(plansDirectory).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(files.Distinct(StringComparer.Ordinal).Count(), files.Length);
        Assert.Equal(runs.Sum(run => run.PlanXmls.Count) * 2, files.Length);
        Assert.Contains("small.set-measure-001-005-000.sqlplan", files);
        Assert.Contains("small.set-measure-001-005-001.sqlplan", files);
        Assert.Contains("a..b-measure-001-006-000.sqlplan", files);
        Assert.Contains("a.-measure-001-007-000.sqlplan", files);
        Assert.All(Directory.GetFiles(plansDirectory), path => AssertDirectChild(plansDirectory, path));
        Assert.All(files, name =>
        {
            Assert.Equal(name, Path.GetFileName(name));
            Assert.DoesNotContain("/", name, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", name, StringComparison.Ordinal);
            Assert.DoesNotContain(":", name, StringComparison.Ordinal);
            Assert.NotEqual(".", name);
            Assert.NotEqual("..", name);
        });

        var metadata = File.ReadAllText(Path.Combine(directory, "report.json"))
            + File.ReadAllText(Path.Combine(directory, "runs.jsonl"))
            + string.Join('\n', files);
        Assert.Contains("\"parameterSet\":\"small.set\"", metadata, StringComparison.Ordinal);
        Assert.Contains("\"parameterSet\":\"a..b\"", metadata, StringComparison.Ordinal);
        Assert.Contains("\"parameterSet\":\"a.\"", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain(@"..\..\outside", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain(@"..\\..\\outside", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\tickets\orders.sql", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\\tickets\\orders.sql", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("plans/../../escape", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("orders.sql", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public void Measure_set_staging_failure_rolls_back_report_plans_and_directory()
    {
        using var temp = new TempDirectory();
        var writer = new CompareArtifactWriter(
            temp.Path,
            () => DateTimeOffset.UnixEpoch,
            (path, content, encoding) =>
            {
                if (path.EndsWith("runs.jsonl", StringComparison.Ordinal))
                    throw new IOException("runs failure");
                File.WriteAllText(path, content, encoding);
            });
        var run = new CompareRunArtifact(
            "measure", 1, 1, 1, 1, new Dictionary<string, long>(), "HASH", [FixturePlan()], 0, "narrow");

        var exception = Assert.Throws<IOException>(() => writer.Write(Report(), [run], "target"));

        Assert.Equal("runs failure", exception.Message);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    private static void AssertDirectChild(string directory, string path)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var fullPath = Path.GetFullPath(path);
        Assert.Equal(fullDirectory, Path.GetDirectoryName(fullPath), ignoreCase: true);
        Assert.Equal(Path.GetFileName(path), Path.GetFileName(fullPath));
    }

    private static SqlHarnessMeasureSetReport Report() => new(
        new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile"),
        1,
        1,
        0,
        ["narrow"],
        "order",
        "cache",
        [
            new MeasureParameterSetReport(
                "narrow",
                [new MeasureParameterMetadata("@id", "int")],
                "HASH",
                1,
                true,
                "RESULT",
                Variant("narrow"),
                ["PLAN"]),
        ],
        new MeasureCrossSetSummary("narrow", 1, "narrow", 1, "narrow", 1, "narrow", 1, "narrow", 1, "narrow", 1),
        null);

    private static CompareVariantReport Variant(string name) => new(
        name,
        new CompareDistribution(1, 1, 1),
        new CompareDistribution(1, 1, 1),
        new CompareDistribution(1, 1, 1),
        new Dictionary<string, long>(),
        [],
        []);

    private static CollectedBenchmarkRun Run(
        string set,
        int repetition,
        IReadOnlyList<string> plans,
        string resultHash) =>
        new(
            new CompareRunArtifact(
                "measure",
                repetition,
                10,
                12,
                5,
                new Dictionary<string, long> { ["Clients"] = 5 },
                resultHash,
                plans,
                1,
                set),
            [new ExecutionPlan([new PlanOperator(1, "Index Seek", "Clients", false, false, false)])],
            ["PLAN-" + set]);

    private static string FixturePlan() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "distiller-sample.sqlplan"));

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sqlharness-artifacts-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, true);
    }
}
