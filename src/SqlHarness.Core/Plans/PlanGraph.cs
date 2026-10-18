namespace SqlHarness.Core;

public sealed record PlanGraphBuildResult(
    string Engine,
    PlanGraph? Graph,
    PlanGraphBuildError? Error);

public sealed record PlanGraphBuildError(string Code, string Message);

public sealed record PlanGraph(
    string Engine,
    IReadOnlyList<PlanGraphStatement> Statements,
    IReadOnlyList<PlanGraphNode> Nodes,
    IReadOnlyList<PlanGraphEdge> Edges);

public sealed record PlanGraphStatement(
    int Index,
    int? StatementId,
    int? StatementCompId,
    string RootNodeId);

public sealed record PlanGraphNode(
    string Id,
    int StatementIndex,
    int? ShowplanNodeId,
    string PhysicalOperator,
    string? LogicalOperator,
    PlanGraphObject? Object,
    int? TableReferenceId,
    IReadOnlyList<string> Predicates,
    IReadOnlyList<PlanGraphDefinedValue> DefinedValues,
    double? EstimatedRows,
    long? ActualRows,
    long? Executions,
    double? CostFraction,
    IReadOnlyList<string> Warnings);

public sealed record PlanGraphObject(
    string? Database,
    string? Schema,
    string? Table,
    string? Index,
    string? Alias);

public sealed record PlanGraphDefinedValue(
    IReadOnlyList<PlanGraphColumnReference> Columns,
    IReadOnlyList<string> Expressions);

public sealed record PlanGraphColumnReference(
    string? Database,
    string? Schema,
    string? Table,
    string? Column,
    string? Alias);

public sealed record PlanGraphEdge(
    string Source,
    string Target,
    double? EstimatedRows,
    long? ActualRows,
    double? EstimatedSizeBytes);

public sealed record PlanGraphPropertyResult(
    string Engine,
    string NodeId,
    PlanGraphPropertyTree? Tree,
    PlanGraphBuildError? Error);

public sealed record PlanGraphPropertyTree(
    IReadOnlyList<PlanGraphPropertyNode> Properties,
    bool IsTruncated,
    int OmittedNodeCount);

public sealed record PlanGraphPropertyNode(
    string Name,
    IReadOnlyList<PlanGraphPropertyAttribute> Attributes,
    IReadOnlyList<PlanGraphPropertyNode> Children,
    string? Text = null,
    bool IsTruncationMarker = false,
    int OmittedNodeCount = 0);

public sealed record PlanGraphPropertyAttribute(string Name, string Value);

internal sealed record PlanGraphBuilderLimits(
    PlanDistillerLimits Document,
    int MaximumNodes,
    int MaximumGraphBytes,
    int MaximumPropertyResponseBytes);
