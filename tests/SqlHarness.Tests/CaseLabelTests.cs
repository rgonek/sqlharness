using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class CaseLabelTests
{
    [Fact]
    public void Formats_stored_values_and_null()
    {
        var one = new ConditionsCase("0", 0, "", null, [Parameter("ClassType", "nvarchar(4000)", "A")], "");
        var nullValue = new ConditionsCase("1", 1, "", null, [Parameter("X", "int", null)], "");

        Assert.Equal("@ClassType = N'A'", CaseLabel.Format(one, 2, true));
        Assert.Equal("@X = NULL", CaseLabel.Format(nullValue, 2, true));
    }

    [Fact]
    public void Formats_multiple_values_and_fallbacks()
    {
        var several = new ConditionsCase("0", 0, "", null,
            [new ConditionsParameter("A", "int", JsonDocument.Parse("1").RootElement.Clone()), Parameter("B", "nvarchar(4000)", "x")], "");
        var named = new ConditionsCase("a", 0, "", "a", [Parameter("A", "int", "1")], "");
        var unnamed = new ConditionsCase("1", 1, "", null, [], "");

        Assert.Equal("@A = 1, @B = N'x'", CaseLabel.Format(several, 3, true));
        Assert.Equal("a", CaseLabel.Format(named, 3, false));
        Assert.Equal("Case 2 of 3", CaseLabel.Format(unnamed, 3, false));
    }

    [Fact]
    public void Formats_materialized_tvp_and_list_sizes_and_values()
    {
        var tvp = new ConditionsParameter("@Ids", "dbo.IdList READONLY", null, "tvp", 500,
            ["Id"], ContentHash: "hash");
        var shortList = new ConditionsParameter("@Types", "list of int", null, "list", 3, [], ContentHash: "hash",
            Values: JsonDocument.Parse("[1,2,3]").RootElement.Clone());
        var longList = shortList with
        {
            Count = 250,
            Values = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, 250)) + "]").RootElement.Clone(),
        };

        Assert.Equal("@Ids = 500 rows (dbo.IdList)", CaseLabel.Format(
            new ConditionsCase("0", 0, "", null, [tvp], ""), 1, true));
        Assert.Equal("@Types IN (1, 2, 3)", CaseLabel.Format(
            new ConditionsCase("0", 0, "", null, [shortList], ""), 1, true));
        Assert.Equal("@Types IN (… 250 values)", CaseLabel.Format(
            new ConditionsCase("0", 0, "", null, [longList], ""), 1, true));
    }

    private static ConditionsParameter Parameter(string name, string type, string? value)
    {
        using var document = JsonDocument.Parse(value is null ? "null" : JsonSerializer.Serialize(value));
        return new ConditionsParameter(name, type, document.RootElement.Clone());
    }
}
