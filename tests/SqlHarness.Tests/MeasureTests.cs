using System.Text.Json;

using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;
using SqlHarness.Tests.Journal;

namespace SqlHarness.Tests;

public class SqlHarnessMeasureTests
{
    private const string Token = "eyJsynthetic.access-never-emit.signature";
    private const string Plan = "<ShowPlanXML><BatchSequence><RelOp NodeId=\"1\" PhysicalOp=\"Index Seek\"><IndexScan><Object Table=\"[Clients]\" /></IndexScan></RelOp></BatchSequence></ShowPlanXML>";

    [Fact]
    public async Task Measure_report_includes_setup_and_query_isolation_notices()
    {
        var session = FakeMeasureSession.Create();
        var outcome = await Module(session).ExecuteAsync(Measure(1) with
        {
            SetupSql = "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE",
            QuerySql = "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ",
        });

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessMeasureReport>(outcome.Report);
        Assert.Equal(
            ["isolation level SERIALIZABLE in effect", "isolation level REPEATABLE READ in effect"],
            report.Warnings);
    }

    [Fact]
    public async Task Measure_parameter_set_failure_preserves_typed_session_warnings()
    {
        var session = FakeMeasureSession.Create(failOnQueryNumber: 1);
        session.QueryFailure = new SessionCleanupException(
            "cleanup failed",
            warnings: [SqlClientSession.BatchTimeoutWarning, SqlClientSession.ConnectionDiscardedWarning]);

        var outcome = await Module(session).ExecuteAsync(Measure(1) with
        {
            SetupSql = null,
            QuerySql = "SELECT @Id",
            ParameterSets = [new("one", ["Id:int=1"]), new("two", ["Id:int=2"])],
        });

        Assert.NotEqual(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Contains("cleanup failed", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(
            [SqlClientSession.BatchTimeoutWarning, SqlClientSession.ConnectionDiscardedWarning],
            outcome.SessionWarnings);
    }

    [Fact]
    public async Task Measure_records_conditions_after_binding()
    {
        var journal = new ConditionsCaptureJournal();
        var module = new JournalingModule(Module(FakeMeasureSession.Create()), () => journal,
            () => JournalTestData.Session());
        var operation = Measure(1) with
        {
            SetupSql = null,
            QuerySql = "SELECT @Id",
            TypedParameters = [new SqlHarnessParameterInput("Id", "int", "1")],
            DryRun = true,
        };

        var outcome = await module.ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.NotNull(journal.Model);
        Assert.Equal("measure", journal.Model.Execution.Operation);
        Assert.Equal("sqlserver", journal.Model.Engine);
        Assert.Equal("1", journal.Model.Fixed.Single().Value!.Value.GetRawText());
        Assert.Equal("0", Assert.Single(journal.Model.Cases).Id);
    }

    [Fact]
    public async Task Measure_dry_run_validates_temp_setup_and_parameter_without_connecting_or_preflighting_artifacts()
    {
        var session = FakeMeasureSession.Create();
        var writer = new CapturingArtifactWriter();
        var operation = Measure(1) with
        {
            SetupSql = "CREATE TABLE #ids (Id int); INSERT #ids VALUES (1);",
            QuerySql = "SELECT Id FROM #ids WHERE Id = @Id",
            TypedParameters = [new SqlHarnessParameterInput("Id", "int", "1")],
            DryRun = true,
        };

        var outcome = await Module(session, writer: writer).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessDryRunReport>(outcome.Report);
        Assert.Equal("measure", report.Command);
        Assert.Equal(1, report.ParameterSets);
        Assert.Equal(0, session.FactoryOpenCount);
        Assert.Equal(0, writer.PreflightCount);
        Assert.Empty(writer.Runs);
    }

    [Fact]
    public async Task Measure_parameter_set_dry_run_binds_every_set_without_connecting_or_preflighting_artifacts()
    {
        var session = FakeMeasureSession.Create();
        var writer = new CapturingArtifactWriter();
        var operation = Measure(1) with
        {
            SetupSql = null,
            QuerySql = "SELECT @Id",
            ParameterSets = [new("small", ["Id:int=1"]), new("large", ["Id:int=2"])],
            DryRun = true,
        };

        var outcome = await Module(session, writer: writer).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessDryRunReport>(outcome.Report);
        Assert.Equal("measure", report.Command);
        Assert.Equal(2, report.ParameterSets);
        Assert.Equal(0, session.FactoryOpenCount);
        Assert.Equal(0, writer.PreflightCount);
        Assert.Empty(writer.Runs);
    }

    [Fact]
    public async Task Measure_inline_parameter_sets_run_on_separate_sessions_and_report_set_names()
    {
        var session = FakeMeasureSession.Create();
        var operation = Measure(1) with
        {
            SetupSql = null,
            QuerySql = "SELECT @Id AS Value",
            ParameterSets =
            [
                new("small", []) { TypedParameters = [new("Id", "int", "7")] },
                new("large", []) { TypedParameters = [new("Id", "int", "8")] },
            ],
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(2, session.FactoryOpenCount);
        var report = Assert.IsType<SqlHarnessMeasureSetReport>(outcome.Report);
        Assert.Equal(["small", "large"], report.Sets.Select(set => set.Name));
        Assert.All(report.Sets, set => Assert.Equal([new MeasureParameterMetadata("@Id", "int")], set.Parameters));
        Assert.All(report.Sets, set => Assert.False(string.IsNullOrEmpty(set.ValueHash)));
    }

    [Theory]
    [InlineData("varchar(12)", "varchar(40)", "short", "a longer value")]
    [InlineData("decimal(10,2)", "decimal(12,2)", "1.25", "123.45")]
    [InlineData("decimal(10,2)", "decimal(10,4)", "1.25", "1.2345")]
    public async Task Measure_rejects_parameter_sets_with_different_declared_shapes(
        string firstType,
        string secondType,
        string firstValue,
        string secondValue)
    {
        var session = FakeMeasureSession.Create();
        var operation = Measure(1) with
        {
            SetupSql = null,
            QuerySql = "SELECT @Value",
            ParameterSets =
            [
                new("first", []) { TypedParameters = [new("Value", firstType, firstValue)] },
                new("second", []) { TypedParameters = [new("Value", secondType, secondValue)] },
            ],
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(0, session.FactoryOpenCount);
        Assert.Contains("different", outcome.SafeError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Measure_accepts_unsized_same_type_with_different_value_inferred_sizes()
    {
        var session = FakeMeasureSession.Create();
        var operation = Measure(1) with
        {
            SetupSql = null,
            QuerySql = "SELECT @Value",
            ParameterSets =
            [
                new("short", []) { TypedParameters = [new("Value", "varchar", "x")] },
                new("long", []) { TypedParameters = [new("Value", "varchar", new string('x', 4001))] },
            ],
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(2, session.FactoryOpenCount);
    }

    [Fact]
    public async Task Measure_runs_setup_warmup_and_measured_queries_in_order_on_one_session()
    {
        var session = FakeMeasureSession.Create();

        var outcome = await Module(session).ExecuteAsync(Measure(repeat: 3));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(["setup", "warmup", "query-1", "query-2", "query-3"], session.Labels);
        Assert.Equal(1, session.FactoryOpenCount);
        Assert.All(session.Commands, command => Assert.DoesNotContain("DBCC", command.Sql, StringComparison.OrdinalIgnoreCase));
        var report = Assert.IsType<SqlHarnessMeasureReport>(outcome.Report);
        Assert.Equal(3, report.Repetitions);
        Assert.Equal(3, report.MeasuredRunCount);
    }

    [Fact]
    public async Task Setup_options_persist_for_the_case_after_each_run()
    {
        var session = FakeMeasureSession.Create();
        var operation = Measure(1) with
        {
            SetupSql = "SET NOCOUNT ON",
            QuerySql = "SET NOCOUNT OFF; SELECT Value FROM dbo.Clients",
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal([true, true], session.NoCountAtQueryStart);
        Assert.Contains(session.Commands, command => command.Sql.StartsWith("SELECT @@OPTIONS", StringComparison.Ordinal));
        Assert.Contains(session.Commands, command => command.Sql.StartsWith("SET NOCOUNT ON; SET XACT_ABORT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Measure_without_setup_starts_with_warmup()
    {
        var session = FakeMeasureSession.Create();

        var outcome = await Module(session).ExecuteAsync(Measure(1) with { SetupSql = null });

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(["warmup", "query-1"], session.Labels);
    }

    [Fact]
    public async Task Measure_reports_measured_only_distributions_and_result_stability()
    {
        var session = FakeMeasureSession.Create(changeLastResult: true);

        var outcome = await Module(session).ExecuteAsync(Measure(3));

        var report = Assert.IsType<SqlHarnessMeasureReport>(outcome.Report);
        Assert.False(report.ResultsStable);
        Assert.Equal(new CompareDistribution(10, 20, 30), report.Query.CpuTimeMilliseconds);
        Assert.Equal(new CompareDistribution(12, 22, 32), report.Query.ElapsedTimeMilliseconds);
        Assert.Equal(new CompareDistribution(5, 10, 15), report.Query.LogicalReads);
        Assert.Equal(30, report.Query.TotalLogicalReadsByTable["Clients"]);
        Assert.Equal(new CompareDistribution(5, 10, 15), report.Query.LogicalReadsByTable["Clients"]);
        Assert.Contains(report.Query.Operators, op => op is { NodeId: 1, PhysicalOp: "Index Seek" });
    }

    [Fact]
    public async Task Measure_reports_per_table_distributions_classifications_and_parameter_metadata()
    {
        var session = FakeMeasureSession.Create(
            ioTable: "dbo.Orders",
            tableReadsForMeasured: measured => measured switch
            {
                1 => 1,
                2 => 5,
                3 => 9,
                _ => measured * 5,
            });
        var operation = Measure(3) with
        {
            SetupSql = "CREATE TABLE #ids (Id int); INSERT #ids (Id) SELECT Id FROM dbo.Clients WHERE Created <= @AsOfDate",
            QuerySql = "SELECT Value FROM dbo.Clients WHERE AsOf <= @AsOfDate",
            Parameters = ["AsOfDate:datetime2=2026-07-29T12:00:00"],
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        var report = Assert.IsType<SqlHarnessMeasureReport>(outcome.Report);
        Assert.Equal(
            new CompareDistribution(1, 5, 9),
            report.Query.LogicalReadsByTable["dbo.Orders"]);
        Assert.Equal(15, report.Query.TotalLogicalReadsByTable["dbo.Orders"]);
        Assert.Equal("session-local", report.Classification.Setup);
        Assert.Equal("read-only", report.Classification.Query);
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
    public async Task Measure_without_setup_classifies_setup_as_none()
    {
        var session = FakeMeasureSession.Create();

        var outcome = await Module(session).ExecuteAsync(Measure(1) with { SetupSql = null });

        var report = Assert.IsType<SqlHarnessMeasureReport>(outcome.Report);
        Assert.Equal("none", report.Classification.Setup);
        Assert.Equal("read-only", report.Classification.Query);
        Assert.Empty(report.Parameters);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(301, 1)]
    [InlineData(30, 0)]
    [InlineData(30, 101)]
    public async Task Measure_validates_bounds_before_authentication(int timeout, int repeat)
    {
        var session = FakeMeasureSession.Create();
        var azure = new FakeAzureCli();

        var outcome = await Module(session, azure).ExecuteAsync(Measure(repeat) with { TimeoutSeconds = timeout });

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.FactoryOpenCount);
    }

    [Fact]
    public async Task Measure_loads_profiles_exactly_once()
    {
        var loads = 0;
        var outcome = await Module(FakeMeasureSession.Create(), loadProfiles: () =>
        {
            loads++;
            return Profiles();
        }).ExecuteAsync(Measure(repeat: 1));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(1, loads);
    }

    [Theory]
    [InlineData("DELETE dbo.Clients", "SELECT Value FROM dbo.Clients")]
    [InlineData("SELECT Id INTO #ids FROM dbo.Clients", "UPDATE dbo.Clients SET Value = 1")]
    [InlineData("INSERT #t(Id) OUTPUT inserted.Id INTO dbo.PersistentAudit VALUES (1)", "SELECT Value FROM dbo.Clients")]
    [InlineData("UPDATE #t SET Id = 2 FROM dbo.Clients AS #t", "SELECT Value FROM dbo.Clients")]
    [InlineData("SELECT Id INTO #ids FROM dbo.Clients", "INSERT #t(Id) OUTPUT inserted.Id INTO dbo.PersistentAudit VALUES (1)")]
    public async Task Measure_classifies_all_SQL_before_authentication(string? setup, string query)
    {
        var session = FakeMeasureSession.Create();
        var azure = new FakeAzureCli();
        var writer = new CapturingArtifactWriter();

        var outcome = await Module(session, azure, writer: writer).ExecuteAsync(Measure(1) with { SetupSql = setup, QuerySql = query });

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.FactoryOpenCount);
        Assert.Empty(writer.Runs);
        Assert.Null(outcome.Report);
    }

    // 011/final (M1): the plan 011 allows through the `measure` entry point.
    // Setup may use a table variable; the measured SQL may use SET.
    [Fact]
    public async Task Measure_runs_table_variable_setup_and_SET_in_measured_SQL_unchanged()
    {
        var session = FakeMeasureSession.Create();
        var operation = Measure(repeat: 2) with
        {
            SetupSql = "DECLARE @n int; SET @n = 1; DECLARE @seed TABLE (Id int); INSERT @seed (Id) VALUES (@n); SELECT Id INTO #ids FROM @seed",
            QuerySql = "DECLARE @n int; SET @n = 1; SELECT Value FROM dbo.Clients WHERE Id = @n",
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(["setup", "warmup", "query-1", "query-2"], session.Labels);
        Assert.Equal(1, session.FactoryOpenCount);
        Assert.Single(session.Commands, command => command.Sql == operation.SetupSql);
        Assert.Equal(3, session.Commands.Count(command => command.Sql == operation.QuerySql));
        var report = Assert.IsType<SqlHarnessMeasureReport>(outcome.Report);
        Assert.Equal("session-local", report.Classification.Setup);
        Assert.Equal("read-only", report.Classification.Query);
    }

    [Theory]
    // An unproven SET target remains denied in setup.
    [InlineData("SET @n = 1; SELECT Id INTO #ids FROM dbo.Clients WHERE Id = @n", "SELECT Value FROM dbo.Clients", "setup")]
    // A table variable declared in setup is another batch: the measured SQL cannot use it.
    [InlineData("DECLARE @seed TABLE (Id int); INSERT @seed (Id) VALUES (1); SELECT Id INTO #ids FROM @seed", "SELECT Id AS Value FROM @seed", "query")]
    // Undeclared table variable in setup; SET to an undeclared local in the measured SQL.
    [InlineData("INSERT @seed (Id) VALUES (1); SELECT Id INTO #ids FROM dbo.Clients", "SELECT Value FROM dbo.Clients", "setup")]
    [InlineData("SELECT Id INTO #ids FROM dbo.Clients", "SET @n = 1; SELECT Value FROM dbo.Clients", "query")]
    public async Task Measure_rejects_unproven_SET_and_table_variable_SQL_before_authentication(
        string setup, string query, string label)
    {
        var session = FakeMeasureSession.Create();
        var azure = new FakeAzureCli();
        var writer = new CapturingArtifactWriter();

        var outcome = await Module(session, azure, writer: writer)
            .ExecuteAsync(Measure(1) with { SetupSql = setup, QuerySql = query });

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Contains($"SQL safety rejection for {label}:", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.FactoryOpenCount);
        Assert.Empty(writer.Runs);
        Assert.Null(outcome.Report);
    }

    [Fact]
    public async Task Measure_parameter_matching_uses_case_insensitive_union_of_setup_and_query()
    {
        var session = FakeMeasureSession.Create();
        var operation = Measure(1) with
        {
            SetupSql = "CREATE TABLE #ids (Id int); INSERT #ids (Id) VALUES (@clientid)",
            Parameters = ["CLIENTID:int=42"],
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.All(session.Commands.Where(command =>
                command.Sql.Contains("INSERT #ids", StringComparison.Ordinal) ||
                command.Sql == operation.QuerySql),
            command => Assert.Equal("@CLIENTID", Assert.Single(command.Parameters).Name));
    }

    [Fact]
    public async Task Measure_rejects_unused_parameter_before_authentication()
    {
        var azure = new FakeAzureCli();
        var session = FakeMeasureSession.Create();

        var outcome = await Module(session, azure).ExecuteAsync(Measure(1) with { Parameters = ["ClinetId:int=42"] });

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
    }

    [Fact]
    public async Task Measure_writes_only_measured_runs_to_artifacts()
    {
        var writer = new CapturingArtifactWriter();

        var outcome = await Module(FakeMeasureSession.Create(), writer: writer).ExecuteAsync(Measure(2));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal("measure-artifacts", Assert.IsType<SqlHarnessMeasureReport>(outcome.Report).ArtifactDirectory);
        Assert.Equal([1, 2], writer.Runs.Select(run => run.Repetition));
        Assert.All(writer.Runs, run => Assert.Equal("measure", run.Variant));
    }

    [Fact]
    public async Task Measure_maps_artifact_failure_to_local_storage()
    {
        var outcome = await Module(FakeMeasureSession.Create(), writer: new ThrowingArtifactWriter())
            .ExecuteAsync(Measure(1));

        Assert.Equal(SqlHarnessExitCode.LocalStorage, outcome.ExitCode);
        Assert.Contains("disk unavailable", outcome.SafeError);
    }

    [Fact]
    public async Task Measure_raw_footprint_counts_only_first_measured_result_and_messages()
    {
        var plan = "<ShowPlanXML>" + new string('x', 100_000) + "</ShowPlanXML>";
        var session = FakeMeasureSession.Create(
                includeSetupResult: true,
                includeExtraMessage: true,
                resultRowCount: 2,
                plan: plan);
        var outcome = await Module(session).ExecuteAsync(Measure(1));

        using var expected = new CanonicalResultAccumulator();
        AddResultSet(expected, "Value", "System.Int32", 42, 43);
        foreach (var message in session.Messages.Skip(3).Take(3))
            expected.AddMessage("sql", message.Text);

        Assert.Equal(expected.Complete().Footprint, outcome.EmissionReceipt!.RawFootprint);
        Assert.Contains(plan, outcome.BenchmarkRuns!.First().PlanXmls);
    }

    [Fact]
    public async Task Measure_raw_footprint_is_independent_of_repeat_count_and_plan_size()
    {
        var lowRepeat = await Module(FakeMeasureSession.Create()).ExecuteAsync(Measure(5));
        var highRepeat = await Module(FakeMeasureSession.Create(plan: "<ShowPlanXML>" + new string('x', 100_000) + "</ShowPlanXML>"))
            .ExecuteAsync(Measure(20));

        Assert.Equal(lowRepeat.EmissionReceipt!.RawFootprint, highRepeat.EmissionReceipt!.RawFootprint);
        Assert.Equal(lowRepeat.BenchmarkRuns!.First().ResultHash, highRepeat.BenchmarkRuns!.First().ResultHash);
    }

    [Fact]
    public async Task Measure_parameter_set_raw_footprint_remains_bounded_across_repeats()
    {
        var lowSession = FakeMeasureSession.Create();
        var lowRepeat = await Module(lowSession).ExecuteAsync(Measure(5) with
        {
            SetupSql = null,
            QuerySql = "SELECT @n AS Value",
            ParameterSets = [new("small", ["n:int=7"]), new("large", ["n:int=8"])],
        });
        var highSession = FakeMeasureSession.Create(
            plan: "<ShowPlanXML>" + new string('x', 100_000) + "</ShowPlanXML>");
        var highRepeat = await Module(highSession).ExecuteAsync(Measure(20) with
        {
            SetupSql = null,
            QuerySql = "SELECT @n AS Value",
            ParameterSets = [new("small", ["n:int=7"]), new("large", ["n:int=8"])],
        });

        Assert.Equal(1, lowRepeat.EmissionReceipt!.RawFootprint!.Lines);
        Assert.Equal(1, highRepeat.EmissionReceipt!.RawFootprint!.Lines);
        Assert.Equal(
            lowRepeat.BenchmarkRuns!.Take(2).Select(run => run.ResultHash),
            highRepeat.BenchmarkRuns!.Take(2).Select(run => run.ResultHash));
    }

    [Fact]
    public async Task Measure_failure_receipt_excludes_setup_and_warmup_output()
    {
        var outcome = await Module(FakeMeasureSession.Create(includeSetupResult: true, failOnQueryNumber: 2))
            .ExecuteAsync(Measure(2));

        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt).CompleteAsync(new OutputFootprint(1, 1));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        using var empty = new CanonicalResultAccumulator();
        Assert.Equal(empty.SnapshotFootprint(), outcome.EmissionReceipt!.RawFootprint);
    }

    [Fact]
    public async Task Measure_partial_query_failure_retains_message_emitted_before_failure_exactly_once()
    {
        var session = FakeMeasureSession.Create(failOnQueryNumber: 2, emitMessageBeforeQueryFailure: true);
        var outcome = await Module(session).ExecuteAsync(Measure(1) with { SetupSql = null });

        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt).CompleteAsync(new OutputFootprint(1, 1));

        using var expected = new CanonicalResultAccumulator();
        expected.AddMessage("sql", "diagnostic before query failure");
        var footprint = expected.SnapshotFootprint();
        var record = outcome.EmissionReceipt!.RawFootprint!;
        Assert.Equal(footprint.Bytes, record.Bytes);
        Assert.Equal(footprint.Lines, record.Lines);
    }

    [Fact]
    public async Task Measure_setup_failure_retains_message_emitted_before_failure_exactly_once()
    {
        var session = FakeMeasureSession.Create(failSetup: true);
        var outcome = await Module(session).ExecuteAsync(Measure(1));

        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt).CompleteAsync(new OutputFootprint(1, 1));

        var record = outcome.EmissionReceipt!.RawFootprint!;
        using var empty = new CanonicalResultAccumulator();
        Assert.Equal(empty.SnapshotFootprint(), record);
    }

    [Fact]
    public async Task Measure_cancellation_uses_bounded_caller_independent_cleanup()
    {
        using var callerCancellation = new CancellationTokenSource();
        var session = FakeMeasureSession.Create(cancelOnQuery: callerCancellation);

        var outcome = await Module(session).ExecuteAsync(Measure(1), callerCancellation.Token);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        var cleanupToken = Assert.Single(session.StatisticsCleanupTokens);
        Assert.True(cleanupToken.CanBeCanceled);
        Assert.False(cleanupToken.IsCancellationRequested);
        Assert.NotEqual(callerCancellation.Token, cleanupToken);
    }

    [Fact]
    public async Task Measure_does_not_apply_comparison_row_limit()
    {
        // Inject a comparison cap of 2 while returning 3 rows. Measure never captures fingerprints,
        // so the comparison limit must not fire (canonical/raw accumulators have no such cap).
        var session = FakeMeasureSession.Create(resultRowCount: 3);

        var outcome = await Module(session, comparisonMaximumRows: 2).ExecuteAsync(Measure(1));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.IsType<SqlHarnessMeasureReport>(outcome.Report);
        Assert.DoesNotContain(
            "Result comparison exceeds",
            outcome.SafeError ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Measure_invalid_parameter_value_is_reported_before_connect()
    {
        var session = FakeMeasureSession.Create();
        var azure = new FakeAzureCli();
        var writer = new CapturingArtifactWriter();

        var outcome = await Module(session, azure, writer: writer).ExecuteAsync(Measure(1) with
        {
            SetupSql = null,
            QuerySql = "SELECT @n AS Value",
            Parameters = ["n:int=private-audit-value"],
        });

        Assert.Equal(0, session.FactoryOpenCount);
        Assert.Empty(azure.Calls);
        Assert.Empty(writer.Runs);
        AssertParameterError(
            outcome,
            "Invalid value 'private-audit-value' for SQL parameter 'n' of type 'int'.",
            "n:int=private-audit-value");
    }

    [Fact]
    public async Task Measure_parameter_set_invalid_value_keeps_name_and_type()
    {
        var session = FakeMeasureSession.Create();
        var writer = new CapturingArtifactWriter();

        var outcome = await Module(session, writer: writer).ExecuteAsync(Measure(1) with
        {
            SetupSql = null,
            QuerySql = "SELECT @ok AS Ok, @n AS Value",
            Parameters = ["ok:int=1"],
            ParameterSets =
            [
                new SqlHarnessParameterSetInput("small", ["n:int=7"]),
                new SqlHarnessParameterSetInput("large", ["n:int=private-audit-value"]),
            ],
        });

        Assert.Equal(0, session.FactoryOpenCount);
        Assert.Empty(writer.Runs);
        AssertParameterError(
            outcome,
            "Parameter set 'large' is invalid: Invalid value 'private-audit-value' for SQL parameter 'n' of type 'int'.",
            "n:int=private-audit-value",
            "ok:int=1");
    }

    [Fact]
    public async Task Measure_parameter_set_overlapping_value_keeps_the_parameter_name()
    {
        var session = FakeMeasureSession.Create();
        var writer = new CapturingArtifactWriter();

        var outcome = await Module(session, writer: writer).ExecuteAsync(Measure(1) with
        {
            SetupSql = null,
            QuerySql = "SELECT @privateaudit AS Value",
            ParameterSets =
            [
                new SqlHarnessParameterSetInput("small", ["privateaudit:int=1"]),
                new SqlHarnessParameterSetInput("large", ["privateaudit:int=private"]),
            ],
        });

        Assert.Equal(0, session.FactoryOpenCount);
        Assert.Empty(writer.Runs);
        AssertParameterError(
            outcome,
            "Parameter set 'large' is invalid: Invalid value 'private' for SQL parameter 'privateaudit' of type 'int'.",
            "privateaudit:int=private");
    }

    [Fact]
    public async Task Measure_runs_carry_full_table_io_for_the_journal()
    {
        var session = FakeMeasureSession.Create();

        var outcome = await Module(session).ExecuteAsync(Measure(3));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var runs = outcome.BenchmarkRuns!;
        Assert.Equal(3, runs.Count);
        Assert.All(runs, run =>
        {
            var table = Assert.Single(run.TableIo);
            Assert.Equal("Clients", table.Table);
            Assert.Equal(1, table.ScanCount);
            Assert.Equal(run.LogicalReads, table.LogicalReads);
        });
    }

    [Fact]
    public async Task Measure_parameter_sets_expose_runs_with_set_names()
    {
        var session = FakeMeasureSession.Create();

        var outcome = await Module(session).ExecuteAsync(Measure(2) with
        {
            SetupSql = null,
            QuerySql = "SELECT @n AS Value",
            ParameterSets =
            [
                new SqlHarnessParameterSetInput("small", ["n:int=7"]),
                new SqlHarnessParameterSetInput("large", ["n:int=8"]),
            ],
        });

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(4, outcome.BenchmarkRuns!.Count);
        Assert.Equal(["small", "small", "large", "large"], outcome.BenchmarkRuns.Select(run => run.ParameterSet));
        Assert.All(outcome.BenchmarkRuns, run => Assert.Null(run.MatrixCell));
    }

    [Fact]
    public async Task Failed_measure_exposes_no_runs()
    {
        var outcome = await Module(FakeMeasureSession.Create()).ExecuteAsync(Measure(1) with { QuerySql = "DELETE FROM dbo.Clients" });

        Assert.NotEqual(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Null(outcome.BenchmarkRuns);
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

    private static SqlHarnessMeasureOperation Measure(int repeat) =>
        new(Target(), "SELECT Id INTO #ids FROM dbo.Clients", "SELECT Value FROM dbo.Clients", [], 30, repeat);

    private static SqlTargetRequest Target() =>
        new("test", new Dictionary<string, string> { ["env"] = "a" });

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new("test-server", "testdb-{env}", new Dictionary<string, string> { ["env"] = "^(a|b)$" }, "integrated"),
        };

    private static void AddResult(CanonicalResultAccumulator accumulator, string name, string dataType, object value)
    {
        accumulator.BeginResultSet([new CanonicalColumn(0, name, dataType, false)]);
        accumulator.AddRow([value]);
        accumulator.EndResultSet();
    }

    private static void AddResultSet(CanonicalResultAccumulator accumulator, string name, string dataType, params object[] values)
    {
        accumulator.BeginResultSet([new CanonicalColumn(0, name, dataType, false)]);
        foreach (var value in values)
            accumulator.AddRow([value]);
        accumulator.EndResultSet();
    }

    private static string StatisticsMessage(long reads, int cpu, int elapsed, string table = "Clients") =>
        $"Table '{table}'. Scan count 1, logical reads {reads}, physical reads 0, lob logical reads 0.\nSQL Server Execution Times: CPU time = {cpu} ms, elapsed time = {elapsed} ms.";

    private static SqlHarnessModule Module(
        FakeMeasureSession session,
        FakeAzureCli? azure = null,
        ICompareArtifactWriter? writer = null,
        Func<IReadOnlyDictionary<string, TargetProfile>>? loadProfiles = null,
        int? comparisonMaximumRows = null) =>
        new(session, writer ?? new CapturingArtifactWriter(), loadProfiles ?? Profiles)
        {
            ComparisonMaximumRows = comparisonMaximumRows ?? CanonicalComparisonAccumulator.MaximumComparedRows,
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

    private sealed class CapturingArtifactWriter : ICompareArtifactWriter
    {
        public int PreflightCount { get; private set; }
        public void CheckStorage() { PreflightCount++; /* This test writer stores artifacts in memory. */ }

        public IReadOnlyList<CompareRunArtifact> Runs { get; private set; } = [];
        public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target)
        {
            Runs = runs.ToArray();
            return "measure-artifacts";
        }
    }

    private sealed class ConditionsCaptureJournal : IActivityJournal
    {
        public ConditionsModel? Model { get; private set; }
        public JournalHandle? Begin(SessionIdentity session, OperationStart start) => new(1);
        public bool Complete(JournalHandle? handle, OperationEnd end) => true;
        public void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted) { }
        public void RecordConditions(JournalHandle handle, ConditionsModel model) => Model = model;
    }

    private sealed class ThrowingArtifactWriter : ICompareArtifactWriter
    {
        public void CheckStorage() { /* This test writer stores artifacts in memory. */ }

        public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target) =>
            throw new IOException("disk unavailable");
    }

    internal sealed class FakeMeasureSession : ISqlSessionFactory, ISqlSession
    {
        private readonly bool _changeLastResult;
        private readonly bool _includeSetupResult;
        private readonly bool _includeExtraMessage;
        private readonly int? _failOnQueryNumber;
        private readonly CancellationTokenSource? _cancelOnQuery;
        private readonly bool _emitMessageBeforeQueryFailure;
        private readonly bool _failSetup;
        private readonly string _ioTable;
        private readonly Func<int, long>? _tableReadsForMeasured;
        private readonly int _resultRowCount;
        private readonly string _plan;
        private readonly List<SessionMessage> _messages = [];
        private int _queryCount;
        private bool _noCount;

        public List<string> Labels { get; } = [];
        public List<SqlExecutionCommand> Commands { get; } = [];
        public int FactoryOpenCount { get; private set; }
        public List<CancellationToken> StatisticsCleanupTokens { get; } = [];
        public List<bool> NoCountAtQueryStart { get; } = [];
        public IReadOnlyList<SessionMessage> Messages => _messages;
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("test-server", "testdb-a", "test-server", "testdb-a", "profile");
        public Func<string, string>? PlanForSql { get; set; }
        public CancellationTokenSource? CancelInventory { get; set; }
        public Exception? InventoryFailure { get; set; }
        public Exception? QueryFailure { get; set; }
        public IReadOnlyList<(string[] Names, object?[][] Rows)>? InventoryResults { get; set; }

        private FakeMeasureSession(
            bool changeLastResult,
            bool includeSetupResult,
            bool includeExtraMessage,
            int? failOnQueryNumber,
            CancellationTokenSource? cancelOnQuery,
            bool emitMessageBeforeQueryFailure,
            bool failSetup,
            string ioTable,
            Func<int, long>? tableReadsForMeasured,
            int resultRowCount,
            string plan)
        {
            _plan = plan;
            _changeLastResult = changeLastResult;
            _includeSetupResult = includeSetupResult;
            _includeExtraMessage = includeExtraMessage;
            _failOnQueryNumber = failOnQueryNumber;
            _cancelOnQuery = cancelOnQuery;
            _emitMessageBeforeQueryFailure = emitMessageBeforeQueryFailure;
            _failSetup = failSetup;
            _ioTable = ioTable;
            _tableReadsForMeasured = tableReadsForMeasured;
            _resultRowCount = resultRowCount;
        }

        public static FakeMeasureSession Create(
            bool changeLastResult = false,
            bool includeSetupResult = false,
            bool includeExtraMessage = false,
            int? failOnQueryNumber = null,
            CancellationTokenSource? cancelOnQuery = null,
            bool emitMessageBeforeQueryFailure = false,
            bool failSetup = false,
            string ioTable = "Clients",
            Func<int, long>? tableReadsForMeasured = null,
            int resultRowCount = 1,
            string plan = Plan) =>
            new(changeLastResult, includeSetupResult, includeExtraMessage, failOnQueryNumber, cancelOnQuery, emitMessageBeforeQueryFailure, failSetup, ioTable, tableReadsForMeasured, resultRowCount, plan);

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
                    [(_noCount ? 512 : 0), 2, -1, 7, "mdy", 0]));
            if (command.Sql.Contains("STATISTICS IO ON", StringComparison.Ordinal))
                return Task.FromResult<ISqlReader>(FakeReader.Empty());
            if (command.Sql.Contains("STATISTICS IO OFF", StringComparison.Ordinal))
            {
                StatisticsCleanupTokens.Add(ct);
                return Task.FromResult<ISqlReader>(FakeReader.Empty());
            }
            if (command.Sql.StartsWith("SET NOCOUNT ", StringComparison.OrdinalIgnoreCase) &&
                command.Sql.Contains("; SET XACT_ABORT ", StringComparison.OrdinalIgnoreCase))
            {
                _noCount = command.Sql.StartsWith("SET NOCOUNT ON", StringComparison.OrdinalIgnoreCase);
                return Task.FromResult<ISqlReader>(FakeReader.Empty());
            }
            if (command.Sql.StartsWith("SET NOCOUNT ON", StringComparison.OrdinalIgnoreCase))
            {
                _noCount = true;
                return Task.FromResult<ISqlReader>(FakeReader.Empty());
            }
            if (command.Sql.Contains("CREATE TABLE #ids", StringComparison.Ordinal) ||
                command.Sql.Contains("INSERT #ids", StringComparison.Ordinal) ||
                command.Sql.Contains("INTO #ids", StringComparison.Ordinal))
            {
                if (!command.Sql.Contains("INSERT #ids", StringComparison.Ordinal))
                    Labels.Add("setup");
                if (_failSetup)
                {
                    _messages.Add(new SessionMessage(0, "diagnostic before setup failure"));
                    return Task.FromException<ISqlReader>(new TimeoutException("setup failed"));
                }
                return Task.FromResult<ISqlReader>(_includeSetupResult ? FakeReader.Single(["SetupValue"], ["setup-result-value"]) : FakeReader.Empty());
            }

            // Not a measured repetition: no label and no query count.
            if (string.Equals(command.Sql, IndexInventoryQuery.Sql, StringComparison.Ordinal))
            {
                if (CancelInventory is not null)
                {
                    CancelInventory.Cancel();
                    return Task.FromException<ISqlReader>(new OperationCanceledException(ct));
                }

                if (InventoryFailure is not null)
                    return Task.FromException<ISqlReader>(InventoryFailure);

                return Task.FromResult<ISqlReader>(FakeReader.FromGrids(InventoryResults ?? []));
            }

            if (_cancelOnQuery is not null)
            {
                _cancelOnQuery.Cancel();
                return Task.FromException<ISqlReader>(new OperationCanceledException(ct));
            }

            var queryNumber = ++_queryCount;
            if (_failOnQueryNumber == queryNumber)
            {
                if (_emitMessageBeforeQueryFailure)
                    _messages.Add(new SessionMessage(0, "diagnostic before query failure"));
                return Task.FromException<ISqlReader>(QueryFailure ?? new TimeoutException("measured run failed"));
            }
            Labels.Add(queryNumber == 1 ? "warmup" : $"query-{queryNumber - 1}");
            NoCountAtQueryStart.Add(_noCount);
            if (command.Sql.Contains("SET NOCOUNT OFF", StringComparison.OrdinalIgnoreCase))
                _noCount = false;
            var measured = Math.Max(queryNumber - 1, 1);
            var reads = _tableReadsForMeasured?.Invoke(measured) ?? measured * 5L;
            var lines = StatisticsMessage(reads, measured * 10, measured * 10 + 2, _ioTable).Split('\n');
            _messages.Add(new SessionMessage(3615, lines[0]));
            _messages.Add(new SessionMessage(3613, lines[1]));
            if (_includeExtraMessage)
                _messages.Add(new SessionMessage(0, $"ordinary diagnostic message {queryNumber}"));
            var value = _changeLastResult && queryNumber == 4 ? 43 : 42;
            object?[][] rows = Enumerable.Range(0, _resultRowCount)
                .Select(index => new object?[] { value + index })
                .ToArray();
            return Task.FromResult<ISqlReader>(FakeReader.WithPlan(["Value"], rows, PlanForSql?.Invoke(command.Sql) ?? _plan));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeReader : ISqlReader
    {
        private readonly IReadOnlyList<Result> _results;
        private readonly bool _sequential;
        private int _result;
        private int _row = -1;
        private int _highestOrdinal = -1;
        private Result Current => _results[_result];
        private FakeReader(IReadOnlyList<Result> results, bool sequential = false)
        {
            _results = results;
            _sequential = sequential;
        }
        public int FieldCount => _results.Count == 0 ? 0 : Current.Names.Length;
        public int RecordsAffected => -1;
        public static FakeReader Empty() => new([]);
        public static FakeReader Single(string[] names, object?[] row) => new([new(names, [row])]);
        public static FakeReader WithPlan(string[] names, object?[] row, string plan) => WithPlan(names, [row], plan);
        public static FakeReader WithPlan(string[] names, object?[][] rows, string plan) =>
            new([new(names, rows), new(["Microsoft SQL Server 2005 XML Showplan"], [[plan]])]);
        public static FakeReader FromGrids(IReadOnlyList<(string[] Names, object?[][] Rows)> grids) =>
            new(grids.Select(grid => new Result(grid.Names, grid.Rows)).ToArray(), sequential: true);
        public string GetName(int ordinal) => Current.Names[ordinal];
        public Type GetFieldType(int ordinal) => Current.Rows[0][ordinal]?.GetType() ?? typeof(object);
        public bool GetAllowNull(int ordinal) => false;
        public object GetValue(int ordinal)
        {
            // Inventory grids match SqlClient SequentialAccess: a lower ordinal cannot be read again.
            if (_sequential && ordinal <= _highestOrdinal)
                throw new InvalidOperationException($"Invalid attempt to read from column ordinal '{ordinal}'.");

            _highestOrdinal = ordinal;
            return Current.Rows[_row][ordinal] ?? DBNull.Value;
        }
        public Task<bool> ReadAsync(CancellationToken ct)
        {
            _highestOrdinal = -1;
            return Task.FromResult(_results.Count > 0 && ++_row < Current.Rows.Length);
        }
        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            _highestOrdinal = -1;
            if (_results.Count == 0 || ++_result >= _results.Count)
                return Task.FromResult(false);
            _row = -1;
            return Task.FromResult(true);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed record Result(string[] Names, object?[][] Rows);
    }
}
