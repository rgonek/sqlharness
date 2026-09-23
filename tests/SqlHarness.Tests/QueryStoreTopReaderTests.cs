using System.Globalization;
using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class QueryStoreTopReaderTests
{
    private const string Secret = "SELECT Secret FROM dbo.T";

    [Fact]
    public async Task Reader_separates_public_metrics_from_sensitive_text()
    {
        var collected = await QueryStoreTopQuery.ReadAsync(
            Fixture.Reader(state: "READ_WRITE", querySqlText: "SELECT Secret FROM dbo.T"),
            CancellationToken.None);

        var item = Assert.Single(collected.Queries);
        Assert.Equal(42L, item.QueryId);
        Assert.Equal("A1B2", item.QueryHash);
        Assert.Equal("dbo.T", item.ObjectName);
        Assert.Equal(4L, item.ExecutionCount);
        Assert.Equal(1, item.PlanCount);
        Assert.Equal(10.5m, item.TotalDurationMilliseconds);
        Assert.Equal(2.5m, item.AverageDurationMilliseconds);
        Assert.Equal(8.0m, item.MaximumDurationMilliseconds);
        Assert.Equal(6.5m, item.TotalCpuMilliseconds);
        Assert.Equal(1.5m, item.AverageCpuMilliseconds);
        Assert.Equal(4.0m, item.MaximumCpuMilliseconds);
        Assert.Equal(100.5m, item.TotalLogicalReads);
        Assert.Equal(25.25m, item.AverageLogicalReads);
        Assert.Equal(80m, item.MaximumLogicalReads);
        Assert.Equal(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero), item.LastExecutionAt);
        var text = Assert.Single(collected.SensitiveTexts);
        Assert.Equal(42L, text.QueryId);
        Assert.Equal("A1B2", text.QueryHash);
        Assert.Equal("SELECT Secret FROM dbo.T", text.QuerySqlText);
        Assert.DoesNotContain("SELECT Secret", JsonSerializer.Serialize(collected.Queries),
            StringComparison.Ordinal);
        Assert.True(collected.RawFootprint.Bytes > 0);
    }

    [Theory]
    [InlineData("READ_ONLY")]
    [InlineData(" read_only ")]
    [InlineData("Read_Write")]
    public async Task Reader_accepts_readable_states(string state)
    {
        var collected = await QueryStoreTopQuery.ReadAsync(
            Fixture.Reader(state: state, querySqlText: Secret),
            CancellationToken.None);

        Assert.Equal(42L, Assert.Single(collected.Queries).QueryId);
        Assert.Equal(Secret, Assert.Single(collected.SensitiveTexts).QuerySqlText);
        AssertNoSecret(JsonSerializer.Serialize(collected.Queries));
    }

    [Fact]
    public async Task Reader_empty_success_keeps_state_in_the_raw_footprint()
    {
        var readWrite = await QueryStoreTopQuery.ReadAsync(
            Fixture.Of(Fixture.StateSet("READ_WRITE"), Fixture.MetricSet(), Fixture.TextSet()),
            CancellationToken.None);
        var readOnly = await QueryStoreTopQuery.ReadAsync(
            Fixture.Of(Fixture.StateSet("READ_ONLY"), Fixture.MetricSet(), Fixture.TextSet()),
            CancellationToken.None);

        Assert.Empty(readWrite.Queries);
        Assert.Empty(readWrite.SensitiveTexts);
        Assert.Empty(readOnly.Queries);
        Assert.Empty(readOnly.SensitiveTexts);
        Assert.True(readWrite.RawFootprint.Bytes > 0);
        Assert.True(readOnly.RawFootprint.Bytes > 0);
        Assert.NotEqual(readWrite.RawFootprint.Bytes, readOnly.RawFootprint.Bytes);
    }

    [Theory]
    [InlineData("OFF")]
    [InlineData(" off ")]
    [InlineData("Off")]
    public async Task Reader_rejects_off_state(string state)
    {
        var ex = await Assert.ThrowsAsync<QueryStoreUnavailableException>(() =>
            QueryStoreTopQuery.ReadAsync(
                Fixture.Reader(state: state, querySqlText: Secret),
                CancellationToken.None));

        Assert.Equal("Query Store is not readable: OFF.", ex.Message);
        AssertNoSecret(ex.Message);
    }

    [Theory]
    [InlineData("ERROR")]
    [InlineData("error")]
    public async Task Reader_rejects_error_state(string state)
    {
        var ex = await Assert.ThrowsAsync<QueryStoreUnavailableException>(() =>
            QueryStoreTopQuery.ReadAsync(
                Fixture.Reader(state: state, querySqlText: Secret),
                CancellationToken.None));

        Assert.Equal("Query Store is not readable: ERROR.", ex.Message);
        AssertNoSecret(ex.Message);
    }

    [Theory]
    [InlineData("SELECT Secret FROM dbo.T")]
    [InlineData("off!")]
    [InlineData("   ")]
    public async Task Reader_rejects_unsafe_state_as_unknown(string state)
    {
        var ex = await Assert.ThrowsAsync<QueryStoreUnavailableException>(() =>
            QueryStoreTopQuery.ReadAsync(
                Fixture.Reader(state: state, querySqlText: Secret),
                CancellationToken.None));

        Assert.Equal("Query Store is not readable: unknown.", ex.Message);
        AssertNoSecret(ex.Message);
        if (state.Trim().Length > 0)
            Assert.DoesNotContain(state.Trim(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_rejects_overlong_state_token_as_unknown()
    {
        var state = new string('A', 33);
        var ex = await Assert.ThrowsAsync<QueryStoreUnavailableException>(() =>
            QueryStoreTopQuery.ReadAsync(
                Fixture.Reader(state: state, querySqlText: Secret),
                CancellationToken.None));

        Assert.Equal("Query Store is not readable: unknown.", ex.Message);
        Assert.DoesNotContain(state, ex.Message, StringComparison.Ordinal);
        AssertNoSecret(ex.Message);
    }

    [Fact]
    public async Task Reader_reports_a_safe_nonreadable_state_token()
    {
        var state = new string('B', 32);
        var ex = await Assert.ThrowsAsync<QueryStoreUnavailableException>(() =>
            QueryStoreTopQuery.ReadAsync(
                Fixture.Reader(state: state, querySqlText: Secret),
                CancellationToken.None));

        Assert.Equal($"Query Store is not readable: {state}.", ex.Message);
        AssertNoSecret(ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reader_rejects_null_state_as_unknown(bool useDbNull)
    {
        object? state = useDbNull ? DBNull.Value : null;
        var ex = await Assert.ThrowsAsync<QueryStoreUnavailableException>(() =>
            QueryStoreTopQuery.ReadAsync(
                Fixture.Of(
                    Fixture.StateSet(state),
                    Fixture.MetricSet(Fixture.Metric()),
                    Fixture.TextSet(Fixture.Text(sql: Secret))),
                CancellationToken.None));

        Assert.Equal("Query Store is not readable: unknown.", ex.Message);
        AssertNoSecret(ex.Message);
    }

    [Fact]
    public async Task Reader_missing_state_row_is_invalid()
    {
        var ex = await AssertInvalid(Fixture.Of(
            Fixture.StateSet(),
            Fixture.MetricSet(Fixture.Metric()),
            Fixture.TextSet(Fixture.Text(sql: Secret))));

        Assert.Equal("Query Store state result set is empty.", ex.Message);
    }

    [Fact]
    public async Task Reader_extra_state_rows_are_invalid()
    {
        var ex = await AssertInvalid(Fixture.Of(
            Fixture.StateSet("READ_WRITE", Secret),
            Fixture.MetricSet(Fixture.Metric()),
            Fixture.TextSet(Fixture.Text(sql: Secret))));

        Assert.Equal("Query Store state result set returned extra rows.", ex.Message);
    }

    [Fact]
    public async Task Reader_missing_metric_result_set_is_invalid()
    {
        var ex = await AssertInvalid(Fixture.Of(Fixture.StateSet("READ_WRITE")));

        Assert.Equal("Query Store metric result set is missing.", ex.Message);
    }

    [Fact]
    public async Task Reader_missing_text_result_set_is_invalid()
    {
        var ex = await AssertInvalid(Fixture.Of(
            Fixture.StateSet("READ_WRITE"),
            Fixture.MetricSet(WithObjectName(Fixture.Metric(), Secret))));

        Assert.Equal("Query Store text result set is missing.", ex.Message);
    }

    [Fact]
    public async Task Reader_metric_without_text_row_is_invalid()
    {
        var ex = await AssertInvalid(Fixture.Of(
            Fixture.StateSet("READ_WRITE"),
            Fixture.MetricSet(
                Fixture.Metric(queryId: 42, queryHash: "A1B2"),
                Fixture.Metric(queryId: 7, queryHash: "BEEF")),
            Fixture.TextSet(Fixture.Text(queryId: 42, queryHash: "A1B2", sql: Secret))));

        Assert.Equal("Query Store text result set does not match the metric queries.", ex.Message);
    }

    [Fact]
    public async Task Reader_extra_text_query_id_is_invalid()
    {
        var ex = await AssertInvalid(Fixture.Of(
            Fixture.StateSet("READ_WRITE"),
            Fixture.MetricSet(Fixture.Metric(queryId: 42, queryHash: "A1B2")),
            Fixture.TextSet(Fixture.Text(queryId: 99, queryHash: "A1B2", sql: Secret))));

        Assert.Equal("Query Store text result set does not match the metric queries.", ex.Message);
    }

    [Fact]
    public async Task Reader_duplicate_metric_ids_are_invalid()
    {
        var ex = await AssertInvalid(Fixture.Of(
            Fixture.StateSet("READ_WRITE"),
            Fixture.MetricSet(
                Fixture.Metric(queryId: 1, queryHash: "AA"),
                Fixture.Metric(queryId: 1, queryHash: "AA")),
            Fixture.TextSet(
                Fixture.Text(queryId: 1, queryHash: "AA", sql: "SELECT 1"),
                Fixture.Text(queryId: 2, queryHash: "BB", sql: Secret))));

        Assert.Equal("Query Store metric result set contains a duplicate query.", ex.Message);
    }

    [Fact]
    public async Task Reader_duplicate_text_ids_are_invalid()
    {
        var ex = await AssertInvalid(Fixture.Of(
            Fixture.StateSet("READ_WRITE"),
            Fixture.MetricSet(Fixture.Metric()),
            Fixture.TextSet(
                Fixture.Text(sql: Secret),
                Fixture.Text(sql: "SELECT Secret FROM dbo.T WHERE Id = 2"))));

        Assert.Equal("Query Store text result set contains a duplicate query.", ex.Message);
    }

    [Fact]
    public async Task Reader_hash_mismatch_is_invalid()
    {
        var ex = await AssertInvalid(Fixture.Of(
            Fixture.StateSet("READ_WRITE"),
            Fixture.MetricSet(Fixture.Metric(queryHash: "A1B2")),
            Fixture.TextSet(Fixture.Text(queryHash: "C3D4", sql: Secret))));

        Assert.Equal("Query Store text result set does not match the metric queries.", ex.Message);
    }

    [Fact]
    public async Task Reader_uppercases_query_hash()
    {
        var collected = await QueryStoreTopQuery.ReadAsync(
            Fixture.Of(
                Fixture.StateSet("READ_WRITE"),
                Fixture.MetricSet(Fixture.Metric(queryHash: "a1B2")),
                Fixture.TextSet(Fixture.Text(queryHash: "A1b2", sql: Secret))),
            CancellationToken.None);

        var item = Assert.Single(collected.Queries);
        var text = Assert.Single(collected.SensitiveTexts);
        Assert.Equal("A1B2", item.QueryHash);
        Assert.Equal("A1B2", text.QueryHash);
        AssertNoSecret(JsonSerializer.Serialize(collected.Queries));
    }

    [Fact]
    public async Task Reader_optional_object_name_preserves_metric_order()
    {
        var nullName = Fixture.Metric(queryId: 30, queryHash: "1E");
        nullName[2] = null;
        var dbNullName = Fixture.Metric(queryId: 10, queryHash: "A");
        dbNullName[2] = DBNull.Value;
        var named = Fixture.Metric(queryId: 20, queryHash: "14", objectName: "dbo.Contracts");

        var collected = await QueryStoreTopQuery.ReadAsync(
            Fixture.Of(
                Fixture.StateSet("READ_WRITE"),
                Fixture.MetricSet(nullName, dbNullName, named),
                Fixture.TextSet(
                    Fixture.Text(queryId: 20, queryHash: "14", sql: "SELECT Secret FROM dbo.T WHERE Id = 20"),
                    Fixture.Text(queryId: 10, queryHash: "A", sql: "SELECT Secret FROM dbo.T WHERE Id = 10"),
                    Fixture.Text(queryId: 30, queryHash: "1E", sql: "SELECT Secret FROM dbo.T WHERE Id = 30"))),
            CancellationToken.None);

        Assert.Equal([30L, 10L, 20L], collected.Queries.Select(query => query.QueryId));
        Assert.Null(collected.Queries[0].ObjectName);
        Assert.Null(collected.Queries[1].ObjectName);
        Assert.Equal("dbo.Contracts", collected.Queries[2].ObjectName);
        Assert.Equal([20L, 10L, 30L], collected.SensitiveTexts.Select(text => text.QueryId));
        Assert.Contains("SELECT Secret FROM dbo.T WHERE Id = 20", collected.SensitiveTexts[0].QuerySqlText, StringComparison.Ordinal);
        AssertNoSecret(JsonSerializer.Serialize(collected.Queries));
    }

    [Fact]
    public async Task Reader_decimal_conversion_is_culture_invariant()
    {
        using var _ = new TemporaryCulture("pl-PL");
        var collected = await QueryStoreTopQuery.ReadAsync(
            Fixture.Of(
                Fixture.StateSet("READ_WRITE"),
                Fixture.MetricSet(Fixture.Metric(
                    executionCount: 7,
                    planCount: 2L,
                    totalDuration: "100.50",
                    averageDuration: 10.5d,
                    maximumDuration: "8.25",
                    totalCpu: "6.50",
                    averageCpu: 1.5d,
                    maximumCpu: "4.00",
                    totalReads: "1000.25",
                    averageReads: 25.25d,
                    maximumReads: 80)),
                Fixture.TextSet(Fixture.Text(sql: Secret))),
            CancellationToken.None);

        var item = Assert.Single(collected.Queries);
        Assert.Equal(7L, item.ExecutionCount);
        Assert.Equal(2, item.PlanCount);
        Assert.Equal(100.50m, item.TotalDurationMilliseconds);
        Assert.Equal(10.5m, item.AverageDurationMilliseconds);
        Assert.Equal(8.25m, item.MaximumDurationMilliseconds);
        Assert.Equal(6.50m, item.TotalCpuMilliseconds);
        Assert.Equal(1.5m, item.AverageCpuMilliseconds);
        Assert.Equal(4.00m, item.MaximumCpuMilliseconds);
        Assert.Equal(1000.25m, item.TotalLogicalReads);
        Assert.Equal(25.25m, item.AverageLogicalReads);
        Assert.Equal(80m, item.MaximumLogicalReads);
        AssertNoSecret(JsonSerializer.Serialize(collected.Queries));
    }

    [Fact]
    public async Task Reader_timestamps_are_stored_as_utc()
    {
        var offset = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.FromHours(2));
        var unspecified = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Unspecified);
        var utc = new DateTime(2026, 7, 29, 8, 30, 0, DateTimeKind.Utc);
        var local = new DateTime(2026, 7, 29, 15, 45, 0, DateTimeKind.Local);
        var collected = await QueryStoreTopQuery.ReadAsync(
            Fixture.Of(
                Fixture.StateSet("READ_WRITE"),
                Fixture.MetricSet(
                    Fixture.Metric(queryId: 1, queryHash: "A1", lastExecution: offset),
                    Fixture.Metric(queryId: 2, queryHash: "A2", lastExecution: unspecified),
                    Fixture.Metric(queryId: 3, queryHash: "A3", lastExecution: utc),
                    Fixture.Metric(queryId: 4, queryHash: "A4", lastExecution: local)),
                Fixture.TextSet(
                    Fixture.Text(queryId: 1, queryHash: "A1", sql: Secret),
                    Fixture.Text(queryId: 2, queryHash: "A2", sql: Secret),
                    Fixture.Text(queryId: 3, queryHash: "A3", sql: Secret),
                    Fixture.Text(queryId: 4, queryHash: "A4", sql: Secret))),
            CancellationToken.None);

        Assert.Equal([1L, 2L, 3L, 4L], collected.Queries.Select(query => query.QueryId));
        Assert.Equal(new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero), collected.Queries[0].LastExecutionAt);
        Assert.Equal(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero), collected.Queries[1].LastExecutionAt);
        Assert.Equal(new DateTimeOffset(2026, 7, 29, 8, 30, 0, TimeSpan.Zero), collected.Queries[2].LastExecutionAt);
        Assert.Equal(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local), TimeSpan.Zero), collected.Queries[3].LastExecutionAt);
        Assert.All(collected.Queries, query => Assert.Equal(TimeSpan.Zero, query.LastExecutionAt.Offset));
        AssertNoSecret(JsonSerializer.Serialize(collected.Queries));
    }

    [Fact]
    public async Task Reader_null_required_metric_is_invalid()
    {
        var row = WithObjectName(Fixture.Metric(), Secret);
        row[6] = DBNull.Value;

        var ex = await AssertInvalid(Fixture.Of(
            Fixture.StateSet("READ_WRITE"),
            Fixture.MetricSet(row),
            Fixture.TextSet(Fixture.Text(sql: Secret))));

        Assert.Equal("Query Store metric result set is missing a required value.", ex.Message);
    }

    [Fact]
    public async Task Reader_non_numeric_query_id_is_invalid()
    {
        var row = Fixture.Metric();
        row[0] = Secret;

        var ex = await AssertInvalid(Fixture.Of(
            Fixture.StateSet("READ_WRITE"),
            Fixture.MetricSet(row),
            Fixture.TextSet(Fixture.Text(sql: Secret))));

        Assert.Equal("Query Store metric result set has a malformed numeric value.", ex.Message);
    }

    [Theory]
    [InlineData("0xA1B2")]
    [InlineData("0XA1B2")]
    [InlineData("ZZZZ")]
    [InlineData("SELECT Secret FROM dbo.T")]
    public async Task Reader_malformed_query_hash_is_invalid(string hash)
    {
        var ex = await AssertInvalid(Fixture.Of(
            Fixture.StateSet("READ_WRITE"),
            Fixture.MetricSet(WithObjectName(Fixture.Metric(queryHash: hash), Secret)),
            Fixture.TextSet(Fixture.Text(sql: Secret))));

        Assert.Equal("Query Store metric result set has a malformed query hash.", ex.Message);
        Assert.DoesNotContain(hash, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_malformed_timestamp_is_invalid()
    {
        var row = Fixture.Metric();
        row[14] = Secret;

        var ex = await AssertInvalid(Fixture.Of(
            Fixture.StateSet("READ_WRITE"),
            Fixture.MetricSet(row),
            Fixture.TextSet(Fixture.Text(sql: Secret))));

        Assert.Equal("Query Store metric result set has a malformed timestamp.", ex.Message);
    }

    [Fact]
    public async Task Reader_null_query_text_is_invalid()
    {
        var text = Fixture.Text(sql: Secret);
        text[2] = null;

        var ex = await AssertInvalid(Fixture.Of(
            Fixture.StateSet("READ_WRITE"),
            Fixture.MetricSet(WithObjectName(Fixture.Metric(), Secret)),
            Fixture.TextSet(text)));

        Assert.Equal("Query Store text result set is missing a required value.", ex.Message);
    }

    [Fact]
    public async Task Reader_short_state_columns_are_invalid()
    {
        var ex = await AssertInvalid(Fixture.Of(
            Fixture.ColumnlessStateSet(),
            Fixture.MetricSet(Fixture.Metric()),
            Fixture.TextSet(Fixture.Text(sql: Secret))));

        Assert.Equal("Query Store state result set has unexpected columns.", ex.Message);
    }

    [Fact]
    public async Task Reader_short_metric_columns_are_invalid()
    {
        var row = WithObjectName(Fixture.Metric(), Secret);
        var ex = await AssertInvalid(Fixture.Of(
            Fixture.StateSet("READ_WRITE"),
            Fixture.MetricSet(14, row),
            Fixture.TextSet(Fixture.Text(sql: Secret))));

        Assert.Equal("Query Store metric result set has unexpected columns.", ex.Message);
    }

    [Fact]
    public async Task Reader_short_text_columns_are_invalid()
    {
        var ex = await AssertInvalid(Fixture.Of(
            Fixture.StateSet("READ_WRITE"),
            Fixture.MetricSet(WithObjectName(Fixture.Metric(), Secret)),
            Fixture.NarrowTextSet(Fixture.Text(sql: Secret))));

        Assert.Equal("Query Store text result set has unexpected columns.", ex.Message);
    }

    [Fact]
    public async Task Reader_extra_result_set_with_rows_is_invalid()
    {
        var ex = await AssertInvalid(Fixture.Of(
            Fixture.StateSet("READ_WRITE"),
            Fixture.MetricSet(Fixture.Metric()),
            Fixture.TextSet(Fixture.Text(sql: Secret)),
            Fixture.ExtraSet(Secret)));

        Assert.Equal("Query Store returned an unexpected result set.", ex.Message);
    }

    [Fact]
    public async Task Reader_raw_footprint_byte_count_changes_when_sql_text_changes()
    {
        var shortCollected = await QueryStoreTopQuery.ReadAsync(
            Fixture.Reader(querySqlText: "SELECT 1"),
            CancellationToken.None);
        var longCollected = await QueryStoreTopQuery.ReadAsync(
            Fixture.Reader(querySqlText: Secret),
            CancellationToken.None);

        AssertNoSecret(JsonSerializer.Serialize(shortCollected.Queries));
        AssertNoSecret(JsonSerializer.Serialize(longCollected.Queries));
        Assert.Equal(Secret, Assert.Single(longCollected.SensitiveTexts).QuerySqlText);
        Assert.True(longCollected.RawFootprint.Bytes > shortCollected.RawFootprint.Bytes);
    }

    [Fact]
    public async Task Reader_honors_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var reader = new CancelOnNextResultReader(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            QueryStoreTopQuery.ReadAsync(reader, cts.Token));

        Assert.Equal(cts.Token, reader.ObservedReadToken);
        Assert.Equal(cts.Token, reader.ObservedNextToken);
    }

    private static async Task<InvalidOperationException> AssertInvalid(ISqlReader reader)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            QueryStoreTopQuery.ReadAsync(reader, CancellationToken.None));
        AssertNoSecret(ex.Message);
        return ex;
    }

    private static void AssertNoSecret(string text) =>
        Assert.DoesNotContain("SELECT Secret", text, StringComparison.Ordinal);

    private static object?[] WithObjectName(object?[] row, string name)
    {
        row[2] = name;
        return row;
    }

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

    private sealed class CancelOnNextResultReader(CancellationTokenSource cts) : ISqlReader
    {
        private int _reads;

        public CancellationToken ObservedReadToken { get; private set; }

        public CancellationToken ObservedNextToken { get; private set; }

        public int FieldCount => 1;

        public int RecordsAffected => -1;

        public string GetName(int ordinal) => "actual_state_desc";

        public Type GetFieldType(int ordinal) => typeof(string);

        public bool GetAllowNull(int ordinal) => false;

        public object GetValue(int ordinal) => "READ_WRITE";

        public Task<bool> ReadAsync(CancellationToken ct)
        {
            ObservedReadToken = ct;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_reads++ == 0);
        }

        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            ObservedNextToken = ct;
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static class Fixture
    {
        private static readonly string[] StateColumns = ["actual_state_desc"];

        private static readonly string[] MetricColumns =
        [
            "query_id",
            "query_hash",
            "object_name",
            "execution_count",
            "plan_count",
            "total_duration_milliseconds",
            "average_duration_milliseconds",
            "maximum_duration_milliseconds",
            "total_cpu_milliseconds",
            "average_cpu_milliseconds",
            "maximum_cpu_milliseconds",
            "total_logical_reads",
            "average_logical_reads",
            "maximum_logical_reads",
            "last_execution_at",
        ];

        private static readonly string[] TextColumns = ["query_id", "query_hash", "query_sql_text"];

        public static ISqlReader Reader(string state = "READ_WRITE", string querySqlText = Secret) =>
            Of(StateSet(state), MetricSet(Metric()), TextSet(Text(sql: querySqlText)));

        public static ISqlReader Of(params object?[][][] sets) => new FakeReader(sets);

        public static object?[][] StateSet(params object?[] values)
        {
            var rows = new object?[values.Length][];
            for (var i = 0; i < values.Length; i++)
                rows[i] = [values[i]];
            return Set(StateColumns, rows);
        }

        public static object?[][] ColumnlessStateSet()
        {
            object?[][] set = new object?[2][];
            set[0] = [];
            set[1] = [];
            return set;
        }

        public static object?[][] MetricSet(params object?[][] rows) => Set(MetricColumns, rows);

        public static object?[][] MetricSet(int columnCount, params object?[][] rows) =>
            Set(
                MetricColumns.Take(columnCount).ToArray(),
                rows.Select(row => row.Take(columnCount).ToArray()).ToArray());

        public static object?[][] TextSet(params object?[][] rows) => Set(TextColumns, rows);

        public static object?[][] NarrowTextSet(object?[] row) =>
            Set(TextColumns.Take(2).ToArray(), [row.Take(2).ToArray()]);

        public static object?[][] ExtraSet(string secret) => Set(["leaked"], [secret]);

        public static object?[] Metric(
            object? queryId = null,
            object? queryHash = null,
            object? objectName = null,
            object? executionCount = null,
            object? planCount = null,
            object? totalDuration = null,
            object? averageDuration = null,
            object? maximumDuration = null,
            object? totalCpu = null,
            object? averageCpu = null,
            object? maximumCpu = null,
            object? totalReads = null,
            object? averageReads = null,
            object? maximumReads = null,
            object? lastExecution = null) =>
        [
            queryId ?? 42,
            queryHash ?? "A1B2",
            objectName ?? "dbo.T",
            executionCount ?? 4,
            planCount ?? 1,
            totalDuration ?? 10.5d,
            averageDuration ?? 2.5d,
            maximumDuration ?? 8.0d,
            totalCpu ?? 6.5d,
            averageCpu ?? 1.5d,
            maximumCpu ?? 4.0d,
            totalReads ?? 100.5d,
            averageReads ?? 25.25d,
            maximumReads ?? 80L,
            lastExecution ?? new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero),
        ];

        public static object?[] Text(object? queryId = null, object? queryHash = null, object? sql = null) =>
        [
            queryId ?? 42,
            queryHash ?? "A1B2",
            sql ?? Secret,
        ];

        private static object?[][] Set(string[] names, params object?[][] rows) => [[.. names], .. rows];
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
            ct.ThrowIfCancellationRequested();
            if (_row < Rows.Length)
            {
                _row++;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (++_set < sets.Length)
            {
                _row = 0;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
