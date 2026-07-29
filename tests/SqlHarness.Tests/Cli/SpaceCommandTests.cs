using System.Globalization;
using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class SpaceCommandTests
{
    [Fact]
    public async Task Space_text_renders_sections_in_order_with_invariant_mb()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            var module = new FakeModule(SpaceReport());
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(module, output).RunAsync(["space", "dev"]);

            Assert.Equal(0, exit);
            var text = output.ToString();
            var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("Files", lines[0]);
            Assert.Equal(@"Primary	ROWS	C:\data.mdf	200.5	80	119.5", lines[1]);
            Assert.Equal("Allocation", lines[2]);
            Assert.Equal("100.25	80	60", lines[3]);
            Assert.Equal("Tables", lines[4]);
            Assert.Equal("dbo	Contracts	42	50.5	40	30", lines[5]);
            Assert.Equal("Indexes", lines[6]);
            Assert.Equal("dbo	Contracts	IX_Contracts_Date	NONCLUSTERED	12.75	10	8	PAGE", lines[7]);
            Assert.DoesNotContain(",", text, StringComparison.Ordinal);
            Assert.Contains(@"C:\data.mdf", lines[1], StringComparison.Ordinal);
            Assert.DoesNotContain(@"C:\data.mdf", string.Join('\n', lines.Skip(2)), StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Space_text_omits_indexes_section_when_empty()
    {
        var report = SpaceReport() with { Indexes = [] };
        var module = new FakeModule(report);
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["space", "dev"]);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("Files", text, StringComparison.Ordinal);
        Assert.Contains("Allocation", text, StringComparison.Ordinal);
        Assert.Contains("Tables", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Indexes", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Space_json_round_trips_typed_report()
    {
        var report = SpaceReport();
        var module = new FakeModule(report);
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["space", "dev", "--json"]);

        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal(report.Target.ActualServer, root.GetProperty("target").GetProperty("actualServer").GetString());
        var file = Assert.Single(root.GetProperty("files").EnumerateArray());
        Assert.Equal("Primary", file.GetProperty("logicalName").GetString());
        Assert.Equal("ROWS", file.GetProperty("type").GetString());
        Assert.Equal(@"C:\data.mdf", file.GetProperty("physicalName").GetString());
        Assert.Equal(200.5m, file.GetProperty("sizeMb").GetDecimal());
        Assert.Equal(100.25m, root.GetProperty("allocation").GetProperty("reservedMb").GetDecimal());
        var table = Assert.Single(root.GetProperty("tables").EnumerateArray());
        Assert.Equal("Contracts", table.GetProperty("name").GetString());
        Assert.Equal(42, table.GetProperty("rows").GetInt64());
        var index = Assert.Single(root.GetProperty("indexes").EnumerateArray());
        Assert.Equal("IX_Contracts_Date", index.GetProperty("index").GetString());
        Assert.Equal("PAGE", index.GetProperty("compression").GetString());
        Assert.DoesNotContain("Password=", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

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

    private static SqlHarnessSpaceReport SpaceReport() =>
        new(
            new SqlHarnessTargetIdentityReport("sql-server", "app-db", "sql-server", "app-db", "profile"),
            [
                new DatabaseFileSpaceReport("Primary", "ROWS", @"C:\data.mdf", 200.5m, 80m, 119.5m),
            ],
            new DatabaseAllocationReport(100.25m, 80m, 60m),
            [
                new TableSpaceReport("dbo", "Contracts", 42, 50.5m, 40m, 30m),
            ],
            [
                new IndexSpaceReport(
                    "dbo", "Contracts", "IX_Contracts_Date", "NONCLUSTERED",
                    12.75m, 10m, 8m, "PAGE"),
            ]);

    private sealed class FakeModule : ISqlHarnessModule
    {
        private readonly SqlHarnessOutcome _outcome;

        public List<SqlHarnessOperation> Operations { get; } = [];

        public FakeModule()
            : this(new SqlHarnessOutcome(
                SqlHarnessExitCode.Success,
                new SqlHarnessSpaceReport(
                    new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
                    [],
                    new DatabaseAllocationReport(0m, 0m, 0m),
                    [],
                    []),
                null))
        {
        }

        public FakeModule(object report)
            : this(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null))
        {
        }

        public FakeModule(SqlHarnessOutcome outcome) => _outcome = outcome;

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            return Task.FromResult(_outcome);
        }
    }
}
