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
    public void Compare_summary_records_the_nullability_diagnostic()
    {
        var report = Compare(false) with
        {
            Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Ordered, false, 0, 0, 0, true, false, ResultMismatchReasons.Nullability),
        };

        using var json = JsonDocument.Parse(JournalSummary.Build(report)!);
        Assert.True(json.RootElement.GetProperty("compareNullability").GetBoolean());
        Assert.False(json.RootElement.GetProperty("nullabilityEquivalent").GetBoolean());

        using var unknown = JsonDocument.Parse(JournalSummary.Build(Compare(true))!);
        Assert.False(unknown.RootElement.GetProperty("compareNullability").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unknown.RootElement.GetProperty("nullabilityEquivalent").ValueKind);
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
    public void Matrix_summary_records_the_nullability_policy()
    {
        static SqlHarnessCompareReport Strict(bool equivalent) => Compare(equivalent) with
        {
            Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Ordered, equivalent, 0, 0, 0, true, equivalent),
        };

        var strict = new SqlHarnessCompareMatrixReport("Tenant", "int",
        [
            new CompareMatrixCellReport(0, "1", Strict(true)),
            new CompareMatrixCellReport(1, "2", Strict(false)),
        ]);
        var lenient = new SqlHarnessCompareMatrixReport("Tenant", "int",
        [
            new CompareMatrixCellReport(0, "1", Compare(true)),
            new CompareMatrixCellReport(1, "2", Compare(true)),
        ]);

        using var strictJson = JsonDocument.Parse(JournalSummary.Build(strict)!);
        using var lenientJson = JsonDocument.Parse(JournalSummary.Build(lenient)!);

        Assert.True(strictJson.RootElement.GetProperty("compareNullability").GetBoolean());
        Assert.False(lenientJson.RootElement.GetProperty("compareNullability").GetBoolean());
    }

    [Fact]
    public void Compare_summaries_preserve_the_existing_regression_and_each_matrix_cell_verdict()
    {
        var regression = new RegressionReport(
            RegressionPolicy.Fail,
            [RegressionPolicy.ReasonElapsedRegressed],
            RegressionPolicy.PolicyVersion,
            new RegressionDiagnostic(RegressionPolicy.StatusRegressed, 10, 20, 10, 100),
            new RegressionDiagnostic(RegressionPolicy.StatusNotRegressed, 100, 100, 0, 0),
            new RegressionDiagnostic(RegressionPolicy.StatusNotRegressed, 2, 2, 0, 0));
        var failedCell = Compare(false) with { Regression = regression };
        var uncheckedCell = Compare(null) with
        {
            Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Off, null, null, null, null),
        };
        var incompleteCell = Compare(null) with
        {
            Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Ordered, null, null, null, null),
        };
        var report = new SqlHarnessCompareMatrixReport("Tenant", "int",
        [
            new CompareMatrixCellReport(0, "SQLH_PRIVATE_VALUE", Compare(true)),
            new CompareMatrixCellReport(1, "SQLH_PRIVATE_VALUE_2", failedCell),
            new CompareMatrixCellReport(2, "SQLH_PRIVATE_VALUE_3", uncheckedCell),
            new CompareMatrixCellReport(3, "SQLH_PRIVATE_VALUE_4", incompleteCell),
        ]);

        using var plainJson = JsonDocument.Parse(JournalSummary.Build(failedCell)!);
        using var matrixJson = JsonDocument.Parse(JournalSummary.Build(report)!);

        Assert.Equal("fail", plainJson.RootElement.GetProperty("regression").GetProperty("verdict").GetString());
        var cases = matrixJson.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(4, cases.Length);
        Assert.True(cases[0].GetProperty("equivalence").GetProperty("equivalent").GetBoolean());
        Assert.False(cases[1].GetProperty("equivalence").GetProperty("equivalent").GetBoolean());
        Assert.Equal("fail", cases[1].GetProperty("regression").GetProperty("verdict").GetString());
        Assert.Equal((int)ResultComparisonMode.Off, cases[2].GetProperty("equivalence").GetProperty("mode").GetInt32());
        Assert.Equal(JsonValueKind.Null, cases[2].GetProperty("equivalence").GetProperty("equivalent").ValueKind);
        Assert.Equal((int)ResultComparisonMode.Ordered, cases[3].GetProperty("equivalence").GetProperty("mode").GetInt32());
        Assert.Equal(JsonValueKind.Null, cases[3].GetProperty("equivalence").GetProperty("equivalent").ValueKind);
        Assert.DoesNotContain("SQLH_PRIVATE_VALUE", matrixJson.RootElement.GetRawText());
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
        Assert.Null(JournalSummary.Build(new SqlHarnessDryRunReport("query", "read-only", "read-only", [], 0)));
}