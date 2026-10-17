using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class JournalingBenchmarkTests
{
    private static readonly SqlTargetRequest Target = new("local", new Dictionary<string, string> { ["tenant"] = "acme" });

    private sealed class FakeModule(SqlHarnessOutcome outcome) : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            Execute(operation);

        private Task<SqlHarnessOutcome> Execute(SqlHarnessOperation operation)
        {
            if (operation.RecordConditions is { } recordConditions && Conditions is { } conditions)
                recordConditions(conditions);
            return Task.FromResult(outcome);
        }

        public ConditionsModel? Conditions { get; init; }
    }

    private static SqlHarnessOutcome BenchmarkOutcome(int? matrixCell = null) =>
        new(SqlHarnessExitCode.Success, null, null)
        {
            BenchmarkRuns =
            [
                new CompareRunArtifact("measure", 1, 10, 12, 5, new Dictionary<string, long> { ["Orders"] = 5 }, "h",
                    [PlanMetricsExtractorTests.ActualPlan], 1)
                {
                    TableIo = [new TableIoCounters("Orders", 1, 5, 0, 0, 0, 0, 0, 0)],
                    MatrixCell = matrixCell,
                },
            ],
        };

    private static async Task<JournalTempDirectory> RunAsync(
        SqlHarnessOutcome outcome,
        bool storeSensitive,
        SqlHarnessOperation? operation = null,
        ConditionsModel? conditions = null)
    {
        var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = storeSensitive }, TextWriter.Null, TimeProvider.System);
        var module = new JournalingModule(new FakeModule(outcome) { Conditions = conditions }, () => journal, () => JournalTestData.Session());
        var returned = await module.ExecuteAsync(operation ?? new SqlHarnessMeasureOperation(Target, null, "SELECT 'SQLH_SQL_MARKER'", ["p:nvarchar=SQLH_PARAM_MARKER"], 30, 1));
        Assert.Same(outcome.Report, returned.Report);
        Assert.Equal(outcome.ExitCode, returned.ExitCode);
        return temp;
    }

    private static ConditionsModel MatrixConditions(string? firstValue = "A") => new(
        1, "sqlserver", "server", "database",
        new ConditionsExecution("compare", "per-case", "per-case", 1, 1, 30, "Ordered", false),
        null, [],
        [
            new ConditionsCase("0", 0, "@ClassType = N'A'", null,
                [new ConditionsParameter("@ClassType", "nvarchar(max)",
                    firstValue is null ? null : JsonDocument.Parse(JsonSerializer.Serialize(firstValue)).RootElement.Clone())], "fingerprint-0"),
            new ConditionsCase("1", 1, "@ClassType = N'B'", null,
                [new ConditionsParameter("@ClassType", "nvarchar(20)", JsonDocument.Parse("\"B\"").RootElement.Clone())], "fingerprint-1"),
        ], "fingerprint", []);

    private static SqlHarnessOutcome MatrixBenchmarkOutcome() =>
        BenchmarkOutcome(matrixCell: 0) with
        {
            BenchmarkRuns =
            [
                BenchmarkOutcome(matrixCell: 0).BenchmarkRuns![0],
                BenchmarkOutcome(matrixCell: 1).BenchmarkRuns![0] with { Variant = "candidate" },
            ],
            Report = new SqlHarnessCompareMatrixReport("ClassType", "nvarchar(20)",
            [
                new CompareMatrixCellReport(0, null, MatrixCompareReport()),
                new CompareMatrixCellReport(1, null, MatrixCompareReport()),
            ]),
        };

    private static SqlHarnessCompareReport MatrixCompareReport() => new(
        new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"), 1, 2, true,
        new CompareVariantReport("baseline", new(1, 1, 1), new(1, 1, 1), new(1, 1, 1), new Dictionary<string, long>(), [], []),
        new CompareVariantReport("candidate", new(1, 1, 1), new(1, 1, 1), new(1, 1, 1), new Dictionary<string, long>(), [], []), null);

    [Fact]
    public async Task Hash_only_mode_stores_metrics_but_no_plans_or_sql()
    {
        using var temp = await RunAsync(BenchmarkOutcome(), storeSensitive: false);

        Assert.Single(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM operation_metrics"));
        Assert.Single(JournalDb.Rows(temp.DatabasePath, "SELECT plan_hash FROM operation_plans"));
        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT hash FROM plans"));
        var bytes = JournalDb.AllBytes(temp.DatabasePath);
        Assert.False(JournalDb.Contains(bytes, "SQLH_SQL_MARKER"));
        Assert.False(JournalDb.Contains(bytes, "SQLH_PARAM_MARKER"));
        Assert.False(JournalDb.Contains(bytes, "CONVERT_IMPLICIT"));
    }

    [Fact]
    public async Task Sensitive_mode_stores_the_plan_and_sql_text()
    {
        using var temp = await RunAsync(BenchmarkOutcome(), storeSensitive: true);

        Assert.Single(JournalDb.Rows(temp.DatabasePath, "SELECT hash FROM plans"));
        Assert.True(JournalDb.Contains(JournalDb.AllBytes(temp.DatabasePath), "SQLH_SQL_MARKER"));
    }

    [Fact]
    public async Task Matrix_values_never_reach_the_database()
    {
        var compare = new SqlHarnessCompareReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"), 1, 2, true,
            new CompareVariantReport("baseline", new(1, 1, 1), new(1, 1, 1), new(1, 1, 1), new Dictionary<string, long>(), [], []),
            new CompareVariantReport("candidate", new(1, 1, 1), new(1, 1, 1), new(1, 1, 1), new Dictionary<string, long>(), [], []),
            null);
        var report = new SqlHarnessCompareMatrixReport("Tenant", "nvarchar(20)", [new CompareMatrixCellReport(0, "SQLH_MATRIX_MARKER", compare)]);
        var outcome = BenchmarkOutcome(matrixCell: 0) with { Report = report };

        using var temp = await RunAsync(outcome, storeSensitive: true);

        Assert.Equal(0L, JournalDb.Rows(temp.DatabasePath, "SELECT matrix_cell FROM operation_metrics").Single()["matrix_cell"]);
        Assert.False(JournalDb.Contains(JournalDb.AllBytes(temp.DatabasePath), "SQLH_MATRIX_MARKER"));
    }

    [Fact]
    public async Task Failed_outcome_records_no_benchmark()
    {
        var failed = new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, null, "boom");

        using var temp = await RunAsync(failed, storeSensitive: true);

        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM operation_metrics"));
    }

    [Fact]
    public async Task Builder_failure_never_changes_the_outcome()
    {
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null)
        {
            BenchmarkRuns = [null!],
        };

        using var temp = await RunAsync(outcome, storeSensitive: false);

        Assert.Equal("succeeded", JournalDb.Rows(temp.DatabasePath, "SELECT status FROM operations").Single()["status"]);
    }

    [Fact]
    public async Task Failed_exit_outcome_with_completed_runs_is_journaled()
    {
        var outcome = BenchmarkOutcome(matrixCell: 0) with { ExitCode = SqlHarnessExitCode.LocalStorage };

        using var temp = await RunAsync(outcome, storeSensitive: false);

        Assert.Equal(0L, JournalDb.Rows(temp.DatabasePath, "SELECT matrix_cell FROM operation_metrics").Single()["matrix_cell"]);
        Assert.NotEqual("succeeded", JournalDb.Rows(temp.DatabasePath, "SELECT status FROM operations").Single()["status"]);
    }

    [Fact]
    public async Task Matrix_runs_journal_case_label()
    {
        using var sensitive = await RunAsync(MatrixBenchmarkOutcome(), storeSensitive: true,
            operation: new SqlHarnessMeasureOperation(Target, null, "SELECT 1", [], 30, 1), conditions: MatrixConditions());
        using var hashOnly = await RunAsync(MatrixBenchmarkOutcome(), storeSensitive: false,
            operation: new SqlHarnessMeasureOperation(Target, null, "SELECT 1", [], 30, 1), conditions: MatrixConditions());

        var sensitiveCases = JournalDb.Rows(sensitive.DatabasePath,
            "SELECT case_id, case_label FROM operation_metrics ORDER BY matrix_cell");
        Assert.Equal([("0", "@ClassType = N'A'"), ("1", "@ClassType = N'B'")],
            sensitiveCases.Select(row => ((string)row["case_id"]!, (string)row["case_label"]!)).ToArray());
        Assert.Equal([("0", "Case 1 of 2"), ("1", "Case 2 of 2")],
            JournalDb.Rows(hashOnly.DatabasePath, "SELECT case_id, case_label FROM operation_metrics ORDER BY matrix_cell")
                .Select(row => ((string)row["case_id"]!, (string)row["case_label"]!)).ToArray());
        Assert.Equal(["0", "1"], JournalDb.Rows(sensitive.DatabasePath,
            "SELECT case_id FROM operation_statements ORDER BY matrix_cell").Select(row => (string)row["case_id"]!).ToArray());

        using var summary = JsonDocument.Parse((string)JournalDb.Rows(sensitive.DatabasePath,
            "SELECT summary_json FROM operations").Single()["summary_json"]!);
        Assert.Equal(["0", "1"], summary.RootElement.GetProperty("cases").EnumerateArray()
            .Select(item => item.GetProperty("caseId").GetString()!).ToArray());
    }

    [Fact]
    public async Task Matrix_labels_follow_oversized_condition_omissions()
    {
        using var temp = await RunAsync(MatrixBenchmarkOutcome(), storeSensitive: true,
            operation: new SqlHarnessMeasureOperation(Target, null, "SELECT 1", [], 30, 1),
            conditions: MatrixConditions(new string('X', 1_100_000)));

        var rows = JournalDb.Rows(temp.DatabasePath,
            "SELECT case_id, case_label FROM operation_metrics ORDER BY matrix_cell");
        Assert.Equal("Case 1 of 2", rows[0]["case_label"]);
        Assert.Equal("@ClassType = N'B'", rows[1]["case_label"]);
        Assert.Contains("cases.0.@ClassType.value", (string)JournalDb.Rows(temp.DatabasePath,
            "SELECT conditions_json FROM operations").Single()["conditions_json"]!);
        Assert.False(JournalDb.Contains(JournalDb.AllBytes(temp.DatabasePath), "XXXXXXXXXXXXXXXX"));
    }
}
