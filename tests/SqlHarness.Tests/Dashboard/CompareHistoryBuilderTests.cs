using System.Text.Json;

using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class CompareHistoryBuilderTests
{
    [Fact]
    public void Versions_follow_first_hash_occurrence_per_session_subject_and_scope()
    {
        var rows = new[]
        {
            Row(3, 1, "orders", "whole", "h0", "h2"),
            Row(1, 1, "orders", "whole", "h0", "h1"),
            Row(2, 1, "orders", "whole", "h1", "h2"),
            Row(4, 1, "orders", "filters", "h1", "h2"),
            Row(5, 1, "other", "whole", "h1", "h2"),
            Row(6, 2, "orders", "whole", "h1", "h2"),
            Row(7, 1, "orders", "whole", "h2", "h2"),
        };

        var history = CompareHistoryBuilder.Build(rows);

        Assert.Equal(("v0", "v1"), Pair(history, 1));
        Assert.Equal(("v1", "v2"), Pair(history, 2));
        Assert.Equal(("v0", "v2"), Pair(history, 3));
        Assert.Equal(("v0", "v1"), Pair(history, 4));
        Assert.Equal(("v0", "v1"), Pair(history, 5));
        Assert.Equal(("v0", "v1"), Pair(history, 6));
        Assert.Equal(("v2", "v2"), Pair(history, 7));
    }

    [Fact]
    public void Context_scoped_build_uses_frozen_labels_and_keeps_context_components_separate()
    {
        var context = ContextKey.AgentSession("claude", "conversation:one");
        var frozen = new Dictionary<(string Subject, string Scope, string Hash), string>
        {
            [("orders", "whole", "h0")] = "v4",
            [("orders", "whole", "h1")] = "v5",
        };

        var history = CompareHistoryBuilder.Build([
            Row(1, 10, "orders", "whole", "h0", "h1"),
        ], context, frozen);

        Assert.Equal(("v4", "v5"), Pair(history, 1));
        Assert.NotEqual(ContextKey.AgentSession("claude", "a:b"), ContextKey.AgentSession("claude:a", "b"));
        Assert.NotEqual(context, ContextKey.Mcp(10));
    }

    [Fact]
    public void Missing_hashes_and_context_remain_unassigned_and_cases_are_projected()
    {
        var summary = JsonDocument.Parse("""{"regression":{"verdict":"improved"},"cases":[{"index":0,"equivalence":{"status":"equal"},"regression":{"verdict":"regression"} }]}""").RootElement.Clone();
        var rows = new[]
        {
            Row(1, 1, "orders", "whole", null, "h1"),
            Row(2, 1, null, null, "h1", "h2"),
            Row(3, 1, "orders", "whole", "h2", null),
        };

        var history = CompareHistoryBuilder.Build(rows).Select(item => item.OperationId == 1
            ? CompareHistoryBuilder.WithEvidence(item, summary, null)
            : item).ToArray();

        Assert.Null(history.Single(item => item.OperationId == 1).BaselineVersion);
        Assert.Equal("v0", history.Single(item => item.OperationId == 1).CandidateVersion);
        Assert.Null(history.Single(item => item.OperationId == 2).Subject);
        Assert.Null(history.Single(item => item.OperationId == 2).BaselineVersion);
        Assert.Null(history.Single(item => item.OperationId == 3).CandidateVersion);
        Assert.Equal("regression", history.Single(item => item.OperationId == 1).Cases![0].GetProperty("regression").GetProperty("verdict").GetString());
    }

    [Fact]
    public void Ordering_uses_started_at_then_id_and_baseline_before_candidate()
    {
        var first = Row(2, 1, "x", "whole", "same", "next", startedAt: "2026-01-01T00:00:00Z");
        var tie = Row(1, 1, "x", "whole", "early", "same", startedAt: "2026-01-01T00:00:00Z");

        var history = CompareHistoryBuilder.Build([first, tie]);

        Assert.Equal(("v0", "v1"), Pair(history, 1));
        Assert.Equal(("v1", "v2"), Pair(history, 2));
    }

    [Fact]
    public void Measure_hash_uses_the_same_version_sequence_as_compare_hashes()
    {
        var history = CompareHistoryBuilder.Build([
            Row(1, 1, "orders", "whole", "h0", null, startedAt: "2026-01-01T00:00:00Z") with { IsMeasure = true },
            Row(2, 1, "orders", "whole", "h0", "h1", startedAt: "2026-01-02T00:00:00Z"),
        ]);

        Assert.Single(history);
        Assert.Equal(("v0", "v1"), Pair(history, 2));
        Assert.Equal([("v0", "h0"), ("v1", "h1")],
            CompareHistoryBuilder.Subjects(history).Single().ScopeVersions.Single().Versions
                .Select(version => (version.Version, version.Hash)));
    }

    [Fact]
    public void Whole_summary_uses_latest_terminal_attempt_and_only_direct_start_comparison()
    {
        var attempts = CompareHistoryBuilder.Build([
            Row(1, 1, "orders", "whole", "h0", "h1", startedAt: "2026-01-01T00:00:00Z"),
            Row(2, 1, "orders", "whole", "h1", "h2", startedAt: "2026-01-02T00:00:00Z") with { Status = "succeeded" },
            Row(3, 1, "orders", "whole", "h2", "h3", startedAt: "2026-01-03T00:00:00Z") with { Status = "failed" },
            Row(4, 1, "orders", "whole", "h3", "h4", startedAt: "2026-01-04T00:00:00Z") with { Status = "running" },
        ]);

        var subject = Assert.Single(CompareHistoryBuilder.Subjects(attempts));

        Assert.Equal(4, subject.LatestWhole!.OperationId);
        Assert.Equal(3, subject.LatestCompletedWhole!.OperationId);
        Assert.Null(subject.StartToLatestWhole);
        Assert.True(subject.VersionsFromRetainedHistory);
    }

    [Fact]
    public void Subject_summary_includes_complete_hash_versions_for_each_scope()
    {
        var attempts = CompareHistoryBuilder.Build([
            Row(1, 1, "orders", "whole", "h0", "h1", startedAt: "2026-01-01T00:00:00Z"),
            Row(2, 1, "orders", "history", "h4", "h5", startedAt: "2026-01-02T00:00:00Z"),
            Row(3, 1, "orders", "whole", "h1", "h2", startedAt: "2026-01-03T00:00:00Z"),
            Row(4, 1, "orders", "whole", "h2", "h3", startedAt: "2026-01-04T00:00:00Z"),
        ]);

        var subject = Assert.Single(CompareHistoryBuilder.Subjects(attempts));

        Assert.Equal(["history", "whole"], subject.Scopes);
        Assert.Equal([("v0", "h0"), ("v1", "h1"), ("v2", "h2"), ("v3", "h3")],
            subject.ScopeVersions.Single(scope => scope.Scope == "whole").Versions
                .Select(version => (version.Version, version.Hash)));
        Assert.Equal([("v0", "h4"), ("v1", "h5")],
            subject.ScopeVersions.Single(scope => scope.Scope == "history").Versions
                .Select(version => (version.Version, version.Hash)));
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("abandoned")]
    public void Rejected_and_abandoned_whole_attempts_are_terminal(string status)
    {
        var attempts = CompareHistoryBuilder.Build([
            Row(1, 1, "orders", "whole", "h0", "h1", startedAt: "2026-01-01T00:00:00Z"),
            Row(2, 1, "orders", "whole", "h1", "h2", startedAt: "2026-01-02T00:00:00Z") with { Status = status },
            Row(3, 1, "orders", "whole", "h2", "h3", startedAt: "2026-01-03T00:00:00Z") with { Status = "running" },
        ]);

        var subject = Assert.Single(CompareHistoryBuilder.Subjects(attempts));

        Assert.Equal(3, subject.LatestWhole!.OperationId);
        Assert.Equal(status, subject.LatestCompletedWhole!.Status);
    }

    [Fact]
    public void Start_comparison_is_the_direct_v0_to_latest_candidate_trial_with_its_own_conditions()
    {
        using var firstConditionsDocument = JsonDocument.Parse("""{"repeat":3,"timeoutSeconds":30}""");
        using var latestConditionsDocument = JsonDocument.Parse("""{"repeat":5,"timeoutSeconds":60,"omittedSensitiveFields":["parameters"]}""");
        var rows = new[]
        {
            Row(1, 1, "orders", "whole", "h0", "h1", startedAt: "2026-01-01T00:00:00Z"),
            Row(2, 1, "orders", "whole", "h1", "h2", startedAt: "2026-01-02T00:00:00Z"),
            Row(3, 1, "orders", "whole", "h0", "h3", startedAt: "2026-01-03T00:00:00Z"),
        };
        var attempts = CompareHistoryBuilder.Build(rows).Select(item => item.OperationId switch
        {
            1 => CompareHistoryBuilder.WithEvidence(item, null, firstConditionsDocument.RootElement.Clone()),
            3 => CompareHistoryBuilder.WithEvidence(item with { BaselineHash = "h0", CandidateHash = "h3" },
                null, latestConditionsDocument.RootElement.Clone()),
            _ => item,
        }).ToArray();

        var subject = Assert.Single(CompareHistoryBuilder.Subjects(attempts));

        Assert.Equal(3, subject.StartToLatestWhole!.OperationId);
        Assert.Equal(5, subject.StartToLatestWhole.Conditions!.Value.GetProperty("repeat").GetInt32());
        Assert.True(subject.StartToLatestWhole.Conditions.Value.TryGetProperty("omittedSensitiveFields", out _));
    }

    private static CompareHistoryRow Row(long id, long session, string? subject, string? scope,
        string? baseline, string? candidate, string startedAt = "2026-01-01T00:00:00Z") =>
        new(id, session, subject, scope, baseline, candidate, "succeeded", startedAt);

    private static (string? Baseline, string? Candidate) Pair(IReadOnlyList<CompareAttemptSummary> history, long id)
    {
        var item = history.Single(row => row.OperationId == id);
        return (item.BaselineVersion, item.CandidateVersion);
    }
}