using System.Text;
using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public class SnapshotStoreTests
{
    [Fact]
    public void Snapshot_round_trip_preserves_typed_cells_and_shape()
    {
        using var temp = new TempDirectory();
        var store = new SnapshotStore(temp.Path);
        var document = SnapshotFixture.Document(
            columns: [("Id", "System.Int32")],
            rows: [[1], [2]]);

        store.Save("before-import", document, force: false);
        AssertDocumentsEqual(document, store.Load("before-import"));
    }

    [Fact]
    public void Existing_snapshot_requires_force_and_original_remains_intact()
    {
        using var temp = new TempDirectory();
        var store = new SnapshotStore(temp.Path);
        var original = SnapshotFixture.Document(rows: [[1]]);
        var replacement = SnapshotFixture.Document(rows: [[2]]);
        store.Save("before", original, force: false);

        Assert.Throws<IOException>(() => store.Save("before", replacement, force: false));
        AssertDocumentsEqual(original, store.Load("before"));
    }

    [Fact]
    public void Force_replaces_existing_snapshot_after_temp_completes()
    {
        using var temp = new TempDirectory();
        var store = new SnapshotStore(temp.Path);
        var original = SnapshotFixture.Document(rows: [[1]], resultHash: "ORIG");
        var replacement = SnapshotFixture.Document(rows: [[2]], resultHash: "REPL");
        store.Save("named", original, force: false);

        store.Save("named", replacement, force: true);

        AssertDocumentsEqual(replacement, store.Load("named"));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-bad")]
    [InlineData("has space")]
    [InlineData("too-long-name-that-exceeds-sixty-four-characters-aaaaaaaaaaaaaaaaaaa")]
    public void Invalid_names_are_rejected(string? name)
    {
        using var temp = new TempDirectory();
        var store = new SnapshotStore(temp.Path);
        var document = SnapshotFixture.Document();

        Assert.ThrowsAny<ArgumentException>(() => store.Save(name!, document, force: false));
        Assert.ThrowsAny<ArgumentException>(() => store.Load(name!));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Theory]
    [InlineData("nested/path")]
    [InlineData("nested\\path")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a/../b")]
    public void Nested_and_traversal_names_are_rejected(string name)
    {
        using var temp = new TempDirectory();
        var store = new SnapshotStore(temp.Path);
        var document = SnapshotFixture.Document();

        Assert.ThrowsAny<ArgumentException>(() => store.Save(name, document, force: false));
        Assert.ThrowsAny<ArgumentException>(() => store.Load(name));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public void Missing_snapshot_throws()
    {
        using var temp = new TempDirectory();
        var store = new SnapshotStore(temp.Path);

        Assert.Throws<FileNotFoundException>(() => store.Load("missing-label"));
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "broken.json");
        File.WriteAllText(path, "{ not json");

        var store = new SnapshotStore(temp.Path);

        Assert.ThrowsAny<Exception>(() => store.Load("broken"));
    }

    [Fact]
    public void Version_mismatch_is_rejected()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "v2.json");
        File.WriteAllText(path, """
            {
              "version": 2,
              "createdAt": "2026-07-29T12:00:00+00:00",
              "resultSets": [],
              "resultHash": "H"
            }
            """);

        var store = new SnapshotStore(temp.Path);

        var exception = Assert.ThrowsAny<Exception>(() => store.Load("v2"));
        Assert.Contains("version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_failure_cleans_up_temporary_files()
    {
        using var temp = new TempDirectory();
        var store = new SnapshotStore(
            temp.Path,
            writeAllBytes: (path, bytes) =>
            {
                File.WriteAllBytes(path, bytes.AsSpan(0, Math.Min(8, bytes.Length)).ToArray());
                throw new IOException("simulated write failure");
            });

        Assert.Throws<IOException>(() =>
            store.Save("cleanup-me", SnapshotFixture.Document(), force: false));

        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public void Persisted_document_contains_no_sql_or_parameter_text()
    {
        using var temp = new TempDirectory();
        var store = new SnapshotStore(temp.Path);
        var document = SnapshotFixture.Document(
            columns: [("Id", "System.Int32"), ("Name", "System.String")],
            rows: [[1, "acme"]],
            resultHash: "ABC123");

        store.Save("before-import", document, force: false);

        var json = File.ReadAllText(Path.Combine(temp.Path, "before-import.json"));
        Assert.DoesNotContain("SELECT", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FROM", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--param", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sql", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("parameter", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"version\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"resultHash\": \"ABC123\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"int32\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"string\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Round_trip_preserves_canonical_type_distinctions()
    {
        using var temp = new TempDirectory();
        var store = new SnapshotStore(temp.Path);
        var createdAt = new DateTimeOffset(2026, 7, 29, 15, 30, 0, TimeSpan.Zero);
        var document = SnapshotDocument.Create(
            createdAt,
            [
                new SqlHarnessResultSetReport(
                    [
                        new SqlHarnessColumnReport(0, "I", "System.Int32", false),
                        new SqlHarnessColumnReport(1, "S", "System.String", true),
                        new SqlHarnessColumnReport(2, "B", "System.Boolean", false),
                        new SqlHarnessColumnReport(3, "D", "System.Decimal", false),
                        new SqlHarnessColumnReport(4, "G", "System.Guid", false),
                        new SqlHarnessColumnReport(5, "N", "System.String", true),
                    ],
                    [
                        [
                            42,
                            "text",
                            true,
                            12.5m,
                            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                            null,
                        ],
                    ],
                    RowCount: 1,
                    OmittedRowCount: 0),
            ],
            resultHash: "TYPED");

        store.Save("typed-cells", document, force: false);
        var loaded = store.Load("typed-cells");

        AssertDocumentsEqual(document, loaded);
        var row = Assert.Single(Assert.Single(loaded.ResultSets).Rows);
        Assert.Equal("int32", row[0].Type);
        Assert.Equal("string", row[1].Type);
        Assert.Equal("boolean", row[2].Type);
        Assert.Equal("decimal", row[3].Type);
        Assert.Equal("guid", row[4].Type);
        Assert.Equal("null", row[5].Type);
        Assert.True(row[5].IsNull);
        Assert.Equal(42, row[0].Value.GetInt32());
        Assert.Equal("text", row[1].Value.GetString());
        Assert.True(row[2].Value.GetBoolean());
        Assert.Equal(12.5m, row[3].Value.GetDecimal());
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", row[4].Value.GetString());
    }

    [Fact]
    public void Save_uses_atomic_temp_then_move_naming()
    {
        using var temp = new TempDirectory();
        string? observedTemp = null;
        string? observedFinal = null;
        var store = new SnapshotStore(
            temp.Path,
            writeAllBytes: (path, bytes) =>
            {
                observedTemp = path;
                File.WriteAllBytes(path, bytes);
            },
            move: (source, destination, overwrite) =>
            {
                observedFinal = destination;
                File.Move(source, destination, overwrite);
            });

        store.Save("atomic", SnapshotFixture.Document(), force: false);

        Assert.NotNull(observedTemp);
        Assert.NotNull(observedFinal);
        Assert.StartsWith(Path.Combine(temp.Path, "atomic.json.tmp-"), observedTemp, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(temp.Path, "atomic.json"), observedFinal);
        Assert.True(File.Exists(observedFinal));
        Assert.False(File.Exists(observedTemp));
    }

    private static void AssertDocumentsEqual(SnapshotDocument expected, SnapshotDocument actual)
    {
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.ResultHash, actual.ResultHash);
        Assert.Equal(expected.ResultSets.Count, actual.ResultSets.Count);
        for (var setIndex = 0; setIndex < expected.ResultSets.Count; setIndex++)
        {
            var expectedSet = expected.ResultSets[setIndex];
            var actualSet = actual.ResultSets[setIndex];
            Assert.Equal(expectedSet.RowCount, actualSet.RowCount);
            Assert.Equal(expectedSet.Columns, actualSet.Columns);
            Assert.Equal(expectedSet.Rows.Count, actualSet.Rows.Count);
            for (var rowIndex = 0; rowIndex < expectedSet.Rows.Count; rowIndex++)
            {
                var expectedRow = expectedSet.Rows[rowIndex];
                var actualRow = actualSet.Rows[rowIndex];
                Assert.Equal(expectedRow.Count, actualRow.Count);
                for (var columnIndex = 0; columnIndex < expectedRow.Count; columnIndex++)
                {
                    var expectedCell = expectedRow[columnIndex];
                    var actualCell = actualRow[columnIndex];
                    Assert.Equal(expectedCell.Type, actualCell.Type);
                    Assert.Equal(expectedCell.IsNull, actualCell.IsNull);
                    Assert.Equal(expectedCell.Value.GetRawText(), actualCell.Value.GetRawText());
                }
            }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"sqlharness-snapshot-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch { /* best effort */ }
        }
    }

    private static class SnapshotFixture
    {
        public static SnapshotDocument Document(
            (string Name, string DataType)[]? columns = null,
            object?[][]? rows = null,
            DateTimeOffset? createdAt = null,
            string resultHash = "HASH")
        {
            columns ??= [("Value", "System.Int32")];
            rows ??= [[1]];
            var columnReports = columns
                .Select((column, ordinal) =>
                    new SqlHarnessColumnReport(ordinal, column.Name, column.DataType, AllowNull: true))
                .ToArray();
            var scalarRows = rows
                .Select(row => (IReadOnlyList<SnapshotScalar>)row.Select(SnapshotScalar.FromValue).ToArray())
                .ToArray();
            return new SnapshotDocument(
                Version: 1,
                CreatedAt: createdAt ?? new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero),
                ResultSets: [new SnapshotResultSet(columnReports, scalarRows, scalarRows.Length)],
                ResultHash: resultHash);
        }
    }
}
