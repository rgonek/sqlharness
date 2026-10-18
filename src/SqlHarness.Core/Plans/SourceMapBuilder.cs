using System.Globalization;
using System.Xml.Linq;

using Microsoft.SqlServer.TransactSql.ScriptDom;

using SqlHarness.Core.Dialect;

namespace SqlHarness.Core;

/// <summary>A versioned set of links between SQL Server plan operators and executed SQL spans.</summary>
public sealed record SourceMapResult(
    string Engine,
    int AlgorithmVersion,
    IReadOnlyList<SourceLink> Links);

/// <summary>A plan-node/source-span link with an explicit confidence state and explanation.</summary>
public sealed record SourceLink(
    string? NodeId,
    int? SpanStart,
    int? SpanLength,
    string Kind,
    string State,
    string Rule);

/// <summary>The persisted executed-text span associated with one plan statement.</summary>
public sealed record SourceMapStatementSpan(
    int StatementIndex,
    int? SpanStart,
    int? SpanLength,
    string MatchKind);

/// <summary>Builds conservative SQL-to-operator links from ScriptDom scopes and a validated Showplan document.</summary>
public static class SourceMapBuilder
{
    public const int CurrentAlgorithmVersion = 1;

    private const string SqlServerEngine = "sqlserver";
    private const string ShowplanNamespace = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private static readonly XNamespace Showplan = ShowplanNamespace;

    /// <summary>Maps statement and table-reference source spans without fetching module definitions or inventing matches.</summary>
    public static SourceMapResult Build(
        string? executedText,
        string? showplanXml,
        PlanGraph graph,
        IReadOnlyList<SourceMapStatementSpan> statementSpans)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(statementSpans);

        var links = new List<SourceLink>();
        if (!string.Equals(graph.Engine, SqlServerEngine, StringComparison.Ordinal)
            || string.IsNullOrEmpty(executedText))
            return new SourceMapResult(graph.Engine, CurrentAlgorithmVersion, links);

        var parsed = SqlServerDocument.Parse(executedText);
        if (parsed.HasErrors || parsed.Fragment is not TSqlScript script)
        {
            AddUnmatchedStatementLinks(graph.Nodes, links, "executed SQL could not be parsed by ScriptDom");
            return new SourceMapResult(graph.Engine, CurrentAlgorithmVersion, links);
        }

        var sqlStatements = script.Batches.SelectMany(batch => batch.Statements).ToArray();
        var planDocument = TryLoadPlan(showplanXml);
        var udfMarkers = planDocument is null
            ? new Dictionary<string, IReadOnlyList<FunctionName>>(StringComparer.Ordinal)
            : ReadUdfMarkers(planDocument, graph);
        var spansByStatement = statementSpans.GroupBy(span => span.StatementIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());

        foreach (var statement in graph.Statements.OrderBy(statement => statement.Index))
        {
            var statementNodes = graph.Nodes.Where(node => node.StatementIndex == statement.Index).ToArray();
            if (statementNodes.Length == 0)
                continue;

            if (!spansByStatement.TryGetValue(statement.Index, out var spanRows) || spanRows.Length != 1)
            {
                var state = spanRows is { Length: > 1 } ? "ambiguous" : "none";
                var rule = state == "ambiguous"
                    ? "multiple persisted source spans refer to this plan statement"
                    : "no persisted source span refers to this plan statement";
                AddStatementLinks(statementNodes, null, null, state, rule, links);
                continue;
            }

            var statementSpan = spanRows[0];
            var pairState = PairState(statementSpan.MatchKind);
            if (pairState is "none" or "ambiguous"
                || !ValidSpan(executedText, statementSpan.SpanStart, statementSpan.SpanLength))
            {
                var rule = pairState == "ambiguous"
                    ? "statement pairing is ambiguous"
                    : "statement pairing did not produce a usable executed-text span";
                AddStatementLinks(statementNodes, null, null, pairState, rule, links);
                continue;
            }

            var astStatement = sqlStatements.FirstOrDefault(candidate =>
                candidate.StartOffset == statementSpan.SpanStart
                && candidate.FragmentLength == statementSpan.SpanLength);
            if (astStatement is null)
            {
                AddStatementLinks(statementNodes, null, null, "none", "persisted span does not identify one ScriptDom statement", links);
                continue;
            }

            var statementRule = "statement paired to executed text by " + statementSpan.MatchKind;
            AddStatementLinks(statementNodes, statementSpan.SpanStart, statementSpan.SpanLength, pairState, statementRule, links);

            var collector = new SourceScopeCollector();
            astStatement.Accept(collector);
            ResolveTableReferences(
                executedText,
                statementSpan,
                statementNodes,
                graph,
                collector.TableReferences,
                collector.QueryScopeIds,
                pairState,
                links);
            ResolveScalarUdfCalls(
                executedText,
                statementNodes,
                collector.ScalarCalls,
                udfMarkers,
                pairState,
                links);
        }

