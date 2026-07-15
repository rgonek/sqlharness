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
DECLARE @objects TABLE (object_id int PRIMARY KEY, SchemaName sysname, ObjectName sysname, Kind nvarchar(5));
INSERT @objects
SELECT TOP (501) o.object_id, s.name, o.name, CASE o.type WHEN 'U' THEN 'TABLE' ELSE 'VIEW' END
FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE o.type IN ('U','V') AND (@filter IS NULL OR o.name LIKE @filter)
ORDER BY s.name, o.name;
SELECT SchemaName,ObjectName,Kind FROM @objects ORDER BY SchemaName,ObjectName;
SELECT x.SchemaName,x.ObjectName,c.name,t.name + CASE WHEN t.name IN ('varchar','char','varbinary','binary','nvarchar','nchar') THEN '(' + CASE WHEN c.max_length=-1 THEN 'max' WHEN t.name IN ('nvarchar','nchar') THEN CONVERT(varchar(10),c.max_length/2) ELSE CONVERT(varchar(10),c.max_length) END + ')' WHEN t.name IN ('decimal','numeric') THEN '('+CONVERT(varchar(10),c.precision)+','+CONVERT(varchar(10),c.scale)+')' ELSE '' END,c.is_nullable,CONVERT(bit,CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END)
FROM @objects x JOIN sys.columns c ON c.object_id=x.object_id JOIN sys.types t ON t.user_type_id=c.user_type_id
OUTER APPLY (SELECT TOP(1) ic.column_id FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id WHERE i.object_id=x.object_id AND i.is_primary_key=1 AND ic.column_id=c.column_id) pk
ORDER BY x.SchemaName,x.ObjectName,c.column_id;
SELECT x.SchemaName,x.ObjectName,i.name,i.is_unique,
STRING_AGG(CASE WHEN ic.is_included_column=0 THEN QUOTENAME(c.name) END,',') WITHIN GROUP (ORDER BY ic.key_ordinal),
STRING_AGG(CASE WHEN ic.is_included_column=1 THEN QUOTENAME(c.name) END,',') WITHIN GROUP (ORDER BY ic.index_column_id),i.filter_definition
FROM @objects x JOIN sys.indexes i ON i.object_id=x.object_id JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE i.name IS NOT NULL GROUP BY x.SchemaName,x.ObjectName,i.name,i.is_unique,i.filter_definition ORDER BY x.SchemaName,x.ObjectName,i.name;
SELECT x.SchemaName,x.ObjectName,f.name,
STRING_AGG(QUOTENAME(pc.name),',') WITHIN GROUP (ORDER BY fc.constraint_column_id),
QUOTENAME(rs.name)+'.'+QUOTENAME(ro.name),STRING_AGG(QUOTENAME(rc.name),',') WITHIN GROUP (ORDER BY fc.constraint_column_id)
FROM @objects x JOIN sys.foreign_keys f ON f.parent_object_id=x.object_id JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=f.object_id JOIN sys.columns pc ON pc.object_id=fc.parent_object_id AND pc.column_id=fc.parent_column_id JOIN sys.objects ro ON ro.object_id=fc.referenced_object_id JOIN sys.schemas rs ON rs.schema_id=ro.schema_id JOIN sys.columns rc ON rc.object_id=fc.referenced_object_id AND rc.column_id=fc.referenced_column_id
GROUP BY x.SchemaName,x.ObjectName,f.name,rs.name,ro.name ORDER BY x.SchemaName,x.ObjectName,f.name;
""";

    internal static IReadOnlyList<SqlHarnessParameter> Parameters(string? filter) =>
        [new("@filter", SqlDbType.NVarChar, filter is null ? DBNull.Value : filter, 4000)];

    internal static async Task<(IReadOnlyList<SchemaObjectReport> Objects, int Omitted, OutputFootprint Raw)> ReadAsync(ISqlReader reader, int max, CancellationToken ct)
    {
        using var raw = new CanonicalResultAccumulator();
        var objects = new List<Builder>();
        await ReadSet(reader, raw, row => objects.Add(new Builder(Text(row[0]), Text(row[1]), Text(row[2]))), ct);
        if (!await reader.NextResultAsync(ct)) throw new InvalidOperationException("Schema columns result set is missing.");
        await ReadSet(reader, raw, row => Find(objects, row).Columns.Add(new(Text(row[2]), Text(row[3]), Bool(row[4]), Bool(row[5]))), ct);
        if (!await reader.NextResultAsync(ct)) throw new InvalidOperationException("Schema indexes result set is missing.");
        await ReadSet(reader, raw, row => Find(objects, row).Indexes.Add(new(Text(row[2]), Bool(row[3]), List(row[4]), List(row[5]), NullText(row[6]))), ct);
        if (!await reader.NextResultAsync(ct)) throw new InvalidOperationException("Schema foreign keys result set is missing.");
        await ReadSet(reader, raw, row => Find(objects, row).ForeignKeys.Add(new(Text(row[2]), Text(row[3]), Text(row[4]), Text(row[5]))), ct);
        while (await reader.NextResultAsync(ct)) while (await reader.ReadAsync(ct)) { }
        var kept = objects.Take(max).Select(x => x.Build()).ToArray();
        return (kept, Math.Max(objects.Count - max, 0), raw.Complete().Footprint);
    }
    private static async Task ReadSet(ISqlReader reader, CanonicalResultAccumulator raw, Action<object?[]> add, CancellationToken ct)
    {
        var cols = Enumerable.Range(0, reader.FieldCount).Select(i => new CanonicalColumn(i, reader.GetName(i), reader.GetFieldType(i).FullName ?? "object", reader.GetAllowNull(i))).ToArray(); raw.BeginResultSet(cols);
        while (await reader.ReadAsync(ct)) { var row = Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i) is DBNull ? null : reader.GetValue(i)).ToArray(); raw.AddRow(row); add(row); }
        raw.EndResultSet();
    }
    private static Builder Find(List<Builder> xs, object?[] r) => xs.Single(x => x.Schema == Text(r[0]) && x.Name == Text(r[1]));
    private static string Text(object? x) => Convert.ToString(x, CultureInfo.InvariantCulture) ?? ""; private static string? NullText(object? x) => x is null or DBNull ? null : Text(x); private static bool Bool(object? x) => Convert.ToBoolean(x, CultureInfo.InvariantCulture);
    private static IReadOnlyList<string> List(object? x) => string.IsNullOrWhiteSpace(NullText(x)) ? [] : Text(x).Split(',').Select(s => s.Trim().Trim('[', ']')).ToArray();
    private sealed class Builder(string schema, string name, string kind) { public string Schema => schema; public string Name => name; public List<SchemaColumn> Columns { get; } = []; public List<SchemaIndex> Indexes { get; } = []; public List<SchemaForeignKey> ForeignKeys { get; } = []; public SchemaObjectReport Build() => new(schema, name, kind, Columns, Indexes, ForeignKeys); }
}