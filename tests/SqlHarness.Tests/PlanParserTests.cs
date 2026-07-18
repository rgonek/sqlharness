using SqlHarness.Core;

namespace SqlHarness.Tests;

public class PlanParserTests
{
    [Fact]
    public void Parser_detects_required_physical_operators()
    {
        var plan = ExecutionPlanParser.Parse(Fixture("operators.sqlplan"));

        Assert.Contains(plan.Operators, x => x.PhysicalOp == "Index Seek");
        Assert.Contains(plan.Operators, x => x.PhysicalOp == "Index Scan");
        Assert.Contains(plan.Operators, x => x.PhysicalOp == "Table Scan");
        Assert.Contains(plan.Operators, x => x.PhysicalOp == "Key Lookup");
    }

    [Fact]
    public void Parent_operator_does_not_inherit_child_object()
    {
        var plan = ExecutionPlanParser.Parse(Fixture("operators.sqlplan"));
        var parent = Assert.Single(plan.Operators, x => x.NodeId == 1);

        Assert.Null(parent.Object);
        Assert.Equal("SampleDetail", plan.Operators.Single(x => x.NodeId == 2).Object);
    }

    [Fact]
    public void Parser_detects_warnings_spills_and_implicit_conversions()
    {
        var plan = ExecutionPlanParser.Parse(Fixture("warnings.sqlplan"));
        var hashMatch = Assert.Single(plan.Operators);

        Assert.True(hashMatch.HasWarnings);
        Assert.True(hashMatch.HasSpill);
        Assert.True(hashMatch.HasImplicitConversion);
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}