namespace SqlHarness.Dashboard;

public sealed record AgentSessionKey(string AgentKind, string ConversationId);

public enum Membership
{
    Exact,
    Inferred,
    Unassigned,
}

public sealed record OperationIdentityRow(
    long OperationId,
    long SessionId,
    string AgentKind,
    string Transport,
    long? AgentPid,
    string? AgentStartedAt,
    string StartedAt,
    string? AgentSessionId,
    string? AgentThreadSource,
    string UpdatedAt);

public sealed record LinkRow(long OperationId, string AgentKind, string ConversationId);

public sealed record OperationMembership(long OperationId, long SessionId, AgentSessionKey? Key, Membership Membership, string UpdatedAt);

/// <summary>Derives one agent-session membership classification for every retained operation.</summary>
public static class AgentSessionMembership
{
    public static IReadOnlyList<OperationMembership> Resolve(
        IReadOnlyList<OperationIdentityRow> ops,
        IReadOnlyList<LinkRow> links)
    {
        var exactByOperation = links.ToDictionary(
            link => link.OperationId,
            link => new AgentSessionKey(link.AgentKind, link.ConversationId));
        foreach (var operation in ops)
        {
            if (operation.AgentKind is "codex" or "opencode" && operation.AgentSessionId is { Length: > 0 } conversationId)
                exactByOperation.TryAdd(operation.OperationId, new AgentSessionKey(operation.AgentKind, conversationId));
        }

        var operationsById = ops.ToDictionary(operation => operation.OperationId);
        var exactBySession = exactByOperation
            .Where(pair => operationsById.ContainsKey(pair.Key))
            .GroupBy(pair => operationsById[pair.Key].SessionId)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Value).Distinct().ToArray());

        var inferred = new Dictionary<long, AgentSessionKey>();
        InferMcpMembership(ops, exactByOperation, exactBySession, inferred);
        InferCliMembership(ops, exactByOperation, exactBySession, inferred);

        return ops.Select(operation =>
        {
            if (exactByOperation.TryGetValue(operation.OperationId, out var exact))
                return new OperationMembership(operation.OperationId, operation.SessionId, exact, Membership.Exact, operation.UpdatedAt);
            return inferred.TryGetValue(operation.OperationId, out var key)
                ? new OperationMembership(operation.OperationId, operation.SessionId, key, Membership.Inferred, operation.UpdatedAt)
                : new OperationMembership(operation.OperationId, operation.SessionId, null, Membership.Unassigned, operation.UpdatedAt);
        }).ToArray();
    }

    private static void InferMcpMembership(
        IReadOnlyList<OperationIdentityRow> operations,
        IReadOnlyDictionary<long, AgentSessionKey> exactByOperation,
        IReadOnlyDictionary<long, AgentSessionKey[]> exactBySession,
        IDictionary<long, AgentSessionKey> inferred)
    {
        foreach (var session in operations.Where(operation => operation.Transport == "mcp").GroupBy(operation => operation.SessionId))
        {
            if (!exactBySession.TryGetValue(session.Key, out var sessionKeys) || sessionKeys.Length != 1)
                continue;
            var key = sessionKeys[0];
            var ordered = session.OrderBy(operation => operation.StartedAt, StringComparer.Ordinal)
                .ThenBy(operation => operation.OperationId).ToArray();
            var linkedIndexes = ordered.Select((operation, index) => (operation, index))
                .Where(item => exactByOperation.TryGetValue(item.operation.OperationId, out var linked) && linked == key)
                .Select(item => item.index).ToArray();
            if (linkedIndexes.Length < 2)
                continue;
            for (var index = linkedIndexes[0] + 1; index < linkedIndexes[^1]; index++)
            {
                var operation = ordered[index];
                if (!exactByOperation.ContainsKey(operation.OperationId))
                    inferred[operation.OperationId] = key;
            }
        }
    }

    private static void InferCliMembership(
        IReadOnlyList<OperationIdentityRow> operations,
        IReadOnlyDictionary<long, AgentSessionKey> exactByOperation,
        IReadOnlyDictionary<long, AgentSessionKey[]> exactBySession,
        IDictionary<long, AgentSessionKey> inferred)
    {
        foreach (var process in operations.Where(operation => operation.AgentPid is not null && operation.AgentStartedAt is not null)
                     .GroupBy(operation => ProcessIdentity(operation)))
        {
            var processOperations = process.ToArray();
            var mcpSessions = processOperations.Where(operation => operation.Transport == "mcp")
                .GroupBy(operation => operation.SessionId).ToArray();
            if (mcpSessions.Length == 0)
                continue;

            AgentSessionKey? consensus = null;
            var agrees = true;
            foreach (var mcpSession in mcpSessions)
            {
                if (!exactBySession.TryGetValue(mcpSession.Key, out var keys) || keys.Length != 1
                    || (consensus is not null && consensus != keys[0]))
                {
                    agrees = false;
                    break;
                }
                consensus = keys[0];
            }
            if (!agrees || consensus is null)
                continue;

            var ordered = processOperations.OrderBy(operation => operation.StartedAt, StringComparer.Ordinal)
                .ThenBy(operation => operation.OperationId).ToArray();
            var eligibleCliSessions = processOperations.Where(operation => operation.Transport == "cli")
                .Select(operation => operation.SessionId).Distinct()
                .Where(sessionId => !exactBySession.TryGetValue(sessionId, out var keys)
                    || keys.Length == 1 && keys[0] == consensus)
                .ToHashSet();
            var linkedIndexes = ordered.Select((operation, index) => (operation, index))
                .Where(item => exactByOperation.TryGetValue(item.operation.OperationId, out var linked) && linked == consensus)
                .Select(item => item.index).ToArray();
            if (linkedIndexes.Length < 2)
                continue;

            for (var index = linkedIndexes[0] + 1; index < linkedIndexes[^1]; index++)
            {
                var operation = ordered[index];
                if (operation.Transport == "cli" && eligibleCliSessions.Contains(operation.SessionId)
                    && !exactByOperation.ContainsKey(operation.OperationId))
                    inferred[operation.OperationId] = consensus;
            }
        }
    }

    private static (long Pid, string StartedAt) ProcessIdentity(OperationIdentityRow operation) =>
        (operation.AgentPid!.Value, operation.AgentStartedAt!);
}