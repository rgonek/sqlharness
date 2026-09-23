using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    public async Task Query_prefers_file_over_redirected_stdin()
    {
        // Agent shells redirect empty stdin; --file must still win (AGENTS.md / CivicLens workflow).
        var path = TempFile("select from_file");
        try
        {
            var module = new FakeModule(Success(QueryReport()));
            var app = SqlHarnessCli.Create(
                module, new StringWriter(), new StringReader("select from_stdin"), stdinRedirected: true);

            var exit = await app.RunAsync(["query", "dev", "--file", path]);

            Assert.Equal(0, exit);
            var operation = Assert.IsType<SqlHarnessQueryOperation>(Assert.Single(module.Operations));
            Assert.Equal("select from_file", operation.Sql);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Query_help_lists_supported_parameter_types()
    {
        // Spectre help goes to the process console, not the injected command writer.
        var cliAssembly = Path.Combine(AppContext.BaseDirectory, "sqlharness.dll");
        using var process = Process.Start(new ProcessStartInfo("dotnet", $"\"{cliAssembly}\" query --help")
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
        // Spectre wraps long option descriptions; collapse whitespace for a stable type-list check.
        var normalized = Regex.Replace(standardOutput, @"\s+", " ");
        Assert.Contains(
            "nvarchar, nvarchar(max), varchar, varchar(max), char, nchar, int, bigint, smallint, tinyint, bit, decimal, decimal(p,s), numeric, numeric(p,s), float, real, money, smallmoney, date, time, datetime, datetime2, smalldatetime, datetimeoffset, uniqueidentifier, varbinary, varbinary(max), hierarchyid, geography, geometry",
            normalized,
            StringComparison.Ordinal);
        Assert.Contains("name:null or name:type:null", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_rejects_missing_sql_source_when_stdin_is_not_redirected()
    {
        var module = new FakeModule(Success(QueryReport()));
        var output = new StringWriter();
        var app = SqlHarnessCli.Create(module, output, new StringReader(""), stdinRedirected: false);

        var exit = await app.RunAsync(["query", "dev"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Contains("exactly one SQL source", output.ToString(), StringComparison.OrdinalIgnoreCase);
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
    public async Task Query_rejects_engine_combined_with_profile()
    {
        var sqlFile = TempFile("select 1");
        try
        {
            var module = new FakeModule(Success(QueryReport()));
            var exit = await SqlHarnessCli.Create(module, new StringWriter())
                .RunAsync(["query", "dev", "--engine", "postgres", "--file", sqlFile]);
            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Empty(module.Operations);
        }
        finally { File.Delete(sqlFile); }
    }

    [Fact]
    public async Task Query_unsafe_direct_carries_engine()
    {
        var sqlFile = TempFile("select 1");
        try
        {
            var module = new FakeModule(Success(QueryReport()));
            var app = SqlHarnessCli.Create(module, new StringWriter());
            var exit = await app.RunAsync([
                "query", "--unsafe-direct", "--engine", "postgres",
                "--server", "localhost,5432", "--database", "appdb",
                "--auth", "sql", "--sql-user", "u", "--password-env-var", "P",
                "--file", sqlFile]);
            Assert.Equal(0, exit);
            Assert.Equal("postgres", Assert.IsType<SqlHarnessQueryOperation>(Assert.Single(module.Operations)).Target.Engine);
        }
        finally { File.Delete(sqlFile); }
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
            var compare = Assert.IsType<SqlHarnessCompareOperation>(module.Operations[^1]);
            Assert.Equal(ResultComparisonMode.Ordered, compare.CompareResults);

            module.Outcome = Success(GainReport());
            Assert.Equal(0, await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(["gain", "--json"]));
            Assert.IsType<SqlHarnessGainOperation>(module.Operations[^1]);
        }
        finally { File.Delete(query); File.Delete(candidate); }
    }

    [Fact]
    public async Task Compare_parses_compare_results_multiset()
    {
        var query = TempFile("select 1");
        var candidate = TempFile("select 2");
        try
        {
            var module = new FakeModule(Success(CompareReport()));
            var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(
                ["compare", "dev", "--baseline", query, "--candidate", candidate, "--compare-results", "multiset"]);

            Assert.Equal(0, exit);
            var operation = Assert.IsType<SqlHarnessCompareOperation>(Assert.Single(module.Operations));
            Assert.Equal(
                ResultComparisonMode.Multiset,
                operation.CompareResults);
        }
        finally
        {
            File.Delete(query);
            File.Delete(candidate);
        }
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    public async Task Compare_rejects_invalid_compare_results_before_dispatch(string mode)
    {
        var query = TempFile("select 1");
        var candidate = TempFile("select 2");
        try
        {
            var module = new FakeModule(Success(CompareReport()));
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(module, output).RunAsync(
                ["compare", "dev", "--baseline", query, "--candidate", candidate, "--compare-results", mode]);

            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Empty(module.Operations);
            Assert.Contains("compare-results", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(query);
            File.Delete(candidate);
        }
    }

    [Fact]
    public async Task Compare_text_renders_technical_equivalence_for_ordered_and_off()
    {
        var ordered = CompareReport() with
        {
            ResultsEquivalent = false,
            Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Ordered, false, 2, 0, 0),
        };
        var off = CompareReport() with
        {
            ResultsEquivalent = null,
            Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Off, null, null, null, null),
        };

        var orderedOutput = new StringWriter();
        var offOutput = new StringWriter();
        var orderedModule = new FakeModule(Success(ordered));
        var offModule = new FakeModule(Success(off));
        var query = TempFile("select 1");
        var candidate = TempFile("select 2");
        try
        {
            Assert.Equal(0, await SqlHarnessCli.Create(orderedModule, orderedOutput).RunAsync(
                ["compare", "dev", "--baseline", query, "--candidate", candidate]));
            Assert.Equal(0, await SqlHarnessCli.Create(offModule, offOutput).RunAsync(
                ["compare", "dev", "--baseline", query, "--candidate", candidate, "--compare-results", "off"]));
        }
        finally
        {
            File.Delete(query);
            File.Delete(candidate);
        }

        Assert.Contains(
            "Technical equivalence (ordered): False; baseline-only: 0; candidate-only: 0; differing positions: 2",
            orderedOutput.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("Technical equivalence: off", offOutput.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ResultComparisonMode.Multiset, "multiset")]
    [InlineData(ResultComparisonMode.Set, "set")]
    public async Task Compare_text_omits_differing_positions_when_null(ResultComparisonMode mode, string modeLabel)
    {
        var report = CompareReport() with
        {
            ResultsEquivalent = false,
            Equivalence = new ResultEquivalenceReport(mode, false, null, 3, 1),
        };
        var output = new StringWriter();
        var query = TempFile("select 1");
        var candidate = TempFile("select 2");
        try
        {
            Assert.Equal(0, await SqlHarnessCli.Create(new FakeModule(Success(report)), output).RunAsync(
                ["compare", "dev", "--baseline", query, "--candidate", candidate]));
        }
        finally
        {
            File.Delete(query);
            File.Delete(candidate);
        }

        var text = output.ToString();
        Assert.Contains(
            $"Technical equivalence ({modeLabel}): False; baseline-only: 3; candidate-only: 1",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("differing positions", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_rejects_json_and_json_summary_together_before_dispatch()
    {
        var query = TempFile("select 1");
        var candidate = TempFile("select 2");
        try
        {
            var module = new FakeModule(Success(CompareReport()));
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(module, output).RunAsync(
            [
                "compare", "dev",
                "--baseline", query,
                "--candidate", candidate,
                "--json",
                "--json-summary",
            ]);

            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Empty(module.Operations);
            Assert.Equal(
                $"Choose only one of --json or --json-summary.{Environment.NewLine}",
                output.ToString());
        }
        finally
        {
            File.Delete(query);
            File.Delete(candidate);
        }
    }

    [Fact]
    public async Task Measure_rejects_json_and_json_summary_together_before_dispatch()
    {
        var query = TempFile("select 1");
        try
        {
            var module = new FakeModule(Success(MeasureReport()));
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(module, output).RunAsync(
                ["measure", "dev", "--query", query, "--json", "--json-summary"]);

            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Empty(module.Operations);
            Assert.Equal(
                $"Choose only one of --json or --json-summary.{Environment.NewLine}",
                output.ToString());
        }
        finally
        {
            File.Delete(query);
        }
    }

    [Fact]
    public async Task Compare_json_summary_emits_projected_object_without_full_operator_arrays()
    {
        var operators = Enumerable.Range(1, 15)
            .Select(i => new CompareOperatorReport(i, $"Op{i:D2}", $"dbo.T{i:D2}", i <= 3, false, false))
            .ToArray();
        var report = CompareReport() with
        {
            Baseline = new CompareVariantReport(
                "baseline",
                new(1, 2, 3),
                new(4, 5, 6),
                new(7, 8, 9),
                new Dictionary<string, long>(StringComparer.Ordinal) { ["dbo.Orders"] = 9 },
                operators,
                ["PlanWarning"])
            {
                LogicalReadsByTable = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal)
                {
                    ["dbo.Orders"] = new(1, 2, 3),
                },
            },
            Candidate = new CompareVariantReport(
                "candidate",
                new(10, 20, 30),
                new(11, 21, 31),
                new(12, 22, 32),
                new Dictionary<string, long>(StringComparer.Ordinal) { ["dbo.Lines"] = 12 },
                operators.Select(op => op with { NodeId = op.NodeId + 100 }).ToArray(),
                ["PlanWarning"])
            {
                LogicalReadsByTable = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal)
                {
                    ["dbo.Lines"] = new(2, 4, 6),
                },
            },
            ArtifactDirectory = @"C:\tmp\compare-artifacts",
            Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Ordered, true, 0, 0, 0),
            Classification = new CompareClassificationReport("none", "read-only", "read-only"),
        };

        var query = TempFile("select 1");
        var candidate = TempFile("select 2");
        try
        {
            var module = new FakeModule(Success(report));
            var summaryOutput = new StringWriter();
            var fullOutput = new StringWriter();

            Assert.Equal(0, await SqlHarnessCli.Create(module, summaryOutput).RunAsync(
                ["compare", "dev", "--baseline", query, "--candidate", candidate, "--json-summary"]));
            Assert.Equal(0, await SqlHarnessCli.Create(module, fullOutput).RunAsync(
                ["compare", "dev", "--baseline", query, "--candidate", candidate, "--json"]));

            using var summary = JsonDocument.Parse(summaryOutput.ToString());
            using var full = JsonDocument.Parse(fullOutput.ToString());

            Assert.True(summary.RootElement.TryGetProperty("noteworthyOperators", out var noteworthy));
            Assert.True(noteworthy.GetArrayLength() <= 10);
            Assert.False(summary.RootElement.GetProperty("baseline").TryGetProperty("operators", out _));
            Assert.False(summary.RootElement.GetProperty("candidate").TryGetProperty("operators", out _));
            Assert.Equal(15, full.RootElement.GetProperty("baseline").GetProperty("operators").GetArrayLength());
            Assert.Equal(15, full.RootElement.GetProperty("candidate").GetProperty("operators").GetArrayLength());
            Assert.DoesNotContain("PlanXmls", summaryOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("ResultHash", summaryOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("runs", summaryOutput.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(query);
            File.Delete(candidate);
        }
    }

    [Fact]
    public async Task Compare_without_matrix_dispatches_compare_operation()
    {
        var baseline = TempFile("select baseline");
        var candidate = TempFile("select candidate");
        try
        {
            var module = new FakeModule(Success(CompareReport()));
            var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(
                ["compare", "dev", "--baseline", baseline, "--candidate", candidate]);

            Assert.Equal(0, exit);
            Assert.IsType<SqlHarnessCompareOperation>(Assert.Single(module.Operations));
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(candidate);
        }
    }

    [Fact]
    public async Task Compare_with_one_matrix_dispatches_matrix_operation()
    {
        var baseline = TempFile("select sentinel_matrix_sql_text");
        var candidate = TempFile("select candidate");
        try
        {
            var module = new FakeModule(Success(CompareReport()));
            var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
                "compare", "dev",
                "--baseline", baseline,
                "--candidate", candidate,
                "--matrix", "BatchSize:int=1,20,100",
                "--param", "CustomerId:int=424242",
                "--repeat", "7",
                "--compare-results", "multiset",
                "--json-summary"]);

            Assert.Equal(0, exit);
            var operation = Assert.IsType<SqlHarnessCompareMatrixOperation>(Assert.Single(module.Operations));
            Assert.Equal("BatchSize:int=1,20,100", operation.Matrix);
            Assert.Equal(["CustomerId:int=424242"], operation.Parameters);
            Assert.Equal(7, operation.Repeat);
            Assert.Equal(ResultComparisonMode.Multiset, operation.CompareResults);
            Assert.Equal("select sentinel_matrix_sql_text", operation.BaselineSql);
            Assert.Equal("select candidate", operation.CandidateSql);
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(candidate);
        }
    }

    [Fact]
    public async Task Compare_rejects_repeated_matrix_before_dispatch()
    {
        var baseline = TempFile("select baseline");
        var candidate = TempFile("select candidate");
        try
        {
            var module = new FakeModule(Success(CompareReport()));
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(module, output).RunAsync([
                "compare", "dev",
                "--baseline", baseline,
                "--candidate", candidate,
                "--matrix", "BatchSize:int=1,20,100",
                "--matrix", "Tenant:int=1,2"]);

            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Empty(module.Operations);
            Assert.Equal(
                $"Version 1 accepts exactly one --matrix option.{Environment.NewLine}",
                output.ToString());
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(candidate);
        }
    }

    [Theory]
    [InlineData("measure")]
    [InlineData("query")]
    public async Task Measure_and_query_reject_matrix_as_unknown_option(string command)
    {
        var sql = TempFile("select 1");
        try
        {
            var module = new FakeModule(Success(command == "measure" ? MeasureReport() : QueryReport()));
            var output = new StringWriter();
            string[] args = command == "measure"
                ? ["measure", "dev", "--query", sql, "--matrix", "BatchSize:int=1,20"]
                : ["query", "dev", "--file", sql, "--matrix", "BatchSize:int=1,20"];

            var exit = await SqlHarnessCli.Create(module, output).RunAsync(args);

            // Spectre owns unknown options and returns -1 before command execution.
            Assert.Equal(-1, exit);
            Assert.Empty(module.Operations);
            Assert.Equal(string.Empty, output.ToString());
        }
        finally
        {
            File.Delete(sql);
        }
    }

    [Fact]
    public async Task Compare_matrix_text_is_one_tab_separated_row_per_cell_in_input_order()
    {
        var report = new SqlHarnessCompareMatrixReport("@BatchSize", "int",
        [
            MatrixCell(0, "100", 5, 21, @"C:\artifacts\cell-100", new ResultEquivalenceReport(ResultComparisonMode.Ordered, true, 0, 0, 0)),
            MatrixCell(1, "20", 8, 9, @"C:\artifacts\cell-20", new ResultEquivalenceReport(ResultComparisonMode.Multiset, false, null, 3, 1)),
            MatrixCell(2, "1", 1, 2, null, new ResultEquivalenceReport(ResultComparisonMode.Off, null, null, null, null)),
        ]);
        var baseline = TempFile("select baseline");
        var candidate = TempFile("select candidate");
        try
        {
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(new FakeModule(Success(report)), output).RunAsync([
                "compare", "dev",
                "--baseline", baseline,
                "--candidate", candidate,
                "--matrix", "BatchSize:int=100,20,1"]);

            Assert.Equal(0, exit);
            Assert.Equal(
                string.Join(Environment.NewLine, [
                    "100\tTechnical equivalence (ordered): True; baseline-only: 0; candidate-only: 0; differing positions: 0\t5\t21\tC:\\artifacts\\cell-100",
                    "20\tTechnical equivalence (multiset): False; baseline-only: 3; candidate-only: 1\t8\t9\tC:\\artifacts\\cell-20",
                    "1\tTechnical equivalence: off\t1\t2\tnone",
                    string.Empty,
                ]),
                output.ToString());
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(candidate);
        }
    }

    [Fact]
    public async Task Compare_matrix_json_summary_is_bounded_and_full_json_keeps_cell_reports()
    {
        var operators = Enumerable.Range(1, 15)
            .Select(i => new CompareOperatorReport(i, $"Op{i:D2}", $"dbo.T{i:D2}", true, false, false))
            .ToArray();
        var report = new SqlHarnessCompareMatrixReport("@BatchSize", "int",
        [
            MatrixCell(0, "100", 5, 21, @"C:\artifacts\cell-100", new ResultEquivalenceReport(ResultComparisonMode.Ordered, true, 0, 0, 0), operators),
            MatrixCell(1, "20", 8, 9, @"C:\artifacts\cell-20", new ResultEquivalenceReport(ResultComparisonMode.Multiset, false, null, 3, 1), operators),
        ]);
        var baseline = TempFile("select sentinel_matrix_sql_text");
        var candidate = TempFile("select candidate");
        try
        {
            var module = new FakeModule(Success(report));
            var summaryOutput = new StringWriter();
            var fullOutput = new StringWriter();
            Assert.Equal(0, await SqlHarnessCli.Create(module, summaryOutput).RunAsync([
                "compare", "dev",
                "--baseline", baseline,
                "--candidate", candidate,
                "--matrix", "BatchSize:int=100,20",
                "--param", "CustomerId:int=424242",
                "--json-summary"]));
            Assert.Equal(0, await SqlHarnessCli.Create(module, fullOutput).RunAsync([
                "compare", "dev",
                "--baseline", baseline,
                "--candidate", candidate,
                "--matrix", "BatchSize:int=100,20",
                "--param", "CustomerId:int=424242",
                "--json"]));

            using var summary = JsonDocument.Parse(summaryOutput.ToString());
            using var full = JsonDocument.Parse(fullOutput.ToString());
            var summaryCells = summary.RootElement.GetProperty("cells");
            var fullCells = full.RootElement.GetProperty("cells");
            Assert.Equal(2, summaryCells.GetArrayLength());
            Assert.Equal("100", summaryCells[0].GetProperty("parameterValue").GetString());
            Assert.Equal("20", summaryCells[1].GetProperty("parameterValue").GetString());
            var noteworthy = summaryCells[0].GetProperty("compare").GetProperty("noteworthyOperators");
            Assert.True(noteworthy.GetArrayLength() <= 10);
            Assert.False(summaryCells[0].GetProperty("compare").GetProperty("baseline").TryGetProperty("operators", out _));
            Assert.False(summaryCells[0].GetProperty("compare").GetProperty("candidate").TryGetProperty("operators", out _));
            Assert.Equal(15, fullCells[0].GetProperty("compare").GetProperty("baseline").GetProperty("operators").GetArrayLength());
            Assert.Equal("100", fullCells[0].GetProperty("parameterValue").GetString());
            Assert.DoesNotContain("sentinel_matrix_sql_text", summaryOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("sentinel_matrix_sql_text", fullOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("424242", summaryOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("424242", fullOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("\"operators\"", summaryOutput.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("runs", summaryOutput.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PlanXmls", summaryOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("ResultHash", summaryOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(candidate);
        }
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

    [Fact]
    public async Task Watch_dispatches_validated_operation_from_file()
    {
        var sql = TempFile("select 1 as Value");
        try
        {
            var module = new FakeModule(Success(WatchReport()));
            var app = SqlHarnessCli.Create(module, new StringWriter());

            var exit = await app.RunAsync([
                "watch", "dev", "--file", sql, "--param", "Target:int=3",
                "--interval", "10", "--max-duration", "5m", "--until", "Value >= 3", "--json"
            ]);

            Assert.Equal(0, exit);
            var operation = Assert.IsType<SqlHarnessWatchOperation>(Assert.Single(module.Operations));
            Assert.Equal("select 1 as Value", operation.Sql);
            Assert.Equal(TimeSpan.FromSeconds(10), operation.Interval);
            Assert.Equal(TimeSpan.FromMinutes(5), operation.MaxDuration);
            Assert.Equal("Value >= 3", operation.Until);
            Assert.Null(operation.UntilUnchanged);
        }
        finally { File.Delete(sql); }
    }

    [Theory]
    [InlineData("--interval", "0")]
    [InlineData("--max-duration", "0s")]
    [InlineData("--until-unchanged", "0")]
    public async Task Watch_rejects_invalid_bounds_before_dispatch(string option, string value)
    {
        var sql = TempFile("select 1");
        try
        {
            var module = new FakeModule(Success(WatchReport()));
            var exit = await SqlHarnessCli.Create(module, new StringWriter())
                .RunAsync(["watch", "dev", "--file", sql, option, value]);
            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Empty(module.Operations);
        }
        finally { File.Delete(sql); }
    }

    [Fact]
    public async Task Watch_reads_sql_from_redirected_stdin()
    {
        var module = new FakeModule(Success(WatchReport()));
        var app = SqlHarnessCli.Create(module, new StringWriter(), new StringReader("select 42 as N"), stdinRedirected: true);

        var exit = await app.RunAsync(["watch", "dev", "--until", "N = 42"]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessWatchOperation>(Assert.Single(module.Operations));
        Assert.Equal("select 42 as N", operation.Sql);
        Assert.Equal(TimeSpan.FromSeconds(30), operation.Interval);
        Assert.Equal(TimeSpan.FromMinutes(15), operation.MaxDuration);
        Assert.Equal(30, operation.TimeoutSeconds);
        Assert.Equal(50, operation.MaxRows);
    }

    [Fact]
    public async Task Watch_prefers_file_over_redirected_stdin()
    {
        var path = TempFile("select from_file");
        try
        {
            var module = new FakeModule(Success(WatchReport()));
            var app = SqlHarnessCli.Create(
                module, new StringWriter(), new StringReader("select from_stdin"), stdinRedirected: true);

            var exit = await app.RunAsync(["watch", "dev", "--file", path]);

            Assert.Equal(0, exit);
            var operation = Assert.IsType<SqlHarnessWatchOperation>(Assert.Single(module.Operations));
            Assert.Equal("select from_file", operation.Sql);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Watch_rejects_missing_sql_source_when_stdin_is_not_redirected()
    {
        var module = new FakeModule(Success(WatchReport()));
        var output = new StringWriter();
        var app = SqlHarnessCli.Create(module, output, new StringReader(""), stdinRedirected: false);

        var exit = await app.RunAsync(["watch", "dev"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Contains("exactly one SQL source", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Watch_rejects_until_and_until_unchanged_together()
    {
        var sql = TempFile("select 1");
        try
        {
            var module = new FakeModule(Success(WatchReport()));
            var exit = await SqlHarnessCli.Create(module, new StringWriter())
                .RunAsync(["watch", "dev", "--file", sql, "--until", "Value >= 1", "--until-unchanged", "3"]);
            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Empty(module.Operations);
        }
        finally { File.Delete(sql); }
    }

    [Fact]
    public async Task Watch_defaults_to_until_unchanged_when_neither_stop_condition_is_set()
    {
        var sql = TempFile("select 1");
        try
        {
            var module = new FakeModule(Success(WatchReport()));
            var exit = await SqlHarnessCli.Create(module, new StringWriter())
                .RunAsync(["watch", "dev", "--file", sql]);

            Assert.Equal(0, exit);
            var operation = Assert.IsType<SqlHarnessWatchOperation>(Assert.Single(module.Operations));
            Assert.Null(operation.Until);
            Assert.Equal(3, operation.UntilUnchanged);
        }
        finally { File.Delete(sql); }
    }

    [Theory]
    [InlineData("5x")]
    [InlineData("10d")]
    [InlineData("abc")]
    [InlineData("5min")]
    public async Task Watch_rejects_duration_without_supported_suffix(string duration)
    {
        var sql = TempFile("select 1");
        try
        {
            var module = new FakeModule(Success(WatchReport()));
            var exit = await SqlHarnessCli.Create(module, new StringWriter())
                .RunAsync(["watch", "dev", "--file", sql, "--max-duration", duration]);
            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Empty(module.Operations);
        }
        finally { File.Delete(sql); }
    }

    [Fact]
    public async Task Watch_rejects_duration_exceeding_24_hours()
    {
        var sql = TempFile("select 1");
        try
        {
            var module = new FakeModule(Success(WatchReport()));
            var exit = await SqlHarnessCli.Create(module, new StringWriter())
                .RunAsync(["watch", "dev", "--file", sql, "--max-duration", "25h"]);
            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Empty(module.Operations);
        }
        finally { File.Delete(sql); }
    }

    [Fact]
    public async Task Watch_maps_max_duration_exit_code()
    {
        var sql = TempFile("select 1");
        try
        {
            var module = new FakeModule(new SqlHarnessOutcome(
                SqlHarnessExitCode.WatchMaxDuration, WatchReport(WatchExitReason.MaxDuration), null));
            var exit = await SqlHarnessCli.Create(module, new StringWriter())
                .RunAsync(["watch", "dev", "--file", sql]);
            Assert.Equal(7, exit);
            Assert.Equal((int)SqlHarnessExitCode.WatchMaxDuration, exit);
        }
        finally { File.Delete(sql); }
    }

    [Fact]
    public async Task Snapshot_rejects_force_with_diff_before_dispatch()
    {
        var sql = TempFile("select 1");
        try
        {
            var module = new FakeModule(Success(SnapshotReport()));
            var exit = await SqlHarnessCli.Create(module, new StringWriter())
                .RunAsync(["snapshot", "dev", "--file", sql, "--name", "before", "--diff", "--force"]);
            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Empty(module.Operations);
        }
        finally { File.Delete(sql); }
    }

    [Fact]
    public async Task Snapshot_dispatches_validated_operation_from_file()
    {
        var sql = TempFile("select 1 as Id");
        try
        {
            var module = new FakeModule(Success(SnapshotReport()));
            var exit = await SqlHarnessCli.Create(module, new StringWriter())
                .RunAsync(["snapshot", "dev", "--file", sql, "--name", "before-import", "--param", "x:int=1", "--json"]);

            Assert.Equal(0, exit);
            var operation = Assert.IsType<SqlHarnessSnapshotOperation>(Assert.Single(module.Operations));
            Assert.Equal("select 1 as Id", operation.Sql);
            Assert.Equal("before-import", operation.Name);
            Assert.False(operation.Diff);
            Assert.False(operation.Force);
            Assert.Equal(30, operation.TimeoutSeconds);
            Assert.Equal(50, operation.MaxRows);
            Assert.Equal(["x:int=1"], operation.Parameters);
        }
        finally { File.Delete(sql); }
    }

    [Fact]
    public async Task Snapshot_prefers_file_over_redirected_stdin()
    {
        var path = TempFile("select from_file");
        try
        {
            var module = new FakeModule(Success(SnapshotReport()));
            var app = SqlHarnessCli.Create(
                module, new StringWriter(), new StringReader("select from_stdin"), stdinRedirected: true);

            var exit = await app.RunAsync(["snapshot", "dev", "--file", path, "--name", "snap1"]);

            Assert.Equal(0, exit);
            var operation = Assert.IsType<SqlHarnessSnapshotOperation>(Assert.Single(module.Operations));
            Assert.Equal("select from_file", operation.Sql);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Snapshot_reads_sql_from_redirected_stdin()
    {
        var module = new FakeModule(Success(SnapshotReport()));
        var app = SqlHarnessCli.Create(module, new StringWriter(), new StringReader("select 1"), stdinRedirected: true);

        var exit = await app.RunAsync(["snapshot", "dev", "--name", "stdin-snap", "--diff"]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessSnapshotOperation>(Assert.Single(module.Operations));
        Assert.Equal("select 1", operation.Sql);
        Assert.True(operation.Diff);
        Assert.False(operation.Force);
    }

    [Fact]
    public async Task Snapshot_rejects_missing_sql_source_when_stdin_is_not_redirected()
    {
        var module = new FakeModule(Success(SnapshotReport()));
        var output = new StringWriter();
        var app = SqlHarnessCli.Create(module, output, new StringReader(""), stdinRedirected: false);

        var exit = await app.RunAsync(["snapshot", "dev", "--name", "x"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Contains("exactly one SQL source", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("_bad")]
    [InlineData("has space")]
    [InlineData("../../etc")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65 chars
    public async Task Snapshot_rejects_missing_or_unsafe_names(string? name)
    {
        var sql = TempFile("select 1");
        try
        {
            var module = new FakeModule(Success(SnapshotReport()));
            var args = name is null
                ? new[] { "snapshot", "dev", "--file", sql }
                : new[] { "snapshot", "dev", "--file", sql, "--name", name };
            var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(args);
            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Empty(module.Operations);
        }
        finally { File.Delete(sql); }
    }

    [Fact]
    public async Task Snapshot_rejects_name_starting_with_hyphen_when_passed_as_equals()
    {
        // Spectre treats a bare `-bad` argv token as an option; `--name=-bad` is the form that reaches validation.
        var sql = TempFile("select 1");
        try
        {
            var module = new FakeModule(Success(SnapshotReport()));
            var exit = await SqlHarnessCli.Create(module, new StringWriter())
                .RunAsync(["snapshot", "dev", "--file", sql, "--name=-bad"]);
            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Empty(module.Operations);
        }
        finally { File.Delete(sql); }
    }

    [Fact]
    public async Task Snapshot_maps_differences_exit_code()
    {
        var sql = TempFile("select 1");
        try
        {
            var module = new FakeModule(new SqlHarnessOutcome(
                SqlHarnessExitCode.SnapshotDifferences, SnapshotReport(SnapshotVerdict.Different), null));
            var exit = await SqlHarnessCli.Create(module, new StringWriter())
                .RunAsync(["snapshot", "dev", "--file", sql, "--name", "before", "--diff"]);
            Assert.Equal(8, exit);
            Assert.Equal((int)SqlHarnessExitCode.SnapshotDifferences, exit);
        }
        finally { File.Delete(sql); }
    }

    [Fact]
    public async Task Watch_text_renders_changed_polls_and_summary_only()
    {
        var sql = TempFile("select 1 as Value");
        try
        {
            var report = new SqlHarnessWatchReport(
                new("s", "d", "s", "d", "profile"),
                PollCount: 3,
                ElapsedMilliseconds: 2000,
                WatchExitReason.Unchanged,
                [
                    new SqlHarnessWatchPoll(
                        1,
                        0,
                        "hash-1",
                        [new SqlHarnessResultSetReport(
                            [new SqlHarnessColumnReport(0, "Value", "int", false)],
                            [[1]],
                            1,
                            0)]),
                    new SqlHarnessWatchPoll(
                        3,
                        2000,
                        "hash-3",
                        [new SqlHarnessResultSetReport(
                            [new SqlHarnessColumnReport(0, "Value", "int", false)],
                            [["changed-value"]],
                            1,
                            0)]),
                ]);
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(new FakeModule(Success(report)), output)
                .RunAsync(["watch", "dev", "--file", sql]);

            Assert.Equal(0, exit);
            var text = output.ToString();
            Assert.Contains("Poll 1; elapsed: 0 ms", text, StringComparison.Ordinal);
            Assert.Contains("Poll 3; elapsed: 2000 ms", text, StringComparison.Ordinal);
            Assert.Contains("changed-value", text, StringComparison.Ordinal);
            Assert.DoesNotContain("unchanged-poll-row-value", text, StringComparison.Ordinal);
            Assert.Contains(
                "Polls: 3; elapsed: 2000 ms; exit reason: unchanged",
                text,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", text, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(sql); }
    }

    [Theory]
    [InlineData(WatchExitReason.ConditionMet, "condition-met")]
    [InlineData(WatchExitReason.Unchanged, "unchanged")]
    [InlineData(WatchExitReason.MaxDuration, "max-duration")]
    public async Task Watch_text_summary_maps_exit_reason_labels(WatchExitReason reason, string label)
    {
        var sql = TempFile("select 1");
        try
        {
            var report = WatchReport(reason) with { PollCount = 1, ElapsedMilliseconds = 10 };
            var exitCode = reason == WatchExitReason.MaxDuration
                ? SqlHarnessExitCode.WatchMaxDuration
                : SqlHarnessExitCode.Success;
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(
                    new FakeModule(new SqlHarnessOutcome(exitCode, report, null)),
                    output)
                .RunAsync(["watch", "dev", "--file", sql]);

            Assert.Equal((int)exitCode, exit);
            Assert.Contains(
                $"Polls: 1; elapsed: 10 ms; exit reason: {label}",
                output.ToString(),
                StringComparison.Ordinal);
        }
        finally { File.Delete(sql); }
    }

    [Fact]
    public async Task Watch_json_round_trips_typed_report()
    {
        var sql = TempFile("select 1 as Value");
        try
        {
            var report = new SqlHarnessWatchReport(
                new("sql-server", "app-db", "sql-server", "app-db", "profile"),
                PollCount: 2,
                ElapsedMilliseconds: 1500,
                WatchExitReason.ConditionMet,
                [
                    new SqlHarnessWatchPoll(
                        1,
                        0,
                        "hash-a",
                        [new SqlHarnessResultSetReport(
                            [new SqlHarnessColumnReport(0, "Value", "int", false)],
                            [[42]],
                            1,
                            0)]),
                ]);
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(new FakeModule(Success(report)), output)
                .RunAsync(["watch", "dev", "--file", sql, "--json"]);

            Assert.Equal(0, exit);
            using var json = JsonDocument.Parse(output.ToString());
            var root = json.RootElement;
            Assert.Equal(2, root.GetProperty("pollCount").GetInt32());
            Assert.Equal(1500, root.GetProperty("elapsedMilliseconds").GetInt64());
            Assert.Equal((int)WatchExitReason.ConditionMet, root.GetProperty("exitReason").GetInt32());
            Assert.Equal("sql-server", root.GetProperty("target").GetProperty("actualServer").GetString());
            var poll = Assert.Single(root.GetProperty("emittedPolls").EnumerateArray());
            Assert.Equal(1, poll.GetProperty("poll").GetInt32());
            Assert.Equal("hash-a", poll.GetProperty("resultHash").GetString());
            Assert.Equal(42, poll.GetProperty("resultSets")[0].GetProperty("rows")[0][0].GetInt32());
            Assert.DoesNotContain("Password=", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(sql); }
    }

    [Fact]
    public async Task Snapshot_text_renders_verdict_and_bounded_locations()
    {
        var sql = TempFile("select 1");
        try
        {
            var different = new SqlHarnessSnapshotReport(
                new("s", "d", "s", "d", "profile"),
                "before-import",
                SnapshotVerdict.Different,
                2,
                [
                    new SqlHarnessSnapshotDifference(0, 0, 1, "cell-changed"),
                    new SqlHarnessSnapshotDifference(0, 1, null, "row-removed"),
                ]);
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(
                    new FakeModule(new SqlHarnessOutcome(
                        SqlHarnessExitCode.SnapshotDifferences, different, null)),
                    output)
                .RunAsync(["snapshot", "dev", "--file", sql, "--name", "before-import", "--diff"]);

            Assert.Equal(8, exit);
            var lines = output.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("Snapshot before-import: 2 differences", lines[0]);
            Assert.Equal("0\t0\t1\tcell-changed", lines[1]);
            Assert.Equal("0\t1\t\trow-removed", lines[2]);
            Assert.Equal(3, lines.Length);
            Assert.DoesNotContain("Password=", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(sql); }
    }

    [Theory]
    [InlineData(SnapshotVerdict.Stored, "stored")]
    [InlineData(SnapshotVerdict.Identical, "identical")]
    public async Task Snapshot_text_renders_one_line_stored_or_identical(SnapshotVerdict verdict, string label)
    {
        var sql = TempFile("select 1");
        try
        {
            var report = SnapshotReport(verdict) with { Name = "baseline" };
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(new FakeModule(Success(report)), output)
                .RunAsync(["snapshot", "dev", "--file", sql, "--name", "baseline"]);

            Assert.Equal(0, exit);
            Assert.Equal(
                $"Snapshot baseline: {label}{Environment.NewLine}",
                output.ToString());
        }
        finally { File.Delete(sql); }
    }

    [Fact]
    public async Task Snapshot_json_round_trips_typed_report()
    {
        var sql = TempFile("select 1");
        try
        {
            var report = new SqlHarnessSnapshotReport(
                new("sql-server", "app-db", "sql-server", "app-db", "profile"),
                "before-import",
                SnapshotVerdict.Different,
                1,
                [new SqlHarnessSnapshotDifference(0, 0, 0, "cell-changed")]);
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(
                    new FakeModule(new SqlHarnessOutcome(
                        SqlHarnessExitCode.SnapshotDifferences, report, null)),
                    output)
                .RunAsync(["snapshot", "dev", "--file", sql, "--name", "before-import", "--diff", "--json"]);

            Assert.Equal(8, exit);
            using var json = JsonDocument.Parse(output.ToString());
            var root = json.RootElement;
            Assert.Equal("before-import", root.GetProperty("name").GetString());
            Assert.Equal((int)SnapshotVerdict.Different, root.GetProperty("verdict").GetInt32());
            Assert.Equal(1, root.GetProperty("differenceCount").GetInt32());
            var difference = Assert.Single(root.GetProperty("differences").EnumerateArray());
            Assert.Equal(0, difference.GetProperty("resultSet").GetInt32());
            Assert.Equal(0, difference.GetProperty("row").GetInt64());
            Assert.Equal(0, difference.GetProperty("column").GetInt32());
            Assert.Equal("cell-changed", difference.GetProperty("kind").GetString());
            Assert.Equal("sql-server", root.GetProperty("target").GetProperty("actualServer").GetString());
            Assert.DoesNotContain("Password=", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(sql); }
    }

    private static SqlHarnessOutcome Success(object report) => new(SqlHarnessExitCode.Success, report, null);
    private static SqlHarnessQueryReport QueryReport() => new(new("expected", "db", "expected", "db", "profile"), "read-only", [], [], 0, 1, "hash", new(10, 1));
    private static CompareVariantReport Variant(string name) => new(name, new(1, 2, 3), new(1, 2, 3), new(1, 2, 3), new Dictionary<string, long>(), [], []);
    private static SqlHarnessMeasureReport MeasureReport() => new(new("s", "d", "s", "d", "profile"), 5, 5, true, Variant("measure"), null);
    private static SqlHarnessCompareReport CompareReport() => new(new("s", "d", "s", "d", "profile"), 5, 10, true, Variant("baseline"), Variant("candidate"), null);

    private static CompareMatrixCellReport MatrixCell(
        int index,
        string value,
        long baselineMedian,
        long candidateMedian,
        string? artifact,
        ResultEquivalenceReport equivalence,
        IReadOnlyList<CompareOperatorReport>? operators = null)
    {
        operators ??= [];
        var baseline = new CompareVariantReport(
            "baseline", new(1, 2, 3), new(baselineMedian, baselineMedian, baselineMedian), new(1, 2, 3),
            new Dictionary<string, long>(), operators, []);
        var candidate = new CompareVariantReport(
            "candidate", new(4, 5, 6), new(candidateMedian, candidateMedian, candidateMedian), new(7, 8, 9),
            new Dictionary<string, long>(), operators, []);
        var compare = new SqlHarnessCompareReport(
            new("s", "d", "s", "d", "profile"), 5, 10, equivalence.Equivalent, baseline, candidate, artifact)
        {
            Equivalence = equivalence,
        };
        return new CompareMatrixCellReport(index, value, compare);
    }
    private static SqlHarnessGainReport GainReport() { var s = new SqlHarnessGainSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0); return new(s, s, s); }
    private static SqlHarnessWatchReport WatchReport(WatchExitReason reason = WatchExitReason.ConditionMet) =>
        new(new("s", "d", "s", "d", "profile"), 1, 0, reason, []);
    private static SqlHarnessSnapshotReport SnapshotReport(SnapshotVerdict verdict = SnapshotVerdict.Stored) =>
        new(new("s", "d", "s", "d", "profile"), "before", verdict, 0, []);
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