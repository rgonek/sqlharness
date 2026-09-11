using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlHarness.Core;

internal sealed record ResolvedCountObject(
    string RequestedName,
    string Schema,
    string Name,
    int ObjectId,
    long ApproxRows);

internal sealed record ResolvedCountSelection(
    IReadOnlyList<ResolvedCountObject> Objects,
    int Omitted);

internal static partial class CountsQuery
{
    private const string GenericMissingMessage = "Requested table was not found or was ambiguous.";

    // Safe for embedding in error text only: ASCII identifier, optional schema.table form.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeRequestedNamePattern();

    internal const string CatalogSql = """
IF @tables IS NOT NULL
BEGIN
    SELECT CAST(0 AS bigint) AS TotalObjects;

    SELECT
        j.value AS RequestedName,
        s.name AS SchemaName,
        t.name AS ObjectName,
        t.object_id AS ObjectId,
        ISNULL(SUM(p.row_count), CONVERT(bigint, 0)) AS ApproxRows
    FROM OPENJSON(@tables) j
    INNER JOIN sys.tables t
        ON t.name = PARSENAME(CONVERT(nvarchar(517), j.value), 1)
    INNER JOIN sys.schemas s
        ON s.schema_id = t.schema_id
       AND (
            PARSENAME(CONVERT(nvarchar(517), j.value), 2) IS NULL
            OR s.name = PARSENAME(CONVERT(nvarchar(517), j.value), 2)
       )
    LEFT JOIN sys.dm_db_partition_stats p
        ON p.object_id = t.object_id
       AND p.index_id IN (0,1)
    WHERE PARSENAME(CONVERT(nvarchar(517), j.value), 1) IS NOT NULL
      AND PARSENAME(CONVERT(nvarchar(517), j.value), 3) IS NULL
    GROUP BY j.value, j.[key], s.name, t.name, t.object_id
    ORDER BY CONVERT(int, j.[key]), s.name, t.name;
END
ELSE
BEGIN
    SELECT COUNT_BIG(*) AS TotalObjects
    FROM sys.tables t
    WHERE @like IS NULL OR t.name LIKE @like;

    SELECT TOP (@top)
        CONVERT(nvarchar(261), NULL) AS RequestedName,
        s.name AS SchemaName,
        t.name AS ObjectName,
        t.object_id AS ObjectId,
        ISNULL(SUM(p.row_count), CONVERT(bigint, 0)) AS ApproxRows
    FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    LEFT JOIN sys.dm_db_partition_stats p
        ON p.object_id = t.object_id
       AND p.index_id IN (0,1)
    WHERE @like IS NULL OR t.name LIKE @like
    GROUP BY s.name, t.name, t.object_id
    ORDER BY ApproxRows DESC, s.name, t.name;
END
""";

    internal static IReadOnlyList<SqlHarnessParameter> CatalogParameters(
        IReadOnlyList<string> tables,
        string? like,
        int top)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var normalized = NormalizeRequestedTables(tables);
        object tablesValue = normalized.Count == 0
            ? DBNull.Value
            : JsonSerializer.Serialize(normalized);

