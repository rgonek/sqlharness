using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class CompareContextTests
{
    [Fact]
    public void Parse_preserves_valid_labels_and_whole_exactly()
    {
        var parsed = Parse("orders-report", "whole", required: true);

        Assert.Equal(new CompareContext("orders-report", "whole"), parsed);
    }

    [Theory]
    [InlineData("", "whole", true)]
    [InlineData("   ", "whole", true)]
    [InlineData("Orders", "whole", true)]
    [InlineData("orders/a", "whole", true)]
    [InlineData("orders", "White", true)]
    [InlineData("orders", null, false)]
    [InlineData("x", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    public void Parse_rejects_invalid_or_incomplete_context(string? subject, string? scope, bool required)
    {
        Assert.ThrowsAny<ArgumentException>(() => Parse(subject, scope, required));
    }

    [Fact]
    public void Parse_accepts_64_character_labels_and_off_with_no_fields()
    {
        var label = "a" + new string('b', 63);
        var parsed = Parse(label, label, required: true);
        Assert.Equal(new CompareContext(label, label), parsed);
        Assert.Null(Parse(null, null, required: false));
    }

    private static CompareContext? Parse(string? subject, string? scope, bool required) =>
        CompareContext.Parse(subject, scope, required);
}