using System.Data;
using System.Globalization;

namespace SqlHarness.Core;

public sealed record SchemaColumn(string Name, string Type, bool Nullable, bool InPrimaryKey);
public sealed record SchemaIndex(string Name, bool Unique, IReadOnlyList<string> Keys, IReadOnlyList<string> Includes, string? Filter);
public sealed record SchemaForeignKey(string Name, string Columns, string ReferencedTable, string ReferencedColumns);
public sealed record SchemaObjectReport(string Schema, string Name, string Kind, IReadOnlyList<SchemaColumn> Columns, IReadOnlyList<SchemaIndex> Indexes, IReadOnlyList<SchemaForeignKey> ForeignKeys);
public sealed record SqlHarnessSchemaReport(SqlHarnessTargetIdentityReport Target, IReadOnlyList<SchemaObjectReport> Objects, int OmittedObjects);

internal static class SchemaReader
{
    internal const string Sql = """
SELECT COUNT_BIG(*) AS TotalObjects
FROM sys.objects o
WHERE o.type IN ('U','V') AND (@filter IS NULL OR o.name LIKE @filter);

DECLARE @objects TABLE (object_id int PRIMARY KEY, SchemaName sysname, ObjectName sysname, Kind nvarchar(5));
INSERT @objects
SELECT TOP (@maxObjects) o.object_id, s.name, o.name, CASE o.type WHEN 'U' THEN 'TABLE' ELSE 'VIEW' END
FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE o.type IN ('U','V') AND (@filter IS NULL OR o.name LIKE @filter)
ORDER BY s.name, o.name, o.object_id;

SELECT SchemaName,ObjectName,Kind FROM @objects ORDER BY SchemaName,ObjectName,object_id;
SELECT x.SchemaName,x.ObjectName,c.name,t.name + CASE WHEN t.name IN ('varchar','char','varbinary','binary','nvarchar','nchar') THEN '(' + CASE WHEN c.max_length=-1 THEN 'max' WHEN t.name IN ('nvarchar','nchar') THEN CONVERT(varchar(10),c.max_length/2) ELSE CONVERT(varchar(10),c.max_length) END + ')' WHEN t.name IN ('decimal','numeric') THEN '('+CONVERT(varchar(10),c.precision)+','+CONVERT(varchar(10),c.scale)+')' ELSE '' END,c.is_nullable,CONVERT(bit,CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END)
FROM @objects x JOIN sys.columns c ON c.object_id=x.object_id JOIN sys.types t ON t.user_type_id=c.user_type_id
OUTER APPLY (SELECT TOP(1) ic.column_id FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id WHERE i.object_id=x.object_id AND i.is_primary_key=1 AND ic.column_id=c.column_id) pk
ORDER BY x.SchemaName,x.ObjectName,c.column_id;
SELECT x.SchemaName,x.ObjectName,i.name,i.is_unique,c.name,ic.is_included_column,ic.key_ordinal,ic.index_column_id,i.filter_definition
FROM @objects x JOIN sys.indexes i ON i.object_id=x.object_id JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE i.name IS NOT NULL ORDER BY x.SchemaName,x.ObjectName,i.name,ic.is_included_column,ic.key_ordinal,ic.index_column_id;
SELECT x.SchemaName,x.ObjectName,f.name,pc.name,rs.name,ro.name,rc.name,fc.constraint_column_id
FROM @objects x JOIN sys.foreign_keys f ON f.parent_object_id=x.object_id JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=f.object_id JOIN sys.columns pc ON pc.object_id=fc.parent_object_id AND pc.column_id=fc.parent_column_id JOIN sys.objects ro ON ro.object_id=fc.referenced_object_id JOIN sys.schemas rs ON rs.schema_id=ro.schema_id JOIN sys.columns rc ON rc.object_id=fc.referenced_object_id AND rc.column_id=fc.referenced_column_id
ORDER BY x.SchemaName,x.ObjectName,f.name,fc.constraint_column_id;
""";

    internal static IReadOnlyList<SqlHarnessParameter> Parameters(string? filter, int maxObjects) =>
    [
        new("@filter", SqlDbType.NVarChar, filter is null ? DBNull.Value : filter, 4000),
        new("@maxObjects", SqlDbType.Int, maxObjects, null),
    ];

