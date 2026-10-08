using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public class ArtifactReaderTests
{
    private const string Secret = "SECRET-9f2c-artifact-content-must-not-leak";

    [Fact]
    public void CompareWorkflow_SummaryMatchesSavedReport_WithoutRebenchmark()
    {
        using var temp = new TempDirectory();
        var report = CompareReport();
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 1, 2, 3,
                new Dictionary<string, long> { ["Clients"] = 3 }, "HASH", [FixturePlan()], 0),
            new CompareRunArtifact("candidate", 1, 4, 5, 6,
                new Dictionary<string, long> { ["Clients"] = 6 }, "HASH", [FixturePlan()], 0),
        };
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(report, runs, "wind");
        var id = Path.GetFileName(directory);
        var before = FileHashes(directory);

        Assert.True(File.Exists(Path.Combine(directory, "manifest.json")));

        var summary = ArtifactReader.ReadSection(temp.Path, id, "summary");
        var metrics = Assert.IsType<ArtifactMetricsSection>(ArtifactReader.ReadSection(temp.Path, id, "metrics"));
        var operators = Assert.IsType<ArtifactOperatorsSection>(ArtifactReader.ReadSection(temp.Path, id, "operators"));

        var expected = BenchmarkSummaryProjector.Project(report with { ArtifactDirectory = directory });
        Assert.Equal(
            JsonSerializer.Serialize(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        Assert.Equal("compare", metrics.ArtifactKind);
        Assert.Equal(["baseline", "candidate"], metrics.Variants.Select(variant => variant.Name).ToArray());
        Assert.Equal(20, metrics.Variants[0].ElapsedTimeMilliseconds.Median);
        Assert.Equal(5, metrics.Variants[1].ElapsedTimeMilliseconds.Median);
        Assert.NotNull(metrics.Equivalence);
        Assert.True(metrics.Equivalence!.Equivalent);
        Assert.Contains(operators.Operators, op => op.PhysicalOp == "Index Seek");

        var payload = JsonSerializer.Serialize(new { summary, metrics, operators });
        Assert.DoesNotContain("HASH", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("planFiles", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("runs.jsonl", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", payload, StringComparison.Ordinal);

        // The reader only opens manifest.json and report.json: nothing is re-run
        // and no artifact file changes.
        Assert.Equal(before, FileHashes(directory));
    }

    [Fact]
    public void Statements_section_returns_per_statement_metrics_without_sql_text()
    {
        using var temp = new TempDirectory();
        var report = CompareReport();
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 30, 40, 0,
                new Dictionary<string, long>(), "hash", [MultiStatementPlan()], 0),
        };

        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(report, runs, "wind");
        var section = ArtifactReader.ReadSection(temp.Path, Path.GetFileName(directory), "statements");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(section, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var statements = json.RootElement.GetProperty("statements").EnumerateArray().ToArray();

        Assert.Equal(2, statements.Length);
        Assert.Equal([0, 1], statements.Select(item => item.GetProperty("statementOrdinal").GetInt32()).ToArray());
        Assert.Equal(30, statements.Sum(item => item.GetProperty("cpuTimeMilliseconds").GetInt64()));
        Assert.Equal(40, statements.Sum(item => item.GetProperty("elapsedTimeMilliseconds").GetInt64()));
        Assert.All(statements, item => Assert.Equal(64, item.GetProperty("statementHash").GetString()!.Length));
        Assert.Equal(2, statements[0].GetProperty("degreeOfParallelism").GetInt32());
        var statementPayload = JsonSerializer.Serialize(section);
        Assert.Contains("Index Seek", statementPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE dbo.SecretTable", statementPayload, StringComparison.Ordinal);
    }

    [Fact]
    public void MeasureAndMeasureSet_AllSectionsRead()
    {
        using var temp = new TempDirectory();
        var writer = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch);
        var measure = new SqlHarnessMeasureReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            5, 5, true, Variant("query"), null);
        var measureSet = new SqlHarnessMeasureSetReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            5, 10, 1, ["small", "large"], "Measured in user order.", "Shared plan cache.",
            [
                new MeasureParameterSetReport("small", [new MeasureParameterMetadata("n", "int")],
                    "vh1", 5, true, "RH", Variant("query"), ["ph1"]),
                new MeasureParameterSetReport("large", [new MeasureParameterMetadata("n", "int")],
                    "vh2", 5, false, "RH", Variant("query"), ["ph2"]),
            ],
            new MeasureCrossSetSummary("small", 3, "large", 3, "small", 2, "large", 2, "small", 4, "large", 4),
            null);
        var run = new CompareRunArtifact("query", 1, 1, 2, 3,
            new Dictionary<string, long>(), "HASH", [FixturePlan()], 0);

        var measureId = Path.GetFileName(writer.Write(measure, [run], "m"));
        var setId = Path.GetFileName(writer.Write(measureSet, [run, run with { ParameterSet = "large" }], "s"));

        var measureMetrics = Assert.IsType<ArtifactMetricsSection>(ArtifactReader.ReadSection(temp.Path, measureId, "metrics"));
        Assert.Equal(["query"], measureMetrics.Variants.Select(variant => variant.Name).ToArray());
        Assert.Null(measureMetrics.Equivalence);

        var setMetrics = Assert.IsType<ArtifactMetricsSection>(ArtifactReader.ReadSection(temp.Path, setId, "metrics"));
        Assert.Equal(["small", "large"], setMetrics.Variants.Select(variant => variant.Name).ToArray());

        var setOperators = Assert.IsType<ArtifactOperatorsSection>(ArtifactReader.ReadSection(temp.Path, setId, "operators"));
        Assert.Equal(ArtifactReader.MeasureSetKind, setOperators.ArtifactKind);
        Assert.True(setOperators.Operators.Count <= 10);

        Assert.IsType<MeasureBenchmarkSummary>(ArtifactReader.ReadSection(temp.Path, measureId, "summary"));
        Assert.IsType<MeasureSetBenchmarkSummary>(ArtifactReader.ReadSection(temp.Path, setId, "summary"));
    }

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("-leading-dash")]
    [InlineData("trailing-dot.")]
    public void UnknownOrTraversalId_IsRefusedWithoutContent(string id)
    {
        using var temp = new TempDirectory();

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Equal(SqlHarnessExitCode.Safety, exception.ExitCode);
        Assert.Equal("Unknown artifact id.", exception.Message);
    }

    [Fact]
    public void OverlongId_IsRefusedWithoutContent()
    {
        using var temp = new TempDirectory();

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, new string('a', 129), "summary"));

        Assert.Equal("Unknown artifact id.", exception.Message);
    }

    [Fact]
    public void LegacyArtifactWithoutManifest_IsRefusedWithoutContent()
    {
        using var temp = new TempDirectory();
        var id = "legacy-artifact";
        Directory.CreateDirectory(Path.Combine(temp.Path, id));
        File.WriteAllText(Path.Combine(temp.Path, id, "report.json"), """{"kind":"compare"}""");
        File.WriteAllText(Path.Combine(temp.Path, id, "queries.jsonl"), Secret);

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Equal(SqlHarnessExitCode.Safety, exception.ExitCode);
        Assert.Contains("no versioned manifest", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptManifest_IsRefusedWithoutContent()
    {
        using var temp = new TempDirectory();
        var id = "corrupt-manifest";
        Directory.CreateDirectory(Path.Combine(temp.Path, id));
        File.WriteAllText(Path.Combine(temp.Path, id, "manifest.json"), "{not json " + Secret);

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Equal("Artifact manifest is invalid.", exception.Message);
        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedManifestVersion_IsRefused()
    {
        using var temp = new TempDirectory();
        var id = WriteRawArtifact(temp.Path, "future-artifact",
            """{"manifestVersion":999,"artifactKind":"compare","reportFile":"report.json","sections":["summary"]}""",
            """{"target":null}""");

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Contains("manifest version 999", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestWithUnknownKind_IsRefused()
    {
        using var temp = new TempDirectory();
        var id = WriteRawArtifact(temp.Path, "odd-kind",
            """{"manifestVersion":1,"artifactKind":"snapshot","reportFile":"report.json","sections":["summary"]}""",
            """{}""");

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Equal("Artifact manifest is invalid.", exception.Message);
    }

    [Fact]
    public void ManifestPointingOutsideReportJson_IsRefused()
    {
        using var temp = new TempDirectory();
        var id = WriteRawArtifact(temp.Path, "remapped",
            """{"manifestVersion":1,"artifactKind":"compare","reportFile":"other.json","sections":["summary"]}""",
            """{}""");
        File.WriteAllText(Path.Combine(temp.Path, id, "other.json"), Secret);

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Equal("Artifact manifest is invalid.", exception.Message);
        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SectionOutsideManifestMapping_IsRefused()
    {
        using var temp = new TempDirectory();
        var id = WriteRawArtifact(temp.Path, "narrow",
            """{"manifestVersion":1,"artifactKind":"compare","reportFile":"report.json","sections":["summary"]}""",
            """{}""");

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "metrics"));

        Assert.Contains("not available", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSection_IsRefused()
    {
        using var temp = new TempDirectory();

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, "whatever", "plans"));

        Assert.Contains("summary, metrics, operators", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptReport_IsRefusedWithoutContent()
    {
        using var temp = new TempDirectory();
        var id = WriteRawArtifact(temp.Path, "corrupt-report",
            """{"manifestVersion":1,"artifactKind":"compare","reportFile":"report.json","sections":["summary","metrics","operators"]}""",
            "GARBAGE-" + Secret);

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Equal("Artifact report is invalid.", exception.Message);
        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestKindMismatchingReportContent_IsRefused()
    {
        using var temp = new TempDirectory();
        var measure = new SqlHarnessMeasureReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            1, 1, true, Variant("query"), null);
        var run = new CompareRunArtifact("measure", 1, 1, 2, 3,
            new Dictionary<string, long>(), "HASH", [FixturePlan()], 0);
        var id = Path.GetFileName(new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(measure, [run], "m"));
        File.WriteAllText(
            Path.Combine(temp.Path, id, "manifest.json"),
            """{"manifestVersion":1,"artifactKind":"compare","reportFile":"report.json","sections":["summary","metrics","operators"]}""");

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Equal("Artifact report is invalid.", exception.Message);
    }

    [Fact]
    public void OversizeReport_IsRefusedWithoutReading()
    {
        using var temp = new TempDirectory();
        var id = WriteRawArtifact(temp.Path, "oversize",
            """{"manifestVersion":1,"artifactKind":"compare","reportFile":"report.json","sections":["summary"]}""",
            """{}""");
        using (var stream = new FileStream(Path.Combine(temp.Path, id, "report.json"), FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(ArtifactReader.MaxReportBytes + 1);
        }

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Contains("read limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizeManifest_IsRefusedWithoutReading()
    {
        using var temp = new TempDirectory();
        var id = WriteRawArtifact(temp.Path, "oversize-manifest",
            """{"manifestVersion":1}""",
            """{}""");
        using (var stream = new FileStream(Path.Combine(temp.Path, id, "manifest.json"), FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(ArtifactReader.MaxManifestBytes + 1);
        }

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Contains("read limit", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:\\outside-target", (int)FileAttributes.Directory, true)]
    [InlineData(null, (int)(FileAttributes.Directory | FileAttributes.ReparsePoint), true)]
    [InlineData("relative-target", (int)FileAttributes.Directory, true)]
    [InlineData(null, (int)FileAttributes.Directory, false)]
    [InlineData(null, (int)FileAttributes.Normal, false)]
    public void LinkOrReparsePredicate_RefusesEscapes(string? linkTarget, int attributes, bool expected)
    {
        Assert.Equal(expected, ArtifactReader.IsEscapeLink(linkTarget, (FileAttributes)attributes));
    }

    [Fact]
    public void SymlinkEscape_IsRefusedWithoutContent()
    {
        using var temp = new TempDirectory();
        var outside = Path.Combine(temp.Path, "outside-secret");
        Directory.CreateDirectory(outside);
        var secretId = WriteRawArtifact(outside, "secret-artifact",
            """{"manifestVersion":1,"artifactKind":"compare","reportFile":"report.json","sections":["summary","metrics","operators"]}""",
            Secret);
        var link = Path.Combine(temp.Path, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(outside, secretId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Creating links needs OS privilege; traversal batteries above always run.
            return;
        }

        var thrown = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, "linked", "summary"));

        Assert.Equal("Unknown artifact id.", thrown.Message);
        Assert.DoesNotContain(Secret, thrown.Message, StringComparison.Ordinal);
    }

    // 003/T2: CLI-compat guards. The offline reader takes no owner (opt-in
    // enforcement lives on the MCP path per plans/003-scope-contract.md), so
    // owner metadata — present, foreign, or absent — never changes CLI reads.

    [Fact]
    public void ManifestWithOwnerMetadata_RemainsReadableByCli()
    {
        using var temp = new TempDirectory();
        var id = WriteOwnedCliArtifact(temp.Path, """{"profile": "cli-owner", "vars": {"tenant": "acme"}, "engine": "sqlserver", "server": "safe-server", "database": "safe-db"}""");

        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "summary"));
        var metrics = Assert.IsType<ArtifactMetricsSection>(ArtifactReader.ReadSection(temp.Path, id, "metrics"));
        Assert.Equal(["baseline", "candidate"], metrics.Variants.Select(variant => variant.Name).ToArray());
        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "operators"));
    }

    [Fact]
    public void ManifestWithForeignOwner_RemainsReadableByCli()
    {
        using var temp = new TempDirectory();
        var id = WriteOwnedCliArtifact(temp.Path, """{"profile": "other-profile", "vars": {"tenant": "other"}, "engine": "sqlserver", "server": "other-server", "database": "other-db"}""");

        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "summary"));
        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "metrics"));
        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "operators"));
    }

    [Fact]
    public void ManifestV1WithoutOwner_RemainsReadableByCli()
    {
        using var temp = new TempDirectory();
        var id = WriteOwnedCliArtifact(temp.Path, ownerJson: null);

        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "summary"));
        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "metrics"));
        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "operators"));
    }

    private static string WriteOwnedCliArtifact(string root, string? ownerJson)
    {
        var report = CompareReport();
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 1, 2, 3,
                new Dictionary<string, long> { ["Clients"] = 3 }, "HASH", [FixturePlan()], 0),
            new CompareRunArtifact("candidate", 1, 4, 5, 6,
                new Dictionary<string, long> { ["Clients"] = 6 }, "HASH", [FixturePlan()], 0),
        };
        var directory = new CompareArtifactWriter(root, () => DateTimeOffset.UnixEpoch)
            .Write(report, runs, "wind");
        if (ownerJson is not null)
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            var trimmed = File.ReadAllText(manifestPath).TrimEnd();
            Assert.EndsWith("}", trimmed);
            File.WriteAllText(manifestPath, trimmed[..^1] + ",\"owner\": " + ownerJson + "}");
        }

        return Path.GetFileName(directory);
    }

    private static string WriteRawArtifact(string root, string id, string manifest, string report)
    {
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest, Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "report.json"), report, Encoding.UTF8);
        return id;
    }

    private static SqlHarnessCompareReport CompareReport() => new(
        new SqlHarnessTargetIdentityReport("safe-server", "safe-db", "safe-server", "safe-db", "profile"),
        5, 10, true, Variant("baseline", new CompareDistribution(10, 20, 30), flagged: true), Variant("candidate", new CompareDistribution(4, 5, 6)), null);

    private static CompareVariantReport Variant(string name, CompareDistribution? elapsed = null, bool flagged = false) => new(
        name,
        new CompareDistribution(1, 2, 3),
        elapsed ?? new CompareDistribution(2, 3, 4),
        new CompareDistribution(3, 4, 5),
        new Dictionary<string, long> { ["Clients"] = 4 },
        [new CompareOperatorReport(1, "Index Seek", "Clients", flagged, false, false)],
        flagged ? ["plan-warning"] : []);

    private static string FixturePlan() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "distiller-sample.sqlplan"));

    private static string MultiStatementPlan() => """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements>
        <StmtSimple StatementText="UPDATE dbo.SecretTable SET A = 1"><QueryPlan DegreeOfParallelism="2"><QueryTimeStats CpuTime="10" ElapsedTime="15"/><RelOp NodeId="0" PhysicalOp="Index Seek" EstimateRows="2"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="4" ActualExecutions="1"/></RunTimeInformation><IndexScan><Object Table="[dbo].[SecretTable]" Index="[IX_A]"/></IndexScan></RelOp></QueryPlan></StmtSimple>
        <StmtSimple StatementText="UPDATE dbo.SecretTable SET B = 2"><QueryPlan DegreeOfParallelism="1"><QueryTimeStats CpuTime="20" ElapsedTime="25"/><RelOp NodeId="0" PhysicalOp="Table Scan" EstimateRows="3"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="6" ActualExecutions="2"/></RunTimeInformation><TableScan><Object Table="[dbo].[SecretTable]"/></TableScan></RelOp></QueryPlan></StmtSimple>
        </Statements></Batch></BatchSequence></ShowPlanXML>
        """;

    private static IReadOnlyDictionary<string, string> FileHashes(string directory)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var stream = File.OpenRead(file);
            hashes[Path.GetRelativePath(directory, file)] =
                Convert.ToHexString(SHA256.HashData(stream));
        }
        return hashes;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sqlharness-artifact-read-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}