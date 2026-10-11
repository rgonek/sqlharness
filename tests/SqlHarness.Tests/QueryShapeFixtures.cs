using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.TestFixtures;

/// <summary>
/// Synthetic query reports and the result-shape invariants of the agent
/// query projection. Shared by the CLI and MCP test projects (linked file).
/// </summary>
internal static class QueryShapeFixtures
{
    public static SqlHarnessResultSetReport Set(int columns, int rows, int width = 4, int nameLength = 4)
    {
        var columnReports = Enumerable.Range(0, columns)
            .Select(c => new SqlHarnessColumnReport(c, $"c{c}".PadRight(nameLength, 'n'), "nvarchar", true))
            .ToArray();
        var rowValues = Enumerable.Range(0, rows)
            .Select(r => (IReadOnlyList<object?>)Enumerable.Range(0, columns)
                .Select(c => (object?)$"{r}:{c}".PadRight(width, 'v'))
                .ToArray())
            .ToArray();
        return new SqlHarnessResultSetReport(columnReports, rowValues, rows, 0);
    }

    public static SqlHarnessQueryReport Report(params SqlHarnessResultSetReport[] sets) => new(
        new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile"),
        "read-only", sets, [], 0, 1, "raw-hash", new OutputFootprint(1234, 1));

    /// <summary>
    /// Every source result set, column and row is either emitted or counted as
    /// omitted, emitted sets and columns are prefixes of the source, and every
    /// emitted row matches the emitted column schema.
    /// </summary>
    public static void AssertAccounted(JsonElement result, SqlHarnessQueryReport source)
    {
        var sets = result.GetProperty("resultSets");
        Assert.Equal(source.ResultSets.Count, sets.GetArrayLength() + result.GetProperty("omittedResultSetCount").GetInt32());
        for (var i = 0; i < sets.GetArrayLength(); i++)
        {
            var set = sets[i];
            var origin = source.ResultSets[i];
            Assert.Equal(i, set.GetProperty("ordinal").GetInt32());
            var columns = set.GetProperty("columns");
            Assert.Equal(origin.Columns.Count, columns.GetArrayLength() + set.GetProperty("omittedColumnCount").GetInt32());
            for (var c = 0; c < columns.GetArrayLength(); c++)
                Assert.Equal(origin.Columns[c].Name, columns[c].GetProperty("name").GetString());
            var rows = set.GetProperty("rows");
            Assert.Equal(origin.RowCount, set.GetProperty("rowCount").GetInt64());
            Assert.Equal(origin.Rows.Count + origin.OmittedRowCount, rows.GetArrayLength() + set.GetProperty("omittedRowCount").GetInt64());
            if (rows.GetArrayLength() > 0)
                Assert.Equal(0, set.GetProperty("omittedColumnCount").GetInt32());
            for (var r = 0; r < rows.GetArrayLength(); r++)
            {
                Assert.Equal(columns.GetArrayLength(), rows[r].GetArrayLength());
                for (var c = 0; c < columns.GetArrayLength(); c++)
                    Assert.StartsWith(rows[r][c].GetString()!, (string)origin.Rows[r][c]!, StringComparison.Ordinal);
            }
        }
    }
}