using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public class ContractsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Exit_codes_are_stable()
    {
        Assert.Equal(0, (int)SqlHarnessExitCode.Success);
        Assert.Equal(2, (int)SqlHarnessExitCode.Safety);
        Assert.Equal(3, (int)SqlHarnessExitCode.Authentication);
        Assert.Equal(4, (int)SqlHarnessExitCode.TargetMismatch);
        Assert.Equal(5, (int)SqlHarnessExitCode.SqlExecution);
        Assert.Equal(6, (int)SqlHarnessExitCode.LocalStorage);
    }

    [Fact]
    public void Operation_family_is_closed_to_query_compare_measure_gain_and_plan()
    {
        Assert.Equal(
            [typeof(SqlHarnessQueryOperation), typeof(SqlHarnessCompareOperation), typeof(SqlHarnessMeasureOperation), typeof(SqlHarnessGainOperation), typeof(SqlHarnessPlanOperation), typeof(SqlHarnessSchemaOperation)],
            typeof(SqlHarnessOperation).Assembly.GetTypes()
                .Where(t => t.BaseType == typeof(SqlHarnessOperation))
                .OrderBy(OperationOrder));
    }

    private static int OperationOrder(Type operationType) => operationType.Name switch
    {
        nameof(SqlHarnessQueryOperation) => 0,
        nameof(SqlHarnessCompareOperation) => 1,
        nameof(SqlHarnessMeasureOperation) => 2,
        nameof(SqlHarnessGainOperation) => 3,
        nameof(SqlHarnessPlanOperation) => 4,
        nameof(SqlHarnessSchemaOperation) => 5,
        _ => int.MaxValue,
    };

    [Fact]
    public void Measure_operation_exposes_the_required_contract()
    {
        var target = new SqlTargetRequest(
            "test",
            new Dictionary<string, string> { ["env"] = "a" });
        var operation = new SqlHarnessMeasureOperation(target, "CREATE TABLE #t(Id int)", "SELECT 1", ["id=1"], 30, 5);

        Assert.Equal(target, operation.Target);
        Assert.Equal("CREATE TABLE #t(Id int)", operation.SetupSql);
        Assert.Equal("SELECT 1", operation.QuerySql);
        Assert.Equal(["id=1"], operation.Parameters);
        Assert.Equal(30, operation.TimeoutSeconds);
        Assert.Equal(5, operation.Repeat);
    }

    [Fact]
    public void Compare_report_json_projects_results_equivalent_and_equivalence_for_ordered_default()
    {
        var report = new SqlHarnessCompareReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            5,
            10,
            true,
            Variant("baseline"),
            Variant("candidate"),
            null);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(report, WebJson));
        var root = document.RootElement;

        Assert.True(root.GetProperty("resultsEquivalent").GetBoolean());
        var equivalence = root.GetProperty("equivalence");
        Assert.Equal((int)ResultComparisonMode.Ordered, equivalence.GetProperty("mode").GetInt32());
        Assert.True(equivalence.GetProperty("equivalent").GetBoolean());
        Assert.Equal(0, equivalence.GetProperty("differingPositions").GetInt64());
        Assert.Equal(0, equivalence.GetProperty("baselineOnlyCount").GetInt64());
        Assert.Equal(0, equivalence.GetProperty("candidateOnlyCount").GetInt64());
        Assert.Equal(report.ResultsEquivalent, report.Equivalence.Equivalent);
    }

    [Fact]
    public void Compare_report_json_nulls_equivalence_fields_for_off_mode()
    {
        var report = new SqlHarnessCompareReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            5,
            10,
            null,
            Variant("baseline"),
            Variant("candidate"),
            null)
        {
            Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Off, null, null, null, null),
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(report, WebJson));
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("resultsEquivalent").ValueKind);
        var equivalence = root.GetProperty("equivalence");
        Assert.Equal((int)ResultComparisonMode.Off, equivalence.GetProperty("mode").GetInt32());
        Assert.Equal(JsonValueKind.Null, equivalence.GetProperty("equivalent").ValueKind);
        Assert.Equal(JsonValueKind.Null, equivalence.GetProperty("differingPositions").ValueKind);
        Assert.Equal(JsonValueKind.Null, equivalence.GetProperty("baselineOnlyCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, equivalence.GetProperty("candidateOnlyCount").ValueKind);
    }

    private static CompareVariantReport Variant(string name) =>
        new(name, new(1, 2, 3), new(1, 2, 3), new(1, 2, 3), new Dictionary<string, long>(), [], []);
}