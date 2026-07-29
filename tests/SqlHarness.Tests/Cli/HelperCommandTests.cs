using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class HelperCommandTests
{
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
        new(new("s", "d", "s", "d", "profile"), "s", "d", "login", 1);

    private static SqlHarnessCountsReport CountsReport() =>
        new(new("s", "d", "s", "d", "profile"), [], 0);

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
