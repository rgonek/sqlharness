namespace SqlHarness.Core.Journal;

public sealed record CallerIdentity(long SessionId, string AgentKind, string? AgentSessionId);

public sealed record TargetRow(
    long OperationId,
    long SessionId,
    string OperationRef,
    string Tool,
    string AgentKind,
    string? AgentSessionId,
    bool Journaled,
    bool OperationRefEligible);

public sealed record ExactOperationLink(long OperationId, long SessionId, string AgentKind, string ConversationId);

public sealed record InferredOperationLink(long OperationId, long SessionId, string AgentKind, string ConversationId);

public sealed record MembershipView(
    IReadOnlyList<ExactOperationLink> Links,
    IReadOnlyList<InferredOperationLink>? Inferred = null);

public sealed record ScopeDecision(bool Available);

/// <summary>Conservative journal-only access rules for agent operation annotations.</summary>
public static class AnnotationScope
{
    private readonly record struct AgentConversationKey(string AgentKind, string ConversationId);

    private static readonly HashSet<string> AnnotatableTools = new(StringComparer.Ordinal)
    {
        "query", "measure", "compare", "watch", "snapshot",
    };

    public static ScopeDecision Decide(CallerIdentity caller, TargetRow target, MembershipView links, bool matchingOn)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(links);

        if (!target.Journaled || !target.OperationRefEligible || !AnnotatableTools.Contains(target.Tool))
            return new ScopeDecision(false);

        if (!matchingOn)
            return new ScopeDecision(target.SessionId == caller.SessionId);

        var targetLinks = links.Links.Where(link => link.OperationId == target.OperationId).ToArray();
        var (targetConversation, targetAmbiguous) = ExactConversation(target.AgentKind, target.AgentSessionId, targetLinks);
        if (targetAmbiguous)
            return new ScopeDecision(false);
        var callerConversation = CallerConversation(caller, links.Links);

        if (callerConversation is not null)
        {
            if (targetConversation == callerConversation)
                return new ScopeDecision(true);

            var unlinkedOwnSessionTarget = target.SessionId == caller.SessionId
                && targetConversation is null;
            return new ScopeDecision(unlinkedOwnSessionTarget);
        }

        // With an unresolved caller there is no positive conversation identity to compare.
        // Only an operation with no exact conversation identity in this MCP session is safe.
        return new ScopeDecision(target.SessionId == caller.SessionId && targetConversation is null);
    }

    private static AgentConversationKey? CallerConversation(CallerIdentity caller, IReadOnlyList<ExactOperationLink> links)
    {
        if (caller.AgentKind is "codex" or "opencode")
            return string.IsNullOrWhiteSpace(caller.AgentSessionId)
                ? null
                : new AgentConversationKey(caller.AgentKind, caller.AgentSessionId);

        if (caller.AgentKind != "claude")
            return null;

        var conversations = links.Where(link => link.SessionId == caller.SessionId && link.AgentKind == caller.AgentKind)
            .Select(link => new AgentConversationKey(link.AgentKind, link.ConversationId)).Distinct().Take(2).ToArray();
        return conversations.Length == 1 ? conversations[0] : null;
    }

    private static (AgentConversationKey? Conversation, bool Ambiguous) ExactConversation(string agentKind, string? agentSessionId, IReadOnlyList<ExactOperationLink> links)
    {
        var conversations = links.Where(link => link.AgentKind == agentKind)
            .Select(link => new AgentConversationKey(link.AgentKind, link.ConversationId)).ToList();
        if (agentKind is "codex" or "opencode" && !string.IsNullOrWhiteSpace(agentSessionId))
            conversations.Add(new AgentConversationKey(agentKind, agentSessionId));
        var distinct = conversations.Distinct().Take(2).ToArray();
        return distinct.Length == 1 ? (distinct[0], false) : (null, distinct.Length > 1);
    }
}
