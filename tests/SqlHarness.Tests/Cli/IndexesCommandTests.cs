using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class IndexesCommandTests
{
    [Fact]
    public async Task Indexes_dispatches_object_top_timeout_and_json()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "indexes", "dev", "--object", "dbo.Contracts",
            "--top", "30", "--timeout", "15", "--json"
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessIndexesOperation>(
            Assert.Single(module.Operations));
        Assert.Equal("dbo.Contracts", operation.Object);
        Assert.Equal(30, operation.Top);
        Assert.Equal(15, operation.TimeoutSeconds);
    }

    [Fact]
    public async Task Indexes_defaults_top_and_timeout_without_object()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(["indexes", "dev"]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessIndexesOperation>(Assert.Single(module.Operations));
        Assert.Equal("dev", operation.Target.Profile);
        Assert.Equal(20, operation.Top);
        Assert.Null(operation.Object);
        Assert.Equal(30, operation.TimeoutSeconds);
        Assert.False(operation.Target.UnsafeDirect);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task Indexes_rejects_top_outside_one_to_five_hundred(int top)
    {
        var module = new FakeModule();
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "indexes", "dev", "--top", top.ToString()
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Contains("--timeout must be 1..300 and --top must be 1..500.", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public async Task Indexes_rejects_timeout_outside_one_to_three_hundred(int timeout)
    {
        var module = new FakeModule();
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "indexes", "dev", "--timeout", timeout.ToString()
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Contains("--timeout must be 1..300 and --top must be 1..500.", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.b.c")]
    [InlineData("a.b.c.d")]
    [InlineData(".Runs")]
    [InlineData("audit.")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Indexes_rejects_invalid_object_specs(string objectSpec)
    {
        var module = new FakeModule();
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "indexes", "dev", "--object", objectSpec
        ]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Contains(
            "indexes --object must be a single object name or schema.name.",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Indexes_preserves_profile_vars()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "indexes", "dev", "--var", "tenant=acme", "--var", "env=uat",
            "--object", "Contracts"
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessIndexesOperation>(Assert.Single(module.Operations));
        Assert.Equal("dev", operation.Target.Profile);
        Assert.Equal("acme", operation.Target.Vars["tenant"]);
        Assert.Equal("uat", operation.Target.Vars["env"]);
        Assert.Equal("Contracts", operation.Object);
        Assert.False(operation.Target.UnsafeDirect);
    }

    [Fact]
    public async Task Indexes_preserves_direct_target_options()
    {
        var module = new FakeModule();
        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
            "indexes", "--unsafe-direct", "--server", "sql", "--database", "db",
            "--auth", "sql-password", "--sql-user", "runner", "--password-env-var", "SQL_SECRET",
            "--trust-server-certificate", "--engine", "postgres",
            "--object", "dbo.Contracts"
        ]);

        Assert.Equal(0, exit);
        var operation = Assert.IsType<SqlHarnessIndexesOperation>(Assert.Single(module.Operations));
        var target = operation.Target;
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
        Assert.Equal("dbo.Contracts", operation.Object);
    }

    [Fact]
    public async Task Indexes_rejects_mutation_file_and_confirm_database_options()
    {
        foreach (var args in new[]
        {
            new[] { "indexes", "dev", "--allow-mutation" },
            new[] { "indexes", "dev", "--file", "queries.sql" },
            new[] { "indexes", "dev", "--confirm-database", "app-db" },
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

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("Contracts", null, "Contracts")]
    [InlineData("dbo.Contracts", "dbo", "Contracts")]
    [InlineData("[dbo].[Contracts]", "[dbo]", "[Contracts]")]
    public void Index_object_syntax_splits_name_without_stripping_brackets(string? objectSpec, string? schema, string? name)
    {
        Assert.True(IndexObjectSyntax.TryParse(objectSpec, out var actualSchema, out var actualName, out var error));
        Assert.Equal(schema, actualSchema);
        Assert.Equal(name, actualName);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void Index_overlap_classification_json_uses_documented_names()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal("\"covered\"", JsonSerializer.Serialize(IndexOverlapClassification.Covered, options));
        Assert.Equal("\"include-gap\"", JsonSerializer.Serialize(IndexOverlapClassification.IncludeGap, options));
        Assert.Equal("\"partial-key\"", JsonSerializer.Serialize(IndexOverlapClassification.PartialKey, options));
        Assert.Equal("\"new-shape\"", JsonSerializer.Serialize(IndexOverlapClassification.NewShape, options));
        Assert.Equal(IndexOverlapClassification.Covered, JsonSerializer.Deserialize<IndexOverlapClassification>("\"covered\"", options));
        Assert.Equal(IndexOverlapClassification.IncludeGap, JsonSerializer.Deserialize<IndexOverlapClassification>("\"include-gap\"", options));
        Assert.Equal(IndexOverlapClassification.PartialKey, JsonSerializer.Deserialize<IndexOverlapClassification>("\"partial-key\"", options));
        Assert.Equal(IndexOverlapClassification.NewShape, JsonSerializer.Deserialize<IndexOverlapClassification>("\"new-shape\"", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IndexOverlapClassification>("\"Covered\"", options));
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