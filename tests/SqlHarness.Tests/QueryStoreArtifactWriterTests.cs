using System.Text;
using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public class QueryStoreArtifactWriterTests
{
    private const string SecretSql = "SELECT Secret FROM dbo.T";
    private const string Mismatch = "Query Store artifact texts do not match the report queries.";

    [Fact]
    public void Writer_keeps_sql_only_in_queries_jsonl()
    {
        using var temp = new TempDirectory();
        var writer = new QueryStoreArtifactWriter(
            temp.Path, () => DateTimeOffset.UnixEpoch);
        var directory = writer.Write(
            Fixture.Report(artifactDirectory: null),
            [new SensitiveQueryStoreText(42, "A1B2", "SELECT Secret FROM dbo.T")],
            "wind/../../unsafe");

        var reportJson = File.ReadAllText(Path.Combine(directory, "report.json"));
        var queriesJsonl = File.ReadAllText(Path.Combine(directory, "queries.jsonl"));
        Assert.DoesNotContain("SELECT Secret", reportJson, StringComparison.Ordinal);
        Assert.Contains("SELECT Secret", queriesJsonl, StringComparison.Ordinal);
        Assert.StartsWith(Path.GetFullPath(temp.Path), Path.GetFullPath(directory),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Writer_creates_unique_safe_directory_names()
    {
        using var temp = new TempDirectory();
        var writer = new QueryStoreArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch);
        var texts = new[] { new SensitiveQueryStoreText(42, "A1B2", SecretSql) };

        var first = writer.Write(Fixture.Report(artifactDirectory: null), texts, "wind/../../unsafe");
        var second = writer.Write(Fixture.Report(artifactDirectory: null), texts, "wind/../../unsafe");
        var escaped = writer.Write(Fixture.Report(artifactDirectory: null), texts, "../../");

        Assert.NotEqual(first, second);
        AssertSafeChild(temp.Path, first, "wind-unsafe");
        AssertSafeChild(temp.Path, second, "wind-unsafe");
        AssertSafeChild(temp.Path, escaped, "target");
        Assert.DoesNotContain(SecretSql, Path.GetFileName(first), StringComparison.Ordinal);
        Assert.Equal(
            new[] { "queries.jsonl", "report.json" },
            Directory.GetFiles(first).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Writer_embeds_final_directory_and_one_jsonl_row_per_query()
    {
        using var temp = new TempDirectory();
        var writer = new QueryStoreArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch);
        var report = Fixture.Report(
            artifactDirectory: null,
            queries:
            [
                Fixture.Item(7, "AA"),
                Fixture.Item(9, "BB"),
            ]);
        var texts = new[]
        {
            new SensitiveQueryStoreText(9, "BB", "SELECT Secret FROM dbo.Nine"),
            new SensitiveQueryStoreText(7, "AA", "SELECT Secret FROM dbo.Seven"),
        };

        var directory = writer.Write(report, texts, "db");

        Assert.Null(report.ArtifactDirectory);
        var reportBytes = File.ReadAllBytes(Path.Combine(directory, "report.json"));
        var reportJson = Encoding.UTF8.GetString(reportBytes);
        AssertNoBom(reportBytes);
        Assert.Contains("\n", reportJson, StringComparison.Ordinal);
        using (var document = JsonDocument.Parse(reportJson))
        {
            Assert.Equal(directory, document.RootElement.GetProperty("artifactDirectory").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("queries").GetArrayLength());
            Assert.False(document.RootElement.TryGetProperty("querySqlText", out _));
        }

        Assert.DoesNotContain("SELECT Secret", reportJson, StringComparison.Ordinal);
        var jsonlPath = Path.Combine(directory, "queries.jsonl");
        var jsonlBytes = File.ReadAllBytes(jsonlPath);
        AssertNoBom(jsonlBytes);
        var lines = File.ReadAllLines(jsonlPath);
        Assert.Equal(2, lines.Length);
        AssertJsonlRow(lines[0], 9, "BB", "SELECT Secret FROM dbo.Nine");
        AssertJsonlRow(lines[1], 7, "AA", "SELECT Secret FROM dbo.Seven");
    }

    [Fact]
    public void Writer_writes_an_empty_queries_jsonl_for_an_empty_success()
    {
        using var temp = new TempDirectory();
        var writer = new QueryStoreArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch);

        var directory = writer.Write(Fixture.Report(queries: []), [], "db");

        var path = Path.Combine(directory, "queries.jsonl");
        Assert.True(File.Exists(path));
        Assert.Empty(File.ReadAllLines(path));
        Assert.Equal(string.Empty, File.ReadAllText(path));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "report.json")));
        Assert.Equal(0, document.RootElement.GetProperty("queries").GetArrayLength());
        Assert.Equal(directory, document.RootElement.GetProperty("artifactDirectory").GetString());
    }

    [Fact]
    public void Writer_rejects_id_or_hash_mismatch_without_publishing()
    {
        using var temp = new TempDirectory();
        var writer = new QueryStoreArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch);
        var secret = "SELECT Secret FROM dbo.T WHERE hidden = 1";
        (SqlHarnessQueryStoreTopReport Report, SensitiveQueryStoreText[] Texts)[] cases =
        [
            (Fixture.Report(queries: [Fixture.Item(42, "A1B2")]), [new SensitiveQueryStoreText(99, "A1B2", secret)]),
            (Fixture.Report(queries: [Fixture.Item(42, "A1B2")]), [new SensitiveQueryStoreText(42, "FFFF", secret)]),
            (
                Fixture.Report(queries: [Fixture.Item(42, "A1B2"), Fixture.Item(43, "B2C3")]),
                [new SensitiveQueryStoreText(42, "A1B2", secret)]),
            (
                Fixture.Report(queries: [Fixture.Item(42, "A1B2")]),
                [
                    new SensitiveQueryStoreText(42, "A1B2", secret),
                    new SensitiveQueryStoreText(42, "A1B2", secret),
                ]),
            (
                Fixture.Report(queries: [Fixture.Item(42, "A1B2"), Fixture.Item(42, "A1B2")]),
                [new SensitiveQueryStoreText(42, "A1B2", secret)]),
        ];

        foreach (var (report, texts) in cases)
        {
            var exception = Assert.Throws<InvalidOperationException>(() => writer.Write(report, texts, "wind/../../unsafe"));
            Assert.Equal(Mismatch, exception.Message);
            Assert.DoesNotContain("SELECT", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        }

        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public void Report_write_failure_leaves_no_final_directory_or_sql()
    {
        using var temp = new TempDirectory();
        var writer = new QueryStoreArtifactWriter(
            temp.Path,
            () => DateTimeOffset.UnixEpoch,
            (path, content, encoding) =>
            {
                if (path.EndsWith("report.json", StringComparison.Ordinal))
                    throw new IOException("report failure");
                File.WriteAllText(path, content, encoding);
            });

        var exception = Assert.Throws<IOException>(() => writer.Write(
            Fixture.Report(artifactDirectory: null),
            [new SensitiveQueryStoreText(42, "A1B2", SecretSql)],
            "wind"));

        Assert.Equal("report failure", exception.Message);
        AssertNoLeak(temp.Path, exception);
    }

    [Fact]
    public void Jsonl_write_failure_leaves_no_final_directory_or_sql()
    {
        using var temp = new TempDirectory();
        var writer = new QueryStoreArtifactWriter(
            temp.Path,
            () => DateTimeOffset.UnixEpoch,
            (path, content, encoding) =>
            {
                if (path.EndsWith("queries.jsonl", StringComparison.Ordinal))
                    throw new IOException("jsonl failure");
                File.WriteAllText(path, content, encoding);
            });

        var exception = Assert.Throws<IOException>(() => writer.Write(
            Fixture.Report(artifactDirectory: null),
            [new SensitiveQueryStoreText(42, "A1B2", SecretSql)],
            "wind"));

        Assert.Equal("jsonl failure", exception.Message);
        AssertNoLeak(temp.Path, exception);
    }

    [Fact]
    public void Directory_move_failure_leaves_no_final_directory_or_sql()
    {
        using var temp = new TempDirectory();
        var writer = new QueryStoreArtifactWriter(
            temp.Path,
            () => DateTimeOffset.UnixEpoch,
            File.WriteAllText,
            (_, _) => throw new IOException("move failure"));

        var exception = Assert.Throws<IOException>(() => writer.Write(
            Fixture.Report(artifactDirectory: null),
            [new SensitiveQueryStoreText(42, "A1B2", SecretSql)],
            "wind"));

        Assert.Equal("move failure", exception.Message);
        AssertNoLeak(temp.Path, exception);
    }

    [Fact]
    public void Directory_move_that_publishes_then_throws_removes_the_final_directory()
    {
        using var temp = new TempDirectory();
        var writer = new QueryStoreArtifactWriter(
            temp.Path,
            () => DateTimeOffset.UnixEpoch,
            File.WriteAllText,
            (source, destination) =>
            {
                Directory.Move(source, destination);
                throw new IOException("move failure after publish");
            });

        var exception = Assert.Throws<IOException>(() => writer.Write(
            Fixture.Report(artifactDirectory: null),
            [new SensitiveQueryStoreText(42, "A1B2", SecretSql)],
            "wind"));

        Assert.Equal("move failure after publish", exception.Message);
        AssertNoLeak(temp.Path, exception);
    }

    [Fact]
    public void Cleanup_is_attempted_after_failure_and_preserves_the_original_exception()
    {
        using var temp = new TempDirectory();
        var deletes = new List<string>();
        var directoryDeletes = new List<string>();
        var writer = new QueryStoreArtifactWriter(
            temp.Path,
            () => DateTimeOffset.UnixEpoch,
            (path, content, encoding) =>
            {
                File.WriteAllText(path, content, encoding);
                if (path.EndsWith("queries.jsonl", StringComparison.Ordinal))
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

        var exception = Assert.Throws<IOException>(() => writer.Write(
            Fixture.Report(artifactDirectory: null),
            [new SensitiveQueryStoreText(42, "A1B2", SecretSql)],
            "wind"));

        Assert.Equal("original failure", exception.Message);
        Assert.DoesNotContain(SecretSql, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("delete failure", exception.Message, StringComparison.Ordinal);
        Assert.Contains(deletes, path => path.EndsWith("report.json", StringComparison.Ordinal));
        Assert.Contains(deletes, path => path.EndsWith("queries.jsonl", StringComparison.Ordinal));
        Assert.Contains(directoryDeletes, path => path.Contains(".staging-", StringComparison.Ordinal));
        AssertNoPublishedDirectory(temp.Path);
        foreach (var file in Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(SecretSql, File.ReadAllText(file), StringComparison.Ordinal);
    }

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
        Assert.DoesNotContain(SecretSql, name, StringComparison.Ordinal);
    }

    private static void AssertJsonlRow(string line, long queryId, string queryHash, string sql)
    {
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(line);
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(new[] { "queryId", "queryHash", "querySqlText" }, names);
        Assert.Equal(queryId, document.RootElement.GetProperty("queryId").GetInt64());
        Assert.Equal(queryHash, document.RootElement.GetProperty("queryHash").GetString());
        Assert.Equal(sql, document.RootElement.GetProperty("querySqlText").GetString());
    }

    private static void AssertNoBom(byte[] bytes) =>
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

    private static void AssertNoLeak(string root, Exception exception)
    {
        Assert.DoesNotContain(SecretSql, exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    private static void AssertNoPublishedDirectory(string root) =>
        Assert.DoesNotContain(
            Directory.GetDirectories(root),
            path => !Path.GetFileName(path).Contains(".staging-", StringComparison.Ordinal));

    private static class Fixture
    {
        public static SqlHarnessQueryStoreTopReport Report(
            string? artifactDirectory = null,
            IReadOnlyList<QueryStoreTopItemReport>? queries = null) =>
            new(
                new SqlHarnessTargetIdentityReport("wind", "safe-db", "wind", "safe-db", "profile"),
                60,
                25,
                queries ?? [Item()],
                artifactDirectory);

        public static QueryStoreTopItemReport Item(long queryId = 42, string queryHash = "A1B2") =>
            new(
                queryId,
                queryHash,
                "dbo.T",
                4,
                1,
                10.5m,
                2.5m,
                8.0m,
                6.5m,
                1.5m,
                4.0m,
                100.5m,
                25.25m,
                80m,
                new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sqlharness-query-store-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, true);
    }
}
