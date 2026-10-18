using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Core.Postgres;

namespace SqlHarness.Tests;

public sealed class StatementMetricsTests
{
    [Fact]
    public void Extract_keeps_colliding_node_ids_on_separate_statements_with_preorder_paths()
    {
        var statements = StatementMetricsExtractor.Extract(CollidingNodeIdPlan(), out var omitted);

        Assert.Equal(0, omitted);
        Assert.Equal(2, statements.Count);
        Assert.Equal(0, statements[0].StatementOrdinal);
        Assert.Equal(1, statements[1].StatementOrdinal);
        Assert.Equal(1, Assert.Single(statements[0].TopOperators).NodeId);
        Assert.Equal(1, Assert.Single(statements[1].TopOperators).NodeId);
        Assert.Equal("0", statements[0].TopOperators[0].NodePath);
        Assert.Equal("0", statements[1].TopOperators[0].NodePath);
        Assert.Equal(0, statements[0].TopOperators[0].Cursor);
        Assert.Equal(0, statements[1].TopOperators[0].Cursor);
        Assert.NotEqual(statements[0].StatementHash, statements[1].StatementHash);
    }

    [Fact]
    public void Extract_node_paths_match_distilled_preorder_including_nested_children()
    {
        var xml = NestedRelOpPlan();
        var extracted = Assert.Single(StatementMetricsExtractor.Extract(xml, out _));
        var distilled = Assert.Single(PlanDistiller.Distill(xml).Statements);
        var preorder = PlanDistiller.Preorder(distilled.Root).ToArray();

        Assert.Equal(3, extracted.TopOperators.Count);
        Assert.Equal(["0", "0/0", "0/1"], extracted.TopOperators.Select(op => op.NodePath!).ToArray());
        Assert.Equal([0, 1, 2], extracted.TopOperators.Select(op => op.Cursor!.Value).ToArray());
        Assert.Equal(preorder.Select(node => node.Path).ToArray(), extracted.TopOperators.Select(op => op.NodePath!).ToArray());
        Assert.Equal([0, 1, 2], preorder.Select(node => node.Node.NodeId!.Value).ToArray());
    }

    [Fact]
    public void Execution_plan_parser_attaches_statement_path_and_cursor_instead_of_flattening_colliding_ids()
    {
        var plan = ExecutionPlanParser.Parse(CollidingNodeIdPlan());

        Assert.Equal(2, plan.StatementCount);
        Assert.Equal(2, plan.Operators.Count);
        Assert.Equal([0, 1], plan.Operators.Select(op => op.StatementOrdinal).ToArray());
        Assert.Equal([1, 1], plan.Operators.Select(op => op.NodeId).ToArray());
        Assert.Equal(["0", "0"], plan.Operators.Select(op => op.NodePath!).ToArray());
        Assert.Equal([0, 0], plan.Operators.Select(op => op.Cursor!.Value).ToArray());
        Assert.Equal("Index Seek", plan.Operators[0].PhysicalOp);
        Assert.True(plan.Operators[1].HasSpill);
    }

    [Fact]
    public void Variant_report_preserves_origin_across_repetitions_and_does_not_merge_colliding_ids()
    {
        var xml = CollidingNodeIdPlan();
        var runs = new[]
        {
            MeasuredRun("baseline", 1, xml),
            MeasuredRun("baseline", 2, xml),
        };

        var operators = BenchmarkReports.CreateVariantReport("baseline", runs).Operators;

        Assert.Equal(4, operators.Count);
        Assert.All(operators, op =>
        {
            Assert.NotNull(op.NodePath);
            Assert.NotNull(op.Statement);
            Assert.NotNull(op.Repetition);
            Assert.NotNull(op.Cursor);
        });
        Assert.Equal(2, operators.Count(op => op.Statement == 1 && op.HasSpill));
        Assert.Contains(operators, op => op.Statement == 0 && op.Repetition == 1 && op.NodeId == 1 && !op.HasSpill);
        Assert.Contains(operators, op => op.Statement == 1 && op.Repetition == 2 && op.NodeId == 1 && op.HasSpill);
    }

