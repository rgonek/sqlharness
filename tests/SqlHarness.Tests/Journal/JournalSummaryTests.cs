using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class JournalSummaryTests
{
    private static readonly SqlHarnessTargetIdentityReport Target = new("s", "d", "s", "d", "profile");

    private static CompareVariantReport Variant(string name, params CompareOperatorReport[] operators) => new(
        name, new(1, 1, 1), new(1, 1, 1), new(1, 1, 1), new Dictionary<string, long>(), operators, []);

    private static SqlHarnessCompareReport Compare(bool? equivalent) =>
        new(Target, 1, 2, equivalent, Variant("baseline", new CompareOperatorReport(1, "Sort", "Orders", false, true, false)), Variant("candidate"), "/dir");

    [Fact]
    public void Measure_summary_has_stability_and_flagged_operators_only()
    {
        var report = new SqlHarnessMeasureReport(Target, 3, 3, true,
            Variant("measure",
                new CompareOperatorReport(1, "Index Seek", "Orders", false, false, false),
                new CompareOperatorReport(2, "Sort", "Orders", false, true, false)),
            "/dir");

        using var json = JsonDocument.Parse(JournalSummary.Build(report)!);

        Assert.Equal("measure", json.RootElement.GetProperty("kind").GetString());
        Assert.True(json.RootElement.GetProperty("resultsStable").GetBoolean());
        var op = Assert.Single(json.RootElement.GetProperty("operators").EnumerateArray());
        Assert.Equal("Sort", op.GetProperty("physicalOp").GetString());
        Assert.True(op.GetProperty("spill").GetBoolean());
    }

    [Fact]
    public void Compare_summary_has_equivalence_mode_and_both_variants()
    {
        using var json = JsonDocument.Parse(JournalSummary.Build(Compare(false))!);

        Assert.Equal("compare", json.RootElement.GetProperty("kind").GetString());
        Assert.False(json.RootElement.GetProperty("resultsEquivalent").GetBoolean());
        Assert.Equal("Ordered", json.RootElement.GetProperty("comparison").GetString());
        Assert.Single(json.RootElement.GetProperty("baselineOperators").EnumerateArray());
        Assert.Empty(json.RootElement.GetProperty("candidateOperators").EnumerateArray());
    }

    [Fact]
    public void Matrix_summary_never_contains_matrix_values()
    {
        var report = new SqlHarnessCompareMatrixReport("Tenant", "nvarchar(20)",
        [
            new CompareMatrixCellReport(0, "SQLH_MATRIX_MARKER_A", Compare(true)),
            new CompareMatrixCellReport(1, "SQLH_MATRIX_MARKER_B", Compare(false)),
        ]);

        var summary = JournalSummary.Build(report)!;

        Assert.DoesNotContain("SQLH_MATRIX_MARKER", summary);
        using var json = JsonDocument.Parse(summary);
        Assert.Equal(2, json.RootElement.GetProperty("cells").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("equivalentCells").GetInt32());
        Assert.Equal("Tenant", json.RootElement.GetProperty("parameterName").GetString());
    }

    [Fact]
    public void Operators_are_capped()
    {
        var operators = Enumerable.Range(1, 15).Select(i => new CompareOperatorReport(i, "Sort", "T", true, false, false)).ToArray();
        var report = new SqlHarnessMeasureReport(Target, 1, 1, true, Variant("measure", operators), null);

        using var json = JsonDocument.Parse(JournalSummary.Build(report)!);

        Assert.Equal(JournalSummary.MaximumOperators, json.RootElement.GetProperty("operators").GetArrayLength());
    }

    [Fact]
    public void Non_benchmark_reports_have_no_summary() =>
        Assert.Null(JournalSummary.Build(new SqlHarnessPingReport(Target, "srv", "db", "login", 1)));
}