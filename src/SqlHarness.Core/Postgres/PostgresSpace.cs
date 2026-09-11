namespace SqlHarness.Core.Postgres;

/// <summary>
/// Fixed read-only pg_catalog batch for database file, allocation, top-table, and optional per-index space.
/// Result sets (in order): object match count, files, allocation, top tables, indexes (or empty shape).
/// Shares shaping with <see cref="SpaceQuery.ReadAsync"/>.
/// </summary>
internal static class PostgresSpace
{
    internal const string Sql = """
-- 1. Exact object match count (0 when @objectName is null; used for missing/ambiguous checks).
SELECT COUNT(*)::bigint AS "MatchCount"
FROM pg_class c
INNER JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE @objectName IS NOT NULL
  AND c.relkind IN ('r', 'p')
  AND n.nspname <> 'pg_catalog'
  AND n.nspname <> 'information_schema'
  AND n.nspname NOT LIKE 'pg_toast%'
  AND n.nspname NOT LIKE 'pg_temp%'
  AND c.relname = @objectName
  AND (@objectSchema IS NULL OR n.nspname = @objectSchema);

-- 2. One DATA "file" row for the database (no WAL row).
SELECT
    current_database() AS "LogicalName",
    'DATA' AS "Type",
    CASE
        WHEN EXISTS (SELECT 1 FROM pg_settings WHERE name = 'data_directory')
            THEN current_setting('data_directory')
        ELSE NULL
    END AS "PhysicalName",
    ROUND(pg_database_size(current_database())::numeric / (1024 * 1024), 2) AS "SizeMb",
    ROUND(pg_database_size(current_database())::numeric / (1024 * 1024), 2) AS "UsedMb",
    CAST(0 AS numeric) AS "FreeMb";

-- 3. One aggregate allocation row for the database.
SELECT
    ROUND(pg_database_size(current_database())::numeric / (1024 * 1024), 2) AS "ReservedMb",
    ROUND(COALESCE(SUM(pg_total_relation_size(c.oid)), 0)::numeric / (1024 * 1024), 2) AS "UsedMb",
    ROUND(COALESCE(SUM(pg_relation_size(c.oid)), 0)::numeric / (1024 * 1024), 2) AS "DataMb"
FROM pg_class c
INNER JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r', 'p')
  AND n.nspname <> 'pg_catalog'
  AND n.nspname <> 'information_schema'
  AND n.nspname NOT LIKE 'pg_toast%'
  AND n.nspname NOT LIKE 'pg_temp%';

-- 4. Top @top tables by total relation size, then schema/name.
SELECT
    n.nspname AS "SchemaName",
    c.relname AS "ObjectName",
    CASE WHEN c.reltuples < 0 THEN CAST(0 AS bigint) ELSE c.reltuples::bigint END AS "Rows",
    ROUND(pg_total_relation_size(c.oid)::numeric / (1024 * 1024), 2) AS "ReservedMb",
    ROUND(pg_total_relation_size(c.oid)::numeric / (1024 * 1024), 2) AS "UsedMb",
    ROUND(pg_relation_size(c.oid)::numeric / (1024 * 1024), 2) AS "DataMb"
FROM pg_class c
INNER JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r', 'p')
  AND n.nspname <> 'pg_catalog'
  AND n.nspname <> 'information_schema'
  AND n.nspname NOT LIKE 'pg_toast%'
  AND n.nspname NOT LIKE 'pg_temp%'
ORDER BY pg_total_relation_size(c.oid) DESC, n.nspname, c.relname
LIMIT @top;

-- 5. Per-index rows for the exact object when requested; otherwise an empty shaped set.
SELECT
    n.nspname AS "SchemaName",
    t.relname AS "TableName",
    i.relname AS "IndexName",
    am.amname AS "Type",
    ROUND(pg_relation_size(i.oid)::numeric / (1024 * 1024), 2) AS "ReservedMb",
    ROUND(pg_relation_size(i.oid)::numeric / (1024 * 1024), 2) AS "UsedMb",
    ROUND(pg_relation_size(i.oid)::numeric / (1024 * 1024), 2) AS "DataMb",
    CAST(NULL AS text) AS "Compression"
FROM pg_class t
INNER JOIN pg_namespace n ON n.oid = t.relnamespace
INNER JOIN pg_index ix ON ix.indrelid = t.oid
INNER JOIN pg_class i ON i.oid = ix.indexrelid
INNER JOIN pg_am am ON am.oid = i.relam
WHERE @objectName IS NOT NULL
  AND t.relkind IN ('r', 'p')
  AND n.nspname <> 'pg_catalog'
  AND n.nspname <> 'information_schema'
  AND n.nspname NOT LIKE 'pg_toast%'
  AND n.nspname NOT LIKE 'pg_temp%'
  AND t.relname = @objectName
  AND (@objectSchema IS NULL OR n.nspname = @objectSchema)
ORDER BY pg_relation_size(i.oid) DESC, n.nspname, t.relname, i.relname;
""";

    /// <summary>
    /// Reads the five-result space batch into the same records as <see cref="SpaceQuery"/>.
    /// </summary>
    internal static Task<SpaceQuery.CollectedSpaceReport> ReadAsync(
        ISqlReader reader,
        bool objectRequested,
        CancellationToken ct) =>
        SpaceQuery.ReadAsync(reader, objectRequested, ct);
}
