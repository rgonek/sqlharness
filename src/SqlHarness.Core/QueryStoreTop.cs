using System.Data;

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
}
