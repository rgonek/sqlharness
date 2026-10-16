using System.Data;
using System.Globalization;

namespace SqlHarness.Core;

/// <summary>Fixed SQL Server catalog batch for the hinted-table index inventory.</summary>
internal static class IndexInventoryQuery
{
    internal const string Sql = """
DECLARE @targets TABLE (slot int NOT NULL, schema_name sysname NOT NULL, table_name sysname NOT NULL, object_id int NULL);
INSERT INTO @targets (slot, schema_name, table_name, object_id)
SELECT v.slot, v.schema_name, v.table_name,
       OBJECT_ID(QUOTENAME(v.schema_name) + N'.' + QUOTENAME(v.table_name))
FROM (VALUES (1, @schema1, @table1), (2, @schema2, @table2), (3, @schema3, @table3), (4, @schema4, @table4),
             (5, @schema5, @table5), (6, @schema6, @table6), (7, @schema7, @table7), (8, @schema8, @table8))
     AS v(slot, schema_name, table_name)
WHERE v.schema_name IS NOT NULL AND v.table_name IS NOT NULL;

SELECT t.slot, CAST(CASE WHEN t.object_id IS NULL THEN 0 ELSE 1 END AS bit) AS object_found
FROM @targets AS t ORDER BY t.slot;

DECLARE @indexes TABLE (slot int NOT NULL, object_id int NOT NULL, index_id int NOT NULL);
INSERT INTO @indexes (slot, object_id, index_id)
SELECT ranked.slot, ranked.object_id, ranked.index_id
FROM (
    SELECT t.slot, i.object_id, i.index_id,
           ROW_NUMBER() OVER (PARTITION BY t.slot ORDER BY i.index_id) AS position
    FROM @targets AS t
    INNER JOIN sys.indexes AS i ON i.object_id = t.object_id
) AS ranked
WHERE ranked.position <= 65;

SELECT x.slot, i.index_id, i.name, i.type_desc, i.is_unique, i.is_primary_key,
       i.is_disabled, i.is_hypothetical, i.has_filter
FROM @indexes AS x
INNER JOIN sys.indexes AS i ON i.object_id = x.object_id AND i.index_id = x.index_id
ORDER BY x.slot, i.index_id;

SELECT x.slot, ic.index_id, c.name AS column_name, ic.key_ordinal, ic.is_included_column
FROM @indexes AS x
INNER JOIN sys.index_columns AS ic ON ic.object_id = x.object_id AND ic.index_id = x.index_id
INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
ORDER BY x.slot, ic.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id;
""";

    internal static IReadOnlyList<SqlHarnessParameter> Parameters(
        IReadOnlyList<(string Schema, string Table)> tables)
    {
        var parameters = new List<SqlHarnessParameter>(IndexInventoryCapture.MaxInventoryTables * 2);
        for (var slot = 0; slot < IndexInventoryCapture.MaxInventoryTables; slot++)
        {
            var present = slot < tables.Count;
            var number = (slot + 1).ToString(CultureInfo.InvariantCulture);
            parameters.Add(new SqlHarnessParameter(
                "@schema" + number,
                SqlDbType.NVarChar,
                present ? tables[slot].Schema : DBNull.Value,
                128));
            parameters.Add(new SqlHarnessParameter(
                "@table" + number,
                SqlDbType.NVarChar,
                present ? tables[slot].Table : DBNull.Value,
                128));
        }

        return parameters;
    }

    internal static async Task<IReadOnlyList<InventoryTable>> ReadAsync(
        ISqlReader reader,
        IReadOnlyList<(string Schema, string Table)> tables,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(tables);
        var slots = await ReadObjectsAsync(reader, tables.Count, ct);
        if (!await reader.NextResultAsync(ct))
            throw new InvalidOperationException("Index inventory batch is missing the index result.");

        await ReadIndexesAsync(reader, slots, ct);
        if (!await reader.NextResultAsync(ct))
            throw new InvalidOperationException("Index inventory batch is missing the column result.");

        await ReadColumnsAsync(reader, slots, ct);
        var inventory = new InventoryTable[tables.Count];
        for (var index = 0; index < tables.Count; index++)
        {
            var slot = slots[index];
            IReadOnlyList<InventoryIndex> indexes = slot.Found
                ? slot.Indexes.Select(builder => builder.ToInventory()).ToArray()
                : [];
            inventory[index] = new InventoryTable(
                tables[index].Schema,
                tables[index].Table,
                slot.Found,
                indexes,
                slot.Found && slot.Truncated);
        }

        return inventory;
    }

