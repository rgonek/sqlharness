using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using SqlHarness.Cli;
using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

using Xunit.Abstractions;

namespace SqlHarness.Tests.Cli;

public sealed class MeasureParameterSetCommandTests
{
    private const string ParameterSecret = "customerId:int=424242";
    private static readonly string QueryFile = CreateQueryFile();
    private readonly ITestOutputHelper _output;

    public MeasureParameterSetCommandTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Measure_help_publishes_current_parameter_file_limits_and_types()
    {
        // Spectre help goes to the process console, not the injected command writer.
        var cliAssembly = Path.Combine(AppContext.BaseDirectory, "sqlharness.dll");
        using var process = Process.Start(new ProcessStartInfo("dotnet", $"\"{cliAssembly}\" measure --help")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        Assert.NotNull(process);
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, standardError);
        var help = Regex.Replace(standardOutput, @"\s+", " ");
        Assert.Contains("16 MiB per file", help, StringComparison.Ordinal);
        Assert.Contains("32 MiB total raw bytes per call", help, StringComparison.Ordinal);
        Assert.DoesNotContain("64 KiB", help, StringComparison.Ordinal);
        Assert.DoesNotContain("table/list execution is not available yet", help, StringComparison.Ordinal);
    }

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

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public async Task Measure_forwards_large_named_parameter_set_file_batches_without_splitting(int count)
    {
        using var files = new TempJsonBatch(count);
        var module = new FakeModule();
        var args = new List<string> { "measure", "dev", "--query", QueryFile };
        foreach (var path in files.Paths)
        {
            args.Add("--param-set");
            args.Add(path);
        }

        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(args.ToArray());

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessMeasureOperation>(Assert.Single(module.Operations));
        Assert.Equal(count, operation.ParameterSets!.Count);
        Assert.Equal(Enumerable.Range(0, count).Select(index => $"set-{index}"), operation.ParameterSets.Select(set => set.Name));
        Assert.Equal(Enumerable.Range(0, count).Select(index => $"id:int={index + 1}"), operation.ParameterSets.SelectMany(set => set.Parameters));
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
    public async Task Measure_accepts_subject_and_scope_without_compare_context_policy()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter(), compareConfig: new CompareConfig { RequireContext = true }).RunAsync([
            "measure", "dev", "--query", QueryFile, "--subject", "orders-report", "--scope", "whole"
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessMeasureOperation>(Assert.Single(module.Operations));
        Assert.Equal(new CompareContext("orders-report", "whole"), operation.Context);
    }

    [Fact]
    public async Task Measure_context_is_optional_when_compare_context_is_required()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter(), compareConfig: new CompareConfig { RequireContext = true }).RunAsync([
            "measure", "dev", "--query", QueryFile
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessMeasureOperation>(Assert.Single(module.Operations));
        Assert.Null(operation.Context);
    }

    [Fact]
    public async Task Measure_rejects_invalid_context_labels_before_dispatch()
    {
        var module = new FakeModule();
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "measure", "dev", "--query", QueryFile, "--subject", "Orders Report", "--scope", "whole"
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Contains("Compare context labels", output.ToString(), StringComparison.Ordinal);
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
    public async Task Measure_reads_large_typed_fixed_and_set_files_matching_inline_typed_values()
    {
        var fixedJson = LargeFixedParametersJson();
        var setJson = ParameterSetFileReaderTests.LargeTypedParameterSetJson();
        Assert.True(Encoding.UTF8.GetByteCount(fixedJson) > 64 * 1024);
        Assert.True(Encoding.UTF8.GetByteCount(setJson) > 64 * 1024);
        using var fixedFile = TempJson(fixedJson);
        using var first = TempJson(setJson);
        var secondJson = setJson.Replace("large-typed-set", "large-typed-set-copy", StringComparison.Ordinal);
        using var second = TempJson(secondJson);
        var inlineOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var inlineFixed = JsonSerializer.Deserialize<InlineFixedParameterFile>(fixedJson, inlineOptions)!;
        var inlineFirst = JsonSerializer.Deserialize<InlineParameterSetFile>(setJson, inlineOptions)!;
        var inlineSecond = JsonSerializer.Deserialize<InlineParameterSetFile>(secondJson, inlineOptions)!;
        var module = new FakeModule();
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "measure", "dev", "--query", QueryFile, "--params-json", fixedFile.Path,
            "--param-set", first.Path, "--param-set", second.Path
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessMeasureOperation>(Assert.Single(module.Operations));
        var fixedParameter = Assert.Single(operation.TypedParameters!);
        var sets = operation.ParameterSets!;
        AssertTypedParametersEqual(inlineFixed.Parameters, operation.TypedParameters!);
        AssertTypedParametersEqual(inlineFirst.Parameters, sets[0].TypedParameters!);
        AssertTypedParametersEqual(inlineSecond.Parameters, sets[1].TypedParameters!);
        var firstParameter = Assert.Single(sets[0].TypedParameters!);
        var secondParameter = Assert.Single(sets[1].TypedParameters!);
        Assert.Equal("list", fixedParameter.Kind);
        Assert.Equal(5000, fixedParameter.Values!.Count);
        Assert.Equal(["item-000000-abcdefghij", "item-000001-abcdefghij"], fixedParameter.Values.Take(2));
        Assert.Equal("list", firstParameter.Kind);
        Assert.Equal(5000, firstParameter.Values!.Count);
        Assert.Equal(["item-000000-abcdefghij", "item-000001-abcdefghij"], firstParameter.Values.Take(2));
        Assert.Equal("large-typed-set-copy", sets[1].Name);
        Assert.Equal(5000, secondParameter.Values!.Count);
        Assert.Equal(["item-000000-abcdefghij", "item-000001-abcdefghij"], secondParameter.Values.Take(2));
        AssertDoesNotLeak(output.ToString(), fixedJson, setJson, secondJson, "item-000000-abcdefghij", "large-typed-set");

        var rawBytes = new[]
        {
            Encoding.UTF8.GetByteCount(fixedJson),
            Encoding.UTF8.GetByteCount(setJson),
            Encoding.UTF8.GetByteCount(secondJson),
        };
        Assert.All(rawBytes, bytes => Assert.True(bytes > 64 * 1024));
        Assert.Single(module.Operations);
        _output.WriteLine($"Offline CLI parameter-file workflow: occurrences=3, rawBytes={string.Join(',', rawBytes)}, totalRawBytes={rawBytes.Sum()}, decodedValues=15000, fakeModuleCalls={module.Operations.Count}, databaseCalls=0, echoedOutputBytes={Encoding.UTF8.GetByteCount(output.ToString())}; materializedParameterBytes={SqlHarnessCapabilitiesProvider.ParameterFileLimits.MaxMaterializedBytes}.");
    }

    [Fact]
    public async Task Measure_rejects_fixed_and_set_files_over_32_mib_together_before_dispatch()
    {
        using var fixedFile = TempBytes(SizedJson(11 * 1024 * 1024, fixedParameters: true));
        using var first = TempBytes(SizedJson(11 * 1024 * 1024));
        using var second = TempBytes(SizedJson(11 * 1024 * 1024));
        var module = new FakeModule();
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "measure", "dev", "--query", QueryFile, "--params-json", fixedFile.Path,
            "--param-set", first.Path, "--param-set", second.Path
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Contains("32 MiB", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Measure_dispatches_exactly_one_named_parameter_set_file()
    {
        using var only = TempJson($$"""{"name":"only","parameters":["{{ParameterSecret}}"]}""");
        var module = new FakeModule();
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "measure", "dev", "--query", QueryFile, "--param-set", only.Path
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessMeasureOperation>(Assert.Single(module.Operations));
        var parameterSets = Assert.IsAssignableFrom<IReadOnlyList<SqlHarnessParameterSetInput>>(operation.ParameterSets);
        Assert.Equal("only", Assert.Single(parameterSets).Name);
        Assert.Equal([ParameterSecret], parameterSets[0].Parameters);
        Assert.Empty(output.ToString());
        AssertDoesNotLeak(output.ToString(), only.Path, ParameterSecret, "only");
    }

    [Fact]
    public async Task Measure_attempts_to_read_a_single_parameter_set_file()
    {
        var missing = Path.Combine(Path.GetTempPath(), "sqlharness-missing-" + Guid.NewGuid().ToString("n"), "secret-set.sqljson");
        var module = new FakeModule();
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "measure", "dev", "--query", QueryFile, "--param-set", missing
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Contains("could not be read", output.ToString(), StringComparison.OrdinalIgnoreCase);
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
    public async Task Measure_invalid_parameter_set_names_path_and_unknown_property()
    {
        using var first = TempJson($$"""{"name":"small","parameters":["{{ParameterSecret}}"],"secret":"private-file-value"}""");
        using var second = TempJson("""{"name":"large","parameters":["id:int=999001"]}""");
        var module = new FakeModule();
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "measure", "dev", "--query", QueryFile,
            "--param-set", first.Path, "--param-set", second.Path
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Equal($"Parameter set file '{first.Path}' contains unknown property 'secret'.{Environment.NewLine}", output.ToString());
        AssertDoesNotLeak(output.ToString(), "private-file-value", ParameterSecret, "999001", "small", "large");
    }

    [Fact]
    public async Task Measure_parameter_set_io_failure_exits_safety_with_the_path()
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
        Assert.Equal($"Parameter set file '{missingA}' could not be read.{Environment.NewLine}", output.ToString());
        Assert.DoesNotContain(missingB, output.ToString(), StringComparison.Ordinal);
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
        "Measured runs execute parameter sets sequentially in user-supplied order.";
    private const string PlanCacheWarning =
        "Parameter-set measurements use separate sessions; SQLHarness did not clear or isolate the server plan cache.";

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

    private static string LargeFixedParametersJson() => JsonSerializer.Serialize(new
    {
        parameters = new[]
        {
            new { name = "Ids", type = "int", kind = "list", values = Enumerable.Range(0, 5000).Select(index => $"item-{index:D6}-abcdefghij") },
        },
    });

    private static void AssertTypedParametersEqual(
        IReadOnlyList<SqlHarnessParameterInput> expected,
        IReadOnlyList<SqlHarnessParameterInput> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Name, actual[index].Name);
            Assert.Equal(expected[index].Type, actual[index].Type);
            Assert.Equal(expected[index].Value, actual[index].Value);
            Assert.Equal(expected[index].Kind, actual[index].Kind);
            Assert.Equal(expected[index].SourceSql, actual[index].SourceSql);
            var expectedRows = expected[index].Rows;
            var actualRows = actual[index].Rows;
            Assert.Equal(expectedRows?.Count, actualRows?.Count);
            if (expectedRows is not null && actualRows is not null)
                for (var row = 0; row < expectedRows.Count; row++)
                    Assert.Equal(expectedRows[row], actualRows[row]);
            Assert.Equal(expected[index].Values, actual[index].Values);
        }
    }

    private sealed record InlineFixedParameterFile(SqlHarnessParameterInput[] Parameters);

    private sealed record InlineParameterSetFile(string Name, SqlHarnessParameterInput[] Parameters);

    private static byte[] SizedJson(int byteCount, bool fixedParameters = false)
    {
        var head = Encoding.UTF8.GetBytes(fixedParameters
            ? "{\"parameters\":[{\"name\":\"id\",\"type\":\"int\",\"value\":\"1\"}]}"
            : "{\"name\":\"aggregate\",\"parameters\":[\"id:int=1\"]}");
        var bytes = new byte[byteCount];
        head.CopyTo(bytes, 0);
        bytes.AsSpan(head.Length, byteCount - head.Length).Fill((byte)' ');
        return bytes;
    }

    private static TempFile TempBytes(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "sqlharness-ps-" + Guid.NewGuid().ToString("n") + ".sqljson");
        File.WriteAllBytes(path, bytes);
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

    private sealed class TempJsonBatch : IDisposable
    {
        private readonly IReadOnlyList<TempFile> _files;

        public TempJsonBatch(int count)
        {
            _files = Enumerable.Range(0, count)
                .Select(index => TempJson(JsonSerializer.Serialize(new
                {
                    name = $"set-{index}",
                    parameters = new[] { $"id:int={index + 1}" },
                })))
                .ToArray();
            Paths = _files.Select(file => file.Path).ToArray();
        }

        public IReadOnlyList<string> Paths { get; }

        public void Dispose()
        {
            foreach (var file in _files)
                file.Dispose();
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
