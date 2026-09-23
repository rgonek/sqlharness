using System.Data;

namespace SqlHarness.Core;

/// <summary>
/// Fixed read-only missing-index batch. Result sets, in order: observation window
/// and user-table match count, ranked candidates, catalog columns, index headers
/// (heaps included), key and INCLUDE columns, then one compression row per partition.
/// Ranked candidates are materialized in @candidates before the dependent sets.
/// </summary>
internal static class IndexAnalysisQuery
{
    internal const string Sql = """
-- @observedAt is captured once. The match count is user tables, not missing-index rows.
-- Both object parameters null counts every non-shipped user table.
DECLARE @observedAt datetime2(7) = SYSUTCDATETIME();

DECLARE @candidates TABLE (
    candidate_id int NOT NULL,
    schema_name sysname NOT NULL,
    table_name sysname NOT NULL,
    equality_columns nvarchar(4000) NULL,
    inequality_columns nvarchar(4000) NULL,
    included_columns nvarchar(4000) NULL,
    user_seeks bigint NOT NULL,
    user_scans bigint NOT NULL,
    avg_total_user_cost float NOT NULL,
    avg_user_impact float NOT NULL,
    cumulative_impact_score float NOT NULL,
    last_user_seek datetime NULL,
    last_user_scan datetime NULL,
    object_id int NOT NULL
);

SELECT
    info.sqlserver_start_time AS observation_since,
    @observedAt AS observed_at,
    (
        SELECT COUNT_BIG(*)
        FROM sys.tables AS t
        INNER JOIN sys.schemas AS s
            ON s.schema_id = t.schema_id
        INNER JOIN sys.objects AS o
            ON o.object_id = t.object_id
        WHERE o.type = 'U'
          AND o.is_ms_shipped = 0
          AND (@objectName IS NULL OR t.name = @objectName)
          AND (@objectSchema IS NULL OR s.name = @objectSchema)
    ) AS object_match_count
FROM sys.dm_os_sys_info AS info;

INSERT INTO @candidates (
    candidate_id,
    schema_name,
    table_name,
    equality_columns,
    inequality_columns,
    included_columns,
    user_seeks,
    user_scans,
    avg_total_user_cost,
    avg_user_impact,
    cumulative_impact_score,
    last_user_seek,
    last_user_scan,
    object_id
)
    SELECT TOP (@top)
        mid.index_handle AS candidate_id,
        s.name AS schema_name,
        t.name AS table_name,
        mid.equality_columns,
        mid.inequality_columns,
        mid.included_columns,
        migs.user_seeks,
        migs.user_scans,
        migs.avg_total_user_cost,
        migs.avg_user_impact,
        (migs.user_seeks + migs.user_scans) * migs.avg_total_user_cost * (migs.avg_user_impact / 100.0) AS cumulative_impact_score,
        migs.last_user_seek,
        migs.last_user_scan,
        t.object_id
    FROM sys.dm_db_missing_index_details AS mid
    INNER JOIN sys.dm_db_missing_index_groups AS mig
        ON mig.index_handle = mid.index_handle
    INNER JOIN sys.dm_db_missing_index_group_stats AS migs
        ON migs.group_handle = mig.index_group_handle
    INNER JOIN sys.tables AS t
        ON t.object_id = mid.object_id
    INNER JOIN sys.schemas AS s
        ON s.schema_id = t.schema_id
    INNER JOIN sys.objects AS o
        ON o.object_id = t.object_id
    WHERE mid.database_id = DB_ID()
      AND o.type = 'U'
      AND o.is_ms_shipped = 0
      AND (@objectName IS NULL OR t.name = @objectName)
      AND (@objectSchema IS NULL OR s.name = @objectSchema)
    ORDER BY
        cumulative_impact_score DESC,
        (migs.user_seeks + migs.user_scans) DESC,
        schema_name ASC,
        table_name ASC,
        candidate_id ASC;

SELECT
    candidate_id,
    schema_name,
    table_name,
    equality_columns,
    inequality_columns,
    included_columns,
    user_seeks,
    user_scans,
    avg_total_user_cost,
    avg_user_impact,
    cumulative_impact_score,
    last_user_seek,
    last_user_scan
FROM @candidates
ORDER BY
    cumulative_impact_score DESC,
    (user_seeks + user_scans) DESC,
    schema_name ASC,
    table_name ASC,
    candidate_id ASC;

SELECT
    tables.schema_name,
    tables.table_name,
    col.name AS column_name
FROM (
    SELECT DISTINCT
        object_id,
        schema_name,
        table_name
    FROM @candidates
) AS tables
INNER JOIN sys.columns AS col
    ON col.object_id = tables.object_id
ORDER BY
    tables.schema_name,
    tables.table_name,
    col.column_id;

SELECT
    tables.schema_name,
    tables.table_name,
    i.index_id,
    i.name AS index_name,
    i.type_desc,
    i.is_unique,
    i.is_primary_key,
    i.is_unique_constraint,
    i.is_disabled,
    i.filter_definition
FROM (
    SELECT DISTINCT
        object_id,
        schema_name,
        table_name
    FROM @candidates
) AS tables
INNER JOIN sys.indexes AS i
    ON i.object_id = tables.object_id
ORDER BY
    tables.schema_name,
    tables.table_name,
    i.index_id;

SELECT
    tables.schema_name,
    tables.table_name,
    ic.index_id,
    col.name AS column_name,
    ic.key_ordinal,
    ic.is_included_column,
    ic.is_descending_key,
    ic.index_column_id
FROM (
    SELECT DISTINCT
        object_id,
        schema_name,
        table_name
    FROM @candidates
) AS tables
INNER JOIN sys.index_columns AS ic
    ON ic.object_id = tables.object_id
INNER JOIN sys.columns AS col
    ON col.object_id = ic.object_id
   AND col.column_id = ic.column_id
ORDER BY
    tables.schema_name,
    tables.table_name,
    ic.index_id,
    ic.is_included_column,
    ic.key_ordinal,
    ic.index_column_id;

SELECT
    tables.schema_name,
    tables.table_name,
    p.index_id,
    p.data_compression_desc
FROM (
    SELECT DISTINCT
        object_id,
        schema_name,
        table_name
    FROM @candidates
) AS tables
INNER JOIN sys.partitions AS p
    ON p.object_id = tables.object_id
ORDER BY
    tables.schema_name,
    tables.table_name,
    p.index_id,
    p.partition_number;
""";

    internal static IReadOnlyList<SqlHarnessParameter> Parameters(
        int top,
        string? schema,
        string? table) =>
    [
        new("@top", SqlDbType.Int, top, null),
        new("@objectSchema", SqlDbType.NVarChar, schema is null ? DBNull.Value : schema, 128),
        new("@objectName", SqlDbType.NVarChar, table is null ? DBNull.Value : table, 128),
    ];
}