using System.Globalization;
using System.Text;

namespace SqlHarness.Core.Postgres;

internal static class PostgresCounts
{
    internal const string CatalogSql = """
SELECT CASE
    WHEN @tables IS NOT NULL THEN CAST(0 AS bigint)
    ELSE (
        SELECT COUNT(*)::bigint
        FROM pg_class c
        INNER JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p')
          AND n.nspname <> 'pg_catalog'
          AND n.nspname <> 'information_schema'
          AND n.nspname NOT LIKE 'pg_toast%'
          AND n.nspname NOT LIKE 'pg_temp%'
          AND (@like IS NULL OR c.relname LIKE @like)
    )
END AS "TotalObjects";

SELECT
    q."RequestedName",
    q."SchemaName",
    q."ObjectName",
    q."ObjectId",
    q."ApproxRows"
FROM (
    SELECT
        req.requested_name AS "RequestedName",
        n.nspname AS "SchemaName",
        c.relname AS "ObjectName",
        c.oid::int AS "ObjectId",
        CASE WHEN c.reltuples < 0 THEN CAST(0 AS bigint) ELSE c.reltuples::bigint END AS "ApproxRows",
        req.ord AS sort_ord,
        CAST(0 AS bigint) AS approx_sort
    FROM jsonb_array_elements_text(COALESCE((@tables)::jsonb, '[]'::jsonb))
         WITH ORDINALITY AS req(requested_name, ord)
    INNER JOIN pg_class c ON c.relkind IN ('r', 'p')
    INNER JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE @tables IS NOT NULL
      AND n.nspname <> 'pg_catalog'
      AND n.nspname <> 'information_schema'
      AND n.nspname NOT LIKE 'pg_toast%'
      AND n.nspname NOT LIKE 'pg_temp%'
      AND (
            (POSITION('.' IN req.requested_name) = 0 AND c.relname = req.requested_name)
            OR (
                POSITION('.' IN req.requested_name) > 0
                AND POSITION('.' IN SUBSTRING(req.requested_name FROM POSITION('.' IN req.requested_name) + 1)) = 0
                AND n.nspname = SPLIT_PART(req.requested_name, '.', 1)
                AND c.relname = SPLIT_PART(req.requested_name, '.', 2)
            )
      )

    UNION ALL

    SELECT
        CAST(NULL AS text) AS "RequestedName",
        n.nspname AS "SchemaName",
        c.relname AS "ObjectName",
        c.oid::int AS "ObjectId",
        CASE WHEN c.reltuples < 0 THEN CAST(0 AS bigint) ELSE c.reltuples::bigint END AS "ApproxRows",
        CAST(0 AS bigint) AS sort_ord,
        CASE WHEN c.reltuples < 0 THEN CAST(0 AS bigint) ELSE c.reltuples::bigint END AS approx_sort
    FROM pg_class c
    INNER JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE @tables IS NULL
      AND c.relkind IN ('r', 'p')
      AND n.nspname <> 'pg_catalog'
      AND n.nspname <> 'information_schema'
      AND n.nspname NOT LIKE 'pg_toast%'
      AND n.nspname NOT LIKE 'pg_temp%'
      AND (@like IS NULL OR c.relname LIKE @like)
) AS q
ORDER BY q.sort_ord, q.approx_sort DESC, q."SchemaName", q."ObjectName"
LIMIT CASE WHEN @tables IS NULL THEN @top ELSE NULL END;
""";

    internal static string BuildExactSql(IReadOnlyList<ResolvedCountObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var builder = new StringBuilder();
        for (var ordinal = 0; ordinal < objects.Count; ordinal++)
        {
            var item = objects[ordinal];
            if (ordinal > 0)
                builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture,
                $"SELECT CAST({ordinal} AS int) AS \"Ordinal\", COUNT(*) AS \"Rows\" FROM {QuoteIdent(item.Schema)}.{QuoteIdent(item.Name)};");
        }

        return builder.ToString();
    }

    internal static string QuoteIdent(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
