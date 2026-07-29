using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class HelperCommandTests
{
    [Fact]
    public async Task Ping_text_renders_one_readiness_line()
    {
        var module = new FakeModule(PingReport());
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["ping", "dev"]);

        Assert.Equal(0, exit);
        var text = output.ToString().TrimEnd();
        Assert.Equal("Ready: sql-server/app-db as dbo-login; 12 ms", text);
        Assert.DoesNotContain("Password=", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\n', text);
    }

    [Fact]
    public async Task Counts_text_renders_schema_name_rows_method()
    {
        var module = new FakeModule(CountsReport());
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["counts", "dev"]);

        Assert.Equal(0, exit);
        var lines = output.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Schema\tName\tRows\tMethod", lines[0]);
        Assert.Equal("dbo\tContracts\t123\tapprox", lines[1]);
        Assert.Equal("audit\tRuns\t0\texact", lines[2]);
        Assert.DoesNotContain("Password=", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ping_json_round_trips_typed_report()
    {
        var report = PingReport();
        var module = new FakeModule(report);
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["ping", "dev", "--json"]);

        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal(report.Server, root.GetProperty("server").GetString());
        Assert.Equal(report.Database, root.GetProperty("database").GetString());
        Assert.Equal(report.Login, root.GetProperty("login").GetString());
        Assert.Equal(report.DurationMilliseconds, root.GetProperty("durationMilliseconds").GetInt64());
        Assert.Equal(report.Target.ActualServer, root.GetProperty("target").GetProperty("actualServer").GetString());
        Assert.DoesNotContain("Password=", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Counts_json_round_trips_typed_report()
    {
        var report = CountsReport();
        var module = new FakeModule(report);
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["counts", "dev", "--json"]);

        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal(0, root.GetProperty("omitted").GetInt32());
        var tables = root.GetProperty("tables");
        Assert.Equal(2, tables.GetArrayLength());
        Assert.Equal("dbo", tables[0].GetProperty("schema").GetString());
        Assert.Equal("Contracts", tables[0].GetProperty("name").GetString());
        Assert.Equal(123, tables[0].GetProperty("rows").GetInt64());
        Assert.Equal("approx", tables[0].GetProperty("method").GetString());
        Assert.Equal("audit", tables[1].GetProperty("schema").GetString());
        Assert.Equal("exact", tables[1].GetProperty("method").GetString());
        Assert.DoesNotContain("Password=", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Counts_dispatches_explicit_tables_and_exact_mode()
    {
        var module = new FakeModule(CountsReport());
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "counts", "dev", "--table", "Contracts", "--table", "audit.Runs",
            "--exact", "--top", "20", "--timeout", "12", "--json"
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessCountsOperation>(Assert.Single(module.Operations));
        Assert.Equal(["Contracts", "audit.Runs"], operation.Tables);
        Assert.True(operation.Exact);
        Assert.Equal(20, operation.Top);
        Assert.Equal(12, operation.TimeoutSeconds);
    }

    [Fact]
    public async Task Ping_dispatches_timeout_and_json()
    {
        var module = new FakeModule(PingReport());
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "ping", "dev", "--timeout", "5", "--json"
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessPingOperation>(Assert.Single(module.Operations));
        Assert.Equal("dev", operation.Target.Profile);
        Assert.Equal(5, operation.TimeoutSeconds);
    }

    [Fact]
    public async Task Ping_defaults_timeout_to_five()
    {
        var module = new FakeModule(PingReport());
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(["ping", "dev"]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessPingOperation>(Assert.Single(module.Operations));
        Assert.Equal(5, operation.TimeoutSeconds);
    }

    [Fact]
    public async Task Counts_dispatches_like_pattern()
    {
        var module = new FakeModule(CountsReport());
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "counts", "dev", "--like", "%Sync%", "--json"
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessCountsOperation>(Assert.Single(module.Operations));
        Assert.Equal("%Sync%", operation.Like);
        Assert.Empty(operation.Tables);
        Assert.False(operation.Exact);
        Assert.Equal(50, operation.Top);
        Assert.Equal(30, operation.TimeoutSeconds);
    }

    [Fact]
    public async Task Counts_defaults_top_approximate_and_timeout()
    {
        var module = new FakeModule(CountsReport());
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(["counts", "dev"]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessCountsOperation>(Assert.Single(module.Operations));
        Assert.Empty(operation.Tables);
        Assert.Null(operation.Like);
        Assert.Equal(50, operation.Top);
        Assert.False(operation.Exact);
        Assert.Equal(30, operation.TimeoutSeconds);
    }

    [Fact]
    public async Task Counts_rejects_table_combined_with_like()
    {
        var module = new FakeModule(CountsReport());
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "counts", "dev", "--table", "Contracts", "--like", "%Sync%"
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Contains("--table", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--like", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task Counts_rejects_top_outside_one_to_five_hundred(int top)
    {
        var module = new FakeModule(CountsReport());
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "counts", "dev", "--top", top.ToString()
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
    }

    [Theory]
    [InlineData("ping", 0)]
    [InlineData("ping", 301)]
    [InlineData("counts", 0)]
    [InlineData("counts", 301)]
    public async Task Helper_commands_reject_timeout_outside_one_to_three_hundred(string command, int timeout)
    {
        var module = new FakeModule(command == "ping" ? PingReport() : CountsReport());
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            command, "dev", "--timeout", timeout.ToString()
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
    }

    [Fact]
    public async Task Ping_preserves_profile_vars()
    {
        var module = new FakeModule(PingReport());
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "ping", "dev", "--var", "tenant=acme", "--var", "env=uat"
        ]);

        Assert.Equal(0, exit);
        var target = Assert.IsType<SqlHarnessPingOperation>(Assert.Single(module.Operations)).Target;
        Assert.Equal("dev", target.Profile);
        Assert.Equal("acme", target.Vars["tenant"]);
        Assert.Equal("uat", target.Vars["env"]);
        Assert.False(target.UnsafeDirect);
    }

    [Fact]
    public async Task Counts_preserves_direct_sql_auth_options()
    {
        var module = new FakeModule(CountsReport());
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "counts", "--unsafe-direct", "--server", "sql", "--database", "db",
            "--auth", "sql-password", "--sql-user", "runner", "--password-env-var", "SQL_SECRET",
            "--trust-server-certificate"
        ]);

        Assert.Equal(0, exit);
        var target = Assert.IsType<SqlHarnessCountsOperation>(Assert.Single(module.Operations)).Target;
        Assert.Null(target.Profile);
        Assert.True(target.UnsafeDirect);
        Assert.Equal("sql", target.Server);
        Assert.Equal("db", target.Database);
        Assert.Equal("sql-password", target.Auth);
        Assert.Equal("runner", target.SqlUser);
        Assert.Equal("SQL_SECRET", target.PasswordEnvVar);
        Assert.True(target.TrustServerCertificate);
    }

    [Fact]
    public async Task Ping_rejects_profile_with_direct_options()
    {
        var module = new FakeModule(PingReport());
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "ping", "dev", "--server", "sql", "--database", "db", "--auth", "azure-cli"
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
    }

    private static SqlHarnessPingReport PingReport() =>
        new(new("sql-server", "app-db", "sql-server", "app-db", "profile"),
            "sql-server", "app-db", "dbo-login", 12);

    private static SqlHarnessCountsReport CountsReport() =>
        new(new("sql-server", "app-db", "sql-server", "app-db", "profile"),
            [
                new("dbo", "Contracts", 123, "approx"),
                new("audit", "Runs", 0, "exact"),
            ],
            0);

    private sealed class FakeModule(SqlHarnessOutcome outcome) : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];

        public FakeModule(object report)
            : this(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null))
        {
        }

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            return Task.FromResult(outcome);
        }
    }
}
