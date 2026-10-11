using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class SafeTruncationTests
{
    [Fact]
    public void Short_values_are_returned_unchanged() =>
        Assert.Equal("abc", SafeTruncation.Cap("abc", 3));

    [Fact]
    public void Plain_text_is_cut_to_the_limit() =>
        Assert.Equal("ab", SafeTruncation.Cap("abcd", 2));

    [Fact]
    public void A_cut_inside_a_surrogate_pair_backs_off_one_unit()
    {
        var value = "ab😀cd"; // units: a b high low c d
        var capped = SafeTruncation.Cap(value, 3);

        Assert.Equal("ab", capped);
        Assert.Equal(capped, new string(capped.Where(c => !char.IsSurrogate(c)).ToArray()));
    }

    [Fact]
    public void A_cut_after_a_whole_pair_keeps_it() =>
        Assert.Equal("ab😀", SafeTruncation.Cap("ab😀cd", 4));

    [Fact]
    public void Zero_limit_yields_empty() =>
        Assert.Equal(string.Empty, SafeTruncation.Cap("😀", 0));
}