        return new SourceMapResult(graph.Engine, CurrentAlgorithmVersion, links);
    }

    private static void ResolveTableReferences(
        string executedText,
        SourceMapStatementSpan statementSpan,
        IReadOnlyList<PlanGraphNode> statementNodes,
        PlanGraph graph,
        IReadOnlyList<SourceReference> references,
        IReadOnlyList<int> queryScopeIds,
        string statementState,
        List<SourceLink> links)
    {
        var linkedReferenceIds = new HashSet<int>();
        var directlyMatchedSpans = new HashSet<(int Start, int Length)>();
        var resolvedByNode = new Dictionary<string, SourceReference>(StringComparer.Ordinal);
        var unresolved = new List<PlanGraphNode>();

        foreach (var node in statementNodes.Where(node => node.Object is not null && !IsKeyLookupWithMinusOne(node)))
        {
            var match = MatchObject(node, references, node.TableReferenceId, statementNodes);
            if (match.Candidates.Count == 0)
            {
                if (HasObjectName(node.Object))
                    unresolved.Add(node);
                continue;
            }

            AddReferenceMatches(node, match, statementState, linkedReferenceIds, directlyMatchedSpans, resolvedByNode, links);
        }

        foreach (var node in statementNodes.Where(IsKeyLookupWithMinusOne))
        {
            var objectMatches = MatchObject(node, references, tableReferenceId: null, statementNodes);
            if (objectMatches.Candidates.Count == 0)
            {
                if (HasObjectName(node.Object))
                    unresolved.Add(node);
                continue;
            }

            if (objectMatches.Candidates.Count == 1)
            {
                AddReferenceMatches(node, objectMatches, statementState, linkedReferenceIds, directlyMatchedSpans, resolvedByNode, links);
                continue;
            }

            var siblingSeek = FindNearestResolvedSeek(node, graph, statementNodes, resolvedByNode);
            if (siblingSeek.Candidates.Count > 0)
            {
                AddReferenceMatches(node, siblingSeek, statementState, linkedReferenceIds, directlyMatchedSpans, resolvedByNode, links);
                continue;
            }

            var byAlias = MatchObject(node, references, tableReferenceId: null, statementNodes, aliasOnly: true);
            if (byAlias.Candidates.Count > 0)
            {
                AddReferenceMatches(node, byAlias, statementState, linkedReferenceIds, directlyMatchedSpans, resolvedByNode, links);
                continue;
            }

            AddReferenceMatches(node, objectMatches with
            {
                IsExact = false,
                Rule = "TableReferenceId -1 has no unique related seek; matching references remain ambiguous",
            }, statementState, linkedReferenceIds, directlyMatchedSpans, resolvedByNode, links);
        }

        foreach (var node in unresolved)
        {
            var objectName = node.Object?.Table;
            var unresolvedModuleCandidates = references
                .Where(reference => !SurfaceSpans(reference).Any(span => directlyMatchedSpans.Contains((span.Start, span.Length)))
                    && reference.IsPotentialModule
                    && !IdentifierEquals(reference.Table, objectName))
                .ToArray();

            var scopeId = ResolveSingleQueryScope(queryScopeIds);
            var moduleCandidates = scopeId is null
                ? unresolvedModuleCandidates
                : unresolvedModuleCandidates.Where(reference => reference.IncludesScope(scopeId.Value)).ToArray();

            if (scopeId is not null && moduleCandidates.Length == 1)
            {
                var candidate = moduleCandidates[0];
                AddLink(node.Id, candidate, "inferred", "plan object is absent from executed SQL; inferred via the single unresolved module reference " + candidate.QualifiedName + " in query scope " + scopeId.Value.ToString(CultureInfo.InvariantCulture),
                    statementState, links);
                linkedReferenceIds.Add(candidate.InstanceId);
                continue;
            }

            if (moduleCandidates.Length > 1)
            {
                foreach (var candidate in moduleCandidates)
                {
                    AddLink(node.Id, candidate, "ambiguous", scopeId is null
                            ? "plan object is absent from executed SQL and its query scope cannot be established"
                            : "plan object is absent from executed SQL and multiple unresolved module references remain in its query scope",
                        statementState, links);
                    linkedReferenceIds.Add(candidate.InstanceId);
                }
                continue;
            }

            if (scopeId is null && unresolvedModuleCandidates.Length > 0)
            {
                foreach (var candidate in unresolvedModuleCandidates)
                {
                    AddLink(node.Id, candidate, "ambiguous", "plan object is absent from executed SQL and its query scope cannot be established",
                        statementState, links);
                    linkedReferenceIds.Add(candidate.InstanceId);
                }
                continue;
            }

            if (scopeId is not null && unresolvedModuleCandidates.Length > 0)
            {
                links.Add(new SourceLink(node.Id, null, null, "table_reference", "none",
                    "no unresolved module reference exists in the plan object's query scope"));
                continue;
            }

            links.Add(new SourceLink(node.Id, null, null, "table_reference", "none",
                "no source reference or module candidate can be established for this plan object"));
        }

        var linkedSpans = references.Where(reference => linkedReferenceIds.Contains(reference.InstanceId))
            .SelectMany(SurfaceSpans)
            .Select(span => (span.Start, span.Length))
            .ToHashSet();
        foreach (var sourceSpan in references.SelectMany(SurfaceSpans).Distinct())
        {
            if (linkedSpans.Contains((sourceSpan.Start, sourceSpan.Length)))
                continue;

            links.Add(new SourceLink(
                NodeId: null,
                sourceSpan.Start,
                sourceSpan.Length,
                Kind: "table_reference",
                State: "none",
                Rule: "no matching plan object survives for this table reference"));
        }
    }

    private static ReferenceMatch MatchObject(
        PlanGraphNode node,
        IReadOnlyList<SourceReference> references,
        int? tableReferenceId,
        IReadOnlyList<PlanGraphNode> statementNodes,
        bool aliasOnly = false)
    {
        if (node.Object is not { } planObject || !HasObjectName(planObject))
            return ReferenceMatch.None;

        var candidates = references.Where(reference => SameObjectName(reference, planObject)
                && (!aliasOnly || IdentifierEquals(reference.Alias, planObject.Alias)))
            .OrderBy(reference => reference.Order)
            .ToArray();
        if (candidates.Length == 0)
            return ReferenceMatch.None;

        if (candidates.Length == 1)
        {
            if (tableReferenceId is > 0 && HasLaterPlanInstanceOfSameObject(node, statementNodes))
                return ReferenceMatch.None;

            return ReferenceMatch.Exact(candidates[0], ObjectMatchRule(node, candidates[0]));
        }

        if (tableReferenceId is > 0 && tableReferenceId <= candidates.Length)
        {
            var selected = candidates[tableReferenceId.Value - 1];
            var cte = selected.CtePath.Count == 0 ? string.Empty : " in expanded CTE " + string.Join(" -> ", selected.CtePath);
            return ReferenceMatch.Exact(selected,
                "TableReferenceId " + tableReferenceId.Value.ToString(CultureInfo.InvariantCulture)
                + " selected the matching table instance" + cte);
        }

        var aliasCandidates = tableReferenceId is > 0 || planObject.Alias is null
            ? []
            : candidates.Where(reference => IdentifierEquals(reference.Alias, planObject.Alias)).ToArray();
        if (aliasCandidates.Length == 1)
        {
            var cte = aliasCandidates[0].CtePath.Count == 0 ? string.Empty : " in expanded CTE " + string.Join(" -> ", aliasCandidates[0].CtePath);
            return ReferenceMatch.Exact(aliasCandidates[0],
                "table name and alias " + planObject.Alias + " selected one source reference" + cte);
        }

        var ambiguous = aliasCandidates.Length > 1 ? aliasCandidates : candidates;
        return ReferenceMatch.Ambiguous(ambiguous,
            "table name matches multiple source references and no unique TableReferenceId or alias settles the match");
    }

    private static bool HasLaterPlanInstanceOfSameObject(PlanGraphNode node, IReadOnlyList<PlanGraphNode> statementNodes) =>
        node.TableReferenceId is > 0
        && statementNodes.Any(candidate => candidate.Id != node.Id
            && candidate.TableReferenceId is int otherId
            && otherId > node.TableReferenceId.Value
            && candidate.Object is { } candidateObject
            && node.Object is { } nodeObject
            && SameBasePlanObject(nodeObject, candidateObject));

    private static bool SameBasePlanObject(PlanGraphObject left, PlanGraphObject right) =>
        IdentifierEquals(left.Table, right.Table)
        && (left.Schema is null || right.Schema is null || IdentifierEquals(left.Schema, right.Schema))
        && (left.Database is null || right.Database is null || IdentifierEquals(left.Database, right.Database));

    private static ReferenceMatch FindNearestResolvedSeek(
        PlanGraphNode lookup,
        PlanGraph graph,
        IReadOnlyList<PlanGraphNode> statementNodes,
        IReadOnlyDictionary<string, SourceReference> resolvedByNode)
    {
        if (lookup.Object is not { } lookupObject)
            return ReferenceMatch.None;

        var distances = DistancesFrom(lookup.Id, graph.Edges);
        var related = statementNodes
            .Where(candidate => candidate.Id != lookup.Id
                && candidate.TableReferenceId is > 0
                && candidate.PhysicalOperator.Contains("seek", StringComparison.OrdinalIgnoreCase)
                && candidate.Object is { } candidateObject
                && SamePlanObject(lookupObject, candidateObject)
                && resolvedByNode.ContainsKey(candidate.Id)
                && distances.ContainsKey(candidate.Id))
            .Select(candidate => (Node: candidate, Distance: distances[candidate.Id], Reference: resolvedByNode[candidate.Id]))
            .ToArray();
        if (related.Length == 0)
            return ReferenceMatch.None;

        var minimumDistance = related.Min(candidate => candidate.Distance);
        var nearest = related.Where(candidate => candidate.Distance == minimumDistance)
            .GroupBy(candidate => candidate.Reference.InstanceId)
            .Select(group => group.First().Reference)
            .ToArray();
        if (nearest.Length == 1)
            return ReferenceMatch.Exact(nearest[0], "TableReferenceId -1 followed the nearest resolved seek for the same table object");

        return ReferenceMatch.Ambiguous(nearest, "TableReferenceId -1 is equally near seeks mapped to different source references");
    }

    private static Dictionary<string, int> DistancesFrom(string nodeId, IReadOnlyList<PlanGraphEdge> edges)
    {
        var adjacent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (!adjacent.TryGetValue(edge.Source, out var sourceNeighbors))
            {
                sourceNeighbors = [];
                adjacent.Add(edge.Source, sourceNeighbors);
            }
            if (!adjacent.TryGetValue(edge.Target, out var targetNeighbors))
            {
                targetNeighbors = [];
                adjacent.Add(edge.Target, targetNeighbors);
            }
            sourceNeighbors.Add(edge.Target);
            targetNeighbors.Add(edge.Source);
        }

        var distances = new Dictionary<string, int>(StringComparer.Ordinal) { [nodeId] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue(nodeId);
        while (queue.TryDequeue(out var current))
        {
            if (!adjacent.TryGetValue(current, out var neighbors))
                continue;
            foreach (var neighbor in neighbors)
            {
                if (distances.ContainsKey(neighbor))
                    continue;
                distances[neighbor] = distances[current] + 1;
                queue.Enqueue(neighbor);
            }
        }
        return distances;
    }

    private static int? ResolveSingleQueryScope(IReadOnlyList<int> queryScopeIds)
    {
        var distinctScopes = queryScopeIds.Distinct().ToArray();
        return distinctScopes.Length == 1 ? distinctScopes[0] : null;
    }

    private static void AddReferenceMatches(
        PlanGraphNode node,
        ReferenceMatch match,
        string statementState,
        HashSet<int> linkedReferenceIds,
        HashSet<(int Start, int Length)> directlyMatchedSpans,
        Dictionary<string, SourceReference> resolvedByNode,
        List<SourceLink> links)
    {
        var state = match.IsExact ? CombineConfidence("exact", statementState) : "ambiguous";
        foreach (var candidate in match.Candidates)
        {
            AddLink(node.Id, candidate, state, match.Rule, statementState, links);
            linkedReferenceIds.Add(candidate.InstanceId);
            directlyMatchedSpans.UnionWith(SurfaceSpans(candidate));
        }
        if (match.IsExact && match.Candidates.Count == 1)
            resolvedByNode[node.Id] = match.Candidates[0];
    }

    private static void ResolveScalarUdfCalls(
        string executedText,
        IReadOnlyList<PlanGraphNode> statementNodes,
        IReadOnlyList<ScalarFunctionCall> calls,
        IReadOnlyDictionary<string, IReadOnlyList<FunctionName>> udfMarkers,
        string statementState,
        List<SourceLink> links)
    {
        foreach (var node in statementNodes.Where(node => udfMarkers.ContainsKey(node.Id)))
        {
            foreach (var marker in udfMarkers[node.Id])
            {
                var candidates = calls.Where(call => SameFunctionName(marker, call.Name)
                        && ValidSpan(executedText, call.Start, call.Length))
                    .ToArray();
                if (candidates.Length == 1)
                {
                    var call = candidates[0];
                    links.Add(new SourceLink(
                        node.Id,
                        call.Start,
                        call.Length,
                        Kind: "statement",
                        State: CombineConfidence("exact", statementState),
                        Rule: "UserDefinedFunction marker " + marker.DisplayName + " on Compute Scalar matched this ScriptDom function call"));
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    links.Add(new SourceLink(
                        node.Id,
                        candidate.Start,
                        candidate.Length,
                        Kind: "statement",
                        State: "ambiguous",
                        Rule: "UserDefinedFunction marker " + marker.DisplayName + " matches multiple ScriptDom function calls"));
                }
            }
        }
    }

    private static Dictionary<string, IReadOnlyList<FunctionName>> ReadUdfMarkers(XDocument planDocument, PlanGraph graph)
    {
        var planStatements = planDocument.Descendants(Showplan + "StmtSimple").ToArray();
        var result = new Dictionary<string, IReadOnlyList<FunctionName>>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes.Where(node => node.ShowplanNodeId is not null
                && string.Equals(node.PhysicalOperator, "Compute Scalar", StringComparison.OrdinalIgnoreCase)))
        {
            if (node.StatementIndex < 0 || node.StatementIndex >= planStatements.Length)
                continue;

            var relOps = planStatements[node.StatementIndex].Descendants(Showplan + "RelOp")
                .Where(relOp => IntegerAttribute(relOp, "NodeId") == node.ShowplanNodeId)
                .Take(2)
                .ToArray();
            if (relOps.Length != 1)
                continue;

            var names = OwnedElements(relOps[0])
                .Where(element => element.Name == Showplan + "UserDefinedFunction")
                .Select(element => (string?)element.Attribute("FunctionName"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => ParseFunctionName(value!))
                .Where(value => value is not null)
                .Cast<FunctionName>()
                .Distinct(FunctionNameComparer.Instance)
                .ToArray();
            if (names.Length > 0)
                result[node.Id] = names;
        }
        return result;
    }

    private static FunctionName? ParseFunctionName(string name)
    {
        var parsed = SqlServerDocument.Parse("SELECT " + name + "()");
        if (parsed.HasErrors)
            return null;

        var visitor = new FunctionNameVisitor();
        parsed.Fragment.Accept(visitor);
        return visitor.Names.Count == 1 ? visitor.Names[0] : null;
    }

    private static FunctionName? FunctionNameOf(FunctionCall call)
    {
        var parts = new List<string>();
        if (call.CallTarget is MultiPartIdentifierCallTarget target)
            parts.AddRange(target.MultiPartIdentifier.Identifiers.Select(identifier => identifier.Value));
        if (call.FunctionName?.Value is { Length: > 0 } name)
            parts.Add(name);
        return parts.Count == 0 ? null : new FunctionName(parts.ToArray());
    }

    private static bool SameFunctionName(FunctionName left, FunctionName right)
    {
        if (left.Parts.SequenceEqual(right.Parts, StringComparer.OrdinalIgnoreCase))
            return true;
        return left.Parts.Length == 1 || right.Parts.Length == 1
            ? IdentifierEquals(left.Parts[^1], right.Parts[^1])
            : false;
    }

    private static XDocument? TryLoadPlan(string? showplanXml)
    {
        if (string.IsNullOrWhiteSpace(showplanXml))
            return null;
        try
        {
            return PlanDistiller.LoadValidatedDocument(showplanXml, PlanDistiller.DefaultLimits);
        }
        catch (SqlHarnessSafetyException)
        {
            return null;
        }
    }

    private static bool IsKeyLookupWithMinusOne(PlanGraphNode node) =>
        node.TableReferenceId == -1
        && node.PhysicalOperator.Contains("Key Lookup", StringComparison.OrdinalIgnoreCase);

    private static bool HasObjectName(PlanGraphObject? planObject) =>
        !string.IsNullOrWhiteSpace(planObject?.Table);

    private static bool SameObjectName(SourceReference reference, PlanGraphObject planObject)
    {
        if (!IdentifierEquals(reference.Table, planObject.Table))
            return false;
        if (reference.Schema is not null && planObject.Schema is not null && !IdentifierEquals(reference.Schema, planObject.Schema))
            return false;
        if (reference.Database is not null && planObject.Database is not null && !IdentifierEquals(reference.Database, planObject.Database))
            return false;
        return true;
    }

    private static bool SamePlanObject(PlanGraphObject left, PlanGraphObject right) =>
        IdentifierEquals(left.Table, right.Table)
        && (left.Schema is null || right.Schema is null || IdentifierEquals(left.Schema, right.Schema))
        && (left.Database is null || right.Database is null || IdentifierEquals(left.Database, right.Database))
        && (left.Alias is null || right.Alias is null || IdentifierEquals(left.Alias, right.Alias));

    private static string ObjectMatchRule(PlanGraphNode node, SourceReference reference)
    {
        var source = reference.CtePath.Count == 0
            ? "source table " + reference.QualifiedName
            : "expanded CTE " + string.Join(" -> ", reference.CtePath) + " source table " + reference.QualifiedName;
        return node.TableReferenceId is > 0
            ? "unique table identity matched " + source + "; TableReferenceId " + node.TableReferenceId.Value.ToString(CultureInfo.InvariantCulture) + " is retained as disambiguation evidence"
            : "unique table identity matched " + source;
    }

    private static void AddLink(
        string nodeId,
        SourceReference reference,
        string matchState,
        string rule,
        string statementState,
        List<SourceLink> links)
    {
        foreach (var span in SurfaceSpans(reference).Distinct())
        {
            links.Add(new SourceLink(
                nodeId,
                span.Start,
                span.Length,
                Kind: "table_reference",
                State: matchState == "ambiguous" ? "ambiguous" : CombineConfidence(matchState, statementState),
                Rule: rule));
        }
    }

    private static IEnumerable<(int Start, int Length)> SurfaceSpans(SourceReference reference)
    {
        yield return (reference.Start, reference.Length);
        foreach (var invocation in reference.CteInvocations)
            yield return (invocation.Start, invocation.Length);
    }

    private static void AddStatementLinks(
        IReadOnlyList<PlanGraphNode> nodes,
        int? spanStart,
        int? spanLength,
        string state,
        string rule,
        List<SourceLink> links)
    {
        foreach (var node in nodes)
            links.Add(new SourceLink(node.Id, spanStart, spanLength, "statement", state, rule));
    }

    private static void AddUnmatchedStatementLinks(
        IReadOnlyList<PlanGraphNode> nodes,
        List<SourceLink> links,
        string rule) =>
        AddStatementLinks(nodes, null, null, "none", rule, links);

    private static bool ValidSpan(string text, int? start, int? length) =>
        start is >= 0 && length is > 0 && start.Value <= text.Length - length.Value;

    private static string PairState(string? matchKind) => matchKind switch
    {
        "exact" => "exact",
        "psp_suffix" or "normalized" or "prefix" => "inferred",
        "ambiguous" => "ambiguous",
        _ => "none",
    };

    private static string CombineConfidence(string matchState, string statementState) =>
        matchState == "exact" && statementState == "inferred" ? "inferred" : matchState;

    private static bool IdentifierEquals(string? left, string? right) =>
        left is not null && right is not null
        && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static int? IntegerAttribute(XElement element, string name) =>
        int.TryParse((string?)element.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

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

    private sealed record ReferenceMatch(IReadOnlyList<SourceReference> Candidates, bool IsExact, string Rule)
    {
        internal static ReferenceMatch None { get; } = new([], false, "no matching source reference");
        internal static ReferenceMatch Exact(SourceReference reference, string rule) => new([reference], true, rule);
        internal static ReferenceMatch Ambiguous(IReadOnlyList<SourceReference> candidates, string rule) => new(candidates, false, rule);
    }

    private sealed record SourceReference(
        int InstanceId,
        int Order,
        int Start,
        int Length,
        string? Database,
        string? Schema,
        string? Table,
        string? Alias,
        bool IsFunction,
        IReadOnlyList<string> CtePath,
        int ScopeId,
        IReadOnlyList<CteInvocationSpan> CteInvocations)
    {
        internal bool IsPotentialModule => IsFunction || !string.IsNullOrWhiteSpace(Table);
        internal string QualifiedName => string.Join('.', new[] { Database, Schema, Table }.Where(value => !string.IsNullOrEmpty(value)));
        internal bool IncludesScope(int scopeId) => ScopeId == scopeId || CteInvocations.Any(invocation => invocation.ScopeId == scopeId);
    }

    private sealed record CteInvocationSpan(int Start, int Length, int ScopeId);

    private sealed record ScalarFunctionCall(int Start, int Length, FunctionName Name);

    private sealed record FunctionName(string[] Parts)
    {
        internal string DisplayName => string.Join('.', Parts);
    }

    private sealed class FunctionNameComparer : IEqualityComparer<FunctionName>
    {
        internal static FunctionNameComparer Instance { get; } = new();

        public bool Equals(FunctionName? left, FunctionName? right) =>
            left is not null && right is not null && left.Parts.SequenceEqual(right.Parts, StringComparer.OrdinalIgnoreCase);

        public int GetHashCode(FunctionName value)
        {
            var hash = new HashCode();
            foreach (var part in value.Parts)
                hash.Add(part, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }

    private sealed class FunctionNameVisitor : TSqlFragmentVisitor
    {
        internal List<FunctionName> Names { get; } = [];

        public override void ExplicitVisit(FunctionCall node)
        {
            if (FunctionNameOf(node) is { } name)
                Names.Add(name);
            base.ExplicitVisit(node);
        }
    }

    private sealed class SourceScopeCollector : TSqlFragmentVisitor
    {
        private CteEnvironment _ctes = new(null);
        private readonly HashSet<CommonTableExpression> _expandingCtes = new(ReferenceEqualityComparer.Instance);
        private readonly List<string> _ctePath = [];
        private readonly List<CteInvocationSpan> _cteInvocations = [];
        private int _nextReferenceId;
        private int _nextQueryScopeId = 1;
        private int _currentScopeId;

        internal List<SourceReference> TableReferences { get; } = [];
        internal List<ScalarFunctionCall> ScalarCalls { get; } = [];
        internal List<int> QueryScopeIds { get; } = [];

        public override void ExplicitVisit(SelectStatement node)
        {
            WithCteScope(node.WithCtesAndXmlNamespaces, () => node.QueryExpression?.Accept(this));
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            var previousScopeId = _currentScopeId;
            _currentScopeId = _nextQueryScopeId++;
            QueryScopeIds.Add(_currentScopeId);
            try
            {
                // Showplan TableReferenceId follows expanded FROM order. Visit
                // FROM before subqueries nested in SELECT expressions.
                node.FromClause?.Accept(this);
                node.TopRowFilter?.Accept(this);
                foreach (var selectElement in node.SelectElements)
                    selectElement.Accept(this);
                node.WhereClause?.Accept(this);
                node.GroupByClause?.Accept(this);
                node.HavingClause?.Accept(this);
                node.WindowClause?.Accept(this);
                node.OrderByClause?.Accept(this);
                node.OffsetClause?.Accept(this);
                node.ForClause?.Accept(this);
            }
            finally
            {
                _currentScopeId = previousScopeId;
            }
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            var name = node.SchemaObject;
            if (name.Identifiers.Count == 1 && _ctes.TryGet(name.BaseIdentifier.Value, out var cte))
            {
                ExpandCte(cte, new CteInvocationSpan(node.StartOffset, node.FragmentLength, _currentScopeId));
                return;
            }

            AddReference(name, node.Alias?.Value, isFunction: false, node.StartOffset, node.FragmentLength);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
        {
            AddReference(node.SchemaObject, node.Alias?.Value, isFunction: true, node.StartOffset, node.FragmentLength);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (FunctionNameOf(node) is { } name && node.StartOffset >= 0 && node.FragmentLength > 0)
                ScalarCalls.Add(new ScalarFunctionCall(node.StartOffset, node.FragmentLength, name));
            base.ExplicitVisit(node);
        }

        private void AddReference(SchemaObjectName name, string? alias, bool isFunction, int start, int length)
        {
            if (start < 0 || length <= 0)
                return;

            TableReferences.Add(new SourceReference(
                _nextReferenceId,
                TableReferences.Count,
                start,
                length,
                name.DatabaseIdentifier?.Value,
                name.SchemaIdentifier?.Value,
                name.BaseIdentifier?.Value,
                alias,
                isFunction,
                _ctePath.ToArray(),
                _currentScopeId,
                _cteInvocations.ToArray()));
            if (!QueryScopeIds.Contains(_currentScopeId))
                QueryScopeIds.Add(_currentScopeId);
            _nextReferenceId++;
        }

        private void ExpandCte(CommonTableExpression cte, CteInvocationSpan invocation)
        {
            if (!_expandingCtes.Add(cte))
                return;

            var name = cte.ExpressionName.Value;
            _ctePath.Add(name);
            _cteInvocations.Add(invocation);
            try
            {
                WithCteScope(cte.WithCtesAndXmlNamespaces, () => cte.QueryExpression.Accept(this));
            }
            finally
            {
                _cteInvocations.RemoveAt(_cteInvocations.Count - 1);
                _ctePath.RemoveAt(_ctePath.Count - 1);
                _expandingCtes.Remove(cte);
            }
        }

        private void WithCteScope(WithCtesAndXmlNamespaces? with, Action action)
        {
            var previous = _ctes;
            var current = new CteEnvironment(previous);
            if (with is not null)
            {
                foreach (var cte in with.CommonTableExpressions)
                    current.Add(cte.ExpressionName.Value, cte);
            }

            _ctes = current;
            try
            {
                action();
            }
            finally
            {
                _ctes = previous;
            }
        }
    }

    private sealed class CteEnvironment(CteEnvironment? parent)
    {
        private readonly Dictionary<string, CommonTableExpression> _local = new(StringComparer.OrdinalIgnoreCase);

        internal void Add(string name, CommonTableExpression cte) => _local[name] = cte;

        internal bool TryGet(string name, out CommonTableExpression cte)
        {
            if (_local.TryGetValue(name, out cte!))
                return true;
            if (parent is not null)
                return parent.TryGet(name, out cte!);
            cte = null!;
            return false;
        }
    }
}