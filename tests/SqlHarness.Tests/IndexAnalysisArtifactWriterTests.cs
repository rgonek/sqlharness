using System.Text;
using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

[Collection(SqlHarnessHomeCollection.Name)]
public sealed class IndexAnalysisArtifactWriterTests
{
    private const string FilterA = "[Status]=(1)";
    private const string FilterB = "City = N'secret-filter'";
    private const string Mismatch = "Index analysis artifacts do not match the existing indexes.";
    private const string MismatchFilter = "CREATE INDEX SecretFilter ON dbo.T (A) WHERE DROP INDEX ALTER INDEX";
    private const string EvidenceWarning =
        "Missing-index evidence is cumulative since SQL Server start and can be shortened or reset by restart, failover, index DDL, or a DMV clear.";

    [Fact]
    public void Index_analysis_directory_is_under_the_sqlharness_home()
    {
        var home = SqlHarnessPaths.Home;
        Assert.Equal(Path.Combine(home, "index-analysis"), SqlHarnessPaths.IndexAnalysisDir);
    }

    [Fact]
    public void Writer_keeps_filter_text_only_in_existing_indexes_jsonl()
    {
        using var temp = new TempDirectory();
        var writer = new IndexAnalysisArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch);
        var report = Fixture.Report(
            candidates:
            [
                Fixture.PublicCandidate(11, IndexOverlapClassification.PartialKey),
                Fixture.PublicCandidate(7, IndexOverlapClassification.NewShape, schema: "dbo", table: "Invoices"),
            ]);
        var candidates = new[]
        {
            Fixture.Candidate(9),
            Fixture.Candidate(7, table: "Invoices", equality: ["InvoiceId"], inequality: [], includes: []),
        };
        var indexes = new[]
        {
            Fixture.Index(indexId: 5, name: "IX_B", filterHash: "HASHB"),
            Fixture.Index(indexId: 2, name: "IX_A", filterHash: "HASHA"),
        };
        var sensitive = new[]
        {
            Fixture.Sensitive(2, FilterA, name: "NOT_THE_INDEX_NAME"),
            Fixture.Sensitive(5, FilterB, name: "ALSO_NOT_THE_NAME"),
        };

        var directory = writer.Write(report, candidates, indexes, sensitive, "wind/../../unsafe");

        Assert.Null(report.ArtifactDirectory);
        Assert.Equal(
            new[] { "candidates.jsonl", "existing-indexes.jsonl", "report.json" },
            Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(
            Directory.GetDirectories(temp.Path),
            path => Path.GetFileName(path).Contains(".staging-", StringComparison.Ordinal));
        AssertSafeChild(temp.Path, directory, "wind-unsafe");

        var reportBytes = File.ReadAllBytes(Path.Combine(directory, "report.json"));
        var reportJson = Encoding.UTF8.GetString(reportBytes);
        AssertNoBom(reportBytes);
        Assert.Contains("\n", reportJson, StringComparison.Ordinal);
        AssertNoFilterOrDdl(reportJson);
        using (var document = JsonDocument.Parse(reportJson))
        {
            Assert.Equal(directory, document.RootElement.GetProperty("artifactDirectory").GetString());
            Assert.Equal(EvidenceWarning, document.RootElement.GetProperty("warnings")[0].GetString());
            Assert.Equal(2, document.RootElement.GetProperty("candidates").GetArrayLength());
            Assert.Equal("partial-key", document.RootElement.GetProperty("candidates")[0].GetProperty("classification").GetString());
            Assert.False(document.RootElement.TryGetProperty("filterDefinition", out _));
        }

        var candidateBytes = File.ReadAllBytes(Path.Combine(directory, "candidates.jsonl"));
        AssertNoBom(candidateBytes);
        var candidateLines = File.ReadAllLines(Path.Combine(directory, "candidates.jsonl"));
        Assert.Equal(2, candidateLines.Length);
        AssertCandidateLine(candidateLines[0], candidates[0]);
        AssertCandidateLine(candidateLines[1], candidates[1]);
        AssertNoFilterOrDdl(File.ReadAllText(Path.Combine(directory, "candidates.jsonl")));

        var indexBytes = File.ReadAllBytes(Path.Combine(directory, "existing-indexes.jsonl"));
        AssertNoBom(indexBytes);
        var indexLines = File.ReadAllLines(Path.Combine(directory, "existing-indexes.jsonl"));
        Assert.Equal(2, indexLines.Length);
        AssertIndexLine(indexLines[0], indexes[0], FilterB);
        AssertIndexLine(indexLines[1], indexes[1], FilterA);
        var indexJsonl = File.ReadAllText(Path.Combine(directory, "existing-indexes.jsonl"));
        Assert.Contains(FilterA, indexJsonl, StringComparison.Ordinal);
        Assert.Contains("secret-filter", indexJsonl, StringComparison.Ordinal);
        AssertNoDdlOrQuery(indexJsonl);
        Assert.DoesNotContain("NOT_THE_INDEX_NAME", indexJsonl, StringComparison.Ordinal);
        Assert.DoesNotContain("ALSO_NOT_THE_NAME", indexJsonl, StringComparison.Ordinal);
    }

