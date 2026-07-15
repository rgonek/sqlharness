using System.Text.Json;
using System.Text;
using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class CommandTests
{
    [Fact]
    public async Task Query_parses_profile_vars_and_emits_json_report()
    {
        var module = new FakeModule(Success(QueryReport()));
        var output = new StringWriter();
        var app = SqlHarnessCli.Create(module, output, new StringReader("select 1"), stdinRedirected: true);

        var exit = await app.RunAsync(["query", "dev", "--var", "region=eu", "--var", "slot=blue", "--json"]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessQueryOperation>(Assert.Single(module.Operations));
        Assert.Equal("dev", operation.Target.Profile);
        Assert.Equal("eu", operation.Target.Vars["region"]);
        Assert.Equal("blue", operation.Target.Vars["slot"]);
        Assert.False(operation.Target.UnsafeDirect);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("profile", json.RootElement.GetProperty("target").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Query_rejects_var_without_equals_before_module_dispatch()
    {
        var module = new FakeModule(Success(QueryReport()));
        var output = new StringWriter();
        var app = SqlHarnessCli.Create(module, output, new StringReader("select 1"), true);

        var exit = await app.RunAsync(["query", "dev", "--var", "region"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Contains("key=value", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("region=eu", "region=us")]
    [InlineData("Region=eu", "region=us")]
    public async Task Query_rejects_duplicate_var_keys_case_insensitively_before_dispatch(string first, string second)
    {
        var module = new FakeModule(Success(QueryReport()));
        var output = new StringWriter();
        var app = SqlHarnessCli.Create(module, output, new StringReader("select 1"), true);

        var exit = await app.RunAsync(["query", "dev", "--var", first, "--var", second]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Equal($"Duplicate --var key 'region'.{Environment.NewLine}", output.ToString(), ignoreCase: true);
    }

    public static TheoryData<string[]> InvalidTargets => new()
    {
        new[] { "query", "dev", "--server", "sql", "--database", "db", "--auth", "azure-cli" },
        new[] { "query", "dev", "--unsafe-direct", "--server", "sql", "--database", "db", "--auth", "azure-cli" },
        new[] { "query", "--unsafe-direct", "--server", "sql", "--database", "db" },
        new[] { "query", "dev", "--sql-user", "sa" },
    };

    [Theory]
    [MemberData(nameof(InvalidTargets))]
    public async Task Query_rejects_invalid_profile_or_direct_combinations(string[] args)
    {
        var module = new FakeModule(Success(QueryReport()));
        var output = new StringWriter();
        var app = SqlHarnessCli.Create(module, output, new StringReader("select 1"), true);

        var exit = await app.RunAsync(args);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
    }

    [Fact]
    public async Task Query_maps_controller_resolved_direct_sql_auth_options()
    {
        var module = new FakeModule(Success(QueryReport()));
        var app = SqlHarnessCli.Create(module, new StringWriter(), new StringReader("select 1"), true);

        var exit = await app.RunAsync(["query", "--unsafe-direct", "--server", "sql", "--database", "db", "--auth", "sql-password", "--sql-user", "runner", "--password-env-var", "SQL_SECRET", "--trust-server-certificate"]);

        Assert.Equal(0, exit);
        var target = Assert.IsType<SqlHarnessQueryOperation>(Assert.Single(module.Operations)).Target;
        Assert.Equal("sql", target.Server);
        Assert.Equal("db", target.Database);
        Assert.Equal("sql-password", target.Auth);
        Assert.Equal("runner", target.SqlUser);
        Assert.Equal("SQL_SECRET", target.PasswordEnvVar);
        Assert.True(target.TrustServerCertificate);
        Assert.Null(target.Profile);
        Assert.True(target.UnsafeDirect);
    }

    [Fact]
    public async Task Measure_compare_and_gain_dispatch_from_real_parser()
    {
        var query = TempFile("select 1");
        var candidate = TempFile("select 2");
        try
        {
            var module = new FakeModule(Success(MeasureReport()));
            Assert.Equal(0, await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(["measure", "dev", "--query", query]));
            Assert.IsType<SqlHarnessMeasureOperation>(module.Operations[^1]);

            module.Outcome = Success(CompareReport());
            Assert.Equal(0, await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(["compare", "dev", "--baseline", query, "--candidate", candidate]));
            Assert.IsType<SqlHarnessCompareOperation>(module.Operations[^1]);

            module.Outcome = Success(GainReport());
            Assert.Equal(0, await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(["gain", "--json"]));
            Assert.IsType<SqlHarnessGainOperation>(module.Operations[^1]);
        }
        finally { File.Delete(query); File.Delete(candidate); }
    }

    [Fact]
    public async Task Module_safe_error_is_redacted_again_before_emission_and_exit_code_is_preserved()
    {
        var module = new FakeModule(new(SqlHarnessExitCode.Authentication, null, "Password=hunter2; access_token=abc"));
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output, new StringReader("select 1"), true).RunAsync(["query", "dev"]);

        Assert.Equal((int)SqlHarnessExitCode.Authentication, exit);
        Assert.DoesNotContain("hunter2", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("abc", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_command_completes_receipt_once_with_actual_utf8_and_platform_lines()
    {
        OutputFootprint? footprint = null;
        var completions = 0;
        var report = QueryReport() with { Messages = ["Zażółć \u001b[31mgęślą\u001b[0m jaźń"] };
        var receipt = new SqlHarnessEmissionReceipt((emitted, _) =>
        {
            completions++;
            footprint = emitted;
            return Task.FromResult(SqlHarnessExitCode.Success);
        });
        var module = new FakeModule(Success(report) with { EmissionReceipt = receipt });
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output, new StringReader("select 1"), true).RunAsync(["query", "dev"]);

        Assert.Equal(0, exit);
        Assert.Equal(1, completions);
        Assert.NotNull(footprint);
        var visible = output.ToString().Replace("\u001b[31m", string.Empty).Replace("\u001b[0m", string.Empty);
        Assert.Equal(Encoding.UTF8.GetByteCount(visible), footprint.Bytes);
        Assert.Equal(CountLines(visible), footprint.Lines);
        Assert.Contains("Zażółć \u001b[31mgęślą\u001b[0m jaźń", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_command_receipt_failure_overrides_exit_once_without_output_corruption()
    {
        OutputFootprint? footprint = null;
        var completions = 0;
        var receipt = new SqlHarnessEmissionReceipt((emitted, _) =>
        {
            completions++;
            footprint = emitted;
            return Task.FromResult(SqlHarnessExitCode.LocalStorage);
        });
        var report = QueryReport() with { Messages = ["Łódź"] };
        var module = new FakeModule(Success(report) with { EmissionReceipt = receipt });
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output, new StringReader("select 1"), true).RunAsync(["query", "dev", "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.LocalStorage, exit);
        Assert.Equal(1, completions);
        Assert.NotNull(footprint);
        Assert.Equal(Encoding.UTF8.GetByteCount(output.ToString()), footprint.Bytes);
        Assert.Equal(CountLines(output.ToString()), footprint.Lines);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("Łódź", json.RootElement.GetProperty("messages")[0].GetString());
    }

    private static SqlHarnessOutcome Success(object report) => new(SqlHarnessExitCode.Success, report, null);
    private static SqlHarnessQueryReport QueryReport() => new(new("expected", "db", "expected", "db", "profile"), "read-only", [], [], 0, 1, "hash", new(10, 1));
    private static CompareVariantReport Variant(string name) => new(name, new(1, 2, 3), new(1, 2, 3), new(1, 2, 3), new Dictionary<string, long>(), [], []);
    private static SqlHarnessMeasureReport MeasureReport() => new(new("s", "d", "s", "d", "profile"), 5, 5, true, Variant("measure"), null);
    private static SqlHarnessCompareReport CompareReport() => new(new("s", "d", "s", "d", "profile"), 5, 10, true, Variant("baseline"), Variant("candidate"), null);
    private static SqlHarnessGainReport GainReport() { var s = new SqlHarnessGainSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0); return new(s, s, s); }
    private static string TempFile(string content) { var path = Path.GetTempFileName(); File.WriteAllText(path, content); return path; }
    private static long CountLines(string value) => value.Length == 0 ? 0 : value.Count(c => c == '\n') + (value[^1] == '\n' ? 0 : 1);

    private sealed class FakeModule(SqlHarnessOutcome outcome) : ISqlHarnessModule
    {
        public SqlHarnessOutcome Outcome { get; set; } = outcome;
        public List<SqlHarnessOperation> Operations { get; } = [];
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        { Operations.Add(operation); return Task.FromResult(Outcome); }
    }
}
