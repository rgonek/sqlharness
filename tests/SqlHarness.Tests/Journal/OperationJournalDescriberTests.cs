using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class OperationJournalDescriberTests
{
    private static readonly SqlTargetRequest Target =
        new("local", new Dictionary<string, string> { ["tenant"] = "acme", ["env"] = "uat" });

    [Fact]
    public void Query_start_carries_profile_vars_hash_text_and_mutation_flag()
    {
        var operation = new SqlHarnessQueryOperation(Target, "SELECT 1", ["id:int=SQLH_PARAM_MARKER"], 30, 100, true, "db");

        var start = OperationJournalDescriber.DescribeStart(operation);

        Assert.Equal("query", start.Operation);
        Assert.Equal("local", start.Profile);
        Assert.Equal("acme", start.Vars!["tenant"]);
        Assert.True(start.MutationRequested);
        Assert.Equal(OperationJournalDescriber.SqlHash("SELECT 1"), start.SqlHash);
        Assert.StartsWith("sha256:", start.SqlHash);
        Assert.Equal(71, start.SqlHash!.Length);
        Assert.Equal("SELECT 1", start.SqlText);
        Assert.DoesNotContain("SQLH_PARAM_MARKER", System.Text.Json.JsonSerializer.Serialize(start));
    }

    [Fact]
    public void Compare_start_carries_both_variants()
    {
        var operation = new SqlHarnessCompareOperation(Target, "CREATE TABLE #t (id int)", "SELECT 1", "SELECT 2", ["id:int=SQLH_PARAM_MARKER"], 30, 5)
        {
            Context = new CompareContext("orders-report", "whole"),
            CompareResults = ResultComparisonMode.Multiset,
            CompareNullability = true,
        };

        var start = OperationJournalDescriber.DescribeStart(operation);

        Assert.Equal("compare", start.Operation);
        Assert.Equal("SELECT 1", start.SqlText);
        Assert.Equal("SELECT 2", start.CandidateSqlText);
        Assert.NotEqual(start.SqlHash, start.CandidateSqlHash);
        Assert.Equal(new CompareContext("orders-report", "whole"), start.Context);
        Assert.Equal("CREATE TABLE #t (id int)", start.SetupSqlText);
        Assert.DoesNotContain("SQLH_PARAM_MARKER", System.Text.Json.JsonSerializer.Serialize(start));
    }

    [Fact]
    public void Compare_matrix_start_does_not_capture_raw_matrix_inputs()
    {
        var operation = new SqlHarnessCompareMatrixOperation(
            Target, null, "SELECT @tenant", "SELECT @tenant", [], 30, 5, "tenant:nvarchar=acme,contoso")
        {
            Context = new CompareContext("orders-report", "whole"),
        };

        var start = OperationJournalDescriber.DescribeStart(operation);

        Assert.Equal(new CompareContext("orders-report", "whole"), start.Context);
        Assert.DoesNotContain("tenant:nvarchar=", System.Text.Json.JsonSerializer.Serialize(start));
    }

    [Fact]
    public void Measure_start_carries_optional_context_and_query_hash()
    {
        var operation = new SqlHarnessMeasureOperation(Target, null, "SELECT 1", [], 30, 5)
        {
            Context = new CompareContext("orders-report", "whole"),
        };

        var start = OperationJournalDescriber.DescribeStart(operation);

        Assert.Equal("measure", start.Operation);
        Assert.Equal(OperationJournalDescriber.SqlHash("SELECT 1"), start.SqlHash);
        Assert.Equal(new CompareContext("orders-report", "whole"), start.Context);
    }

    [Fact]
    public void Unsafe_direct_start_has_no_profile()
    {
        var direct = new SqlTargetRequest(null, new Dictionary<string, string>(), "srv", "db", "integrated", UnsafeDirect: true);

        var start = OperationJournalDescriber.DescribeStart(new SqlHarnessQueryOperation(direct, "SELECT 1", [], 5, 10, false, null));

        Assert.Equal("query", start.Operation);
        Assert.Null(start.Profile);
    }

    [Fact]
    public void Target_free_operations_are_named()
    {
        Assert.Equal("plan", OperationJournalDescriber.DescribeStart(new SqlHarnessPlanOperation("<x/>", new OutputFootprint(0, 0))).Operation);
    }

    [Theory]
    [InlineData(SqlHarnessExitCode.Success, "succeeded")]
    [InlineData(SqlHarnessExitCode.WatchMaxDuration, "succeeded")]
    [InlineData(SqlHarnessExitCode.SnapshotDifferences, "succeeded")]
    [InlineData(SqlHarnessExitCode.Safety, "rejected")]
    [InlineData(SqlHarnessExitCode.Authentication, "failed")]
    [InlineData(SqlHarnessExitCode.TargetMismatch, "failed")]
    [InlineData(SqlHarnessExitCode.SqlExecution, "failed")]
    [InlineData(SqlHarnessExitCode.LocalStorage, "failed")]
    public void Exit_codes_map_to_statuses(SqlHarnessExitCode exitCode, string status) =>
        Assert.Equal(status, OperationJournalDescriber.Status(exitCode));

    [Fact]
    public void Query_end_reads_target_identity_and_row_counts()
    {
        var identity = new SqlHarnessTargetIdentityReport("srv", "db", "srv-actual", "db-actual", "profile");
        var resultSet = new SqlHarnessResultSetReport([], [], 7, 0);
        var report = new SqlHarnessQueryReport(identity, "read-only", [resultSet, resultSet], [], 0, 5, "hash", new OutputFootprint(0, 0));

        var end = OperationJournalDescriber.DescribeEnd(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), 42);

        Assert.Equal("succeeded", end.Status);
        Assert.Equal(0, end.ExitCode);
        Assert.Equal(42, end.DurationMilliseconds);
        Assert.Equal("sqlserver", end.Engine);
        Assert.Equal("srv-actual", end.Server);
        Assert.Equal("db-actual", end.Database);
        Assert.Equal(2, end.ResultSets);
        Assert.Equal(14, end.RowsReturned);
    }

    [Fact]
    public void Failure_end_carries_machine_error_code_and_message()
    {
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, "SQL safety rejection: SELECT secret");

        var end = OperationJournalDescriber.DescribeEnd(outcome, 1);

        Assert.Equal("rejected", end.Status);
        Assert.Equal(2, end.ExitCode);
        Assert.Equal(outcome.MachineError?.Code, end.ErrorKind);
        Assert.Equal("SQL safety rejection: SELECT secret", end.ErrorMessage);
        Assert.Null(end.Server);
    }

    [Fact]
    public void Error_hint_is_appended_on_a_new_line()
    {
        var error = new SqlHarnessError("sql_execution_failed", "sql", "Timeout expired.", Hint: "Raise --timeout.");
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, null, null, Error: error);

        Assert.Equal("Timeout expired.\nRaise --timeout.", OperationJournalDescriber.DescribeEnd(outcome, 1).ErrorMessage);
    }

    [Fact]
    public void Success_and_controlled_outcomes_carry_no_error_message()
    {
        Assert.Null(OperationJournalDescriber.DescribeEnd(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null), 1).ErrorMessage);
        Assert.Null(OperationJournalDescriber.DescribeEnd(new SqlHarnessOutcome(SqlHarnessExitCode.WatchMaxDuration, null, null), 1).ErrorMessage);
        Assert.Null(OperationJournalDescriber.Cancelled(1).ErrorMessage);
        Assert.Null(OperationJournalDescriber.Crashed(1).ErrorMessage);
    }

    [Fact]
    public void Cancelled_and_crashed_ends_are_failed_with_kind()
    {
        Assert.Equal(("failed", "cancelled"), (OperationJournalDescriber.Cancelled(3).Status, OperationJournalDescriber.Cancelled(3).ErrorKind));
        Assert.Equal(("failed", "unhandled_exception"), (OperationJournalDescriber.Crashed(3).Status, OperationJournalDescriber.Crashed(3).ErrorKind));
    }

    [Fact]
    public void Benchmark_end_carries_artifact_directory_summary_and_matrix_target()
    {
        var identity = new SqlHarnessTargetIdentityReport("srv", "db", "srv-actual", "db-actual", "profile");
        var variant = new CompareVariantReport("measure", new(1, 1, 1), new(1, 1, 1), new(1, 1, 1), new Dictionary<string, long>(), [], []);
        var measure = new SqlHarnessMeasureReport(identity, 1, 1, true, variant, "/artifacts/m1");
        var compare = new SqlHarnessCompareReport(identity, 1, 2, true, variant, variant, "/artifacts/c1");
        var matrix = new SqlHarnessCompareMatrixReport("BatchSize", "int", [new CompareMatrixCellReport(0, "1", compare)]);

        var measureEnd = OperationJournalDescriber.DescribeEnd(new SqlHarnessOutcome(SqlHarnessExitCode.Success, measure, null), 1);
        var matrixEnd = OperationJournalDescriber.DescribeEnd(new SqlHarnessOutcome(SqlHarnessExitCode.Success, matrix, null), 1);

        Assert.Equal("/artifacts/m1", measureEnd.ArtifactDirectory);
        Assert.NotNull(measureEnd.SummaryJson);
        Assert.Equal("srv-actual", matrixEnd.Server);
        Assert.Null(matrixEnd.ArtifactDirectory);
        Assert.Contains("compare-matrix", matrixEnd.SummaryJson);
    }
}
