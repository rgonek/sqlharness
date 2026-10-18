using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace SqlHarness.Core;

/// <summary>Builds renderer-neutral SQL Server operator graphs from bounded Showplan documents.</summary>
public static class PlanGraphBuilder
{
    private const string EngineName = "sqlserver";
    private const string InvalidPlanCode = "invalid_or_over_limit_plan";
    private const string InvalidPlanMessage = "The plan document is invalid or exceeds supported limits.";
    private const string ShowplanNamespace = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private static readonly XNamespace Showplan = ShowplanNamespace;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Builds the complete graph or an explicit error state; nodes and predicates are never clipped.</summary>
    public static PlanGraphBuildResult Build(string? showplanXml) => Build(showplanXml, DefaultLimits);

    internal static PlanGraphBuildResult Build(string? showplanXml, PlanGraphBuilderLimits limits)
    {
        try
        {
            var document = PlanDistiller.LoadValidatedDocument(showplanXml, limits.Document);
            var statements = ReadStatementRoots(document);
            var graphStatements = new List<PlanGraphStatement>(statements.Count);
            var nodes = new List<PlanGraphNode>();
            var edges = new List<PlanGraphEdge>();

            for (var statementIndex = 0; statementIndex < statements.Count; statementIndex++)
            {
                var statement = statements[statementIndex];
                var rootId = NodeId(statementIndex, "0");
                var rootCost = Number(statement.Root, "EstimatedTotalSubtreeCost");
                graphStatements.Add(new PlanGraphStatement(
                    statementIndex,
                    OptionalInteger(statement.Element, "StatementId"),
                    OptionalInteger(statement.Element, "StatementCompId"),
                    rootId));

                AppendNode(statement.Root, statementIndex, "0", rootCost, parentId: null);
            }

            var graph = new PlanGraph(EngineName, graphStatements, nodes, edges);
            var graphBytes = JsonSerializer.SerializeToUtf8Bytes(graph, JsonOptions);
            if (graphBytes.Length > limits.MaximumGraphBytes)
                return Failure("graph_size_limit_exceeded", "The complete plan graph exceeds the response size limit.");

            return new PlanGraphBuildResult(EngineName, graph, null);

            void AppendNode(XElement relOp, int statementIndex, string path, double? rootCost, string? parentId)
            {
                if (limits.MaximumNodes <= 0 || nodes.Count >= limits.MaximumNodes)
                    throw new PlanGraphFailure("graph_node_limit_exceeded", "The complete plan graph exceeds the node limit.");

                var owned = OwnedElements(relOp).ToArray();
                var objectElement = owned.FirstOrDefault(element => element.Name == Showplan + "Object");
                var runtime = owned.FirstOrDefault(element => element.Name == Showplan + "RunTimeInformation");
                var counters = runtime?.Elements(Showplan + "RunTimeCountersPerThread").ToArray();
                var estimatedRows = Number(relOp, "EstimateRows");
                var estimatedRowSize = Number(relOp, "AvgRowSize");
                var actualRows = counters is null ? null : Sum(counters, "ActualRows");
                var executions = counters is null ? null : Sum(counters, "ActualExecutions");
                var nodeCost = Number(relOp, "EstimatedTotalSubtreeCost");
                var costFraction = rootCost is > 0 && nodeCost is not null ? nodeCost / rootCost : null;
                if (costFraction is not null && !double.IsFinite(costFraction.Value))
                    throw SafetyFailure();
                var objectReference = objectElement is null ? null : new PlanGraphObject(
                    PlanDistiller.Unbracket(Attribute(objectElement, "Database")),
                    PlanDistiller.Unbracket(Attribute(objectElement, "Schema")),
                    PlanDistiller.Unbracket(Attribute(objectElement, "Table")),
                    PlanDistiller.Unbracket(Attribute(objectElement, "Index")),
                    PlanDistiller.Unbracket(Attribute(objectElement, "Alias")));
                var nodeId = NodeId(statementIndex, path);
                var node = new PlanGraphNode(
                    nodeId,
                    statementIndex,
                    Integer(relOp, "NodeId"),
                    Attribute(relOp, "PhysicalOp") ?? "Unknown operator",
                    Attribute(relOp, "LogicalOp"),
                    objectReference,
                    objectElement is null ? null : Integer(objectElement, "TableReferenceId"),
                    ParsePredicates(owned),
                    ParseDefinedValues(owned),
                    estimatedRows,
                    actualRows,
                    executions,
                    costFraction,
                    ParseWarnings(owned));
                nodes.Add(node);

                if (parentId is not null)
                {
                    double? estimatedSize = estimatedRows is not null && estimatedRowSize is not null
                        ? estimatedRows.Value * estimatedRowSize.Value
                        : null;
                    if (estimatedSize is not null && !double.IsFinite(estimatedSize.Value))
                        throw SafetyFailure();
                    edges.Add(new PlanGraphEdge(parentId, nodeId, estimatedRows, actualRows, estimatedSize));
                }

                var children = DirectChildOperators(relOp).ToArray();
                for (var childIndex = 0; childIndex < children.Length; childIndex++)
                    AppendNode(children[childIndex], statementIndex, path + "/" + childIndex, rootCost, nodeId);
            }
        }
        catch (PlanGraphFailure failure)
        {
            return Failure(failure.Code, failure.Message);
        }
        catch (SqlHarnessSafetyException)
        {
            return Failure(InvalidPlanCode, InvalidPlanMessage);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            return Failure(InvalidPlanCode, InvalidPlanMessage);
        }
    }

