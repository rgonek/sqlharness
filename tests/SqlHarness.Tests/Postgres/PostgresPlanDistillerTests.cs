using SqlHarness.Core;
using SqlHarness.Core.Postgres;

namespace SqlHarness.Tests.Postgres;

public sealed class PostgresPlanDistillerTests
{
    [Fact]
    public void Distills_seq_scan_and_hash_join()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "seq-scan.explain.json"));
        var plan = PostgresPlanDistiller.Distill(json);
        var root = Assert.Single(plan.Statements).Root;
        Assert.Equal("Hash Join", root.PhysicalOp);
        Assert.Equal("Seq Scan", root.Children[0].PhysicalOp);
        Assert.Equal("foo", root.Children[0].ObjectName);
        Assert.True(root.CostFraction is >= 0 and <= 1);
        Assert.Empty(Assert.Single(plan.Statements).MissingIndexes);
    }

    [Fact]
    public void Invalid_json_throws_without_echoing_payload()
    {
        const string secret = "explain-secret";
        var error = Assert.Throws<SqlHarnessSafetyException>(() => PostgresPlanDistiller.Distill(secret));
        Assert.DoesNotContain(secret, error.Message);
    }

    [Fact]
    public void Distilled_plan_json_is_rejected()
    {
        const string distilled = """{"statements":[{"root":{"physicalOp":"Seq Scan"}}]}""";
        var error = Assert.Throws<SqlHarnessSafetyException>(() => PostgresPlanDistiller.Distill(distilled));
        Assert.DoesNotContain("Seq Scan", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("statements", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_too_deep_nested_plans_without_echoing_payload()
    {
        const string secret = "deep-plan-secret";
        var inner = $$"""{"Node Type":"Seq Scan","Relation Name":"{{secret}}"}""";
        for (var i = 0; i < 6; i++)
            inner = $$"""{"Node Type":"Nested Loop","Plans":[{{inner}}]}""";
        var json = $$"""[{"Plan":{{inner}}}]""";

        var error = Assert.Throws<SqlHarnessSafetyException>(() =>
            PostgresPlanDistiller.Distill(json, new PlanDistillerLimits(1_000_000, 100_000, MaximumDepth: 5)));

        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Nested Loop", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(json, error.Message, StringComparison.Ordinal);
    }
}