        return
        [
            new("@tables", SqlDbType.NVarChar, tablesValue, -1),
            new("@like", SqlDbType.NVarChar, like is null ? DBNull.Value : like, 4000),
            new("@top", SqlDbType.Int, top, null),
        ];
    }

    internal static async Task<ResolvedCountSelection> ReadCatalogAsync(
        ISqlReader reader,
        IReadOnlyList<string> requestedTables,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(requestedTables);

        var normalizedRequested = NormalizeRequestedTables(requestedTables);

        if (reader.FieldCount < 1)
            throw new InvalidOperationException("Counts catalog total result set is empty.");

        long? total = null;
        while (await reader.ReadAsync(ct))
        {
            if (total is not null)
                throw new InvalidOperationException("Counts catalog total result set returned extra rows.");
            total = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
        }

        if (total is null)
            throw new InvalidOperationException("Counts catalog total result set is empty.");

        if (!await reader.NextResultAsync(ct))
            throw new InvalidOperationException("Counts catalog objects result set is missing.");

        var rows = new List<ResolvedCountObject>();
        while (await reader.ReadAsync(ct))
        {
            if (reader.FieldCount < 5)
                throw new InvalidOperationException("Counts catalog objects result set has unexpected columns.");

            var requestedName = Text(reader.GetValue(0));
            var schema = Text(reader.GetValue(1));
            var name = Text(reader.GetValue(2));
            var objectId = Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
            var approxRows = Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture);
            rows.Add(new ResolvedCountObject(requestedName, schema, name, objectId, approxRows));
        }

        while (await reader.NextResultAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
            }
        }

        if (normalizedRequested.Count > 0)
            return ResolveExplicit(normalizedRequested, rows);

        var omitted = checked((int)(total.Value - rows.Count));
        if (omitted < 0)
            throw new InvalidOperationException("Counts catalog total is smaller than the selected object count.");

        return new ResolvedCountSelection(rows, omitted);
    }

    private static ResolvedCountSelection ResolveExplicit(
        IReadOnlyList<string> requested,
        List<ResolvedCountObject> rows)
    {
        var resolved = new List<ResolvedCountObject>(requested.Count);
        foreach (var name in requested)
        {
            var matches = rows
                .Where(r => string.Equals(r.RequestedName, name, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matches.Length != 1)
                throw new SqlHarnessSafetyException(MissingMessage(name));

            var match = matches[0];
            // Preserve the caller-facing requested spelling from the normalized input list.
            resolved.Add(match with { RequestedName = name });
        }

        return new ResolvedCountSelection(resolved, Omitted: 0);
    }

    internal static IReadOnlyList<string> NormalizeRequestedTables(IReadOnlyList<string> tables)
    {
        var result = new List<string>(tables.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            if (table is null)
                throw new SqlHarnessSafetyException(GenericMissingMessage);
            if (seen.Add(table))
                result.Add(table);
        }

        return result;
    }

    internal static Task<IReadOnlyList<long>> ExecuteExactAsync(
        ISqlSession session,
        IReadOnlyList<ResolvedCountObject> objects,
        int timeoutSeconds,
        CancellationToken ct) =>
        ExecuteExactAsync(session, objects, timeoutSeconds, ct, BuildExactSql);

    internal static async Task<IReadOnlyList<long>> ExecuteExactAsync(
        ISqlSession session,
        IReadOnlyList<ResolvedCountObject> objects,
        int timeoutSeconds,
        CancellationToken ct,
        Func<IReadOnlyList<ResolvedCountObject>, string> buildExactSql)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(buildExactSql);

        if (objects.Count == 0)
            return Array.Empty<long>();

        var sql = buildExactSql(objects);
        await using var reader = await session.ExecuteReaderAsync(
            new SqlExecutionCommand(sql, [], timeoutSeconds),
            ct);

        var counts = new long[objects.Count];
        var seen = new bool[objects.Count];

        await ReadExactRowAsync(reader, counts, seen, ct);
        for (var i = 1; i < objects.Count; i++)
        {
            if (!await reader.NextResultAsync(ct))
                throw new InvalidOperationException("Exact counts result set is missing.");
            await ReadExactRowAsync(reader, counts, seen, ct);
        }

        while (await reader.NextResultAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
            }
        }

        for (var ordinal = 0; ordinal < seen.Length; ordinal++)
        {
            if (!seen[ordinal])
                throw new InvalidOperationException($"Exact count missing for ordinal {ordinal}.");
        }

        return counts;
    }

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
                $"SELECT CAST({ordinal} AS int) AS Ordinal, COUNT_BIG(*) AS Rows FROM {Quote(item.Schema)}.{Quote(item.Name)};");
        }

        return builder.ToString();
    }

    internal static string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    private static async Task ReadExactRowAsync(
        ISqlReader reader,
        long[] counts,
        bool[] seen,
        CancellationToken ct)
    {
        if (reader.FieldCount < 2)
            throw new InvalidOperationException("Exact counts result set has unexpected columns.");

        int? ordinal = null;
        long? rows = null;
        while (await reader.ReadAsync(ct))
        {
            if (ordinal is not null)
                throw new InvalidOperationException("Exact counts result set returned extra rows.");
            ordinal = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            rows = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        if (ordinal is null || rows is null)
            throw new InvalidOperationException("Exact counts result set is empty.");
        if (ordinal.Value < 0 || ordinal.Value >= counts.Length)
            throw new InvalidOperationException("Exact counts ordinal is out of range.");
        if (seen[ordinal.Value])
            throw new InvalidOperationException("Exact counts ordinal was returned more than once.");

        counts[ordinal.Value] = rows.Value;
        seen[ordinal.Value] = true;
    }

    private static string MissingMessage(string requestedName) =>
        IsSafeRequestedName(requestedName)
            ? $"Requested table '{requestedName}' was not found or was ambiguous."
            : GenericMissingMessage;

    private static bool IsSafeRequestedName(string name) =>
        SafeRequestedNamePattern().IsMatch(name);

    private static string Text(object? value) =>
        value is null or DBNull
            ? string.Empty
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}