    [Fact]
    public void Writer_matches_sensitive_rows_by_schema_and_table_case_insensitively()
    {
        using var temp = new TempDirectory();
        var writer = new IndexAnalysisArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch);
        var index = Fixture.Index(schema: "dbo", table: "Contracts", indexId: 2);
        var sensitive = Fixture.Sensitive(2, FilterA, schema: "DBO", table: "contracts", name: "other");

        var directory = writer.Write(Fixture.Report(), [Fixture.Candidate()], [index], [sensitive], "db");

        var line = Assert.Single(File.ReadAllLines(Path.Combine(directory, "existing-indexes.jsonl")));
        AssertIndexLine(line, index, FilterA);
    }

    [Fact]
    public void Writer_creates_unique_safe_directory_names()
    {
        using var temp = new TempDirectory();
        var writer = new IndexAnalysisArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch);
        var candidates = new[] { Fixture.Candidate() };
        var indexes = new[] { Fixture.Index() };
        var sensitive = new[] { Fixture.Sensitive(2, FilterA) };

        var first = writer.Write(Fixture.Report(), candidates, indexes, sensitive, "wind/../../unsafe");
        var second = writer.Write(Fixture.Report(), candidates, indexes, sensitive, "wind/../../unsafe");
        var escaped = writer.Write(Fixture.Report(), candidates, indexes, sensitive, "../../");

        Assert.NotEqual(first, second);
        AssertSafeChild(temp.Path, first, "wind-unsafe");
        AssertSafeChild(temp.Path, second, "wind-unsafe");
        AssertSafeChild(temp.Path, escaped, "target");
        Assert.DoesNotContain(FilterA, Path.GetFileName(first), StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE INDEX", Path.GetFileName(first), StringComparison.Ordinal);
    }

    [Fact]
    public void Writer_writes_empty_jsonl_files_for_an_empty_success()
    {
        using var temp = new TempDirectory();
        var writer = new IndexAnalysisArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch);

        var directory = writer.Write(Fixture.Report(candidates: []), [], [], [], "db");

        Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(directory, "candidates.jsonl")));
        Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(directory, "existing-indexes.jsonl")));
        Assert.Empty(File.ReadAllLines(Path.Combine(directory, "candidates.jsonl")));
        Assert.Empty(File.ReadAllLines(Path.Combine(directory, "existing-indexes.jsonl")));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "report.json")));
        Assert.Equal(0, document.RootElement.GetProperty("candidates").GetArrayLength());
        Assert.Equal(directory, document.RootElement.GetProperty("artifactDirectory").GetString());
        AssertNoFilterOrDdl(File.ReadAllText(Path.Combine(directory, "report.json")));
    }

    [Fact]
    public void Writer_rejects_index_mismatch_without_publishing()
    {
        using var temp = new TempDirectory();
        var writer = new IndexAnalysisArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch);
        var secret = Fixture.Sensitive(2, MismatchFilter);
        (ExistingIndex[] Indexes, SensitiveExistingIndex[] Sensitive)[] cases =
        [
            ([Fixture.Index(indexId: 2)], [Fixture.Sensitive(9, MismatchFilter)]),
            ([Fixture.Index(schema: "dbo")], [Fixture.Sensitive(2, MismatchFilter, schema: "sales")]),
            ([Fixture.Index(table: "Contracts")], [Fixture.Sensitive(2, MismatchFilter, table: "Orders")]),
            ([Fixture.Index(indexId: 2)], [secret, Fixture.Sensitive(3, MismatchFilter)]),
            ([Fixture.Index(indexId: 2), Fixture.Index(indexId: 3)], [secret]),
            ([Fixture.Index(indexId: 2)], [secret, Fixture.Sensitive(2, MismatchFilter, schema: "DBO", table: "contracts")]),
            ([Fixture.Index(indexId: 2), Fixture.Index(indexId: 2, schema: "DBO", table: "contracts")], [secret]),
        ];

        foreach (var (indexes, sensitive) in cases)
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                writer.Write(Fixture.Report(), [Fixture.Candidate()], indexes, sensitive, "wind/../../unsafe"));
            Assert.Equal(Mismatch, exception.Message);
            Assert.DoesNotContain(MismatchFilter, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("CREATE INDEX", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("DROP INDEX", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("ALTER INDEX", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SecretFilter", exception.Message, StringComparison.Ordinal);
        }

        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public void Report_write_failure_leaves_no_final_directory_or_filter()
    {
        using var temp = new TempDirectory();
        var writer = FailingWriter(temp.Path, "report.json", "report failure", beforeWrite: true);

        var exception = Assert.Throws<IOException>(() => WriteDefault(writer));

        Assert.Equal("report failure", exception.Message);
        AssertNoLeak(temp.Path, exception);
    }

    [Fact]
    public void Candidates_jsonl_write_failure_leaves_no_final_directory_or_filter()
    {
        using var temp = new TempDirectory();
        var writer = FailingWriter(temp.Path, "candidates.jsonl", "candidates failure", beforeWrite: true);

        var exception = Assert.Throws<IOException>(() => WriteDefault(writer));

        Assert.Equal("candidates failure", exception.Message);
        AssertNoLeak(temp.Path, exception);
    }

    [Fact]
    public void Existing_indexes_jsonl_write_failure_leaves_no_final_directory_or_filter()
    {
        using var temp = new TempDirectory();
        var writer = FailingWriter(temp.Path, "existing-indexes.jsonl", "indexes failure", beforeWrite: true);

        var exception = Assert.Throws<IOException>(() => WriteDefault(writer));

        Assert.Equal("indexes failure", exception.Message);
        AssertNoLeak(temp.Path, exception);
    }

    [Fact]
    public void Directory_move_failure_leaves_no_final_directory_or_filter()
    {
        using var temp = new TempDirectory();
        var writer = new IndexAnalysisArtifactWriter(
            temp.Path,
            () => DateTimeOffset.UnixEpoch,
            File.WriteAllText,
            (_, _) => throw new IOException("move failure"));

        var exception = Assert.Throws<IOException>(() => WriteDefault(writer));

        Assert.Equal("move failure", exception.Message);
        AssertNoLeak(temp.Path, exception);
    }

    [Fact]
    public void Directory_move_that_publishes_then_throws_removes_the_final_directory()
    {
        using var temp = new TempDirectory();
        var writer = new IndexAnalysisArtifactWriter(
            temp.Path,
            () => DateTimeOffset.UnixEpoch,
            File.WriteAllText,
            (source, destination) =>
            {
                Directory.Move(source, destination);
                throw new IOException("move failure after publish");
            });

        var exception = Assert.Throws<IOException>(() => WriteDefault(writer));

        Assert.Equal("move failure after publish", exception.Message);
        AssertNoLeak(temp.Path, exception);
    }

    [Fact]
    public void Cleanup_is_attempted_after_failure_and_preserves_the_original_exception()
    {
        using var temp = new TempDirectory();
        var deletes = new List<string>();
        var directoryDeletes = new List<string>();
        var writer = new IndexAnalysisArtifactWriter(
            temp.Path,
            () => DateTimeOffset.UnixEpoch,
            (path, content, encoding) =>
            {
                File.WriteAllText(path, content, encoding);
                if (path.EndsWith("existing-indexes.jsonl", StringComparison.Ordinal))
                    throw new IOException("original failure");
            },
            Directory.Move,
            path =>
            {
                deletes.Add(path);
                if (path.EndsWith("report.json", StringComparison.Ordinal))
                    throw new IOException("delete failure");
                File.Delete(path);
            },
            (path, recursive) =>
            {
                directoryDeletes.Add(path);
                Directory.Delete(path, recursive);
            });

        var exception = Assert.Throws<IOException>(() => WriteDefault(writer));

        Assert.Equal("original failure", exception.Message);
        Assert.DoesNotContain(FilterA, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("delete failure", exception.Message, StringComparison.Ordinal);
        Assert.Contains(deletes, path => path.EndsWith("report.json", StringComparison.Ordinal));
        Assert.Contains(deletes, path => path.EndsWith("candidates.jsonl", StringComparison.Ordinal));
        Assert.Contains(deletes, path => path.EndsWith("existing-indexes.jsonl", StringComparison.Ordinal));
        Assert.Contains(directoryDeletes, path => path.Contains(".staging-", StringComparison.Ordinal));
        AssertNoPublishedDirectory(temp.Path);
        foreach (var file in Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories))
            AssertNoFilterOrDdl(File.ReadAllText(file));
    }

    private static string WriteDefault(IndexAnalysisArtifactWriter writer) =>
        writer.Write(
            Fixture.Report(),
            [Fixture.Candidate()],
            [Fixture.Index()],
            [Fixture.Sensitive(2, FilterA)],
            "wind");

    private static IndexAnalysisArtifactWriter FailingWriter(string root, string fileName, string message, bool beforeWrite) =>
        new(
            root,
            () => DateTimeOffset.UnixEpoch,
            (path, content, encoding) =>
            {
                if (path.EndsWith(fileName, StringComparison.Ordinal))
                    throw new IOException(message);
                if (!beforeWrite)
                    return;
                File.WriteAllText(path, content, encoding);
            });

    private static void AssertSafeChild(string root, string directory, string targetSegment)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullDirectory = Path.GetFullPath(directory);
        Assert.StartsWith(fullRoot, fullDirectory, StringComparison.OrdinalIgnoreCase);
        var name = Path.GetFileName(fullDirectory);
        Assert.Equal(name, Path.GetRelativePath(fullRoot, fullDirectory));
        var prefix = $"{DateTimeOffset.UnixEpoch:yyyyMMddTHHmmssfffZ}-{targetSegment}-";
        Assert.StartsWith(prefix, name, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{32}$", name[prefix.Length..]);
        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, name);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, name);
        Assert.DoesNotContain(FilterA, name, StringComparison.Ordinal);
        Assert.DoesNotContain(FilterB, name, StringComparison.Ordinal);
    }

    private static void AssertCandidateLine(string line, IndexCandidate candidate)
    {
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(line);
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(
            new[]
            {
                "candidateId",
                "schema",
                "table",
                "equalityColumns",
                "inequalityColumns",
                "includeColumns",
                "userSeeks",
                "userScans",
                "averageTotalUserCost",
                "averageUserImpactPercent",
                "cumulativeImpactScore",
                "lastUserSeek",
                "lastUserScan",
            },
            names);
        Assert.Equal(candidate.CandidateId, document.RootElement.GetProperty("candidateId").GetInt64());
        Assert.Equal(candidate.Schema, document.RootElement.GetProperty("schema").GetString());
        Assert.Equal(candidate.Table, document.RootElement.GetProperty("table").GetString());
        Assert.Equal(candidate.EqualityColumns, Strings(document.RootElement.GetProperty("equalityColumns")));
        Assert.Equal(candidate.InequalityColumns, Strings(document.RootElement.GetProperty("inequalityColumns")));
        Assert.Equal(candidate.IncludeColumns, Strings(document.RootElement.GetProperty("includeColumns")));
        Assert.Equal(candidate.UserSeeks, document.RootElement.GetProperty("userSeeks").GetInt64());
        Assert.Equal(candidate.UserScans, document.RootElement.GetProperty("userScans").GetInt64());
        Assert.Equal(candidate.AverageTotalUserCost, document.RootElement.GetProperty("averageTotalUserCost").GetDecimal());
        Assert.Equal(candidate.AverageUserImpactPercent, document.RootElement.GetProperty("averageUserImpactPercent").GetDecimal());
        Assert.Equal(candidate.CumulativeImpactScore, document.RootElement.GetProperty("cumulativeImpactScore").GetDecimal());
        Assert.Equal(candidate.LastUserSeek, document.RootElement.GetProperty("lastUserSeek").GetDateTimeOffset());
        Assert.Equal(candidate.LastUserScan, document.RootElement.GetProperty("lastUserScan").GetDateTimeOffset());
        Assert.False(document.RootElement.TryGetProperty("classification", out _));
        Assert.False(document.RootElement.TryGetProperty("filterDefinition", out _));
        Assert.False(document.RootElement.TryGetProperty("filterHash", out _));
    }

    private static void AssertIndexLine(string line, ExistingIndex index, string filter)
    {
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(line);
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(
            new[]
            {
                "schema",
                "table",
                "indexId",
                "name",
                "type",
                "keyColumns",
                "keyDescending",
                "includeColumns",
                "unique",
                "primaryKey",
                "uniqueConstraint",
                "disabled",
                "hasFilter",
                "filterHash",
                "compression",
                "filterDefinition",
            },
            names);
        var root = document.RootElement;
        Assert.Equal(index.Schema, root.GetProperty("schema").GetString());
        Assert.Equal(index.Table, root.GetProperty("table").GetString());
        Assert.Equal(index.IndexId, root.GetProperty("indexId").GetInt32());
        Assert.Equal(index.Name, root.GetProperty("name").GetString());
        Assert.Equal(index.Type, root.GetProperty("type").GetString());
        Assert.Equal(index.KeyColumns, Strings(root.GetProperty("keyColumns")));
        Assert.Equal(index.KeyDescending, root.GetProperty("keyDescending").EnumerateArray().Select(item => item.GetBoolean()).ToArray());
        Assert.Equal(index.IncludeColumns, Strings(root.GetProperty("includeColumns")));
        Assert.Equal(index.Unique, root.GetProperty("unique").GetBoolean());
        Assert.Equal(index.PrimaryKey, root.GetProperty("primaryKey").GetBoolean());
        Assert.Equal(index.UniqueConstraint, root.GetProperty("uniqueConstraint").GetBoolean());
        Assert.Equal(index.Disabled, root.GetProperty("disabled").GetBoolean());
        Assert.Equal(index.HasFilter, root.GetProperty("hasFilter").GetBoolean());
        Assert.Equal(index.FilterHash, root.GetProperty("filterHash").GetString());
        Assert.Equal(index.Compression, root.GetProperty("compression").GetString());
        Assert.Equal(filter, root.GetProperty("filterDefinition").GetString());
    }

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static void AssertNoBom(byte[] bytes) =>
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

    private static void AssertNoFilterOrDdl(string text)
    {
        Assert.DoesNotContain(FilterA, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FilterB, text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-filter", text, StringComparison.Ordinal);
        AssertNoDdlOrQuery(text);
    }

    private static void AssertNoDdlOrQuery(string text)
    {
        Assert.DoesNotContain("CREATE INDEX", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP INDEX", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER INDEX", text, StringComparison.Ordinal);
        Assert.DoesNotContain(IndexAnalysisQuery.Sql, text, StringComparison.Ordinal);
        Assert.DoesNotContain("dm_db_missing_index", text, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoLeak(string root, Exception exception)
    {
        Assert.DoesNotContain(FilterA, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FilterB, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE INDEX", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    private static void AssertNoPublishedDirectory(string root) =>
        Assert.DoesNotContain(
            Directory.GetDirectories(root),
            path => !Path.GetFileName(path).Contains(".staging-", StringComparison.Ordinal));

    private static class Fixture
    {
        public static SqlHarnessIndexesReport Report(IReadOnlyList<IndexCandidateReport>? candidates = null) =>
            new(
                new SqlHarnessTargetIdentityReport("wind", "safe-db", "wind", "safe-db", "profile"),
                new DateTimeOffset(2026, 7, 29, 1, 2, 3, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero),
                20,
                "dbo.Contracts",
                [EvidenceWarning],
                candidates ?? [PublicCandidate(11, IndexOverlapClassification.PartialKey)],
                null);

        public static IndexCandidateReport PublicCandidate(
            long candidateId,
            IndexOverlapClassification classification,
            string schema = "dbo",
            string table = "Contracts") =>
            new(
                candidateId,
                schema,
                table,
                ["TenantId"],
                ["CreatedAt"],
                ["Name"],
                3,
                4,
                2.5m,
                50m,
                99.25m,
                new DateTimeOffset(2026, 7, 29, 13, 45, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 28, 23, 30, 0, TimeSpan.Zero),
                classification,
                "IX_Contracts_Tenant",
                1,
                2,
                [],
                false,
                true,
                "HASH");

        public static IndexCandidate Candidate(
            long candidateId = 11,
            string schema = "dbo",
            string table = "Contracts",
            IReadOnlyList<string>? equality = null,
            IReadOnlyList<string>? inequality = null,
            IReadOnlyList<string>? includes = null) =>
            new(
                candidateId,
                schema,
                table,
                equality ?? ["TenantId"],
                inequality ?? ["CreatedAt"],
                includes ?? ["Name"],
                3,
                4,
                2.5m,
                50m,
                99.25m,
                new DateTimeOffset(2026, 7, 29, 13, 45, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 28, 23, 30, 0, TimeSpan.Zero));

        public static ExistingIndex Index(
            int indexId = 2,
            string schema = "dbo",
            string table = "Contracts",
            string name = "IX_Contracts_Tenant",
            string? filterHash = "HASHA",
            bool disabled = true) =>
            new(
                schema,
                table,
                indexId,
                name,
                "NONCLUSTERED",
                ["TenantId"],
                [false],
                ["Status"],
                false,
                false,
                false,
                disabled,
                filterHash is not null,
                filterHash,
                "PAGE");

        public static SensitiveExistingIndex Sensitive(
            int indexId,
            string? filter,
            string schema = "dbo",
            string table = "Contracts",
            string name = "IX_Contracts_Tenant") =>
            new(schema, table, indexId, name, filter);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sqlharness-index-analysis-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, true);
    }
}
