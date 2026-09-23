using System.Globalization;
using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class QueryStoreTopCommandTests
{
    [Fact]
    public async Task Qstop_dispatches_explicit_bounds_and_json()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "qstop", "dev", "--var", "env=uat", "--top", "30",
            "--window", "2h", "--timeout", "15", "--json"
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessQueryStoreTopOperation>(
            Assert.Single(module.Operations));
        Assert.Equal(30, operation.Top);
        Assert.Equal(120, operation.WindowMinutes);
        Assert.Equal(15, operation.TimeoutSeconds);
        Assert.Equal("uat", operation.Target.Vars["env"]);
    }

    [Fact]
    public async Task Qstop_defaults_top_window_and_timeout()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(["qstop", "dev"]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessQueryStoreTopOperation>(Assert.Single(module.Operations));
        Assert.Equal("dev", operation.Target.Profile);
        Assert.Equal(20, operation.Top);
        Assert.Equal(1440, operation.WindowMinutes);
        Assert.Equal(30, operation.TimeoutSeconds);
        Assert.False(operation.Target.UnsafeDirect);
    }

    [Theory]
    [InlineData("1m", 1)]
    [InlineData("24h", 1440)]
    [InlineData("31d", 44640)]
    [InlineData("44640m", 44640)]
    public async Task Qstop_parses_window_to_minutes(string window, int minutes)
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "qstop", "dev", "--window", window
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessQueryStoreTopOperation>(Assert.Single(module.Operations));
        Assert.Equal(minutes, operation.WindowMinutes);
        Assert.Equal(minutes, QueryStoreWindowParser.Parse(window));
    }

    [Theory]
    [InlineData("0m")]
    [InlineData("1.5h")]
    [InlineData("24")]
    [InlineData("1w")]
    [InlineData("32d")]
    [InlineData("44641m")]
    [InlineData("71582789h")] // unchecked int32 wraps this product to 44, which is inside 1..44640
    public async Task Qstop_rejects_malformed_or_out_of_range_windows(string window)
    {
        var module = new FakeModule();
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "qstop", "dev", "--window", window
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Contains("--window", output.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => QueryStoreWindowParser.Parse(window));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task Qstop_rejects_top_outside_one_to_five_hundred(int top)
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "qstop", "dev", "--top", top.ToString()
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public async Task Qstop_rejects_timeout_outside_one_to_three_hundred(int timeout)
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "qstop", "dev", "--timeout", timeout.ToString()
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
    }

    [Fact]
    public async Task Qstop_preserves_direct_target_options()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "qstop", "--unsafe-direct", "--server", "sql", "--database", "db",
            "--auth", "sql-password", "--sql-user", "runner", "--password-env-var", "SQL_SECRET",
            "--trust-server-certificate", "--engine", "postgres"
        ]);

        Assert.Equal(0, exit);
        var target = Assert.IsType<SqlHarnessQueryStoreTopOperation>(Assert.Single(module.Operations)).Target;
        Assert.Null(target.Profile);
        Assert.Empty(target.Vars);
        Assert.True(target.UnsafeDirect);
        Assert.Equal("sql", target.Server);
        Assert.Equal("db", target.Database);
        Assert.Equal("sql-password", target.Auth);
        Assert.Equal("runner", target.SqlUser);
        Assert.Equal("SQL_SECRET", target.PasswordEnvVar);
        Assert.True(target.TrustServerCertificate);
        Assert.Equal("postgres", target.Engine);
    }

    [Fact]
    public async Task Qstop_rejects_mutation_and_file_options()
    {
        foreach (var args in new[]
        {
            new[] { "qstop", "dev", "--allow-mutation" },
            new[] { "qstop", "dev", "--file", "queries.sql" },
        })
        {
            var module = new FakeModule();
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(module, output).RunAsync(args);

            // Spectre owns unknown options and returns -1 before command execution.
            Assert.Equal(-1, exit);
            Assert.Empty(module.Operations);
            Assert.Equal(string.Empty, output.ToString());
        }
    }

    [Fact]
    public async Task Qstop_text_renders_target_window_artifact_and_columns()
    {
        const string secretSql = "SELECT Secret";
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            var report = Report(15, @"C:\artifacts\qstop-run",
            [
                Item(42, "A1B2C3", null, 10, 2, 1500.5m, 150.25m, 900m, 800.5m, 80m, 400.10m, 1000.5m, 100.25m, 500m,
                    new DateTimeOffset(2026, 7, 29, 12, 34, 56, TimeSpan.FromHours(2))),
                Item(7, "DEADBEEF", "dbo.Orders", 3, 1, 8.0m, 2.5m, 4m, 6.5m, 1.5m, 4m, 100.5m, 25.25m, 80m,
                    new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero)),
            ]);
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(new FakeModule(report), output).RunAsync(["qstop", "dev"]);

            Assert.Equal(0, exit);
            var text = output.ToString();
            var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("Target: sql-server/app-db (profile)", lines[0]);
            Assert.Equal("Window: 15 minutes", lines[1]);
            Assert.Equal(@"Artifacts: C:\artifacts\qstop-run", lines[2]);
            Assert.Equal(
                "QueryId\tHash\tObject\tExecutions\tPlans\tTotalDuration\tAverageDuration\tMaximumDuration\tTotalCpu\tAverageCpu\tMaximumCpu\tTotalLogicalReads\tAverageLogicalReads\tMaximumLogicalReads\tLastExecution",
                lines[3]);
            Assert.Equal(
                "42\tA1B2C3\t\t10\t2\t1500.5\t150.25\t900\t800.5\t80\t400.1\t1000.5\t100.25\t500\t2026-07-29T12:34:56.0000000+02:00",
                lines[4]);
            Assert.Equal(
                "7\tDEADBEEF\tdbo.Orders\t3\t1\t8\t2.5\t4\t6.5\t1.5\t4\t100.5\t25.25\t80\t2026-07-29T12:00:00.0000000+00:00",
                lines[5]);
            Assert.Equal(6, lines.Length);
            Assert.DoesNotContain("requested-host", text, StringComparison.Ordinal);
            Assert.DoesNotContain(",", text, StringComparison.Ordinal);
            Assert.DoesNotContain("1440", text, StringComparison.Ordinal);
            Assert.DoesNotContain("No Query Store runtime data", text, StringComparison.Ordinal);
            Assert.DoesNotContain(secretSql, text, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Qstop_text_renders_explicit_empty_window()
    {
        const string secretSql = "SELECT Secret";
        var report = Report(120, @"C:\artifacts\qstop-empty", []);
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(new FakeModule(report), output).RunAsync(["qstop", "dev"]);

        Assert.Equal(0, exit);
        var text = output.ToString();
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
        [
            "Target: sql-server/app-db (profile)",
            "Window: 120 minutes",
            @"Artifacts: C:\artifacts\qstop-empty",
            "No Query Store runtime data in the selected 120-minute window.",
        ],
        lines);
        Assert.DoesNotContain("1440", text, StringComparison.Ordinal);
        Assert.DoesNotContain("QueryId", text, StringComparison.Ordinal);
        Assert.DoesNotContain(secretSql, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Qstop_json_omits_query_text_properties()
    {
        const string secretSql = "SELECT Secret";
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            var report = Report(15, @"C:\artifacts\qstop-run",
            [
                Item(42, "A1B2C3", null, 10, 2, 1500.5m, 150.25m, 900m, 800.5m, 80m, 400.10m, 1000.5m, 100.25m, 500m,
                    new DateTimeOffset(2026, 7, 29, 12, 34, 56, TimeSpan.FromHours(2))),
            ]);
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(new FakeModule(report), output).RunAsync(["qstop", "dev", "--json"]);

            Assert.Equal(0, exit);
            var text = output.ToString();
            using var json = JsonDocument.Parse(text);
            var query = json.RootElement.GetProperty("queries")[0];
            var names = query.EnumerateObject().Select(property => property.Name).ToArray();
            Assert.Contains("queryId", names);
            Assert.Contains("queryHash", names);
            Assert.Contains("objectName", names);
            Assert.Contains("executionCount", names);
            Assert.Contains("planCount", names);
            Assert.Contains("totalDurationMilliseconds", names);
            Assert.Contains("averageDurationMilliseconds", names);
            Assert.Contains("maximumDurationMilliseconds", names);
            Assert.Contains("totalCpuMilliseconds", names);
            Assert.Contains("averageCpuMilliseconds", names);
            Assert.Contains("maximumCpuMilliseconds", names);
            Assert.Contains("totalLogicalReads", names);
            Assert.Contains("averageLogicalReads", names);
            Assert.Contains("maximumLogicalReads", names);
            Assert.Contains("lastExecutionAt", names);
            Assert.All(names, name =>
            {
                Assert.DoesNotContain("sql", name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("text", name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("statement", name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("batch", name, StringComparison.OrdinalIgnoreCase);
            });
            Assert.Equal(42, query.GetProperty("queryId").GetInt64());
            Assert.Equal("A1B2C3", query.GetProperty("queryHash").GetString());
            Assert.Equal(JsonValueKind.Null, query.GetProperty("objectName").ValueKind);
            Assert.Equal(1500.5m, query.GetProperty("totalDurationMilliseconds").GetDecimal());
            Assert.Equal(15, json.RootElement.GetProperty("windowMinutes").GetInt32());
            Assert.Equal(@"C:\artifacts\qstop-run", json.RootElement.GetProperty("artifactDirectory").GetString());
            Assert.Equal("sql-server", json.RootElement.GetProperty("target").GetProperty("actualServer").GetString());
            Assert.Equal("app-db", json.RootElement.GetProperty("target").GetProperty("actualDatabase").GetString());
            Assert.DoesNotContain(secretSql, text, StringComparison.Ordinal);
            Assert.DoesNotContain("1500,5", text, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Gain_text_and_json_include_qstop_scope()
    {
        var qstop = new SqlHarnessGainSummary(1, 0, 4, 16, 1, 4, 1, 4, 1, 3);
        var empty = new SqlHarnessGainSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var report = new SqlHarnessGainReport(qstop, empty, empty) { QueryStoreTop = qstop };
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);

        var text = new StringWriter();
        new Renderer().Render(outcome, OutputMode.Text, new OutputCaptureWriter(text));
        Assert.Contains("qstop\t1\t0\t3\t75", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("total\t1\t0\t3\t75", text.ToString(), StringComparison.Ordinal);

        var json = new StringWriter();
        new Renderer().Render(outcome, OutputMode.Json, new OutputCaptureWriter(json));
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal(1, document.RootElement.GetProperty("queryStoreTop").GetProperty("executions").GetInt64());
        Assert.Equal(1, document.RootElement.GetProperty("total").GetProperty("executions").GetInt64());
    }

    private static SqlHarnessQueryStoreTopReport Report(
        int windowMinutes,
        string artifactDirectory,
        IReadOnlyList<QueryStoreTopItemReport> queries) =>
        new(
            new SqlHarnessTargetIdentityReport("requested-host", "requested-db", "sql-server", "app-db", "profile"),
            windowMinutes,
            20,
            queries,
            artifactDirectory);

    private static QueryStoreTopItemReport Item(
        long queryId,
        string queryHash,
        string? objectName,
        long executions,
        int plans,
        decimal totalDuration,
        decimal averageDuration,
        decimal maximumDuration,
        decimal totalCpu,
        decimal averageCpu,
        decimal maximumCpu,
        decimal totalReads,
        decimal averageReads,
        decimal maximumReads,
        DateTimeOffset lastExecution) =>
        new(
            queryId,
            queryHash,
            objectName,
            executions,
            plans,
            totalDuration,
            averageDuration,
            maximumDuration,
            totalCpu,
            averageCpu,
            maximumCpu,
            totalReads,
            averageReads,
            maximumReads,
            lastExecution);

    private sealed class FakeModule : ISqlHarnessModule
    {
        private readonly SqlHarnessOutcome _outcome;
        public List<SqlHarnessOperation> Operations { get; } = [];

        public FakeModule(SqlHarnessQueryStoreTopReport? report = null) =>
            _outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            return Task.FromResult(_outcome);
        }
    }
}