using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class SchemaCommandTests
{
    [Fact]
    public async Task Schema_parser_dispatches_profile_filter_bounds_and_json()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(["schema", "dev", "--var", "env=a", "--filter", "%Order%", "--max-objects", "75", "--timeout", "12", "--json"]);
        Assert.Equal(0, exit);
        var op = Assert.IsType<SqlHarnessSchemaOperation>(Assert.Single(module.Operations));
        Assert.Equal("dev", op.Target.Profile); Assert.Equal("a", op.Target.Vars["env"]); Assert.Equal("%Order%", op.Filter); Assert.Equal(75, op.MaxObjects); Assert.Equal(12, op.TimeoutSeconds);
    }

    [Fact]
    public async Task Schema_supports_direct_sql_auth_and_rejects_invalid_bounds()
    {
        var module = new FakeModule();
        var app = SqlHarnessCli.Create(module, new StringWriter());
        Assert.Equal(0, await app.RunAsync(["schema", "--unsafe-direct", "--server", "s", "--database", "d", "--auth", "sql-password", "--sql-user", "u", "--password-env-var", "P", "--trust-server-certificate"]));
        var target = Assert.IsType<SqlHarnessSchemaOperation>(module.Operations[0]).Target;
        Assert.Equal("u", target.SqlUser); Assert.Equal("P", target.PasswordEnvVar); Assert.True(target.TrustServerCertificate);
        Assert.Equal((int)SqlHarnessExitCode.Safety, await app.RunAsync(["schema", "dev", "--max-objects", "501"]));
        Assert.Single(module.Operations);
    }

    private sealed class FakeModule : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) { Operations.Add(operation); return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, new SqlHarnessSchemaReport(new("s", "d", "s", "d", "profile"), [], 0), null)); }
    }
}