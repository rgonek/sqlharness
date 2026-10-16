using SqlHarness.Core;

namespace SqlHarness.Tests;

public class IndexOverlapClassifierTests
{
    private static MissingIndex Hint(string[] eq, string[] ineq, string[] inc) =>
        new("db", "dbo", "Orders", eq, ineq, inc, 50);

    private static InventoryIndex Ix(int id, string name, string[] keys, string[]? inc = null,
        string type = "NONCLUSTERED", bool disabled = false, bool hypothetical = false, bool filter = false) =>
        new(id, name, type, keys, inc ?? [], false, false, disabled, hypothetical, filter);

    private static InventoryTable Table(params InventoryIndex[] indexes) =>
        new("dbo", "Orders", true, indexes, false);

    [Fact]
    public void Full_key_and_includes_is_covered()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["CustomerId"], ["OrderDate"], ["Total"]),
            Table(Ix(2, "IX_C_D", ["CustomerId", "OrderDate"], ["Total"])));
        Assert.Equal(IndexOverlapVerdict.Covered, overlap.Verdict);
        Assert.Equal("IX_C_D", overlap.Index!.Name);
        Assert.Equal(2, overlap.MatchedKeyColumns);
        Assert.Empty(overlap.MissingIncludeColumns);
    }

    [Fact]
    public void Equality_columns_may_lead_in_any_order()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["B", "A"], [], []), Table(Ix(2, "IX_AB", ["A", "B"])));
        Assert.Equal(IndexOverlapVerdict.Covered, overlap.Verdict);
    }

    [Fact]
    public void Missing_include_is_include_gap_naming_the_columns()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["CustomerId"], [], ["Total", "Status"]),
            Table(Ix(2, "IX_C", ["CustomerId"], ["Status"])));
        Assert.Equal(IndexOverlapVerdict.IncludeGap, overlap.Verdict);
        Assert.Equal(["Total"], overlap.MissingIncludeColumns);
    }

    [Fact]
    public void Leading_prefix_only_is_partial_key()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["CustomerId", "Status"], [], []),
            Table(Ix(2, "IX_C", ["CustomerId"])));
        Assert.Equal(IndexOverlapVerdict.PartialKey, overlap.Verdict);
        Assert.Equal(1, overlap.MatchedKeyColumns);
        Assert.Equal(2, overlap.HintKeyColumns);
    }

    [Fact]
    public void Non_leading_match_is_new_shape()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["Status"], [], []),
            Table(Ix(2, "IX_C_S", ["CustomerId", "Status"])));
        Assert.Equal(IndexOverlapVerdict.NewShape, overlap.Verdict);
        Assert.Null(overlap.Index);
    }

    [Fact]
    public void Filtered_index_is_never_better_than_partial_key()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["CustomerId"], [], []),
            Table(Ix(2, "IX_C_f", ["CustomerId"], filter: true)));
        Assert.Equal(IndexOverlapVerdict.PartialKey, overlap.Verdict);
        Assert.True(overlap.Index!.Filtered);
    }

    [Theory]
    [InlineData("NONCLUSTERED", true, false)]
    [InlineData("NONCLUSTERED", false, true)]
    [InlineData("NONCLUSTERED COLUMNSTORE", false, false)]
    [InlineData("HEAP", false, false)]
    public void Disabled_hypothetical_columnstore_and_heap_never_match(string type, bool disabled, bool hypothetical)
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["CustomerId"], [], []),
            Table(Ix(2, "IX", ["CustomerId"], type: type, disabled: disabled, hypothetical: hypothetical)));
        Assert.Equal(IndexOverlapVerdict.NewShape, overlap.Verdict);
    }

    [Fact]
    public void Clustered_index_covers_every_column()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["OrderId"], [], ["Total", "Status"]),
            Table(Ix(1, "PK_Orders", ["OrderId"], type: "CLUSTERED")));
        Assert.Equal(IndexOverlapVerdict.Covered, overlap.Verdict);
    }

    [Fact]
    public void Nonclustered_index_covers_the_clustered_key_as_row_locator()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["CustomerId"], [], ["OrderId"]),
            Table(Ix(1, "PK_Orders", ["OrderId"], type: "CLUSTERED"), Ix(2, "IX_C", ["CustomerId"])));
        Assert.Equal(IndexOverlapVerdict.Covered, overlap.Verdict);
        Assert.Equal("IX_C", overlap.Index!.Name);
    }

    [Fact]
    public void Strongest_match_wins_then_more_key_columns_then_fewer_missing_includes_then_lower_index_id()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["A", "B"], [], ["X"]),
            Table(Ix(3, "IX_A", ["A"]), Ix(4, "IX_AB_noX", ["A", "B"]), Ix(5, "IX_AB_X", ["A", "B"], ["X"])));
        Assert.Equal("IX_AB_X", overlap.Index!.Name);
    }

    [Fact]
    public void Column_names_compare_case_insensitively()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["customerid"], [], []), Table(Ix(2, "IX_C", ["CustomerId"])));
        Assert.Equal(IndexOverlapVerdict.Covered, overlap.Verdict);
    }
}