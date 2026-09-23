using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class IndexOverlapClassifierTests
{
    // IndexCandidate and ExistingIndex are internal, so a public theory cannot name them.
    [Theory]
    [MemberData(nameof(Cases))]
    public void Finds_expected_best_match(
        object candidate, object indexes, IndexOverlapClassification expected)
    {
        Assert.Equal(expected,
            IndexOverlapClassifier.FindBest(
                (IndexCandidate)candidate,
                (ExistingIndex[])indexes).Classification);
    }

    public static TheoryData<object, object, IndexOverlapClassification> Cases()
    {
        var cases = new TheoryData<object, object, IndexOverlapClassification>();

        // Equality columns match a leading key segment in any order.
        cases.Add(
            Candidate(["TenantId", "Status"]),
            Indexes(Index(1, ["Status", "TenantId"], type: "CLUSTERED")),
            IndexOverlapClassification.Covered);

        // Inequality columns then match in candidate order.
        cases.Add(
            Candidate(["TenantId"], ["CreatedAt", "Amount"]),
            Indexes(Index(1, ["TenantId", "CreatedAt", "Amount"])),
            IndexOverlapClassification.Covered);

        // A reversed inequality sequence is only a partial key.
        cases.Add(
            Candidate(["TenantId"], ["CreatedAt", "Amount"]),
            Indexes(Index(1, ["TenantId", "Amount", "CreatedAt"])),
            IndexOverlapClassification.PartialKey);

        // An inequality gap stops the ordered match.
        cases.Add(
            Candidate(["TenantId"], ["CreatedAt", "Amount"]),
            Indexes(Index(1, ["TenantId", "CreatedAt", "Extra", "Amount"])),
            IndexOverlapClassification.PartialKey);

        // Trailing existing key columns do not break a full key.
        cases.Add(
            Candidate(["TenantId"], ["CreatedAt", "Amount"]),
            Indexes(Index(1, ["TenantId", "CreatedAt", "Amount", "ExtraId"])),
            IndexOverlapClassification.Covered);

        // INCLUDE columns cover the candidate includes, in any include order.
        cases.Add(
            Candidate(["TenantId"], include: ["Name", "Status"]),
            Indexes(Index(1, ["TenantId"], ["Status", "Name"])),
            IndexOverlapClassification.Covered);

        // A candidate include that is an existing key column is covered with an empty INCLUDE list.
        cases.Add(
            Candidate(["TenantId", "Name"], include: ["Name"]),
            Indexes(Index(1, ["TenantId", "Name"])),
            IndexOverlapClassification.Covered);

        // Full key, and some candidate includes are absent.
        cases.Add(
            Candidate(["TenantId"], include: ["Notes", "Name", "Status"]),
            Indexes(Index(1, ["TenantId"], ["Name"])),
            IndexOverlapClassification.IncludeGap);

        // A proper leading equality prefix is a partial key.
        cases.Add(
            Candidate(["TenantId", "Status"], ["CreatedAt"]),
            Indexes(Index(1, ["TenantId"])),
            IndexOverlapClassification.PartialKey);

        // The same columns later in the key are not a leading overlap.
        cases.Add(
            Candidate(["TenantId", "Status"]),
            Indexes(Index(1, ["Other", "Status", "TenantId"])),
            IndexOverlapClassification.NewShape);

        // No shared leading column.
        cases.Add(
            Candidate(["TenantId"]),
            Indexes(Index(1, ["OtherId"])),
            IndexOverlapClassification.NewShape);

        cases.Add(
            Candidate(["TenantId"]),
            Indexes(),
            IndexOverlapClassification.NewShape);

        // Another table, even with the same shape, does not compete.
        cases.Add(
            Candidate(["TenantId"], include: ["Name"]),
            Indexes(Index(1, ["TenantId"], ["Name"], table: "Orders")),
            IndexOverlapClassification.NewShape);

        // Another schema does not compete.
        cases.Add(
            Candidate(["TenantId"], include: ["Name"]),
            Indexes(Index(1, ["TenantId"], ["Name"], schema: "sales")),
            IndexOverlapClassification.NewShape);

        // A structurally covered filtered index is partial-key.
        cases.Add(
            Candidate(["TenantId"], include: ["Name"]),
            Indexes(Index(1, ["TenantId"], ["Name"], hasFilter: true)),
            IndexOverlapClassification.PartialKey);

        // A filtered leading overlap stays partial-key.
        cases.Add(
            Candidate(["TenantId", "Status"]),
            Indexes(Index(1, ["TenantId"], hasFilter: true)),
            IndexOverlapClassification.PartialKey);

        // Heaps do not compete, so the non-heap include gap wins.
        cases.Add(
            Candidate(["TenantId"], include: ["Name", "Status"]),
            Indexes(
                Index(1, ["TenantId"], ["Name", "Status"], type: "HeAp"),
                Index(2, ["TenantId"], ["Name"])),
            IndexOverlapClassification.IncludeGap);

        // A heap alone is not a match.
        cases.Add(
            Candidate(["TenantId"], include: ["Name"]),
            Indexes(Index(0, ["TenantId"], ["Name"], type: "HEAP")),
            IndexOverlapClassification.NewShape);

        // Column identity is ordinal-ignore-case.
        cases.Add(
            Candidate(["TenantId"], include: ["Name"]),
            Indexes(Index(1, ["tenantid"], ["nAmE"])),
            IndexOverlapClassification.Covered);

        // Schema and table identity is ordinal-ignore-case.
        cases.Add(
            Candidate(["TenantId"], schema: "DBO", table: "contracts"),
            Indexes(Index(1, ["TenantId"], schema: "dbo", table: "Contracts")),
            IndexOverlapClassification.Covered);

        // Strength outranks enabled state: a disabled covered index beats an enabled partial key.
        cases.Add(
            Candidate(["TenantId", "Status"]),
            Indexes(
                Index(1, ["TenantId", "Status"], disabled: true),
                Index(2, ["TenantId"])),
            IndexOverlapClassification.Covered);

        // Downgrade before selection: an unfiltered include gap beats a filtered covered index.
        cases.Add(
            Candidate(["TenantId"], include: ["Name", "Status"]),
            Indexes(
                Index(1, ["TenantId"], ["Name", "Status"], hasFilter: true),
                Index(9, ["TenantId"], ["Name"])),
            IndexOverlapClassification.IncludeGap);

        // A later equality column does not count once an inequality column has started the key.
        cases.Add(
            Candidate(["TenantId"], ["CreatedAt"]),
            Indexes(Index(1, ["CreatedAt", "TenantId"])),
            IndexOverlapClassification.PartialKey);

        return cases;
    }

    [Fact]
    public void Include_gap_lists_absent_columns_in_candidate_order()
    {
        var candidate = Candidate(["TenantId"], include: ["Status", "Name", "Notes"]);
        var missingThree = Index(1, ["TenantId"]);
        var missingTwo = Index(8, ["tenantid"], ["name"]);

        var match = IndexOverlapClassifier.FindBest(candidate, [missingThree, missingTwo]);

        Assert.Equal(IndexOverlapClassification.IncludeGap, match.Classification);
        Assert.Same(missingTwo, match.BestIndex);
        Assert.Equal(["Status", "Notes"], match.MissingIncludeColumns);
        Assert.Equal(1, match.MatchedKeyColumnCount);
        Assert.Equal(1, match.CandidateKeyColumnCount);
    }

    [Fact]
    public void Filtered_include_gap_is_partial_key_and_keeps_missing_includes()
    {
        var candidate = Candidate(["TenantId"], ["CreatedAt"], ["Status", "Name", "Notes"]);
        var index = Index(3, ["TenantId", "CreatedAt"], ["Name"], hasFilter: true);

        var match = IndexOverlapClassifier.FindBest(candidate, [index]);

        Assert.Equal(IndexOverlapClassification.PartialKey, match.Classification);
        Assert.Same(index, match.BestIndex);
        Assert.Equal(["Status", "Notes"], match.MissingIncludeColumns);
        Assert.Equal(2, match.MatchedKeyColumnCount);
        Assert.Equal(2, match.CandidateKeyColumnCount);
    }

    [Fact]
    public void Filtered_covered_index_keeps_full_key_counts_and_no_missing_includes()
    {
        var candidate = Candidate(["TenantId", "Status"], ["CreatedAt"], ["Name"]);
        var index = Index(2, ["Status", "TenantId", "CreatedAt", "Extra"], ["Name"], hasFilter: true);

        var match = IndexOverlapClassifier.FindBest(candidate, [index]);

        Assert.Equal(IndexOverlapClassification.PartialKey, match.Classification);
        Assert.Same(index, match.BestIndex);
        Assert.Equal(3, match.MatchedKeyColumnCount);
        Assert.Equal(3, match.CandidateKeyColumnCount);
        Assert.Empty(match.MissingIncludeColumns);
    }

    [Fact]
    public void Enabled_index_wins_tie_against_lower_disabled_id()
    {
        var candidate = Candidate(["TenantId"], include: ["Name"]);
        var disabled = Index(1, ["TenantId"], ["Name"], disabled: true);
        var enabled = Index(6, ["TenantId"], ["Name"]);

        var match = IndexOverlapClassifier.FindBest(candidate, [disabled, enabled]);

        Assert.Equal(IndexOverlapClassification.Covered, match.Classification);
        Assert.Same(enabled, match.BestIndex);
        Assert.Empty(match.MissingIncludeColumns);
        Assert.Equal(1, match.MatchedKeyColumnCount);
        Assert.Equal(1, match.CandidateKeyColumnCount);
    }

    [Fact]
    public void Lower_index_id_wins_when_the_other_rank_keys_match()
    {
        var candidate = Candidate(["TenantId"], include: ["Name"]);
        var higher = Index(7, ["TenantId"], ["Name"], unique: true, primaryKey: true);
        var lower = Index(3, ["TenantId"], ["Name"]);

        var enabled = IndexOverlapClassifier.FindBest(candidate, [higher, lower]);

        Assert.Same(lower, enabled.BestIndex);

        var higherDisabled = Index(7, ["TenantId"], ["Name"], disabled: true, unique: true, primaryKey: true);
        var lowerDisabled = Index(3, ["TenantId"], ["Name"], disabled: true);
        var disabled = IndexOverlapClassifier.FindBest(candidate, [higherDisabled, lowerDisabled]);

        Assert.Same(lowerDisabled, disabled.BestIndex);
    }

    [Fact]
    public void More_matched_keys_beats_a_lower_index_id()
    {
        var candidate = Candidate(["TenantId", "Status", "Region"], ["CreatedAt"]);
        var shorter = Index(1, ["TenantId"]);
        var longer = Index(5, ["TenantId", "Status"]);

        var match = IndexOverlapClassifier.FindBest(candidate, [shorter, longer]);

        Assert.Equal(IndexOverlapClassification.PartialKey, match.Classification);
        Assert.Same(longer, match.BestIndex);
        Assert.Equal(2, match.MatchedKeyColumnCount);
        Assert.Equal(4, match.CandidateKeyColumnCount);
        Assert.Empty(match.MissingIncludeColumns);
    }

    [Fact]
    public void Fewer_missing_includes_beats_an_enabled_lower_id()
    {
        var candidate = Candidate(["TenantId"], include: ["Status", "Name", "Notes"]);
        var enabledMore = Index(1, ["TenantId"], ["Name"]);
        var disabledFewer = Index(4, ["TenantId"], ["Name", "Notes"], disabled: true);

        var match = IndexOverlapClassifier.FindBest(candidate, [enabledMore, disabledFewer]);

        Assert.Equal(IndexOverlapClassification.IncludeGap, match.Classification);
        Assert.Same(disabledFewer, match.BestIndex);
        Assert.Equal(["Status"], match.MissingIncludeColumns);
    }

    [Fact]
    public void Partial_overlap_counts_leading_equality_and_ordered_inequality()
    {
        var candidate = Candidate(["TenantId", "Status"], ["CreatedAt", "Amount"]);
        var index = Index(2, ["Status", "TenantId", "CreatedAt", "Extra"]);

        var match = IndexOverlapClassifier.FindBest(candidate, [index]);

        Assert.Equal(IndexOverlapClassification.PartialKey, match.Classification);
        Assert.Same(index, match.BestIndex);
        Assert.Equal(3, match.MatchedKeyColumnCount);
        Assert.Equal(4, match.CandidateKeyColumnCount);
        Assert.Empty(match.MissingIncludeColumns);
    }

    [Fact]
    public void New_shape_has_null_best_index_and_empty_missing_includes()
    {
        var candidate = Candidate(["TenantId", "Status"], ["CreatedAt"], ["Name"]);
        var match = IndexOverlapClassifier.FindBest(candidate, [
            Index(0, ["TenantId", "Status", "CreatedAt"], ["Name"], type: "HEAP"),
            Index(2, ["TenantId", "Status", "CreatedAt"], ["Name"], schema: "other"),
            Index(3, ["TenantId"], table: "Orders"),
            Index(4, ["Unrelated"]),
        ]);

        Assert.Equal(IndexOverlapClassification.NewShape, match.Classification);
        Assert.Null(match.BestIndex);
        Assert.Equal(0, match.MatchedKeyColumnCount);
        Assert.Equal(3, match.CandidateKeyColumnCount);
        Assert.Empty(match.MissingIncludeColumns);
    }

    private static ExistingIndex[] Indexes(params ExistingIndex[] indexes) => indexes;

    private static IndexCandidate Candidate(
        string[]? equality = null,
        string[]? inequality = null,
        string[]? include = null,
        string schema = "dbo",
        string table = "Contracts") =>
        new(
            11,
            schema,
            table,
            equality ?? [],
            inequality ?? [],
            include ?? [],
            3,
            4,
            2.5m,
            50m,
            99.25m,
            null,
            null);

    private static ExistingIndex Index(
        int id,
        string[] key,
        string[]? include = null,
        string type = "NONCLUSTERED",
        bool disabled = false,
        bool hasFilter = false,
        string schema = "dbo",
        string table = "Contracts",
        bool unique = false,
        bool primaryKey = false) =>
        new(
            schema,
            table,
            id,
            "IX_" + id,
            type,
            key,
            new bool[key.Length],
            include ?? [],
            unique,
            primaryKey,
            false,
            disabled,
            hasFilter,
            hasFilter ? "ABC" : null,
            "");
}