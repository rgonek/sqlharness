using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class OptimizationSummaryBuilderTests
{
    [Fact]
    public void Groups_by_subject_database_and_fingerprint()
    {
        var section = OptimizationSummaryBuilder.Build([
            Compare(1, "orders", "whole", "db1", "f1", "h0", "h1"),
            Compare(2, "orders", "whole", "db2", "f1", "h0", "h1"),
            Compare(3, "orders", "whole", "db1", "f2", "h0", "h1"),
        ]);

        Assert.Equal(2, section.Subjects.Count);
        var db1 = section.Subjects.Single(subject => subject.Database == "db1");
        var db2 = section.Subjects.Single(subject => subject.Database == "db2");
        Assert.Equal(2, db1.Groups.Count);
        Assert.Equal("orders", Assert.Single(db1.Groups, group => group.Fingerprint == "f1").Subject);
        Assert.Equal("db1", Assert.Single(db1.Groups, group => group.Fingerprint == "f1").Database);
        Assert.Equal("db2", Assert.Single(db2.Groups).Database);
    }

    [Fact]
    public void Provisional_suffix_continues_after_the_highest_frozen_label()
    {
        var frozen = new Dictionary<(string Subject, string Scope, string Hash), string>
        {
            [("orders", "whole", "h0")] = "v4",
            [("orders", "whole", "h1")] = "v5",
        };

        var section = OptimizationSummaryBuilder.Build([
            Compare(1, "orders", "whole", "db", "f", "h0", "h1"),
            Compare(2, "orders", "whole", "db", "f", "h1", "h2"),
        ], ContextKey.AgentSession("claude", "conversation"), frozen, provisional: true);

        var versions = Assert.Single(Assert.Single(Assert.Single(section.Subjects).Groups).Scopes).Versions;
        Assert.Equal(new[] { ("h0", "v4"), ("h1", "v5"), ("h2", "v6") },
            versions.Select(version => (version.Hash, version.Version)).ToArray());
        Assert.True(section.VersionLabelsProvisional);
        Assert.Equal("versions may renumber while matching catches up", section.VersionLabelNote);
    }

    [Fact]
    public void Provisional_labels_reserve_ordinals_for_frozen_hashes_absent_from_retained_evidence()
    {
        var frozen = new Dictionary<(string Subject, string Scope, string Hash), string>
        {
            [("orders", "whole", "expired-hash-4")] = "v4",
            [("orders", "whole", "expired-hash-5")] = "v5",
        };

        var section = OptimizationSummaryBuilder.Build([
            Compare(1, "orders", "whole", "db", "f", "new-hash", "new-hash"),
        ], ContextKey.AgentSession("claude", "conversation"), frozen, provisional: true);

        var versions = Assert.Single(Assert.Single(Assert.Single(section.Subjects).Groups).Scopes).Versions;
        Assert.Equal("v6", Assert.Single(versions).Version);
        Assert.Equal("new-hash", Assert.Single(versions).Hash);
    }

    [Fact]
    public void Version_in_two_groups_is_listed_in_each_with_that_groups_numbers()
    {
        var section = OptimizationSummaryBuilder.Build([
            Compare(1, "orders", "whole", "db", "f1", "h0", "h1", reads: (100, 70)),
            Compare(2, "orders", "whole", "db", "f2", "h0", "h1", reads: (200, 160)),
        ]);

        var groups = section.Subjects.Single().Groups;
        Assert.Equal(100, groups.Single(group => group.Fingerprint == "f1").Scopes.Single().Versions.Single(version => version.Version == "v0").LogicalReadsSum);
        Assert.Equal(200, groups.Single(group => group.Fingerprint == "f2").Scopes.Single().Versions.Single(version => version.Version == "v0").LogicalReadsSum);
    }

    [Fact]
    public void Default_pair_follows_equivalence_chain()
    {
        var section = OptimizationSummaryBuilder.Build([
            Compare(1, "orders", "whole", "db", "f", "h0", "h1"),
            Compare(2, "orders", "whole", "db", "f", "h1", "h2"),
            Compare(3, "orders", "whole", "db", "f", "h2", "h3", equivalent: false),
        ]);

        var group = Assert.Single(Assert.Single(section.Subjects).Groups);
        Assert.Equal("v0", group.DefaultA);
        Assert.Equal("v2", group.DefaultB);
    }

    [Fact]
    public void Default_pair_continues_past_a_failed_edge_to_later_equivalent_branch()
    {
        var section = OptimizationSummaryBuilder.Build([
            Compare(1, "orders", "whole", "db", "f", "h0", "h1", equivalent: false),
            Compare(2, "orders", "whole", "db", "f", "h0", "h2"),
        ]);

        var group = Assert.Single(Assert.Single(section.Subjects).Groups);
        Assert.Equal("v0", group.DefaultA);
        Assert.Equal("v2", group.DefaultB);
    }

    [Fact]
    public void Equivalence_off_falls_back_to_newest_version()
    {
        var section = OptimizationSummaryBuilder.Build([
            Compare(1, "orders", "whole", "db", "f", "h0", "h1", comparisonMode: "Off"),
        ]);

        Assert.Equal("v1", Assert.Single(Assert.Single(section.Subjects).Groups).DefaultB);
    }

    [Fact]
    public void Case_outcome_uses_logical_read_margins_and_results_equivalence()
    {
        var compare = Compare(1, "orders", "whole", "db", "f", "h0", "h1") with
        {
            Cases =
            [
                new CompareCaseEvidence("0", "@CustomerId = 1", null, true, null),
                new CompareCaseEvidence("1", "@CustomerId = 2", null, true, null),
                new CompareCaseEvidence("2", "@CustomerId = 3", null, true, null),
            ],
            Metrics =
            [
                Metric("baseline", "0", 1000), Metric("candidate", "0", 980),
                Metric("baseline", "1", 1000), Metric("candidate", "1", 1020),
                Metric("baseline", "2", 1000), Metric("candidate", "2", 995),
            ],
        };

        var section = OptimizationSummaryBuilder.Build([compare]);
        var group = Assert.Single(Assert.Single(section.Subjects).Groups);
        var candidate = Assert.Single(group.Scopes).Versions.Single(version => version.Version == "v1");

        Assert.Equal("3 cases: @CustomerId = 1, 2, 3", group.Label);
        Assert.Equal("1 better · 1 worse", candidate.CaseOutcome);
        Assert.Equal(2995, candidate.LogicalReadsSum);
        Assert.Equal(998, candidate.LogicalReadsMean);
    }

    [Fact]
    public void Version_table_outcome_uses_the_same_changed_statement_basis()
    {
        var compare = Compare(203, "orders", "whole", "db", "f", "h0", "h1", reads: (12010, 11960)) with
        {
            Metrics =
            [
                Metric("baseline", "0", 12010) with { Statements = [Statement("shared", 11910, "same-plan"), Statement("changed", 100, "old-plan")], StatementAttribution = "confirmed" },
                Metric("candidate", "0", 11960) with { Statements = [Statement("shared", 11910, "same-plan"), Statement("changed", 50, "new-plan")], StatementAttribution = "confirmed" },
            ],
        };

        var section = OptimizationSummaryBuilder.Build([compare]);
        var candidate = Assert.Single(Assert.Single(Assert.Single(section.Subjects).Groups).Scopes).Versions.Single(version => version.Version == "v1");

        Assert.Equal("1 better", candidate.CaseOutcome);
        Assert.Equal("changed statements", candidate.VerdictBasis);
    }

    [Fact]
    public void Candidate_outcome_is_incomplete_when_any_baseline_case_metric_is_missing()
    {
        var compare = Compare(1, "orders", "whole", "db", "f", "h0", "h1") with
        {
            Cases =
            [
                new CompareCaseEvidence("0", "@CustomerId = 1", null, true, null),
                new CompareCaseEvidence("1", "@CustomerId = 2", null, true, null),
            ],
            Metrics =
            [
                Metric("baseline", "0", 100), Metric("candidate", "0", 100),
                Metric("candidate", "1", 100),
            ],
        };

        var candidate = Assert.Single(Assert.Single(Assert.Single(OptimizationSummaryBuilder.Build([compare]).Subjects).Groups).Scopes)
            .Versions.Single(version => version.Version == "v1");

        Assert.Equal("incomplete", candidate.CaseOutcome);
    }

    [Fact]
    public void Newest_measurement_wins_per_version_and_keeps_all_measurement_ids()
    {
        var section = OptimizationSummaryBuilder.Build([
            Compare(201, "orders", "whole", "db", "f", "h0", "h1", reads: (100, 80)),
            Compare(214, "orders", "whole", "db", "f", "h0", "h1", reads: (150, 90)),
        ]);

        var row = Assert.Single(Assert.Single(Assert.Single(section.Subjects).Groups).Scopes).Versions.Single(version => version.Version == "v0");
        Assert.Equal(150, row.LogicalReadsSum);
        Assert.Equal([201L, 214L], row.MeasuredIn);
        Assert.Equal(214, row.LatestOperationId);
    }

    [Fact]
    public void Whole_query_sorts_before_fragments()
    {
        var section = OptimizationSummaryBuilder.Build([
            Compare(1, "orders", "z-fragment", "db", "f", "h0", "h1"),
            Compare(2, "orders", "whole", "db", "f", "h0", "h1"),
            Compare(3, "orders", "a-fragment", "db", "f", "h0", "h1"),
        ]);

        Assert.Equal(["whole query", "a-fragment", "z-fragment"],
            Assert.Single(Assert.Single(section.Subjects).Groups).Scopes.Select(scope => scope.Scope));
    }

    [Fact]
    public void Unassigned_compares_are_listed()
    {
        var section = OptimizationSummaryBuilder.Build([
            Compare(9, null, null, "db", "f", "h0", "h1", status: "failed"),
        ]);

        Assert.Empty(section.Subjects);
        Assert.Equal(new UnassignedCompare(9, "2026-10-07T09:00:09Z", "failed"), Assert.Single(section.Unassigned));
    }

    [Fact]
    public void Measure_only_group_defaults_to_earliest_and_newest_and_marks_observations_measured_only()
    {
        var section = OptimizationSummaryBuilder.Build([
            Measure(1, "orders", "whole", "db", "f", "h0", 100),
            Measure(2, "orders", "whole", "db", "f", "h1", 70),
        ]);

        var group = Assert.Single(Assert.Single(section.Subjects).Groups);
        Assert.Equal(("v0", "v1"), (group.DefaultA, group.DefaultB));
        Assert.Equal(["measured only", "measured only"], group.Scopes.Single().Versions.Select(row => row.CaseOutcome));
        Assert.Equal([100L, 70L], group.Scopes.Single().Versions.Select(row => row.LogicalReadsSum));
    }

    [Fact]
    public void Measure_without_subject_is_not_versioned()
    {
        var section = OptimizationSummaryBuilder.Build([Measure(1, null, "whole", "db", "f", "h0", 100)]);

        Assert.Empty(section.Subjects);
        Assert.Empty(section.Unassigned);
    }

    [Theory]
    [InlineData(false, 4L)]
    [InlineData(true, null)]
    public void Version_row_sums_complete_case_counts_and_preserves_missing(bool missing, long? expected)
    {
        var compare = Compare(1, "orders", "whole", "db", "f", "h0", "h1") with
        {
            Cases = [new("0", "a", null, true, null), new("1", "b", null, true, null), new("2", "c", null, true, null)],
            Metrics = [Metric("baseline", "0", 1) with { RowsReturned = 1 },
                Metric("baseline", "1", 1) with { RowsReturned = 0 },
                Metric("baseline", "2", 1) with { RowsReturned = missing ? null : 3 }],
        };
        var row = Assert.Single(Assert.Single(Assert.Single(OptimizationSummaryBuilder.Build([compare]).Subjects).Groups).Scopes)
            .Versions.Single(v => v.Version == "v0");
        Assert.Equal(expected, row.RowsReturnedSum);
    }

    [Fact]
    public void Version_row_reports_variation_only_when_a_case_asserts_it()
    {
        var compare = Compare(1, "orders", "whole", "db", "f", "h0", "h1") with
        {
            Cases = [new("0", "a", null, true, null), new("1", "b", null, true, null)],
            Metrics = [Metric("baseline", "0", 1) with { RowsReturnedVaries = true }],
        };
        var row = Assert.Single(Assert.Single(Assert.Single(OptimizationSummaryBuilder.Build([compare]).Subjects).Groups).Scopes)
            .Versions.Single(v => v.Version == "v0");
        Assert.Null(row.RowsReturnedSum);
        Assert.True(row.RowsReturnedVaries);
    }

    private static CompareEvidence Compare(long id, string? subject, string? scope, string database, string fingerprint,
        string baselineHash, string candidateHash, (long Baseline, long Candidate) reads = default,
        bool equivalent = true, string comparisonMode = "Ordered", string status = "succeeded") =>
        new(id, subject, scope, database, "sqlserver", fingerprint, baselineHash, candidateHash,
            $"2026-10-07T09:00:{id % 60:D2}Z", status, comparisonMode, equivalent, null,
            [new CompareCaseEvidence("0", "@CustomerId = 1", null, equivalent, null)],
            [Metric("baseline", reads.Baseline), Metric("candidate", reads.Candidate)]);

    private static CompareEvidence Measure(long id, string? subject, string? scope, string database, string fingerprint,
        string hash, long reads) =>
        new(id, subject, scope, database, "sqlserver", fingerprint, hash, null,
            $"2026-10-07T09:00:{id % 60:D2}Z", "succeeded", null, null, null,
            [new CompareCaseEvidence("set-a", "set-a", "set-a", null, null)],
            [new CompareMetricEvidence("measure", "set-a", "set-a", reads, reads, 1, [])])
        { IsMeasure = true };

    private static CompareMetricEvidence Metric(string variant, long reads) => Metric(variant, "0", reads);

    private static CompareMetricEvidence Metric(string variant, string caseId, long reads) =>
        new(variant, caseId, $"@CustomerId = {int.Parse(caseId, System.Globalization.CultureInfo.InvariantCulture) + 1}", reads, reads, 12, []);

    private static CompareStatementEvidence Statement(string key, long reads, string planHash) =>
        new(key, 0, key, planHash, "confirmed", reads, 1, 1, [], []);
}