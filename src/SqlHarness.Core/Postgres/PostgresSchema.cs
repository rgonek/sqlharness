namespace SqlHarness.Core.Postgres;

internal static class PostgresSchema
{
    internal const string Sql = """
SELECT COUNT(*)::bigint AS "TotalObjects"
FROM pg_class c
INNER JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r', 'p', 'v')
  AND n.nspname <> 'pg_catalog'
  AND n.nspname <> 'information_schema'
  AND n.nspname NOT LIKE 'pg_toast%'
  AND n.nspname NOT LIKE 'pg_temp%'
  AND (
    (@objectName IS NULL AND (@filter IS NULL OR c.relname LIKE @filter))
    OR
    (@objectName IS NOT NULL AND c.relname = @objectName
     AND (@objectSchema IS NULL OR n.nspname = @objectSchema))
  );

SELECT
  n.nspname AS "SchemaName",
  c.relname AS "ObjectName",
  CASE WHEN c.relkind = 'v' THEN 'VIEW' ELSE 'TABLE' END AS "Kind"
FROM pg_class c
INNER JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r', 'p', 'v')
  AND n.nspname <> 'pg_catalog'
  AND n.nspname <> 'information_schema'
  AND n.nspname NOT LIKE 'pg_toast%'
  AND n.nspname NOT LIKE 'pg_temp%'
  AND (
    (@objectName IS NULL AND (@filter IS NULL OR c.relname LIKE @filter))
    OR
    (@objectName IS NOT NULL AND c.relname = @objectName
     AND (@objectSchema IS NULL OR n.nspname = @objectSchema))
  )
ORDER BY n.nspname, c.relname, c.oid
LIMIT @maxObjects;

WITH selected_objects AS (
  SELECT
    c.oid AS object_oid,
    n.nspname AS "SchemaName",
    c.relname AS "ObjectName",
    CASE WHEN c.relkind = 'v' THEN 'VIEW' ELSE 'TABLE' END AS "Kind"
  FROM pg_class c
  INNER JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE c.relkind IN ('r', 'p', 'v')
    AND n.nspname <> 'pg_catalog'
    AND n.nspname <> 'information_schema'
    AND n.nspname NOT LIKE 'pg_toast%'
    AND n.nspname NOT LIKE 'pg_temp%'
    AND (
      (@objectName IS NULL AND (@filter IS NULL OR c.relname LIKE @filter))
      OR
      (@objectName IS NOT NULL AND c.relname = @objectName
       AND (@objectSchema IS NULL OR n.nspname = @objectSchema))
    )
  ORDER BY n.nspname, c.relname, c.oid
  LIMIT @maxObjects
)
SELECT
  o."SchemaName",
  o."ObjectName",
  a.attname AS "ColumnName",
  format_type(a.atttypid, a.atttypmod) AS "TypeName",
  NOT a.attnotnull AS "Nullable",
  EXISTS (
    SELECT 1
    FROM pg_index i
    WHERE i.indrelid = o.object_oid
      AND i.indisprimary
      AND a.attnum = ANY (i.indkey)
  ) AS "InPrimaryKey"
FROM selected_objects o
INNER JOIN pg_attribute a ON a.attrelid = o.object_oid
WHERE a.attnum > 0
  AND NOT a.attisdropped
ORDER BY o."SchemaName", o."ObjectName", a.attnum;

WITH selected_objects AS (
  SELECT
    c.oid AS object_oid,
    n.nspname AS "SchemaName",
    c.relname AS "ObjectName",
    CASE WHEN c.relkind = 'v' THEN 'VIEW' ELSE 'TABLE' END AS "Kind"
  FROM pg_class c
  INNER JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE c.relkind IN ('r', 'p', 'v')
    AND n.nspname <> 'pg_catalog'
    AND n.nspname <> 'information_schema'
    AND n.nspname NOT LIKE 'pg_toast%'
    AND n.nspname NOT LIKE 'pg_temp%'
    AND (
      (@objectName IS NULL AND (@filter IS NULL OR c.relname LIKE @filter))
      OR
      (@objectName IS NOT NULL AND c.relname = @objectName
       AND (@objectSchema IS NULL OR n.nspname = @objectSchema))
    )
  ORDER BY n.nspname, c.relname, c.oid
  LIMIT @maxObjects
)
SELECT
  o."SchemaName",
  o."ObjectName",
  ic.relname AS "IndexName",
  i.indisunique AS "IsUnique",
  a.attname AS "ColumnName",
  (ord.ordinality > i.indnkeyatts) AS "IsIncluded",
  CASE WHEN ord.ordinality > i.indnkeyatts THEN 0 ELSE ord.ordinality END AS "KeyOrdinal",
  CASE WHEN ord.ordinality > i.indnkeyatts THEN ord.ordinality - i.indnkeyatts ELSE 0 END AS "IndexColumnOrdinal",
  pg_get_expr(i.indpred, i.indrelid) AS "Filter"
FROM selected_objects o
INNER JOIN pg_index i ON i.indrelid = o.object_oid
INNER JOIN pg_class ic ON ic.oid = i.indexrelid
CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS ord(attnum, ordinality)
INNER JOIN pg_attribute a ON a.attrelid = o.object_oid AND a.attnum = ord.attnum
WHERE ord.attnum > 0
ORDER BY o."SchemaName", o."ObjectName", ic.relname, (ord.ordinality > i.indnkeyatts), ord.ordinality;

WITH selected_objects AS (
  SELECT
    c.oid AS object_oid,
    n.nspname AS "SchemaName",
    c.relname AS "ObjectName",
    CASE WHEN c.relkind = 'v' THEN 'VIEW' ELSE 'TABLE' END AS "Kind"
  FROM pg_class c
  INNER JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE c.relkind IN ('r', 'p', 'v')
    AND n.nspname <> 'pg_catalog'
    AND n.nspname <> 'information_schema'
    AND n.nspname NOT LIKE 'pg_toast%'
    AND n.nspname NOT LIKE 'pg_temp%'
    AND (
      (@objectName IS NULL AND (@filter IS NULL OR c.relname LIKE @filter))
      OR
      (@objectName IS NOT NULL AND c.relname = @objectName
       AND (@objectSchema IS NULL OR n.nspname = @objectSchema))
    )
  ORDER BY n.nspname, c.relname, c.oid
  LIMIT @maxObjects
)
SELECT
  o."SchemaName",
  o."ObjectName",
  con.conname AS "ForeignKeyName",
  pa.attname AS "ColumnName",
  rn.nspname AS "ReferencedSchema",
  rc.relname AS "ReferencedObject",
  ra.attname AS "ReferencedColumn",
  ord.ordinality AS "ConstraintOrdinal"
FROM selected_objects o
INNER JOIN pg_constraint con ON con.conrelid = o.object_oid AND con.contype = 'f'
INNER JOIN pg_class rc ON rc.oid = con.confrelid
INNER JOIN pg_namespace rn ON rn.oid = rc.relnamespace
CROSS JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS ord(attnum, fattnum, ordinality)
INNER JOIN pg_attribute pa ON pa.attrelid = o.object_oid AND pa.attnum = ord.attnum
INNER JOIN pg_attribute ra ON ra.attrelid = con.confrelid AND ra.attnum = ord.fattnum
ORDER BY o."SchemaName", o."ObjectName", con.conname, ord.ordinality;
""";

    /// <summary>
    /// Shapes pg_catalog result sets into the same records as <see cref="SchemaReader"/>.
    /// Result-set order: total count, objects, columns, indexes, foreign keys.
    /// </summary>
    internal static Task<(IReadOnlyList<SchemaObjectReport> Objects, int Omitted, OutputFootprint Raw)> ReadAsync(
        ISqlReader reader,
        CancellationToken ct) =>
        SchemaReader.ReadAsync(reader, ct);
}
