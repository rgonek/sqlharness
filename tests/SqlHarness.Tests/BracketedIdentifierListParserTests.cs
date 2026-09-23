using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class BracketedIdentifierListParserTests
{
    private const string MalformedMessage = "Missing-index column metadata is malformed.";

    [Theory]
    [InlineData("[A], [B]", new[] { "A", "B" })]
    [InlineData("[A,B], [C]]D]", new[] { "A,B", "C]D" })]
    public void Parser_handles_commas_and_escaped_brackets(
        string text, string[] expected)
    {
        Assert.Equal(expected,
            BracketedIdentifierListParser.ParseAndResolve(text, expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    [InlineData(" \t\r\n ")]
    public void Null_or_blank_returns_empty(string? text)
    {
        Assert.Empty(BracketedIdentifierListParser.ParseAndResolve(text, ["Status", "STATUS"]));
    }

    [Fact]
    public void Surrounding_whitespace_outside_brackets_is_ignored()
    {
        Assert.Equal<string>(["A", "B"],
            BracketedIdentifierListParser.ParseAndResolve("  [A] , [B]  ", ["B", "A"]));
        Assert.Equal<string>(["A", "B"],
            BracketedIdentifierListParser.ParseAndResolve("\t[A]\r\n,\r\n[B]\t", ["A", "B"]));
    }

    [Fact]
    public void Whitespace_inside_brackets_is_part_of_the_name()
    {
        Assert.Equal<string>([" A "],
            BracketedIdentifierListParser.ParseAndResolve("[ A ]", ["A", " A "]));
        Assert.Equal<string>(["\tA"],
            BracketedIdentifierListParser.ParseAndResolve("[\tA]", ["\tA", "A"]));
        AssertMalformed("[ A ]", ["A"], "A");
    }

    [Fact]
    public void Space_only_name_resolves_against_the_catalog()
    {
        Assert.Equal<string>([" "],
            BracketedIdentifierListParser.ParseAndResolve("[ ]", ["Status", " "]));
    }

    [Fact]
    public void Resolution_returns_catalog_spelling_in_parse_order()
    {
        Assert.Equal<string>(["Status", "TenantId"],
            BracketedIdentifierListParser.ParseAndResolve(
                "[status], [TENANTID]",
                ["TenantId", "Status"]));
    }

    [Fact]
    public void Unicode_names_round_trip()
    {
        string[] catalog = ["Łódź", "名前"];
        Assert.Equal<string>(catalog,
            BracketedIdentifierListParser.ParseAndResolve("[Łódź], [名前]", catalog));
        Assert.Equal<string>(["Łódź"],
            BracketedIdentifierListParser.ParseAndResolve("[łódź]", ["Łódź"]));
    }

    [Fact]
    public void Bracket_outside_starts_the_next_identifier()
    {
        Assert.Equal<string>(["A", "B"],
            BracketedIdentifierListParser.ParseAndResolve("[A][B]", ["A", "B"]));
        Assert.Equal<string>(["A]"],
            BracketedIdentifierListParser.ParseAndResolve("[A]]]", ["A]"]));
    }

    [Fact]
    public void Duplicate_case_insensitive_name_throws_without_echoing_input()
    {
        AssertMalformed("[Status], [status]", ["Status"], "Status", "status");
        AssertMalformed("[Status], [Status]", ["Status"], "Status");
        AssertMalformed("[Łódź], [łódź]", ["Łódź"], "Łódź", "łódź");
    }

    [Fact]
    public void Unknown_column_throws_without_echoing_input()
    {
        AssertMalformed("[NotInCatalog]", ["Status", "TenantId"], "NotInCatalog", "Status", "TenantId");
    }

    [Fact]
    public void Ambiguous_case_insensitive_catalog_match_throws_without_echoing_input()
    {
        AssertMalformed("[status]", ["Status", "STATUS"], "status", "Status", "STATUS");
        AssertMalformed("[Status]", ["Status", "Status"], "Status");
    }

    [Theory]
    [InlineData("A")]
    [InlineData("A, [B]")]
    [InlineData("[A")]
    [InlineData("[")]
    [InlineData("[A]]")]
    [InlineData("[A],")]
    [InlineData("[A], ")]
    [InlineData(", [A]")]
    [InlineData(" ,[A]")]
    [InlineData("[A],, [B]")]
    [InlineData("[A], , [B]")]
    [InlineData(",,")]
    [InlineData("[]")]
    [InlineData("[A], []")]
    [InlineData("]")]
    [InlineData("[A] trailing")]
    public void Malformed_grammar_throws_the_fixed_message_without_the_input(string text)
    {
        AssertMalformed(text, ["A", "B"], "A", "B");
    }

    private static void AssertMalformed(string text, IReadOnlyList<string> catalog, params string[] absent)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BracketedIdentifierListParser.ParseAndResolve(text, catalog));
        Assert.Equal(MalformedMessage, exception.Message);
        Assert.DoesNotContain(text, exception.Message, StringComparison.Ordinal);
        foreach (var token in absent)
            Assert.DoesNotContain(token, exception.Message, StringComparison.Ordinal);
    }
}