    private static async Task<Slot[]> ReadObjectsAsync(ISqlReader reader, int count, CancellationToken ct)
    {
        var columns = ColumnMap(reader, "slot", "object_found");
        var slots = new Slot[count];
        for (var index = 0; index < count; index++)
            slots[index] = new Slot();

        var seen = new bool[count];
        while (await reader.ReadAsync(ct))
        {
            var slot = ReadInt(Value(reader, columns, "slot"));
            if (slot < 1 || slot > count || seen[slot - 1])
                throw new InvalidOperationException("Index inventory object result has a duplicate or unknown slot.");

            seen[slot - 1] = true;
            slots[slot - 1].Found = ReadBool(Value(reader, columns, "object_found"));
        }

        for (var index = 0; index < seen.Length; index++)
        {
            if (!seen[index])
                throw new InvalidOperationException("Index inventory object result is missing a slot.");
        }

        return slots;
    }

    private static async Task ReadIndexesAsync(ISqlReader reader, Slot[] slots, CancellationToken ct)
    {
        var columns = ColumnMap(
            reader,
            "slot",
            "index_id",
            "name",
            "type_desc",
            "is_unique",
            "is_primary_key",
            "is_disabled",
            "is_hypothetical",
            "has_filter");
        while (await reader.ReadAsync(ct))
        {
            var slot = SlotAt(slots, ReadInt(Value(reader, columns, "slot")));
            if (!slot.Found)
                continue;

            if (slot.Indexes.Count >= IndexInventoryCapture.MaxIndexesPerTable)
            {
                slot.Truncated = true;
                continue;
            }

            var indexId = ReadInt(Value(reader, columns, "index_id"));
            var builder = new IndexBuilder(
                indexId,
                ReadString(Value(reader, columns, "name")),
                ReadString(Value(reader, columns, "type_desc")),
                ReadBool(Value(reader, columns, "is_unique")),
                ReadBool(Value(reader, columns, "is_primary_key")),
                ReadBool(Value(reader, columns, "is_disabled")),
                ReadBool(Value(reader, columns, "is_hypothetical")),
                ReadBool(Value(reader, columns, "has_filter")));
            if (!slot.ById.TryAdd(indexId, builder))
                throw new InvalidOperationException("Index inventory index result repeats an index.");

            slot.Indexes.Add(builder);
        }
    }

    private static async Task ReadColumnsAsync(ISqlReader reader, Slot[] slots, CancellationToken ct)
    {
        var columns = ColumnMap(reader, "slot", "index_id", "column_name", "key_ordinal", "is_included_column");
        while (await reader.ReadAsync(ct))
        {
            // SequentialAccess allows only a later ordinal. column_name precedes key_ordinal.
            var slotNumber = ReadInt(Value(reader, columns, "slot"));
            var indexId = ReadInt(Value(reader, columns, "index_id"));
            var name = ReadString(Value(reader, columns, "column_name"));
            _ = ReadInt(Value(reader, columns, "key_ordinal"));
            var included = ReadBool(Value(reader, columns, "is_included_column"));
            var slot = SlotAt(slots, slotNumber);
            if (!slot.Found || !slot.ById.TryGetValue(indexId, out var builder))
                continue;

            if (included)
                builder.Includes.Add(name);
            else
                builder.Keys.Add(name);
        }
    }

    private static Slot SlotAt(Slot[] slots, int slot)
    {
        if (slot < 1 || slot > slots.Length)
            throw new InvalidOperationException("Index inventory result has an unknown slot.");

        return slots[slot - 1];
    }

    private static Dictionary<string, int> ColumnMap(ISqlReader reader, params string[] required)
    {
        var map = new Dictionary<string, int>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            map[reader.GetName(ordinal)] = ordinal;

        foreach (var name in required)
        {
            if (!map.ContainsKey(name))
                throw new InvalidOperationException($"Index inventory result is missing column {name}.");
        }

        return map;
    }

    private static object Value(ISqlReader reader, Dictionary<string, int> columns, string name) =>
        reader.GetValue(columns[name]);

    private static int ReadInt(object value) => value switch
    {
        int number => number,
        short number => number,
        long number => checked((int)number),
        byte number => number,
        bool flag => flag ? 1 : 0,
        _ => throw new InvalidOperationException("Index inventory number is not an integer."),
    };

    private static bool ReadBool(object value) => value switch
    {
        bool flag => flag,
        byte or short or int or long => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
        _ => throw new InvalidOperationException("Index inventory flag is not a bit."),
    };

    private static string ReadString(object value) =>
        value is null or DBNull
            ? string.Empty
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private sealed class Slot
    {
        public bool Found { get; set; }

        public bool Truncated { get; set; }

        public List<IndexBuilder> Indexes { get; } = [];

        public Dictionary<int, IndexBuilder> ById { get; } = [];
    }

    private sealed class IndexBuilder(
        int indexId,
        string name,
        string type,
        bool unique,
        bool primaryKey,
        bool disabled,
        bool hypothetical,
        bool hasFilter)
    {
        public List<string> Keys { get; } = [];

        public List<string> Includes { get; } = [];

        public InventoryIndex ToInventory() =>
            new(indexId, name, type, Keys, Includes, unique, primaryKey, disabled, hypothetical, hasFilter);
    }
}