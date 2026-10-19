using SqlHarness.Mcp;

using Xunit;

namespace SqlHarness.Mcp.Tests;

public sealed class McpOperationAnnotationsTests
{
    [Fact]
    public void Label_limit_counts_unicode_code_points()
    {
        McpOperationAnnotations.ValidateLabel(string.Concat(Enumerable.Repeat("😀", 120)));

        Assert.Throws<McpMappingException>(() => McpOperationAnnotations.ValidateLabel(string.Concat(Enumerable.Repeat("😀", 121))));
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("line\u2028break")]
    [InlineData("line\u2029break")]
    [InlineData("tab\tvalue")]
    [InlineData("nul\0value")]
    public void Label_rejects_control_characters(string value) =>
        Assert.Throws<McpMappingException>(() => McpOperationAnnotations.ValidateLabel(value));

    [Fact]
    public void Label_rejects_unpaired_surrogates()
    {
        Assert.Throws<McpMappingException>(() => McpOperationAnnotations.ValidateLabel(new string((char)0xD800, 1)));
        Assert.Throws<McpMappingException>(() => McpOperationAnnotations.ValidateLabel(new string((char)0xDC00, 1)));
    }
}