    /// <summary>Returns one operator's Showplan properties independently from the graph response.</summary>
    public static PlanGraphPropertyResult BuildProperties(string? showplanXml, string? nodeId) =>
        BuildProperties(showplanXml, nodeId, DefaultLimits);

    internal static PlanGraphPropertyResult BuildProperties(
        string? showplanXml,
        string? nodeId,
        PlanGraphBuilderLimits limits)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || !TryParseNodeId(nodeId, out var statementIndex, out var path))
            return PropertyFailure(nodeId ?? string.Empty, "node_not_found", "The requested plan node does not exist.");

        try
        {
            var document = PlanDistiller.LoadValidatedDocument(showplanXml, limits.Document);
            var statements = ReadStatementRoots(document);
            if (statementIndex >= statements.Count)
                return PropertyFailure(nodeId, "node_not_found", "The requested plan node does not exist.");

            var relOp = statements[statementIndex].Root;
            foreach (var childIndex in path.Skip(1))
            {
                var children = DirectChildOperators(relOp).ToArray();
                if (childIndex >= children.Length)
                    return PropertyFailure(nodeId, "node_not_found", "The requested plan node does not exist.");
                relOp = children[childIndex];
            }

            if (limits.MaximumPropertyResponseBytes <= 0)
                return PropertyFailure(nodeId, "property_tree_size_limit_exceeded", "The property response size limit is too small.");

            var tree = BuildBoundedPropertyTree(relOp, nodeId, limits.MaximumPropertyResponseBytes);
            var result = new PlanGraphPropertyResult(EngineName, nodeId, tree, null);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
            if (bytes.Length > limits.MaximumPropertyResponseBytes)
                return PropertyFailure(nodeId, "property_tree_size_limit_exceeded", "The property response exceeds the response size limit.");

            return result;
        }
        catch (SqlHarnessSafetyException)
        {
            return PropertyFailure(nodeId, InvalidPlanCode, InvalidPlanMessage);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            return PropertyFailure(nodeId, InvalidPlanCode, InvalidPlanMessage);
        }
    }

    private static PlanGraphPropertyTree BuildBoundedPropertyTree(XElement relOp, string nodeId, int maximumResponseBytes)
    {
        var entries = new List<(PlanGraphPropertyNode Node, int Depth)>();
        foreach (var child in relOp.Elements().Where(element => element.Name != Showplan + "RelOp"))
            Collect(child, 1);

        var root = new MutablePropertyNode(PropertyNode(relOp));
        var emptyTree = new PlanGraphPropertyTree([], false, 0);
        var emptyTreeBytes = JsonSerializer.SerializeToUtf8Bytes(emptyTree, JsonOptions).Length;
        var emptyResponseBytes = JsonSerializer.SerializeToUtf8Bytes(
            new PlanGraphPropertyResult(EngineName, nodeId, emptyTree, null),
            JsonOptions).Length;
        var responseEnvelopeBytes = emptyResponseBytes - emptyTreeBytes;
        var maximumTreeBytes = maximumResponseBytes - responseEnvelopeBytes;
        var usedBytes = emptyTreeBytes
            + NodeSize(root.Freeze())
            + 16; // Reserve exact JSON field overhead for the final truncation flag and omitted count.

        if (usedBytes > maximumTreeBytes)
            return MarkerOnlyPropertyTree(entries.Count, maximumTreeBytes);

        var stack = new List<MutablePropertyNode> { root };
        var truncated = false;
        var omitted = 0;
        for (var index = 0; index < entries.Count; index++)
        {
            var (property, depth) = entries[index];
            while (stack.Count > depth)
                stack.RemoveAt(stack.Count - 1);

            var parent = stack[depth - 1];
            var separatorBytes = parent.Children.Count == 0 ? 0 : 1;
            var candidateBytes = separatorBytes + NodeSize(property);
            var omittedIfStopped = entries.Count - index;
            var marker = TruncationMarker(omittedIfStopped);
            var markerBytes = (root.Children.Count == 0 ? 0 : 1) + NodeSize(marker);
            var reserveMarker = index == entries.Count - 1 ? 0 : markerBytes;
            if (usedBytes + candidateBytes + reserveMarker > maximumTreeBytes)
            {
                truncated = true;
                omitted = omittedIfStopped;
                root.Children.Add(new MutablePropertyNode(marker));
                usedBytes += markerBytes;
                break;
            }

            var child = new MutablePropertyNode(property);
            parent.Children.Add(child);
            stack.Add(child);
            usedBytes += candidateBytes;
        }

        var tree = new PlanGraphPropertyTree([root.Freeze()], truncated, omitted);
        if (JsonSerializer.SerializeToUtf8Bytes(tree, JsonOptions).Length <= maximumTreeBytes)
            return tree;

        return MarkerOnlyPropertyTree(entries.Count, maximumTreeBytes);

        void Collect(XElement element, int depth)
        {
            entries.Add((PropertyNode(element), depth));
            foreach (var child in element.Elements().Where(child => child.Name != Showplan + "RelOp"))
                Collect(child, depth + 1);
        }
    }

    private static PlanGraphPropertyTree MarkerOnlyPropertyTree(int omittedCount, int maximumTreeBytes)
    {
        var root = new PlanGraphPropertyNode("RelOp", [], [TruncationMarker(omittedCount)]);
        var tree = new PlanGraphPropertyTree([root], true, omittedCount);
        if (JsonSerializer.SerializeToUtf8Bytes(tree, JsonOptions).Length <= maximumTreeBytes)
            return tree;

        return new PlanGraphPropertyTree([TruncationMarker(omittedCount)], true, omittedCount);
    }

    private static PlanGraphPropertyNode TruncationMarker(int omittedCount) =>
        new("Truncated", [], [], IsTruncationMarker: true, OmittedNodeCount: omittedCount);

    private static PlanGraphPropertyNode PropertyNode(XElement element)
    {
        var attributes = element.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .Select(attribute => new PlanGraphPropertyAttribute(QualifiedName(attribute.Name), attribute.Value))
            .OrderBy(attribute => attribute.Name, StringComparer.Ordinal)
            .ToArray();
        var text = element.HasElements ? null : NonBlank(element.Value);
        return new PlanGraphPropertyNode(QualifiedName(element.Name), attributes, [], Text: text);
    }

    private static int NodeSize(PlanGraphPropertyNode node) =>
        JsonSerializer.SerializeToUtf8Bytes(node, JsonOptions).Length;

    private static string? NonBlank(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string QualifiedName(XName name) => name.NamespaceName == ShowplanNamespace
        ? name.LocalName
        : name.NamespaceName.Length == 0 ? name.LocalName : name.NamespaceName + "::" + name.LocalName;

    private static List<(XElement Element, XElement Root)> ReadStatementRoots(XDocument document)
    {
        var statements = new List<(XElement Element, XElement Root)>();
        foreach (var statement in document.Descendants(Showplan + "StmtSimple"))
        {
            var queryPlan = statement.Elements(Showplan + "QueryPlan").FirstOrDefault()
                ?? throw SafetyFailure();
            var root = queryPlan.Elements(Showplan + "RelOp").FirstOrDefault()
                ?? throw SafetyFailure();
            statements.Add((statement, root));
        }
        return statements;
    }

    private static IReadOnlyList<string> ParsePredicates(IReadOnlyList<XElement> owned) =>
        owned.Where(element => element.Name == Showplan + "SeekPredicates" || element.Name == Showplan + "Predicate")
            .SelectMany(element => element.DescendantsAndSelf(Showplan + "ScalarOperator"))
            .Select(element => Attribute(element, "ScalarString"))
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();

    private static IReadOnlyList<PlanGraphDefinedValue> ParseDefinedValues(IReadOnlyList<XElement> owned) =>
        owned.Where(element => element.Name == Showplan + "DefinedValues")
            .SelectMany(container => container.Elements(Showplan + "DefinedValue"))
            .Select(value => new PlanGraphDefinedValue(
                value.DescendantsAndSelf(Showplan + "ColumnReference").Select(ParseColumnReference).ToArray(),
                value.DescendantsAndSelf(Showplan + "ScalarOperator")
                    .Select(element => Attribute(element, "ScalarString"))
                    .Where(expression => expression is not null)
                    .Cast<string>()
                    .ToArray()))
            .ToArray();

    private static PlanGraphColumnReference ParseColumnReference(XElement column) => new(
        PlanDistiller.Unbracket(Attribute(column, "Database")),
        PlanDistiller.Unbracket(Attribute(column, "Schema")),
        PlanDistiller.Unbracket(Attribute(column, "Table")),
        PlanDistiller.Unbracket(Attribute(column, "Column")),
        PlanDistiller.Unbracket(Attribute(column, "Alias")));

    private static IReadOnlyList<string> ParseWarnings(IReadOnlyList<XElement> owned)
    {
        var warnings = new List<string>();
        foreach (var warning in owned.Where(element => element.Name == Showplan + "Warnings"))
        {
            warnings.AddRange(warning.Attributes().Where(attribute => attribute.Name.NamespaceName.Length == 0)
                .Select(attribute => attribute.Name.LocalName + "=" + attribute.Value));
            warnings.AddRange(warning.Elements().Where(element => element.Name.NamespaceName == ShowplanNamespace)
                .Select(FormatWarningElement));
        }
        return warnings.Order(StringComparer.Ordinal).ToArray();
    }

    private static string FormatWarningElement(XElement element)
    {
        var attributes = element.Attributes()
            .Where(attribute => attribute.Name.NamespaceName.Length == 0)
            .OrderBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal)
            .Select(attribute => attribute.Name.LocalName + "=" + attribute.Value);
        var suffix = string.Join(';', attributes);
        return suffix.Length == 0 ? element.Name.LocalName : element.Name.LocalName + "[" + suffix + "]";
    }

    private static IEnumerable<XElement> OwnedElements(XElement relOp)
    {
        foreach (var child in relOp.Elements())
        {
            if (child.Name == Showplan + "RelOp")
                continue;

            yield return child;
            foreach (var descendant in OwnedElements(child))
                yield return descendant;
        }
    }

    private static IEnumerable<XElement> DirectChildOperators(XElement relOp)
    {
        foreach (var child in relOp.Elements())
        {
            if (child.Name == Showplan + "RelOp")
            {
                yield return child;
                continue;
            }

            foreach (var descendant in DirectChildOperators(child))
                yield return descendant;
        }
    }

    private static long? Sum(XElement[] counters, string attributeName)
    {
        long total = 0;
        foreach (var counter in counters)
        {
            var value = Attribute(counter, attributeName);
            if (value is null || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                throw SafetyFailure();
            total = checked(total + parsed);
        }
        return total;
    }

    private static double? Number(XElement element, string attributeName)
    {
        var value = Attribute(element, attributeName);
        if (value is null)
            return null;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed))
            throw SafetyFailure();
        return parsed;
    }

    private static int? Integer(XElement element, string attributeName)
    {
        var value = Attribute(element, attributeName);
        if (value is null)
            return null;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw SafetyFailure();
        return parsed;
    }

    private static int? OptionalInteger(XElement element, string attributeName)
    {
        var value = Attribute(element, attributeName);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string? Attribute(XElement element, string attributeName) => element.Attribute(attributeName)?.Value;

    private static string NodeId(int statementIndex, string path) => statementIndex.ToString(CultureInfo.InvariantCulture) + ":" + path;

    private static bool TryParseNodeId(string nodeId, out int statementIndex, out int[] path)
    {
        statementIndex = 0;
        path = [];
        var separator = nodeId.IndexOf(':');
        if (separator <= 0
            || !int.TryParse(nodeId.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out statementIndex)
            || statementIndex < 0)
            return false;

        var pathParts = nodeId[(separator + 1)..].Split('/');
        if (pathParts.Length == 0
            || pathParts[0] != "0"
            || pathParts.Any(part => !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            return false;

        path = pathParts.Select(part => int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray();
        return path.Length > 0;
    }

    private static SqlHarnessSafetyException SafetyFailure() =>
        new("The execution plan is not a valid SQL Server Showplan document.");

    private static PlanGraphBuildResult Failure(string code, string message) =>
        new(EngineName, null, new PlanGraphBuildError(code, message));

    private static PlanGraphPropertyResult PropertyFailure(string nodeId, string code, string message) =>
        new(EngineName, nodeId, null, new PlanGraphBuildError(code, message));

    private static PlanGraphBuilderLimits DefaultLimits { get; } = new(
        PlanDistiller.DefaultLimits,
        MaximumNodes: 10_000,
        MaximumGraphBytes: 1024 * 1024,
        MaximumPropertyResponseBytes: 128 * 1024);

    private sealed class MutablePropertyNode(PlanGraphPropertyNode value)
    {
        internal List<MutablePropertyNode> Children { get; } = [];

        internal PlanGraphPropertyNode Freeze() => value with { Children = Children.Select(child => child.Freeze()).ToArray() };
    }

    private sealed class PlanGraphFailure(string code, string message) : Exception(message)
    {
        internal string Code { get; } = code;
    }
}
