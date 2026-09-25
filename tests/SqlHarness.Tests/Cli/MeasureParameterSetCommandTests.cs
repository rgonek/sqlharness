using System.Text;

using SqlHarness.Cli;
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
