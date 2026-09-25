using System.Text;
using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class MeasureParameterSetCommandTests
{
    private const string ParameterSecret = "customerId:int=424242";
    private static readonly string QueryFile = CreateQueryFile();

    [Fact]
    public async Task Measure_dispatches_two_parameter_sets_in_user_order()
    {
        using var first = TempJson("""{"name":"small","parameters":["id:int=1"]}""");
        using var second = TempJson("""{"name":"large","parameters":["id:int=999"]}""");
        var module = new FakeModule();

        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "measure", "dev", "--query", QueryFile,
            "--param-set", first.Path, "--param-set", second.Path
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessMeasureOperation>(
            Assert.Single(module.Operations));
        Assert.NotNull(operation.ParameterSets);
        Assert.Equal(["small", "large"], operation.ParameterSets.Select(x => x.Name));
        Assert.Equal(["id:int=1"], operation.ParameterSets[0].Parameters);
        Assert.Equal(["id:int=999"], operation.ParameterSets[1].Parameters);
    }

    [Fact]
    public async Task Measure_without_param_set_leaves_parameter_sets_null()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "measure", "dev", "--query", QueryFile, "--param", "id:int=1", "--repeat", "4"
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessMeasureOperation>(Assert.Single(module.Operations));
        Assert.Null(operation.ParameterSets);
        Assert.Equal(["id:int=1"], operation.Parameters);
        Assert.Equal(4, operation.Repeat);
    }

    [Fact]
    public async Task Measure_keeps_fixed_params_and_does_not_print_set_text()
    {
        using var first = TempJson("""{"name":"small","parameters":["id:int=111111","asOf:date=2026-06-30"]}""");
        using var second = TempJson("""{"name":"large","parameters":["id:int=222222","asOf:date=2026-07-01"]}""");
        var module = new FakeModule();
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "measure", "dev", "--query", QueryFile,
            "--param", "tenant:nvarchar=acme-secret",
            "--param-set", first.Path, "--param-set", second.Path,
            "--repeat", "3", "--timeout", "12"
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessMeasureOperation>(Assert.Single(module.Operations));
        Assert.Equal(["tenant:nvarchar=acme-secret"], operation.Parameters);
        Assert.Equal(3, operation.Repeat);
        Assert.Equal(12, operation.TimeoutSeconds);
        Assert.NotNull(operation.ParameterSets);
        Assert.Equal(["id:int=111111", "asOf:date=2026-06-30"], operation.ParameterSets[0].Parameters);
        Assert.Equal(["id:int=222222", "asOf:date=2026-07-01"], operation.ParameterSets[1].Parameters);
        AssertDoesNotLeak(output.ToString(), first.Path, second.Path, "acme-secret", "111111", "222222", "2026-06-30");
    }

    [Fact]
    public async Task Measure_rejects_exactly_one_parameter_set()
    {
        using var only = TempJson($$"""{"name":"only","parameters":["{{ParameterSecret}}"]}""");
        var module = new FakeModule();
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "measure", "dev", "--query", QueryFile, "--param-set", only.Path
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Equal(
            $"Exactly one --param-set is invalid. Use --param for one set.{Environment.NewLine}",
            output.ToString());
        AssertDoesNotLeak(output.ToString(), only.Path, ParameterSecret, "only");
    }

    [Fact]
    public async Task Measure_rejects_one_missing_parameter_set_without_its_path()
    {
        var missing = Path.Combine(Path.GetTempPath(), "sqlharness-missing-" + Guid.NewGuid().ToString("n"), "secret-set.sqljson");
        var module = new FakeModule();
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "measure", "dev", "--query", QueryFile, "--param-set", missing
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Contains("Use --param", output.ToString(), StringComparison.Ordinal);
        AssertDoesNotLeak(output.ToString(), missing, "secret-set");
    }

    [Fact]
    public async Task Measure_allows_duplicate_set_names_across_files()
    {
        using var first = TempJson("""{"name":"same","parameters":["id:int=1"]}""");
        using var second = TempJson("""{"name":"same","parameters":["id:int=2"]}""");
        var module = new FakeModule();

        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "measure", "dev", "--query", QueryFile,
            "--param-set", first.Path, "--param-set", second.Path
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessMeasureOperation>(Assert.Single(module.Operations));
        Assert.NotNull(operation.ParameterSets);
        Assert.Equal(["same", "same"], operation.ParameterSets.Select(set => set.Name));
    }

    [Fact]
    public async Task Measure_invalid_parameter_set_exits_safety_without_leaking_text()
    {
        using var first = TempJson($$"""{"name":"small","parameters":["{{ParameterSecret}}"],}""");
        using var second = TempJson("""{"name":"large","parameters":["id:int=999001"]}""");
        var module = new FakeModule();
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "measure", "dev", "--query", QueryFile,
            "--param-set", first.Path, "--param-set", second.Path
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Equal($"The parameter set file is invalid.{Environment.NewLine}", output.ToString());
        AssertDoesNotLeak(output.ToString(), first.Path, second.Path, ParameterSecret, "999001", "small", "large");
    }

    [Fact]
    public async Task Measure_parameter_set_io_failure_exits_safety_without_the_path()
    {
        var missingA = Path.Combine(Path.GetTempPath(), "sqlharness-io-" + Guid.NewGuid().ToString("n") + ".sqljson");
        var missingB = Path.Combine(Path.GetTempPath(), "sqlharness-io-" + Guid.NewGuid().ToString("n") + ".sqljson");
        var module = new FakeModule();
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "measure", "dev", "--query", QueryFile,
            "--param-set", missingA, "--param-set", missingB
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Equal($"Unable to read parameter set file.{Environment.NewLine}", output.ToString());
        AssertDoesNotLeak(output.ToString(), missingA, missingB);
    }

    [Fact]
    public async Task Measure_param_sets_still_require_a_query_file()
    {
        using var first = TempJson("""{"name":"small","parameters":["id:int=1"]}""");
        using var second = TempJson("""{"name":"large","parameters":["id:int=2"]}""");
        var module = new FakeModule();
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "measure", "dev", "--param-set", first.Path, "--param-set", second.Path
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Contains("--query", output.ToString(), StringComparison.Ordinal);
        AssertDoesNotLeak(output.ToString(), first.Path, second.Path);
    }

    [Fact]
    public void Measure_set_text_shows_cache_rotation_stability_and_artifact_without_values_or_paths()
    {
        var text = Render(SampleMeasureSetReport(), OutputMode.Text);
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
        [
            PlanCacheWarning,
            "Warm-up: small, large",
            MeasuredOrderRule,
            "Setup executions: 1",
            "small\tstable=true\telapsed=10/20/40\tcpu=1/2/3\treads=100/200/300\tplans=plan-hash-small-a,plan-hash-small-b",
            "large\tstable=false\telapsed=50/80/90\tcpu=4/5/6\treads=7/8/9\tplans=plan-hash-large",
            "Cross-set elapsed: small 20 .. large 80",
            "Cross-set cpu: small 2 .. large 5",
            "Cross-set reads: large 8 .. small 200",
            @"artifacts: D:\artifacts\measure-sets",
        ],
        lines);
        AssertDoesNotLeak(text, SecretValue, SourcePath, "sqljson", PlanXmlSentinel);
    }

    [Fact]
    public void Measure_set_json_keeps_the_report_and_omits_parameter_values_and_paths()
    {
        var json = Render(SampleMeasureSetReport(), OutputMode.Json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(PlanCacheWarning, root.GetProperty("planCacheWarning").GetString());
        Assert.Equal(MeasuredOrderRule, root.GetProperty("measuredOrderRule").GetString());
        Assert.Equal("small", root.GetProperty("warmupOrder")[0].GetString());
        Assert.Equal("plan-hash-small-a", root.GetProperty("sets")[0].GetProperty("planHashes")[0].GetString());
        Assert.Equal("hash-small", root.GetProperty("sets")[0].GetProperty("valueHash").GetString());
        Assert.Equal("nvarchar", root.GetProperty("sets")[0].GetProperty("parameters")[0].GetProperty("type").GetString());
        Assert.Equal("small", root.GetProperty("crossSetSummary").GetProperty("minimumMedianElapsedSet").GetString());
        Assert.Equal(@"D:\artifacts\measure-sets", root.GetProperty("artifactDirectory").GetString());
        AssertDoesNotLeak(json, SecretValue, SourcePath, PlanXmlSentinel);
    }

    [Fact]
    public void Measure_set_json_summary_projects_medians_labels_and_capped_operators_without_values_or_plans()
    {
        var json = Render(SampleMeasureSetReport(), OutputMode.JsonSummary);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var small = root.GetProperty("sets")[0];
        var operators = small.GetProperty("noteworthyOperators");

        Assert.Equal(PlanCacheWarning, root.GetProperty("planCacheWarning").GetString());
        Assert.Equal(MeasuredOrderRule, root.GetProperty("measuredOrderRule").GetString());
        Assert.Equal("app-db", root.GetProperty("target").GetProperty("actualDatabase").GetString());
        Assert.Equal("small", small.GetProperty("name").GetString());
        Assert.Equal("@tenant", small.GetProperty("parameters")[0].GetProperty("name").GetString());
        Assert.Equal("nvarchar", small.GetProperty("parameters")[0].GetProperty("type").GetString());
        Assert.Equal("hash-small", small.GetProperty("valueHash").GetString());
        Assert.True(small.GetProperty("resultsStable").GetBoolean());
        Assert.Equal(20, small.GetProperty("medianElapsedMilliseconds").GetInt64());
        Assert.Equal(2, small.GetProperty("medianCpuMilliseconds").GetInt64());
        Assert.Equal(200, small.GetProperty("medianLogicalReads").GetInt64());
        Assert.Equal(10, operators.GetArrayLength());
        Assert.Equal("Op00", operators[0].GetProperty("physicalOp").GetString());
        Assert.Equal("Op09", operators[9].GetProperty("physicalOp").GetString());
        Assert.All(operators.EnumerateArray(), op => Assert.True(op.GetProperty("hasWarnings").GetBoolean() || op.GetProperty("hasSpill").GetBoolean() || op.GetProperty("hasImplicitConversion").GetBoolean()));
        Assert.Equal("small", root.GetProperty("crossSetSummary").GetProperty("minimumMedianElapsedSet").GetString());
        Assert.Equal("large", root.GetProperty("crossSetSummary").GetProperty("maximumMedianElapsedSet").GetString());
        Assert.Equal("large", root.GetProperty("crossSetSummary").GetProperty("minimumMedianReadsSet").GetString());
        Assert.Equal("small", root.GetProperty("crossSetSummary").GetProperty("maximumMedianReadsSet").GetString());
        Assert.Equal(@"D:\artifacts\measure-sets", root.GetProperty("artifactDirectory").GetString());
        Assert.False(root.TryGetProperty("warmupOrder", out _));
        Assert.False(root.TryGetProperty("repeat", out _));
        Assert.False(root.TryGetProperty("measuredRunCount", out _));
        Assert.False(root.TryGetProperty("setupExecutionCount", out _));
        Assert.False(small.TryGetProperty("metrics", out _));
        Assert.False(small.TryGetProperty("planHashes", out _));
        Assert.False(small.TryGetProperty("resultHash", out _));
        Assert.False(small.TryGetProperty("operators", out _));
        Assert.DoesNotContain("\"operators\"", json, StringComparison.OrdinalIgnoreCase);
        AssertDoesNotLeak(json, SecretValue, SourcePath, "plan-hash-small-a", "plan-hash-large", PlanXmlSentinel, "sqljson");
    }

    private const string SecretValue = "super-secret-424242";
    private const string SourcePath = @"D:\ticket\sets\small.sqljson";
    private const string PlanXmlSentinel = "PLAN-XML-SHOULD-NOT-LEAK";
    private const string MeasuredOrderRule =
        "In one-based round r, measured execution starts at index r modulo setCount and wraps in user-supplied order.";
    private const string PlanCacheWarning =
        "Parameter-set measurements use the observed server plan-cache state; SQLHarness did not clear or isolate the plan cache.";

    private static string Render(SqlHarnessMeasureSetReport report, OutputMode mode)
    {
        var writer = new StringWriter();
        new Renderer().Render(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null),
            mode,
            new OutputCaptureWriter(writer));
        return writer.ToString();
    }

    private static SqlHarnessMeasureSetReport SampleMeasureSetReport()
    {
        var flagged = Enumerable.Range(0, 12)
            .Select(index => new CompareOperatorReport(
                index + 1,
                $"Op{index:D2}",
                $"dbo.T{index:D2}",
                HasWarnings: true,
                HasSpill: false,
                HasImplicitConversion: false))
            .Append(new CompareOperatorReport(99, "CleanSeek", "dbo.Orders", false, false, false))
            .ToArray();

        return new SqlHarnessMeasureSetReport(
            new SqlHarnessTargetIdentityReport("requested-host", "requested-db", "sql-server", "app-db", "profile"),
            5,
            10,
            1,
            ["small", "large"],
            MeasuredOrderRule,
            PlanCacheWarning,
            [
                SetReport(
                    "small",
                    [new MeasureParameterMetadata("@tenant", "nvarchar"), new MeasureParameterMetadata("@id", "int")],
                    "hash-small",
                    stable: true,
                    resultHash: "result-small",
                    cpu: new CompareDistribution(1, 2, 3),
                    elapsed: new CompareDistribution(10, 20, 40),
                    reads: new CompareDistribution(100, 200, 300),
                    operators: flagged,
                    plans: ["plan-hash-small-a", "plan-hash-small-b"]),
                SetReport(
                    "large",
                    [new MeasureParameterMetadata("@tenant", "nvarchar"), new MeasureParameterMetadata("@id", "int")],
                    "hash-large",
                    stable: false,
                    resultHash: null,
                    cpu: new CompareDistribution(4, 5, 6),
                    elapsed: new CompareDistribution(50, 80, 90),
                    reads: new CompareDistribution(7, 8, 9),
                    operators: [new CompareOperatorReport(3, "Hash Match", "dbo.Lines", false, true, false)],
                    plans: ["plan-hash-large"]),
            ],
            new MeasureCrossSetSummary(
                "small", 20,
                "large", 80,
                "small", 2,
                "large", 5,
                "large", 8,
                "small", 200),
            @"D:\artifacts\measure-sets");
    }

    private static MeasureParameterSetReport SetReport(
        string name,
        IReadOnlyList<MeasureParameterMetadata> parameters,
        string valueHash,
        bool stable,
        string? resultHash,
        CompareDistribution cpu,
        CompareDistribution elapsed,
        CompareDistribution reads,
        IReadOnlyList<CompareOperatorReport> operators,
        IReadOnlyList<string> plans) =>
        new(
            name,
            parameters,
            valueHash,
            5,
            stable,
            resultHash,
            new CompareVariantReport(
                name,
                cpu,
                elapsed,
                reads,
                new Dictionary<string, long>(StringComparer.Ordinal),
                operators,
                [])
            {
                LogicalReadsByTable = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal),
            },
            plans);

    private static void AssertDoesNotLeak(string text, params string[] forbidden)
    {
        foreach (var value in forbidden)
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
    }

    private static string CreateQueryFile()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "select 1");
        return path;
    }

    private static TempFile TempJson(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "sqlharness-ps-" + Guid.NewGuid().ToString("n") + ".sqljson");
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new TempFile(path);
    }

    private sealed class TempFile(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }

    private sealed class FakeModule : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        }
    }
}