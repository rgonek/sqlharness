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

    private static ConditionsParameter Parameter(string name, string type, string? value)
    {
        using var document = JsonDocument.Parse(value is null ? "null" : JsonSerializer.Serialize(value));
        return new ConditionsParameter(name, type, document.RootElement.Clone());
    }
}
