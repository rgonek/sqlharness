using SqlHarness.Core;
namespace SqlHarness.Tests;
public sealed class JournalStatementGrantTests
{
    [Fact]
    public void Statement_grants_and_spill_are_owned_by_query_plan()
    {
        var first = StatementMetricsTests.JournalPlan("SELECT 1", 1, 1, "0x01", 0, 1).Replace("<QueryPlan ", "<QueryPlan ", StringComparison.Ordinal).Replace("<RelOp", "<MemoryGrantInfo RequestedMemory=\"10\" GrantedMemory=\"8\" MaxUsedMemory=\"4\" /><RelOp", StringComparison.Ordinal);
        var statement = Assert.Single(JournalStatementMetricsExtractor.Extract([first], out _));
        Assert.Equal(10, statement.GrantRequestedKb);
        Assert.Equal(8, statement.GrantGrantedKb);
        Assert.Equal(4, statement.GrantMaxUsedKb);
        Assert.False(statement.HasSpillWarning);
    }

    [Fact]
    public void Spill_and_missing_plan_are_tristate_and_multiple_sources_are_not_summed()
    {
        var clean = StatementMetricsTests.JournalPlan("SELECT 1", 1, 1, "0x01", 0, 1);
        var spill = clean.Replace("<RelOp", "<Warnings><SpillToTempDb /></Warnings><RelOp", StringComparison.Ordinal);
        var missing = "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements><StmtSimple StatementText=\"SELECT 1\" StatementCompId=\"1\" QueryPlanHash=\"0x01\" /></Statements></Batch></BatchSequence></ShowPlanXML>";
        Assert.True(Assert.Single(JournalStatementMetricsExtractor.Extract([spill], out _)).HasSpillWarning);
        Assert.Null(Assert.Single(JournalStatementMetricsExtractor.Extract([missing], out _)).HasSpillWarning);
        var multiple = Assert.Single(JournalStatementMetricsExtractor.Extract([clean, clean], out _));
        Assert.Null(multiple.HasSpillWarning);
        Assert.Null(multiple.GrantRequestedKb);
        var dynamicSql = clean.Replace("SELECT 1", "EXEC dbo.p", StringComparison.Ordinal);
        Assert.Null(Assert.Single(JournalStatementMetricsExtractor.Extract([dynamicSql], out _)).HasSpillWarning);
        var udf = clean.Replace("<QueryPlan ", "<QueryPlan UdfElapsedTime=\"0\" ", StringComparison.Ordinal);
        Assert.Null(Assert.Single(JournalStatementMetricsExtractor.Extract([udf], out _)).HasSpillWarning);
    }

    [Fact]
    public void Each_statement_owns_its_grant()
    {
        var first = StatementMetricsTests.JournalPlan("SELECT 1", 1, 1, "0x01", 0, 1).Replace("<RelOp", "<MemoryGrantInfo RequestedMemory=\"10\" /><RelOp", StringComparison.Ordinal);
        var second = StatementMetricsTests.JournalPlan("SELECT 2", 2, 2, "0x02", 0, 1).Replace("<RelOp", "<MemoryGrantInfo RequestedMemory=\"20\" /><RelOp", StringComparison.Ordinal);
        var statements = JournalStatementMetricsExtractor.Extract([first, second], out _);
        Assert.Equal(new long?[] { 10, 20 }, statements.Select(item => item.GrantRequestedKb));
        Assert.Equal(new int?[] { 0, 1 }, statements.Select(item => item.PlanDocumentOrdinal));
    }
}
