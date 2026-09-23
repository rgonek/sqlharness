using System.Data;
using System.Globalization;

namespace SqlHarness.Core;

/// <summary>
/// Fixed read-only Query Store batch. Result sets, in order: actual_state_desc,
/// ranked value-free metrics, then query text for those same ids.
/// Duration and CPU are milliseconds; logical reads stay pages.
/// </summary>
internal static class QueryStoreTopQuery
{
    internal const string Sql = """
-- Ranking is materialized once in @topQueries so metrics and text cannot disagree.
-- Duration and CPU totals are microseconds converted to milliseconds; logical reads stay pages.
DECLARE @topQueries TABLE (
    query_id bigint NOT NULL,
    query_hash varchar(16) NOT NULL,
    object_name nvarchar(257) NULL,
    execution_count bigint NOT NULL,
    plan_count int NOT NULL,
    total_duration_milliseconds float NULL,
    average_duration_milliseconds float NULL,
    maximum_duration_milliseconds float NULL,
    total_cpu_milliseconds float NULL,
    average_cpu_milliseconds float NULL,
    maximum_cpu_milliseconds float NULL,
    total_logical_reads float NULL,
    average_logical_reads float NULL,
    maximum_logical_reads bigint NULL,
    last_execution_at datetimeoffset NULL
);

INSERT INTO @topQueries (
    query_id,
    query_hash,
    object_name,
    execution_count,
    plan_count,
    total_duration_milliseconds,
    average_duration_milliseconds,
    maximum_duration_milliseconds,
    total_cpu_milliseconds,
    average_cpu_milliseconds,
    maximum_cpu_milliseconds,
    total_logical_reads,
    average_logical_reads,
    maximum_logical_reads,
    last_execution_at
)
SELECT TOP (@top)
    q.query_id,
    CONVERT(varchar(16), q.query_hash, 2) AS query_hash,
    CASE
        WHEN q.object_id = 0 THEN NULL
        ELSE s.name + N'.' + o.name
    END AS object_name,
    SUM(rs.count_executions) AS execution_count,
    COUNT(DISTINCT p.plan_id) AS plan_count,
    SUM(rs.avg_duration * rs.count_executions) / 1000.0 AS total_duration_milliseconds,
    SUM(rs.avg_duration * rs.count_executions) / 1000.0 / NULLIF(SUM(rs.count_executions), 0) AS average_duration_milliseconds,
    MAX(rs.max_duration) / 1000.0 AS maximum_duration_milliseconds,
    SUM(rs.avg_cpu_time * rs.count_executions) / 1000.0 AS total_cpu_milliseconds,
    SUM(rs.avg_cpu_time * rs.count_executions) / 1000.0 / NULLIF(SUM(rs.count_executions), 0) AS average_cpu_milliseconds,
    MAX(rs.max_cpu_time) / 1000.0 AS maximum_cpu_milliseconds,
    SUM(rs.avg_logical_io_reads * rs.count_executions) AS total_logical_reads,
    SUM(rs.avg_logical_io_reads * rs.count_executions) / NULLIF(SUM(rs.count_executions), 0) AS average_logical_reads,
    MAX(rs.max_logical_io_reads) AS maximum_logical_reads,
    MAX(rs.last_execution_time) AS last_execution_at
FROM sys.query_store_runtime_stats AS rs
INNER JOIN sys.query_store_runtime_stats_interval AS rsi
    ON rsi.runtime_stats_interval_id = rs.runtime_stats_interval_id
INNER JOIN sys.query_store_plan AS p
    ON p.plan_id = rs.plan_id
INNER JOIN sys.query_store_query AS q
    ON q.query_id = p.query_id
LEFT JOIN sys.objects AS o
    ON o.object_id = q.object_id
LEFT JOIN sys.schemas AS s
    ON s.schema_id = o.schema_id
WHERE rsi.start_time < SYSUTCDATETIME()
  AND rsi.end_time >= DATEADD(minute, -@windowMinutes, SYSUTCDATETIME())
  AND rs.execution_type = 0
GROUP BY
    q.query_id,
    q.query_hash,
    q.object_id,
    s.name,
    o.name
ORDER BY
    total_duration_milliseconds DESC,
    total_cpu_milliseconds DESC,
    execution_count DESC,
    query_id ASC;

SELECT
    actual_state_desc
FROM sys.database_query_store_options;

SELECT
    query_id,
    query_hash,
    object_name,
    execution_count,
    plan_count,
    total_duration_milliseconds,
    average_duration_milliseconds,
    maximum_duration_milliseconds,
    total_cpu_milliseconds,
    average_cpu_milliseconds,
    maximum_cpu_milliseconds,
    total_logical_reads,
    average_logical_reads,
    maximum_logical_reads,
    last_execution_at
FROM @topQueries
ORDER BY
    total_duration_milliseconds DESC,
    total_cpu_milliseconds DESC,
    execution_count DESC,
    query_id ASC;

SELECT
    t.query_id,
    t.query_hash,
    qt.query_sql_text
FROM @topQueries AS t
INNER JOIN sys.query_store_query AS q
    ON q.query_id = t.query_id
INNER JOIN sys.query_store_query_text AS qt
    ON qt.query_text_id = q.query_text_id
ORDER BY
    t.total_duration_milliseconds DESC,
    t.total_cpu_milliseconds DESC,
    t.execution_count DESC,
    t.query_id ASC;
""";

