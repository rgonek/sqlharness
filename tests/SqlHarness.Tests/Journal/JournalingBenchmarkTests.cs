using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class JournalingBenchmarkTests
{
    private static readonly SqlTargetRequest Target = new("local", new Dictionary<string, string> { ["tenant"] = "acme" });

    private sealed class FakeModule(SqlHarnessOutcome outcome) : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            Task.FromResult(outcome);
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

    private static async Task<JournalTempDirectory> RunAsync(SqlHarnessOutcome outcome, bool storeSensitive, SqlHarnessOperation? operation = null)
    {
        var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = storeSensitive }, TextWriter.Null, TimeProvider.System);
        var module = new JournalingModule(new FakeModule(outcome), () => journal, () => JournalTestData.Session());
        var returned = await module.ExecuteAsync(operation ?? new SqlHarnessMeasureOperation(Target, null, "SELECT 'SQLH_SQL_MARKER'", ["p:nvarchar=SQLH_PARAM_MARKER"], 30, 1));
        Assert.Same(outcome.Report, returned.Report);
        Assert.Equal(outcome.ExitCode, returned.ExitCode);
        return temp;
    }

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
}