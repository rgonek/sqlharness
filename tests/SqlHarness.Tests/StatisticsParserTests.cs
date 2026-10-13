using SqlHarness.Core;

namespace SqlHarness.Tests;

public class StatisticsParserTests
{
    [Theory]
    [InlineData("[dbo].[#ClientPrices_____________________________________________________________________________________________________00000001C686]", "[dbo].[#ClientPrices]")]
    [InlineData("#Work__Items_______________________________________________________________________________________________________000000000A1B", "#Work__Items")]
    [InlineData("[dbo].[Orders]", "[dbo].[Orders]")]
    [InlineData("[dbo].[Archive_000000000ABC]", "[dbo].[Archive_000000000ABC]")]
    [InlineData("Worktable", "Worktable")]
    [InlineData("@Work__Items", "@Work__Items")]
    public void Temp_table_keys_drop_only_the_session_suffix(string table, string expected) =>
        Assert.Equal(expected, TempTableKey.Normalize(table));

    [Fact]
    public void Io_parser_uses_a_stable_key_across_temp_table_sessions()
    {
        var first = StatisticsIoParser.Parse("Table '#t__________________________________________________________________________________________________________________00000001C686'. Scan count 1, logical reads 8.");
        var second = StatisticsIoParser.Parse("Table '#t__________________________________________________________________________________________________________________00000001C687'. Scan count 1, logical reads 8.");

        Assert.Equal(first.Tables.Keys, second.Tables.Keys);
    }

    [Fact]
    public void Io_detail_parser_normalizes_and_sums_repeated_temp_table_keys()
    {
        var parsed = StatisticsIoDetailParser.Parse("""
            Table '#t__________________________________________________________________________________________________________________00000001C686'. Scan count 1, logical reads 8, physical reads 0, page server reads 0, read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob read-ahead reads 0.
            Table '#t__________________________________________________________________________________________________________________00000001C687'. Scan count 2, logical reads 3, physical reads 1, page server reads 0, read-ahead reads 2, lob logical reads 4, lob physical reads 0, lob read-ahead reads 0.
            """);

        var table = Assert.Single(parsed);
        Assert.Equal("#t", table.Table);
        Assert.Equal(3, table.ScanCount);
        Assert.Equal(11, table.LogicalReads);
        Assert.Equal(1, table.PhysicalReads);
        Assert.Equal(2, table.ReadAheadReads);
        Assert.Equal(4, table.LobLogicalReads);
    }

    [Fact]
    public void Io_parser_sums_reads_and_keeps_table_breakdown()
    {
        var parsed = StatisticsIoParser.Parse(
            "Table 'SampleRows'. Scan count 1, logical reads 42, physical reads 0, read-ahead reads 3, lob logical reads 2.");

        Assert.Equal(44, parsed.LogicalReads);
        Assert.Equal(44, parsed.Tables["SampleRows"]);
    }

    [Fact]
    public void Io_parser_aggregates_repeated_tables_and_multiple_lines()
    {
        var parsed = StatisticsIoParser.Parse("""
            Table 'SampleRows'. Scan count 1, logical reads 10, physical reads 0, read-ahead reads 0, lob logical reads 1.
            Table 'LookupRows'. Scan count 1, logical reads 7, physical reads 0, read-ahead reads 2, lob logical reads 0.
            Table 'SampleRows'. Scan count 1, logical reads 4, physical reads 0, read-ahead reads 0, lob logical reads 3.
            """);

        Assert.Equal(25, parsed.LogicalReads);
        Assert.Equal(18, parsed.Tables["SampleRows"]);
        Assert.Equal(7, parsed.Tables["LookupRows"]);
    }

    [Fact]
    public void Time_parser_sums_sql_server_execution_times()
    {
        var parsed = StatisticsTimeParser.Parse("""
            SQL Server Execution Times:
               CPU time = 12 ms,  elapsed time = 20 ms.
            SQL Server Execution Times: CPU time = 3 ms, elapsed time = 5 ms.
            """);

        Assert.Equal(15, parsed.CpuTimeMs);
        Assert.Equal(25, parsed.ElapsedTimeMs);
        Assert.Equal(2, parsed.RecognizedBlocks);
    }

    [Fact]
    public void Time_parser_counts_no_blocks_for_localized_output()
    {
        var parsed = StatisticsTimeParser.Parse(
            "SQL Server-Ausführungszeiten:\n   CPU-Zeit = 12 ms, verstrichene Zeit = 20 ms.");

        Assert.Equal(0, parsed.RecognizedBlocks);
        Assert.Equal(0, parsed.CpuTimeMs);
    }

    [Fact]
    public void Time_parser_counts_no_blocks_for_empty_input()
    {
        var parsed = StatisticsTimeParser.Parse("");

        Assert.Equal(0, parsed.RecognizedBlocks);
    }
}
