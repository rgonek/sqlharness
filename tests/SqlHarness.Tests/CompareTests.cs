using System.Data;
using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public class SqlHarnessCompareTests
{
    [Fact]
    public async Task Compare_dry_run_classifies_lists_after_representative_expansion()
    {
        var operation = Compare(1) with
        {
            SetupSql = null,
            BaselineSql = "SELECT Id FROM dbo.Items WHERE Id IN @Ids AND Category = @Category",
            CandidateSql = "SELECT Id FROM dbo.Items WHERE Id IN @Ids AND Category = @Category",
            TypedParameters =
            [
                new SqlHarnessParameterInput("Ids", "int", null) { Kind = "list", Values = ["1", "2"] },
                new SqlHarnessParameterInput("Category", "int", "4"),
            ],
            DryRun = true,
        };

        var outcome = await Module(FakeCompareSession.Create()).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessDryRunReport>(outcome.Report);
        Assert.Equal(["@Category", "@Ids1"], report.Parameters.Select(parameter => parameter.Name));
    }

    [Fact]
    public async Task Compare_report_includes_setup_and_variant_isolation_notices()
    {
        var outcome = await Module(FakeCompareSession.Create()).ExecuteAsync(Compare(1) with
        {
            SetupSql = "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE",
            BaselineSql = "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ",
            CandidateSql = "SELECT Value FROM dbo.Clients",
        });

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.Equal(
            ["isolation level SERIALIZABLE in effect", "isolation level REPEATABLE READ in effect"],
            report.Warnings);
    }

    [Fact]
    public async Task Compare_matrix_failure_preserves_typed_session_warnings()
    {
        var session = FakeCompareSession.Create(failOnBenchmarkNumber: 1);
        session.BenchmarkFailure = new SessionCleanupException(
            "cleanup failed",
            warnings: [SqlClientSession.BatchTimeoutWarning, SqlClientSession.ConnectionDiscardedWarning]);
        var operation = new SqlHarnessCompareMatrixOperation(
            Target(), null, "SELECT @Id AS Value", "SELECT @Id AS Value", [], 30, 1, "Id:int=1,2")
        {
            Context = new CompareContext("test-query", "whole"),
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Contains("cleanup failed", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(
            [SqlClientSession.BatchTimeoutWarning, SqlClientSession.ConnectionDiscardedWarning],
            outcome.SessionWarnings);
    }

    private const string Token = "eyJsynthetic.access-never-emit.signature";
    private const string PlanA = "<ShowPlanXML><BatchSequence><RelOp NodeId=\"1\" PhysicalOp=\"Index Seek\"><IndexScan><Object Table=\"[Clients]\" /></IndexScan></RelOp></BatchSequence></ShowPlanXML>";
    private const string PlanB = "<ShowPlanXML><BatchSequence><RelOp NodeId=\"2\" PhysicalOp=\"Hash Match\"><Warnings><SpillToTempDb /></Warnings><Hash /></RelOp></BatchSequence></ShowPlanXML>";

    [Fact]
    public async Task Compare_alternates_variants_after_one_excluded_warmup_pair_on_one_session()
    {
        var session = FakeCompareSession.Create();
        var artifacts = new CapturingArtifactWriter();

        var outcome = await Module(session, artifacts: artifacts).ExecuteAsync(Compare(repeat: 5));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(
            ["setup", "warmup-A", "warmup-B", "A", "B", "B", "A", "A", "B", "B", "A", "A", "B"],
            session.Labels);
        Assert.Equal(1, session.FactoryOpenCount);
        Assert.All(session.Commands, command => Assert.DoesNotContain("DBCC", command.Sql, StringComparison.OrdinalIgnoreCase));
        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.Equal(10, report.MeasuredRunCount);
        Assert.True(report.ResultsEquivalent);
        Assert.Equal(ResultComparisonMode.Ordered, report.Equivalence.Mode);
        Assert.True(report.Equivalence.Equivalent);
        Assert.Equal("session-local", report.Classification.Setup);
        Assert.Equal("read-only", report.Classification.Baseline);
        Assert.Equal("read-only", report.Classification.Candidate);
        Assert.Equal("cell-artifacts", report.ArtifactDirectory);
        var written = Assert.IsType<SqlHarnessCompareReport>(artifacts.Report);
        Assert.Null(written.ArtifactDirectory);
        Assert.True(written.Equivalence.Equivalent);
        Assert.Equal(ResultComparisonMode.Ordered, written.Equivalence.Mode);
        Assert.Equal(report.Classification, written.Classification);
        Assert.Equal(report.Parameters, written.Parameters);
        Assert.Equal("testdb-a", artifacts.Target);
        Assert.Equal(
            ["baseline", "candidate", "candidate", "baseline", "baseline", "candidate", "candidate", "baseline", "baseline", "candidate"],
            artifacts.Runs.Select(run => run.Variant));
    }

    [Fact]
    public async Task Compare_classifies_if_else_batch_end_to_end_on_fake_session()
    {
        const string branchingSql = "DECLARE @Flag bit = 0; IF @Flag = 1 SELECT 1 AS A ELSE SELECT 2 AS A;";
        var session = FakeCompareSession.Create();
        var operation = Compare(repeat: 1) with
        {
            SetupSql = null,
            BaselineSql = branchingSql,
            CandidateSql = branchingSql,
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.Equal("read-only", report.Classification.Baseline);
        Assert.Equal("read-only", report.Classification.Candidate);
        Assert.Contains(session.Commands, command => command.Sql == branchingSql);
    }

    [Fact]
    public async Task CompareCellRunner_repeat_two_uses_one_session_and_returns_equivalence_and_artifacts()
    {
        var session = FakeCompareSession.Create();
        var artifacts = new CapturingArtifactWriter();
        var runner = new CompareCellRunner(session, artifacts);

        var cell = await runner.RunAsync(
            new CompareCellRequest(
                new ResolvedTarget("test-server", "testdb-a", new AuthSpec(AuthStrategy.Integrated), "profile"),
                "SELECT Id INTO #ids FROM dbo.Clients",
                "SELECT Value FROM dbo.Clients",
                "SELECT Value FROM dbo.Clients -- candidate",
                [],
                30,
                2,
                ResultComparisonMode.Ordered),
            CancellationToken.None);

        Assert.Equal(
            ["setup", "warmup-A", "warmup-B", "A", "B", "B", "A"],
            session.Labels);
        Assert.Equal(1, session.FactoryOpenCount);
        Assert.True(cell.Report.ResultsEquivalent);
        Assert.True(cell.Report.Equivalence.Equivalent);
        Assert.Equal(ResultComparisonMode.Ordered, cell.Report.Equivalence.Mode);
        Assert.Equal("cell-artifacts", cell.Report.ArtifactDirectory);
        Assert.Equal("testdb-a", artifacts.Target);
        Assert.Equal(4, cell.Runs.Count);
        Assert.Equal(
            ["baseline", "candidate", "candidate", "baseline"],
            cell.Runs.Select(run => run.Variant));
    }

    [Fact]
    public async Task Measured_options_do_not_leak_between_variants()
    {
        var session = FakeCompareSession.Create();
        var operation = Compare(repeat: 2) with
        {
            SetupSql = "SET ANSI_NULLS ON",
            BaselineSql = "SET ANSI_NULLS OFF; SELECT Value FROM dbo.Clients",
            CandidateSql = "SELECT Value FROM dbo.Clients -- candidate",
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        Assert.Equal([true, true, true], session.CandidateAnsiNullsAtStart);
    }

    [Fact]
    public async Task SqlServerDialect_round_trips_the_captured_session_state()
    {
        var session = FakeCompareSession.Create();
        var dialect = new SqlServerDialect();
        var setupState = new SessionState(29560, 4, 1234, 3, "dmy", -5);
        session.SetSessionState(setupState);

        var captured = await dialect.CaptureSessionStateAsync(session, CancellationToken.None);
        await session.ExecuteReaderAsync(new SqlExecutionCommand(
            "SET NOCOUNT OFF; SET XACT_ABORT OFF; SET ARITHABORT OFF; SET ANSI_NULLS OFF; SET ANSI_WARNINGS OFF; SET ANSI_PADDING OFF; SET QUOTED_IDENTIFIER OFF; SET CONCAT_NULL_YIELDS_NULL OFF; SET NUMERIC_ROUNDABORT OFF; SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET LOCK_TIMEOUT -1; SET DATEFIRST 7; SET DATEFORMAT mdy; SET DEADLOCK_PRIORITY 0",
            [], 30), CancellationToken.None);
        await dialect.RestoreSessionStateAsync(session, captured, CancellationToken.None);

        Assert.Equal(setupState, captured);
        Assert.Equal(setupState, session.CurrentSessionState);
    }

    [Fact]
    public async Task Compare_reports_measured_distributions_tables_equivalence_operators_and_warnings()
    {
        var session = FakeCompareSession.Create();

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 3));

        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.True(report.ResultsEquivalent);
        Assert.Equal(ResultComparisonMode.Ordered, report.Equivalence.Mode);
        Assert.True(report.Equivalence.Equivalent);
        Assert.Equal(0, report.Equivalence.DifferingPositions);
        Assert.Equal(0, report.Equivalence.BaselineOnlyCount);
        Assert.Equal(0, report.Equivalence.CandidateOnlyCount);
        Assert.Equal(new CompareDistribution(10, 20, 30), report.Baseline.CpuTimeMilliseconds);
        Assert.Equal(new CompareDistribution(12, 22, 32), report.Baseline.ElapsedTimeMilliseconds);
        Assert.Equal(new CompareDistribution(5, 10, 15), report.Baseline.LogicalReads);
        Assert.Equal(30, report.Baseline.TotalLogicalReadsByTable["Clients"]);
        Assert.Equal(new CompareDistribution(5, 10, 15), report.Baseline.LogicalReadsByTable["Clients"]);
        Assert.Contains(report.Baseline.Operators, op => op is { NodeId: 1, PhysicalOp: "Index Seek", Object: "Clients" });
        Assert.Empty(report.Baseline.Warnings);
        Assert.Contains(report.Candidate.Operators, op => op is { NodeId: 2, PhysicalOp: "Hash Match", HasSpill: true });
        Assert.Contains("SpillToTempDb", report.Candidate.Warnings);
        var legacyCandidateOperator = Assert.Single(report.Candidate.Operators);
        Assert.Null(legacyCandidateOperator.Statement);
        Assert.Null(legacyCandidateOperator.NodePath);
        Assert.Null(legacyCandidateOperator.Cursor);
    }

    [Fact]
    public async Task Compare_reports_per_table_logical_read_distributions_classifications_and_parameter_metadata()
    {
        var session = FakeCompareSession.Create(
            ioTable: "dbo.Orders",
            tableReadsForMeasured: measured => measured switch
            {
                1 => 1,
                2 => 5,
                3 => 9,
                _ => measured * 5,
            });
        var operation = Compare(repeat: 3) with
        {
            SetupSql = "CREATE TABLE #ids (Id int); INSERT #ids (Id) SELECT Id FROM dbo.Clients WHERE Created <= @AsOfDate",
            BaselineSql = "SELECT Value FROM dbo.Clients WHERE AsOf <= @AsOfDate",
            CandidateSql = "SELECT Value FROM dbo.Clients WHERE AsOf <= @AsOfDate -- candidate",
            Parameters = ["AsOfDate:datetime2=2026-07-29T12:00:00"],
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.Equal(
            new CompareDistribution(1, 5, 9),
            report.Baseline.LogicalReadsByTable["dbo.Orders"]);
        Assert.Equal(
            new CompareDistribution(1, 5, 9),
            report.Candidate.LogicalReadsByTable["dbo.Orders"]);
        Assert.Equal(15, report.Baseline.TotalLogicalReadsByTable["dbo.Orders"]);
        Assert.Equal("session-local", report.Classification.Setup);
        Assert.Equal("read-only", report.Classification.Baseline);
        Assert.Equal("read-only", report.Classification.Candidate);
        var parameter = Assert.Single(report.Parameters);
        Assert.Equal("@AsOfDate", parameter.Name);
        Assert.Equal("datetime2", parameter.Type);
        Assert.Null(parameter.Size);
        Assert.Null(parameter.Precision);
        Assert.Null(parameter.Scale);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("2026-07-29", json, StringComparison.Ordinal);
        Assert.DoesNotContain("T12:00:00", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_treats_missing_table_reads_as_zero_in_distributions()
    {
        var session = FakeCompareSession.Create(
            ioTable: "dbo.Orders",
            tableReadsForMeasured: measured => measured switch
            {
                1 => 1,
                2 => 5,
                3 => 9,
                _ => measured * 5,
            },
            secondaryIoTable: "dbo.Lines",
            secondaryTableReadsForMeasured: measured => measured == 2 ? 4L : 0L);

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 3));

        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.Equal(
            new CompareDistribution(1, 5, 9),
            report.Baseline.LogicalReadsByTable["dbo.Orders"]);
        Assert.Equal(
            new CompareDistribution(0, 0, 4),
            report.Baseline.LogicalReadsByTable["dbo.Lines"]);
    }

    [Fact]
    public async Task Compare_reports_non_equivalent_results_when_candidate_values_differ()
    {
        var session = FakeCompareSession.Create(candidateValue: 43);

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 1));

        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.False(report.ResultsEquivalent);
        Assert.Equal(ResultComparisonMode.Ordered, report.Equivalence.Mode);
        Assert.False(report.Equivalence.Equivalent);
    }

    [Fact]
    public async Task Compare_propagates_the_nullability_policy_into_the_report()
    {
        var lenient = await Module(FakeCompareSession.Create()).ExecuteAsync(Compare(repeat: 2));
        var strict = await Module(FakeCompareSession.Create())
            .ExecuteAsync(Compare(repeat: 2) with { CompareNullability = true });

        var lenientReport = Assert.IsType<SqlHarnessCompareReport>(lenient.Report);
        Assert.False(lenientReport.Equivalence.CompareNullability);
        Assert.True(lenientReport.Equivalence.NullabilityEquivalent);
        var strictReport = Assert.IsType<SqlHarnessCompareReport>(strict.Report);
        Assert.True(strictReport.Equivalence.CompareNullability);
        Assert.True(strictReport.Equivalence.NullabilityEquivalent);
        Assert.True(strictReport.ResultsEquivalent);
        Assert.Null(strictReport.Equivalence.MismatchReason);
    }

    [Fact]
    public async Task Compare_reports_unknown_provider_nullability_as_unavailable()
    {
        var lenientSession = FakeCompareSession.Create();
        lenientSession.UnknownNullability = true;
        var strictSession = FakeCompareSession.Create();
        strictSession.UnknownNullability = true;

        var lenient = await Module(lenientSession).ExecuteAsync(Compare(repeat: 2));
        var strict = await Module(strictSession).ExecuteAsync(Compare(repeat: 2) with { CompareNullability = true });

        var lenientReport = Assert.IsType<SqlHarnessCompareReport>(lenient.Report);
        Assert.True(lenientReport.ResultsEquivalent);
        Assert.Null(lenientReport.Equivalence.NullabilityEquivalent);
        Assert.Null(lenientReport.Equivalence.MismatchReason);
        var strictReport = Assert.IsType<SqlHarnessCompareReport>(strict.Report);
        Assert.False(strictReport.ResultsEquivalent);
        Assert.Null(strictReport.Equivalence.NullabilityEquivalent);
        Assert.Null(strictReport.Equivalence.MismatchReason);
        Assert.Equal(0, strictReport.Equivalence.BaselineOnlyCount);
        Assert.Equal(0, strictReport.Equivalence.CandidateOnlyCount);
    }

    [Fact]
    public async Task Compare_order_only_change_is_false_ordered_true_multiset_and_null_off()
    {
        var ordered = await Module(FakeCompareSession.Create(reorderCandidate: true))
            .ExecuteAsync(Compare(repeat: 1));
        var multiset = await Module(FakeCompareSession.Create(reorderCandidate: true))
            .ExecuteAsync(Compare(repeat: 1) with { CompareResults = ResultComparisonMode.Multiset });
        var off = await Module(FakeCompareSession.Create(reorderCandidate: true))
            .ExecuteAsync(Compare(repeat: 1) with { CompareResults = ResultComparisonMode.Off });

        var orderedReport = Assert.IsType<SqlHarnessCompareReport>(ordered.Report);
        Assert.False(orderedReport.ResultsEquivalent);
        Assert.False(orderedReport.Equivalence.Equivalent);
        Assert.Equal(ResultComparisonMode.Ordered, orderedReport.Equivalence.Mode);
        Assert.True(orderedReport.Equivalence.DifferingPositions > 0);

        var multisetReport = Assert.IsType<SqlHarnessCompareReport>(multiset.Report);
        Assert.True(multisetReport.ResultsEquivalent);
        Assert.True(multisetReport.Equivalence.Equivalent);
        Assert.Equal(ResultComparisonMode.Multiset, multisetReport.Equivalence.Mode);

        var offReport = Assert.IsType<SqlHarnessCompareReport>(off.Report);
        Assert.Null(offReport.ResultsEquivalent);
        Assert.Null(offReport.Equivalence.Equivalent);
        Assert.Equal(ResultComparisonMode.Off, offReport.Equivalence.Mode);
    }

    [Fact]
    public async Task Compare_requires_context_before_opening_a_session()
    {
        var session = FakeCompareSession.Create();

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 1) with { Context = null });

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Contains("Both --subject and --scope are required", outcome.SafeError, StringComparison.Ordinal);
        Assert.Equal(0, session.FactoryOpenCount);
    }

    [Fact]
    public async Task Compare_validates_all_user_SQL_before_authentication_or_session_open()
    {
        var session = FakeCompareSession.Create();
        var azure = new FakeAzureCli();
        var operation = Compare(repeat: 1) with { CandidateSql = "DELETE dbo.Clients" };

        var outcome = await Module(session, azure).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.FactoryOpenCount);
    }

    [Theory]
    [InlineData("INSERT #t(Id) OUTPUT inserted.Id INTO dbo.PersistentAudit VALUES (1)", null, null)]
    [InlineData("UPDATE #t SET Id = 2 FROM dbo.Clients AS #t", null, null)]
    [InlineData(null, "INSERT #t(Id) OUTPUT inserted.Id INTO dbo.PersistentAudit VALUES (1)", null)]
    [InlineData(null, null, "UPDATE #t SET Id = 2 FROM dbo.Clients AS #t")]
    public async Task Compare_persistent_write_targets_are_rejected_before_connect_without_artifacts(
        string? setupSql,
        string? baselineSql,
        string? candidateSql)
    {
        var session = FakeCompareSession.Create();
        var azure = new FakeAzureCli();
        var artifacts = new CapturingArtifactWriter();
        var operation = Compare(repeat: 1);
        if (setupSql is not null)
            operation = operation with { SetupSql = setupSql };
        if (baselineSql is not null)
            operation = operation with { BaselineSql = baselineSql };
        if (candidateSql is not null)
            operation = operation with { CandidateSql = candidateSql };

        var outcome = await Module(session, azure, artifacts: artifacts).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.FactoryOpenCount);
        Assert.Empty(artifacts.Runs);
        Assert.Null(outcome.Report);
    }

    // 011/final (M1): the plan 011 allows through the `compare` entry point.
    [Fact]
    public async Task Compare_runs_table_variable_setup_and_SET_in_both_variants_unchanged()
    {
        var session = FakeCompareSession.Create();
        var operation = Compare(repeat: 1) with
        {
            SetupSql = "DECLARE @n int; SET @n = 1; DECLARE @seed TABLE (Id int); INSERT @seed (Id) VALUES (@n); SELECT Id INTO #ids FROM @seed",
            BaselineSql = "DECLARE @n int; SET @n = 1; SELECT Value FROM dbo.Clients WHERE Id = @n",
            CandidateSql = "DECLARE @n int; SET @n = 1; SELECT Value FROM dbo.Clients WHERE Id = @n -- candidate",
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(1, session.FactoryOpenCount);
        Assert.Equal("setup", session.Labels[0]);
        Assert.Single(session.Commands, command => command.Sql == operation.SetupSql);
        Assert.Contains(session.Commands, command => command.Sql == operation.BaselineSql);
        Assert.Contains(session.Commands, command => command.Sql == operation.CandidateSql);
        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.Equal("session-local", report.Classification.Setup);
        Assert.Equal("read-only", report.Classification.Baseline);
        Assert.Equal("read-only", report.Classification.Candidate);
    }

    [Theory]
    // An unproven SET target remains denied in setup.
    [InlineData("SET @n = 1; SELECT Id INTO #ids FROM dbo.Clients WHERE Id = @n", null, null, "setup")]
    // A table variable declared in setup is not visible to a measured variant.
    [InlineData("DECLARE @seed TABLE (Id int); INSERT @seed (Id) VALUES (1); SELECT Id INTO #ids FROM @seed", "SELECT Id AS Value FROM @seed", null, "baseline")]
    [InlineData("DECLARE @seed TABLE (Id int); INSERT @seed (Id) VALUES (1); SELECT Id INTO #ids FROM @seed", null, "SELECT Id AS Value FROM @seed -- candidate", "candidate")]
    // SET to a local that is not declared in the same batch.
    [InlineData(null, null, "SET @n = 1; SELECT Value FROM dbo.Clients -- candidate", "candidate")]
    public async Task Compare_rejects_unproven_SET_and_table_variable_SQL_before_connect(
        string? setupSql,
        string? baselineSql,
        string? candidateSql,
        string label)
    {
        var session = FakeCompareSession.Create();
        var azure = new FakeAzureCli();
        var artifacts = new CapturingArtifactWriter();
        var operation = Compare(repeat: 1);
        if (setupSql is not null)
            operation = operation with { SetupSql = setupSql };
        if (baselineSql is not null)
            operation = operation with { BaselineSql = baselineSql };
        if (candidateSql is not null)
            operation = operation with { CandidateSql = candidateSql };

        var outcome = await Module(session, azure, artifacts: artifacts).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Contains($"SQL safety rejection for {label}:", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.FactoryOpenCount);
        Assert.Empty(artifacts.Runs);
    }

    [Fact]
    public async Task Compare_invalid_repeat_uses_compare_wording()
    {
        var outcome = await Module(FakeCompareSession.Create()).ExecuteAsync(Compare(repeat: 0));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Contains("Compare repetitions", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("Compare" + "ormance", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_loads_profiles_exactly_once()
    {
        var loads = 0;
        var outcome = await Module(FakeCompareSession.Create(), loadProfiles: () =>
        {
            loads++;
            return Profiles();
        }).ExecuteAsync(Compare(repeat: 1));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task Compare_rejects_a_parameter_referenced_by_none_of_setup_baseline_or_candidate()
    {
        var session = FakeCompareSession.Create();
        var azure = new FakeAzureCli();
        var operation = Compare(repeat: 1) with { Parameters = ["ClinetId:int=42"] };

        var outcome = await Module(session, azure).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.FactoryOpenCount);
    }

    [Fact]
    public async Task Compare_parameter_reference_matching_uses_the_union_of_all_batches_case_insensitively()
    {
        var session = FakeCompareSession.Create();
        var operation = Compare(repeat: 1) with
        {
            SetupSql = "CREATE TABLE #ids (Id int); INSERT #ids (Id) VALUES (@clientid)",
            Parameters = ["CLIENTID:int=42"],
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var setup = Assert.Single(session.Commands, command => command.Sql.Contains("INSERT #ids", StringComparison.Ordinal));
        Assert.Equal("@CLIENTID", Assert.Single(setup.Parameters).Name);
    }

    [Fact]
    public async Task Compare_raw_footprint_counts_only_first_measured_results_and_messages()
    {
        var plan = "<ShowPlanXML>" + new string('x', 100_000) + "</ShowPlanXML>";
        var session = FakeCompareSession.Create(
            includeSetupResult: true,
            includeExtraMessage: true,
            resultRowCount: 2,
            plan: plan);

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 5));

        using var expected = new CanonicalResultAccumulator();
        AddResultSet(expected, "Value", "System.Int32", 42, 43);
        foreach (var message in session.Messages.Skip(6).Take(3))
            expected.AddMessage("sql", message.Text);
        AddResultSet(expected, "Value", "System.Int32", 42, 43);
        foreach (var message in session.Messages.Skip(9).Take(3))
            expected.AddMessage("sql", message.Text);

        Assert.Equal(expected.Complete().Footprint, outcome.EmissionReceipt!.RawFootprint);
        Assert.True(Assert.IsType<SqlHarnessCompareReport>(outcome.Report).ResultsEquivalent);
        Assert.Contains(plan, outcome.BenchmarkRuns!.First().PlanXmls);
    }

    [Fact]
    public async Task Compare_raw_footprint_is_independent_of_repeat_count_and_plan_size()
    {
        var lowRepeat = await Module(FakeCompareSession.Create()).ExecuteAsync(Compare(repeat: 5));
        var highRepeat = await Module(FakeCompareSession.Create(plan: "<ShowPlanXML>" + new string('x', 100_000) + "</ShowPlanXML>"))
            .ExecuteAsync(Compare(repeat: 20));

        Assert.Equal(lowRepeat.EmissionReceipt!.RawFootprint, highRepeat.EmissionReceipt!.RawFootprint);
        Assert.Equal(
            lowRepeat.BenchmarkRuns!.First(run => run.Variant == "baseline").ResultHash,
            highRepeat.BenchmarkRuns!.First(run => run.Variant == "baseline").ResultHash);
        Assert.Equal(
            lowRepeat.BenchmarkRuns!.First(run => run.Variant == "candidate").ResultHash,
            highRepeat.BenchmarkRuns!.First(run => run.Variant == "candidate").ResultHash);
    }

    [Fact]
    public async Task Compare_retains_and_parses_every_showplan_resultset_in_a_run()
    {
        var session = FakeCompareSession.Create(includeSecondPlan: true);

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 1));

        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.Contains(report.Baseline.Operators, op => op.NodeId == 1);
        Assert.Contains(report.Baseline.Operators, op => op.NodeId == 2);
    }

    [Fact]
    public async Task Compare_receipt_completion_returns_the_exit_code()
    {
        var session = FakeCompareSession.Create();
        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 1));

        var completion = await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(80, 4));

        Assert.Equal(SqlHarnessExitCode.Success, completion);
    }

    [Fact]
    public async Task Partial_statistics_enable_failure_still_attempts_OFF_cleanup()
    {
        var session = FakeCompareSession.Create(failStatisticsEnable: true);

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 1));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Single(session.StatisticsCleanupTokens);
    }

    [Fact]
    public async Task Measured_failure_receipt_excludes_setup_and_warmup_output()
    {
        var session = FakeCompareSession.Create(includeSetupResult: true, failOnBenchmarkNumber: 3);

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 1));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        var raw = outcome.EmissionReceipt!.RawFootprint!;
        using var empty = new CanonicalResultAccumulator();
        Assert.Equal(empty.SnapshotFootprint(), raw);
    }

    [Fact]
    public async Task Compare_exposes_measured_runs_of_both_variants()
    {
        var session = FakeCompareSession.Create();

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 2));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var runs = outcome.BenchmarkRuns!;
        Assert.Equal(["baseline", "candidate", "candidate", "baseline"], runs.Select(run => run.Variant));
        Assert.All(runs, run => Assert.NotEmpty(run.TableIo));
    }

    [Fact]
    public async Task Compare_json_summary_marks_unrecognized_statistics_unavailable()
    {
        var baseline = Path.GetTempFileName();
        var candidate = Path.GetTempFileName();
        try
        {
            File.WriteAllText(baseline, "SELECT Value FROM dbo.Clients");
            File.WriteAllText(candidate, "SELECT Value FROM dbo.Clients -- candidate");
            var session = FakeCompareSession.Create();
            // Same localized fixture as the dialect run: no English TIME block.
            session.StatisticsMessages =
            [
                new SessionMessage(3615, "Tabelle 'X'. Scananzahl 1, logische Lesevorgänge 5"),
                new SessionMessage(3612, "SQL Server-Ausführungszeiten:\n   CPU-Zeit = 12 ms, verstrichene Zeit = 20 ms."),
            ];
            var output = new StringWriter();
            var exit = await SqlHarnessCli.Create(Module(session), output).RunAsync([
                "compare", "test", "--var", "env=a",
                "--baseline", baseline,
                "--candidate", candidate,
                "--subject", "test-query",
                "--scope", "whole",
                "--repeat", "1",
                "--json-summary"]);

            Assert.True(exit == 0, output.ToString());
            using var summary = JsonDocument.Parse(output.ToString());
            foreach (var side in new[] { "baseline", "candidate" })
            {
                var metric = summary.RootElement.GetProperty(side).GetProperty("metricReport");
                Assert.Equal(BenchmarkMetricReport.Unavailable, metric.GetProperty("cpuTimeAvailability").GetString());
                Assert.Equal(BenchmarkMetricReport.Unavailable, metric.GetProperty("logicalReadsAvailability").GetString());
                Assert.Contains(
                    "was not recognized",
                    string.Join('\n', metric.GetProperty("warnings").EnumerateArray().Select(item => item.GetString())),
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(candidate);
        }
    }

    private static void AddResult(
        CanonicalResultAccumulator accumulator,
        string name,
        string dataType,
        object value)
    {
        accumulator.BeginResultSet([new CanonicalColumn(0, name, dataType, false)]);
        accumulator.AddRow([value]);
        accumulator.EndResultSet();
    }

    private static void AddResultSet(
        CanonicalResultAccumulator accumulator,
        string name,
        string dataType,
        params object[] values)
    {
        accumulator.BeginResultSet([new CanonicalColumn(0, name, dataType, false)]);
        foreach (var value in values)
            accumulator.AddRow([value]);
        accumulator.EndResultSet();
    }

    private static string StatisticsMessage(long reads, int cpu, int elapsed, string table = "Clients") =>
        $"Table '{table}'. Scan count 1, logical reads {reads}, physical reads 0, lob logical reads 0.\nSQL Server Execution Times: CPU time = {cpu} ms, elapsed time = {elapsed} ms.";

    private static string TableIoLine(string table, long reads) =>
        $"Table '{table}'. Scan count 1, logical reads {reads}, physical reads 0, lob logical reads 0.";

    [Fact]
    public async Task Caller_cancellation_uses_bounded_non_cancelled_OFF_cleanup_token()
    {
        using var callerCancellation = new CancellationTokenSource();
        var session = FakeCompareSession.Create(cancelOnBenchmark: callerCancellation);

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 1), callerCancellation.Token);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        var cleanupToken = Assert.Single(session.StatisticsCleanupTokens);
        Assert.True(cleanupToken.CanBeCanceled);
        Assert.False(cleanupToken.IsCancellationRequested);
        Assert.NotEqual(callerCancellation.Token, cleanupToken);
    }

    [Fact]
    public async Task Compare_ordered_applies_comparison_row_limit()
    {
        var session = FakeCompareSession.Create(resultRowCount: 3);

        var outcome = await Module(session, comparisonMaximumRows: 2)
            .ExecuteAsync(Compare(repeat: 1));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(
            "Result comparison exceeds the 1000000-row limit.",
            outcome.SafeError);
    }

    [Fact]
    public async Task Compare_off_does_not_apply_comparison_row_limit()
    {
        var session = FakeCompareSession.Create(resultRowCount: 3);

        var outcome = await Module(session, comparisonMaximumRows: 2)
            .ExecuteAsync(Compare(repeat: 1) with { CompareResults = ResultComparisonMode.Off });

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.Equal(ResultComparisonMode.Off, report.Equivalence.Mode);
        Assert.Null(report.Equivalence.Equivalent);
        Assert.DoesNotContain(
            "Result comparison exceeds",
            outcome.SafeError ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_invalid_parameter_value_is_reported_before_connect()
    {
        var session = FakeCompareSession.Create();
        var artifacts = new CapturingArtifactWriter();

        var outcome = await Module(session, artifacts: artifacts).ExecuteAsync(Compare(1) with
        {
            SetupSql = null,
            BaselineSql = "SELECT @n AS Value",
            CandidateSql = "SELECT @n AS Value",
            Parameters = ["n:int=private-audit-value"],
        });

        Assert.Equal(0, session.FactoryOpenCount);
        Assert.Empty(artifacts.Runs);
        Assert.Null(artifacts.Report);
        AssertParameterError(
            outcome,
            "Invalid value 'private-audit-value' for SQL parameter 'n' of type 'int'.",
            "n:int=private-audit-value");
    }

    [Fact]
    public async Task Compare_rejects_setup_only_variable_before_connect_with_generic_diagnostic()
    {
        var session = FakeCompareSession.Create();
        const string variable = "privateSetupVariableSentinel";
        const string value = "privateSetupValueSentinel";
        var operation = Compare(1) with
        {
            SetupSql = $"DECLARE @{variable} int = 73195; SELECT '{value}'",
            BaselineSql = $"SELECT @{variable}; DECLARE @{variable} int = 2;",
            CandidateSql = "SELECT 1",
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(0, session.FactoryOpenCount);
        Assert.Contains(
            "A variable declared in setup is referenced by a benchmark batch. Setup variables do not cross into benchmark batches.",
            outcome.SafeError,
            StringComparison.Ordinal);
        Assert.DoesNotContain(variable, outcome.SafeError, StringComparison.Ordinal);
        Assert.DoesNotContain(value, outcome.SafeError, StringComparison.Ordinal);
        Assert.DoesNotContain("73195", outcome.SafeError, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", outcome.SafeError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_matrix_invalid_value_keeps_name_type_and_offending_value()
    {
        var session = FakeCompareSession.Create();
        var artifacts = new CapturingArtifactWriter();

        var outcome = await Module(session, artifacts: artifacts).ExecuteAsync(new SqlHarnessCompareMatrixOperation(
            Target(),
            null,
            "SELECT @amount AS Value",
            "SELECT @amount AS Value",
            [],
            30,
            1,
            "amount:decimal(19,4)=private-audit-value,1.00")
        { Context = new CompareContext("test-query", "whole") });

        Assert.Equal(0, session.FactoryOpenCount);
        Assert.Empty(artifacts.Runs);
        AssertParameterError(
            outcome,
            "The --matrix option for SQL parameter '@amount' of type 'decimal(19,4)' with value 'private-audit-value' is invalid.",
            "amount:decimal(19,4)=private-audit-value,1.00",
            "1.00");
    }

    [Fact]
    public async Task Compare_matrix_overlapping_value_keeps_the_parameter_name()
    {
        var session = FakeCompareSession.Create();
        var artifacts = new CapturingArtifactWriter();

        var outcome = await Module(session, artifacts: artifacts).ExecuteAsync(new SqlHarnessCompareMatrixOperation(
            Target(),
            null,
            "SELECT @privateaudit AS Value",
            "SELECT @privateaudit AS Value",
            [],
            30,
            1,
            "privateaudit:int=1,private")
        { Context = new CompareContext("test-query", "whole") });

        Assert.Equal(0, session.FactoryOpenCount);
        Assert.Empty(artifacts.Runs);
        AssertParameterError(
            outcome,
            "The --matrix option for SQL parameter '@privateaudit' of type 'int' with value 'private' is invalid.",
            "privateaudit:int=1,private");
    }

    private static void AssertParameterError(SqlHarnessOutcome outcome, string expected, params string[] forbidden)
    {
        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(expected, outcome.SafeError);
        Assert.DoesNotContain(" | ", outcome.SafeError, StringComparison.Ordinal);
        foreach (var secret in forbidden)
            Assert.DoesNotContain(secret, outcome.SafeError, StringComparison.Ordinal);

        var stdout = new StringWriter();
        new Renderer().Render(outcome, OutputMode.Text, new OutputCaptureWriter(stdout));
        var rendered = stdout.ToString();
        Assert.Contains(expected, rendered, StringComparison.Ordinal);
        foreach (var secret in forbidden)
            Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), "sqlharness-audit-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var report = Path.Combine(directory, "error.txt");
            File.WriteAllText(report, rendered);
            var saved = File.ReadAllText(report);
            Assert.Contains(expected, saved, StringComparison.Ordinal);
            foreach (var secret in forbidden)
                Assert.DoesNotContain(secret, saved, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Compare_runs_setup_exactly_once_across_repetitions()
    {
        var session = FakeCompareSession.Create();

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 5));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(1, session.Commands.Count(command =>
            command.Sql.Contains("INTO #ids", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Compare_never_reassigns_session_identity_after_connect()
    {
        var factory = new IdentityAssigningFactory(FakeCompareSession.Create());
        var module = new SqlHarnessModule(factory, new NullArtifactWriter(), Profiles);

        var outcome = await module.ExecuteAsync(Compare(repeat: 1));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.Same(factory.AssignedIdentity, report.Target);
        Assert.Equal(1, factory.IdentitySets);
    }

    [Fact]
    public async Task Compare_failure_still_carries_a_completion_receipt()
    {
        var session = FakeCompareSession.Create();
        var operation = Compare(repeat: 1) with
        {
            BaselineSql = "DELETE dbo.Clients",
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.NotNull(outcome.EmissionReceipt);
        Assert.Equal(
            SqlHarnessExitCode.Safety,
            await outcome.EmissionReceipt.CompleteAsync(new OutputFootprint(0, 0)));
    }

    private sealed class IdentityAssigningFactory(FakeCompareSession inner) : ISqlSessionFactory
    {
        public SqlHarnessTargetIdentityReport AssignedIdentity { get; } =
            new("test-server", "testdb-a", "test-server", "testdb-a", "profile");

        public CountingIdentitySession? CreatedSession { get; private set; }

        public int IdentitySets => CreatedSession?.Sets ?? 0;

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            // The factory assigns identity once before handing the session out,
            // mirroring production session factories; runners must only read it.
            var session = new CountingIdentitySession(inner) { Identity = AssignedIdentity };
            CreatedSession = session;
            return Task.FromResult<ISqlSession>(session);
        }

        public sealed class CountingIdentitySession(FakeCompareSession inner) : ISqlSession
        {
            public int Sets { get; private set; }
            public IReadOnlyList<SessionMessage> Messages => inner.Messages;
            public SqlHarnessTargetIdentityReport Identity
            {
                get => inner.Identity;
                set
                {
                    Sets++;
                    inner.Identity = value;
                }
            }

            public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct) =>
                inner.ExecuteReaderAsync(command, ct);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static SqlHarnessModule Module(
        FakeCompareSession session,
        FakeAzureCli? azure = null,
        Func<IReadOnlyDictionary<string, TargetProfile>>? loadProfiles = null,
        int? comparisonMaximumRows = null,
        ICompareArtifactWriter? artifacts = null) =>
        new(session, artifacts ?? new NullArtifactWriter(), loadProfiles ?? Profiles)
        {
            ComparisonMaximumRows = comparisonMaximumRows ?? CanonicalComparisonAccumulator.MaximumComparedRows,
        };

    private static SqlHarnessCompareOperation Compare(int repeat) =>
        new(Target(), "SELECT Id INTO #ids FROM dbo.Clients", "SELECT Value FROM dbo.Clients", "SELECT Value FROM dbo.Clients -- candidate", [], 30, repeat)
        { Context = new CompareContext("test-query", "whole") };

    private static SqlTargetRequest Target() =>
        new("test", new Dictionary<string, string> { ["env"] = "a" });

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new("test-server", "testdb-{env}", new Dictionary<string, string> { ["env"] = "^(a|b)$" }, "integrated"),
        };

    private sealed class FakeAzureCli : IAzureCli
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<bool> IsLoggedInAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<JsonElement> RunJsonAsync(IReadOnlyList<string> args, CancellationToken ct = default)
        {
            Calls.Add(args.ToArray());
            using var document = JsonDocument.Parse($"{{\"accessToken\":\"{Token}\"}}");
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class NullArtifactWriter : ICompareArtifactWriter
    {
        public void CheckStorage() { /* This test writer stores artifacts in memory. */ }

        public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target) => "ignored";
    }

    private sealed class CapturingArtifactWriter : ICompareArtifactWriter
    {
        public void CheckStorage() { /* This test writer stores artifacts in memory. */ }

        public object? Report { get; private set; }
        public IReadOnlyList<CompareRunArtifact> Runs { get; private set; } = [];
        public string? Target { get; private set; }

        public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target)
        {
            Report = report;
            Runs = runs.ToArray();
            Target = target;
            return "cell-artifacts";
        }
    }

    internal sealed class FakeCompareSession : ISqlSessionFactory, ISqlSession
    {
        private readonly int _candidateValue;
        private readonly bool _failStatisticsEnable;
        private readonly CancellationTokenSource? _cancelOnBenchmark;
        private readonly bool _includeSetupResult;
        private readonly bool _includeSecondPlan;
        private readonly bool _includeExtraMessage;
        private readonly int? _failOnBenchmarkNumber;
        private readonly bool _reorderCandidate;
        private readonly string _ioTable;
        private readonly Func<int, long>? _tableReadsForMeasured;
        private readonly string? _secondaryIoTable;
        private readonly Func<int, long>? _secondaryTableReadsForMeasured;
        private readonly int _resultRowCount;
        private readonly string? _plan;
        private int _baseline;
        private int _candidate;
        private SessionState _sessionState = new(32, 2, -1, 7, "mdy", 0);
        private readonly List<SessionMessage> _messages = [];

        public List<string> Labels { get; } = [];
        public List<SqlExecutionCommand> Commands { get; } = [];
        public int FactoryOpenCount { get; private set; }
        public List<CancellationToken> StatisticsCleanupTokens { get; } = [];
        public List<bool> CandidateAnsiNullsAtStart { get; } = [];
        public SessionState CurrentSessionState => _sessionState;
        public IReadOnlyList<SessionMessage> Messages => _messages;

        // When set, benchmark commands emit these messages instead of English STATISTICS text.
        public IReadOnlyList<SessionMessage>? StatisticsMessages { get; set; }

        // When set, candidate benchmark commands emit these messages instead of StatisticsMessages.
        public IReadOnlyList<SessionMessage>? CandidateStatisticsMessages { get; set; }

        public Exception? BenchmarkFailure { get; set; }

        // Simulates SqlClient reporting no AllowDBNull for result columns.
        public bool UnknownNullability { get; set; }

        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("test-server", "testdb-a", "test-server", "testdb-a", "profile");

        private FakeCompareSession(
            int candidateValue,
            bool failStatisticsEnable,
            CancellationTokenSource? cancelOnBenchmark,
            bool includeSetupResult,
            bool includeSecondPlan,
            bool includeExtraMessage,
            int? failOnBenchmarkNumber,
            bool reorderCandidate,
            string ioTable,
            Func<int, long>? tableReadsForMeasured,
            string? secondaryIoTable,
            Func<int, long>? secondaryTableReadsForMeasured,
            int resultRowCount,
            string? plan)
        {
            _plan = plan;
            _candidateValue = candidateValue;
            _failStatisticsEnable = failStatisticsEnable;
            _cancelOnBenchmark = cancelOnBenchmark;
            _includeSetupResult = includeSetupResult;
            _includeSecondPlan = includeSecondPlan;
            _includeExtraMessage = includeExtraMessage;
            _failOnBenchmarkNumber = failOnBenchmarkNumber;
            _reorderCandidate = reorderCandidate;
            _ioTable = ioTable;
            _tableReadsForMeasured = tableReadsForMeasured;
            _secondaryIoTable = secondaryIoTable;
            _secondaryTableReadsForMeasured = secondaryTableReadsForMeasured;
            _resultRowCount = resultRowCount;
        }

        public static FakeCompareSession Create(
            int candidateValue = 42,
            bool failStatisticsEnable = false,
            CancellationTokenSource? cancelOnBenchmark = null,
            bool includeSetupResult = false,
            bool includeSecondPlan = false,
            bool includeExtraMessage = false,
            int? failOnBenchmarkNumber = null,
            bool reorderCandidate = false,
            string ioTable = "Clients",
            Func<int, long>? tableReadsForMeasured = null,
            string? secondaryIoTable = null,
            Func<int, long>? secondaryTableReadsForMeasured = null,
            int resultRowCount = 1,
            string? plan = null) =>
            new(candidateValue, failStatisticsEnable, cancelOnBenchmark, includeSetupResult, includeSecondPlan, includeExtraMessage, failOnBenchmarkNumber, reorderCandidate, ioTable, tableReadsForMeasured, secondaryIoTable, secondaryTableReadsForMeasured, resultRowCount, plan);

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            FactoryOpenCount++;
            return Task.FromResult<ISqlSession>(this);
        }

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (command.Sql.StartsWith("SELECT @@OPTIONS", StringComparison.Ordinal))
                return Task.FromResult<ISqlReader>(FakeReader.Single(
                    ["Options", "Isolation", "LockTimeout", "DateFirst", "DateFormat", "DeadlockPriority"],
                    [_sessionState.Options, _sessionState.IsolationLevel, _sessionState.LockTimeout,
                        _sessionState.DateFirst, _sessionState.DateFormat, _sessionState.DeadlockPriority]));
            if (!command.Sql.Contains("STATISTICS", StringComparison.OrdinalIgnoreCase) && TryExecuteSetBatch(command.Sql))
                return Task.FromResult<ISqlReader>(FakeReader.Empty());
            if (command.Sql.Contains("STATISTICS IO ON", StringComparison.Ordinal))
            {
                if (_failStatisticsEnable)
                    return Task.FromException<ISqlReader>(new InvalidOperationException("statistics partially enabled"));
                return Task.FromResult<ISqlReader>(FakeReader.Empty());
            }
            if (command.Sql.Contains("STATISTICS IO OFF", StringComparison.Ordinal))
            {
                StatisticsCleanupTokens.Add(ct);
                return Task.FromResult<ISqlReader>(FakeReader.Empty());
            }
            if (command.Sql.Contains("CREATE TABLE #ids", StringComparison.Ordinal) ||
                command.Sql.Contains("INSERT #ids", StringComparison.Ordinal) ||
                command.Sql.Contains("INTO #ids", StringComparison.Ordinal))
            {
                if (!command.Sql.Contains("INSERT #ids", StringComparison.Ordinal))
                    Labels.Add("setup");
                return Task.FromResult<ISqlReader>(_includeSetupResult
                    ? FakeReader.Single(["SetupValue"], ["setup-result-value"])
                    : FakeReader.Empty());
            }

            if (_cancelOnBenchmark is not null)
            {
                _cancelOnBenchmark.Cancel();
                return Task.FromException<ISqlReader>(new OperationCanceledException(ct));
            }

            var baseline = !command.Sql.Contains("candidate", StringComparison.Ordinal);
            if (!baseline)
                CandidateAnsiNullsAtStart.Add((_sessionState.Options & 32) != 0);
            var count = baseline ? ++_baseline : ++_candidate;
            var benchmarkNumber = _baseline + _candidate;
            if (_failOnBenchmarkNumber == benchmarkNumber)
                return Task.FromException<ISqlReader>(BenchmarkFailure ?? new TimeoutException("measured run failed"));
            var measured = Math.Max(count - 1, 1);
            Labels.Add(count == 1 ? $"warmup-{(baseline ? "A" : "B")}" : baseline ? "A" : "B");
            var overrideMessages = baseline ? StatisticsMessages : CandidateStatisticsMessages ?? StatisticsMessages;
            if (overrideMessages is not null)
            {
                _messages.AddRange(overrideMessages);
            }
            else
            {
                var cpu = measured * 10;
                var elapsed = cpu + 2;
                var reads = _tableReadsForMeasured?.Invoke(measured) ?? measured * 5L;
                var message = StatisticsMessage(reads, cpu, elapsed, _ioTable);
                if (_secondaryIoTable is not null)
                {
                    var secondaryReads = _secondaryTableReadsForMeasured?.Invoke(measured) ?? 0L;
                    if (secondaryReads > 0)
                        message = TableIoLine(_secondaryIoTable, secondaryReads) + "\n" + message;
                }
                foreach (var line in message.Split('\n'))
                {
                    var number = line.StartsWith("Table '", StringComparison.Ordinal) ? 3615 : 3612;
                    _messages.Add(new SessionMessage(number, line));
                }
            }
            if (_includeExtraMessage)
                _messages.Add(new SessionMessage(0, $"ordinary diagnostic message {count}"));
            var plans = _plan is not null ? new[] { _plan } : _includeSecondPlan ? new[] { PlanA, PlanB } : new[] { baseline ? PlanA : PlanB };
            if (_reorderCandidate)
            {
                object?[][] rows = baseline
                    ? [[1], [2]]
                    : [[2], [1]];
                return Task.FromResult<ISqlReader>(FakeReader.WithPlans(["Value"], rows, plans, UnknownNullability));
            }

            var baseValue = baseline ? 42 : _candidateValue;
            object?[][] valueRows = Enumerable.Range(0, _resultRowCount)
                .Select(index => new object?[] { baseValue + index })
                .ToArray();
            return Task.FromResult<ISqlReader>(FakeReader.WithPlans(
                ["Value"],
                valueRows,
                plans,
                UnknownNullability));
        }

        public void SetSessionState(SessionState state) => _sessionState = state;

        private bool TryExecuteSetBatch(string sql)
        {
            var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var hasSet = false;
            foreach (var statement in statements)
            {
                if (!statement.StartsWith("SET ", StringComparison.OrdinalIgnoreCase))
                    break;
                hasSet = true;
                var tokens = statement.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 3 && tokens[1].Equals("TRANSACTION", StringComparison.OrdinalIgnoreCase))
                {
                    var level = string.Join(' ', tokens.Skip(4)).ToUpperInvariant() switch
                    {
                        "READ UNCOMMITTED" => 1,
                        "READ COMMITTED" => 2,
                        "REPEATABLE READ" => 3,
                        "SERIALIZABLE" => 4,
                        "SNAPSHOT" => 5,
                        _ => throw new InvalidOperationException($"Unexpected isolation command: {statement}"),
                    };
                    _sessionState = _sessionState with { IsolationLevel = level };
                    continue;
                }

                if (tokens.Length < 3)
                    throw new InvalidOperationException($"Unexpected SET command: {statement}");
                var name = tokens[1].ToUpperInvariant();
                var value = tokens[2];
                var bit = name switch
                {
                    "NOCOUNT" => 512,
                    "XACT_ABORT" => 16384,
                    "ARITHABORT" => 64,
                    "ANSI_NULLS" => 32,
                    "ANSI_WARNINGS" => 8,
                    "ANSI_PADDING" => 16,
                    "QUOTED_IDENTIFIER" => 256,
                    "CONCAT_NULL_YIELDS_NULL" => 4096,
                    "NUMERIC_ROUNDABORT" => 8192,
                    _ => 0,
                };
                if (bit != 0)
                {
                    var options = value.Equals("ON", StringComparison.OrdinalIgnoreCase)
                        ? _sessionState.Options | bit
                        : _sessionState.Options & ~bit;
                    _sessionState = _sessionState with { Options = options };
                }
                else if (name == "LOCK_TIMEOUT")
                    _sessionState = _sessionState with { LockTimeout = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) };
                else if (name == "DATEFIRST")
                    _sessionState = _sessionState with { DateFirst = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) };
                else if (name == "DATEFORMAT")
                    _sessionState = _sessionState with { DateFormat = value };
                else if (name == "DEADLOCK_PRIORITY")
                    _sessionState = _sessionState with { DeadlockPriority = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) };
                else
                    throw new InvalidOperationException($"Unexpected SET command: {statement}");
            }

            return hasSet && statements.All(statement => statement.StartsWith("SET ", StringComparison.OrdinalIgnoreCase));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeReader : ISqlReader
    {
        private readonly IReadOnlyList<Result> _results;
        private int _result;
        private int _row = -1;
        private Result Current => _results[_result];
        private FakeReader(IReadOnlyList<Result> results) => _results = results;
        public int FieldCount => _results.Count == 0 ? 0 : Current.Names.Length;
        public int RecordsAffected => -1;
        public static FakeReader Empty() => new([]);
        public static FakeReader Single(string[] names, object?[] row) => new([new(names, [row])]);
        public static FakeReader WithPlans(string[] names, object?[][] rows, IReadOnlyList<string> plans, bool unknownNullability = false) => new(
            [new(names, rows), .. plans.Select(plan => new Result(["Microsoft SQL Server 2005 XML Showplan"], [[plan]]))])
        {
            UnknownNullability = unknownNullability,
        };
        private bool UnknownNullability { get; init; }
        public string GetName(int ordinal) => Current.Names[ordinal];
        public Type GetFieldType(int ordinal) => Current.Rows[0][ordinal]?.GetType() ?? typeof(object);
        public bool GetAllowNull(int ordinal) => UnknownNullability;
        public bool? GetReportedAllowNull(int ordinal) => UnknownNullability ? null : false;
        public object GetValue(int ordinal) => Current.Rows[_row][ordinal] ?? DBNull.Value;
        public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(_results.Count > 0 && ++_row < Current.Rows.Length);
        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            if (_results.Count == 0 || ++_result >= _results.Count)
                return Task.FromResult(false);
            _row = -1;
            return Task.FromResult(true);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed record Result(string[] Names, object?[][] Rows);
    }
}
