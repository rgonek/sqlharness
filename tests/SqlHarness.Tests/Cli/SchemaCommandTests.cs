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

    [Fact]
    public async Task Schema_dispatches_object_mode()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "schema", "dev", "--object", "SyncRuns", "--timeout", "12", "--json"
        ]);

        Assert.Equal(0, exit);
        var op = Assert.IsType<SqlHarnessSchemaOperation>(Assert.Single(module.Operations));
        Assert.Equal("SyncRuns", op.Object);
        Assert.Null(op.Filter);
        Assert.Equal(12, op.TimeoutSeconds);
    }

    [Fact]
    public async Task Schema_rejects_object_combined_with_filter()
    {
        var module = new FakeModule();
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "schema", "dev", "--object", "SyncRuns", "--filter", "%Order%"
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Contains("--object", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--filter", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Schema_object_mode_preserves_profile_and_direct_options()
    {
        var module = new FakeModule();
        var app = SqlHarnessCli.Create(module, new StringWriter());

        Assert.Equal(0, await app.RunAsync(["schema", "dev", "--var", "env=a", "--object", "dbo.Orders"]));
        var profileOp = Assert.IsType<SqlHarnessSchemaOperation>(module.Operations[0]);
        Assert.Equal("dev", profileOp.Target.Profile);
        Assert.Equal("a", profileOp.Target.Vars["env"]);
        Assert.Equal("dbo.Orders", profileOp.Object);

        Assert.Equal(0, await app.RunAsync([
            "schema", "--unsafe-direct", "--server", "s", "--database", "d", "--auth", "azure-cli",
            "--object", "audit.Runs"
        ]));
        var directOp = Assert.IsType<SqlHarnessSchemaOperation>(module.Operations[1]);
        Assert.True(directOp.Target.UnsafeDirect);
        Assert.Equal("s", directOp.Target.Server);
        Assert.Equal("audit.Runs", directOp.Object);
    }

    private sealed class FakeModule : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) { Operations.Add(operation); return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, new SqlHarnessSchemaReport(new("s", "d", "s", "d", "profile"), [], 0), null)); }
    }
}