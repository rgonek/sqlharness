using System.Xml.Linq;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class SourceMapBuilderTests
{
    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    [Fact]
    public void Cte_instances_resolve_by_table_reference_id()
    {
        const string sql = "WITH StatusRows AS (SELECT s.Id FROM dbo.ProjectionStatus AS s) SELECT a.Id FROM StatusRows AS a JOIN StatusRows AS b ON b.Id = a.Id;";
        var built = BuildGraph(sql, Join(
            Scan("ProjectionStatus", "s", 1),
            Scan("ProjectionStatus", "s", 2)));

        var links = TableLinks(built.SourceMap);
        Assert.Collection(links, _ => { }, _ => { });
        Assert.All(links, link => Assert.Equal("exact", link.State));
        Assert.All(links, link => Assert.Equal("dbo.ProjectionStatus AS s", SpanText(sql, link)));
        Assert.Contains(links, link => link.Rule.Contains("TableReferenceId 1", StringComparison.Ordinal));
        Assert.Contains(links, link => link.Rule.Contains("TableReferenceId 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Repeated_table_in_outer_from_and_in_subquery_resolves_by_table_reference_id()
    {
        const string sql = "SELECT p.Id FROM dbo.Person WHERE p.ParentId IN (SELECT ParentId FROM dbo.Person);";
        var built = BuildGraph(sql, Join(
            Scan("Person", alias: null, tableReferenceId: 1),
            Scan("Person", alias: null, tableReferenceId: 2)));

        var links = TableLinks(built.SourceMap);
        Assert.Equal(["dbo.Person", "dbo.Person"], links.Select(link => SpanText(sql, link)).ToArray());
        Assert.Equal(["exact", "exact"], links.Select(link => link.State).ToArray());
        Assert.Equal(2, links.Select(link => link.SpanStart).Distinct().Count());
    }

    [Fact]
    public void Same_alias_in_derived_tables_resolves_by_table_name_without_tri()
    {
        const string sql = "SELECT c.Id FROM (SELECT x.Id FROM dbo.Customer AS x) AS c JOIN (SELECT x.Id FROM dbo.Supplier AS x) AS s ON s.Id = c.Id;";
        var built = BuildGraph(sql, Join(
            Scan("Customer", "x"),
            Scan("Supplier", "x")));

        var customer = Assert.Single(TableLinks(built.SourceMap), link => link.NodeId == Node(built.Graph, "Customer").Id);
        var supplier = Assert.Single(TableLinks(built.SourceMap), link => link.NodeId == Node(built.Graph, "Supplier").Id);
        Assert.Equal("exact", customer.State);
        Assert.Equal("exact", supplier.State);
        Assert.Contains("dbo.Customer AS x", SpanText(sql, customer), StringComparison.Ordinal);
        Assert.Contains("dbo.Supplier AS x", SpanText(sql, supplier), StringComparison.Ordinal);
    }

    [Fact]
    public void View_internal_alias_does_not_bind_to_outer_alias()
    {
        const string sql = "SELECT ci.Id FROM dbo.CustomerInformationView AS CI;";
        var built = BuildGraph(sql, Scan("InternalCustomer", "CI"));

        var link = Assert.Single(TableLinks(built.SourceMap));
        Assert.Equal("inferred", link.State);
        Assert.Equal("dbo.CustomerInformationView AS CI", SpanText(sql, link));
        Assert.Contains("module", link.Rule, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Module_alias_collision_uses_table_name_before_module_fallback()
    {
        const string sql = "SELECT ci.Id, v.Id FROM dbo.CustomerInformationView AS v JOIN dbo.Customer AS CI ON CI.Id = v.Id;";
        var built = BuildGraph(sql, Join(
            Scan("Customer", "CI"),
            Scan("InternalCustomer", "CI")));

        var customer = Assert.Single(TableLinks(built.SourceMap), link => link.NodeId == Node(built.Graph, "Customer").Id);
        var internalCustomer = Assert.Single(TableLinks(built.SourceMap), link => link.NodeId == Node(built.Graph, "InternalCustomer").Id);
        Assert.Equal("exact", customer.State);
        Assert.Equal("dbo.Customer AS CI", SpanText(sql, customer));
        Assert.Equal("inferred", internalCustomer.State);
        Assert.Equal("dbo.CustomerInformationView AS v", SpanText(sql, internalCustomer));
    }

    [Fact]
    public void Mstvf_and_table_scan_link_to_the_function_invocation()
    {
        const string sql = "SELECT r.Id FROM dbo.GetRows(@parentId) AS r;";
        var built = BuildGraph(sql, Operator("Table-valued function", FunctionObject("GetRows", "r"),
            [Operator("Table Scan", FunctionObject("GetRows", "r"))]));

        var links = TableLinks(built.SourceMap);
        Assert.Collection(links, _ => { }, _ => { });
        Assert.All(links, link => Assert.Equal("exact", link.State));
        Assert.All(links, link => Assert.Equal("dbo.GetRows(@parentId) AS r", SpanText(sql, link)));
    }

    [Fact]
    public void Non_inlined_scalar_udf_links_compute_scalar_to_call_span()
    {
        const string sql = "SELECT dbo.NormalizeName(c.Name) AS display_name FROM dbo.Customer AS c;";
        var built = BuildGraph(sql, ComputeScalar("[dbo].[NormalizeName]([c].[Name])", "[dbo].[NormalizeName]",
            Scan("Customer", "c")));

        var compute = Assert.Single(built.Graph.Nodes, node => node.PhysicalOperator == "Compute Scalar");
        var link = Assert.Single(built.SourceMap.Links, candidate => candidate.NodeId == compute.Id
            && candidate.Rule.Contains("UserDefinedFunction", StringComparison.Ordinal));
        Assert.Equal("exact", link.State);
        Assert.Equal("dbo.NormalizeName(c.Name)", SpanText(sql, link));
        Assert.Contains("UserDefinedFunction", link.Rule, StringComparison.Ordinal);
    }

    [Fact]
    public void Compute_scalar_without_user_defined_function_marker_does_not_claim_a_call()
    {
        const string sql = "SELECT ISNULL(c.Name, '') AS display_name FROM dbo.Customer AS c;";
        var built = BuildGraph(sql, Operator("Compute Scalar", null,
            [Scan("Customer", "c")], scalarExpression: "isnull([c].[Name], '')"));

        var compute = Assert.Single(built.Graph.Nodes, node => node.PhysicalOperator == "Compute Scalar");
        Assert.DoesNotContain(TableLinks(built.SourceMap), link => link.NodeId == compute.Id);
    }

    [Fact]
    public void Eliminated_branch_has_no_link()
    {
        const string sql = "SELECT c.Id FROM dbo.Customer AS c LEFT JOIN dbo.OptionalDetail AS d ON d.CustomerId = c.Id WHERE d.IsActive = 1;";
        var built = BuildGraph(sql, Scan("Customer", "c"));

        var customer = Assert.Single(TableLinks(built.SourceMap), link => link.NodeId == Node(built.Graph, "Customer").Id);
        var eliminated = Assert.Single(TableLinks(built.SourceMap), link => link.NodeId is null && link.State == "none");
        Assert.Equal("dbo.Customer AS c", SpanText(sql, customer));
        Assert.Equal("dbo.OptionalDetail AS d", SpanText(sql, eliminated));
        Assert.Contains("no matching plan object", eliminated.Rule, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Seek_and_key_lookup_both_link_to_one_reference()
    {
        const string sql = "SELECT c.Name FROM dbo.Customer AS c WHERE c.Id = @id;";
        var built = BuildGraph(sql, Join(
            Operator("Index Seek", TableObject("Customer", "c", 1)),
            Operator("Key Lookup", TableObject("Customer", "c", -1))));

        var links = TableLinks(built.SourceMap);
        Assert.Collection(links, _ => { }, _ => { });
        Assert.Single(links.Select(link => link.SpanStart).Distinct());
        Assert.All(links, link => Assert.Equal("dbo.Customer AS c", SpanText(sql, link)));
        Assert.All(links, link => Assert.Equal("exact", link.State));
    }

    [Fact]
    public void Multiple_unresolved_modules_remain_ambiguous()
    {
        const string sql = "SELECT a.Id FROM dbo.FirstView AS a JOIN dbo.SecondView AS b ON a.Id = b.Id;";
        var built = BuildGraph(sql, Scan("InternalEntity", "internal"));

        var links = TableLinks(built.SourceMap).Where(link => link.NodeId == Node(built.Graph, "InternalEntity").Id).ToArray();
        Assert.Collection(links,
            link => Assert.Equal("ambiguous", link.State),
            link => Assert.Equal("ambiguous", link.State));
        Assert.NotEqual(links[0].SpanStart, links[1].SpanStart);
    }

    [Fact]
    public void Apply_function_links_to_its_call_site()
    {
        const string sql = "SELECT r.Id FROM dbo.Customer AS c CROSS APPLY dbo.GetRows(c.Id) AS r;";
        var built = BuildGraph(sql, Join(
            Scan("Customer", "c"),
            Operator("Table-valued function", FunctionObject("GetRows", "r"))));

        var function = Assert.Single(TableLinks(built.SourceMap), link => link.NodeId == Node(built.Graph, "GetRows").Id);
        Assert.Equal("exact", function.State);
        Assert.Equal("dbo.GetRows(c.Id) AS r", SpanText(sql, function));
    }

    [Fact]
    public void Dml_target_is_included_in_source_map()
    {
        const string sql = "DELETE FROM dbo.Customer WHERE Id = 1;";
        var built = BuildGraph(sql, Scan("Customer", alias: null));

        var link = Assert.Single(TableLinks(built.SourceMap), candidate => candidate.NodeId == Node(built.Graph, "Customer").Id);
        Assert.Equal("exact", link.State);
        Assert.Contains("dbo.Customer", SpanText(sql, link), StringComparison.Ordinal);
    }

    private static BuiltPlan BuildGraph(string sql, SyntheticOperator root, bool includeUdfMarker = true)
    {
        var document = PlanDocument(sql, root, includeUdfMarker);
        var result = PlanGraphBuilder.Build(document);
        Assert.Null(result.Error);
        var graph = Assert.IsType<PlanGraph>(result.Graph);
        var pairs = StatementSpanPairer.Pair(sql, document);
        var spans = pairs.Select((pair, index) => new SourceMapStatementSpan(
            index, pair.SpanStart, pair.SpanLength, pair.MatchKind)).ToArray();
        var sourceMap = SourceMapBuilder.Build(sql, document, graph, spans);
        Assert.Equal("sqlserver", sourceMap.Engine);
        Assert.Equal(1, sourceMap.AlgorithmVersion);
        return new BuiltPlan(graph, sourceMap);
    }

    private static IReadOnlyList<SourceLink> TableLinks(SourceMapResult sourceMap) =>
        sourceMap.Links.Where(link => link.Kind == "table_reference").ToArray();

    private static PlanGraphNode Node(PlanGraph graph, string table) =>
        Assert.Single(graph.Nodes, node => node.Object?.Table == table);

    private static string SpanText(string sql, SourceLink link)
    {
        Assert.NotNull(link.SpanStart);
        Assert.NotNull(link.SpanLength);
        return sql.Substring(link.SpanStart!.Value, link.SpanLength!.Value);
    }

    private static SyntheticOperator Join(params SyntheticOperator[] children) => Operator("Nested Loops", null, children);

    private static SyntheticOperator Scan(string table, string? alias, int? tableReferenceId = null) =>
        Operator("Index Scan", TableObject(table, alias, tableReferenceId));

    private static SyntheticOperator ComputeScalar(string expression, string functionName, params SyntheticOperator[] children) =>
        Operator("Compute Scalar", null, children, expression, functionName);

    private static SyntheticOperator Operator(
        string physicalOperator,
        SyntheticObject? objectReference,
        SyntheticOperator[]? children = null,
        string? scalarExpression = null,
        string? userDefinedFunction = null) =>
        new(physicalOperator, objectReference, children ?? [], scalarExpression, userDefinedFunction);

    private static SyntheticObject TableObject(string table, string? alias, int? tableReferenceId = null) =>
        new("dbo", table, alias, tableReferenceId);

    private static SyntheticObject FunctionObject(string table, string? alias) =>
        new("dbo", table, alias, null);

    private static string PlanDocument(string statementText, SyntheticOperator root, bool includeUdfMarker = true)
    {
        var statement = new XElement(Showplan + "StmtSimple",
            new XAttribute("StatementId", 1),
            new XAttribute("StatementCompId", 1),
            new XAttribute("StatementText", statementText),
            new XElement(Showplan + "QueryPlan", BuildRelOp(root, includeUdfMarker, 0)));
        return new XDocument(new XElement(Showplan + "ShowPlanXML",
            new XElement(Showplan + "BatchSequence",
                new XElement(Showplan + "Batch",
                    new XElement(Showplan + "Statements", statement))))).ToString(SaveOptions.DisableFormatting);
    }

    private static XElement BuildRelOp(SyntheticOperator item, bool includeUdfMarker, int nodeId)
    {
        var attributes = new List<object>
        {
            new XAttribute("NodeId", nodeId),
            new XAttribute("PhysicalOp", item.PhysicalOperator),
            new XAttribute("LogicalOp", item.PhysicalOperator),
        };
        var operatorNode = new XElement(Showplan + "Operator");
        if (item.Object is { } obj)
        {
            var objectAttributes = new List<object>
            {
                new XAttribute("Database", "[db]"),
                new XAttribute("Schema", "[" + obj.Schema + "]"),
                new XAttribute("Table", "[" + obj.Table + "]"),
            };
            if (obj.Alias is not null)
                objectAttributes.Add(new XAttribute("Alias", "[" + obj.Alias + "]"));
            if (obj.TableReferenceId is not null)
                objectAttributes.Add(new XAttribute("TableReferenceId", obj.TableReferenceId.Value));
            operatorNode.Add(new XElement(Showplan + "Object", objectAttributes));
        }

        if (item.ScalarExpression is not null)
        {
            var scalar = new XElement(Showplan + "ScalarOperator", new XAttribute("ScalarString", item.ScalarExpression));
            if (includeUdfMarker && item.UserDefinedFunction is not null)
                scalar.Add(new XElement(Showplan + "UserDefinedFunction", new XAttribute("FunctionName", item.UserDefinedFunction)));
            operatorNode.Add(new XElement(Showplan + "DefinedValues",
                new XElement(Showplan + "DefinedValue", scalar)));
        }

        for (var index = 0; index < item.Children.Length; index++)
            operatorNode.Add(BuildRelOp(item.Children[index], includeUdfMarker, nodeId + index + 1));

        var name = item.PhysicalOperator.Replace(" ", string.Empty, StringComparison.Ordinal);
        return new XElement(Showplan + "RelOp", attributes, new XElement(Showplan + name, operatorNode.Nodes()));
    }

    private sealed record SyntheticOperator(
        string PhysicalOperator,
        SyntheticObject? Object,
        SyntheticOperator[] Children,
        string? ScalarExpression,
        string? UserDefinedFunction);

    private sealed record SyntheticObject(string Schema, string Table, string? Alias, int? TableReferenceId);

    private sealed record BuiltPlan(PlanGraph Graph, SourceMapResult SourceMap);
}