using System.Globalization;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class SpaceReaderTests
{
    [Fact]
    public async Task Reader_builds_files_allocation_tables_and_indexes()
    {
        var result = await SpaceQuery.ReadAsync(
            SpaceFixture.Reader(objectMatches: 1), objectRequested: true,
            CancellationToken.None);

        Assert.Single(result.Files);
        Assert.Equal(100m, result.Allocation.ReservedMb);
        Assert.Equal("Contracts", Assert.Single(result.Tables).Name);
        Assert.Equal("IX_Contracts_Date", Assert.Single(result.Indexes).Index);
        Assert.True(result.Raw.Bytes > 0);
    }

    [Fact]
    public async Task Reader_without_object_requires_zero_matches_and_empty_indexes()
    {
        var result = await SpaceQuery.ReadAsync(
            SpaceFixture.Reader(objectMatches: 0, includeIndex: false),
            objectRequested: false,
            CancellationToken.None);

        Assert.Empty(result.Indexes);
        Assert.Single(result.Files);
        Assert.Equal(100m, result.Allocation.ReservedMb);
        Assert.Equal("Contracts", Assert.Single(result.Tables).Name);
    }

    [Fact]
    public async Task Reader_zero_matches_in_object_mode_is_safety()
    {
        var ex = await Assert.ThrowsAsync<SqlHarnessSafetyException>(() =>
            SpaceQuery.ReadAsync(
                SpaceFixture.Reader(objectMatches: 0),
                objectRequested: true,
                CancellationToken.None));

        Assert.Equal(SpaceQuery.MissingOrAmbiguousMessage, ex.Message);
    }

    [Fact]
    public async Task Reader_two_matches_in_object_mode_is_safety()
    {
        var ex = await Assert.ThrowsAsync<SqlHarnessSafetyException>(() =>
            SpaceQuery.ReadAsync(
                SpaceFixture.Reader(objectMatches: 2),
                objectRequested: true,
                CancellationToken.None));

        Assert.Equal(SpaceQuery.MissingOrAmbiguousMessage, ex.Message);
    }

    [Fact]
    public async Task Reader_nonzero_matches_without_object_is_invalid()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SpaceQuery.ReadAsync(
                SpaceFixture.Reader(objectMatches: 1, includeIndex: false),
                objectRequested: false,
                CancellationToken.None));
    }

    [Fact]
    public async Task Reader_missing_result_set_is_invalid()
    {
        var reader = new FakeReader(
            Set(["MatchCount"], [1L]),
            Set(["LogicalName", "Type", "PhysicalName", "SizeMb", "UsedMb", "FreeMb"],
                ["Primary", "ROWS", @"C:\data.mdf", 200m, 80m, 120m]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SpaceQuery.ReadAsync(reader, objectRequested: true, CancellationToken.None));
    }

    [Fact]
    public async Task Reader_duplicate_allocation_rows_is_invalid()
    {
        var reader = SpaceFixture.Reader(
            objectMatches: 1,
            allocationRows:
            [
                [100m, 80m, 60m],
                [50m, 40m, 30m],
            ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SpaceQuery.ReadAsync(reader, objectRequested: true, CancellationToken.None));
    }

    [Fact]
    public async Task Reader_empty_allocation_is_invalid()
    {
        var reader = SpaceFixture.Reader(objectMatches: 1, allocationRows: []);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SpaceQuery.ReadAsync(reader, objectRequested: true, CancellationToken.None));
    }

    [Fact]
    public async Task Reader_decimal_conversion_is_culture_invariant()
    {
        using var _ = new TemporaryCulture("pl-PL");
        // String decimals would parse as 1005 under pl-PL if culture were used (comma separator).
        var reader = SpaceFixture.Reader(
            objectMatches: 1,
            allocationRows: [["100.50", "80.25", "60.00"]]);

        var result = await SpaceQuery.ReadAsync(reader, objectRequested: true, CancellationToken.None);

        Assert.Equal(100.50m, result.Allocation.ReservedMb);
        Assert.Equal(80.25m, result.Allocation.UsedMb);
        Assert.Equal(60.00m, result.Allocation.DataMb);
    }

    [Fact]
    public async Task Reader_dbnull_physical_path_maps_to_null()
    {
        var result = await SpaceQuery.ReadAsync(
            SpaceFixture.Reader(objectMatches: 1, physicalName: DBNull.Value, physicalNameSpecified: true),
            objectRequested: true,
            CancellationToken.None);

        Assert.Null(Assert.Single(result.Files).PhysicalName);
    }

    [Fact]
    public async Task Reader_null_physical_path_maps_to_null()
    {
        var result = await SpaceQuery.ReadAsync(
            SpaceFixture.Reader(objectMatches: 1, physicalName: null, physicalNameSpecified: true),
            objectRequested: true,
            CancellationToken.None);

        Assert.Null(Assert.Single(result.Files).PhysicalName);
    }

    [Fact]
    public async Task Reader_maps_full_row_shapes()
    {
        var result = await SpaceQuery.ReadAsync(
            SpaceFixture.Reader(objectMatches: 1),
            objectRequested: true,
            CancellationToken.None);

        var file = Assert.Single(result.Files);
        Assert.Equal("Primary", file.LogicalName);
        Assert.Equal("ROWS", file.Type);
        Assert.Equal(@"C:\data.mdf", file.PhysicalName);
        Assert.Equal(200m, file.SizeMb);
        Assert.Equal(80m, file.UsedMb);
        Assert.Equal(120m, file.FreeMb);

        Assert.Equal(100m, result.Allocation.ReservedMb);
        Assert.Equal(80m, result.Allocation.UsedMb);
        Assert.Equal(60m, result.Allocation.DataMb);

        var table = Assert.Single(result.Tables);
        Assert.Equal("dbo", table.Schema);
        Assert.Equal("Contracts", table.Name);
        Assert.Equal(42L, table.Rows);
        Assert.Equal(50m, table.ReservedMb);
        Assert.Equal(40m, table.UsedMb);
        Assert.Equal(30m, table.DataMb);

        var index = Assert.Single(result.Indexes);
        Assert.Equal("dbo", index.Schema);
        Assert.Equal("Contracts", index.Table);
        Assert.Equal("IX_Contracts_Date", index.Index);
        Assert.Equal("NONCLUSTERED", index.Type);
        Assert.Equal(12m, index.ReservedMb);
        Assert.Equal(10m, index.UsedMb);
        Assert.Equal(8m, index.DataMb);
        Assert.Equal("PAGE", index.Compression);
    }

    [Fact]
    public async Task Reader_null_compression_maps_to_null()
    {
        var reader = SpaceFixture.Reader(
            objectMatches: 1,
            indexRows:
            [
                ["dbo", "Contracts", "HEAP", "HEAP", 5m, 4m, 3m, null],
            ]);

        var result = await SpaceQuery.ReadAsync(reader, objectRequested: true, CancellationToken.None);

        Assert.Null(Assert.Single(result.Indexes).Compression);
    }

    [Fact]
    public async Task Reader_honors_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var reader = new CancelOnReadReader(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SpaceQuery.ReadAsync(reader, objectRequested: false, cts.Token));
    }

    [Fact]
    public async Task Reader_empty_match_count_is_invalid()
    {
        var reader = new FakeReader(Set(["MatchCount"]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SpaceQuery.ReadAsync(reader, objectRequested: false, CancellationToken.None));
    }

    private static object?[][] Set(string[] names, params object?[][] rows) => [[.. names], .. rows];

    private sealed class TemporaryCulture : IDisposable
    {
        private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;

        public TemporaryCulture(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _originalCulture;
            CultureInfo.CurrentUICulture = _originalUiCulture;
        }
    }

    private sealed class CancelOnReadReader(CancellationTokenSource cts) : ISqlReader
    {
        public int FieldCount => 1;
        public int RecordsAffected => -1;
        public string GetName(int ordinal) => "MatchCount";
        public Type GetFieldType(int ordinal) => typeof(long);
        public bool GetAllowNull(int ordinal) => false;
        public object GetValue(int ordinal) => throw new InvalidOperationException("unreachable");

        public Task<bool> ReadAsync(CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }

        public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeReader(params object?[][][] sets) : ISqlReader
    {
        private int _set;
        private int _row;

        private string[] Names => sets[_set][0].Cast<string>().ToArray();
        private object?[][] Rows => sets[_set].Skip(1).ToArray();

        public int FieldCount => Names.Length;
        public int RecordsAffected => -1;
        public string GetName(int ordinal) => Names[ordinal];
        public Type GetFieldType(int ordinal) => typeof(object);
        public bool GetAllowNull(int ordinal) => true;
        public object GetValue(int ordinal) => Rows[_row - 1][ordinal] ?? DBNull.Value;

        public Task<bool> ReadAsync(CancellationToken ct)
        {
            if (_row < Rows.Length)
            {
                _row++;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            if (++_set < sets.Length)
            {
                _row = 0;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static class SpaceFixture
    {
        public static ISqlReader Reader(
            long objectMatches,
            bool includeIndex = true,
            object? physicalName = null,
            object?[][]? allocationRows = null,
            object?[][]? indexRows = null,
            bool physicalNameSpecified = false)
        {
            var path = physicalNameSpecified ? physicalName : physicalName ?? @"C:\data.mdf";
            return new FakeReader(
                Set(["MatchCount"], [objectMatches]),
                Set(
                    ["LogicalName", "Type", "PhysicalName", "SizeMb", "UsedMb", "FreeMb"],
                    ["Primary", "ROWS", path, 200m, 80m, 120m]),
                Set(
                    ["ReservedMb", "UsedMb", "DataMb"],
                    allocationRows ?? [[100m, 80m, 60m]]),
                Set(
                    ["SchemaName", "ObjectName", "Rows", "ReservedMb", "UsedMb", "DataMb"],
                    ["dbo", "Contracts", 42L, 50m, 40m, 30m]),
                includeIndex
                    ? Set(
                        ["SchemaName", "TableName", "IndexName", "Type", "ReservedMb", "UsedMb", "DataMb", "Compression"],
                        indexRows ?? [["dbo", "Contracts", "IX_Contracts_Date", "NONCLUSTERED", 12m, 10m, 8m, "PAGE"]])
                    : Set(
                        ["SchemaName", "TableName", "IndexName", "Type", "ReservedMb", "UsedMb", "DataMb", "Compression"]));
        }
    }
}