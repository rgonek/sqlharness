using System.Data;
using System.Globalization;

namespace SqlHarness.Core;

/// <summary>
/// Fixed read-only DMV batch for database file, allocation, top-table, and optional per-index space.
/// Result sets (in order): object match count, files, allocation, top tables, indexes (or empty shape).
/// </summary>
internal static class SpaceQuery
{
    internal const string MissingOrAmbiguousMessage = "Space object was not found or was ambiguous.";

    internal sealed record CollectedSpaceReport(
        IReadOnlyList<DatabaseFileSpaceReport> Files,
        DatabaseAllocationReport Allocation,
        IReadOnlyList<TableSpaceReport> Tables,
        IReadOnlyList<IndexSpaceReport> Indexes,
        OutputFootprint Raw);

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

    /// <summary>
    /// Reads the five-result space batch. <paramref name="reader"/> must be positioned on the match-count set.
    /// Object mode requires exactly one match; non-object mode requires zero matches.
    /// </summary>
    internal static async Task<CollectedSpaceReport> ReadAsync(
        ISqlReader reader,
        bool objectRequested,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        using var raw = new CanonicalResultAccumulator();

        long? matchCount = null;
        await ReadSet(reader, raw, row =>
        {
            if (matchCount is not null)
                throw new InvalidOperationException("Space object match count result set returned extra rows.");
            matchCount = Convert.ToInt64(row[0], CultureInfo.InvariantCulture);
        }, ct);

        if (matchCount is null)
            throw new InvalidOperationException("Space object match count result set is empty.");

        if (objectRequested)
        {
            if (matchCount.Value != 1)
                throw new SqlHarnessSafetyException(MissingOrAmbiguousMessage);
        }
        else if (matchCount.Value != 0)
        {
            throw new InvalidOperationException(
                "Space object match count must be zero when no object was requested.");
        }

        if (!await reader.NextResultAsync(ct))
            throw new InvalidOperationException("Space files result set is missing.");

        var files = new List<DatabaseFileSpaceReport>();
        await ReadSet(reader, raw, row =>
        {
            if (row.Length < 6)
                throw new InvalidOperationException("Space files result set has unexpected columns.");
            files.Add(new DatabaseFileSpaceReport(
                Text(row[0]),
                Text(row[1]),
                NullText(row[2]),
                Decimal(row[3]),
                Decimal(row[4]),
                Decimal(row[5])));
        }, ct);

        if (!await reader.NextResultAsync(ct))
            throw new InvalidOperationException("Space allocation result set is missing.");

        DatabaseAllocationReport? allocation = null;
        await ReadSet(reader, raw, row =>
        {
            if (allocation is not null)
                throw new InvalidOperationException("Space allocation result set returned extra rows.");
            if (row.Length < 3)
                throw new InvalidOperationException("Space allocation result set has unexpected columns.");
            allocation = new DatabaseAllocationReport(Decimal(row[0]), Decimal(row[1]), Decimal(row[2]));
        }, ct);

        if (allocation is null)
            throw new InvalidOperationException("Space allocation result set is empty.");

        if (!await reader.NextResultAsync(ct))
            throw new InvalidOperationException("Space tables result set is missing.");

        var tables = new List<TableSpaceReport>();
        await ReadSet(reader, raw, row =>
        {
            if (row.Length < 6)
                throw new InvalidOperationException("Space tables result set has unexpected columns.");
            tables.Add(new TableSpaceReport(
                Text(row[0]),
                Text(row[1]),
                Convert.ToInt64(row[2], CultureInfo.InvariantCulture),
                Decimal(row[3]),
                Decimal(row[4]),
                Decimal(row[5])));
        }, ct);

        if (!await reader.NextResultAsync(ct))
            throw new InvalidOperationException("Space indexes result set is missing.");

        var indexes = new List<IndexSpaceReport>();
        await ReadSet(reader, raw, row =>
        {
            if (row.Length < 8)
                throw new InvalidOperationException("Space indexes result set has unexpected columns.");
            indexes.Add(new IndexSpaceReport(
                Text(row[0]),
                Text(row[1]),
                Text(row[2]),
                Text(row[3]),
                Decimal(row[4]),
                Decimal(row[5]),
                Decimal(row[6]),
                NullText(row[7])));
        }, ct);

        while (await reader.NextResultAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
            }
        }

        return new CollectedSpaceReport(files, allocation, tables, indexes, raw.Complete().Footprint);
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

    private static string Text(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string? NullText(object? value) =>
        value is null or DBNull ? null : Text(value);

    private static decimal Decimal(object? value) =>
        Convert.ToDecimal(value, CultureInfo.InvariantCulture);
}