using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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

    /// <summary>
    /// Reads the six index-analysis result sets.
    /// <paramref name="reader"/> must be positioned on the observation set.
    /// Filter text stays on <see cref="SensitiveExistingIndex"/> and in the raw footprint.
    /// </summary>
    internal static async Task<CollectedIndexAnalysis> ReadAsync(
        ISqlReader reader,
        bool objectRequested,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        using var raw = new CanonicalResultAccumulator();

        var observations = new List<object?[]>();
        await ReadSet(reader, raw, ObservationColumnCount, observations.Add, ct);
        if (observations.Count != 1)
            throw Malformed();

        var observation = observations[0];
        var since = RequiredTimestamp(observation[0]);
        var observedAt = RequiredTimestamp(observation[1]);
        var matchCount = RequiredInt64(observation[2]);
        if (objectRequested)
        {
            if (matchCount != 1)
                throw new SqlHarnessSafetyException(ObjectNotFoundMessage);
        }
        else if (matchCount < 0)
        {
            throw Malformed();
        }

        if (!await reader.NextResultAsync(ct))
            throw Malformed();

        var candidates = new List<RawCandidate>();
        await ReadSet(reader, raw, CandidateColumnCount, row => candidates.Add(ReadCandidate(row)), ct);

        if (!await reader.NextResultAsync(ct))
            throw Malformed();

        var catalog = new Dictionary<(string Schema, string Table), List<string>>(TableKeyComparer.Instance);
        await ReadSet(reader, raw, CatalogColumnCount, row => AddCatalog(catalog, row), ct);

        if (!await reader.NextResultAsync(ct))
            throw Malformed();

        var indexes = new List<IndexBuilder>();
        var indexLookup = new Dictionary<IndexKey, IndexBuilder>(IndexKeyComparer.Instance);
        await ReadSet(reader, raw, IndexHeaderColumnCount, row => AddIndex(indexes, indexLookup, row), ct);

        if (!await reader.NextResultAsync(ct))
            throw Malformed();

        await ReadSet(reader, raw, IndexColumnColumnCount, row => AddIndexColumn(indexLookup, row), ct);

        if (!await reader.NextResultAsync(ct))
            throw Malformed();

        await ReadSet(reader, raw, CompressionColumnCount, row => AddCompression(indexLookup, row), ct);

        while (await reader.NextResultAsync(ct))
        {
            if (await reader.ReadAsync(ct))
                throw Malformed();
        }

        var resolved = new List<IndexCandidate>(candidates.Count);
        foreach (var candidate in candidates)
            resolved.Add(ResolveCandidate(candidate, catalog));

        var existing = new List<ExistingIndex>(indexes.Count);
        var sensitive = new List<SensitiveExistingIndex>(indexes.Count);
        foreach (var index in indexes)
        {
            existing.Add(index.ToExisting());
            sensitive.Add(index.ToSensitive());
        }

        return new CollectedIndexAnalysis(
            resolved,
            existing,
            sensitive,
            since,
            observedAt,
            raw.Complete().Footprint);
    }

    private static async Task ReadSet(
        ISqlReader reader,
        CanonicalResultAccumulator raw,
        int minimumColumns,
        Action<object?[]> add,
        CancellationToken ct)
    {
        if (reader.FieldCount < minimumColumns)
            throw Malformed();

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
            if (row.Length < minimumColumns)
                throw Malformed();

            add(row);
        }

        raw.EndResultSet();
    }

    private static RawCandidate ReadCandidate(object?[] row) =>
        new(
            RequiredInt64(row[0]),
            RequiredText(row[1]),
            RequiredText(row[2]),
            OptionalText(row[3]),
            OptionalText(row[4]),
            OptionalText(row[5]),
            RequiredInt64(row[6]),
            RequiredInt64(row[7]),
            RequiredDecimal(row[8]),
            RequiredDecimal(row[9]),
            // The batch already returns this score in column 10. Do not recompute it.
            RequiredDecimal(row[10]),
            OptionalTimestamp(row[11]),
            OptionalTimestamp(row[12]));

    private static void AddCatalog(
        Dictionary<(string Schema, string Table), List<string>> catalog,
        object?[] row)
    {
        var key = (RequiredText(row[0]), RequiredText(row[1]));
        if (!catalog.TryGetValue(key, out var columns))
        {
            columns = [];
            catalog.Add(key, columns);
        }

        columns.Add(RequiredText(row[2]));
    }

    private static IndexCandidate ResolveCandidate(
        RawCandidate candidate,
        Dictionary<(string Schema, string Table), List<string>> catalog)
    {
        if (!catalog.TryGetValue((candidate.Schema, candidate.Table), out var columns))
            columns = [];

        return new IndexCandidate(
            candidate.CandidateId,
            candidate.Schema,
            candidate.Table,
            BracketedIdentifierListParser.ParseAndResolve(candidate.EqualityColumns, columns),
            BracketedIdentifierListParser.ParseAndResolve(candidate.InequalityColumns, columns),
            BracketedIdentifierListParser.ParseAndResolve(candidate.IncludeColumns, columns),
            candidate.UserSeeks,
            candidate.UserScans,
            candidate.AverageTotalUserCost,
            candidate.AverageUserImpactPercent,
            candidate.CumulativeImpactScore,
            candidate.LastUserSeek,
            candidate.LastUserScan);
    }

    private static void AddIndex(
        List<IndexBuilder> indexes,
        Dictionary<IndexKey, IndexBuilder> lookup,
        object?[] row)
    {
        var builder = new IndexBuilder(
            RequiredText(row[0]),
            RequiredText(row[1]),
            RequiredInt32(row[2]),
            IndexName(row[3]),
            RequiredText(row[4]),
            RequiredBool(row[5]),
            RequiredBool(row[6]),
            RequiredBool(row[7]),
            RequiredBool(row[8]),
            OptionalText(row[9]));
        if (!lookup.TryAdd(new IndexKey(builder.Schema, builder.Table, builder.IndexId), builder))
            throw Malformed();

        indexes.Add(builder);
    }

    private static void AddIndexColumn(Dictionary<IndexKey, IndexBuilder> lookup, object?[] row)
    {
        var key = new IndexKey(RequiredText(row[0]), RequiredText(row[1]), RequiredInt32(row[2]));
        if (!lookup.TryGetValue(key, out var builder))
            throw Malformed();

        var piece = new IndexColumnPiece(
            RequiredText(row[3]),
            RequiredInt32(row[4]),
            RequiredBool(row[6]),
            RequiredInt32(row[7]));
        if (RequiredBool(row[5]))
            builder.Includes.Add(piece);
        else
            builder.Keys.Add(piece);
    }

    private static void AddCompression(Dictionary<IndexKey, IndexBuilder> lookup, object?[] row)
    {
        var key = new IndexKey(RequiredText(row[0]), RequiredText(row[1]), RequiredInt32(row[2]));
        if (!lookup.TryGetValue(key, out var builder))
            throw Malformed();

        builder.CompressionDescriptions.Add(RequiredText(row[3]));
    }

    private static long RequiredInt64(object? value) =>
        RequiredNumber(value, static number => Convert.ToInt64(number, CultureInfo.InvariantCulture));

    private static int RequiredInt32(object? value) =>
        RequiredNumber(value, static number => Convert.ToInt32(number, CultureInfo.InvariantCulture));

    private static decimal RequiredDecimal(object? value) =>
        RequiredNumber(value, static number => Convert.ToDecimal(number, CultureInfo.InvariantCulture));

    private static bool RequiredBool(object? value) =>
        RequiredNumber(value, static number => Convert.ToBoolean(number, CultureInfo.InvariantCulture));

    private static T RequiredNumber<T>(object? value, Func<object, T> convert)
    {
        if (value is null or DBNull)
            throw Malformed();

        try
        {
            return convert(value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw Malformed();
        }
    }

    private static string RequiredText(object? value)
    {
        if (value is null or DBNull)
            throw Malformed();

        return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture)
            ?? throw Malformed();
    }

    private static string? OptionalText(object? value)
    {
        if (value is null or DBNull)
            return null;

        return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string IndexName(object? value)
    {
        if (value is null or DBNull)
            return "";

        return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private static DateTimeOffset? OptionalTimestamp(object? value) =>
        value is null or DBNull ? null : RequiredTimestamp(value);

    private static DateTimeOffset RequiredTimestamp(object? value)
    {
        if (value is null or DBNull)
            throw Malformed();

        return value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime(),
            // Unspecified and Utc clock times are already UTC. Local is converted.
            DateTime timestamp when timestamp.Kind == DateTimeKind.Local => new DateTimeOffset(timestamp).ToUniversalTime(),
            DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            _ => throw Malformed(),
        };
    }

    private static string HashFilter(string filter) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(filter)));

    private static InvalidOperationException Malformed() => new(MalformedMessage);

    private const int ObservationColumnCount = 3;
    private const int CandidateColumnCount = 13;
    private const int CatalogColumnCount = 3;
    private const int IndexHeaderColumnCount = 10;
    private const int IndexColumnColumnCount = 8;
    private const int CompressionColumnCount = 4;
    private const string MalformedMessage = "Index analysis result is malformed.";
    private const string ObjectNotFoundMessage = "indexes object was not found or was ambiguous.";
    private const string MixedCompression = "MIXED";

    private sealed record RawCandidate(
        long CandidateId,
        string Schema,
        string Table,
        string? EqualityColumns,
        string? InequalityColumns,
        string? IncludeColumns,
        long UserSeeks,
        long UserScans,
        decimal AverageTotalUserCost,
        decimal AverageUserImpactPercent,
        decimal CumulativeImpactScore,
        DateTimeOffset? LastUserSeek,
        DateTimeOffset? LastUserScan);

    private readonly record struct IndexColumnPiece(
        string Name,
        int KeyOrdinal,
        bool Descending,
        int IndexColumnId);

    private readonly record struct IndexKey(string Schema, string Table, int IndexId);

    private sealed class TableKeyComparer : IEqualityComparer<(string Schema, string Table)>
    {
        public static TableKeyComparer Instance { get; } = new();

        public bool Equals((string Schema, string Table) x, (string Schema, string Table) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Schema, y.Schema)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Table, y.Table);

        public int GetHashCode((string Schema, string Table) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Schema),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table));
    }

    private sealed class IndexKeyComparer : IEqualityComparer<IndexKey>
    {
        public static IndexKeyComparer Instance { get; } = new();

        public bool Equals(IndexKey x, IndexKey y) =>
            x.IndexId == y.IndexId
            && StringComparer.OrdinalIgnoreCase.Equals(x.Schema, y.Schema)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Table, y.Table);

        public int GetHashCode(IndexKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Schema),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table),
                obj.IndexId);
    }

    private sealed class IndexBuilder(
        string schema,
        string table,
        int indexId,
        string name,
        string type,
        bool unique,
        bool primaryKey,
        bool uniqueConstraint,
        bool disabled,
        string? filterDefinition)
    {
        public string Schema => schema;
        public string Table => table;
        public int IndexId => indexId;
        public string Name => name;
        public string Type => type;
        public bool Unique => unique;
        public bool PrimaryKey => primaryKey;
        public bool UniqueConstraint => uniqueConstraint;
        public bool Disabled => disabled;
        public string? FilterDefinition => filterDefinition;
        public List<IndexColumnPiece> Keys { get; } = [];
        public List<IndexColumnPiece> Includes { get; } = [];
        public List<string> CompressionDescriptions { get; } = [];

        public ExistingIndex ToExisting()
        {
            var keys = Keys
                .OrderBy(static column => column.KeyOrdinal)
                .ThenBy(static column => column.IndexColumnId)
                .ToArray();
            var includes = Includes
                .OrderBy(static column => column.IndexColumnId)
                .Select(static column => column.Name)
                .ToArray();
            var filter = FilterDefinition;
            string? filterHash = null;
            if (filter is not null)
                filterHash = HashFilter(filter);

            return new ExistingIndex(
                Schema,
                Table,
                IndexId,
                Name,
                Type,
                keys.Select(static column => column.Name).ToArray(),
                keys.Select(static column => column.Descending).ToArray(),
                includes,
                Unique,
                PrimaryKey,
                UniqueConstraint,
                Disabled,
                filter is not null,
                filterHash,
                CollapseCompression());
        }

        public SensitiveExistingIndex ToSensitive() =>
            new(Schema, Table, IndexId, Name, FilterDefinition);

        private string CollapseCompression()
        {
            if (CompressionDescriptions.Count == 0)
                return "";

            var first = CompressionDescriptions[0];
            for (var index = 1; index < CompressionDescriptions.Count; index++)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(first, CompressionDescriptions[index]))
                    return MixedCompression;
            }

            return first;
        }
    }
}

