using SqlHarness.Core;
using SqlHarness.Core.Journal;

using Xunit;

namespace SqlHarness.Tests.Journal;

public sealed class AnnotationScopeTests
{
    private static readonly CallerIdentity CodexCaller = new(10, "codex", "conversation-a");

    [Fact]
    public void Fresh_unlinked_target_in_own_session_is_allowed()
    {
        var decision = AnnotationScope.Decide(CodexCaller, Target(1, 10), new MembershipView([]), matchingOn: true);

        Assert.True(decision.Available);
    }

    [Fact]
    public void Other_conversation_target_is_not_available()
    {
        var target = Target(2, 20);
        var links = new MembershipView([new ExactOperationLink(2, 20, "codex", "conversation-b")]);

        Assert.False(AnnotationScope.Decide(CodexCaller, target, links, matchingOn: true).Available);
    }

    [Fact]
    public void Own_session_target_with_exact_link_to_another_conversation_is_not_available()
    {
        var target = Target(2, 10);
        var links = new MembershipView([new ExactOperationLink(2, 10, "codex", "conversation-b")]);

        Assert.False(AnnotationScope.Decide(CodexCaller, target, links, matchingOn: true).Available);
    }

    [Fact]
    public void Cross_mcp_session_target_in_same_conversation_is_allowed()
    {
        var target = Target(2, 20);
        var links = new MembershipView([new ExactOperationLink(2, 20, "codex", "conversation-a")]);

        Assert.True(AnnotationScope.Decide(CodexCaller, target, links, matchingOn: true).Available);
    }

    [Fact]
    public void Same_conversation_id_from_another_agent_kind_is_not_available()
    {
        var caller = new CallerIdentity(10, "codex", "same-id");
        var target = new TargetRow(2, 20, "ref-2", "query", "opencode", "same-id", true, true);

        Assert.False(AnnotationScope.Decide(caller, target, new MembershipView([]), matchingOn: true).Available);
    }

    [Fact]
    public void Claude_caller_resolves_from_one_conversation_linked_in_own_session()
    {
        var caller = new CallerIdentity(10, "claude", null);
        var links = new MembershipView([new ExactOperationLink(1, 10, "claude", "conversation-a")]);

        Assert.True(AnnotationScope.Decide(caller, Target(2, 20, "claude"),
            new MembershipView([.. links.Links, new ExactOperationLink(2, 20, "claude", "conversation-a")]), matchingOn: true).Available);
    }

    [Fact]
    public void Claude_caller_with_two_conversations_is_unresolved()
    {
        var caller = new CallerIdentity(10, "claude", null);
        var links = new MembershipView([
            new ExactOperationLink(1, 10, "claude", "conversation-a"),
            new ExactOperationLink(3, 10, "claude", "conversation-b"),
            new ExactOperationLink(2, 20, "claude", "conversation-a"),
        ]);

        Assert.False(AnnotationScope.Decide(caller, Target(2, 20, "claude"), links, matchingOn: true).Available);
        Assert.True(AnnotationScope.Decide(caller, Target(4, 10), links, matchingOn: true).Available);
    }

    [Fact]
    public void Inferred_membership_never_authorizes()
    {
        var links = new MembershipView([], [new InferredOperationLink(2, 20, "codex", "conversation-a")]);

        Assert.False(AnnotationScope.Decide(CodexCaller, Target(2, 20), links, matchingOn: true).Available);
    }

    [Fact]
    public void Annotation_invocation_is_journaled_without_an_operation_reference_or_target()
    {
        var operation = new SqlHarnessAnnotationOperation();

        Assert.Null(operation.OperationRef);
        Assert.False(operation.OperationAnnotationsEnabled);
    }

    [Fact]
    public async Task Annotation_operation_runs_its_handler_without_a_target_reference()
    {
        var called = false;
        var operation = new SqlHarnessAnnotationOperation(_ =>
        {
            called = true;
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, new object(), null));
        });

        var outcome = await new SqlHarnessModule().ExecuteAsync(operation);

        Assert.True(called);
        Assert.Null(outcome.OperationRef);
    }

    [Fact]
    public void Matching_off_uses_only_the_callers_mcp_session()
    {
        Assert.True(AnnotationScope.Decide(CodexCaller, Target(4, 10), new MembershipView([]), matchingOn: false).Available);
        Assert.False(AnnotationScope.Decide(CodexCaller, Target(4, 20), new MembershipView([]), matchingOn: false).Available);
    }

    private static TargetRow Target(long operationId, long sessionId, string agentKind = "codex", string tool = "query") =>
        new(operationId, sessionId, "ref-" + operationId, tool, agentKind, null, Journaled: true, OperationRefEligible: true);
}
