using System.Globalization;
using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
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

    [Fact]
    public async Task Indexes_text_renders_target_top_object_artifact_and_columns()
    {
        const string secretFilter = "SecretFilterPredicate=1";
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            var report = Report(30, "dbo.Contracts", @"C:\artifacts\indexes-run",
            [
                Candidate(11, "dbo", "Contracts", IndexOverlapClassification.Covered,
                    ["TenantId"], [], ["Name", "Status"], 120, 3, 2.5m, 75.5m, 180.75m,
                    "IX_Contracts_Tenant", 1, 1, [], true, false, "9F2CAAF1B0",
                    new DateTimeOffset(2026, 7, 29, 12, 34, 56, TimeSpan.FromHours(2)),
                    new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero)),
                Candidate(12, "dbo", "Orders", IndexOverlapClassification.NewShape,
                    ["CustomerId"], ["PlacedAt"], [], 8, 0, 1.25m, 40m, 12.5m,
                    null, 0, 2, [], null, null, null, null, null),
            ]);
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(new FakeReportModule(report), output).RunAsync(["indexes", "dev"]);

            Assert.Equal(0, exit);
            var text = output.ToString();
            var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("Target: sql-server/app-db (profile)", lines[0]);
            Assert.Equal("Top: 30", lines[1]);
            Assert.Equal("Object: dbo.Contracts", lines[2]);
            Assert.Equal(@"Artifacts: C:\artifacts\indexes-run", lines[3]);
            Assert.Equal(
                "CandidateId\tTable\tClassification\tEquality\tInequality\tInclude\tSeeks\tScans\tAverageCost\tAverageImpact\tScore\tBestIndex\tMatchedKeys\tCandidateKeys\tMissingInclude\tDisabled\tFiltered\tFilterHash\tLastSeek\tLastScan",
                lines[4]);
            Assert.Equal(
                "11\tdbo.Contracts\tcovered\tTenantId\t\tName,Status\t120\t3\t2.5\t75.5\t180.75\tIX_Contracts_Tenant\t1\t1\t\ttrue\tfalse\t9F2CAAF1B0\t2026-07-29T12:34:56.0000000+02:00\t2026-07-29T12:00:00.0000000+00:00",
                lines[5]);
            Assert.Equal(
                "12\tdbo.Orders\tnew-shape\tCustomerId\tPlacedAt\t\t8\t0\t1.25\t40\t12.5\t\t0\t2\t\t\t\t\t\t",
                lines[6]);
            Assert.Equal(7, lines.Length);
            Assert.DoesNotContain("requested-host", text, StringComparison.Ordinal);
            Assert.DoesNotContain("2,5", text, StringComparison.Ordinal);
            Assert.DoesNotContain("No missing-index candidates", text, StringComparison.Ordinal);
            Assert.DoesNotContain(secretFilter, text, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Indexes_text_renders_warning_and_empty_object_message()
    {
        var report = Report(20, "dbo.Contracts", @"C:\artifacts\indexes-empty", [], [EvidenceWarning]);
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(new FakeReportModule(report), output).RunAsync(["indexes", "dev"]);

        Assert.Equal(0, exit);
        var text = output.ToString();
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
        [
            "Target: sql-server/app-db (profile)",
            "Top: 20",
            "Object: dbo.Contracts",
            @"Artifacts: C:\artifacts\indexes-empty",
            "Warning: " + EvidenceWarning,
            "No missing-index candidates for dbo.Contracts in the current DMV evidence.",
        ],
        lines);
        Assert.DoesNotContain("CandidateId", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Gain_text_and_json_include_indexes_scope()
    {
        var indexes = new SqlHarnessGainSummary(1, 0, 4, 16, 1, 4, 1, 4, 1, 3);
        var empty = new SqlHarnessGainSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var report = new SqlHarnessGainReport(indexes, empty, empty) { Indexes = indexes };
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);

        var text = new StringWriter();
        new Renderer().Render(outcome, OutputMode.Text, new OutputCaptureWriter(text));
        Assert.Contains("indexes\t1\t0\t3\t75", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("total\t1\t0\t3\t75", text.ToString(), StringComparison.Ordinal);

        var json = new StringWriter();
        new Renderer().Render(outcome, OutputMode.Json, new OutputCaptureWriter(json));
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal(1, document.RootElement.GetProperty("indexes").GetProperty("executions").GetInt64());
        Assert.Equal(1, document.RootElement.GetProperty("total").GetProperty("executions").GetInt64());
    }

    private const string EvidenceWarning =
        "Missing-index evidence is cumulative since SQL Server start and can be shortened or reset by restart, failover, index DDL, or a DMV clear.";

    private static SqlHarnessIndexesReport Report(
        int top,
        string? objectFilter,
        string artifactDirectory,
        IReadOnlyList<IndexCandidateReport> candidates,
        IReadOnlyList<string>? warnings = null) =>
        new(
            new SqlHarnessTargetIdentityReport("requested-host", "requested-db", "sql-server", "app-db", "profile"),
            new DateTimeOffset(2026, 7, 29, 1, 2, 3, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero),
            top,
            objectFilter,
            warnings ?? [],
            candidates,
            artifactDirectory);

    private static IndexCandidateReport Candidate(
        long candidateId,
        string schema,
        string table,
        IndexOverlapClassification classification,
        IReadOnlyList<string> equality,
        IReadOnlyList<string> inequality,
        IReadOnlyList<string> include,
        long seeks,
        long scans,
        decimal averageCost,
        decimal averageImpact,
        decimal score,
        string? bestIndex,
        int matchedKeys,
        int candidateKeys,
        IReadOnlyList<string> missingInclude,
        bool? disabled,
        bool? filtered,
        string? filterHash,
        DateTimeOffset? lastSeek,
        DateTimeOffset? lastScan) =>
        new(
            candidateId,
            schema,
            table,
            equality,
            inequality,
            include,
            seeks,
            scans,
            averageCost,
            averageImpact,
            score,
            lastSeek,
            lastScan,
            classification,
            bestIndex,
            matchedKeys,
            candidateKeys,
            missingInclude,
            disabled,
            filtered,
            filterHash);

    private sealed class FakeReportModule(SqlHarnessIndexesReport report) : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null));
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