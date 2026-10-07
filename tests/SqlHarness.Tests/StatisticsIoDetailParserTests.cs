using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class StatisticsIoDetailParserTests
{
    [Fact]
    public void Modern_line_with_page_server_counters_is_fully_parsed()
    {
        const string text = "Table 'Orders'. Scan count 3, logical reads 120, physical reads 2, page server reads 0, read-ahead reads 40, page server read-ahead reads 0, lob logical reads 7, lob physical reads 1, lob page server reads 0, lob read-ahead reads 5, lob page server read-ahead reads 0.";

        var table = Assert.Single(StatisticsIoDetailParser.Parse(text));

        Assert.Equal(new TableIoCounters("Orders", 3, 120, 2, 0, 40, 7, 1, 5), table);
    }

    [Fact]
    public void Legacy_line_without_page_server_counters_defaults_missing_to_zero()
    {
        const string text = "Table 'Clients'. Scan count 1, logical reads 5, physical reads 0, lob logical reads 0.";

        Assert.Equal(new TableIoCounters("Clients", 1, 5, 0, 0, 0, 0, 0, 0), Assert.Single(StatisticsIoDetailParser.Parse(text)));
    }

    [Fact]
    public void Worktables_quoted_names_and_repeated_tables_are_handled()
    {
        const string text = """
            Table 'Worktable'. Scan count 0, logical reads 0, physical reads 0, read-ahead reads 0.
            Table 'O''Brien'. Scan count 1, logical reads 4, physical reads 1, read-ahead reads 0.
            SQL Server Execution Times: CPU time = 1 ms, elapsed time = 2 ms.
            Table 'O''Brien'. Scan count 2, logical reads 6, physical reads 0, read-ahead reads 3.
            """;

        var tables = StatisticsIoDetailParser.Parse(text);

        Assert.Equal(["Worktable", "O'Brien"], tables.Select(table => table.Table));
        Assert.Equal(new TableIoCounters("O'Brien", 3, 10, 1, 0, 3, 0, 0, 0), tables[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SQL Server parse and compile time: CPU time = 0 ms.")]
    [InlineData("Table 'Broken'. Scan count x, logical reads.")]
    [InlineData("Table 'Clients'. Lectures logiques 5, lectures physiques 0.")]
    public void Text_without_counters_yields_no_rows(string text) =>
        Assert.Empty(StatisticsIoDetailParser.Parse(text));

    [Fact]
    public void Columnstore_segment_lines_do_not_add_rows()
    {
        const string text = """
            Table 'Sales'. Scan count 1, logical reads 0, physical reads 0, lob logical reads 30, lob physical reads 0, lob read-ahead reads 0.
            Table 'Sales'. Segment reads 2, segment skipped 1.
            """;

        Assert.Equal(new TableIoCounters("Sales", 1, 0, 0, 0, 0, 30, 0, 0), Assert.Single(StatisticsIoDetailParser.Parse(text)));
    }
}