    internal static IReadOnlyList<SqlHarnessParameter> Parameters(int windowMinutes, int top) =>
    [
        new("@windowMinutes", SqlDbType.Int, windowMinutes, null),
        new("@top", SqlDbType.Int, top, null),
    ];

    /// <summary>
    /// Reads the state, metric, and query-text result sets.
    /// <paramref name="reader"/> must be positioned on the state set.
    /// Query text stays out of <see cref="QueryStoreTopItemReport"/> and is retained only as sensitive text and in the raw footprint.
    /// </summary>
    internal static async Task<CollectedQueryStoreTop> ReadAsync(ISqlReader reader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        using var raw = new CanonicalResultAccumulator();

        object? stateValue = null;
        var stateRows = 0;
        await ReadSet(reader, raw, row =>
        {
            if (row.Length < 1)
                throw new InvalidOperationException("Query Store state result set has unexpected columns.");
            stateRows++;
            if (stateRows > 1)
                throw new InvalidOperationException("Query Store state result set returned extra rows.");
            stateValue = row[0];
        }, ct);

        if (stateRows != 1)
            throw new InvalidOperationException("Query Store state result set is empty.");

        EnsureReadable(stateValue);

        if (!await reader.NextResultAsync(ct))
            throw new InvalidOperationException("Query Store metric result set is missing.");

        var queries = new List<QueryStoreTopItemReport>();
        await ReadSet(reader, raw, row => queries.Add(ReadMetric(row)), ct);

        if (!await reader.NextResultAsync(ct))
            throw new InvalidOperationException("Query Store text result set is missing.");

        var texts = new List<SensitiveQueryStoreText>();
        await ReadSet(reader, raw, row => texts.Add(ReadText(row)), ct);
        RequireMatchingTexts(queries, texts);

        while (await reader.NextResultAsync(ct))
        {
            if (await reader.ReadAsync(ct))
                throw new InvalidOperationException("Query Store returned an unexpected result set.");
        }

        return new CollectedQueryStoreTop(queries, texts, raw.Complete().Footprint);
    }

    private static async Task ReadSet(
        ISqlReader reader,
        CanonicalResultAccumulator raw,
        Action<object?[]> add,
        CancellationToken ct)
    {
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(i => new CanonicalColumn(
                i,
                reader.GetName(i),
                reader.GetFieldType(i).FullName ?? "object",
                reader.GetAllowNull(i)))
            .ToArray();
        raw.BeginResultSet(columns);
        while (await reader.ReadAsync(ct))
        {
            var row = Enumerable.Range(0, reader.FieldCount).Select(i =>
            {
                // SequentialAccess allows each ordinal only once per row.
                var value = reader.GetValue(i);
                return value is DBNull ? null : value;
            }).ToArray();
            raw.AddRow(row);
            add(row);
        }

        raw.EndResultSet();
    }

    private static void EnsureReadable(object? value)
    {
        if (value is null or DBNull)
            throw new QueryStoreUnavailableException(null);

        var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        var token = text?.Trim().ToUpperInvariant();
        if (token is "READ_WRITE" or "READ_ONLY")
            return;

        throw new QueryStoreUnavailableException(text);
    }

    private static QueryStoreTopItemReport ReadMetric(object?[] row)
    {
        if (row.Length < 15)
            throw new InvalidOperationException("Query Store metric result set has unexpected columns.");

        return new QueryStoreTopItemReport(
            RequiredInt64(row[0]),
            RequiredHash(row[1], MetricMissing, MetricHash),
            OptionalText(row[2]),
            RequiredInt64(row[3]),
            RequiredInt32(row[4]),
            RequiredDecimal(row[5]),
            RequiredDecimal(row[6]),
            RequiredDecimal(row[7]),
            RequiredDecimal(row[8]),
            RequiredDecimal(row[9]),
            RequiredDecimal(row[10]),
            RequiredDecimal(row[11]),
            RequiredDecimal(row[12]),
            RequiredDecimal(row[13]),
            RequiredTimestamp(row[14]));
    }

