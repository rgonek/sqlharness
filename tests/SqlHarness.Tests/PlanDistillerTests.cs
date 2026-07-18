using System.Text;
using System.Text.Json;
using System.Xml.Linq;

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

    [Fact]
    public void Distilled_json_meets_the_sixty_percent_contract_for_canonical_lf_input()
    {
        var xml = Fixture("warnings.sqlplan").ReplaceLineEndings("\n");
        var json = JsonSerializer.Serialize(PlanDistiller.Distill(xml));

        Assert.True(Encoding.UTF8.GetByteCount(json) <= Encoding.UTF8.GetByteCount(xml) * 0.4,
            $"Distilled JSON was {Encoding.UTF8.GetByteCount(json)} bytes for {Encoding.UTF8.GetByteCount(xml)} source bytes.");
    }

    [Fact]
    public void Ignores_attacker_namespaced_attributes_even_when_they_appear_first()
    {
        var xml = MinimalPlan("""
            xmlns:attacker="urn:attacker" attacker:StatementText="spoof" StatementText="valid"
            """, """
            attacker:PhysicalOp="spoof" PhysicalOp="Index Seek"
            attacker:EstimateRows="NaN" EstimateRows="4"
            """, """
            <IndexScan><Object attacker:Table="[evil]" Table="[dbo].[Valid]" /></IndexScan>
            """);

        var statement = Assert.Single(PlanDistiller.Distill(xml).Statements);

        Assert.Equal("valid", statement.StatementText);
        Assert.Equal("Index Seek", statement.Root.PhysicalOp);
        Assert.Equal(4, statement.Root.EstimatedRows);
        Assert.Equal("[dbo].[Valid]", statement.Root.ObjectName);
    }

    [Fact]
    public void Namespaced_only_attributes_do_not_masquerade_as_showplan_data()
    {
        var xml = MinimalPlan("xmlns:attacker=\"urn:attacker\" attacker:StatementText=\"spoof\"",
            "attacker:PhysicalOp=\"spoof\" attacker:EstimateRows=\"9\"", "");

        var statement = Assert.Single(PlanDistiller.Distill(xml).Statements);

        Assert.Null(statement.StatementText);
        Assert.Equal(string.Empty, statement.Root.PhysicalOp);
        Assert.Null(statement.Root.EstimatedRows);
    }

    [Fact]
    public void Ignores_foreign_warning_children_and_orders_genuine_showplan_warnings()
    {
        var xml = MinimalPlan("xmlns:attacker=\"urn:attacker\"", "PhysicalOp=\"Scan\"", """
            <Warnings>
              <SpillToTempDb SpillLevel="1" />
              <attacker:SpillToTempDb SpillLevel="999" />
              <PlanAffectingConvert ConvertIssue="Cardinality Estimate" />
            </Warnings>
            """);

        var warnings = Assert.Single(PlanDistiller.Distill(xml).Statements).Root.Warnings;

        Assert.Equal([
            "PlanAffectingConvert[ConvertIssue=Cardinality Estimate]",
            "SpillToTempDb[SpillLevel=1]"
        ], warnings);
    }

    [Fact]
    public void Rejects_dtd_and_external_entities_without_resolving_or_leaking_content()
    {
        const string secret = "distiller-secret-value";
        var xml = $"""
            <!DOCTYPE ShowPlanXML [<!ENTITY xxe SYSTEM "file:///C:/unlikely/{secret}.txt">]>
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><Statements><StmtSimple StatementText="&xxe;"><QueryPlan><RelOp PhysicalOp="Scan" /></QueryPlan></StmtSimple></Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;

        var error = Assert.Throws<SqlHarnessSafetyException>(() => PlanDistiller.Distill(xml));

        Assert.Equal(SafetyMessage, error.Message);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_input_above_sixteen_mibibytes()
    {
        var oversized = MinimalPlan(statementAttributes: "", relOpAttributes: "", content: "")
            + new string(' ', 16 * 1024 * 1024);

        AssertSanitizedFailure(oversized);
    }

    [Fact]
    public void Rejects_configured_element_count_and_depth_limits()
    {
        var xml = MinimalPlan("", "PhysicalOp=\"Scan\"", "<Wrapper><Nested /></Wrapper>");

        Assert.Throws<SqlHarnessSafetyException>(() => PlanDistiller.Distill(xml,
            new PlanDistillerLimits(MaximumCharacters: 10_000, MaximumElements: 5, MaximumDepth: 100)));
        Assert.Throws<SqlHarnessSafetyException>(() => PlanDistiller.Distill(xml,
            new PlanDistillerLimits(MaximumCharacters: 10_000, MaximumElements: 100, MaximumDepth: 5)));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void Rejects_non_finite_numeric_values_with_sanitized_failure(string value) =>
        AssertSanitizedFailure(MinimalPlan("", $"PhysicalOp=\"Scan\" EstimateRows=\"{value}\"", ""));

    [Fact]
    public void Rejects_malformed_and_runtime_overflow_with_sanitized_failure()
    {
        AssertSanitizedFailure("<ShowPlanXML");
        AssertSanitizedFailure(MinimalPlan("", "PhysicalOp=\"Scan\"", $"""
            <RunTimeInformation>
              <RunTimeCountersPerThread ActualRows="{long.MaxValue}" ActualExecutions="1" />
              <RunTimeCountersPerThread ActualRows="1" ActualExecutions="1" />
            </RunTimeInformation>
            """));
    }

    [Fact]
    public void Preserves_statement_and_batch_document_order()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowplanNamespace}"><BatchSequence>
              <Batch><Statements>
                <StmtSimple StatementText="first"><QueryPlan><RelOp PhysicalOp="Scan" /></QueryPlan></StmtSimple>
                <StmtSimple StatementText="second"><QueryPlan><RelOp PhysicalOp="Seek" /></QueryPlan></StmtSimple>
              </Statements></Batch>
              <Batch><Statements><StmtSimple StatementText="third"><QueryPlan><RelOp PhysicalOp="Join" /></QueryPlan></StmtSimple></Statements></Batch>
            </BatchSequence></ShowPlanXML>
            """;

        Assert.Equal(["first", "second", "third"], PlanDistiller.Distill(xml).Statements.Select(s => s.StatementText));
    }

    [Fact]
    public void Nested_relops_are_children_and_do_not_leak_owned_metadata_to_parent()
    {
        var xml = MinimalPlan("", "PhysicalOp=\"Parent\"", """
            <ParentOp>
              <Object Table="[Parent]" />
              <RelOp PhysicalOp="Child"><ChildOp><Object Table="[Child]" /></ChildOp></RelOp>
            </ParentOp>
            """);

        var root = Assert.Single(PlanDistiller.Distill(xml).Statements).Root;

        Assert.Equal("[Parent]", root.ObjectName);
        Assert.Equal("[Child]", Assert.Single(root.Children).ObjectName);
    }

    [Fact]
    public void Truncates_predicate_before_json_serialization_and_escapes_json()
    {
        var predicate = new string('x', 198) + "\"\\tail";
        var escaped = new XAttribute("ScalarString", predicate).ToString();
        var xml = MinimalPlan("", "PhysicalOp=\"Seek\"", $"<SeekPredicates><ScalarOperator {escaped} /></SeekPredicates>");

        var plan = PlanDistiller.Distill(xml);
        var distilledPredicate = Assert.Single(plan.Statements).Root.Predicate;
        var json = JsonSerializer.Serialize(plan);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(200, distilledPredicate!.Length);
        Assert.Equal(distilledPredicate, document.RootElement.GetProperty("Statements")[0].GetProperty("Root").GetProperty("Predicate").GetString());
        Assert.Contains("\\\"", json, StringComparison.Ordinal);
        Assert.Contains("\\\\", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_json_has_deterministic_property_order_and_schema()
    {
        var plan = new DistilledPlan([new PlanStatement("x", new PlanNode(
            "Scan", "Logical", null, null, 1.5, 2, 1, 0.25, "a\"b", [], []), [])]);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var first = JsonSerializer.Serialize(plan, options);
        var second = JsonSerializer.Serialize(plan, options);

        Assert.Equal(first, second);
        Assert.Equal("{\"statements\":[{\"sql\":\"x\",\"root\":{\"physicalOp\":\"Scan\",\"logicalOp\":\"Logical\",\"estimatedRows\":1.5,\"actualRows\":2,\"executions\":1,\"costFraction\":0.25,\"predicate\":\"a\\u0022b\"}}]}", first);
    }

    private static IEnumerable<PlanNode> Flatten(PlanNode node)
    {
        yield return node;
        foreach (var descendant in node.Children.SelectMany(Flatten))
            yield return descendant;
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string MinimalPlan(string statementAttributes, string relOpAttributes, string content)
    {
        var namespaceEnd = statementAttributes.StartsWith("xmlns:", StringComparison.Ordinal)
            ? statementAttributes.IndexOf(' ')
            : -1;
        var namespaceDeclaration = namespaceEnd >= 0 ? statementAttributes[..namespaceEnd] : string.Empty;
        var remainingStatementAttributes = namespaceEnd >= 0
            ? statementAttributes[(namespaceEnd + 1)..]
            : statementAttributes;

        return $"""
            <ShowPlanXML xmlns="{ShowplanNamespace}" {namespaceDeclaration}>
              <BatchSequence><Batch><Statements>
                <StmtSimple {remainingStatementAttributes}><QueryPlan><RelOp {relOpAttributes}>{content}</RelOp></QueryPlan></StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;
    }

    private static void AssertSanitizedFailure(string xml)
    {
        var error = Assert.Throws<SqlHarnessSafetyException>(() => PlanDistiller.Distill(xml));
        Assert.Equal(SafetyMessage, error.Message);
    }

    private const string ShowplanNamespace = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private const string SafetyMessage = "The execution plan is not a valid SQL Server Showplan document.";
}