    [Fact]
    public void Statement_metrics_projection_keeps_node_path_after_repetition_aggregation()
    {
        var xml = NestedRelOpPlan();
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 10, 11, 1, new Dictionary<string, long>(), "h1", [xml], 0),
            new CompareRunArtifact("baseline", 2, 20, 21, 2, new Dictionary<string, long>(), "h2", [xml], 0),
        };

        var section = ArtifactStatementProjector.Project(ArtifactReader.CompareKind, runs);
        var statement = Assert.Single(section.Statements);
        var operators = statement.TopOperators.ToDictionary(op => op.NodeId);

        Assert.Equal(0, statement.StatementOrdinal);
        Assert.Equal("0", operators[0].NodePath);
        Assert.Equal("0/0", operators[1].NodePath);
        Assert.Equal("0/1", operators[2].NodePath);
        Assert.Equal(1, operators[1].Cursor);
    }

    [Fact]
    public void Statement_metrics_hash_uses_the_paired_full_executed_span()
    {
        var sql = "SELECT N'" + new string('a', 4100) + "';";
        var statementText = System.Security.SecurityElement.Escape(sql[..3999]);
        var xml = $"<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements><StmtSimple StatementText=\"{statementText}\"><QueryPlan /></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>";
        var run = new CompareRunArtifact("baseline", 1, 10, 11, 1, new Dictionary<string, long>(), "h", [xml], 0)
        {
            ExecutedSql = sql,
        };

        var statement = Assert.Single(ArtifactStatementProjector.Project(ArtifactReader.CompareKind, [run]).Statements);
        var journalStatement = Assert.Single(JournalBenchmarkBuilder.Build([run]).Statements);

        Assert.Equal(PlanIdentity.StatementSha256(sql), statement.StatementHash);
        Assert.NotEqual(PlanIdentity.StatementSha256(sql[..3999]), statement.StatementHash);
        Assert.Equal(statement.StatementHash, journalStatement.StatementHash);
    }

    [Fact]
    public void Postgres_explain_json_is_readable_without_node_ids()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "seq-scan.explain.json"));
        var distilled = PostgresPlanDistiller.Distill(json);
        var parsed = ExecutionPlanParser.Parse(json);

        Assert.All(PlanDistiller.Preorder(distilled.Statements[0].Root), node => Assert.Null(node.Node.NodeId));
        Assert.NotEmpty(parsed.Operators);
        Assert.Equal("0", parsed.Operators[0].NodePath);
        var stored = StoredPlanReader.Read(JsonSerializer.Serialize(distilled, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Null(stored.Statements[0].Root.NodeId);
    }

    internal static CollectedBenchmarkRun MeasuredRun(string variant, int repetition, string xml, string? parameterSet = null)
    {
        var artifact = new CompareRunArtifact(
            variant, repetition, 1, 2, 3, new Dictionary<string, long>(), "hash", [xml], 0, parameterSet);
        var parsed = ExecutionPlanParser.Parse(xml);
        return new CollectedBenchmarkRun(artifact, [parsed], ["hash"]);
    }

    internal static string CollidingNodeIdPlan() => """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT a FROM dbo.Accounts">
              <QueryPlan>
                <RelOp NodeId="1" PhysicalOp="Index Seek" EstimateRows="1">
                  <IndexScan><Object Table="[dbo].[Accounts]" /></IndexScan>
                </RelOp>
              </QueryPlan>
            </StmtSimple>
            <StmtSimple StatementText="SELECT b FROM dbo.Accounts">
              <QueryPlan>
                <RelOp NodeId="1" PhysicalOp="Clustered Index Scan" EstimateRows="7">
                  <Warnings><SpillToTempDb SpillLevel="1" /></Warnings>
                  <IndexScan><Object Table="[dbo].[Accounts]" /></IndexScan>
                </RelOp>
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    internal static string NestedRelOpPlan() => """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT nested">
              <QueryPlan>
                <RelOp NodeId="0" PhysicalOp="Nested Loops" EstimateRows="2">
                  <NestedLoops>
                    <RelOp NodeId="1" PhysicalOp="Index Seek" EstimateRows="3">
                      <IndexScan><Object Table="[dbo].[Child]" Index="[IX_Child]" /></IndexScan>
                    </RelOp>
                    <RelOp NodeId="2" PhysicalOp="Table Scan" EstimateRows="4">
                      <Warnings><SpillToTempDb SpillLevel="1" /></Warnings>
                      <TableScan><Object Table="[dbo].[Parent]" /></TableScan>
                    </RelOp>
                  </NestedLoops>
                </RelOp>
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;
}