internal sealed record IndexCandidate(
    long CandidateId, string Schema, string Table,
    IReadOnlyList<string> EqualityColumns,
    IReadOnlyList<string> InequalityColumns,
    IReadOnlyList<string> IncludeColumns,
    long UserSeeks, long UserScans,
    decimal AverageTotalUserCost, decimal AverageUserImpactPercent,
    decimal CumulativeImpactScore,
    DateTimeOffset? LastUserSeek, DateTimeOffset? LastUserScan);

internal sealed record ExistingIndex(
    string Schema, string Table, int IndexId, string Name, string Type,
    IReadOnlyList<string> KeyColumns, IReadOnlyList<bool> KeyDescending,
    IReadOnlyList<string> IncludeColumns,
    bool Unique, bool PrimaryKey, bool UniqueConstraint,
    bool Disabled, bool HasFilter, string? FilterHash, string Compression);

/// <summary>Exact filter text. It is not a field of <see cref="ExistingIndex"/>.</summary>
internal sealed record SensitiveExistingIndex(
    string Schema, string Table, int IndexId, string Name,
    string? FilterDefinition);

internal sealed record CollectedIndexAnalysis(
    IReadOnlyList<IndexCandidate> Candidates,
    IReadOnlyList<ExistingIndex> ExistingIndexes,
    IReadOnlyList<SensitiveExistingIndex> SensitiveIndexes,
    DateTimeOffset ObservationSince,
    DateTimeOffset ObservedAt,
    OutputFootprint RawFootprint);