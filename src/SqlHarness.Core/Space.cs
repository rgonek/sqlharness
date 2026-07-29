using System.Data;

namespace SqlHarness.Core;

/// <summary>
/// Fixed read-only DMV batch for database file, allocation, top-table, and optional per-index space.
/// Result sets (in order): object match count, files, allocation, top tables, indexes (or empty shape).
/// </summary>
internal static class SpaceQuery
{
    internal const string Sql = """
-- 1. Exact object match count (0 when @objectName is null; used for missing/ambiguous checks).
SELECT COUNT_BIG(*) AS MatchCount
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE @objectName IS NOT NULL
  AND t.name = @objectName
  AND (@objectSchema IS NULL OR s.name = @objectSchema);

-- 2. Database files (data + log).
SELECT
    f.name AS LogicalName,
    f.type_desc AS Type,
    f.physical_name AS PhysicalName,
    CAST(f.size AS decimal(19,2)) * 8 / 1024 AS SizeMb,
    CAST(ISNULL(FILEPROPERTY(name,'SpaceUsed'), 0) AS decimal(19,2)) * 8 / 1024 AS UsedMb,
    CAST(f.size - ISNULL(FILEPROPERTY(name,'SpaceUsed'), 0) AS decimal(19,2)) * 8 / 1024 AS FreeMb
FROM sys.database_files f
ORDER BY f.type, f.name;

-- 3. One aggregate allocation row for the database.
SELECT
    CAST(ISNULL(SUM(ps.reserved_page_count), 0) AS decimal(19,2)) * 8 / 1024 AS ReservedMb,
    CAST(ISNULL(SUM(ps.used_page_count), 0) AS decimal(19,2)) * 8 / 1024 AS UsedMb,
    CAST(ISNULL(SUM(
        CASE
            WHEN ps.index_id IN (0, 1)
                THEN ps.in_row_data_page_count + ps.lob_used_page_count + ps.row_overflow_used_page_count
            ELSE ps.lob_used_page_count + ps.row_overflow_used_page_count
        END), 0) AS decimal(19,2)) * 8 / 1024 AS DataMb
FROM sys.dm_db_partition_stats ps;

-- 4. Top @top tables by reserved pages, then schema/name.
SELECT TOP (@top)
    SchemaName,
    ObjectName,
    [Rows],
    ReservedMb,
    UsedMb,
    DataMb
FROM (
    SELECT
        s.name AS SchemaName,
        t.name AS ObjectName,
        ISNULL(SUM(CASE WHEN ps.index_id IN (0,1) THEN ps.row_count ELSE CONVERT(bigint, 0) END), CONVERT(bigint, 0)) AS [Rows],
        ISNULL(SUM(ps.reserved_page_count), CONVERT(bigint, 0)) AS ReservedPages,
        CAST(ISNULL(SUM(ps.reserved_page_count), 0) AS decimal(19,2)) * 8 / 1024 AS ReservedMb,
        CAST(ISNULL(SUM(ps.used_page_count), 0) AS decimal(19,2)) * 8 / 1024 AS UsedMb,
        CAST(ISNULL(SUM(
            CASE
                WHEN ps.index_id IN (0, 1)
                    THEN ps.in_row_data_page_count + ps.lob_used_page_count + ps.row_overflow_used_page_count
                ELSE ps.lob_used_page_count + ps.row_overflow_used_page_count
            END), 0) AS decimal(19,2)) * 8 / 1024 AS DataMb
    FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    LEFT JOIN sys.dm_db_partition_stats ps ON ps.object_id = t.object_id
    GROUP BY s.name, t.name, t.object_id
) tableSpace
ORDER BY ReservedPages DESC, SchemaName, ObjectName;

-- 5. Per-index rows for the exact object when requested; otherwise an empty shaped set.
IF @objectName IS NOT NULL
BEGIN
    SELECT
        s.name AS SchemaName,
        t.name AS TableName,
        ISNULL(i.name, CASE WHEN i.index_id = 0 THEN N'HEAP' ELSE N'' END) AS IndexName,
        i.type_desc AS Type,
        CAST(ISNULL(SUM(ps.reserved_page_count), 0) AS decimal(19,2)) * 8 / 1024 AS ReservedMb,
        CAST(ISNULL(SUM(ps.used_page_count), 0) AS decimal(19,2)) * 8 / 1024 AS UsedMb,
        CAST(ISNULL(SUM(
            CASE
                WHEN ps.index_id IN (0, 1)
                    THEN ps.in_row_data_page_count + ps.lob_used_page_count + ps.row_overflow_used_page_count
                ELSE ps.lob_used_page_count + ps.row_overflow_used_page_count
            END), 0) AS decimal(19,2)) * 8 / 1024 AS DataMb,
        CASE
            WHEN COUNT(DISTINCT p.data_compression_desc) > 1 THEN N'MIXED'
            ELSE MAX(p.data_compression_desc)
        END AS Compression
    FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    INNER JOIN sys.indexes i ON i.object_id = t.object_id
    LEFT JOIN sys.partitions p
        ON p.object_id = i.object_id
       AND p.index_id = i.index_id
    LEFT JOIN sys.dm_db_partition_stats ps
        ON ps.object_id = p.object_id
       AND ps.index_id = p.index_id
       AND ps.partition_number = p.partition_number
    WHERE t.name = @objectName
      AND (@objectSchema IS NULL OR s.name = @objectSchema)
    GROUP BY s.name, t.name, i.name, i.index_id, i.type_desc
    ORDER BY
        ISNULL(SUM(ps.reserved_page_count), CONVERT(bigint, 0)) DESC,
        s.name,
        t.name,
        ISNULL(i.name, CASE WHEN i.index_id = 0 THEN N'HEAP' ELSE N'' END);
END
ELSE
BEGIN
    SELECT
        CONVERT(sysname, NULL) AS SchemaName,
        CONVERT(sysname, NULL) AS TableName,
        CONVERT(sysname, NULL) AS IndexName,
        CONVERT(nvarchar(60), NULL) AS Type,
        CONVERT(decimal(19,2), NULL) AS ReservedMb,
        CONVERT(decimal(19,2), NULL) AS UsedMb,
        CONVERT(decimal(19,2), NULL) AS DataMb,
        CONVERT(nvarchar(60), NULL) AS Compression
    WHERE 1 = 0;
END
""";

    internal static IReadOnlyList<SqlHarnessParameter> Parameters(
        int top,
        string? objectSchema,
        string? objectName) =>
    [
        new("@top", SqlDbType.Int, top, null),
        new("@objectSchema", SqlDbType.NVarChar, objectSchema is null ? DBNull.Value : objectSchema, 128),
        new("@objectName", SqlDbType.NVarChar, objectName is null ? DBNull.Value : objectName, 128),
    ];
}
