using SqlHarness.Cli;
using SqlHarness.Cli.Commands;
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
            var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(args);

            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Empty(module.Operations);
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