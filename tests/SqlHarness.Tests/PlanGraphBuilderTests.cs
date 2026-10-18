using System.Text.Json;
using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class PlanGraphBuilderTests
{
    [Fact]
    public void Graph_uses_only_owned_object_and_emits_row_and_size_edges()
    {
        var result = PlanGraphBuilder.Build(Fixture("graph-properties.sqlplan"));

        Assert.Null(result.Error);
        var graph = Assert.IsType<PlanGraph>(result.Graph);
        var root = Assert.Single(graph.Nodes, node => node.StatementIndex == 0 && node.ShowplanNodeId == 0);
        Assert.Equal("sqlserver", graph.Engine);
        Assert.Equal("Parent", root.Object!.Table);
        Assert.Equal("IX_Parent", root.Object.Index);
        Assert.Equal("p", root.Object.Alias);
        Assert.Equal(7, root.TableReferenceId);
        Assert.Equal("p.Column = 'keep this readable'", Assert.Single(root.Predicates));
        Assert.Equal("Column", Assert.Single(Assert.Single(root.DefinedValues).Columns).Column);
        Assert.Contains("PlanAffectingConvert[ConvertIssue=Cardinality Estimate]", root.Warnings);
        Assert.Equal(8, root.ActualRows);
        Assert.Equal(3, root.Executions);
        Assert.Equal(2, graph.Edges.Count);
        Assert.Contains(graph.Edges, edge => edge.ActualRows == 6 && edge.EstimatedRows == 2 && edge.EstimatedSizeBytes == 16);

        var longPredicate = "p.Column = '" + new string('x', 240) + "'";
        var longPredicateFixture = Fixture("graph-properties.sqlplan").Replace(
            "p.Column = 'keep this readable'", longPredicate, StringComparison.Ordinal);
        var longPredicateGraph = Assert.IsType<PlanGraph>(PlanGraphBuilder.Build(longPredicateFixture).Graph);
        Assert.Equal(longPredicate, Assert.Single(longPredicateGraph.Nodes[0].Predicates));
    }

    [Fact]
    public void Key_lookup_preserves_table_reference_id_minus_one()
    {
        var graph = Assert.IsType<PlanGraph>(PlanGraphBuilder.Build(Fixture("graph-properties.sqlplan")).Graph);

        var keyLookup = Assert.Single(graph.Nodes, node => node.PhysicalOperator == "Key Lookup");
        Assert.Equal(-1, keyLookup.TableReferenceId);
    }

    [Fact]
    public void Parallel_runtime_counters_are_summed_per_operator()
    {
        var graph = Assert.IsType<PlanGraph>(PlanGraphBuilder.Build(Fixture("graph-properties.sqlplan")).Graph);
        var root = Assert.Single(graph.Nodes, node => node.ShowplanNodeId == 0);

        Assert.Equal(8, root.ActualRows);
        Assert.Equal(3, root.Executions);
    }

    [Fact]
    public void Unknown_operator_keeps_generic_label_and_exposes_its_properties()
    {
        var graph = Assert.IsType<PlanGraph>(PlanGraphBuilder.Build(Fixture("graph-properties.sqlplan")).Graph);
        var unknown = Assert.Single(graph.Nodes, node => node.PhysicalOperator == "FuturePlanOperator");
        var properties = PlanGraphBuilder.BuildProperties(Fixture("graph-properties.sqlplan"), unknown.Id);

        Assert.Null(properties.Error);
        Assert.Equal("sqlserver", properties.Engine);
        Assert.Contains(Flatten(Assert.IsType<PlanGraphPropertyTree>(properties.Tree).Properties),
            property => property.Name == "FutureSpecificMetadata" && property.Attributes.Any(attribute => attribute.Name == "Code" && attribute.Value == "42"));
    }

    [Fact]
    public void Property_size_limit_marks_truncation_and_keeps_the_complete_node_list()
    {
        var fixture = Fixture("graph-properties.sqlplan");
        var limits = new PlanGraphBuilderLimits(
            PlanDistiller.DefaultLimits,
            MaximumNodes: 10,
            MaximumGraphBytes: 1024 * 1024,
            MaximumPropertyResponseBytes: 2048);
        var result = PlanGraphBuilder.Build(fixture, limits);
        var graph = Assert.IsType<PlanGraph>(result.Graph);
        var properties = PlanGraphBuilder.BuildProperties(fixture, graph.Nodes[0].Id, limits);

        Assert.Equal(3, graph.Nodes.Count);
        var tree = Assert.IsType<PlanGraphPropertyTree>(properties.Tree);
        Assert.True(tree.IsTruncated);
        Assert.True(tree.OmittedNodeCount > 0);
        var marker = Assert.Single(Flatten(tree.Properties), property => property.IsTruncationMarker);
        Assert.Equal(tree.OmittedNodeCount, marker.OmittedNodeCount);
        var custom = Assert.Single(Flatten(tree.Properties), property => property.Name == "FutureSpecificMetadata");
        Assert.Equal("renderer-neutral custom operator properties: Å 漢字 \"quoted\" & <escaped>",
            Assert.Single(custom.Attributes, attribute => attribute.Name == "Description").Value);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(properties, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length <= 2048);
    }

    [Fact]
    public void Over_limit_document_returns_an_explicit_error_without_a_partial_graph()
    {
        var limits = new PlanGraphBuilderLimits(
            new PlanDistillerLimits(MaximumCharacters: 100, MaximumElements: 100, MaximumDepth: 20),
            MaximumNodes: 10,
            MaximumGraphBytes: 1024,
            MaximumPropertyResponseBytes: 512);

        var result = PlanGraphBuilder.Build(Fixture("graph-properties.sqlplan"), limits);

        Assert.Equal("sqlserver", result.Engine);
        Assert.Null(result.Graph);
        Assert.Equal("invalid_or_over_limit_plan", result.Error?.Code);
    }

    [Fact]
    public void Graph_size_limit_returns_an_explicit_error_instead_of_clipping_nodes()
    {
        var limits = new PlanGraphBuilderLimits(
            PlanDistiller.DefaultLimits,
            MaximumNodes: 10,
            MaximumGraphBytes: 1,
            MaximumPropertyResponseBytes: 512);

        var result = PlanGraphBuilder.Build(Fixture("graph-properties.sqlplan"), limits);

        Assert.Null(result.Graph);
        Assert.Equal("graph_size_limit_exceeded", result.Error?.Code);
    }

    [Fact]
    public void Node_limit_returns_an_explicit_error_instead_of_a_partial_node_list()
    {
        var limits = new PlanGraphBuilderLimits(
            PlanDistiller.DefaultLimits,
            MaximumNodes: 2,
            MaximumGraphBytes: 1024 * 1024,
            MaximumPropertyResponseBytes: 512);

        var result = PlanGraphBuilder.Build(Fixture("graph-properties.sqlplan"), limits);

        Assert.Null(result.Graph);
        Assert.Equal("graph_node_limit_exceeded", result.Error?.Code);
    }

    [Fact]
    public void Property_response_budget_includes_the_result_envelope()
    {
        var fixture = Fixture("graph-properties.sqlplan");
        var graph = Assert.IsType<PlanGraph>(PlanGraphBuilder.Build(fixture).Graph);
        var nodeId = graph.Nodes[0].Id;
        var roomyLimits = new PlanGraphBuilderLimits(
            PlanDistiller.DefaultLimits,
            MaximumNodes: 10,
            MaximumGraphBytes: 1024 * 1024,
            MaximumPropertyResponseBytes: 128 * 1024);
        var completeResult = PlanGraphBuilder.BuildProperties(fixture, nodeId, roomyLimits);
        var completeTree = Assert.IsType<PlanGraphPropertyTree>(completeResult.Tree);
        var treeBytes = JsonSerializer.SerializeToUtf8Bytes(completeTree, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
        var completeResponseBytes = JsonSerializer.SerializeToUtf8Bytes(completeResult, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;

        Assert.True(treeBytes < completeResponseBytes);
        var boundaryLimits = roomyLimits with { MaximumPropertyResponseBytes = completeResponseBytes - 1 };
        var boundedResult = PlanGraphBuilder.BuildProperties(fixture, nodeId, boundaryLimits);
        var boundedTree = Assert.IsType<PlanGraphPropertyTree>(boundedResult.Tree);

        Assert.Null(boundedResult.Error);
        Assert.True(boundedTree.IsTruncated);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(boundedTree, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length <= boundaryLimits.MaximumPropertyResponseBytes);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(boundedResult, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length <= boundaryLimits.MaximumPropertyResponseBytes);
    }
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Plans", name));

    private static IEnumerable<PlanGraphPropertyNode> Flatten(IEnumerable<PlanGraphPropertyNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
                yield return child;
        }
    }
}
