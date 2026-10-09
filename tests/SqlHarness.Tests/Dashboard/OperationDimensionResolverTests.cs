using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class OperationDimensionResolverTests
{
    [Fact]
    public void Missing_value_is_distinct_from_literal_unknown_and_separator_values_are_kept_intact()
    {
        var recorded = OperationDimensionResolver.ReadRecordedValues("""{"environment":"Unknown","region":"eu:west|blue"}""");

        var dimensions = OperationDimensionResolver.Resolve(["environment", "region", "tenant"], recorded);

        Assert.Equal(("Unknown", false, "recorded"),
            (dimensions[0].Value, dimensions[0].IsUnknown, dimensions[0].Source));
        Assert.Equal(("eu:west|blue", false), (dimensions[1].Value, dimensions[1].IsUnknown));
        Assert.Equal(("Unknown", true, "unknown"),
            (dimensions[2].Value, dimensions[2].IsUnknown, dimensions[2].Source));
    }

    [Fact]
    public void Invalid_json_is_empty_and_a_non_string_field_does_not_discard_valid_fields()
    {
        Assert.Empty(OperationDimensionResolver.ReadRecordedValues("{"));
        var recorded = OperationDimensionResolver.ReadRecordedValues("""{"valid":"value","bad":42}""");

        Assert.Equal("value", recorded["valid"]);
        Assert.False(recorded.ContainsKey("bad"));
        var malformedDimension = Assert.Single(OperationDimensionResolver.Resolve(
            OperationDimensionResolver.ReadDimensionNames("""{"bad":42}"""), recorded));
        Assert.True(malformedDimension.IsUnknown);

        var duplicate = OperationDimensionResolver.ReadRecordedValues("""{"region":"one","region":"two"}""");
        Assert.False(duplicate.ContainsKey("region"));
    }

    [Fact]
    public void Dimension_filters_match_missing_and_literal_values_separately()
    {
        var recorded = new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "Unknown" };

        Assert.True(OperationDimensionResolver.Matches(
            new Dictionary<string, string?> { ["region"] = "Unknown" }, recorded));
        Assert.False(OperationDimensionResolver.Matches(
            new Dictionary<string, string?> { ["region"] = null }, recorded));
        Assert.True(OperationDimensionResolver.Matches(
            new Dictionary<string, string?> { ["region"] = null }, new Dictionary<string, string>()));
    }
}