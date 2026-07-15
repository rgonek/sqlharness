using System.Text;
using System.Text.Json;
using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class PlanDistillerTests
{
    [Fact]
    public void Distills_statement_tree_runtime_warnings_and_missing_indexes()
    {
        var plan = PlanDistiller.Distill(Fixture("distiller-sample.sqlplan"));

        var statement = Assert.Single(plan.Statements);
        Assert.Equal("SELECT ...", statement.StatementText);
        Assert.Equal("Nested Loops", statement.Root.PhysicalOp);
        Assert.Equal(2, statement.Root.Children.Count);

        var seek = statement.Root.Children[0];
        Assert.Equal("[dbo].[Orders]", seek.ObjectName);
        Assert.Equal("[IX_Orders_Customer]", seek.IndexName);
        Assert.Equal(7, seek.ActualRows);
        Assert.Equal(2, seek.Executions);
        Assert.Contains("CustomerId", seek.Predicate);

        var scan = statement.Root.Children[1];
        Assert.Contains(scan.Warnings, warning => warning.Contains("SpillToTempDb", StringComparison.Ordinal));

        var missing = Assert.Single(statement.MissingIndexes);
        Assert.Equal(87.5, missing.Impact);
        Assert.Equal("[Orders]", missing.Table);
        Assert.Equal(["[CustomerId]"], missing.EqualityColumns);
        Assert.Empty(missing.InequalityColumns);
        Assert.Equal(["[OrderDate]"], missing.IncludeColumns);

        Assert.All(Flatten(statement.Root), node =>
        {
            Assert.NotNull(node.CostFraction);
            Assert.InRange(node.CostFraction!.Value, 0, 1);
        });
    }

    [Fact]
    public void Rejects_non_showplan_xml() =>
        Assert.Throws<SqlHarnessSafetyException>(() => PlanDistiller.Distill("<not-showplan/>"));

    [Fact]
    public void Estimated_plan_has_no_actual_runtime_values()
    {
        var xml = Fixture("distiller-sample.sqlplan");
        var start = xml.IndexOf("<RunTimeInformation>", StringComparison.Ordinal);
        var end = xml.IndexOf("</RunTimeInformation>", StringComparison.Ordinal) + "</RunTimeInformation>".Length;

        var plan = PlanDistiller.Distill(xml.Remove(start, end - start));

        Assert.Null(plan.Statements[0].Root.Children[0].ActualRows);
        Assert.Null(plan.Statements[0].Root.Children[0].Executions);
    }

    [Theory]
    [InlineData("operators.sqlplan")]
    [InlineData("warnings.sqlplan")]
    public void Distilled_json_is_at_least_sixty_percent_smaller_than_source(string fixture)
    {
        var xml = Fixture(fixture);
        var json = JsonSerializer.Serialize(PlanDistiller.Distill(xml));

        Assert.True(Encoding.UTF8.GetByteCount(json) <= Encoding.UTF8.GetByteCount(xml) * 0.4,
            $"Distilled JSON was {Encoding.UTF8.GetByteCount(json)} bytes for {Encoding.UTF8.GetByteCount(xml)} source bytes.");
    }

    private static IEnumerable<PlanNode> Flatten(PlanNode node)
    {
        yield return node;
        foreach (var descendant in node.Children.SelectMany(Flatten))
            yield return descendant;
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
