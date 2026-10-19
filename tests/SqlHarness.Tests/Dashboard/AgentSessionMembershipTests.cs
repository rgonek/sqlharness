using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class AgentSessionMembershipTests
{
    [Fact]
    public void Single_conversation_mcp_session_infers_only_operations_between_exact_links()
    {
        var operations = Ops(
            Op(1, 10, startedAt: "01"), Op(2, 10, startedAt: "02"), Op(3, 10, startedAt: "03"),
            Op(4, 10, startedAt: "04"), Op(5, 10, startedAt: "05"));
        var result = Resolve(operations, Link(1, "claude", "conversation"), Link(4, "claude", "conversation"));

        Assert.Equal(
        [
            (1L, Membership.Exact, "conversation"),
            (2L, Membership.Inferred, "conversation"),
            (3L, Membership.Inferred, "conversation"),
            (4L, Membership.Exact, "conversation"),
            (5L, Membership.Unassigned, null),
        ],
            result.Select(item => (item.OperationId, item.Membership, item.Key?.ConversationId)));
    }

    [Fact]
    public void Shared_session_splits_and_counts_once()
    {
        var operations = Ops(Op(1, 10, "01"), Op(2, 10, "02"), Op(3, 10, "03"), Op(4, 10, "04"), Op(5, 10, "05"));
        var result = Resolve(operations,
            Link(1, "claude", "conversation-a"), Link(3, "claude", "conversation-a"),
            Link(4, "claude", "conversation-b"), Link(5, "claude", "conversation-b"));

        Assert.Equal(operations.Count, result.Count);
        Assert.Equal(1, result.Count(item => item.Membership == Membership.Unassigned));
        Assert.Equal((4, 0, 1), Counts(result));
        Assert.Null(result.Single(item => item.OperationId == 2).Key);
    }

    [Fact]
    public void New_conversation_in_same_session_not_inferred_into_previous()
    {
        var operations = Ops(Op(1, 10, "01"), Op(2, 10, "02"), Op(3, 10, "03"));
        var result = Resolve(operations, Link(1, "claude", "old"), Link(3, "claude", "new"));

        Assert.Equal(Membership.Unassigned, result.Single(item => item.OperationId == 2).Membership);
    }

    [Fact]
    public void Cli_inference_requires_process_mcp_consensus_and_two_chronological_brackets()
    {
        var operations = Ops(
            Op(8, 20, "00", transport: "cli"), Op(1, 10, "01", transport: "mcp"), Op(2, 20, "02", transport: "cli"),
            Op(3, 30, "03", transport: "mcp"), Op(4, 20, "04", transport: "cli"),
            Op(5, 20, "05", transport: "cli"), Op(6, 10, "06", transport: "mcp"), Op(7, 20, "07", transport: "cli"));
        var result = Resolve(operations, Link(1, "claude", "same"), Link(3, "claude", "same"), Link(6, "claude", "same"),
            Link(4, "claude", "same"));

        Assert.Equal(Membership.Inferred, result.Single(item => item.OperationId == 2).Membership);
        Assert.Equal(Membership.Unassigned, result.Single(item => item.OperationId == 8).Membership);
        Assert.Equal(Membership.Exact, result.Single(item => item.OperationId == 4).Membership);
        Assert.Equal(Membership.Inferred, result.Single(item => item.OperationId == 5).Membership);
        Assert.Equal(Membership.Unassigned, result.Single(item => item.OperationId == 7).Membership);

        var conflict = Resolve(operations, Link(1, "claude", "same"), Link(3, "claude", "other"), Link(6, "claude", "same"),
            Link(4, "claude", "same"));
        Assert.Equal(Membership.Unassigned, conflict.Single(item => item.OperationId == 2).Membership);
    }

    [Fact]
    public void Cli_session_with_conflicting_exact_link_does_not_infer_process_consensus()
    {
        var operations = Ops(
            Op(1, 10, "01", transport: "mcp"), Op(2, 10, "06", transport: "mcp"),
            Op(3, 20, "02", transport: "cli"), Op(4, 21, "05", transport: "cli"),
            Op(5, 21, "04", transport: "cli"));
        var result = Resolve(operations,
            Link(1, "claude", "consensus"), Link(2, "claude", "consensus"), Link(5, "claude", "other"));

        Assert.Equal(Membership.Unassigned, result.Single(item => item.OperationId == 4).Membership);
    }

    [Fact]
    public void Codex_membership_uses_agent_session_id_and_keeps_subagent_thread_separate()
    {
        var operations = Ops(
            Op(1, 10, "01", agentKind: "codex", agentSessionId: "parent"),
            Op(2, 10, "02", agentKind: "codex", agentSessionId: "child", threadSource: "subagent"));

        var result = Resolve(operations);

        Assert.Equal((Membership.Exact, "parent"), (result[0].Membership, result[0].Key?.ConversationId));
        Assert.Equal((Membership.Exact, "child"), (result[1].Membership, result[1].Key?.ConversationId));
    }

    [Fact]
    public void Claude_subagent_lines_fold_into_the_transcript_parent_session()
    {
        var operations = Ops(Op(1, 10, "01"), Op(2, 10, "02"));
        var result = Resolve(operations, Link(1, "claude", "parent-session"), Link(2, "claude", "parent-session"));

        Assert.All(result, item => Assert.Equal((Membership.Exact, "parent-session"), (item.Membership, item.Key?.ConversationId)));
    }

    [Fact]
    public void Journal_reader_loads_current_identity_and_link_columns_for_membership()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("mcp:membership") with { Transport = SqlHarness.Core.JournalTransport.Mcp };
        var first = seed.Operation(session);
        var middle = seed.Operation(session);
        var last = seed.Operation(session);
        JournalSeed.Link(home.DatabasePath, first.OperationId, "claude", "conversation", "Conversation");
        JournalSeed.Link(home.DatabasePath, last.OperationId, "claude", "conversation", "Conversation");

        var result = new JournalReader(home.DatabasePath, new FakeProcesses()).AgentSessionMemberships();

        Assert.Equal(Membership.Inferred, result.Single(item => item.OperationId == middle.OperationId).Membership);
    }

    private static IReadOnlyList<OperationMembership> Resolve(IReadOnlyList<OperationIdentityRow> operations, params LinkRow[] links) =>
        AgentSessionMembership.Resolve(operations, links);

    private static OperationIdentityRow Op(long id, long session, string startedAt, string agentKind = "claude", string transport = "mcp",
        long? agentPid = null, string? agentStartedAt = null, string? agentSessionId = null, string? threadSource = null) =>
        new(id, session, agentKind, transport, agentPid ?? 42, agentStartedAt ?? "process-start", startedAt, agentSessionId, threadSource);

    private static LinkRow Link(long operationId, string kind, string conversationId) => new(operationId, kind, conversationId);

    private static IReadOnlyList<OperationIdentityRow> Ops(params OperationIdentityRow[] operations) => operations;

    private static (int Exact, int Inferred, int Unassigned) Counts(IReadOnlyList<OperationMembership> memberships) =>
        (memberships.Count(item => item.Membership == Membership.Exact),
            memberships.Count(item => item.Membership == Membership.Inferred),
            memberships.Count(item => item.Membership == Membership.Unassigned));
}