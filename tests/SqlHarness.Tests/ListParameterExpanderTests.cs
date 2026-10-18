using System.Data;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class ListParameterExpanderTests
{
    [Fact]
    public void Expands_a_list_in_both_supported_IN_forms_with_typed_parameters()
    {
        var expanded = ListParameterExpander.Expand(
            "SELECT * FROM dbo.Items WHERE Id IN @Ids OR ParentId IN (@Ids)",
            [new ListValues("Ids", "int", ["1", "2", "3"]) ]);

        Assert.Equal(
            "SELECT * FROM dbo.Items WHERE Id IN (@Ids1, @Ids2, @Ids3) OR ParentId IN (@Ids1, @Ids2, @Ids3)",
            expanded.Text);
        Assert.Equal(["@Ids1", "@Ids2", "@Ids3"], expanded.Parameters.Select(parameter => parameter.Name));
        Assert.All(expanded.Parameters, parameter => Assert.Equal(SqlDbType.Int, parameter.Type));
        Assert.Equal([1, 2, 3], expanded.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void Expands_variable_without_whitespace_after_IN()
    {
        var expanded = ListParameterExpander.Expand(
            "SELECT * FROM dbo.Items WHERE Id IN@Ids",
            [new ListValues("Ids", "int", ["7"]) ]);

        Assert.Equal("SELECT * FROM dbo.Items WHERE Id IN (@Ids1)", expanded.Text);
    }

    [Fact]
    public void Shares_parameters_for_repeated_list_and_respects_name_boundaries()
    {
        var expanded = ListParameterExpander.Expand(
            "SELECT * FROM dbo.Items WHERE Id IN @Ids AND ParentId IN @Ids AND Code = @IdsOther",
            [new ListValues("Ids", "int", ["1", "2"]) ]);

        Assert.Equal(
            "SELECT * FROM dbo.Items WHERE Id IN (@Ids1, @Ids2) AND ParentId IN (@Ids1, @Ids2) AND Code = @IdsOther",
            expanded.Text);
        Assert.Equal(["@Ids1", "@Ids2"], expanded.Parameters.Select(parameter => parameter.Name));
    }

    [Fact]
    public void Expands_an_empty_list_to_a_typed_false_subquery()
    {
        var expanded = ListParameterExpander.Expand(
            "SELECT * FROM dbo.Items WHERE Id IN @Ids",
            [new ListValues("Ids", "int", [])]);

        Assert.Equal("SELECT * FROM dbo.Items WHERE Id IN (SELECT @Ids WHERE 1 = 0)", expanded.Text);
        var parameter = Assert.Single(expanded.Parameters);
        Assert.Equal("@Ids", parameter.Name);
        Assert.Equal(SqlDbType.Int, parameter.Type);
        Assert.Equal(DBNull.Value, parameter.Value);
    }

    [Fact]
    public void Empty_parenthesized_list_keeps_comments_and_avoids_double_parentheses()
    {
        var expanded = ListParameterExpander.Expand(
            "SELECT * FROM dbo.Items WHERE Id IN (/* before */ @Ids /* after */)",
            [new ListValues("Ids", "int", [])]);

        Assert.Equal(
            "SELECT * FROM dbo.Items WHERE Id IN (/* before */ SELECT @Ids WHERE 1 = 0 /* after */)",
            expanded.Text);
    }

    [Fact]
    public void Leaves_string_literals_and_comments_unchanged()
    {
        var expanded = ListParameterExpander.Expand(
            "SELECT '@Ids' AS Literal -- @Ids\nFROM dbo.Items WHERE Id IN @Ids /* @Ids */",
            [new ListValues("Ids", "int", ["1", "2"]) ]);

        Assert.Equal(
            "SELECT '@Ids' AS Literal -- @Ids\nFROM dbo.Items WHERE Id IN (@Ids1, @Ids2) /* @Ids */",
            expanded.Text);
    }

    [Fact]
    public void Rejects_expansion_over_the_command_parameter_limit_with_the_list_name()
    {
        var values = Enumerable.Repeat<string?>("1", 2_101).ToArray();

        var error = Assert.Throws<SqlHarnessSafetyException>(() =>
            ListParameterExpander.Expand("SELECT 1 WHERE 1 IN @Ids", [new ListValues("Ids", "int", values)]));

        Assert.Contains("@Ids", error.Message, StringComparison.Ordinal);
        Assert.Contains("2100", error.Message, StringComparison.Ordinal);
    }
}