    internal static async Task<(IReadOnlyList<SchemaObjectReport> Objects, int Omitted, OutputFootprint Raw)> ReadAsync(ISqlReader reader, CancellationToken ct)
    {
        using var raw = new CanonicalResultAccumulator();
        long? total = null;
        await ReadSet(reader, raw, row => total = Convert.ToInt64(row[0], CultureInfo.InvariantCulture), ct);
        if (total is null) throw new InvalidOperationException("Schema object count result set is empty.");
        if (!await reader.NextResultAsync(ct)) throw new InvalidOperationException("Schema objects result set is missing.");
        var objects = new List<ObjectBuilder>();
        await ReadSet(reader, raw, row => objects.Add(new(Text(row[0]), Text(row[1]), Text(row[2]))), ct);
        if (!await reader.NextResultAsync(ct)) throw new InvalidOperationException("Schema columns result set is missing.");
        await ReadSet(reader, raw, row => Find(objects, row).Columns.Add(new(Text(row[2]), Text(row[3]), Bool(row[4]), Bool(row[5]))), ct);
        if (!await reader.NextResultAsync(ct)) throw new InvalidOperationException("Schema indexes result set is missing.");
        await ReadSet(reader, raw, row => Find(objects, row).AddIndexColumn(row), ct);
        if (!await reader.NextResultAsync(ct)) throw new InvalidOperationException("Schema foreign keys result set is missing.");
        await ReadSet(reader, raw, row => Find(objects, row).AddForeignKeyColumn(row), ct);
        while (await reader.NextResultAsync(ct)) while (await reader.ReadAsync(ct)) { }
        var omitted = checked((int)(total.Value - objects.Count));
        if (omitted < 0) throw new InvalidOperationException("Schema object count is smaller than the selected object count.");
        return (objects.Select(x => x.Build()).ToArray(), omitted, raw.Complete().Footprint);
    }

    private static async Task ReadSet(ISqlReader reader, CanonicalResultAccumulator raw, Action<object?[]> add, CancellationToken ct)
    {
        var columns = Enumerable.Range(0, reader.FieldCount).Select(i => new CanonicalColumn(i, reader.GetName(i), reader.GetFieldType(i).FullName ?? "object", reader.GetAllowNull(i))).ToArray();
        raw.BeginResultSet(columns);
        while (await reader.ReadAsync(ct))
        {
            var row = Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i) is DBNull ? null : reader.GetValue(i)).ToArray();
            raw.AddRow(row); add(row);
        }
        raw.EndResultSet();
    }

    private static ObjectBuilder Find(List<ObjectBuilder> objects, object?[] row) => objects.Single(x => x.Schema == Text(row[0]) && x.Name == Text(row[1]));
    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    private static string? NullText(object? value) => value is null or DBNull ? null : Text(value);
    private static bool Bool(object? value) => Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    private static int Int(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private sealed class ObjectBuilder(string schema, string name, string kind)
    {
        private readonly Dictionary<string, IndexBuilder> _indexes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ForeignKeyBuilder> _foreignKeys = new(StringComparer.Ordinal);
        public string Schema => schema; public string Name => name; public List<SchemaColumn> Columns { get; } = [];
        public void AddIndexColumn(object?[] row)
        {
            var indexName = Text(row[2]);
            if (!_indexes.TryGetValue(indexName, out var index)) _indexes.Add(indexName, index = new(indexName, Bool(row[3]), NullText(row[8])));
            index.Add(Text(row[4]), Bool(row[5]), Int(row[6]), Int(row[7]));
        }
        public void AddForeignKeyColumn(object?[] row)
        {
            var foreignKeyName = Text(row[2]);
            if (!_foreignKeys.TryGetValue(foreignKeyName, out var fk)) _foreignKeys.Add(foreignKeyName, fk = new(foreignKeyName, Text(row[4]), Text(row[5])));
            fk.Add(Text(row[3]), Text(row[6]), Int(row[7]));
        }
        public SchemaObjectReport Build() => new(schema, name, kind, Columns,
            _indexes.Values.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Build()).ToArray(),
            _foreignKeys.Values.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Build()).ToArray());
    }
    private sealed class IndexBuilder(string name, bool unique, string? filter)
    {
        private readonly List<(int Ordinal, string Name)> _keys = [], _includes = [];
        public string Name => name;
        public void Add(string column, bool included, int keyOrdinal, int indexColumnOrdinal) => (included ? _includes : _keys).Add((included ? indexColumnOrdinal : keyOrdinal, column));
        public SchemaIndex Build() => new(name, unique, _keys.OrderBy(x => x.Ordinal).Select(x => x.Name).ToArray(), _includes.OrderBy(x => x.Ordinal).Select(x => x.Name).ToArray(), filter);
    }
    private sealed class ForeignKeyBuilder(string name, string referencedSchema, string referencedObject)
    {
        private readonly List<(int Ordinal, string Parent, string Referenced)> _columns = [];
        public string Name => name;
        public void Add(string parent, string referenced, int ordinal) => _columns.Add((ordinal, parent, referenced));
        public SchemaForeignKey Build() { var ordered = _columns.OrderBy(x => x.Ordinal).ToArray(); return new(name, string.Join(',', ordered.Select(x => x.Parent)), $"{referencedSchema}.{referencedObject}", string.Join(',', ordered.Select(x => x.Referenced))); }
    }
}