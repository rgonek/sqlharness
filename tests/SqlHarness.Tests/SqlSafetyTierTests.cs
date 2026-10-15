using SqlHarness.Core;

namespace SqlHarness.Tests;

public class SqlSafetyTierTests
{
    private readonly SqlSafetyClassifier _classifier = new();

    private SqlSafetyDecision Mutation(string sql) => _classifier.Classify(sql, SqlUsage.Mutation, "appdb", false);
    private SqlSafetyDecision Query(string sql) => _classifier.Classify(sql, SqlUsage.Query, "appdb", false);

    [Theory]
    [InlineData("SELECT 1 AS A", SqlRiskTier.ReadOnly)]
    [InlineData("CREATE TABLE #t (Id int); INSERT #t VALUES (1);", SqlRiskTier.Session)]
    [InlineData("UPDATE dbo.Orders SET Total = 0 WHERE Id = 1", SqlRiskTier.Dml)]
    [InlineData("CREATE TABLE #t (Id int); DELETE dbo.Orders WHERE Id = 1;", SqlRiskTier.Dml)]
    public void Mutation_usage_reports_the_highest_tier(string sql, object expected)
    {
        var decision = Mutation(sql);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.Equal((SqlRiskTier)expected, decision.Tier);
        Assert.Equal((SqlRiskTier)expected >= SqlRiskTier.Dml, decision.HasMutation);
    }

    [Theory]
    [InlineData("SELECT 1 AS A", SqlRiskTier.ReadOnly)]
    [InlineData("CREATE TABLE #t (Id int);", SqlRiskTier.Session)]
    public void Query_usage_reports_tier_without_changing_decisions(string sql, object expected)
    {
        var decision = Query(sql);
        Assert.True(decision.Allowed);
        Assert.Equal((SqlRiskTier)expected, decision.Tier);
    }

    [Fact]
    public void Query_usage_still_rejects_unapproved_dml_with_the_same_reason() =>
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, Query("DELETE dbo.Orders WHERE Id = 1").Reason);

    [Theory]
    [InlineData("SELECT 1 AS A", SqlRiskTier.ReadOnly)]
    [InlineData("CREATE TABLE #t (Id int);", SqlRiskTier.Session)]
    public void Compare_setup_reports_tier(string sql, object expected)
    {
        var decision = _classifier.Classify(sql, SqlUsage.CompareSetup, "appdb", false);
        Assert.True(decision.Allowed);
        Assert.Equal((SqlRiskTier)expected, decision.Tier);
    }

    [Theory]
    [InlineData(SqlRiskTier.ReadOnly, "read-only")]
    [InlineData(SqlRiskTier.Session, "session")]
    [InlineData(SqlRiskTier.Dml, "dml")]
    [InlineData(SqlRiskTier.Schema, "schema")]
    [InlineData(SqlRiskTier.Privilege, "privilege")]
    [InlineData(SqlRiskTier.Opaque, "opaque")]
    public void Labels_are_stable(object tier, string label) =>
        Assert.Equal(label, SqlRiskTiers.Label((SqlRiskTier)tier));
}
