using SqlHarness.Core;

namespace SqlHarness.Tests;

public class StatisticsParserTests
{
    [Fact]
    public void Io_parser_sums_reads_and_keeps_table_breakdown()
    {
        var parsed = StatisticsIoParser.Parse(
            "Table 'Cards'. Scan count 1, logical reads 42, physical reads 0, read-ahead reads 3, lob logical reads 2.");

        Assert.Equal(44, parsed.LogicalReads);
        Assert.Equal(44, parsed.Tables["Cards"]);
    }

    [Fact]
    public void Io_parser_aggregates_repeated_tables_and_multiple_lines()
    {
        var parsed = StatisticsIoParser.Parse("""
            Table 'Cards'. Scan count 1, logical reads 10, physical reads 0, read-ahead reads 0, lob logical reads 1.
            Table 'Clients'. Scan count 1, logical reads 7, physical reads 0, read-ahead reads 2, lob logical reads 0.
            Table 'Cards'. Scan count 1, logical reads 4, physical reads 0, read-ahead reads 0, lob logical reads 3.
            """);

        Assert.Equal(25, parsed.LogicalReads);
        Assert.Equal(18, parsed.Tables["Cards"]);
        Assert.Equal(7, parsed.Tables["Clients"]);
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
    }
}
