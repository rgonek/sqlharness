using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class SpaceCommandTests
{
    [Fact]
    public async Task Space_dispatches_top_object_timeout_and_json()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "space", "dev", "--top", "40", "--object", "dbo.Contracts",
            "--timeout", "20", "--json"
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessSpaceOperation>(Assert.Single(module.Operations));
        Assert.Equal(40, operation.Top);
        Assert.Equal("dbo.Contracts", operation.Object);
        Assert.Equal(20, operation.TimeoutSeconds);
    }

    [Fact]
    public async Task Space_defaults_top_and_timeout_without_object()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(["space", "dev"]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessSpaceOperation>(Assert.Single(module.Operations));
        Assert.Equal("dev", operation.Target.Profile);
        Assert.Equal(25, operation.Top);
        Assert.Null(operation.Object);
        Assert.Equal(30, operation.TimeoutSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task Space_rejects_top_outside_one_to_five_hundred(int top)
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "space", "dev", "--top", top.ToString()
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public async Task Space_rejects_timeout_outside_one_to_three_hundred(int timeout)
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "space", "dev", "--timeout", timeout.ToString()
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
    }

    [Theory]
    [InlineData("a.b.c")]
    [InlineData("a.b.c.d")]
    [InlineData(".Runs")]
    [InlineData("audit.")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Space_rejects_invalid_object_specs(string objectSpec)
    {
        var module = new FakeModule();
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "space", "dev", "--object", objectSpec
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Contains("--object", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Space_preserves_profile_vars()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "space", "dev", "--var", "tenant=acme", "--var", "env=uat",
            "--object", "Contracts"
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessSpaceOperation>(Assert.Single(module.Operations));
        Assert.Equal("dev", operation.Target.Profile);
        Assert.Equal("acme", operation.Target.Vars["tenant"]);
        Assert.Equal("uat", operation.Target.Vars["env"]);
        Assert.Equal("Contracts", operation.Object);
        Assert.False(operation.Target.UnsafeDirect);
    }

    [Fact]
    public async Task Space_preserves_direct_sql_auth_options()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "space", "--unsafe-direct", "--server", "sql", "--database", "db",
            "--auth", "sql-password", "--sql-user", "runner", "--password-env-var", "SQL_SECRET",
            "--trust-server-certificate", "--object", "dbo.Contracts"
        ]);

        Assert.Equal(0, exit);
        var target = Assert.IsType<SqlHarnessSpaceOperation>(Assert.Single(module.Operations)).Target;
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
    public async Task Space_rejects_profile_with_direct_options()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "space", "dev", "--server", "sql", "--database", "db", "--auth", "azure-cli"
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
    }

    private sealed class FakeModule : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            var identity = new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile");
            var report = new SqlHarnessSpaceReport(
                identity,
                [],
                new DatabaseAllocationReport(0m, 0m, 0m),
                [],
                []);
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null));
        }
    }
}