    private static SensitiveQueryStoreText ReadText(object?[] row)
    {
        if (row.Length < 3)
            throw new InvalidOperationException("Query Store text result set has unexpected columns.");

        return new SensitiveQueryStoreText(
            RequiredInt64(row[0], TextMissing, TextNumber),
            RequiredHash(row[1], TextMissing, TextHash),
            RequiredText(row[2], TextMissing));
    }

    private static void RequireMatchingTexts(
        IReadOnlyList<QueryStoreTopItemReport> queries,
        IReadOnlyList<SensitiveQueryStoreText> texts)
    {
        var metricIds = new HashSet<long>();
        foreach (var query in queries)
        {
            if (!metricIds.Add(query.QueryId))
                throw new InvalidOperationException("Query Store metric result set contains a duplicate query.");
        }

        var byId = new Dictionary<long, string>(texts.Count);
        foreach (var text in texts)
        {
            if (!byId.TryAdd(text.QueryId, text.QueryHash))
                throw new InvalidOperationException("Query Store text result set contains a duplicate query.");
        }

        if (byId.Count != queries.Count)
            throw new InvalidOperationException("Query Store text result set does not match the metric queries.");

        foreach (var query in queries)
        {
            if (!byId.TryGetValue(query.QueryId, out var hash) || !string.Equals(hash, query.QueryHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Query Store text result set does not match the metric queries.");
        }
    }

    private static long RequiredInt64(object? value) =>
        RequiredInt64(value, MetricMissing, MetricNumber);

    private static long RequiredInt64(object? value, string missingMessage, string malformedMessage)
    {
        if (value is null or DBNull)
            throw new InvalidOperationException(missingMessage);

        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException(malformedMessage);
        }
    }

    private static int RequiredInt32(object? value)
    {
        if (value is null or DBNull)
            throw new InvalidOperationException(MetricMissing);

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException(MetricNumber);
        }
    }

    private static decimal RequiredDecimal(object? value)
    {
        if (value is null or DBNull)
            throw new InvalidOperationException(MetricMissing);

        try
        {
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException(MetricNumber);
        }
    }

    private static string RequiredHash(object? value, string missingMessage, string malformedMessage)
    {
        if (value is null or DBNull)
            throw new InvalidOperationException(missingMessage);

        var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(text)
            || text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !IsAsciiHex(text))
        {
            throw new InvalidOperationException(malformedMessage);
        }

        return text.ToUpperInvariant();
    }

    private static string RequiredText(object? value, string missingMessage)
    {
        if (value is null or DBNull)
            throw new InvalidOperationException(missingMessage);

        return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException(missingMessage);
    }

    private static string? OptionalText(object? value) =>
        value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    private static DateTimeOffset RequiredTimestamp(object? value)
    {
        if (value is null or DBNull)
            throw new InvalidOperationException(MetricMissing);

        return value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime(),
            // Query Store clock times with Kind Unspecified or Utc are already UTC.
            DateTime timestamp when timestamp.Kind == DateTimeKind.Local => new DateTimeOffset(timestamp).ToUniversalTime(),
            DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException(MetricTime),
        };
    }

    private static bool IsAsciiHex(string text)
    {
        foreach (var ch in text)
        {
            if (ch is not (>= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    private const string MetricMissing = "Query Store metric result set is missing a required value.";
    private const string MetricNumber = "Query Store metric result set has a malformed numeric value.";
    private const string MetricHash = "Query Store metric result set has a malformed query hash.";
    private const string MetricTime = "Query Store metric result set has a malformed timestamp.";
    private const string TextMissing = "Query Store text result set is missing a required value.";
    private const string TextNumber = "Query Store text result set has a malformed numeric value.";
    private const string TextHash = "Query Store text result set has a malformed query hash.";
}

internal sealed class QueryStoreUnavailableException : Exception
{
    internal QueryStoreUnavailableException(string? state)
        : base($"Query Store is not readable: {SafeState(state)}.")
    {
    }

    private static string SafeState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return "unknown";

        var token = state.Trim().ToUpperInvariant();
        return IsSafeStateToken(token) ? token : "unknown";
    }

    private static bool IsSafeStateToken(string token)
    {
        if (token.Length is < 1 or > 32)
            return false;

        foreach (var ch in token)
        {
            if (ch is not (>= 'A' and <= 'Z' or '_'))
                return false;
        }

        return true;
    }
}

internal sealed record SensitiveQueryStoreText(long QueryId, string QueryHash, string QuerySqlText);

internal sealed record CollectedQueryStoreTop(
    IReadOnlyList<QueryStoreTopItemReport> Queries,
    IReadOnlyList<SensitiveQueryStoreText> SensitiveTexts,
    OutputFootprint RawFootprint);
