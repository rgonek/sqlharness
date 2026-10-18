using System.Data;

using SqlHarness.Core.Dialect;

namespace SqlHarness.Core;

internal sealed record TableTypeColumn(string Name, SqlDbType Type, long Size, byte Precision, byte Scale, bool Nullable)
{
    internal string ParameterType => Type switch
    {
        SqlDbType.NVarChar => $"nvarchar({(Size == -1 ? "max" : Size / 2)})",
        SqlDbType.NChar => $"nchar({Size / 2})",
        SqlDbType.VarChar => $"varchar({(Size == -1 ? "max" : Size)})",
        SqlDbType.Char => $"char({Size})",
        SqlDbType.VarBinary => $"varbinary({(Size == -1 ? "max" : Size)})",
        SqlDbType.Binary => $"binary({Size})",
        SqlDbType.Decimal => $"decimal({Precision},{Scale})",
        SqlDbType.Time => "time",
        SqlDbType.DateTime2 => "datetime2",
        SqlDbType.DateTimeOffset => "datetimeoffset",
        _ => Type.ToString().ToLowerInvariant(),
    };
}

internal sealed record TableTypeShape(string QualifiedName, IReadOnlyList<TableTypeColumn> Columns);

internal static class SqlServerTableTypes
{
    private const string ResolveSql = """
        ;WITH resolved_type AS
        (
            SELECT TOP (1) tt.type_table_object_id, s.name AS schema_name, tt.name AS type_name
            FROM sys.table_types AS tt
            JOIN sys.schemas AS s ON s.schema_id = tt.schema_id
            WHERE tt.name = @TypeName
              AND ((@SchemaName IS NOT NULL AND s.name = @SchemaName)
                   OR (@SchemaName IS NULL AND s.name IN (SCHEMA_NAME(), N'dbo')))
            ORDER BY CASE WHEN s.name = SCHEMA_NAME() THEN 0 ELSE 1 END
        )
        SELECT QUOTENAME(r.schema_name) + N'.' + QUOTENAME(r.type_name), c.name, bt.name,
               c.max_length, c.precision, c.scale, c.is_nullable
        FROM resolved_type AS r
        JOIN sys.columns AS c ON c.object_id = r.type_table_object_id
        JOIN sys.types AS bt ON bt.user_type_id = c.system_type_id AND bt.user_type_id = bt.system_type_id
        ORDER BY c.column_id;
        """;

    internal static async Task<TableTypeShape> ResolveAsync(ISqlSession session, string typeName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var parts = typeName.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || parts.Any(part => part.Length == 0 || part.Any(char.IsControl)))
            throw new SqlHarnessSafetyException($"Invalid table type name '{typeName}'.");
        var parameters = SqlParameterParser.Bind([
            new SqlHarnessParameterInput("SchemaName", "nvarchar(128)", parts.Length == 2 ? parts[0].Trim('[', ']') : null),
            new SqlHarnessParameterInput("TypeName", "nvarchar(128)", parts[^1].Trim('[', ']')),
        ]);
        var columns = new List<TableTypeColumn>();
        string? qualifiedName = null;
        await using var reader = await session.ExecuteReaderAsync(new SqlExecutionCommand(ResolveSql, parameters, 30), ct);
        while (await reader.ReadAsync(ct))
        {
            qualifiedName ??= Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
            var typeNameFromCatalog = Convert.ToString(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (!SqlTypeMap.TryGetValue(typeNameFromCatalog, out var type))
                throw new SqlHarnessSafetyException($"Table type '{typeName}' contains unsupported column type '{typeNameFromCatalog}'.");
            columns.Add(new TableTypeColumn(
                Convert.ToString(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture) ?? "",
                type,
                Convert.ToInt64(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToByte(reader.GetValue(4), System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToByte(reader.GetValue(5), System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToBoolean(reader.GetValue(6), System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (qualifiedName is null || columns.Count == 0)
            throw new SqlHarnessSafetyException($"Table type '{typeName}' was not found or has no columns.");
        return new TableTypeShape(qualifiedName, columns);
    }

    private static readonly IReadOnlyDictionary<string, SqlDbType> SqlTypeMap = new Dictionary<string, SqlDbType>(StringComparer.OrdinalIgnoreCase)
    {
        ["bigint"] = SqlDbType.BigInt, ["binary"] = SqlDbType.Binary, ["bit"] = SqlDbType.Bit,
        ["char"] = SqlDbType.Char, ["date"] = SqlDbType.Date, ["datetime"] = SqlDbType.DateTime,
        ["datetime2"] = SqlDbType.DateTime2, ["datetimeoffset"] = SqlDbType.DateTimeOffset,
        ["decimal"] = SqlDbType.Decimal, ["float"] = SqlDbType.Float, ["int"] = SqlDbType.Int,
        ["money"] = SqlDbType.Money, ["nchar"] = SqlDbType.NChar, ["ntext"] = SqlDbType.NText,
        ["numeric"] = SqlDbType.Decimal, ["nvarchar"] = SqlDbType.NVarChar, ["real"] = SqlDbType.Real,
        ["smalldatetime"] = SqlDbType.SmallDateTime, ["smallint"] = SqlDbType.SmallInt,
        ["smallmoney"] = SqlDbType.SmallMoney, ["text"] = SqlDbType.Text, ["time"] = SqlDbType.Time,
        ["tinyint"] = SqlDbType.TinyInt, ["uniqueidentifier"] = SqlDbType.UniqueIdentifier,
        ["varbinary"] = SqlDbType.VarBinary, ["varchar"] = SqlDbType.VarChar,
    };
}
