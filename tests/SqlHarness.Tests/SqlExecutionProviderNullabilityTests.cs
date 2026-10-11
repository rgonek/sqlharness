using System.Data.Common;
using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

/// <summary>
/// Plan 046 (I1): provider-unknown nullability (DbColumn.AllowDBNull == null) stays coerced to
/// AllowNull = true on every public surface and reaches only the comparison path as "unknown".
/// </summary>
public class SqlExecutionProviderNullabilityTests
{
    [Theory]
    [InlineData(null, true, null)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void Provider_column_nullability_keeps_the_coerced_value_and_exposes_the_reported_one(
        bool? reported, bool expectedAllowNull, bool? expectedReported)
    {
        var column = new FakeDbColumn(reported);

        Assert.Equal(expectedAllowNull, ProviderNullability.AllowNull(column));
        Assert.Equal(expectedReported, ProviderNullability.Reported(column));
    }

    [Fact]
    public async Task Query_and_snapshot_output_bytes_are_unchanged_for_a_column_with_unknown_nullability()
    {
        var unknown = await CollectQueryAsync(reported: null);
        var knownNullable = await CollectQueryAsync(reported: true);

        Assert.True(Assert.Single(unknown.Result.ResultSets[0].Columns).AllowNull);
        Assert.Equal(knownNullable.Result.Canonical.Hash, unknown.Result.Canonical.Hash);
        Assert.Equal(knownNullable.Raw, unknown.Raw);
        Assert.Equal(
            JsonSerializer.Serialize(knownNullable.Result.ResultSets, ArtifactDirectoryPublisher.JsonOptions),
            JsonSerializer.Serialize(unknown.Result.ResultSets, ArtifactDirectoryPublisher.JsonOptions));
        Assert.Contains(
            "\"allowNull\": true",
            JsonSerializer.Serialize(unknown.Result.ResultSets, ArtifactDirectoryPublisher.JsonOptions),
            StringComparison.Ordinal);

        var createdAt = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        var snapshotOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        Assert.Equal(
            JsonSerializer.SerializeToUtf8Bytes(
                SnapshotDocument.Create(createdAt, knownNullable.Result.ResultSets, knownNullable.Result.Canonical.Hash),
                snapshotOptions),
            JsonSerializer.SerializeToUtf8Bytes(
                SnapshotDocument.Create(createdAt, unknown.Result.ResultSets, unknown.Result.Canonical.Hash),
                snapshotOptions));
    }

    [Fact]
    public async Task Benchmark_collection_marks_unknown_nullability_only_in_the_comparison_fingerprint()
    {
        var unknown = await CollectCompareAsync(reported: null);
        var knownNullable = await CollectCompareAsync(reported: true);

        Assert.Null(unknown.Collected.Comparison.NullabilityFingerprint);
        Assert.NotNull(knownNullable.Collected.Comparison.NullabilityFingerprint);
        Assert.Equal(knownNullable.Collected.Comparison.SchemaHash, unknown.Collected.Comparison.SchemaHash);
        Assert.Equal(knownNullable.Collected.Comparison.OrderedRows, unknown.Collected.Comparison.OrderedRows);
        Assert.Equal(knownNullable.Collected.Canonical.Hash, unknown.Collected.Canonical.Hash);
        Assert.Equal(knownNullable.Raw, unknown.Raw);
    }

    private static async Task<(CollectedQueryResult Result, CanonicalResult Raw)> CollectQueryAsync(bool? reported)
    {
        CanonicalResultAccumulator? raw = null;
        var result = await QueryResultCollector.CollectAsync(
            new FakeSession(new DbColumnReader(reported)),
            new SqlExecutionCommand("select", [], 30),
            maxRows: 50,
            knownSecrets: [],
            () => raw = new CanonicalResultAccumulator(),
            CancellationToken.None);
        using (raw)
            return (result, raw!.Complete());
    }

    private static async Task<(CollectedCompare Collected, CanonicalResult Raw)> CollectCompareAsync(bool? reported)
    {
        using var raw = new CanonicalResultAccumulator();
        await using var reader = new DbColumnReader(reported);
        var collected = await BenchmarkCollector.CollectCompareAsync(
            reader, raw, captureComparison: true, CanonicalComparisonAccumulator.MaximumComparedRows, CancellationToken.None);
        return (collected, raw.Complete());
    }

    private sealed class FakeDbColumn : DbColumn
    {
        public FakeDbColumn(bool? allowDbNull)
        {
            ColumnName = "Value";
            AllowDBNull = allowDbNull;
        }
    }

    /// <summary>One int column whose nullability comes from a provider DbColumn, as in production readers.</summary>
    private sealed class DbColumnReader(bool? reported) : ISqlReader
    {
        private readonly DbColumn _column = new FakeDbColumn(reported);
        private readonly object?[][] _rows = [[1], [2]];
        private int _position = -1;
        public int FieldCount => 1;
        public int RecordsAffected => -1;
        public string GetName(int ordinal) => "Value";
        public Type GetFieldType(int ordinal) => typeof(int);
        public bool GetAllowNull(int ordinal) => ProviderNullability.AllowNull(_column);
        public bool? GetReportedAllowNull(int ordinal) => ProviderNullability.Reported(_column);
        public object GetValue(int ordinal) => _rows[_position][ordinal] ?? DBNull.Value;
        public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(++_position < _rows.Length);
        public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSession(ISqlReader reader) : ISqlSession
    {
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } = new("server", "db", "server", "db", "profile");
        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct) => Task.FromResult(reader);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}